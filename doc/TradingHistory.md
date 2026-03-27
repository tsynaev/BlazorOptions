# Trading History

## Symbol dialog summary

When you open a symbol from trading history, the dialog now includes a `Summary` tab.

The summary groups fully closed round-trip cycles for that symbol and shows:

- Direction as `Open/Close Long` or `Open/Close Short`
- Entry time range from the first opening fill to the last opening fill
- Close time range from the first closing fill to the last closing fill
- Entry price as the VWAP of all opening fills
- Size as the absolute opened size of the cycle
- Close price as the VWAP of all closing fills
- Total fees across all fills in the cycle
- Net PnL after fees

Closed cycles are listed with close price and net PnL. Open positions are also listed, with close-specific columns left empty until the cycle closes.
When several fills share the same exchange timestamp, the summary preserves the original fill order so separate flat-to-flat cycles are not merged or dropped.

## Position trades

On the position page, the `Trades` panel now shows the same trade-cycle summary format across all tracked symbols in the position.

The main table combines all tracked symbols into one timeline and adds a `Symbol` column.
The `Tracked Symbols` section is used only to manage which symbols belong to the position and from which date/time their trades should be included. By default, the date comes from the position creation time, and it can be left empty for a symbol.

The Trading History feature pulls and analyzes Bybit transaction logs, with paging for fast initial load.

## Core capabilities
- Load latest transactions from Bybit by category (linear, inverse, spot, option).
- Backfill older history using stored cursors and registration date.
- Virtualized grid with server/page-backed loading for large histories.
- Summary tables by symbol and by settle coin (fees and realized PnL), loaded from the server.
- Trade detail dialog per symbol (optionally filtered by a since date), with raw JSON view and one-click markdown export.
- Manual recalculation of realized PnL and running totals (server-side).

## Performance behavior
- On page load, only the most recent 100 records are fetched and shown.
- The "Recent transactions" grid orders by trade timestamp (descending), then id.
- As you scroll, the next 100 records are loaded incrementally.
- Calculated fields are stored so reloading is fast.
- The client streams transaction pages to the server as they are fetched from Bybit, and the server computes calculated fields and deduplicates entries. The client does not calculate PnL.
- Daily summaries are generated and stored on the server during recalculation. The UI requests daily PnL by date range from the server, along with symbol/coin summaries.
- The trade detail dialog shows symbol trades in ascending timestamp order so position changes can be read from open to close.

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
