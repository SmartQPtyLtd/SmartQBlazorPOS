// Verifies the terminal's real data layer.
//
// local-store.js holds every sale, refund, shift, drawer event, and transfer the till records. The
// C# side reaches it through JsLocalStore, but every C# test substitutes InMemoryLocalStore — so the
// JavaScript had never been executed by anything. It was verified by reading it, and by a
// hand-written mirror agreeing with a contract somebody wrote by hand. The worst data-integrity bug
// this project has had, two sequence counters handing a sale and a drawer event the same position,
// lived in this file.
//
// This runs the real module against an in-memory IndexedDB (tools/fake-indexeddb.mjs) and asserts
// the invariants the offline design rests on. No package, no install step, Node built-ins only.
//
// Run from the repository root:  node tools/verify-local-store.mjs

import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { installFakeIndexedDb } from './fake-indexeddb.mjs';
import { loadModule } from './module-loader.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const storePath = join(root, 'src/Pos.Web/wwwroot/js/local-store.js');

const databases = installFakeIndexedDb();
const store = await loadModule(storePath);

let step = 0;
let failures = 0;

function check(label, condition, detail = '') {
    step++;

    if (!condition) {
        failures++;
    }

    console.log(`${String(step).padStart(2)}. ${condition ? 'ok  ' : 'FAIL'} ${label}${detail ? ` -- ${detail}` : ''}`);
}

function checkEqual(label, actual, expected) {
    check(label, Object.is(actual, expected), `got ${JSON.stringify(actual)}, expected ${JSON.stringify(expected)}`);
}

/** A terminal identity, as the C# side supplies. */
const TILL = 'till-0001';

const sale = (id, storeId, date, seq, number) => ({
    id,
    storeId,
    number,
    terminalSeq: seq,
    terminalId: TILL,
    completedAt: `${date}T09:00:00.0000000+00:00`,
    businessDate: date,
    localHour: 9,
    currency: 'ZAR',
    taxMode: 'Inclusive',
    status: 'Completed',
    lines: [],
    tenders: [],
    subtotal: 100,
    totalDiscount: 0,
    taxTotal: 13,
    total: 100,
});

const movement = (id, storeId, productId, seq) => ({
    id,
    storeId,
    terminalId: TILL,
    terminalSeq: seq,
    productId,
    qtyDelta: -1,
    reason: 'Sale',
    reference: 'sale',
    occurredAt: '2026-03-25T09:00:00.0000000+00:00',
});

console.log('Local store:\n');

// --- schema -------------------------------------------------------------------

const info = await store.init();

checkEqual('init reports the database name', info.name, 'pos');
checkEqual('init reports the schema version', info.version, 4);
check('init reports durable storage was granted', info.persisted === true);
check('the store reports itself supported', store.isSupported() === true);

const db = databases.get('pos');

const expectedStores = [
    'stores', 'products', 'sales', 'outbox', 'stockMovements', 'syncCursors',
    'meta', 'payloads', 'returns', 'employees', 'shifts', 'drawerEvents', 'transfers',
];

const missing = expectedStores.filter(name => !db.stores.has(name));

check('every object store was created', missing.length === 0, missing.join(', '));

// The upgrade is additive by design: a terminal that updates keeps its recorded sales. A drop or
// recreate here would mean losing a day's trading.
check(
    'the transfers store is indexed on both ends',
    db.stores.get('transfers').indexes.has('fromStoreId') && db.stores.get('transfers').indexes.has('toStoreId'));

check(
    'the shifts store has no index on closedAt',
    !db.stores.get('shifts').indexes.has('closedAt'),
    'IndexedDB cannot index null, so an open shift would be absent from it');

// --- the terminal sequence, which is where this project once went wrong --------

const first = await store.reserveTerminalSequence(3);
const second = await store.reserveTerminalSequence(2);

checkEqual('the first reservation starts at one', first, 1);
checkEqual('a block of three is followed by the next block', second, 4);

// A gap would be read by the hub as evidence of loss, so a reservation must consume exactly what it
// claimed and nothing else.
const third = await store.reserveTerminalSequence(1);
checkEqual('reservations remain contiguous', third, 6);

// The counter has to be persisted, not in memory: a browser refresh that restarted it at one would
// hand a new sale a position already pushed to the hub, and the hub would reject the sale.
const reloaded = await loadModule(storePath);
const afterReload = await reloaded.reserveTerminalSequence(1);

checkEqual('the sequence survives a reload', afterReload, 7);

check(
    'a reservation of zero is refused',
    await store.reserveTerminalSequence(0).then(() => false, () => true));

// --- committing a sale ---------------------------------------------------------

const s1 = sale('sale-1', 'store-a', '2026-03-25', await store.reserveTerminalSequence(2), 'CT01-20260325-0001');
const m1 = movement('mov-1', 'store-a', 'product-1', s1.terminalSeq + 1);

const commit = await store.commitSale(s1, JSON.stringify(s1), [m1], [JSON.stringify(m1)], TILL);

checkEqual('the commit reports the sale', commit.saleId, 'sale-1');
checkEqual('the commit queued the sale and its movement', commit.queued, 2);
check('the commit reports an outbox position', commit.outboxSeq >= 2);

const storedSale = await store.get('sales', 'sale-1');
check('the sale is retrievable by id', storedSale !== null && storedSale.id === 'sale-1');

const forDate = await store.getSalesForDate('store-a', '2026-03-25');
checkEqual('the sale is found for its trading day', forDate.length, 1);

const otherStore = await store.getSalesForDate('store-b', '2026-03-25');
checkEqual('another store sees none of it', otherStore.length, 0);

const byNumber = await store.findSaleByNumber('CT01-20260325-0001');
check('the sale is found by the number printed on the receipt', byNumber?.id === 'sale-1');

// The movement and its payload, written in the same transaction as the sale.
const movements = await store.getStockMovements('store-a', 'product-1');
checkEqual('the stock movement was written', movements.length, 1);
check('the movement is queued with a payload', (await store.getSyncPayload('mov-1')) !== null);
check('the sale is queued with a payload', (await store.getSyncPayload('sale-1')) !== null);

// --- the outbox ------------------------------------------------------------------

const summary = await store.outboxSummary();
checkEqual('both records are pending', summary.pending, 2);

const pending = await store.peekOutbox();
checkEqual('peekOutbox returns them', pending.length, 2);
check(
    'peekOutbox returns them in enqueue order',
    pending[0].enqueuedSeq < pending[1].enqueuedSeq,
    `${pending[0].enqueuedSeq} then ${pending[1].enqueuedSeq}`);
check('the sale is queued first', pending[0].entityId === 'sale-1');

await store.markOutboxFailed('movement:mov-1', 'hub refused the movement');

const afterFailure = await store.outboxSummary();

// A single failure is retried, not thrown away: a hub that was briefly unreachable must not cost the
// shop a record. The entry stays pending with the failure recorded against it.
checkEqual('a failed entry is still pending after one attempt', afterFailure.pending, 2);
checkEqual('nothing is parked yet', afterFailure.dead, 0);

const failure = await store.get('outbox', 'movement:mov-1');
checkEqual('the failure is recorded against the entry', failure.lastError, 'hub refused the movement');
checkEqual('the attempt is counted', failure.attempts, 1);

// After repeated failures it is parked, so one poison record cannot block every later record behind
// it. This is the difference between a stuck till and a till that reports one bad record.
for (let attempt = 0; attempt < 7; attempt++) {
    await store.markOutboxFailed('movement:mov-1', 'hub refused the movement');
}

const afterParking = await store.outboxSummary();

checkEqual('an entry parked after repeated failures is not pending', afterParking.pending, 1);
checkEqual('and is counted as dead', afterParking.dead, 1);
checkEqual('the parked entry is on record', await store.count('outbox'), 2);

await store.acknowledgeOutbox(['sale:sale-1']);

const afterAck = await store.outboxSummary();

// Only the parked movement is left, so nothing is waiting to be sent.
checkEqual('an acknowledged entry is gone', afterAck.pending, 0);
checkEqual('the parked entry is still on record', afterAck.dead, 1);

check('its payload is released too', (await store.getSyncPayload('sale-1')) === null);

// Acknowledging the sale must not have taken the movement's payload with it: each entity's payload is
// keyed by its own id, and clearing the wrong one would strand a movement on the outbox forever.
check('the movement payload is untouched', (await store.getSyncPayload('mov-1')) !== null);

// Payloads hold a full serialised copy of every record ever committed. If nothing prunes them, the
// largest store in the database grows for the life of the till — and a shop that fills its quota
// loses its trading, which is the one thing the local store exists to prevent.
checkEqual(
    'the payload store does not keep acknowledged records',
    await store.count('payloads'),
    1);

// --- sale numbering ----------------------------------------------------------------

const n1 = await store.nextSaleSequence('store-a', '2026-03-25');
const n2 = await store.nextSaleSequence('store-a', '2026-03-25');
const n3 = await store.nextSaleSequence('store-b', '2026-03-25');
const n4 = await store.nextSaleSequence('store-a', '2026-03-26');

checkEqual('sale numbering starts at one', n1, 1);
checkEqual('and increments', n2, 2);
checkEqual('another store has its own sequence', n3, 1);
checkEqual('another day has its own sequence', n4, 1);

// --- the catalogue ------------------------------------------------------------------

await store.putMany('products', [
    { id: 'p-live', storeId: 'store-a', barcode: '111', name: 'Cola 500ml', sku: 'SKU1', unitPrice: 15, isActive: true },
    { id: 'p-withdrawn', storeId: 'store-a', barcode: '222', name: 'Old Cola', unitPrice: 10, isActive: false },
    { id: 'p-deleted', storeId: 'store-a', barcode: '333', name: 'Gone Cola', unitPrice: 10, isActive: true, isDeleted: true },
    { id: 'p-elsewhere', storeId: 'store-b', barcode: '444', name: 'Other Shop Cola', unitPrice: 10, isActive: true },
]);

const live = await store.getProducts('store-a');

checkEqual('the catalogue returns only this store', live.filter(p => p.id === 'p-elsewhere').length, 0);
checkEqual('a withdrawn product is hidden by default', live.filter(p => p.id === 'p-withdrawn').length, 0);

// A head-office deletion is authoritative and never comes back, not even to the management screen —
// otherwise the next catalogue pull would resurrect it.
const withInactive = await store.getProducts('store-a', true);

checkEqual('a withdrawn product is shown when asked for', withInactive.filter(p => p.id === 'p-withdrawn').length, 1);
checkEqual('a deleted product is never shown', withInactive.filter(p => p.id === 'p-deleted').length, 0);

const byBarcode = await store.findProductByBarcode('store-a', '111');
check('a product is found by its barcode', byBarcode?.id === 'p-live');

check(
    'a barcode is not found at a store that does not stock it',
    (await store.findProductByBarcode('store-b', '111')) === null,
    'the same barcode belongs to a different row in every store');

// The one that costs money. A chain sells the same tin in every branch, so the local catalogue holds
// one row per store per barcode — and an unscoped lookup returns whichever row comes first, charging
// one shop's price at another shop's till.
await store.put('products', {
    id: 'p-live-elsewhere',
    storeId: 'store-b',
    barcode: '111',
    name: 'Cola 500ml',
    unitPrice: 18,
    isActive: true,
});

const scannedAtB = await store.findProductByBarcode('store-b', '111');

checkEqual(
    'the scanning store gets its own row for a shared barcode',
    scannedAtB?.id,
    'p-live-elsewhere');

checkEqual(
    'and its own price',
    scannedAtB?.unitPrice,
    18);

const scannedAtA = await store.findProductByBarcode('store-a', '111');

checkEqual('the other store still gets its own', scannedAtA?.id, 'p-live');

const searched = await store.searchProductsByName('store-a', 'cola');

// The manual-search fallback for a label that will not scan. It runs at the till, on a basket that is
// about to be charged for, so it has to answer the same question the barcode lookup does: what may
// THIS store sell right now.
check(
    'search matches on a partial name',
    searched.some(p => p.id === 'p-live'),
    searched.map(p => p.id).join(', ') || 'nothing');

check(
    'search does not offer another store\'s product',
    !searched.some(p => p.id === 'p-elsewhere'),
    'a till would otherwise sell a product belonging to a different shop');

check(
    'search does not offer a withdrawn product',
    !searched.some(p => p.id === 'p-withdrawn'));

check(
    'search does not offer a deleted product',
    !searched.some(p => p.id === 'p-deleted'),
    'getProducts hides these; the search path has to agree with it');

// --- refunds -------------------------------------------------------------------------

const r1 = {
    id: 'return-1',
    storeId: 'store-a',
    originalSaleId: 'sale-1',
    originalSaleNumber: 'CT01-20260325-0001',
    number: 'R-CT01-20260325-0001',
    terminalSeq: await store.reserveTerminalSequence(1),
    terminalId: TILL,
    completedAt: '2026-03-25T10:00:00.0000000+00:00',
    businessDate: '2026-03-25',
    localHour: 10,
    currency: 'ZAR',
    lines: [],
    refunds: [],
    reason: 'Faulty',
    totalRefund: 100,
    taxReversed: 13,
};

await store.commitReturn(r1, JSON.stringify(r1), [], [], TILL);

const forSale = await store.getReturnsForSale('sale-1');
checkEqual('a refund is found by the sale it reverses', forSale.length, 1);

const returnsForDate = await store.getReturnsForDate('store-a', '2026-03-25');
checkEqual('and by its trading day', returnsForDate.length, 1);

checkEqual('refund numbering starts at one', await store.nextReturnSequence('store-a', '2026-03-25'), 1);
checkEqual('and increments', await store.nextReturnSequence('store-a', '2026-03-25'), 2);

// --- shifts and the drawer --------------------------------------------------------------

const shift = {
    id: 'shift-1',
    storeId: 'store-a',
    terminalId: TILL,
    employeeId: 'emp-1',
    employeeName: 'Sipho',
    openedAt: '2026-03-25T07:00:00.0000000+00:00',
    openingFloat: 500,
};

const opening = {
    id: 'drawer-open',
    shiftId: 'shift-1',
    storeId: 'store-a',
    terminalId: TILL,
    terminalSeq: await store.reserveTerminalSequence(1),
    type: 'Opening',
    amount: 500,
    employeeId: 'emp-1',
    occurredAt: '2026-03-25T07:00:00.0000000+00:00',
};

await store.openShift(shift, JSON.stringify(shift), opening, JSON.stringify(opening), TILL);

const open = await store.getOpenShift(TILL);

check('the open shift is found by terminal', open?.id === 'shift-1');
check('a shift opened elsewhere is not this terminal\'s', (await store.getOpenShift('till-9999')) === null);

const noSale = {
    id: 'drawer-nosale',
    shiftId: 'shift-1',
    storeId: 'store-a',
    terminalId: TILL,
    terminalSeq: await store.reserveTerminalSequence(1),
    type: 'NoSale',
    amount: 0,
    employeeId: 'emp-1',
    occurredAt: '2026-03-25T08:00:00.0000000+00:00',
    reason: 'Customer wanted change',
};

await store.recordDrawerEvent(noSale, JSON.stringify(noSale), TILL);

const events = await store.getDrawerEvents('shift-1');

checkEqual('drawer events are recorded against the shift', events.length, 2);
check(
    'a no-sale opening keeps its reason',
    events.some(e => e.reason === 'Customer wanted change'));

// Closing records the counted drawer as a drawer event in the same transaction. The close and its
// count are one operation: a shift marked closed without the count would lose the figure the whole
// cash-up exists to produce.
const closing = {
    id: 'drawer-close',
    shiftId: 'shift-1',
    storeId: 'store-a',
    terminalId: TILL,
    terminalSeq: await store.reserveTerminalSequence(1),
    type: 'Closing',
    amount: 600,
    employeeId: 'emp-1',
    occurredAt: '2026-03-25T17:00:00.0000000+00:00',
};

const closed = { ...shift, closedAt: '2026-03-25T17:00:00.0000000+00:00', closingCount: 600 };

await store.closeShift(closed, JSON.stringify(closed), closing, JSON.stringify(closing), TILL);

check('a closed shift is no longer open', (await store.getOpenShift(TILL)) === null);
check('but it is still on record', (await store.getShift('shift-1'))?.closingCount === 600);
checkEqual('and listed for the store', (await store.getShifts('store-a')).length, 1);

// --- transfers -----------------------------------------------------------------------

const transfer = {
    id: 'transfer-1',
    fromStoreId: 'store-a',
    toStoreId: 'store-b',
    reference: 'TR-CT01-JN01-0001',
    status: 'Dispatched',
    createdAt: '2026-03-25T15:00:00.0000000+00:00',
    createdByEmployeeId: 'emp-1',
    dispatchTerminalSeq: await store.reserveTerminalSequence(1),
    lines: [],
};

await store.commitTransfer(transfer, JSON.stringify(transfer), [], [], TILL);

// The two ends of a transfer are different stores asking different questions, which is why both
// sides are indexed.
checkEqual('the sending store sees it as outgoing', (await store.getTransfers('store-a', 'outgoing')).length, 1);
checkEqual('the sending store has nothing incoming', (await store.getTransfers('store-a', 'incoming')).length, 0);
checkEqual('the destination sees it as incoming', (await store.getTransfers('store-b', 'incoming')).length, 1);

// --- sync cursors ----------------------------------------------------------------------

await store.setCursor('sales', '42');
checkEqual('a cursor is persisted', await store.getCursor('sales'), '42');
check('an unwritten stream has no cursor', (await store.getCursor('catalog')) === null);

// --- a store's records are cloned, not shared ---------------------------------------------

// IndexedDB clones on the way out. If it did not, a caller mutating what it read would be editing the
// store, and a bug of that kind is invisible until the data is wrong on disk.
const fetched = await store.get('sales', 'sale-1');
fetched.total = 99999;

checkEqual('mutating a fetched record does not change the store', (await store.get('sales', 'sale-1')).total, 100);

// --- wipe ---------------------------------------------------------------------------------

await store.wipe();

checkEqual('the sale is gone', await store.count('sales'), 0);
checkEqual('the outbox is gone', await store.count('outbox'), 0);
checkEqual('the shifts are gone', await store.count('shifts'), 0);

console.log('');

if (failures > 0) {
    console.error(`${failures} check(s) failed.`);
    process.exit(1);
}

console.log('All local store checks passed.');
