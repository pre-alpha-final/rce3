self.importScripts('./service-worker-assets.js');

const baseUrl = new URL('./', self.location.href);
const cachePrefix = `tabletui-${baseUrl.pathname}-`;
const cacheName = cachePrefix + self.assetsManifest.version;
const assets = self.assetsManifest.assets.filter(asset => !/^service-worker(?:\.published)?\.js$/.test(asset.url));
const assetUrls = new Set(assets.map(asset => new URL(asset.url, baseUrl).href));
const indexUrl = new URL('index.html', baseUrl).href;

self.addEventListener('install', event => event.waitUntil((async () => {
    const cache = await caches.open(cacheName);
    await cache.addAll(assets.map(asset => new Request(new URL(asset.url, baseUrl), {
        integrity: asset.hash, cache: 'no-cache'
    })));
    // Activate only after the complete release has been cached successfully.
    await self.skipWaiting();
})()));

self.addEventListener('activate', event => event.waitUntil((async () => {
    const names = await caches.keys();
    await Promise.all(names.filter(name => name.startsWith(cachePrefix) && name !== cacheName)
        .map(name => caches.delete(name)));
    await self.clients.claim();
})()));

self.addEventListener('fetch', event => {
    const request = event.request;
    // Every feed request uses cache:no-store, including when the feed shares our origin.
    // Only exact application assets and navigation to the application root use the cache.
    if (request.method !== 'GET' || request.cache === 'no-store') return;
    const url = new URL(request.url);
    const isHome = request.mode === 'navigate' && url.origin === baseUrl.origin
        && (url.pathname === baseUrl.pathname || url.pathname === new URL(indexUrl).pathname);
    if (!isHome && !assetUrls.has(url.href)) return;
    event.respondWith((async () => {
        try {
            const cache = await caches.open(cacheName);
            const response = await cache.match(isHome ? indexUrl : request);
            if (response) {
                // Hosts can redirect index.html to the app root. A redirected cached
                // response cannot satisfy a navigation whose redirect mode is manual.
                if (isHome && response.redirected) {
                    return new Response(response.body, {
                        status: response.status, statusText: response.statusText, headers: response.headers
                    });
                }
                return response;
            }
        } catch {
            // Unavailable browser storage must not prevent an online load.
        }
        return fetch(request);
    })());
});
