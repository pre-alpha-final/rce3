import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../TabletUI/wwwroot/service-worker.published.js', import.meta.url), 'utf8');
function worker(options = {}) {
    const listeners = new Map();
    const fetched = [];
    const installed = [];
    const removed = [];
    const scope = 'https://tablet.example/remote/';
    const context = {
        URL, Request, Response,
        self: {
            location: { href: scope + 'service-worker.js' },
            importScripts() {},
            clients: { async claim() {} },
            assetsManifest: { version: 'new', assets: [
                { url: 'index.html', hash: '' }, { url: '_framework/runtime.wasm', hash: '' },
                { url: 'vendor/font.woff2', hash: '' }, { url: 'service-worker.js', hash: '' }
            ] },
            addEventListener: (name, callback) => listeners.set(name, callback)
        },
        caches: {
            async open() {
                if (options.openError) throw new Error('Cache unavailable');
                return {
                async addAll(requests) { installed.push(...requests); },
                async match(request) {
                    if (options.matchError) throw new Error('Cache read failed');
                    if (options.cacheMiss) return undefined;
                    if (options.response) return options.response.clone();
                    return { cached: typeof request === 'string' ? request : request.url };
                }
            }; },
            async keys() { return ['tabletui-/remote/-old', 'tabletui-/remote/-new', 'other-app']; },
            async delete(name) { removed.push(name); }
        },
        async fetch(request) {
            fetched.push(request.url);
            if (options.offline) throw new TypeError('Offline');
            return { network: request.url };
        }
    };
    vm.runInNewContext(source, context);
    function fetchEvent(url, options = {}) {
        let response;
        listeners.get('fetch')({ request: { url, method: 'GET', cache: 'default', mode: 'cors', ...options },
            respondWith(value) { response = value; } });
        return response;
    }
    async function lifecycle(name) {
        let pending;
        listeners.get(name)({ waitUntil(value) { pending = value; } });
        await pending;
    }
    return { fetchEvent, lifecycle, installed, removed, fetched, scope };
}

test('feed traffic always bypasses the worker, even same-origin or asset-shaped URLs', () => {
    const w = worker();
    assert.equal(w.fetchEvent('https://feed.example/feed/reader'), undefined);
    assert.equal(w.fetchEvent(w.scope + 'feed/reader'), undefined);
    assert.equal(w.fetchEvent(w.scope + 'index.html', { cache: 'no-store' }), undefined);
    assert.equal(w.fetchEvent(w.scope, { method: 'POST' }), undefined);
    assert.equal(w.fetchEvent(w.scope + 'feed', { mode: 'navigate' }), undefined);
});

test('only exact app assets and app-root navigation use the cache', async () => {
    const w = worker();
    assert.deepEqual(await w.fetchEvent(w.scope, { mode: 'navigate' }), { cached: w.scope + 'index.html' });
    assert.deepEqual(await w.fetchEvent(w.scope + '_framework/runtime.wasm'), { cached: w.scope + '_framework/runtime.wasm' });
    assert.equal(w.fetchEvent(w.scope + 'unknown.js'), undefined);
    assert.equal(w.fetchEvent('https://elsewhere.example/remote/index.html'), undefined);
    assert.equal(w.fetched.length, 0);
});

test('installation includes fonts and WASM; activation cleans only this app cache', async () => {
    const w = worker();
    await w.lifecycle('install');
    assert.deepEqual(w.installed.map(request => request.url), [
        w.scope + 'index.html', w.scope + '_framework/runtime.wasm', w.scope + 'vendor/font.woff2'
    ]);
    assert.ok(w.installed.every(request => request.cache === 'no-cache'));
    await w.lifecycle('activate');
    assert.deepEqual(w.removed, ['tabletui-/remote/-old']);
});

for (const failure of ['openError', 'matchError', 'cacheMiss']) {
    test(`${failure} falls back to the network for navigation and assets`, async () => {
        const w = worker({ [failure]: true });
        assert.deepEqual(await w.fetchEvent(w.scope, { mode: 'navigate' }), { network: w.scope });
        const asset = w.scope + '_framework/runtime.wasm';
        assert.deepEqual(await w.fetchEvent(asset), { network: asset });
        assert.deepEqual(w.fetched, [w.scope, asset]);
    });
}

test('cached redirected HTML supports repeated offline navigation', async () => {
    const response = new Response('<html>TabletUI</html>', {
        headers: { 'Content-Type': 'text/html; charset=utf-8' }
    });
    // Model the redirect metadata retained by Cache API responses after a host redirect.
    const clone = response.clone.bind(response);
    response.clone = () => {
        const cached = clone();
        Object.defineProperty(cached, 'redirected', { value: true });
        return cached;
    };
    const w = worker({ response, offline: true });
    for (const path of ['', '', 'index.html', '?launch=home', 'index.html?launch=home']) {
        const result = await w.fetchEvent(w.scope + path, { mode: 'navigate', redirect: 'manual' });
        assert.equal(result.redirected, false);
        assert.equal(result.status, 200);
        assert.equal(result.headers.get('Content-Type'), 'text/html; charset=utf-8');
        assert.equal(await result.text(), '<html>TabletUI</html>');
    }
    assert.equal(w.fetched.length, 0);
});

test('a healthy cache still serves the app when offline', async () => {
    const w = worker({ offline: true });
    assert.deepEqual(await w.fetchEvent(w.scope, { mode: 'navigate' }), { cached: w.scope + 'index.html' });
    assert.equal(w.fetched.length, 0);
});
