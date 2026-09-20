// Verifies the browser device bridge.
//
// device-bridge.js is the only thing in this system that talks to real hardware, and it holds the
// most intricate logic here: the deferred-promise handshake that exists because
// navigator.usb.requestDevice() only works while a user gesture is still live. A Blazor async handler
// that awaits anything before calling it has already lost the gesture, so the bridge calls it
// synchronously, parks the promise against a request id, and lets C# poll for the outcome.
//
// That design has been described in comments and in PLAN.md since the beginning and had never been
// executed. The C# side is tested against FakeDeviceBridge, which is the same mirror problem as the
// local store: the fake is written to agree with the real one, so an omission shared by both is
// invisible.
//
// This shims navigator.usb, navigator.serial and just enough DOM to print, then runs the real module.
// No package, no browser, Node built-ins only.
//
// Run from the repository root:  node tools/verify-device-bridge.mjs

import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { loadModule } from './module-loader.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const bridgePath = join(root, 'src/Pos.Web/wwwroot/js/device-bridge.js');

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

/** A stand-in USB device, with the interface layout an ESC/POS printer exposes. */
function fakeUsbDevice({
    productName = 'Epson TM-T20III',
    vendorId = 0x04b8,
    productId = 0x0e15,
    printerInterface = 1,
    claimFails = [],
} = {}) {
    const device = {
        // The descriptors the bridge reads to label a device for the operator. These are plain
        // properties on the device in the real API, not constructor arguments of anything.
        productName,
        vendorId,
        productId,

        opened: false,
        configuration: null,
        openedCount: 0,
        claimed: [],
        released: [],
        closedCount: 0,
        writes: [],

        async open() {
            device.opened = true;
            device.openedCount += 1;
        },

        async selectConfiguration() {
            device.configuration = {
                interfaces: [
                    // Interface 0: a vendor-specific interface with no bulk OUT, which the bridge
                    // must skip rather than claim.
                    {
                        interfaceNumber: 0,
                        alternates: [{ endpoints: [{ direction: 'in', type: 'bulk', endpointNumber: 1 }] }],
                    },
                    {
                        interfaceNumber: printerInterface,
                        alternates: [{
                            endpoints: [
                                { direction: 'out', type: 'bulk', endpointNumber: 3 },
                                { direction: 'in', type: 'bulk', endpointNumber: 4 },
                            ],
                        }],
                    },
                ],
            };
        },

        async claimInterface(number) {
            if (claimFails.includes(number)) {
                throw new Error('The interface is claimed by a system driver.');
            }

            device.claimed.push(number);
        },

        async releaseInterface(number) {
            device.released.push(number);
        },

        async close() {
            device.opened = false;
            device.closedCount += 1;
        },

        async transferOut(endpointNumber, chunk) {
            if (device.failTransfer) {
                return { status: 'stall', bytesWritten: 0 };
            }

            device.writes.push({ endpointNumber, bytes: Uint8Array.from(chunk) });

            return { status: 'ok', bytesWritten: chunk.length };
        },
    };

    return device;
}

/** Installs browser globals; returns handles so a test can drive the shims. */
function installBrowser({ usb = true, serial = false, granted = [], requestBehaviour } = {}) {
    const state = {
        requestedFilters: [],
        requestDeviceCalls: 0,
        requestedPortCalls: 0,
        printed: [],
    };

    const navigatorShim = {};

    if (usb) {
        navigatorShim.usb = {
            requestDevice(options) {
                state.requestDeviceCalls += 1;
                state.requestedFilters.push(options?.filters);

                return requestBehaviour();
            },
            async getDevices() {
                return granted;
            },
        };
    }

    if (serial) {
        navigatorShim.serial = {
            requestPort() {
                state.requestedPortCalls += 1;

                return Promise.reject(Object.assign(new Error('no port'), { name: 'NotFoundError' }));
            },
            async getPorts() {
                return [];
            },
        };
    }

    Object.defineProperty(globalThis, 'navigator', {
        configurable: true,
        writable: true,
        value: navigatorShim,
    });

    // `crypto.randomUUID` exists in Node, but the bridge calls it as a bare global.
    if (!globalThis.crypto) {
        globalThis.crypto = {};
    }

    if (!globalThis.crypto.randomUUID) {
        let counter = 0;
        globalThis.crypto.randomUUID = () => `0000-${++counter}`;
    }

    globalThis.window = { isSecureContext: true, crossOriginIsolated: false, print() { } };

    return state;
}

console.log('Device bridge:\n');

// --- support probes ------------------------------------------------------------------

let browser = installBrowser({ usb: false, serial: false });
let bridge = await loadModule(bridgePath);

check('WebUSB is reported unsupported when absent', bridge.isWebUsbSupported() === false);
check('Web Serial is reported unsupported when absent', bridge.isWebSerialSupported() === false);

const report = bridge.capabilityReport();

check('the capability report says so', report.webusb === false && report.webserial === false);
check('and reports the secure-context flag', report.secureContext === true);
check('and reports HID and Bluetooth as absent', report.webhid === false && report.bluetooth === false);

// --- the deferred chooser, and the user gesture it exists for -------------------------

let settle;

browser = installBrowser({
    usb: true,
    requestBehaviour: () => new Promise((resolve, reject) => {
        settle = { resolve, reject };
    }),
});

bridge = await loadModule(bridgePath);

// The constraint the whole design exists for. A live user gesture lasts only for the synchronous
// part of an event handler, so requestDevice() has to be in that window — an await before it and the
// browser rejects the request, and no printer can ever be paired on a real till.
bridge.beginWebUsbRequest('req-1', 0x04b8);

checkEqual(
    'requestDevice is called synchronously, inside the gesture',
    browser.requestDeviceCalls,
    1);

checkEqual(
    'the chooser is filtered to the printer class',
    browser.requestedFilters[0][0].classCode,
    7);

check(
    'and optionally to a vendor',
    browser.requestedFilters[0].some(f => f.vendorId === 0x04b8),
    JSON.stringify(browser.requestedFilters[0]));

checkEqual('the request is pending until the chooser answers', bridge.pollRequest('req-1').state, 'pending');

// A chooser the operator is still looking at must keep reporting pending, or C# would give up while
// the dialog is open.
checkEqual('polling again is still pending', bridge.pollRequest('req-1').state, 'pending');

const chosen = fakeUsbDevice();

settle.resolve(chosen);

// Let the promise chain settle.
await new Promise(resolve => setTimeout(resolve, 0));

const succeeded = bridge.pollRequest('req-1');

checkEqual('choosing a device succeeds', succeeded.state, 'succeeded');
check('a connection id comes back', typeof succeeded.connectionId === 'string' && succeeded.connectionId.length > 0);
check(
    'the label names the device and its ids',
    succeeded.label === 'Epson TM-T20III (04b8:0e15)',
    succeeded.label);

// Terminal states are consumed once read, so the map cannot grow for the life of the till.
checkEqual('a consumed outcome is gone', bridge.pollRequest('req-1').state, 'unknown');

// --- a dismissed chooser is a cancellation, not an error -------------------------------

browser = installBrowser({
    usb: true,
    requestBehaviour: () => Promise.reject(
        Object.assign(new Error('No device selected.'), { name: 'NotFoundError' })),
});

bridge = await loadModule(bridgePath);
bridge.beginWebUsbRequest('req-2');

await new Promise(resolve => setTimeout(resolve, 0));

const cancelled = bridge.pollRequest('req-2');

checkEqual('a dismissed chooser is reported as cancelled', cancelled.state, 'cancelled');
check(
    'and carries no error for the operator to read',
    !cancelled.error || cancelled.error.length === 0 || cancelled.state === 'cancelled');

browser = installBrowser({
    usb: true,
    requestBehaviour: () => Promise.reject(new Error('Access denied by policy.')),
});

bridge = await loadModule(bridgePath);
bridge.beginWebUsbRequest('req-3');

await new Promise(resolve => setTimeout(resolve, 0));

const failed = bridge.pollRequest('req-3');

checkEqual('a real failure is reported as failed', failed.state, 'failed');
check('with the reason', failed.error === 'Access denied by policy.', failed.error);

// Asking when the browser cannot do it at all must answer rather than throw.
browser = installBrowser({ usb: false });
bridge = await loadModule(bridgePath);
bridge.beginWebUsbRequest('req-4');

const unsupported = bridge.pollRequest('req-4');

checkEqual('asking without WebUSB fails cleanly', unsupported.state, 'failed');
check('and says why', /not supported/i.test(unsupported.error ?? ''), unsupported.error);

// --- reattaching without a prompt -------------------------------------------------------

const printer = fakeUsbDevice();

browser = installBrowser({ usb: true, granted: [printer] });
bridge = await loadModule(bridgePath);

const first = await bridge.getAuthorisedConnections();
const second = await bridge.getAuthorisedConnections();

checkEqual('a granted device is reattached without prompting', first.length, 1);
check(
    'and reuses the same handle on the next call',
    second[0] === first[0],
    'a duplicate handle would open the same device twice');

// --- opening: pick the interface that can actually carry printer data -------------------

await bridge.openWebUsb(first[0]);

checkEqual('the printer interface is claimed', printer.claimed.length, 1);
checkEqual(
    'and it is the one with a bulk OUT endpoint, not the vendor interface',
    printer.claimed[0],
    1);
check('the device was opened once', printer.openedCount === 1);

// --- writing: chunked, and a failed transfer is not swallowed ---------------------------

const payload = new Uint8Array(40000);

for (let i = 0; i < payload.length; i++) {
    payload[i] = i % 251;
}

await bridge.writeWebUsb(first[0], payload);

checkEqual('a payload larger than one packet is chunked', printer.writes.length, 3);
checkEqual('the first chunk is a full packet', printer.writes[0].bytes.length, 16384);

check(
    'every chunk is within the packet limit',
    printer.writes.every(w => w.bytes.length <= 16384));

// Chunking splits the payload; it must not reorder, drop, or duplicate a byte. A receipt is a
// command stream, so a byte out of place is a page of garbage or a truncated cut.
const reassembled = Buffer.concat(printer.writes.map(w => Buffer.from(w.bytes)));

check(
    'the chunks reassemble to exactly what was sent',
    reassembled.length === payload.length && reassembled.every((b, i) => b === payload[i]),
    `${reassembled.length} bytes back from ${payload.length}`);

checkEqual('every chunk goes to the printer endpoint', printer.writes[0].endpointNumber, 3);

printer.failTransfer = true;

let rejected = false;

try {
    await bridge.writeWebUsb(first[0], new Uint8Array([1, 2, 3]));
} catch {
    rejected = true;
}

check('a failed USB transfer throws rather than truncating silently', rejected);

// --- opening when the operating system holds the interface ------------------------------

const held = fakeUsbDevice({ claimFails: [1] });

browser = installBrowser({ usb: true, granted: [held] });
bridge = await loadModule(bridgePath);

const heldId = (await bridge.getAuthorisedConnections())[0];

let claimMessage = '';

try {
    await bridge.openWebUsb(heldId);
} catch (error) {
    claimMessage = String(error.message);
}

check(
    'an interface held by a system driver is refused with advice',
    /could not claim/i.test(claimMessage) && /serial/i.test(claimMessage),
    claimMessage);

// --- closing, and the failure it tolerates ----------------------------------------------

browser = installBrowser({ usb: true, granted: [printer] });
bridge = await loadModule(bridgePath);

const closeId = (await bridge.getAuthorisedConnections())[0];

await bridge.openWebUsb(closeId);
await bridge.closeWebUsb(closeId);

check('the interface is released on close', printer.released.includes(1));
check('and the device is closed', printer.closedCount >= 1);
checkEqual('an unknown connection describes as nothing', await bridge.describeConnection(closeId), null);

// A device unplugged between opening and closing must not turn into an error the operator sees.
browser = installBrowser({ usb: true, granted: [printer] });
bridge = await loadModule(bridgePath);

const unplugId = (await bridge.getAuthorisedConnections())[0];

await bridge.openWebUsb(unplugId);
printer.close = async () => { throw new Error('The device has been lost.'); };
printer.releaseInterface = async () => { throw new Error('The device has been lost.'); };

let closedCleanly = true;

try {
    await bridge.closeWebUsb(unplugId);
} catch {
    closedCleanly = false;
}

check('closing a device that has gone away is not an error', closedCleanly);

// --- Web Serial --------------------------------------------------------------------------

browser = installBrowser({ usb: false, serial: true });
bridge = await loadModule(bridgePath);

check('Web Serial is detected', bridge.isWebSerialSupported() === true);

bridge.beginWebSerialRequest('serial-1');

checkEqual(
    'the serial chooser is also called synchronously',
    browser.requestedPortCalls,
    1);

await new Promise(resolve => setTimeout(resolve, 0));

// Polled through `pollSerialRequest`, not `pollRequest`. The two choosers keep separate maps, and a
// bridge that conflated them would report a USB outcome for a serial pairing — which is the kind of
// mistake that only shows up as "the printer never appears" on a shop's counter.
checkEqual(
    'a dismissed serial chooser is a cancellation too',
    bridge.pollSerialRequest('serial-1').state,
    'cancelled');

check(
    'and reading the serial outcome twice consumes it',
    bridge.pollSerialRequest('serial-1').state === 'unknown');

checkEqual(
    'a serial request read through the USB map is unknown',
    bridge.pollRequest('serial-1').state,
    'unknown');

// --- the browser print fallback ------------------------------------------------------------
//
// The one route to paper in Safari and Firefox, where neither device API exists. The C# side renders
// the receipt to HTML and this writes it into an off-screen iframe; the two halves have to agree, and
// half of that agreement — the class vocabulary — was silently broken once already.

/** Just enough DOM for the print path. */
function installPrintDom() {
    const frame = {
        attributes: {},
        style: {},
        appended: false,
        removed: false,
        written: '',

        setAttribute(name, value) {
            frame.attributes[name] = value;
        },

        addEventListener() { /* readyState is reported complete, so no load event is needed. */ },

        remove() {
            frame.removed = true;
        },

        set contentDocument(value) {
            frame._document = value;
        },

        get contentDocument() {
            return frame._document;
        },

        contentWindow: {
            document: { readyState: 'complete' },
            focused: false,
            printed: false,

            focus() {
                frame.contentWindow.focused = true;
            },

            print() {
                frame.contentWindow.printed = true;
            },
        },
    };

    frame._document = {
        open() { /* nothing to reset */ },
        write(html) {
            frame.written += html;
        },
        close() { /* nothing to finalise */ },
    };

    Object.defineProperty(globalThis, 'document', {
        configurable: true,
        writable: true,
        value: {
            createElement: () => frame,
            body: { appendChild: () => { frame.appended = true; } },
        },
    });

    return frame;
}

globalThis.window = {
    isSecureContext: true,
    crossOriginIsolated: false,
    print() { /* the system dialog */ },
};

const frame = installPrintDom();

bridge = await loadModule(bridgePath);

check('browser printing is reported available', bridge.isBrowserPrintAvailable() === true);

// The delayed cleanup runs on a timer; firing timers immediately keeps the check honest without
// making the script wait two seconds.
const realSetTimeout = globalThis.setTimeout;
globalThis.setTimeout = callback => { callback(); return 0; };

await bridge.printViaBrowser(
    'Receipt CT01-20260325-0007',
    '<div class="c b">CORNER STORE</div><table><tr><td>Cola</td><td class="amt">15.00</td></tr></table>');

globalThis.setTimeout = realSetTimeout;

check('a frame is created and attached', frame.appended);
check('the document is written', frame.written.length > 0);
check('the receipt body reaches the frame', frame.written.includes('CORNER STORE'));
check('the job is printed', frame.contentWindow.printed);

// A receipt is 80mm wide with no margins. Without this the system dialog scales a receipt onto A4 and
// the customer gets a slip in the corner of a page.
check(
    'the paper size is declared, so the dialog does not scale to A4',
    /@page\s*\{[^}]*size:\s*80mm/.test(frame.written),
    'looking for a @page size rule');

check(
    'the frame is hidden from the page rather than omitted',
    frame.attributes['aria-hidden'] === 'true');

check('the frame is cleaned up afterwards', frame.removed);

// The contract with the C# renderer, asserted from the sending side. That renderer emits .c .b .s
// .big .rule and td.amt; a stylesheet missing one of them prints an unstyled receipt that is
// readable but has every price jammed against the item name.
const styled = ['c', 'r', 'b', 's', 'big', 'rule'];

const unstyled = styled.filter(name => !new RegExp(`\\.${name}\\s*\\{`).test(frame.written));

check(
    'the written stylesheet defines every class the renderer emits',
    unstyled.length === 0,
    unstyled.join(', '));

check(
    'and the amount column is right-aligned',
    /td\.amt\s*\{[^}]*text-align:\s*right/.test(frame.written));

// The title comes from a sale number and goes into markup, so it is escaped on this side too.
const titled = installPrintDom();

globalThis.setTimeout = callback => { callback(); return 0; };

await bridge.printViaBrowser('<script>alert(1)</script>', '<p>body</p>');

globalThis.setTimeout = realSetTimeout;

check(
    'a title containing markup cannot break out of the document',
    !titled.written.includes('<script>alert(1)</script>') && titled.written.includes('&lt;script&gt;'),
    'the title is interpolated into the head');

// A frame that cannot be created must fail loudly rather than printing nothing.
installPrintDom();
delete globalThis.document.createElement;

let frameFailure = '';

try {
    await bridge.printViaBrowser('x', '<p>y</p>');
} catch (error) {
    frameFailure = String(error.message);
}

check('a frame that cannot be created is an error, not silence', frameFailure.length > 0, frameFailure);

console.log('');

if (failures > 0) {
    console.error(`${failures} check(s) failed.`);
    process.exit(1);
}

console.log('All device bridge checks passed.');
