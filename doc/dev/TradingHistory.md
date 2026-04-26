# Trading History

## Exchange adapter loading

The trading history page must fetch remote transaction pages through `IExchangeService.TransactionHistory`.

Keep the page and its view models exchange-agnostic after the raw payload has been normalized into `TradingTransactionRaw`. Persisted history, summaries, and recalculation continue to go through `ITradingHistoryPort`.

Trading history storage is partitioned by exchange connection id. Each connection uses its own database file, including `bybit-main`. When the main connection is opened and the new scoped file is still empty, the server copies only the trading tables (`TradingHistoryEntries`, `TradingHistoryMeta`, `TradingDailySummaries`) out of the legacy `{userId}.db` into `{userId}.bybit-main.db`, then deletes those legacy trading tables after the scoped database has the migrated data. The legacy database keeps the `Positions` table in place.

## Derivatives position-after handling

For Bybit `linear` and `inverse` `TRADE` entries, the raw payload `size` field is the authoritative position size after the fill.

Use that value when recalculating `Size after`, average price, and realized PnL. This prevents close-only or reduce-only fills from reopening an opposite position when the reported execution `qty` is larger than the remaining position.
