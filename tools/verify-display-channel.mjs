// Verifies the customer display channel module.
//
// customer-display.js is the wire between the till window and the display window, and until now it
// had never been executed by anything: no test imported it, and the only browser check ever done on
// the app was of the till screen. Its logic is small but every part of it matters — it decides
// whether the display updates at all, and it is the piece that must not throw when the browser has
// no BroadcastChannel.
//
// Node is enough to run it. The module touches the DOM nowhere and only reaches for the global
// BroadcastChannel inside its functions, so a stub that records what was posted exercises the whole
// contract: what the till sends, what the display recognises, and what happens when there is no
// channel at all.
//
// Run from the repository root:  node tools/verify-display-channel.mjs

import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const modulePath = join(root, 'src/Pos.Web/wwwroot/js/customer-display.js');

let step = 0;
let failures = 0;

function check(label, condition, detail = '') {
    step++;
    const mark = condition ? 'ok  ' : 'FAIL';
    console.log(`${String(step).padStart(2)}. ${mark} ${label}${detail ? ` -- ${detail}` : ''}`);

    if (!condition) {
        failures++;
    }
}

/** A BroadcastChannel stand-in that keeps what was posted and lets a test deliver messages. */
class FakeChannel {
    constructor(name) {
        this.name = name;
        this.posted = [];
        this.listeners = [];
        FakeChannel.instances.push(this);
    }

    postMessage(data) {
        this.posted.push(data);
    }

    addEventListener(type, handler) {
        if (type === 'message') {
            this.listeners.push(handler);
        }
    }

    removeEventListener(type, handler) {
        this.listeners = this.listeners.filter(h => h !== handler);
    }

    /** Delivers a message to whatever is subscribed, as the browser would. */
    deliver(data) {
        for (const listener of this.listeners) {
            listener({ data });
        }
    }
}

FakeChannel.instances = [];

globalThis.BroadcastChannel = FakeChannel;

// Imported through a data URL so the module is treated as ESM regardless of the package.json above
// it, which keeps this script from having to add one to the web root.
const source = await readFile(modulePath, 'utf8');
const display = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

console.log('Customer display channel:\n');

// --- the capability probe -----------------------------------------------------

check('isSupported is true when BroadcastChannel exists', display.isSupported() === true);

// --- publishing a basket ------------------------------------------------------

const basket = JSON.stringify({ lines: [], total: 15, itemCount: 0 });

check('publish reports success', display.publish(basket) === true);

const channel = FakeChannel.instances.at(-1);

check('publish creates one channel', FakeChannel.instances.length === 1);
check('publish posts the payload unchanged', channel.posted.at(-1) === basket);

// A second publish must reuse the channel rather than opening another, or the display would end up
// listening on a channel the till has stopped writing to.
display.publish(JSON.stringify({ total: 30 }));
check('publish reuses the channel', FakeChannel.instances.length === 1);
check('publish posts again', channel.posted.length === 2);

// --- the heartbeat ------------------------------------------------------------

check('sendHeartbeat reports success', display.sendHeartbeat('2026-03-25T14:30:00.0000000+00:00') === true);

const beat = JSON.parse(channel.posted.at(-1));

// The display recognises a heartbeat by this property and would otherwise try to read it as a
// basket with no lines. It is the reason the till can send one without confusing the screen.
check('the heartbeat is wrapped in the shape the display looks for', 'heartbeat' in beat, channel.posted.at(-1));
check('the heartbeat carries the timestamp', beat.heartbeat === '2026-03-25T14:30:00.0000000+00:00');

// --- subscribing, as the display window does ----------------------------------

const received = [];
const fakeRef = { invokeMethodAsync: (method, payload) => received.push({ method, payload }) };

check('subscribe reports success', display.subscribe(fakeRef) === true);

channel.deliver(basket);

check('a delivered message reaches .NET', received.length === 1);
check('it arrives on OnDisplayUpdate', received[0]?.method === 'OnDisplayUpdate');

// Passed through as the raw string, so the C# side deserialises with the same options it serialised
// with rather than depending on JavaScript's JSON shape.
check('the payload is the raw JSON string', received[0]?.payload === basket);

channel.deliver(JSON.stringify({ heartbeat: 'later' }));
check('heartbeats reach .NET too', received.length === 2);

// --- unsubscribing ------------------------------------------------------------

display.unsubscribe();
channel.deliver(basket);

check('nothing arrives after unsubscribe', received.length === 2);

// --- a browser with no channel at all -----------------------------------------

// Locked-down profiles and some embedded browsers have no BroadcastChannel. The till must keep
// selling, so every entry point has to answer rather than throw.
delete globalThis.BroadcastChannel;

FakeChannel.instances.length = 0;

const fresh = await import(
    `data:text/javascript;base64,${Buffer.from(source).toString('base64')}#nocache`
);

check('isSupported is false without the API', fresh.isSupported() === false);
check('publish answers false rather than throwing', fresh.publish(basket) === false);
check('sendHeartbeat answers false rather than throwing', fresh.sendHeartbeat('now') === false);
check('subscribe answers false rather than throwing', fresh.subscribe(fakeRef) === false);
check('no channel was opened', FakeChannel.instances.length === 0);

console.log('');

if (failures > 0) {
    console.error(`${failures} check(s) failed.`);
    process.exit(1);
}

console.log('All customer display checks passed.');
