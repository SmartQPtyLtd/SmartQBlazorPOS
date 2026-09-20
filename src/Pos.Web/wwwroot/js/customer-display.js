// customer-display.js — the second-screen channel.
//
// A customer-facing display is a second browser window showing the basket as it is scanned.
// BroadcastChannel carries the state between the till and the display window: it is
// same-origin, works in every modern browser, and needs no device API and no server round
// trip, so it keeps working when the shop's internet is down.
//
// The display is strictly an output. It never sends anything back, so a display that crashes
// or is closed cannot affect a sale.

const CHANNEL_NAME = 'pos-customer-display';

let channel = null;
let handler = null;

function ensureChannel() {
    if (channel || typeof BroadcastChannel === 'undefined') {
        return channel;
    }

    channel = new BroadcastChannel(CHANNEL_NAME);
    return channel;
}

export function isSupported() {
    return typeof BroadcastChannel !== 'undefined';
}

/**
 * Publishes the current basket to any listening display.
 * @param {string} payloadJson Serialised basket state.
 */
export function publish(payloadJson) {
    const bus = ensureChannel();
    if (!bus) {
        return false;
    }

    bus.postMessage(payloadJson);
    return true;
}

/**
 * Registers the callback that receives basket updates.
 *
 * The handler is passed the raw JSON string so the C# side can deserialise it with the same
 * options it serialised with, rather than relying on JavaScript's JSON shape.
 */
export function subscribe(dotNetReference) {
    const bus = ensureChannel();
    if (!bus) {
        return false;
    }

    handler = event => {
        try {
            dotNetReference.invokeMethodAsync('OnDisplayUpdate', String(event.data));
        } catch {
            // The till may have navigated away while the display stayed open. Nothing to do:
            // the display simply stops updating.
        }
    };

    bus.addEventListener('message', handler);
    return true;
}

export function unsubscribe() {
    if (channel && handler) {
        channel.removeEventListener('message', handler);
        handler = null;
    }
}

/**
 * Tells the display window it is still connected, so it can show a live indicator rather than
 * silently showing a stale basket.
 */
export function sendHeartbeat(timestampIso) {
    const bus = ensureChannel();
    if (!bus) {
        return false;
    }

    bus.postMessage(JSON.stringify({ heartbeat: timestampIso }));
    return true;
}
