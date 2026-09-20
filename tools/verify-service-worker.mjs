// Verifies the service worker's runtime behaviour.
//
// The offline check (`tools/verify-offline.ps1`) proves the *contract*: that everything needed to boot
// is covered by the worker's cache patterns, checked against a real publish. It reads the patterns out
// of the source and reasons about the manifest. What it cannot see is whether the worker behaves —
// install, activate, and the fetch handler, which is the code that actually decides what a shop's
// browser gets when the internet is down.
//
// That handler has a branch worth naming. A navigation is served `index.html` from the cache, *unless*
// the requested URL is itself one of the manifest's assets — because serving an HTML document in reply
// to a request for a `.wasm` file fails in a way that looks like nothing at all. Getting that wrong
// means a till that loads a blank page offline while every asset sits in the cache beside it.
//
// The worker is a classic script with no exports, so it is evaluated in a VM context with a `self`
// scope. The manifest is synthetic: this checks the worker's logic against representative data, while
// `verify-offline.ps1` checks the real published manifest against the worker's patterns. Between them
// the two halves meet.
//
// Run from the repository root:  node tools/verify-service-worker.mjs

import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import vm from 'node:vm';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const workerPath = join(root, 'src/Pos.Web/wwwroot/service-worker.published.js');

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

const ORIGIN = 'https://pos.example.com';

/** A manifest shaped like the one a publish generates. */
const manifest = {
    version: 'abc123',
    assets: [
        { url: 'index.html', hash: 'sha256-index' },
        { url: 'css/pos.css', hash: 'sha256-css' },
        { url: '_framework/blazor.webassembly.abc.js', hash: 'sha256-boot' },
        { url: '_framework/dotnet.native.abc.wasm', hash: 'sha256-wasm' },
        { url: 'icon-192.png', hash: 'sha256-icon' },
        { url: 'manifest.webmanifest', hash: 'sha256-manifest' },
        { url: '_framework/icudt.dat', hash: 'sha256-icu' },

        // An asset the patterns deliberately skip. Kept in the manifest, because a real publish lists
        // it, and the install step is what has to ignore it.
        { url: 'lib/bootstrap/bootstrap.css.map', hash: 'sha256-map' },
        { url: 'service-worker.js', hash: 'sha256-sw' },
    ],
};

/**
 * Resolves a cache key the way a service worker does.
 *
 * `cache.match('index.html')` is relative to the worker's script URL in a browser, and the fetch
 * handler relies on that: a navigation is answered by looking up the bare string `'index.html'`. A
 * stub that compared it literally against absolute keys missed every time and reported a worker that
 * never serves the shell offline — a shim bug that would have been read as a product bug.
 */
const resolve = request =>
    new URL(typeof request === 'string' ? request : request.url, `${ORIGIN}/`).href;

/**
 * A Cache stand-in that performs the fetches `addAll` implies.
 *
 * `cache.addAll` is not a bookkeeping call: it fetches every request and rejects the whole batch if any
 * one fails, which is what makes a service worker refuse to install when its assets cannot be
 * downloaded. A stub that merely recorded the requests reported a successful install with the network
 * down — a terminal that believes it works offline and does not, which is the exact failure this
 * worker exists to prevent.
 */
class FakeCache {
    constructor(name, fetcher) {
        this.name = name;
        this.fetcher = fetcher;
        this.entries = new Map();
        this.requested = [];
    }

    async addAll(requests) {
        for (const request of requests) {
            this.requested.push(request);

            const response = await this.fetcher(request);

            this.entries.set(resolve(request), response);
        }
    }

    async match(request) {
        return this.entries.get(resolve(request));
    }
}

/**
 * A request type that resolves relative URLs the way a service worker scope does.
 *
 * In a browser `new Request('index.html')` resolves against the worker's script URL, so the manifest's
 * relative asset paths become absolute. Node's `Request` has no base URL and throws on a relative
 * input, so this stands in — carrying the three fields the worker actually reads.
 */
class ScopedRequest {
    constructor(input, init = {}) {
        this.url = typeof input === 'string'
            ? new URL(input, `${ORIGIN}/`).href
            : input.url;

        this.integrity = init.integrity ?? '';
        this.cache = init.cache ?? 'default';
        this.method = init.method ?? 'GET';
        this.mode = init.mode ?? 'cors';
    }
}

/** Installs a `self` scope and evaluates the worker in it. */
async function runWorker({ existingCaches = [] } = {}) {
    const source = await readFile(workerPath, 'utf8');

    const state = {
        handlers: {},
        imported: [],
        caches: new Map(existingCaches.map(name => [name, new FakeCache(name, () => 'cached')])),
        deletedCaches: [],
        fetched: [],
        log: [],

        /**
         * Whether the network is reachable.
         *
         * Mutable, because the order matters and it is the order a shop lives through: a service
         * worker installs *while online*, caching what it will later need, and only then does the
         * connection go away. A harness that started offline could never have installed anything, and
         * would have reported the offline case as broken when the real sequence works.
         */
        offline: false,
    };

    const caches = {
        async open(name) {
            if (!state.caches.has(name)) {
                state.caches.set(name, new FakeCache(name, fetchImpl));
            }

            return state.caches.get(name);
        },

        async keys() {
            return [...state.caches.keys()];
        },

        async delete(name) {
            state.deletedCaches.push(name);
            state.caches.delete(name);

            return true;
        },
    };

    async function fetchImpl(request) {
        const url = typeof request === 'string' ? request : request.url;

        state.fetched.push(url);

        if (state.offline) {
            throw new TypeError('Failed to fetch');
        }

        return `network:${url}`;
    }

    const self = {
        origin: ORIGIN,
        assetsManifest: manifest,
        addEventListener(type, handler) {
            state.handlers[type] = handler;
        },
        importScripts(path) {
            state.imported.push(path);
        },
    };

    const context = vm.createContext({
        self,
        caches,
        fetch: fetchImpl,
        console: { info: message => state.log.push(message) },
        URL,
        Request: ScopedRequest,
        Promise,
        TypeError,
    });

    // `self` and the bare globals in the worker both have to resolve, and a classic service worker
    // scope has them pointing at the same object.
    vm.runInContext('globalThis.self = self;', context);
    vm.runInContext(source, context, { filename: 'service-worker.js' });

    /** Fires a lifecycle event and waits for its waitUntil promise. */
    async function fire(type, event = {}) {
        const pending = [];

        state.handlers[type]({ ...event, waitUntil: promise => pending.push(promise) });

        await Promise.all(pending);
    }

    /** Fires a fetch event and returns whatever the worker responded with. */
    async function fetchEvent(request) {
        let responded = null;

        state.handlers.fetch({
            request,
            respondWith: promise => { responded = promise; },
        });

        return responded === null ? null : await responded;
    }

    return { state, fire, fetchEvent };
}

console.log('Service worker:\n');

// --- install ---------------------------------------------------------------------------

let worker = await runWorker();

await worker.fire('install');

checkEqual('the worker imports its asset manifest', worker.state.imported.length, 1);
check('the import is the generated manifest', worker.state.imported[0] === './service-worker-assets.js');

const cache = worker.state.caches.get('offline-cache-abc123');

check('the cache is named after the manifest version', cache !== undefined);

const cachedUrls = [...cache.entries.keys()].sort();

check(
    'the shell is cached',
    cachedUrls.includes(`${ORIGIN}/index.html`),
    'a navigation offline has nothing to serve without it');

check(
    'the runtime and its assemblies are cached',
    cachedUrls.some(u => u.includes('blazor.webassembly')) && cachedUrls.some(u => u.endsWith('.wasm')));

check(
    'the app stylesheet and icons are cached',
    cachedUrls.some(u => u.endsWith('pos.css')) && cachedUrls.some(u => u.endsWith('icon-192.png')));

check(
    'the globalization data is cached',
    cachedUrls.some(u => u.endsWith('.dat')),
    'a till that cannot format a price is not a till');

// Source maps are the one thing a publish lists but the worker deliberately skips: a browser fetches
// them only for devtools and never needs one offline.
check(
    'source maps are left out',
    !cachedUrls.some(u => u.endsWith('.map')),
    cachedUrls.filter(u => u.endsWith('.map')).join(', '));

// The worker must not cache itself. A cached copy of the running worker is how a terminal gets
// permanently stuck on an old build.
check(
    'the worker does not cache itself',
    !cachedUrls.some(u => u.endsWith('/service-worker.js')),
    'a stale worker is a terminal that never updates');

checkEqual('every request carries the manifest integrity hash', cache.requested.filter(r => r.integrity).length, cache.requested.length);

check(
    'and bypasses the HTTP cache, so an update is really fetched',
    cache.requested.every(r => r.cache === 'no-cache'));

// --- activate --------------------------------------------------------------------------

worker = await runWorker({
    existingCaches: ['offline-cache-oldversion', 'offline-cache-abc123', 'some-other-app-cache'],
});

await worker.fire('activate');

check(
    'an older offline cache is deleted',
    worker.state.deletedCaches.includes('offline-cache-oldversion'));

check(
    'the current cache is kept',
    !worker.state.deletedCaches.includes('offline-cache-abc123'),
    'deleting the cache it just filled would empty the till on every update');

check(
    'a cache belonging to something else is left alone',
    !worker.state.deletedCaches.includes('some-other-app-cache'),
    'another app on the same origin is not this worker\'s to clear');

// --- fetch: a navigation is served the shell --------------------------------------------

worker = await runWorker();

await worker.fire('install');

// Reset, because install fetches every asset it caches. What this section is about is whether serving
// a navigation costs a network round trip once the cache is warm.
worker.state.fetched.length = 0;

const shell = await worker.fetchEvent({
    method: 'GET',
    mode: 'navigate',
    url: `${ORIGIN}/`,
});

check('a navigation is answered', shell !== null);
check(
    'from the cached shell',
    String(shell).includes('index.html'),
    String(shell));
checkEqual('and the network is not consulted', worker.state.fetched.length, 0);

// --- fetch: an asset comes from its own cache entry, never the shell ---------------------

worker = await runWorker();

await worker.fire('install');

const asset = await worker.fetchEvent({
    method: 'GET',
    mode: 'cors',
    url: `${ORIGIN}/css/pos.css`,
});

check('a cached asset is answered from the cache', String(asset).includes('pos.css'), String(asset));

check(
    'and not with the shell document',
    !String(asset).includes('index.html'),
    'an HTML document in reply to a .css request is a blank till that looks like nothing at all');

// A navigation to a URL that is itself an asset. The worker must not answer it with index.html.
const navigationToAsset = await worker.fetchEvent({
    method: 'GET',
    mode: 'navigate',
    url: `${ORIGIN}/manifest.webmanifest`,
});

check(
    'a navigation to an asset URL is not answered with the shell',
    navigationToAsset === null || !String(navigationToAsset).includes('index.html'),
    String(navigationToAsset));

// --- fetch: anything else goes to the network --------------------------------------------

worker = await runWorker();

await worker.fire('install');

// Measured from here, because install itself fetches every asset it caches.
worker.state.fetched.length = 0;

const uncached = await worker.fetchEvent({
    method: 'GET',
    mode: 'cors',
    url: `${ORIGIN}/api/sync/pull`,
});

checkEqual('an uncached GET falls through to the network', uncached, `network:${ORIGIN}/api/sync/pull`);
checkEqual('and the network was consulted once', worker.state.fetched.length, 1);

const posted = await worker.fetchEvent({
    method: 'POST',
    mode: 'cors',
    url: `${ORIGIN}/api/sync/push`,
});

checkEqual('a POST goes to the network', posted, `network:${ORIGIN}/api/sync/push`);

// --- offline --------------------------------------------------------------------------

// Installed while online, as a real terminal is, and only then does the connection go away.
worker = await runWorker();

await worker.fire('install');

worker.state.offline = true;

const offlineShell = await worker.fetchEvent({
    method: 'GET',
    mode: 'navigate',
    url: `${ORIGIN}/checkout`,
});

check(
    'the shell is still served with the network down',
    String(offlineShell).includes('index.html'),
    'this is the whole point of the offline cache');

const offlineAsset = await worker.fetchEvent({
    method: 'GET',
    mode: 'cors',
    url: `${ORIGIN}/css/pos.css`,
});

check('a cached asset is still served with the network down', String(offlineAsset).includes('pos.css'));

// An uncached request with no network must fail rather than hand back something wrong. A silent
// substitute here would be worse than a visible failure.
let refused = false;

try {
    await worker.fetchEvent({
        method: 'GET',
        mode: 'cors',
        url: `${ORIGIN}/api/sync/pull`,
    });
} catch {
    refused = true;
}

check('an uncached request with no network fails rather than inventing a response', refused);

// --- install with no network at all ----------------------------------------------------

worker = await runWorker();

worker.state.offline = true;

let installFailed = false;

try {
    await worker.fire('install');
} catch {
    installFailed = true;
}

// The worker has no control over this: if the assets cannot be fetched it cannot cache them, and
// pretending otherwise would leave a terminal that believes it works offline and does not.
check('an install that cannot fetch its assets fails', installFailed);

console.log('');

if (failures > 0) {
    console.error(`${failures} check(s) failed.`);
    process.exit(1);
}

console.log('All service worker checks passed.');
