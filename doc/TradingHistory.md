# Trading History

The Trading History feature pulls and analyzes Bybit transaction logs, with paging for fast initial load.

## Core capabilities
- Load latest transactions from Bybit by category (linear, inverse, spot, option).
- Backfill older history using stored cursors and registration date.
- Virtualized grid with server/page-backed loading for large histories.
- Summary tables by symbol and by settle coin (fees and realized PnL), loaded from the server.
- Trade detail dialog per symbol (optionally filtered by a since date), with raw JSON view.
- Manual recalculation of realized PnL and running totals (server-side).

## Performance behavior
- On page load, only the most recent 100 records are fetched and shown.
- The "Recent transactions" grid orders by trade timestamp (descending), then id.
- As you scroll, the next 100 records are loaded incrementally.
- Calculated fields are stored so reloading is fast.
- The client streams transaction pages to the server as they are fetched from Bybit, and the server computes calculated fields and deduplicates entries. The client does not calculate PnL.
- Daily summaries are generated and stored on the server during recalculation. The UI requests daily PnL by date range from the server, along with symbol/coin summaries.

## Decimal parsing
- Some provider values (for example, `change`) can exceed decimal range.
- Trading history values are parsed with a safe decimal converter that falls back to `0` on overflow or invalid data to avoid UI crashes.

## Persistence and sync
- Trading history is available only for authenticated users.
- The app uses the server Web API with SQLite storage for trading history.
- When authenticated, new trades are published as events to the server.
- The server deduplicates trades by unique key to avoid duplicates from multiple devices.
- Updates from other devices are delivered in real time via SignalR.

## Typical workflow
1) Set your registration date (first-time load).
2) Click Load to fetch new Bybit transactions and calculate fields.
3) Scroll to load older pages.
4) Use Recalculate if you want to recompute PnL across all entries.
