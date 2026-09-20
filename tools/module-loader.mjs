// Loads one of the app's browser modules into Node.
//
// The modules are plain ES modules with no build step, but they live under wwwroot as `.js` and this
// repository has no package.json, so Node would read them as CommonJS. Copying to a temporary `.mjs`
// sidesteps that without adding a package manifest to the web root.
//
// Copied rather than imported through a `data:` URL because a data URL puts the entire base64 source
// into every stack frame: one failing assertion produced fourteen kilobytes of unreadable noise
// before this was changed, which is its own kind of defect in a verification script.
//
// Each call copies to a fresh temporary path, so every caller gets a **fresh module instance**. That
// matters: these modules keep state at module level — open device handles, parked requests, the
// indexedDB connection — and a test that reused an instance would inherit the previous one's devices.

import { mkdtemp, copyFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, basename } from 'node:path';

/**
 * Imports a browser module as ESM, in a private instance.
 *
 * @param {string} path Path to the `.js` module.
 * @returns {Promise<object>} The module's exports.
 */
export async function loadModule(path) {
    const directory = await mkdtemp(join(tmpdir(), 'pos-module-'));

    const name = basename(path).replace(/\.js$/, '.mjs');
    const target = join(directory, name);

    await copyFile(path, target);

    return import(`file://${target.replace(/\\/g, '/')}`);
}
