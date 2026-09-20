// capture-shot.mjs — loads the till in headless Edge, waits for it to settle, and writes
// both a screenshot and the rendered text.
//
// Separate from capture-console.mjs because --virtual-time-budget races the WebAssembly
// startup: it screenshots the first frame, before the terminal has initialised, which
// looks like a broken app when it is merely a slow one. Waiting on the real clock and
// then asking the DevTools protocol for a capture avoids that false negative.

import { writeFileSync } from 'node:fs';

const [, , targetUrl, outPath, debugPort = '9222', settleMs = '15000'] = process.argv;

if (!targetUrl || !outPath) {
    console.error('usage: node capture-shot.mjs <url> <outPng> [debugPort] [settleMs]');
    process.exit(2);
}

const list = await (await fetch(`http://127.0.0.1:${debugPort}/json/list`)).json();
const page = list.find(t => t.type === 'page' && t.webSocketDebuggerUrl);
if (!page) {
    throw new Error('No page target.');
}

const socket = new WebSocket(page.webSocketDebuggerUrl);
let nextId = 1;
const pending = new Map();

function send(method, params = {}) {
    const id = nextId++;
    socket.send(JSON.stringify({ id, method, params }));
    return new Promise(resolve => pending.set(id, resolve));
}

socket.addEventListener('message', event => {
    const frame = JSON.parse(event.data);
    if (frame.id && pending.has(frame.id)) {
        pending.get(frame.id)(frame.result);
        pending.delete(frame.id);
    }
});

await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve);
    socket.addEventListener('error', () => reject(new Error('WebSocket failed')));
});

await send('Page.enable');
await send('Runtime.enable');
await send('Page.navigate', { url: targetUrl });

// Wait on the real clock: the WASM runtime has to download, compile, and start, and the
// terminal then loads its store from IndexedDB.
await new Promise(resolve => setTimeout(resolve, Number(settleMs)));

const state = await send('Runtime.evaluate', {
    expression: `({
        hasShell: !!document.querySelector('.pos-shell'),
        status: document.querySelector('.pos-status')?.innerText.replace(/\\n/g, ' | ') ?? null,
        scanPlaceholder: document.querySelector('.pos-scan__input')?.placeholder ?? null,
        activeElement: document.activeElement?.className ?? null,
        errorVisible: (() => { const e = document.getElementById('blazor-error-ui'); return e ? getComputedStyle(e).display !== 'none' : false; })(),
    })`,
    returnByValue: true,
});

const shot = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(outPath, Buffer.from(shot.data, 'base64'));

console.log(JSON.stringify(state.result.value, null, 2));
console.log(`screenshot written to ${outPath}`);

socket.close();
