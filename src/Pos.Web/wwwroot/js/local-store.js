// local-store.js — IndexedDB persistence for the POS.
//
// The terminal is the system of record while it is trading: a store must be able to
// keep selling with no network at all. Everything is therefore written here first and
// synced to the hub afterwards.
//
// The one invariant that matters most lives in commitSale(): the sale, its stock
// movements, and its queued outbox entries are written in a SINGLE IndexedDB
// transaction. A sale can never exist without being queued for sync, and a queue entry
// can never exist without its sale. Anything less would either lose a sale or
// double-post one.

const DB_NAME = 'pos';

// Bump this whenever the schema changes. Upgrades are additive: existing object stores are left
// alone and only indexes are added, so a terminal that updates keeps its recorded sales. A
// destructive migration on a till would mean losing a day's trading, which is never acceptable.
const DB_VERSION = 4;

// Object store names.
const STORES = {
    stores: 'stores',
    products: 'products',
    sales: 'sales',
    outbox: 'outbox',
    stockMovements: 'stockMovements',
    syncCursors: 'syncCursors',
    meta: 'meta',
    payloads: 'payloads',
    returns: 'returns',
    employees: 'employees',
    shifts: 'shifts',
    drawerEvents: 'drawerEvents',
    transfers: 'transfers',
};

let dbPromise = null;

function openDb() {
    if (dbPromise) {
        return dbPromise;
    }

    dbPromise = new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME, DB_VERSION);

        request.onupgradeneeded = event => {
            const db = event.target.result;

            if (!db.objectStoreNames.contains(STORES.stores)) {
                db.createObjectStore(STORES.stores, { keyPath: 'id' });
            }

            if (!db.objectStoreNames.contains(STORES.products)) {
                const products = db.createObjectStore(STORES.products, { keyPath: 'id' });
                // Barcodes are the hot lookup at the till, so they are indexed rather
                // than scanned.
                products.createIndex('barcode', 'barcode', { unique: false });
                products.createIndex('sku', 'sku', { unique: false });
            }

            if (!db.objectStoreNames.contains(STORES.sales)) {
                const sales = db.createObjectStore(STORES.sales, { keyPath: 'id' });
                sales.createIndex('storeId', 'storeId', { unique: false });
                sales.createIndex('completedAt', 'completedAt', { unique: false });
                sales.createIndex('businessDate', 'businessDate', { unique: false });
            }

            if (!db.objectStoreNames.contains(STORES.outbox)) {
                const outbox = db.createObjectStore(STORES.outbox, { keyPath: 'id' });
                // Sync drains by insertion order, so the sequence is the index.
                outbox.createIndex('enqueuedSeq', 'enqueuedSeq', { unique: false });
                outbox.createIndex('status', 'status', { unique: false });
            }

            if (!db.objectStoreNames.contains(STORES.stockMovements)) {
                const movements = db.createObjectStore(STORES.stockMovements, { keyPath: 'id' });
                movements.createIndex('productId', 'productId', { unique: false });
                movements.createIndex('storeId', 'storeId', { unique: false });
            } else {
                // Added in schema version 2. Indexes can be created on an existing store without
                // touching its records, which is what makes this upgrade safe on a live till.
                const movements = event.target.transaction.objectStore(STORES.stockMovements);

                if (!movements.indexNames.contains('storeId')) {
                    movements.createIndex('storeId', 'storeId', { unique: false });
                }
            }

            if (!db.objectStoreNames.contains(STORES.syncCursors)) {
                db.createObjectStore(STORES.syncCursors, { keyPath: 'stream' });
            }

            if (!db.objectStoreNames.contains(STORES.meta)) {
                db.createObjectStore(STORES.meta, { keyPath: 'key' });
            }

            // Serialised payloads awaiting sync, keyed by entity id. The outbox holds a
            // reference rather than a copy, so a payload is produced once, in C#, and read
            // back here at send time.
            if (!db.objectStoreNames.contains(STORES.payloads)) {
                db.createObjectStore(STORES.payloads, { keyPath: 'id' });
            }

            // Refunds. Append-only, like sales: a return is a financial event in its own right,
            // not an edit to the sale it refers to.
            if (!db.objectStoreNames.contains(STORES.returns)) {
                const returns = db.createObjectStore(STORES.returns, { keyPath: 'id' });
                returns.createIndex('originalSaleId', 'originalSaleId', { unique: false });
                returns.createIndex('storeId', 'storeId', { unique: false });
                returns.createIndex('businessDate', 'businessDate', { unique: false });
            }

            // The roster. A replica of head-office data, pulled like the catalogue: a terminal
            // that cannot sign an operator in cannot attribute a sale to anyone, so the roster has
            // to be present locally and work offline.
            if (!db.objectStoreNames.contains(STORES.employees)) {
                const employees = db.createObjectStore(STORES.employees, { keyPath: 'id' });
                employees.createIndex('storeId', 'storeId', { unique: false });
            }

            // Shifts. Terminal-owned and effectively append-only: opened once, closed once.
            if (!db.objectStoreNames.contains(STORES.shifts)) {
                const shifts = db.createObjectStore(STORES.shifts, { keyPath: 'id' });
                shifts.createIndex('terminalId', 'terminalId', { unique: false });
                shifts.createIndex('storeId', 'storeId', { unique: false });
                shifts.createIndex('openedAt', 'openedAt', { unique: false });

                // Deliberately no index on closedAt: IndexedDB cannot index null, so an open shift
                // would simply be absent from it and a lookup for "the open shift" would silently
                // find nothing. Openness is filtered in code instead.
            }

            // Drawer events. Append-only, including every no-sale opening, which is the classic
            // cover for a small theft and is therefore never deleted or reset.
            if (!db.objectStoreNames.contains(STORES.drawerEvents)) {
                const drawerEvents = db.createObjectStore(STORES.drawerEvents, { keyPath: 'id' });
                drawerEvents.createIndex('shiftId', 'shiftId', { unique: false });
            }

            // Stock transfers between stores. A document with a two-stage lifecycle: dispatched
            // out of one store, received into another, possibly days apart and possibly offline.
            if (!db.objectStoreNames.contains(STORES.transfers)) {
                const transfers = db.createObjectStore(STORES.transfers, { keyPath: 'id' });

                // Indexed on both sides, because the two ends of a transfer are different stores
                // asking different questions: what have we sent, and what are we expecting.
                transfers.createIndex('fromStoreId', 'fromStoreId', { unique: false });
                transfers.createIndex('toStoreId', 'toStoreId', { unique: false });
            }
        };

        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
        request.onblocked = () => reject(new Error(
            'The database is blocked by another open tab. Close other POS tabs and retry.'));
    });

    return dbPromise;
}

function promisify(request) {
    return new Promise((resolve, reject) => {
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

/** Resolves when the transaction commits, rejects if it aborts. */
function txDone(tx) {
    return new Promise((resolve, reject) => {
        tx.oncomplete = () => resolve();
        tx.onabort = () => reject(tx.error || new Error('Transaction aborted.'));
        tx.onerror = () => reject(tx.error);
    });
}

export async function init() {
    const db = await openDb();

    // Local data is not a cache. If the browser evicts it, a day of trading is gone,
    // so persistent storage is requested rather than merely desired.
    let persisted = false;
    if (navigator.storage && navigator.storage.persist) {
        try {
            persisted = await navigator.storage.persisted() || await navigator.storage.persist();
        } catch {
            persisted = false;
        }
    }

    return { name: db.name, version: db.version, persisted };
}

/** Reports whether the browser has granted durable storage. */
export async function isPersisted() {
    if (!navigator.storage || !navigator.storage.persisted) {
        return false;
    }

    try {
        return await navigator.storage.persisted();
    } catch {
        return false;
    }
}

/** Rough storage usage, for the device settings screen. */
export async function storageEstimate() {
    if (!navigator.storage || !navigator.storage.estimate) {
        return null;
    }

    try {
        const estimate = await navigator.storage.estimate();
        return { usage: estimate.usage || 0, quota: estimate.quota || 0 };
    } catch {
        return null;
    }
}

// --- Generic single-store operations ---------------------------------------------

export async function put(storeName, value) {
    const db = await openDb();
    const tx = db.transaction(storeName, 'readwrite');
    tx.objectStore(storeName).put(value);
    await txDone(tx);
    return value;
}

export async function putMany(storeName, values) {
    const db = await openDb();
    const tx = db.transaction(storeName, 'readwrite');
    const store = tx.objectStore(storeName);
    for (const value of values) {
        store.put(value);
    }

    await txDone(tx);
    return values.length;
}

export async function get(storeName, key) {
    const db = await openDb();
    const tx = db.transaction(storeName, 'readonly');
    const result = await promisify(tx.objectStore(storeName).get(key));
    return result === undefined ? null : result;
}

export async function getAll(storeName) {
    const db = await openDb();
    const tx = db.transaction(storeName, 'readonly');
    const result = await promisify(tx.objectStore(storeName).getAll());
    return result || [];
}

export async function remove(storeName, key) {
    const db = await openDb();
    const tx = db.transaction(storeName, 'readwrite');
    tx.objectStore(storeName).delete(key);
    await txDone(tx);
}

export async function count(storeName) {
    const db = await openDb();
    const tx = db.transaction(storeName, 'readonly');
    return await promisify(tx.objectStore(storeName).count());
}

export async function clearAll(storeName) {
    const db = await openDb();
    const tx = db.transaction(storeName, 'readwrite');
    tx.objectStore(storeName).clear();
    await txDone(tx);
}

/**
 * Looks a product up by barcode, which is the hot path at the till.
 *
 * Scoped to a store, and this one costs money if it is not. The products store holds one row per
 * store per product, and a chain sells the same barcode in every branch — so `barcode` is NOT unique
 * across the store, and `index.get(barcode)` returned whichever row happened to come first. A scan at
 * one shop's till could resolve to another shop's row and charge that shop's price and tax.
 *
 * `getAll` rather than `get` for that reason: the index yields every store's copy and the right one
 * is picked here.
 *
 * @param {string} storeId Store whose catalogue to search.
 * @param {string} barcode The scanned code.
 */
export async function findProductByBarcode(storeId, barcode) {
    const db = await openDb();
    const tx = db.transaction(STORES.products, 'readonly');
    const index = tx.objectStore(STORES.products).index('barcode');
    const all = await promisify(index.getAll(barcode));

    return (all || []).find(p => p.storeId === storeId) || null;
}

/**
 * Finds products a store may sell whose name contains the term.
 *
 * The fallback for a label that will not scan. There is no name index, so this walks the catalogue;
 * the result is capped because it feeds a type-ahead list rather than a report.
 *
 * It answers the same question the barcode lookup does — what may THIS store sell right now — and
 * that means three filters, not one. Store, because a terminal holds the whole estate's catalogue
 * after a sync and a search that ignored the store would offer one shop's products at another's till,
 * with a basket about to be charged for them. Deleted, because a head-office deletion is
 * authoritative and `getProducts` already refuses to return one; a search that returned it anyway
 * would resurrect exactly what that rule exists to bury. And withdrawn, which the till never shows.
 *
 * @param {string} storeId Store whose catalogue to search.
 * @param {string} term    What the operator typed.
 * @param {number} limit   Caps the type-ahead list.
 */
export async function searchProductsByName(storeId, term, limit = 8) {
    if (!term || term.trim().length < 2) {
        return [];
    }

    const needle = term.trim().toLowerCase();
    const all = await getAll(STORES.products);

    return all
        .filter(p => p.storeId === storeId)
        .filter(p => !p.isDeleted)
        .filter(p => (p.isActive === undefined || p.isActive) &&
            typeof p.name === 'string' &&
            p.name.toLowerCase().includes(needle))
        .sort((a, b) => a.name.localeCompare(b.name))
        .slice(0, Math.max(1, limit));
}

/** Sales for a business date, ordered by completion time. */
export async function getSalesForDate(storeId, businessDate) {
    const db = await openDb();
    const tx = db.transaction(STORES.sales, 'readonly');
    const index = tx.objectStore(STORES.sales).index('businessDate');
    const all = await promisify(index.getAll(businessDate));

    return (all || [])
        .filter(sale => sale.storeId === storeId)
        .sort((a, b) => (a.completedAt < b.completedAt ? -1 : 1));
}

/**
 * Finds a sale by the receipt number printed on the customer's slip.
 *
 * How a refund starts in practice: the customer presents a receipt and the operator types the
 * number from it. The comparison is case-insensitive and trimmed, because the number is read off
 * paper and typed by hand.
 */
export async function findSaleByNumber(saleNumber) {
    if (!saleNumber || !saleNumber.trim()) {
        return null;
    }

    const needle = saleNumber.trim().toLowerCase();
    const all = await getAll(STORES.sales);

    return all.find(s => typeof s.number === 'string' && s.number.toLowerCase() === needle) || null;
}

/** Sales for a store on a trading day, most recent first. */
export async function findRecentSales(storeId, businessDate, limit = 50) {
    const sales = await getSalesForDate(storeId, businessDate);

    return sales
        .sort((a, b) => (a.completedAt < b.completedAt ? 1 : -1))
        .slice(0, Math.max(1, limit));
}

// --- Catalogue management ---------------------------------------------------------

/**
 * Lists a store's catalogue.
 *
 * @param {string} storeId
 * @param {boolean} includeInactive Include products withdrawn from sale. The management screen
 *   needs them so one can be brought back; the till never does.
 * @param {number} limit Bounds what is loaded into the browser.
 */
export async function getProducts(storeId, includeInactive = false, limit = 500) {
    const all = await getAll(STORES.products);

    return all
        .filter(p => p.storeId === storeId)
        // A head-office deletion is authoritative and is never returned, not even to the management
        // screen — otherwise the next catalogue pull would resurrect it. A local deactivation is
        // different: it is reversible, so the management screen can ask for it.
        .filter(p => !p.isDeleted)
        .filter(p => includeInactive || p.isActive !== false)
        .sort((a, b) => String(a.name).localeCompare(String(b.name)))
        .slice(0, Math.max(1, limit));
}

// --- Stock ledger -----------------------------------------------------------------

/**
 * Records a manual stock movement — a count correction, goods receipt, or write-off.
 *
 * Appends to the same ledger as sale and refund movements rather than editing a stored level. A
 * correction is itself an event, so the history still explains the current figure.
 */
export async function recordStockMovement(movement, movementPayload, terminalId) {
    const db = await openDb();

    const tx = db.transaction(
        [STORES.stockMovements, STORES.outbox, STORES.meta, STORES.payloads],
        'readwrite');

    const movementsStore = tx.objectStore(STORES.stockMovements);
    const outbox = tx.objectStore(STORES.outbox);
    const meta = tx.objectStore(STORES.meta);
    const payloads = tx.objectStore(STORES.payloads);

    const seqRow = await promisify(meta.get('outboxSeq'));
    const seq = (seqRow ? seqRow.value : 0) + 1;

    movementsStore.put(movement);
    payloads.put({ id: movement.id, payload: movementPayload });

    outbox.put({
        id: `movement:${movement.id}`,
        enqueuedSeq: seq,
        status: 'pending',
        attempts: 0,
        lastError: null,
        enqueuedAt: new Date().toISOString(),
        entityType: 'stockMovement',
        entityId: movement.id,
        terminalId,
        terminalSeq: movement.terminalSeq,
    });

    meta.put({ key: 'outboxSeq', value: seq });

    await txDone(tx);

    return { movementId: movement.id, outboxSeq: seq };
}

/** Stock movements for a store, optionally for one product, most recent first. */
export async function getStockMovements(storeId, productId = null, limit = 200) {
    const db = await openDb();
    const tx = db.transaction(STORES.stockMovements, 'readonly');
    const index = tx.objectStore(STORES.stockMovements).index('storeId');
    const all = await promisify(index.getAll(storeId));

    return (all || [])
        .filter(m => !productId || m.productId === productId)
        .sort((a, b) => (a.occurredAt < b.occurredAt ? 1 : -1))
        .slice(0, Math.max(1, limit));
}

// --- Meta key/value ---------------------------------------------------------------

export async function getMeta(key, fallback = null) {
    const row = await get(STORES.meta, key);
    return row ? row.value : fallback;
}

export async function setMeta(key, value) {
    await put(STORES.meta, { key, value });
    return value;
}

// --- Terminal sequencing ----------------------------------------------------------

/**
 * Reserves a contiguous block of terminal sequence numbers and returns the first.
 *
 * Every record this terminal authors — sales, refunds, stock movements, shifts, drawer
 * events — takes its `terminalSeq` from this ONE counter, and the counter lives in
 * IndexedDB rather than in memory.
 *
 * Both halves of that matter:
 *
 *   * One counter, because they all share a single ordered stream. Two independent
 *     in-memory counters both starting at 1 would hand a sale and a drawer event the same
 *     position, and the hub's unique index on (terminalId, terminalSeq) would refuse the
 *     second one.
 *   * Persisted, because a browser refresh otherwise restarts the count at 1 and every
 *     record after the reload collides with one pushed before it. The hub would reject a
 *     legitimate sale as a duplicate sequence position, and that sale would be stuck on
 *     the terminal's outbox forever.
 *
 * Reserving a block up front keeps a sale and its stock movements on consecutive
 * positions, so the stream stays gap-free — which is what lets the hub treat a gap as
 * evidence of loss rather than as noise it must ignore.
 *
 * @param {number} count How many consecutive numbers are needed.
 * @returns {Promise<number>} The first number in the reserved block.
 */
export async function reserveTerminalSequence(count = 1) {
    if (!Number.isInteger(count) || count < 1) {
        throw new Error('A sequence reservation must cover at least one number.');
    }

    const db = await openDb();
    const tx = db.transaction(STORES.meta, 'readwrite');
    const store = tx.objectStore(STORES.meta);

    // Read and write in one transaction: two tabs committing at the same moment cannot
    // claim the same block.
    const existing = await promisify(store.get('terminalSeq'));
    const first = (existing ? existing.value : 0) + 1;

    store.put({ key: 'terminalSeq', value: first + count - 1 });
    await txDone(tx);

    return first;
}

// --- Sale numbering ---------------------------------------------------------------

/**
 * Allocates the next sale number for a store and business date.
 *
 * The counter is per store, per date, and persisted, so a terminal that reloads
 * mid-shift resumes the sequence instead of restarting at 1 and colliding with a
 * receipt already handed to a customer.
 */
export async function nextSaleSequence(storeId, businessDate) {
    const db = await openDb();
    const tx = db.transaction(STORES.meta, 'readwrite');
    const store = tx.objectStore(STORES.meta);
    const key = `saleSeq:${storeId}:${businessDate}`;

    const existing = await promisify(store.get(key));
    const next = (existing ? existing.value : 0) + 1;

    store.put({ key, value: next });
    await txDone(tx);

    return next;
}

// --- The atomic commit ------------------------------------------------------------

/**
 * Writes a completed sale, its stock movements, and its outbox entries in ONE
 * transaction.
 *
 * This is the durability guarantee the whole offline design rests on. Either the sale
 * is durably recorded AND queued for sync, or nothing happened at all and the cashier
 * still has the basket on screen. A partial write here would mean either a lost sale or
 * a sale that never reaches head office.
 *
 * @param {object} sale               The completed sale, already carrying its final number.
 * @param {string} salePayload        The sale serialised for the hub.
 * @param {object[]} movements        Append-only stock movements caused by the sale.
 * @param {string[]} movementPayloads Each movement serialised, positionally matching.
 * @param {string} terminalId         Identity of this till, used for the ordering key.
 */
export async function commitSale(sale, salePayload, movements, movementPayloads, terminalId) {
    if (!Array.isArray(movements) || !Array.isArray(movementPayloads)) {
        throw new Error('movements and movementPayloads must both be arrays.');
    }

    if (movements.length !== movementPayloads.length) {
        throw new Error(
            `Each movement needs exactly one payload: got ${movements.length} movements ` +
            `and ${movementPayloads.length} payloads.`);
    }

    const db = await openDb();

    const tx = db.transaction(
        [STORES.sales, STORES.outbox, STORES.stockMovements, STORES.meta, STORES.payloads],
        'readwrite');

    const sales = tx.objectStore(STORES.sales);
    const outbox = tx.objectStore(STORES.outbox);
    const movementsStore = tx.objectStore(STORES.stockMovements);
    const meta = tx.objectStore(STORES.meta);
    const payloads = tx.objectStore(STORES.payloads);

    // Allocate the outbox sequence inside the same transaction so entries stay strictly
    // ordered and no two concurrent commits can claim the same sequence.
    const seqRow = await promisify(meta.get('outboxSeq'));
    let seq = seqRow ? seqRow.value : 0;

    sales.put(sale);
    payloads.put({ id: sale.id, payload: salePayload });

    seq += 1;
    outbox.put({
        id: `sale:${sale.id}`,
        enqueuedSeq: seq,
        status: 'pending',
        attempts: 0,
        lastError: null,
        enqueuedAt: new Date().toISOString(),
        entityType: 'sale',
        entityId: sale.id,
        terminalId,
        terminalSeq: sale.terminalSeq,
    });

    for (let i = 0; i < movements.length; i++) {
        const movement = movements[i];

        movementsStore.put(movement);
        payloads.put({ id: movement.id, payload: movementPayloads[i] });

        seq += 1;
        outbox.put({
            id: `movement:${movement.id}`,
            enqueuedSeq: seq,
            status: 'pending',
            attempts: 0,
            lastError: null,
            enqueuedAt: new Date().toISOString(),
            entityType: 'stockMovement',
            entityId: movement.id,
            terminalId,
            terminalSeq: movement.terminalSeq,
        });
    }

    meta.put({ key: 'outboxSeq', value: seq });

    await txDone(tx);

    return { saleId: sale.id, queued: movements.length + 1, outboxSeq: seq };
}

/**
 * Reads the serialised payload a queued entry refers to.
 *
 * The outbox holds a reference rather than a copy, so the payload is produced once, in
 * C#, and fetched here only when the entry is actually sent.
 */
export async function getSyncPayload(entityId) {
    const row = await get(STORES.payloads, entityId);
    return row ? row.payload : null;
}

// --- The atomic refund commit -----------------------------------------------------

/**
 * Writes a refund, the stock movements returning goods to the ledger, and the outbox
 * entries, in ONE transaction.
 *
 * The same guarantee a sale gets, for the same reason: a refund that is stored but never
 * queued would never reach head office, and the goods would stay missing from stock with
 * nothing to explain why.
 *
 * @param {object} salesReturn        The refund, already carrying its final number.
 * @param {string} returnPayload      The refund serialised for the hub.
 * @param {object[]} movements        Positive movements returning goods to stock.
 * @param {string[]} movementPayloads Each movement serialised, positionally matching.
 * @param {string} terminalId         Identity of this till, for the ordering key.
 */
export async function commitReturn(salesReturn, returnPayload, movements, movementPayloads, terminalId) {
    if (!Array.isArray(movements) || !Array.isArray(movementPayloads)) {
        throw new Error('movements and movementPayloads must both be arrays.');
    }

    if (movements.length !== movementPayloads.length) {
        throw new Error(
            `Each movement needs exactly one payload: got ${movements.length} movements ` +
            `and ${movementPayloads.length} payloads.`);
    }

    const db = await openDb();

    const tx = db.transaction(
        [STORES.returns, STORES.outbox, STORES.stockMovements, STORES.meta, STORES.payloads],
        'readwrite');

    const returns = tx.objectStore(STORES.returns);
    const outbox = tx.objectStore(STORES.outbox);
    const movementsStore = tx.objectStore(STORES.stockMovements);
    const meta = tx.objectStore(STORES.meta);
    const payloads = tx.objectStore(STORES.payloads);

    const seqRow = await promisify(meta.get('outboxSeq'));
    let seq = seqRow ? seqRow.value : 0;

    returns.put(salesReturn);
    payloads.put({ id: salesReturn.id, payload: returnPayload });

    seq += 1;
    outbox.put({
        id: `return:${salesReturn.id}`,
        enqueuedSeq: seq,
        status: 'pending',
        attempts: 0,
        lastError: null,
        enqueuedAt: new Date().toISOString(),
        entityType: 'salesReturn',
        entityId: salesReturn.id,
        terminalId,
        terminalSeq: salesReturn.terminalSeq,
    });

    for (let i = 0; i < movements.length; i++) {
        const movement = movements[i];

        movementsStore.put(movement);
        payloads.put({ id: movement.id, payload: movementPayloads[i] });

        seq += 1;
        outbox.put({
            id: `movement:${movement.id}`,
            enqueuedSeq: seq,
            status: 'pending',
            attempts: 0,
            lastError: null,
            enqueuedAt: new Date().toISOString(),
            entityType: 'stockMovement',
            entityId: movement.id,
            terminalId,
            terminalSeq: movement.terminalSeq,
        });
    }

    meta.put({ key: 'outboxSeq', value: seq });

    await txDone(tx);

    return { returnId: salesReturn.id, queued: movements.length + 1, outboxSeq: seq };
}

/** Every refund recorded against a sale, needed to work out what remains refundable. */
export async function getReturnsForSale(originalSaleId) {
    const db = await openDb();
    const tx = db.transaction(STORES.returns, 'readonly');
    const index = tx.objectStore(STORES.returns).index('originalSaleId');
    const all = await promisify(index.getAll(originalSaleId));

    return (all || []).sort((a, b) => (a.completedAt < b.completedAt ? -1 : 1));
}

/** Refunds recorded for one trading day. */
export async function getReturnsForDate(storeId, businessDate) {
    const db = await openDb();
    const tx = db.transaction(STORES.returns, 'readonly');
    const index = tx.objectStore(STORES.returns).index('businessDate');
    const all = await promisify(index.getAll(businessDate));

    return (all || [])
        .filter(r => r.storeId === storeId)
        .sort((a, b) => (a.completedAt < b.completedAt ? -1 : 1));
}

/**
 * Allocates the next refund number.
 *
 * A separate counter from sales, so a refund number can never be mistaken for a receipt
 * number on a document that is explicitly not a receipt.
 */
export async function nextReturnSequence(storeId, businessDate) {
    const db = await openDb();
    const tx = db.transaction(STORES.meta, 'readwrite');
    const store = tx.objectStore(STORES.meta);
    const key = `returnSeq:${storeId}:${businessDate}`;

    const existing = await promisify(store.get(key));
    const next = (existing ? existing.value : 0) + 1;

    store.put({ key, value: next });
    await txDone(tx);

    return next;
}

// --- Outbox draining --------------------------------------------------------------

/**
 * The oldest pending entries, in enqueue order.
 *
 * Ordered by the local sequence rather than a timestamp: clocks drift and two entries
 * can share a millisecond, which would silently reorder a store's trading history.
 */
export async function peekOutbox(limit = 50) {
    const db = await openDb();
    const tx = db.transaction(STORES.outbox, 'readonly');
    const index = tx.objectStore(STORES.outbox).index('enqueuedSeq');

    const results = [];
    await new Promise((resolve, reject) => {
        const cursorRequest = index.openCursor();
        cursorRequest.onsuccess = () => {
            const cursor = cursorRequest.result;
            if (!cursor || results.length >= limit) {
                resolve();
                return;
            }

            if (cursor.value.status === 'pending') {
                results.push(cursor.value);
            }

            cursor.continue();
        };
        cursorRequest.onerror = () => reject(cursorRequest.error);
    });

    return results;
}

/**
 * Marks entries as acknowledged and removes them, along with the payloads they referenced.
 *
 * Called only after the server has confirmed receipt. Deleting optimistically would
 * lose sales whenever a push succeeded but its response was lost.
 *
 * The payloads go in the same transaction, and that is not tidiness. A payload is a full serialised
 * copy of a record — the largest thing this database holds — and nothing used to delete one, so every
 * sale, movement, refund, shift, drawer event, and transfer this till ever recorded stayed in
 * IndexedDB for the life of the till. A shop that fills its storage quota loses its trading, which is
 * the one outcome the local store exists to prevent. The entity id comes off the outbox row, so the
 * right payload is released and a movement's is not taken with its sale's.
 */
export async function acknowledgeOutbox(ids) {
    const db = await openDb();
    const tx = db.transaction([STORES.outbox, STORES.payloads], 'readwrite');
    const store = tx.objectStore(STORES.outbox);
    const payloads = tx.objectStore(STORES.payloads);

    for (const id of ids) {
        const row = await promisify(store.get(id));

        if (row && row.entityId) {
            payloads.delete(row.entityId);
        }

        store.delete(id);
    }

    await txDone(tx);
    return ids.length;
}

/** Records a failed push attempt so the entry can be retried with backoff. */
export async function markOutboxFailed(id, error) {
    const db = await openDb();
    const tx = db.transaction(STORES.outbox, 'readwrite');
    const store = tx.objectStore(STORES.outbox);

    const row = await promisify(store.get(id));
    if (row) {
        row.attempts = (row.attempts || 0) + 1;
        row.lastError = error ? String(error) : 'Unknown error';

        // After repeated failures an entry is parked rather than retried forever, so a
        // single poison record cannot block the whole queue behind it.
        row.status = row.attempts >= 8 ? 'dead' : 'pending';
        store.put(row);
    }

    await txDone(tx);
}

/** Counts entries the sync loop still has to send, for the sync status indicator. */
export async function outboxSummary() {
    const db = await openDb();
    const tx = db.transaction(STORES.outbox, 'readonly');
    const all = await promisify(tx.objectStore(STORES.outbox).getAll());

    const summary = { pending: 0, dead: 0, total: 0 };
    for (const row of all || []) {
        summary.total += 1;
        if (row.status === 'dead') {
            summary.dead += 1;
        } else {
            summary.pending += 1;
        }
    }

    return summary;
}

// --- Sync cursors -----------------------------------------------------------------

export async function getCursor(stream) {
    const row = await get(STORES.syncCursors, stream);
    return row ? row.cursor : null;
}

export async function setCursor(stream, cursor) {
    await put(STORES.syncCursors, { stream, cursor, updatedAt: new Date().toISOString() });
    return cursor;
}

/** Wipes local data. Used when a terminal is revoked or re-provisioned. */
export async function wipe() {
    const db = await openDb();
    const names = Array.from(db.objectStoreNames);

    const tx = db.transaction(names, 'readwrite');
    for (const name of names) {
        tx.objectStore(name).clear();
    }

    await txDone(tx);
}

// --- Shifts and the cash drawer ---------------------------------------------------

/** Employees who can sign in at a store. */
export async function getEmployees(storeId) {
    const db = await openDb();
    const tx = db.transaction(STORES.employees, 'readonly');
    const index = tx.objectStore(STORES.employees).index('storeId');
    const all = await promisify(index.getAll(storeId));

    return (all || [])
        .filter(e => e.isActive !== false)
        .sort((a, b) => String(a.name).localeCompare(String(b.name)));
}

/**
 * The shift currently open on a terminal, or null.
 *
 * One open shift per terminal, enforced where it is written rather than only where it is
 * checked. Two open drawers on one till would make every cash-up ambiguous.
 */
export async function getOpenShift(terminalId) {
    const db = await openDb();
    const tx = db.transaction(STORES.shifts, 'readonly');
    const index = tx.objectStore(STORES.shifts).index('terminalId');
    const all = await promisify(index.getAll(terminalId));

    const open = (all || [])
        .filter(s => !s.closedAt)
        .sort((a, b) => (a.openedAt < b.openedAt ? 1 : -1));

    return open.length ? open[0] : null;
}

export async function getShift(shiftId) {
    return await get(STORES.shifts, shiftId);
}

/** Shifts for a store, most recent first. */
export async function getShifts(storeId, limit = 50) {
    const db = await openDb();
    const tx = db.transaction(STORES.shifts, 'readonly');
    const index = tx.objectStore(STORES.shifts).index('storeId');
    const all = await promisify(index.getAll(storeId));

    return (all || [])
        .sort((a, b) => (a.openedAt < b.openedAt ? 1 : -1))
        .slice(0, Math.max(1, limit));
}

export async function getDrawerEvents(shiftId) {
    const db = await openDb();
    const tx = db.transaction(STORES.drawerEvents, 'readonly');
    const index = tx.objectStore(STORES.drawerEvents).index('shiftId');
    const all = await promisify(index.getAll(shiftId));

    return (all || []).sort((a, b) => a.terminalSeq - b.terminalSeq);
}

/** Sales rung during a shift. */
export async function getShiftSales(shiftId) {
    const all = await getAll(STORES.sales);

    return (all || [])
        .filter(s => s.shiftId === shiftId)
        .sort((a, b) => a.terminalSeq - b.terminalSeq);
}

/** Refunds processed during a shift. */
export async function getShiftReturns(shiftId) {
    const all = await getAll(STORES.returns);

    return (all || [])
        .filter(r => r.shiftId === shiftId)
        .sort((a, b) => a.terminalSeq - b.terminalSeq);
}

/**
 * Opens a shift and records the opening float as a drawer event, in ONE transaction.
 *
 * The float is cash that entered the drawer, so a cash-up has to account for it. Splitting the
 * shift from its float event would allow an open shift with no float recorded, which reconciles
 * as a shortage of exactly the float — every time.
 *
 * @throws {Error} When a shift is already open on this terminal.
 */
export async function openShift(shift, shiftPayload, openingEvent, eventPayload, terminalId) {
    const db = await openDb();

    const tx = db.transaction(
        [STORES.shifts, STORES.drawerEvents, STORES.outbox, STORES.meta, STORES.payloads],
        'readwrite');

    const shifts = tx.objectStore(STORES.shifts);
    const events = tx.objectStore(STORES.drawerEvents);

    // Checked inside the transaction, not before it. Two tabs opening a drawer at the same
    // instant would both pass a check made outside, and the till would end up with two open
    // shifts and a cash-up that cannot be attributed.
    const existing = await promisify(shifts.index('terminalId').getAll(terminalId));
    if ((existing || []).some(s => !s.closedAt)) {
        throw new Error('A shift is already open on this terminal. Close it before opening another.');
    }

    shifts.put(shift);
    events.put(openingEvent);

    const seq = await enqueueIn(tx, [
        { entityType: 'shift', entityId: shift.id, terminalSeq: shift.terminalSeq, payload: shiftPayload },
        { entityType: 'drawerEvent', entityId: openingEvent.id, terminalSeq: openingEvent.terminalSeq, payload: eventPayload },
    ], terminalId);

    await txDone(tx);

    return { shiftId: shift.id, queued: 2, outboxSeq: seq };
}

/**
 * Closes a shift with its counted drawer, in ONE transaction.
 *
 * The close and its count are one operation: a shift marked closed without the count recorded
 * would lose the figure the whole cash-up exists to produce, and there would be no way to tell
 * afterwards whether the drawer was short or simply never counted.
 *
 * @throws {Error} When the shift is missing or has already been closed.
 */
export async function closeShift(shift, shiftPayload, closingEvent, eventPayload, terminalId) {
    const db = await openDb();

    const tx = db.transaction(
        [STORES.shifts, STORES.drawerEvents, STORES.outbox, STORES.meta, STORES.payloads],
        'readwrite');

    const shifts = tx.objectStore(STORES.shifts);
    const existing = await promisify(shifts.get(shift.id));

    if (!existing) {
        throw new Error('That shift is not on this terminal.');
    }

    // Closing twice would overwrite the count that was signed off, destroying the evidence of
    // whatever it showed — which is precisely what someone covering a shortage would want.
    if (existing.closedAt) {
        throw new Error('That shift has already been closed.');
    }

    shifts.put(shift);
    tx.objectStore(STORES.drawerEvents).put(closingEvent);

    const seq = await enqueueIn(tx, [
        { entityType: 'shift', entityId: shift.id, terminalSeq: shift.terminalSeq, payload: shiftPayload },
        { entityType: 'drawerEvent', entityId: closingEvent.id, terminalSeq: closingEvent.terminalSeq, payload: eventPayload },
    ], terminalId);

    await txDone(tx);

    return { shiftId: shift.id, queued: 2, outboxSeq: seq };
}

/**
 * Records a drawer event against an open shift.
 *
 * Refused against a closed one: it would change a cash-up that has already been signed off.
 */
export async function recordDrawerEvent(drawerEvent, eventPayload, terminalId) {
    const db = await openDb();

    const tx = db.transaction(
        [STORES.drawerEvents, STORES.shifts, STORES.outbox, STORES.meta, STORES.payloads],
        'readwrite');

    const shift = await promisify(tx.objectStore(STORES.shifts).get(drawerEvent.shiftId));

    if (!shift) {
        throw new Error('That drawer event belongs to a shift this terminal does not have.');
    }

    if (shift.closedAt) {
        throw new Error('The shift is closed, so no further drawer activity can be recorded against it.');
    }

    tx.objectStore(STORES.drawerEvents).put(drawerEvent);

    const seq = await enqueueIn(tx, [
        { entityType: 'drawerEvent', entityId: drawerEvent.id, terminalSeq: drawerEvent.terminalSeq, payload: eventPayload },
    ], terminalId);

    await txDone(tx);

    return { shiftId: drawerEvent.shiftId, queued: 1, outboxSeq: seq };
}

/**
 * Writes outbox entries and their payloads inside an existing transaction.
 *
 * Shared by the shift commits so all three write the queue the same way the sale, refund, and
 * stock commits do — one payload per entity, written in the same transaction as the record it
 * describes.
 *
 * @returns {Promise<number>} The outbox sequence after the entries were added.
 */
async function enqueueIn(tx, entries, terminalId) {
    const outbox = tx.objectStore(STORES.outbox);
    const meta = tx.objectStore(STORES.meta);
    const payloads = tx.objectStore(STORES.payloads);

    const seqRow = await promisify(meta.get('outboxSeq'));
    let seq = seqRow ? seqRow.value : 0;

    for (const entry of entries) {
        payloads.put({ id: entry.entityId, payload: entry.payload });

        seq += 1;
        outbox.put({
            id: `${entry.entityType}:${entry.entityId}`,
            enqueuedSeq: seq,
            status: 'pending',
            attempts: 0,
            lastError: null,
            enqueuedAt: new Date().toISOString(),
            entityType: entry.entityType,
            entityId: entry.entityId,
            terminalId,
            terminalSeq: entry.terminalSeq,
        });
    }

    meta.put({ key: 'outboxSeq', value: seq });

    return seq;
}

// --- Stock transfers --------------------------------------------------------------

/**
 * Writes a transfer event and the stock movements it caused, in ONE transaction.
 *
 * A dispatch takes stock out of the sending store; a receipt books it into the receiving one. Both
 * are one commit here for the same reason a sale is: a transfer whose status advanced without its
 * movements would be stock that vanished from the ledger while the paperwork said it had moved, and
 * there would be no way afterwards to tell which was right.
 *
 * @param {object} transfer          The transfer in its new state.
 * @param {string} transferPayload   The transfer serialised for the hub.
 * @param {object[]} movements       Signed movements the event caused.
 * @param {string[]} movementPayloads Each movement serialised, positionally matching.
 * @param {string} terminalId        Identity of this till, for the ordering key.
 */
export async function commitTransfer(transfer, transferPayload, movements, movementPayloads, terminalId) {
    if (!Array.isArray(movements) || !Array.isArray(movementPayloads)) {
        throw new Error('movements and movementPayloads must both be arrays.');
    }

    if (movements.length !== movementPayloads.length) {
        throw new Error(
            `Each movement needs exactly one payload: got ${movements.length} movements ` +
            `and ${movementPayloads.length} payloads.`);
    }

    const db = await openDb();

    const tx = db.transaction(
        [STORES.transfers, STORES.outbox, STORES.stockMovements, STORES.meta, STORES.payloads],
        'readwrite');

    const transfers = tx.objectStore(STORES.transfers);

    // Checked inside the transaction, not before it. Two tabs committing the same transfer at the
    // same instant would both pass a check made outside, and the stock would move twice.
    const existing = await promisify(transfers.get(transfer.id));

    if (existing && existing.status === transfer.status) {
        throw new Error(`That transfer has already been recorded as ${String(transfer.status).toLowerCase()}.`);
    }

    transfers.put(transfer);

    const seq = await enqueueIn(tx, [
        { entityType: 'stockTransfer', entityId: transfer.id, terminalSeq: 0, payload: transferPayload },
        ...movements.map((movement, i) => ({
            entityType: 'stockMovement',
            entityId: movement.id,
            terminalSeq: movement.terminalSeq,
            payload: movementPayloads[i],
        })),
    ], terminalId);

    await txDone(tx);

    return { transferId: transfer.id, queued: movements.length + 1, outboxSeq: seq };
}

/** Transfers this store sent, or is expecting. */
export async function getTransfers(storeId, direction = 'outgoing', limit = 100) {
    const db = await openDb();
    const indexName = direction === 'incoming' ? 'toStoreId' : 'fromStoreId';
    const tx = db.transaction(STORES.transfers, 'readonly');
    const index = tx.objectStore(STORES.transfers).index(indexName);
    const all = await promisify(index.getAll(storeId));

    return (all || [])
        .sort((a, b) => (a.createdAt < b.createdAt ? 1 : -1))
        .slice(0, Math.max(1, limit));
}

export function isSupported() {
    return typeof indexedDB !== 'undefined';
}
