// A minimal in-memory IndexedDB, faithful enough to execute the POS's real data layer.
//
// local-store.js is the production store: 43 KB, 48 exported functions, and every sale, refund,
// shift, drawer event, and transfer the terminal records. Until now nothing had ever executed it.
// The C# side talks to it through JsLocalStore, but every test substitutes InMemoryLocalStore, so
// the JavaScript was verified only by reading it and by a hand-written mirror agreeing with a
// contract somebody wrote by hand. The one serious data-integrity bug this project has had — two
// sequence counters handing a sale and a drawer event the same position — lived in this file.
//
// A package would be the obvious route, but this repository has no package.json and every
// verification script here runs on Node built-ins alone. Keeping that property is worth more than
// the last few percent of fidelity, so this models the parts the store actually uses:
//
//   * object stores keyed by a keyPath, with secondary indexes;
//   * transactions that buffer their writes and apply them only on commit, so a transaction that
//     aborts leaves nothing behind — which is exactly the guarantee commitSale depends on;
//   * read-your-writes inside a transaction, through both stores and indexes;
//   * structured cloning on the way in and out, so a caller mutating a returned record cannot
//     reach into the store and hide a bug.
//
// What it does NOT model: durability across a process restart, versionchange blocking between real
// tabs, and cursor iteration beyond the one forward pass peekOutbox performs. Those are stated here
// rather than pretended away.

const DELETED = Symbol('deleted');

class FakeRequest {
    constructor() {
        this.result = undefined;
        this.error = null;
        this.onsuccess = null;
        this.onerror = null;
        this.onupgradeneeded = null;
    }

    /** Fires the success handler once the caller has had a chance to attach one. */
    resolve(result) {
        queueMicrotask(() => {
            this.result = result;

            if (this.onsuccess) {
                this.onsuccess({ target: this });
            }
        });
    }

    fail(error) {
        queueMicrotask(() => {
            this.error = error;

            if (this.onerror) {
                this.onerror({ target: this });
            }
        });
    }
}

const clone = value => (value === undefined ? undefined : structuredClone(value));

function keyOf(keyPath, value) {
    return keyPath.split('.').reduce((acc, part) => (acc == null ? acc : acc[part]), value);
}

/** Every record of a store with a transaction's overlay applied, for index reads. */
function visibleRecords(store, pending) {
    const keys = new Set([...store.records.keys(), ...(pending ? pending.keys() : [])]);
    const values = [];

    for (const key of keys) {
        const value = pending && pending.has(key) ? pending.get(key) : store.records.get(key);

        if (value !== DELETED && value !== undefined) {
            values.push(clone(value));
        }
    }

    return values;
}

class FakeIndex {
    constructor(store, name, keyPath) {
        this.store = store;
        this.name = name;
        this.keyPath = keyPath;
    }

    /**
     * The matching records, read through a transaction's overlay.
     *
     * The overlay is a parameter rather than state on the index, because an index is shared by every
     * transaction open on its store and must not carry one transaction's uncommitted writes into
     * another's reads.
     */
    matching(overlay, key, hasKey) {
        return visibleRecords(this.store, overlay)
            .filter(value => !hasKey || keyOf(this.keyPath, value) === key);
    }

    /** The first match for a key, as the barcode lookup uses. */
    get(overlay, key) {
        const request = new FakeRequest();

        request.resolve(this.matching(overlay, key, true)[0]);

        return request;
    }

    /**
     * A forward pass over the matching records.
     *
     * Only what peekOutbox uses: read `cursor.value`, then either stop or continue. There is no
     * reverse direction, no key cursor, and no update or delete through the cursor.
     */
    openCursor(overlay, key) {
        const request = new FakeRequest();
        const rows = this.matching(overlay, key, key !== undefined);

        let position = 0;

        const step = () => {
            if (position >= rows.length) {
                request.result = null;

                if (request.onsuccess) {
                    request.onsuccess({ target: request });
                }

                return;
            }

            request.result = { value: rows[position], continue: () => queueMicrotask(step) };
            position += 1;

            if (request.onsuccess) {
                request.onsuccess({ target: request });
            }
        };

        queueMicrotask(step);

        return request;
    }
}

class FakeObjectStore {
    constructor(name, keyPath) {
        this.name = name;
        this.keyPath = keyPath;
        this.records = new Map();
        this.indexes = new Map();
    }

    get indexNames() {
        return { contains: name => this.indexes.has(name) };
    }

    createIndex(name, keyPath) {
        this.indexes.set(name, new FakeIndex(this, name, keyPath));
    }

    index(name) {
        const index = this.indexes.get(name);

        if (!index) {
            throw new Error(`No index named '${name}' on '${this.name}'.`);
        }

        return index;
    }
}

/**
 * A transaction.
 *
 * Writes are buffered and applied only on commit, which is what makes an abort leave nothing
 * behind — the guarantee `commitSale` is built on. Reads see the buffer, so a transaction can read
 * back its own writes, as IndexedDB allows.
 */
class FakeTransaction {
    constructor(db, storeNames, mode) {
        this.db = db;
        this.mode = mode;
        this.error = null;
        this.oncomplete = null;
        this.onabort = null;
        this.onerror = null;

        this.pending = new Map();
        this.finished = false;
        this.commitScheduled = false;

        for (const name of storeNames) {
            if (!db.stores.has(name)) {
                throw new Error(`No object store named '${name}'.`);
            }

            this.pending.set(name, new Map());
        }

        // Scheduled up front, so a transaction that ends up issuing no writes still completes rather
        // than leaving its caller waiting on a promise that can never settle. In a browser an empty
        // transaction commits immediately; a `wipe()` that found nothing to clear is the case that
        // exposed this.
        if (mode !== 'readonly') {
            this.schedule();
        }
    }

    /** The overlay for a store, created on demand so an upgrade can reach any store. */
    overlay(name) {
        if (!this.pending.has(name)) {
            this.pending.set(name, new Map());
        }

        return this.pending.get(name);
    }

    objectStore(name) {
        const store = this.db.stores.get(name);

        if (!store) {
            throw new Error(`No object store named '${name}'.`);
        }

        const readonly = this.mode === 'readonly';
        const transaction = this;

        /**
         * Refuses a write to a transaction that has already finished.
         *
         * Real IndexedDB throws `TransactionInactiveError` here. Swallowing it would let a request
         * issued too late vanish silently, which is the failure this whole shim exists to expose.
         */
        const guard = () => {
            if (transaction.finished) {
                throw new Error(
                    `A write was issued against the '${name}' transaction after it had finished. ` +
                    'In a browser this is a TransactionInactiveError and the write is lost.');
            }
        };

        const read = key => {
            const pending = this.overlay(name);

            return pending.has(key) ? pending.get(key) : store.records.get(key);
        };

        return {
            name,

            get: key => {
                const request = new FakeRequest();
                const found = read(key);

                request.resolve(found === DELETED ? undefined : clone(found));

                return request;
            },

            getAll: () => {
                const request = new FakeRequest();

                request.resolve(visibleRecords(store, this.overlay(name)));

                return request;
            },

            count: () => {
                const request = new FakeRequest();

                request.resolve(visibleRecords(store, this.overlay(name)).length);

                return request;
            },

            index: indexName => {
                const index = store.index(indexName);
                const transaction = this;

                // Reads through an index must see this transaction's own writes, so its overlay is
                // handed to the index rather than the index reading the store directly.
                return {
                    get: key => index.get(transaction.overlay(name), key),

                    getAll: key => {
                        const request = new FakeRequest();

                        request.resolve(index.matching(
                            transaction.overlay(name), key, key !== undefined));

                        return request;
                    },

                    openCursor: key => index.openCursor(transaction.overlay(name), key),
                };
            },

            put: value => {
                guard();

                if (readonly) {
                    throw new Error('Cannot write in a readonly transaction.');
                }

                const key = keyOf(store.keyPath, value);

                if (key === undefined) {
                    throw new Error(`Record has no '${store.keyPath}'.`);
                }

                this.overlay(name).set(key, clone(value));
                this.schedule();

                const request = new FakeRequest();
                request.resolve(key);

                return request;
            },

            delete: key => {
                guard();

                if (readonly) {
                    throw new Error('Cannot write in a readonly transaction.');
                }

                this.overlay(name).set(key, DELETED);
                this.schedule();

                return new FakeRequest();
            },

            clear: () => {
                guard();

                if (readonly) {
                    throw new Error('Cannot write in a readonly transaction.');
                }

                const pending = this.overlay(name);

                for (const key of store.records.keys()) {
                    pending.set(key, DELETED);
                }

                this.schedule();

                return new FakeRequest();
            },
        };
    }

    /**
     * Commits once the caller stops issuing operations.
     *
     * On a macrotask rather than a microtask, and that distinction is the whole reason this is not
     * simply deferred by one tick. Real IndexedDB keeps a transaction alive across `await`s inside it:
     * the store reads a counter, awaits it, and then writes — several times over in `commitSale`. A
     * commit scheduled as a microtask fires *between* those awaits, so the later writes land on a
     * finished transaction and the promise the caller is waiting on never settles. This hung on
     * `openShift` the first time it was run, which is exactly the shape of bug the real store would
     * have if it ever issued a request after the transaction had auto-committed.
     */
    schedule() {
        if (this.commitScheduled || this.finished) {
            return;
        }

        this.commitScheduled = true;

        setTimeout(() => {
            this.commitScheduled = false;

            if (!this.finished) {
                this.commit();
            }
        }, 0);
    }

    commit() {
        if (this.finished) {
            return;
        }

        this.finished = true;

        for (const [name, pending] of this.pending) {
            const store = this.db.stores.get(name);

            if (!store) {
                continue;
            }

            for (const [key, value] of pending) {
                if (value === DELETED) {
                    store.records.delete(key);
                } else {
                    store.records.set(key, value);
                }
            }
        }

        queueMicrotask(() => this.oncomplete && this.oncomplete());
    }

    abort() {
        if (this.finished) {
            return;
        }

        this.finished = true;
        this.error = new Error('Transaction aborted.');

        // The overlay is simply dropped, so nothing from this transaction survives.
        queueMicrotask(() => this.onabort && this.onabort());
    }
}

class FakeDatabase {
    constructor(name, version) {
        this.name = name;
        this.version = version;
        this.stores = new Map();
    }

    /**
     * The store names, array-like and with `contains`.
     *
     * A browser's `objectStoreNames` is a `DOMStringList`: iterable, indexable, and carrying
     * `contains`. `wipe()` does `Array.from(db.objectStoreNames)`, which against a bare
     * `{ contains }` object yields an empty array — so nothing was cleared and the transaction it then
     * awaited had no work to commit, and never completed. It hung rather than failing, which is why
     * fidelity here is worth the extra few lines.
     */
    get objectStoreNames() {
        const names = [...this.stores.keys()];

        names.contains = name => this.stores.has(name);

        return names;
    }

    createObjectStore(name, options = {}) {
        const store = new FakeObjectStore(name, options.keyPath);

        this.stores.set(name, store);

        return store;
    }

    transaction(storeNames, mode = 'readonly') {
        const names = Array.isArray(storeNames) ? storeNames : [storeNames];

        return new FakeTransaction(this, names, mode);
    }

    close() { /* Nothing is held open. */ }
}

/**
 * Installs the fake on globals and returns the databases it has opened.
 *
 * The store calls `indexedDB.open` once and caches the promise, so one database per name is what a
 * page load looks like. A test simulating a reload loads the module afresh and finds the same
 * database, which is what makes the persisted-counter assertions meaningful.
 */
export function installFakeIndexedDb() {
    const databases = new Map();

    globalThis.indexedDB = {
        open(name, version) {
            const request = new FakeRequest();

            let db = databases.get(name);
            const isNew = !db;

            if (isNew) {
                db = new FakeDatabase(name, version);
                databases.set(name, db);
            }

            // Deferred by a microtask, because the caller attaches `onupgradeneeded` after `open()`
            // returns — so the handler is only in place once the current synchronous block ends.
            // The upgrade must run before `onsuccess`, as the browser does it.
            queueMicrotask(() => {
                if (isNew && request.onupgradeneeded) {
                    request.onupgradeneeded({
                        target: { result: db, transaction: new FakeTransaction(db, [], 'readwrite') },
                    });
                }

                request.resolve(db);
            });

            return request;
        },
    };

    // Defined rather than assigned: Node 24 ships a read-only `navigator` of its own, and a plain
    // assignment to it throws.
    Object.defineProperty(globalThis, 'navigator', {
        configurable: true,
        writable: true,
        value: {
            storage: {
                persisted: async () => true,
                persist: async () => true,
                estimate: async () => ({ usage: 1024, quota: 1024 * 1024 }),
            },
        },
    });

    return databases;
}

