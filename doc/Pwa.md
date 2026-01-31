# PWA Support

BlazorOptions includes a basic Progressive Web App setup so the UI can be installed on desktop or mobile.

## What's Included
- `wwwroot/manifest.json` with icons and app metadata.
- `wwwroot/service-worker.js` that caches the app shell and recent requests.
- `wwwroot/index.html` registers the service worker and links the manifest.

## Notes
- The first load needs network access to cache assets. After that, the app shell is available offline.
- If you update PWA assets, bump the `cacheVersion` value in `service-worker.js` to refresh caches.
