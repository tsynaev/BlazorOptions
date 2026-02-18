# Data Tools (Technical)

## Routes

- `/data`: navigation page for analytics tools.
- `/volume-heatmap`: weekday/hour aggregates.
- `/open-interest`: open-interest surfaces split by call/put.
- `/volatility-skew`: IV skew by strike with expiration selection.

## Open Interest

- Source: options chain tickers (`IExchangeService.OptionsChain`).
- Input metric: `OptionChainTicker.OpenInterest`.
- Dimensions:
- X axis: strike
- Y axis: expiration date
- Z/value axis: aggregated open interest for strike + expiration
- Rendered as separate call and put charts.

## Volatility Skew

- Source: options chain tickers (`IExchangeService.OptionsChain`).
- Instrument selector is built from configured option base/quote pairs.
- Dimensions:
- X axis: strike
- Y axis: IV %
- Main line: mark IV
- Single-expiration mode:
- bid/ask IV are rendered as triangle markers
- tooltip contains mark/bid/ask IV and prices
- Multi-expiration mode:
- one line per expiration
- marker overlays are suppressed for readability

## Volume Heatmap

- Trading pairs source: `IFuturesInstrumentsService` through `IExchangeService`.
- Candle source: `ITickersService.GetCandlesWithVolumeAsync(...)`.
- Default interval: last 6 months from current UTC time.
- Supported metrics:
- average volume per weekday/hour bucket
- average absolute open-close difference per weekday/hour bucket
- Aggregation target:
- `7 x 24` matrix (`Mon..Sun`, `00..23` UTC)
- Min/max values are recalculated on every reload.
