import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../TabletUI/wwwroot/service-worker.published.js', import.meta.url), 'utf8');
const assetBody = 'published asset';
const assetHash = 'sha256-' + createHash('sha256').update(assetBody).digest('base64');
function worker(options = {}) {
    const listeners = new Map();
    const fetched = [];
    const installed = [];
    const removed = [];
    const lifecycleSteps = [];
    const scope = 'https://tablet.example/remote/';
    const context = {
        URL, Request, Response,
        self: {
            location: { href: scope + 'service-worker.js' },
            importScripts() {},
            async skipWaiting() { lifecycleSteps.push("skipWaiting"); },
            clients: { async claim() { lifecycleSteps.push("claim"); } },
            assetsManifest: { version: 'new', assets: [
                { url: 'index.html', hash: assetHash }, { url: '_framework/runtime.wasm', hash: assetHash },
                { url: 'vendor/font.woff2', hash: assetHash }, { url: 'service-worker.js', hash: assetHash }
            ] },
            addEventListener: (name, callback) => listeners.set(name, callback)
        },
        caches: {
            async open() {
                if (options.openError) throw new Error('Cache unavailable');
                return {
                async addAll(requests) {
                    if (options.installError) throw new Error("Incomplete release");
                    for (const request of requests) {
                        const changed = new URL(request.url).pathname.endsWith(options.changedAsset || '/never');
                        const body = changed ? assetBody + '<script>host injection</script>' : assetBody;
                        const hash = 'sha256-' + createHash('sha256').update(body).digest('base64');
                        if (request.integrity && request.integrity !== hash) throw new TypeError('Integrity mismatch');
                    }
                    installed.push(...requests);
                    lifecycleSteps.push("cached");
                },
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
    return { fetchEvent, lifecycle, installed, removed, fetched, scope, lifecycleSteps };
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

test('a complete release activates without waiting for old windows to close', async () => {
    const w = worker();
    await w.lifecycle('install');
    assert.deepEqual(w.lifecycleSteps, ['cached', 'skipWaiting']);
    await w.lifecycle('activate');
    assert.deepEqual(w.lifecycleSteps, ['cached', 'skipWaiting', 'claim']);
});

test('an incomplete release never skips waiting or removes the current cache', async () => {
    const w = worker({ installError: true });
    await assert.rejects(w.lifecycle('install'), /Incomplete release/);
    assert.deepEqual(w.lifecycleSteps, []);
    assert.deepEqual(w.removed, []);
});

const updateSource = await readFile(new URL('../TabletUI/wwwroot/app-updates.js', import.meta.url), 'utf8');
async function updateClient({ controlled = true, registerError = false, updateError = false } = {}) {
    const events = new Map();
    const state = { reloads: 0, checks: 0 };
    const listen = name => (event, callback) => events.set(`${name}:${event}`, callback);
    const navigator = {
        onLine: true,
        serviceWorker: {
            controller: controlled ? {} : null,
            addEventListener: listen('worker'),
            async register(url, options) {
                assert.equal(url, 'service-worker.js');
                assert.equal(options.updateViaCache, 'none');
                if (registerError) throw new Error('Unavailable');
                return { async update() {
                    state.checks++;
                    if (updateError) throw new Error('Offline');
                } };
            }
        }
    };
    const document = { visibilityState: 'visible', addEventListener: listen('document') };
    vm.runInNewContext(updateSource, {
        navigator, document,
        window: {
            location: { reload() { state.reloads++; } },
            addEventListener: listen('window'),
            setInterval(callback, delay) {
                assert.equal(delay, 60_000);
                events.set('interval', callback);
            }
        }
    });
    await new Promise(resolve => setImmediate(resolve));
    return { state, navigator, document, events };
}

test('replacement controller reloads the running app only once', async () => {
    const client = await updateClient();
    client.events.get('worker:controllerchange')();
    client.events.get('worker:controllerchange')();
    assert.equal(client.state.reloads, 1);
});

test('first successful installation reloads an older uncontrolled page once', async () => {
    const client = await updateClient({ controlled: false });
    client.events.get('worker:controllerchange')();
    assert.equal(client.state.reloads, 1);
    client.events.get('worker:controllerchange')();
    assert.equal(client.state.reloads, 1);
});

test('checks at startup and on resume, online, pageshow and the visible timer', async () => {
    const client = await updateClient();
    assert.equal(client.state.checks, 1);
    for (const event of ['document:visibilitychange', 'window:online', 'window:pageshow', 'interval']) {
        await client.events.get(event)();
    }
    assert.equal(client.state.checks, 5);
    client.document.visibilityState = 'hidden';
    await client.events.get('interval')();
    assert.equal(client.state.checks, 5);
    client.document.visibilityState = 'visible';
    client.navigator.onLine = false;
    await client.events.get('interval')();
    assert.equal(client.state.checks, 5);
});

test('failed checks can retry and registration failures are handled', async () => {
    const client = await updateClient({ updateError: true });
    await client.events.get('window:online')();
    assert.equal(client.state.checks, 2);
    assert.equal(client.state.reloads, 0);
    const unavailable = await updateClient({ registerError: true });
    assert.equal(unavailable.state.checks, 0);
    vm.runInNewContext(updateSource, { navigator: {} });
});

test('host-injected HTML does not prevent the release from activating', async () => {
    const w = worker({ changedAsset: 'index.html' });
    await w.lifecycle('install');
    assert.equal(w.installed[0].integrity, '');
    assert.ok(w.installed.slice(1).every(request => request.integrity === assetHash));
    assert.deepEqual(w.lifecycleSteps, ['cached', 'skipWaiting']);
});

test('changed non-HTML assets still fail installation and preserve the active release', async () => {
    const w = worker({ changedAsset: 'runtime.wasm' });
    await assert.rejects(w.lifecycle('install'), /Integrity mismatch/);
    assert.deepEqual(w.lifecycleSteps, []);
    assert.deepEqual(w.removed, []);
});
