# Trading History

## Exchange adapter loading

The trading history page must fetch remote transaction pages through `IExchangeService.TransactionHistory`.

Keep the page and its view models exchange-agnostic after the raw payload has been normalized into `TradingTransactionRaw`. Persisted history, summaries, recalculation, realtime execution sync, and cache-backed history reads must sit behind `IExchangeService.TransactionHistory`.

At app startup, the browser creates one background exchange facade per saved exchange connection and calls `IExchangeService.TransactionHistory.InitializeAsync()`. The exchange-specific transaction-history service owns websocket execution subscriptions, startup catch-up sync, reconnect catch-up sync, saving to `ITradingHistoryPort`, and post-save subscriber notifications.

Subscribers must not read `ITradingHistoryPort` directly. They subscribe to `IExchangeService.TransactionHistory.SubscribeExecutionsAsync(...)` and then load cached history back through `IExchangeService.TransactionHistory.LoadBySymbolAsync(...)` or `LoadBySymbolsAsync(...)`.

`ITradingHistoryPort` is the persisted cache and deduplication layer in front of the exchange. Exchange-specific transaction-history services may use different categories, symbol shapes, or sync windows internally, but they must notify subscribers only after new executions have already been saved to `ITradingHistoryPort`.

Trading history storage is partitioned by exchange connection id. Each connection uses its own database file, including `bybit-main`. When the main connection is opened and the new scoped file is still empty, the server copies only the trading tables (`TradingHistoryEntries`, `TradingHistoryMeta`, `TradingDailySummaries`) out of the legacy `{userId}.db` into `{userId}.bybit-main.db`, then deletes those legacy trading tables after the scoped database has the migrated data. The legacy database keeps the `Positions` table in place.

Server writes for trading history must be idempotent by `TradingHistoryEntry.Id`. Concurrent browsers may post the same execution batch; the store inserts only previously unseen ids and returns the inserted ids so exchange-side history services can emit only real new-execution notifications.

Realtime position trade summaries must stay symbol-scoped. The position-page `TradesViewModel` listens to `PositionViewModel.ExchangeService.TransactionHistory` and reloads only when the saved execution set intersects the symbols tracked in `ClosedPositionsViewModel`.

## Derivatives position-after handling

For Bybit `linear` and `inverse` `TRADE` entries, the raw payload `size` field is the authoritative position size after the fill.

Use that value when recalculating `Size after`, average price, and realized PnL. This prevents close-only or reduce-only fills from reopening an opposite position when the reported execution `qty` is larger than the remaining position.
