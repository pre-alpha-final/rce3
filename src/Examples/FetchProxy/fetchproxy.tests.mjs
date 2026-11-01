import test from 'node:test';
import assert from 'node:assert/strict';
import { webcrypto, randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const source = await readFile(new URL('./fetchproxy.js', import.meta.url), 'utf8');
const feed = 'https://feed.example/00000000-0000-0000-0000-000000000000';
const settle = () => new Promise(resolve => setImmediate(resolve));

function harness(t, config = { RCE3_FEED: feed }) {
    const calls = [];
    const logs = [];
    const timers = new Map();
    const window = new EventTarget();
    const context = vm.createContext({
        ...config, window, URL, Headers, AbortController, crypto: webcrypto,
        console: Object.fromEntries(['info', 'warn', 'error'].map(level =>
            [level, (...args) => logs.push({ level, text: args.join(' ') })])),
        setTimeout(callback, delay) {
            const id = Symbol();
            timers.set(id, { callback, delay });
            return id;
        },
        clearTimeout(id) { timers.delete(id); },
        fetch(url, init) {
            return new Promise((resolve, reject) => {
                const cancel = () => reject(init.signal.reason);
                init.signal.addEventListener('abort', cancel, { once: true });
                calls.push({ url, init,
                    resolve(response) {
                        init.signal.removeEventListener('abort', cancel);
                        resolve(response);
                    },
                    reject(error = new TypeError('Network failure')) {
                        init.signal.removeEventListener('abort', cancel);
                        reject(error);
                    }
                });
            });
        }
    });
    t.after(async () => {
        window.RCE3_FETCH_PROXY?.stop();
        await settle();
        assert.equal(timers.size, 0, 'all timers must be cleared on stop');
    });
    return {
        calls, logs, timers, window, context,
        start() { vm.runInContext(source, context); },
        async call(index, url) {
            await settle();
            assert.ok(calls[index], `expected request ${index}, got ${calls.length} requests`);
            if (url !== undefined) assert.equal(calls[index].url, url);
            return calls[index];
        },
        async fire(delay) {
            const matching = [...timers].filter(([, timer]) => timer.delay === delay);
            assert.equal(matching.length, 1, `expected one ${delay} ms timer`);
            const [id, timer] = matching[0];
            timers.delete(id);
            timer.callback();
            await settle();
        }
    };
}

function targetResponse(body = 'hello', options = {}) {
    const { url = 'https://tab.example/result', redirected = false, ...init } = options;
    const response = new Response(body, init);
    Object.defineProperties(response, {
        url: { value: url }, redirected: { value: redirected }, type: { value: 'basic' }
    });
    return response;
}

async function command(h, message = 'fetchproxy: /target') {
    h.start();
    const poll = await h.call(0);
    poll.resolve(new Response(message));
    return h.call(1);
}

function result(publish, url = '/target') {
    const prefix = `fetchproxyresponse: ${url}\n`;
    assert.ok(publish.init.body.startsWith(prefix));
    return JSON.parse(publish.init.body.slice(prefix.length));
}

for (const config of [
    {}, { RCE3_FEED: undefined }, { RCE3_FEED: '' }, { RCE3_FEED: '  ' }, { RCE3_FEED: 42 },
    { RCE3_FEED: '/relative' }, { RCE3_FEED: 'not a URL' }, { RCE3_FEED: 'ftp://feed.example/feed' },
    { RCE3_FEED: 'https://feed.example/' }, { RCE3_FEED: feed + '?query=1' },
    { RCE3_FEED: feed + '#fragment' }, { RCE3_FEED: 'https://user:password@feed.example/feed' },
    ...[null, 42, ' ', 'line\nbreak', 'line\rbreak', '\u0100'].map(RCE3_AUTH => ({ RCE3_FEED: feed, RCE3_AUTH }))
]) {
    test(`rejects invalid configuration before connecting: ${JSON.stringify(config)}`, t => {
        const h = harness(t, config);
        assert.throws(() => h.start(), /FetchProxy:.*RCE3_(FEED|AUTH)/);
        assert.equal(h.calls.length, 0);
        assert.equal(h.window.RCE3_FETCH_PROXY, undefined);
        assert.equal(h.logs.length, 0);
    });
}

for (const auth of [undefined, '']) {
    test(`open feed omits Authorization (${JSON.stringify(auth)})`, async t => {
        const h = harness(t, { RCE3_FEED: '  ' + feed + '///  ', RCE3_AUTH: auth });
        const target = await command(h);
        const poll = h.calls[0];
        assert.match(poll.url, new RegExp(`^${feed}/[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`));
        assert.equal(new Headers(poll.init.headers).has('Authorization'), false);
        assert.equal(poll.init.credentials, 'omit');
        assert.equal(poll.init.cache, 'no-store');
        assert.equal(poll.init.redirect, 'manual');
        target.resolve(targetResponse());
        const publish = await h.call(2, feed);
        assert.equal(new Headers(publish.init.headers).has('Authorization'), false);
        assert.equal(publish.init.credentials, 'omit');
        assert.equal(publish.init.method, 'POST');
        assert.equal(new Headers(publish.init.headers).get('Content-Type'), 'text/plain;charset=UTF-8');
    });
}

test('reads top-level lexical configuration without assigning to it', async t => {
    const h = harness(t, {});
    vm.runInContext(`const RCE3_FEED = ${JSON.stringify(feed)}; let RCE3_AUTH = '';`, h.context);
    h.start();
    await h.call(0);
    assert.equal(vm.runInContext('RCE3_FEED', h.context), feed);
    assert.equal(vm.runInContext('RCE3_AUTH', h.context), '');
});

test('protected feed key stays on feed requests and out of target options, responses, and logs', async t => {
    const key = 'test-' + randomUUID();
    const h = harness(t, { RCE3_FEED: feed, RCE3_AUTH: key });
    const target = await command(h);
    assert.equal(h.calls[0].init.headers.Authorization, key);
    assert.deepEqual(Object.keys(target.init), ['signal']);
    assert.equal(target.url, '/target');
    target.resolve(targetResponse('target body'));
    const publish = await h.call(2, feed);
    assert.equal(publish.init.headers.Authorization, key);
    assert.equal(publish.init.body.includes(key), false);
    assert.equal(h.logs.some(log => log.text.includes(key)), false);
});

test('handles empty polls and ignores commands without a literal space, unrelated, differently cased, and response messages', async t => {
    const h = harness(t);
    h.start();
    const bodies = [null, 'youtube: playpause', 'Fetchproxy: /wrong', ' fetchproxy: /wrong',
        'frechproxy: /wrong', 'fetchproxyresponse: /target\n{}', '',
        'fetchproxy:', 'fetchproxy:/wrong', 'fetchproxy:\t/wrong', 'fetchproxy:\n/wrong'];
    for (let i = 0; i < bodies.length; i++) {
        const poll = await h.call(i);
        assert.equal(poll.url, h.calls[0].url);
        poll.resolve(new Response(bodies[i], { status: bodies[i] === null ? 204 : 200 }));
    }
    const next = await h.call(bodies.length);
    next.resolve(new Response('fetchproxy:   /target?x=1  \n'));
    const target = await h.call(bodies.length + 1, '/target?x=1');
    assert.equal(target.init.method, undefined, 'use the normal GET default');
});

for (const [name, body, status] of [
    ['text', 'hello\nworld 😀', 200], ['JSON', '{"answer":42}', 200],
    ['HTML error', '<h1>Not found</h1>', 404], ['empty', null, 204]
]) {
    test(`serializes the complete ${name} response with metadata and original URL`, async t => {
        const h = harness(t);
        const target = await command(h, 'fetchproxy:   /original  ');
        target.resolve(targetResponse(body, {
            status, statusText: status === 404 ? 'Not Found' : '',
            url: 'https://tab.example/final', redirected: true,
            headers: { 'Content-Type': 'text/plain; charset=UTF-8', 'X-Example': 'value' }
        }));
        const publish = await h.call(2, feed);
        assert.deepEqual(result(publish, '/original'), {
            url: 'https://tab.example/final', status, statusText: status === 404 ? 'Not Found' : '',
            ok: status < 400, redirected: true, type: 'basic',
            headers: { 'content-type': 'text/plain; charset=UTF-8', 'x-example': 'value' },
            body: body ?? ''
        });
        assert.ok(publish.init.body.includes('\n  "url":'), 'JSON is pretty-printed');
    });
}

test('does not poll again until both the target fetch and publication finish', async t => {
    const h = harness(t);
    const target = await command(h);
    await settle();
    assert.equal(h.calls.length, 2);
    target.resolve(targetResponse());
    const publish = await h.call(2, feed);
    await settle();
    assert.equal(h.calls.length, 3);
    publish.resolve(new Response('1'));
    const next = await h.call(3, h.calls[0].url);
    next.resolve(new Response('fetchproxy: /second'));
    await h.call(4, '/second');
});

for (const url of ['', 'https://[invalid']) {
    test(`publishes invalid target errors without losing the source URL (${JSON.stringify(url)})`, async t => {
        const h = harness(t);
        h.start();
        (await h.call(0)).resolve(new Response(`fetchproxy: ${url}`));
        let publishIndex = 1;
        if (url !== '') {
            (await h.call(1, url)).reject(new TypeError('Invalid URL'));
            publishIndex++;
        }
        const publish = await h.call(publishIndex, feed);
        assert.equal(result(publish, url).error.name, 'TypeError');
        assert.ok(result(publish, url).error.message);
    });
}

test('publishes a network error once, then resumes polling after publishing it', async t => {
    const h = harness(t);
    (await command(h)).reject();
    const publish = await h.call(2, feed);
    assert.deepEqual(result(publish), { error: { name: 'TypeError', message: 'Network failure' } });
    publish.resolve(new Response('1'));
    await h.call(3, h.calls[0].url);
});

for (const stage of ['headers', 'body']) {
    test(`10-second target timeout covers ${stage} and publishes an error`, async t => {
        const h = harness(t);
        const target = await command(h);
        let bodyStarted = false;
        if (stage === 'body') {
            target.resolve({
                text() {
                    bodyStarted = true;
                    return new Promise((resolve, reject) =>
                        target.init.signal.addEventListener('abort', () => reject(target.init.signal.reason), { once: true }));
                }
            });
            await settle();
            assert.equal(bodyStarted, true);
        }
        await h.fire(10_000);
        assert.equal(target.init.signal.aborted, true);
        const publish = await h.call(2, feed);
        assert.deepEqual(result(publish), {
            error: { name: 'TimeoutError', message: 'Target fetch exceeded 10 seconds.' }
        });
    });
}

for (const failure of ['network', 'body', 408, 429, 500, 503]) {
    test(`retries transient feed reads after one second (${failure})`, async t => {
        const h = harness(t);
        h.start();
        const poll = await h.call(0);
        if (failure === 'network') poll.reject();
        else if (failure === 'body') poll.resolve({ status: 200, async text() { throw new TypeError('Interrupted'); } });
        else poll.resolve(new Response(null, { status: failure }));
        await settle();
        assert.equal(h.calls.length, 1);
        assert.equal(h.logs.filter(log => log.level === 'warn').length, 1);
        await h.fire(1000);
        await h.call(1, poll.url);
    });
}

for (const status of [400, 401, 403, 404]) {
    test(`permanent poll failure ${status} stops loudly`, async t => {
        const h = harness(t);
        h.start();
        const poll = await h.call(0);
        poll.resolve(new Response(null, { status }));
        await settle();
        assert.equal(poll.init.signal.aborted, true);
        assert.equal(h.calls.length, 1);
        assert.match(h.logs.find(log => log.level === 'error').text, new RegExp(`HTTP ${status}`));
    });
}

for (const redirect of ['302', 'opaqueredirect']) {
    test(`overflow resets the same reader using manual redirects (${redirect})`, async t => {
        const h = harness(t);
        h.start();
        const poll = await h.call(0);
        assert.equal(poll.url.endsWith('/reset'), false, 'startup polls directly');
        poll.resolve(new Response(null, { status: 410 }));
        const reset = await h.call(1, poll.url + '/reset');
        assert.equal(reset.init.redirect, 'manual');
        reset.resolve(new Response(null, { status: 503 }));
        await settle();
        await h.fire(1000);
        const retriedReset = await h.call(2, reset.url);
        retriedReset.resolve(redirect === '302' ? new Response(null, { status: 302 }) :
            { status: 0, type: 'opaqueredirect', body: null });
        await h.call(3, poll.url);
    });
}

test('reset authorization failure stops without following a redirect or polling again', async t => {
    const h = harness(t);
    h.start();
    (await h.call(0)).resolve(new Response(null, { status: 410 }));
    const reset = await h.call(1);
    reset.resolve(new Response(null, { status: 401 }));
    await settle();
    assert.equal(reset.init.signal.aborted, true);
    assert.equal(h.calls.length, 2);
    assert.match(h.logs.find(log => log.level === 'error').text, /reset.*401/);
});

for (const failure of ['network', 401, 413, 503]) {
    test(`publishing failure ${failure} stops without retrying or truncating`, async t => {
        const h = harness(t);
        const body = 'x'.repeat(1_048_577);
        (await command(h)).resolve(targetResponse(body));
        const publish = await h.call(2, feed);
        assert.equal(result(publish).body, body);
        if (failure === 'network') publish.reject();
        else publish.resolve(new Response(null, { status: failure }));
        await settle();
        assert.equal(publish.init.signal.aborted, true);
        assert.equal(h.calls.length, 3);
        const error = h.logs.find(log => log.level === 'error').text;
        assert.match(error, /publish failed.*not retried/i);
        if (failure !== 'network') assert.ok(error.includes(`HTTP ${failure}`));
        assert.equal(error.includes(body), false);
    });
}

for (const stage of ['poll', 'reset', 'target', 'body', 'publish', 'retry']) {
    test(`pagehide cancels ${stage} and pageshow does not restart it`, async t => {
        const h = harness(t);
        h.start();
        let active = await h.call(0);
        if (stage === 'reset') {
            active.resolve(new Response(null, { status: 410 }));
            active = await h.call(1);
        } else if (['target', 'body', 'publish'].includes(stage)) {
            active.resolve(new Response('fetchproxy: /target'));
            active = await h.call(1);
            if (stage === 'body') {
                const signal = active.init.signal;
                active.resolve({ text: () => new Promise((resolve, reject) =>
                    signal.addEventListener('abort', () => reject(signal.reason), { once: true })) });
            } else if (stage === 'publish') {
                active.resolve(targetResponse());
                active = await h.call(2, feed);
            }
        } else if (stage === 'retry') {
            active.reject();
        }
        await settle();
        const count = h.calls.length;
        h.window.dispatchEvent(new Event('pagehide'));
        await settle();
        assert.equal(active.init.signal.aborted, true);
        assert.equal(h.timers.size, 0);
        assert.equal(h.logs.filter(log => log.level === 'error').length, 0);
        h.window.dispatchEvent(new Event('pageshow'));
        h.window.RCE3_FETCH_PROXY.stop();
        await settle();
        assert.equal(h.calls.length, count);
        assert.equal(h.logs.filter(log => log.text.includes('Stopped')).length, 1);
    });
}

test('re-pasting aborts the previous target and starts a fresh reader without an old response', async t => {
    const h = harness(t);
    const target = await command(h);
    const previous = h.window.RCE3_FETCH_PROXY;
    h.start();
    const poll = await h.call(2);
    assert.equal(target.init.signal.aborted, true);
    assert.notEqual(poll.url, h.calls[0].url);
    assert.notEqual(h.window.RCE3_FETCH_PROXY, previous);
    previous.stop();
    assert.equal(poll.init.signal.aborted, false, 'old stop cannot stop the new run');
    assert.equal(h.calls.length, 3);
    assert.equal(h.logs.filter(log => log.level === 'error').length, 0);
    h.window.dispatchEvent(new Event('pagehide'));
    assert.equal(poll.init.signal.aborted, true);
});

test('invalid re-paste leaves the existing instance running', async t => {
    const h = harness(t);
    h.start();
    const poll = await h.call(0);
    const previous = h.window.RCE3_FETCH_PROXY;
    h.context.RCE3_FEED = '';
    assert.throws(() => h.start(), /RCE3_FEED/);
    assert.equal(h.window.RCE3_FETCH_PROXY, previous);
    assert.equal(poll.init.signal.aborted, false);
});
