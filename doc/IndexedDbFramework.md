# IndexedDB Framework (idb)

This app includes a small IndexedDB framework built on top of the `idb` library.

## What it provides
- Open/create databases with stores and indexes.
- Simple CRUD helpers for object stores.
- Range and index queries with `IDBKeyRange` support.
- Cursor-based range scans when you need ordered traversal.

## Script dependencies
The framework dynamically imports a local `idb` module from `wwwroot/lib/idb`:

```html
<script src="js/indexeddb-framework.js"></script>
```

Local files:
- `wwwroot/lib/idb/idb.js`
- `wwwroot/lib/idb/wrap-idb-value.js`

## Usage pattern
1) Call `indexedDbFramework.openDb` with a schema (stores + indexes).
2) Use the helper functions (`getAllByIndexRange`, `getByIndexRangeCursor`, etc.) for range or index queries.
3) Handle `indexedDbFramework.getLastError()` for diagnostics if needed.
