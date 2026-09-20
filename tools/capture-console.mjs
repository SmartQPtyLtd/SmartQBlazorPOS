// capture-console.mjs — attaches to a headless Edge over the DevTools protocol,
// loads the till, and prints every console message and uncaught exception.
//
// Written by hand rather than pulling in Playwright/Puppeteer: the browser is already on
// the machine, so the only missing piece is the CDP handshake, and Node has a WebSocket
// client built in. Keeping the dependency count at zero matters for a POS that has to be
// buildable in a locked-down environment.

const [, , targetUrl, debugPort = '9222'] = process.argv;

if (!targetUrl) {
    console.error('usage: node capture-console.mjs <url> [debugPort]');
    process.exit(2);
}

async function findPageTarget(port) {
    // Chrome writes its WebSocket endpoint to /json/version; the page list is at /json.
    const response = await fetch(`http://127.0.0.1:${port}/json/list`);
    const targets = await response.json();

    const page = targets.find(t => t.type === 'page' && t.webSocketDebuggerUrl);
    if (!page) {
        throw new Error(`No page target found. Targets: ${targets.map(t => t.type).join(', ')}`);
    }

    return page.webSocketDebuggerUrl;
}

const wsUrl = await findPageTarget(debugPort);
const socket = new WebSocket(wsUrl);

let nextId = 1;
const pending = new Map();

function send(method, params = {}) {
    const id = nextId++;
    socket.send(JSON.stringify({ id, method, params }));
    return new Promise(resolve => pending.set(id, resolve));
}

const messages = [];

socket.addEventListener('message', event => {
    const frame = JSON.parse(event.data);

    if (frame.id && pending.has(frame.id)) {
        pending.get(frame.id)(frame.result);
        pending.delete(frame.id);
        return;
    }

    if (frame.method === 'Runtime.consoleAPICalled') {
        const text = (frame.params.args || [])
            .map(a => a.value ?? a.description ?? a.type)
            .join(' ');
        messages.push(`[${frame.params.type}] ${text}`);
        return;
    }

    if (frame.method === 'Runtime.exceptionThrown') {
        const d = frame.params.exceptionDetails;
        const description = d.exception?.description || d.text || 'unknown exception';
        messages.push(`[EXCEPTION] ${description}`);
        return;
    }

    if (frame.method === 'Log.entryAdded') {
        const e = frame.params.entry;
        if (e.level === 'error' || e.level === 'warning') {
            messages.push(`[log:${e.level}] ${e.text}`);
        }
    }
});

await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve);
    socket.addEventListener('error', () => reject(new Error('WebSocket failed to open')));
});

await send('Runtime.enable');
await send('Log.enable');
await send('Page.enable');

// Hard-reload so every startup diagnostic is captured from the very first byte.
await send('Page.navigate', { url: targetUrl });

// Give the WASM runtime time to download, start, and either render or throw.
await new Promise(resolve => setTimeout(resolve, 25000));

const dom = await send('Runtime.evaluate', {
    expression: `(() => {
        const shell = document.querySelector('.pos-shell');
        const errorUi = document.getElementById('blazor-error-ui');
        const errorVisible = errorUi ? getComputedStyle(errorUi).display !== 'none' : false;
        return {
            hasShell: !!shell,
            errorVisible,
            errorText: errorVisible && errorUi ? errorUi.innerText.trim() : null,
            bodyStart: document.body.innerText.slice(0, 400),
        };
    })()`,
    returnByValue: true,
});

console.log('=== console output ===');
console.log(messages.length ? messages.join('\n') : '(none)');
console.log('\n=== DOM ===');
console.log(JSON.stringify(dom?.result?.value ?? dom, null, 2));

socket.close();
