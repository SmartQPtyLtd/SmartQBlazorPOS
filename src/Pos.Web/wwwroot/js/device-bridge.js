// device-bridge.js — WebUSB access for the POS, driven from Blazor.
//
// Why this is JavaScript and not C#: a live USBDevice handle cannot be marshalled into
// .NET. So JavaScript owns the device handle and C# owns the bytes. Every exported
// function is keyed by an opaque connection id that maps to a real USBDevice here.
//
// The user-gesture problem
// ------------------------
// navigator.usb.requestDevice() only works while a user gesture is still "live". A
// Blazor async handler that awaits anything before calling it has already lost that
// gesture, and the browser rejects the request. So requestDevice() is called
// SYNCHRONOUSLY inside beginWebUsbRequest(), the resulting promise is parked against a
// request id, and C# polls pollRequest() for the outcome.

const devices = new Map();   // connectionId -> { device, label }
const requests = new Map();  // requestId    -> { state, connectionId, label, error }

function newId(prefix) {
    return `${prefix}-${crypto.randomUUID()}`;
}

function describe(device) {
    const name = device.productName || 'USB printer';
    const vid = device.vendorId.toString(16).padStart(4, '0');
    const pid = device.productId.toString(16).padStart(4, '0');
    return `${name} (${vid}:${pid})`;
}

export function isWebUsbSupported() {
    return typeof navigator !== 'undefined' && 'usb' in navigator;
}

export function isWebSerialSupported() {
    return typeof navigator !== 'undefined' && 'serial' in navigator;
}

// --- Deferred device chooser ------------------------------------------------------

/**
 * Opens the USB device chooser. MUST be called from a user gesture.
 * Returns immediately; the outcome is retrieved via pollRequest().
 */
export function beginWebUsbRequest(requestId, vendorId) {
    if (!isWebUsbSupported()) {
        requests.set(requestId, { state: 'failed', error: 'WebUSB is not supported in this browser.' });
        return;
    }

    // Filtering tells the chooser to show printers, not every USB device. Filtering on
    // the printer interface class is more reliable than matching specific vendors,
    // since ESC/POS printers come from many manufacturers.
    const filters = [{ classCode: 7 }];
    if (typeof vendorId === 'number' && vendorId > 0) {
        filters.push({ vendorId });
    }

    requests.set(requestId, { state: 'pending' });

    // Called synchronously so the gesture is still valid.
    navigator.usb.requestDevice({ filters })
        .then(device => {
            const connectionId = newId('usb');
            devices.set(connectionId, { device, label: describe(device) });
            requests.set(requestId, {
                state: 'succeeded',
                connectionId,
                label: describe(device),
            });
        })
        .catch(error => {
            // A dismissed chooser surfaces as NotFoundError. That is a cancellation,
            // not a fault, and must not be reported to the operator as an error.
            const cancelled = error && error.name === 'NotFoundError';
            requests.set(requestId, {
                state: cancelled ? 'cancelled' : 'failed',
                error: error ? String(error.message || error) : 'Unknown error',
            });
        });
}

export function pollRequest(requestId) {
    const entry = requests.get(requestId);
    if (!entry) {
        return { state: 'unknown' };
    }

    // Terminal states are consumed once read, so the map cannot grow without bound.
    if (entry.state !== 'pending') {
        requests.delete(requestId);
    }

    return entry;
}

// --- Device access ----------------------------------------------------------------

/**
 * Devices this origin has already been granted access to, without prompting.
 *
 * This is what makes a terminal usable day to day: after a one-time pairing, a page
 * refresh or power cycle reattaches silently instead of showing a chooser.
 */
export async function getAuthorisedConnections() {
    if (!isWebUsbSupported()) {
        return [];
    }

    const granted = await navigator.usb.getDevices();
    const ids = [];

    for (const device of granted) {
        // Reuse an existing handle when the device is already open.
        let existing = null;
        for (const [id, entry] of devices) {
            if (entry.device === device) {
                existing = id;
                break;
            }
        }

        if (existing) {
            ids.push(existing);
            continue;
        }

        const connectionId = newId('usb');
        devices.set(connectionId, { device, label: describe(device) });
        ids.push(connectionId);
    }

    return ids;
}

/**
 * Opens a device and selects the interface carrying the printer data.
 *
 * ESC/POS printers commonly expose the print endpoint on interface 0 or 1, and some
 * expose a vendor-specific interface alongside a standard printer-class one. Rather
 * than hard-coding an interface number, the first interface with a bulk OUT endpoint is
 * chosen, which is what an ESC/POS printer uses to receive data.
 */
export async function openWebUsb(connectionId) {
    const entry = devices.get(connectionId);
    if (!entry) {
        throw new Error(`Unknown connection ${connectionId}.`);
    }

    const { device } = entry;

    if (!device.opened) {
        await device.open();
    }

    if (device.configuration === null) {
        await device.selectConfiguration(1);
    }

    let claimed = false;
    let lastError = null;

    for (const iface of device.configuration.interfaces) {
        const hasBulkOut = iface.alternates.some(alt =>
            alt.endpoints.some(ep => ep.direction === 'out' && ep.type === 'bulk'));

        if (!hasBulkOut) {
            continue;
        }

        try {
            await device.claimInterface(iface.interfaceNumber);
            entry.interfaceNumber = iface.interfaceNumber;

            // Remember the bulk OUT endpoint so writes need no further discovery.
            const alternate = iface.alternates[0];
            const endpoint = alternate.endpoints.find(
                ep => ep.direction === 'out' && ep.type === 'bulk');
            entry.endpointNumber = endpoint.endpointNumber;
            claimed = true;
            break;
        } catch (error) {
            // An interface may already be claimed by the operating system's usbprint
            // driver. Record why and try the next candidate.
            lastError = error;
        }
    }

    if (!claimed) {
        const detail = lastError ? String(lastError.message || lastError) : 'no bulk OUT endpoint';
        throw new Error(
            `Could not claim a printer interface: ${detail}. ` +
            'On Windows the printer may be held by a system driver; try a Web Serial connection instead.');
    }

    entry.label = describe(device);
}

/**
 * Writes a byte payload to the printer.
 *
 * Chunked because a USB bulk transfer has a maximum packet size, and a receipt can be
 * far larger than one packet. Writing it in one call would silently truncate.
 */
export async function writeWebUsb(connectionId, payload) {
    const entry = devices.get(connectionId);
    if (!entry) {
        throw new Error(`Unknown connection ${connectionId}.`);
    }

    const { device, endpointNumber } = entry;
    if (endpointNumber === undefined) {
        throw new Error('The device is not open. Call openWebUsb first.');
    }

    const bytes = payload instanceof Uint8Array ? payload : new Uint8Array(payload);
    const CHUNK = 16384;

    for (let offset = 0; offset < bytes.length; offset += CHUNK) {
        const chunk = bytes.subarray(offset, Math.min(offset + CHUNK, bytes.length));
        const result = await device.transferOut(endpointNumber, chunk);

        if (result.status !== 'ok') {
            throw new Error(`USB transfer failed with status "${result.status}".`);
        }
    }
}

export async function describeConnection(connectionId) {
    const entry = devices.get(connectionId);
    return entry ? entry.label : null;
}

export async function closeWebUsb(connectionId) {
    const entry = devices.get(connectionId);
    if (!entry) {
        return;
    }

    try {
        if (entry.interfaceNumber !== undefined) {
            await entry.device.releaseInterface(entry.interfaceNumber);
        }
        await entry.device.close();
    } catch {
        // Closing an already-detached device is not worth surfacing; the handle is
        // discarded either way.
    } finally {
        devices.delete(connectionId);
    }
}

/**
 * Lightweight capability probe for the device settings screen.
 */
export function capabilityReport() {
    return {
        webusb: isWebUsbSupported(),
        webserial: isWebSerialSupported(),
        webhid: typeof navigator !== 'undefined' && 'hid' in navigator,
        bluetooth: typeof navigator !== 'undefined' && 'bluetooth' in navigator,
        secureContext: typeof window !== 'undefined' ? window.isSecureContext : false,
        crossOriginIsolated: typeof window !== 'undefined' ? window.crossOriginIsolated : false,
    };
}

// --- Web Serial -------------------------------------------------------------------
//
// The most forgiving transport for thermal printers. Many ESC/POS printers expose a USB
// serial interface, and USB-to-serial adapters are universal, so this reaches hardware
// WebUSB cannot when the printer interface is already claimed by a system driver.

const serialPorts = new Map();   // connectionId -> { port, writer, label }
const serialRequests = new Map(); // requestId   -> { state, connectionId, label, error }

function describePort(port) {
    // Serial ports expose very little metadata, and only after they have been opened at
    // least once. A stable fallback label is better than showing "undefined".
    const info = typeof port.getInfo === 'function' ? port.getInfo() : {};
    const vid = info.usbVendorId ? info.usbVendorId.toString(16).padStart(4, '0') : '????';
    const pid = info.usbProductId ? info.usbProductId.toString(16).padStart(4, '0') : '????';
    return `Serial printer (${vid}:${pid})`;
}

/** Opens the serial port chooser. MUST be called from a user gesture. */
export function beginWebSerialRequest(requestId) {
    if (!isWebSerialSupported()) {
        serialRequests.set(requestId, { state: 'failed', error: 'Web Serial is not supported in this browser.' });
        return;
    }

    serialRequests.set(requestId, { state: 'pending' });

    navigator.serial.requestPort()
        .then(port => {
            const connectionId = newId('serial');
            serialPorts.set(connectionId, { port, writer: null, label: describePort(port) });
            serialRequests.set(requestId, {
                state: 'succeeded',
                connectionId,
                label: describePort(port),
            });
        })
        .catch(error => {
            const cancelled = error && error.name === 'NotFoundError';
            serialRequests.set(requestId, {
                state: cancelled ? 'cancelled' : 'failed',
                error: error ? String(error.message || error) : 'Unknown error',
            });
        });
}

/** Serial ports already granted to this origin, without prompting. */
export async function getAuthorisedWebSerialConnections() {
    if (!isWebSerialSupported()) {
        return [];
    }

    const granted = await navigator.serial.getPorts();
    const ids = [];

    for (const port of granted) {
        let existing = null;
        for (const [id, entry] of serialPorts) {
            if (entry.port === port) {
                existing = id;
                break;
            }
        }

        if (existing) {
            ids.push(existing);
            continue;
        }

        const connectionId = newId('serial');
        serialPorts.set(connectionId, { port, writer: null, label: describePort(port) });
        ids.push(connectionId);
    }

    return ids;
}

/**
 * Opens a serial port for writing.
 *
 * A printer needs no handshake: ESC/POS is a write-only protocol, so the port is opened
 * at the configured line rate and left open for the session rather than reopened per job.
 */
export async function openWebSerial(connectionId, baudRate) {
    const entry = serialPorts.get(connectionId);
    if (!entry) {
        throw new Error(`Unknown serial connection ${connectionId}.`);
    }

    if (!entry.port.readable) {
        await entry.port.open({ baudRate: baudRate || 9600, dataBits: 8, stopBits: 1, parity: 'none' });
    }

    entry.label = describePort(entry.port);
}

/**
 * Writes bytes to the serial port.
 *
 * Serialised through a promise chain because a serial port accepts exactly one writer at
 * a time; two overlapping writes would interleave two receipts into an unreadable stream.
 */
export async function writeWebSerial(connectionId, payload) {
    const entry = serialPorts.get(connectionId);
    if (!entry) {
        throw new Error(`Unknown serial connection ${connectionId}.`);
    }

    const bytes = payload instanceof Uint8Array ? payload : new Uint8Array(payload);

    entry.queue = (entry.queue || Promise.resolve()).then(async () => {
        const writer = entry.port.writable.getWriter();
        try {
            await writer.write(bytes);
        } finally {
            // Releasing the lock is mandatory: the next job cannot acquire a writer while
            // this one holds it, and the failure would look like a dead printer.
            writer.releaseLock();
        }
    });

    return entry.queue;
}

export async function closeWebSerial(connectionId) {
    const entry = serialPorts.get(connectionId);
    if (!entry) {
        return;
    }

    try {
        if (entry.port.readable || entry.port.writable) {
            await entry.port.close();
        }
    } catch {
        // Closing an already-detached port is not worth surfacing.
    } finally {
        serialPorts.delete(connectionId);
    }
}

// --- Browser printing -------------------------------------------------------------
//
// The fallback that always works. It cannot cut paper or open a cash drawer, and it is not
// suitable for high-volume receipt printing, but it means a browser with no device APIs can
// still complete a sale rather than leaving the operator with nothing.

/** Whether the browser print fallback is usable. */
export function isBrowserPrintAvailable() {
    return typeof window !== 'undefined' && typeof window.print === 'function';
}

/**
 * Renders a receipt into a hidden iframe and opens the system print dialog.
 *
 * An iframe is used rather than printing the page itself, because printing the main window
 * would also print the till UI behind the receipt.
 *
 * The stylesheet below is a CONTRACT with ReceiptHtmlRenderer in Pos.Devices: that renderer emits
 * exactly these class names (.c .r .b .s .big .rule, and a table with td.amt for amounts). A class
 * added or renamed on either side silently produces an unstyled receipt — readable, but with every
 * price jammed against the item name. Change one and check the other.
 *
 * @param {string} title    Job name shown in the print dialog.
 * @param {string} htmlBody Printable HTML for the receipt body.
 */
export async function printViaBrowser(title, htmlBody) {
    if (!isBrowserPrintAvailable()) {
        throw new Error('Browser printing is not available.');
    }

    const frame = document.createElement('iframe');

    // Positioned off-screen rather than display:none, because a hidden iframe has no
    // layout and some browsers print it blank.
    frame.setAttribute('aria-hidden', 'true');
    frame.style.position = 'fixed';
    frame.style.right = '0';
    frame.style.bottom = '0';
    frame.style.width = '302px';
    frame.style.height = '600px';
    frame.style.border = '0';
    frame.style.opacity = '0';
    frame.style.pointerEvents = 'none';

    document.body.appendChild(frame);

    const doc = frame.contentDocument;
    if (!doc) {
        frame.remove();
        throw new Error('Could not create a print frame.');
    }

    // A thermal receipt is 80mm wide with no margins; the page box is set to match so the
    // output is not scaled down to A4.
    doc.open();
    doc.write(`<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8" />
<title>${escapeHtml(title)}</title>
<style>
  @page { size: 80mm auto; margin: 0; }
  html, body { margin: 0; padding: 0; }
  body {
    width: 72mm;
    padding: 2mm;
    font-family: "Courier New", monospace;
    font-size: 12px;
    line-height: 1.25;
    color: #000;
  }
  .c { text-align: center; }
  .r { text-align: right; }
  .b { font-weight: bold; }
  .s { font-size: 10px; }
  .big { font-size: 18px; font-weight: bold; }
  .rule { border-top: 1px dashed #000; margin: 4px 0; }
  table { width: 100%; border-collapse: collapse; }
  td { vertical-align: top; padding: 1px 0; }
  td.amt { text-align: right; white-space: nowrap; }
</style>
</head>
<body>${htmlBody}</body>
</html>`);
    doc.close();

    // Wait for layout and fonts, otherwise the dialog can open before the content is
    // painted and the preview comes out empty.
    await new Promise(resolve => {
        if (frame.contentWindow && frame.contentWindow.document.readyState === 'complete') {
            resolve();
        } else {
            frame.addEventListener('load', resolve, { once: true });
        }
    });

    try {
        frame.contentWindow.focus();
        frame.contentWindow.print();
    } finally {
        // Left in place briefly: removing the frame synchronously can cancel the print job
        // in some browsers before the dialog has taken its snapshot.
        setTimeout(() => frame.remove(), 2000);
    }
}

function escapeHtml(text) {
    return String(text)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

/** Shared poll handler: WebUSB and Web Serial use the same deferred-promise shape. */
export function pollSerialRequest(requestId) {
    const entry = serialRequests.get(requestId);
    if (!entry) {
        return { state: 'unknown' };
    }

    if (entry.state !== 'pending') {
        serialRequests.delete(requestId);
    }

    return entry;
}
