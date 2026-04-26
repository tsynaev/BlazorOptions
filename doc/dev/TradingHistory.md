# Trading History

## Exchange adapter loading

The trading history page must fetch remote transaction pages through `IExchangeService.TransactionHistory`.

Keep the page and its view models exchange-agnostic after the raw payload has been normalized into `TradingTransactionRaw`. Persisted history, summaries, and recalculation continue to go through `ITradingHistoryPort`.

## Derivatives position-after handling

For Bybit `linear` and `inverse` `TRADE` entries, the raw payload `size` field is the authoritative position size after the fill.

Use that value when recalculating `Size after`, average price, and realized PnL. This prevents close-only or reduce-only fills from reopening an opposite position when the reported execution `qty` is larger than the remaining position.
