window.tradingHistoryDb = (function () {
    const DB_NAME = 'blazor-options-db';
    const DB_VERSION = 3;
    const TRADE_STORE = 'trades';
    const META_STORE = 'tradeMeta';
    const DAILY_STORE = 'dailySummaries';

    let lastError = null;
    let dbPromise = null;

    function openDb() {
        if (dbPromise) {
            return dbPromise;
        }

        dbPromise = new Promise((resolve, reject) => {
            const request = indexedDB.open(DB_NAME, DB_VERSION);

            request.onupgradeneeded = function (event) {
                const db = event.target.result;

                if (!db.objectStoreNames.contains(TRADE_STORE)) {
                    const tradeStore = db.createObjectStore(TRADE_STORE, { keyPath: 'uniqueKey' });
                    tradeStore.createIndex('timestamp', 'timestamp', { unique: false });
                    tradeStore.createIndex('timestamp_unique', ['timestamp', 'uniqueKey'], { unique: false });
                    tradeStore.createIndex('symbol', 'symbolKey', { unique: false });
                    tradeStore.createIndex('symbol_timestamp', ['symbolKey', 'timestamp'], { unique: false });
                    tradeStore.createIndex('symbol_category_timestamp', ['symbolKey', 'categoryKey', 'timestamp'], { unique: false });
                }

                if (!db.objectStoreNames.contains(META_STORE)) {
                    db.createObjectStore(META_STORE, { keyPath: 'key' });
                }

                if (!db.objectStoreNames.contains(DAILY_STORE)) {
                    const dailyStore = db.createObjectStore(DAILY_STORE, { keyPath: 'key' });
                    dailyStore.createIndex('day', 'day', { unique: false });
                    dailyStore.createIndex('symbol_day', ['symbolKey', 'day'], { unique: false });
                }
            };

            request.onsuccess = function (event) {
                resolve(event.target.result);
            };

            request.onerror = function () {
                lastError = request.error ? String(request.error) : 'openDb error';
                reject(request.error);
            };
        });

        return dbPromise;
    }

    async function withStore(storeName, mode, action) {
        const db = await openDb();
        return new Promise((resolve, reject) => {
            const tx = db.transaction(storeName, mode);
            const store = tx.objectStore(storeName);
            const result = action(store, tx);

            tx.oncomplete = function () {
                resolve(result);
            };
            tx.onerror = function () {
                lastError = tx.error ? String(tx.error) : 'transaction error';
                reject(tx.error || new Error('IndexedDB transaction failed.'));
            };
            tx.onabort = function () {
                lastError = tx.error ? String(tx.error) : 'transaction aborted';
                reject(tx.error || new Error('IndexedDB transaction aborted.'));
            };
        });
    }

    async function ensureSchema() {
        const db = await openDb();
        const hasTrades = db.objectStoreNames.contains(TRADE_STORE);
        const hasMeta = db.objectStoreNames.contains(META_STORE);
        const hasDaily = db.objectStoreNames.contains(DAILY_STORE);
        if (hasTrades && hasMeta && hasDaily) {
            return;
        }

        db.close();
        dbPromise = null;
        await new Promise((resolve, reject) => {
            const request = indexedDB.deleteDatabase(DB_NAME);
            request.onsuccess = function () { resolve(); };
            request.onerror = function () {
                lastError = request.error ? String(request.error) : 'deleteDatabase error';
                reject(request.error);
            };
            request.onblocked = function () { resolve(); };
        });
        await openDb();
    }

    async function init() {
        await ensureSchema();
    }

    async function getMeta() {
        return withStore(META_STORE, 'readonly', (store) => {
            return new Promise((resolve, reject) => {
                const request = store.get('state');
                request.onsuccess = function () {
                    resolve(request.result || null);
                };
                request.onerror = function () {
                    reject(request.error);
                };
            });
        });
    }

    async function setMeta(meta) {
        return withStore(META_STORE, 'readwrite', (store) => {
            store.put({ key: 'state', value: meta });
        });
    }

    async function replaceDailySummaries(items) {
        if (!items || !Array.isArray(items) || items.length === 0) {
            return 0;
        }

        return withStore(DAILY_STORE, 'readwrite', (store, tx) => {
            let written = 0;
            lastError = null;
            try {
                store.clear();
            } catch (err) {
                lastError = err ? String(err) : 'store.clear error';
            }
            for (let i = 0; i < items.length; i++) {
                const item = items[i] || {};
                if (!item.key) {
                    item.key = `${item.symbolKey || ''}|${item.day || ''}`;
                }
                try {
                    store.put(item);
                } catch (err) {
                    lastError = err ? String(err) : 'store.put error';
                }
                written++;
            }
            return written;
        });
    }

    async function getCount() {
        return withStore(TRADE_STORE, 'readonly', (store) => {
            return new Promise((resolve, reject) => {
                const request = store.count();
                request.onsuccess = function () {
                    resolve(request.result || 0);
                };
                request.onerror = function () {
                    reject(request.error);
                };
            });
        });
    }

    async function putTrades(trades) {
        const isArray = Array.isArray(trades);
        const normalized = isArray
            ? trades
            : (trades && Array.isArray(trades.$values) ? trades.$values
                : (trades && Array.isArray(trades.values) ? trades.values
                    : (trades && Array.isArray(trades.items) ? trades.items : null)));
        if (!isArray && normalized) {
            trades = normalized;
        } else if (!isArray && trades && typeof trades === 'object') {
            const keys = Object.keys(trades);
            const looksLikeTrade = keys.includes('timestamp') || keys.includes('uniqueKey') || keys.includes('tradeId');
            if (looksLikeTrade) {
                trades = [trades];
            }
        }

        if (!trades || !Array.isArray(trades) || trades.length === 0) {
            return 0;
        }

        return withStore(TRADE_STORE, 'readwrite', (store) => {
            let written = 0;
            lastError = null;
            const sample = trades[0] || {};
            for (let i = 0; i < trades.length; i++) {
                const trade = trades[i] || {};
                const original = trade;
                trade.id = trade.id || trade.Id || trade.uniqueKey || trade.UniqueKey;
                trade.uniqueKey = trade.uniqueKey || trade.UniqueKey || trade.id || trade.Id;
                trade.timestamp = trade.timestamp || trade.Timestamp || trade.timeStamp || trade.TimeStamp || trade.time || trade.Time;
                trade.timestamp = Number.isFinite(trade.timestamp) ? trade.timestamp : Number(trade.Timestamp || trade.timestamp || 0);
                trade.symbolKey = trade.symbolKey || trade.SymbolKey || (trade.symbol ? String(trade.symbol).toUpperCase() : (trade.Symbol ? String(trade.Symbol).toUpperCase() : ''));
                trade.categoryKey = trade.categoryKey || trade.CategoryKey || (trade.category ? String(trade.category).toLowerCase() : (trade.Category ? String(trade.Category).toLowerCase() : ''));
                if (!trade.uniqueKey) {
                    trade.uniqueKey = `auto_${Date.now()}_${i}`;
                }
                if (!Number.isFinite(trade.timestamp) || trade.timestamp <= 0) {
                    trade.timestamp = Date.now();
                }
                try {
                    store.put(trade);
                } catch (err) {
                    lastError = err ? String(err) : 'store.put error';
                }
                written++;
            }
            return written;
        });
    }

    async function fetchLatest(limit) {
        const results = [];
        return withStore(TRADE_STORE, 'readonly', (store) => {
            return new Promise((resolve, reject) => {
                const index = store.index('timestamp_unique');
                const request = index.openCursor(null, 'prev');
                request.onsuccess = function (event) {
                    const cursor = event.target.result;
                    if (!cursor || results.length >= limit) {
                        resolve(results);
                        return;
                    }
                    const value = cursor.value;
                    if (value && !value.id && (value.uniqueKey || value.UniqueKey)) {
                        value.id = value.uniqueKey || value.UniqueKey;
                    }
                    results.push(value);
                    cursor.continue();
                };
                request.onerror = function () {
                    reject(request.error);
                };
            });
        });
    }

    async function fetchAny(limit) {
        const results = [];
        return withStore(TRADE_STORE, 'readonly', (store) => {
            return new Promise((resolve, reject) => {
                const request = store.openCursor(null, 'prev');
                request.onsuccess = function (event) {
                    const cursor = event.target.result;
                    if (!cursor || results.length >= limit) {
                        resolve(results);
                        return;
                    }
                    const value = cursor.value;
                    if (value && !value.id && (value.uniqueKey || value.UniqueKey)) {
                        value.id = value.uniqueKey || value.UniqueKey;
                    }
                    results.push(value);
                    cursor.continue();
                };
                request.onerror = function () {
                    reject(request.error);
                };
            });
        });
    }

    async function fetchBefore(beforeTimestamp, beforeKey, limit) {
        const results = [];
        const hasCursor = Number.isFinite(beforeTimestamp) && beforeKey;
        const upperBound = hasCursor
            ? IDBKeyRange.upperBound([Number(beforeTimestamp), String(beforeKey)], true)
            : null;

        return withStore(TRADE_STORE, 'readonly', (store) => {
            return new Promise((resolve, reject) => {
                const index = store.index('timestamp_unique');
                const request = index.openCursor(upperBound, 'prev');
                request.onsuccess = function (event) {
                    const cursor = event.target.result;
                    if (!cursor || results.length >= limit) {
                        resolve(results);
                        return;
                    }
                    const value = cursor.value;
                    if (value && !value.id && (value.uniqueKey || value.UniqueKey)) {
                        value.id = value.uniqueKey || value.UniqueKey;
                    }
                    results.push(value);
                    cursor.continue();
                };
                request.onerror = function () {
                    reject(request.error);
                };
            });
        });
    }

    async function fetchBySymbol(symbolKey, categoryKey) {
        const results = [];
        return withStore(TRADE_STORE, 'readonly', (store) => {
            return new Promise((resolve, reject) => {
                let index = store.index('symbol_timestamp');
                let range = null;

                if (categoryKey) {
                    index = store.index('symbol_category_timestamp');
                    range = IDBKeyRange.bound([symbolKey, categoryKey, 0], [symbolKey, categoryKey, Number.MAX_SAFE_INTEGER]);
                } else {
                    range = IDBKeyRange.bound([symbolKey, 0], [symbolKey, Number.MAX_SAFE_INTEGER]);
                }

                const request = index.openCursor(range, 'prev');
                request.onsuccess = function (event) {
                    const cursor = event.target.result;
                    if (!cursor) {
                        resolve(results);
                        return;
                    }
                    const value = cursor.value;
                    if (value && !value.id && (value.uniqueKey || value.UniqueKey)) {
                        value.id = value.uniqueKey || value.UniqueKey;
                    }
                    results.push(value);
                    cursor.continue();
                };
                request.onerror = function () {
                    reject(request.error);
                };
            });
        });
    }

    async function fetchAllAsc() {
        const results = [];
        return withStore(TRADE_STORE, 'readonly', (store) => {
            return new Promise((resolve, reject) => {
                const index = store.index('timestamp_unique');
                const request = index.openCursor(null, 'next');
                request.onsuccess = function (event) {
                    const cursor = event.target.result;
                    if (!cursor) {
                        resolve(results);
                        return;
                    }
                    const value = cursor.value;
                    if (value && !value.id && (value.uniqueKey || value.UniqueKey)) {
                        value.id = value.uniqueKey || value.UniqueKey;
                    }
                    results.push(value);
                    cursor.continue();
                };
                request.onerror = function () {
                    reject(request.error);
                };
            });
        });
    }

    return {
        init,
        getLastError: function () { return lastError; },
        getMeta,
        setMeta,
        getCount,
        putTrades,
        replaceDailySummaries,
        fetchLatest,
        fetchAny,
        fetchBefore,
        fetchBySymbol,
        fetchAllAsc
    };
})();
