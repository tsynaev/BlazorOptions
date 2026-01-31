window.indexedDbFramework = (function () {
    "use strict";

    var dbPromise = null;
    var openPromise = null;
    var lastError = null;

    function loadIdbScript() {
        return new Promise(function (resolve, reject) {
            if (window.idb && window.idb.openDB) {
                resolve(true);
                return;
            }

            import('/lib/idb/idb.js')
                .then(function (mod) {
                    window.idb = mod;
                    resolve(true);
                })
                .catch(function () {
                    var existing = document.querySelector('script[data-idb-lib]');
                    if (existing) {
                        existing.addEventListener('load', function () { resolve(true); }, { once: true });
                        existing.addEventListener('error', function () { reject(new Error('Failed to load idb library.')); }, { once: true });
                        return;
                    }

                    var script = document.createElement('script');
                    script.src = 'https://cdn.jsdelivr.net/npm/idb@7/build/iife/index-min.js';
                    script.async = false;
                    script.setAttribute('data-idb-lib', 'true');
                    script.onload = function () { resolve(true); };
                    script.onerror = function () { reject(new Error('Failed to load idb library.')); };
                    (document.head || document.getElementsByTagName('head')[0]).appendChild(script);
                });
        });
    }

    async function ensureIdb() {
        if (!window.idb || !window.idb.openDB) {
            await loadIdbScript();
        }
        if (!window.idb || !window.idb.openDB) {
            throw new Error("idb library is not loaded.");
        }
    }

    function createRange(range) {
        if (!range) {
            return undefined;
        }

        var hasLower = range.lower !== undefined && range.lower !== null;
        var hasUpper = range.upper !== undefined && range.upper !== null;
        if (!hasLower && !hasUpper) {
            return undefined;
        }

        if (hasLower && hasUpper) {
            return IDBKeyRange.bound(range.lower, range.upper, !!range.lowerOpen, !!range.upperOpen);
        }

        if (hasLower) {
            return IDBKeyRange.lowerBound(range.lower, !!range.lowerOpen);
        }

        return IDBKeyRange.upperBound(range.upper, !!range.upperOpen);
    }

    function keyPathEquals(a, b) {
        if (Array.isArray(a) || Array.isArray(b)) {
            return JSON.stringify(a || []) === JSON.stringify(b || []);
        }
        return a === b;
    }

    async function openDb(config) {
        await ensureIdb();

        if (!config || !config.name) {
            throw new Error("Database config with name is required.");
        }

        if (dbPromise) {
            return dbPromise;
        }

        if (openPromise) {
            return openPromise;
        }

        var version = config.version || 1;
        openPromise = window.idb.openDB(config.name, version, {
            upgrade: function (db, oldVersion, newVersion, transaction) {
                var stores = Array.isArray(config.stores) ? config.stores : [];
                for (var i = 0; i < stores.length; i++) {
                    var storeDef = stores[i];
                    if (!storeDef || !storeDef.name) {
                        continue;
                    }

                    var store;
                    if (!db.objectStoreNames.contains(storeDef.name)) {
                        store = db.createObjectStore(storeDef.name, {
                            keyPath: storeDef.keyPath,
                            autoIncrement: !!storeDef.autoIncrement
                        });
                    } else {
                        store = transaction.objectStore(storeDef.name);
                        if (!keyPathEquals(store.keyPath, storeDef.keyPath)) {
                            db.deleteObjectStore(storeDef.name);
                            store = db.createObjectStore(storeDef.name, {
                                keyPath: storeDef.keyPath,
                                autoIncrement: !!storeDef.autoIncrement
                            });
                        }
                    }

                    var indexes = Array.isArray(storeDef.indexes) ? storeDef.indexes : [];
                    for (var j = 0; j < indexes.length; j++) {
                        var index = indexes[j];
                        if (!index || !index.name || !index.keyPath) {
                            continue;
                        }

                        if (!store.indexNames.contains(index.name)) {
                            store.createIndex(index.name, index.keyPath, {
                                unique: !!index.unique,
                                multiEntry: !!index.multiEntry
                            });
                        }
                    }
                }
            }
        }).then(function (db) {
            dbPromise = Promise.resolve(db);
            openPromise = null;
            return db;
        }).catch(function (err) {
            openPromise = null;
            throw err;
        });

        return openPromise;
    }

    async function withStore(storeName, mode, action) {
        try {
            if (!dbPromise) {
                throw new Error("Database is not initialized. Call indexedDbFramework.openDb first.");
            }

            var db = await dbPromise;
            var tx = db.transaction(storeName, mode);
            var store = tx.objectStore(storeName);
            var result = await action(store, tx);
            await tx.done;
            return result;
        } catch (err) {
            lastError = err ? String(err) : "IndexedDB operation failed.";
            throw err;
        }
    }

    return {
        openDb: openDb,
        getLastError: function () { return lastError; },

        getAll: function (storeName) {
            return withStore(storeName, "readonly", function (store) {
                return store.getAll();
            });
        },

        getById: function (storeName, id) {
            return withStore(storeName, "readonly", function (store) {
                return store.get(id);
            });
        },

        add: function (storeName, value, key) {
            return withStore(storeName, "readwrite", function (store) {
                if (store.keyPath && key !== undefined && key !== null) {
                    return store.add(value);
                }
                if (key === undefined || key === null) {
                    return store.add(value);
                }
                return store.add(value, key);
            });
        },

        put: function (storeName, value, key) {
            return withStore(storeName, "readwrite", function (store) {
                if (store.keyPath && key !== undefined && key !== null) {
                    return store.put(value);
                }
                if (key === undefined || key === null) {
                    return store.put(value);
                }
                return store.put(value, key);
            });
        },

        delete: function (storeName, key) {
            return withStore(storeName, "readwrite", function (store) {
                return store.delete(key);
            });
        },

        clear: function (storeName) {
            return withStore(storeName, "readwrite", function (store) {
                return store.clear();
            });
        },

        count: function (storeName) {
            return withStore(storeName, "readonly", function (store) {
                return store.count();
            });
        },

        getAllByIndex: function (storeName, indexName, queryValue) {
            return withStore(storeName, "readonly", function (store) {
                return store.index(indexName).getAll(queryValue);
            });
        },

        getAllByIndexRange: function (storeName, indexName, range, limit) {
            return withStore(storeName, "readonly", function (store) {
                var keyRange = createRange(range);
                return store.index(indexName).getAll(keyRange, limit);
            });
        },

        putMany: function (storeName, values) {
            return withStore(storeName, "readwrite", function (store) {
                var items = Array.isArray(values) ? values : [];
                for (var i = 0; i < items.length; i++) {
                    store.put(items[i]);
                }
                return items.length;
            });
        },

        getByIndexRangeCursor: function (storeName, indexName, range, direction, limit) {
            return withStore(storeName, "readonly", async function (store) {
                var results = [];
                var keyRange = createRange(range);
                var cursor = await store.index(indexName).openCursor(keyRange, direction || "next");
                while (cursor) {
                    results.push(cursor.value);
                    if (limit && results.length >= limit) {
                        break;
                    }
                    cursor = await cursor.continue();
                }
                return results;
            });
        },

        getLatestMetaByIndexRange: function (storeName, indexName, range, direction) {
            return withStore(storeName, "readonly", async function (store) {
                var keyRange = createRange(range);
                var cursor = await store.index(indexName).openCursor(keyRange, direction || "prev");
                var meta = { timestamp: null, ids: [] };

                while (cursor) {
                    var value = cursor.value || {};
                    var timestamp = value.timestamp || value.Timestamp || 0;
                    if (timestamp && meta.timestamp === null) {
                        meta.timestamp = Number(timestamp);
                    }

                    if (meta.timestamp !== null && Number(timestamp) === meta.timestamp) {
                        var id = value.uniqueKey || value.UniqueKey || value.id || value.Id || '';
                        if (id) {
                            meta.ids.push(String(id));
                        }
                        cursor = await cursor.continue();
                        continue;
                    }

                    break;
                }

                return meta;
            });
        }
    };
})();
