const cacheVersion = 'bo-pwa-v1';
const appShell = [
    '/',
    '/index.html',
    '/manifest.json',
    '/css/app.css',
    '/BlazorOptions.styles.css',
    '/_content/MudBlazor/MudBlazor.min.css',
    '/icon-192.png',
    '/icon-512.png'
];

self.addEventListener('install', event => {
    event.waitUntil(
        caches.open(cacheVersion).then(cache => cache.addAll(appShell))
    );
    self.skipWaiting();
});

self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys().then(keys => Promise.all(
            keys.filter(key => key !== cacheVersion).map(key => caches.delete(key))
        ))
    );
    self.clients.claim();
});

self.addEventListener('fetch', event => {
    if (event.request.method !== 'GET') {
        return;
    }

    if (event.request.mode === 'navigate') {
        event.respondWith(
            fetch(event.request)
                .then(response => {
                    const copy = response.clone();
                    caches.open(cacheVersion).then(cache => cache.put(event.request, copy));
                    return response;
                })
                .catch(() => caches.match('/index.html'))
        );
        return;
    }

    event.respondWith(
        caches.match(event.request).then(cached => {
            if (cached) {
                return cached;
            }

            return fetch(event.request).then(response => {
                if (response && response.status === 200) {
                    const copy = response.clone();
                    caches.open(cacheVersion).then(cache => cache.put(event.request, copy));
                }
                return response;
            });
        })
    );
});
