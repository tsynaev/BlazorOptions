# Data Pages

## Routes

- `/data`: entry page with links to data tools.
- `/volume-heatmap`: existing weekday/hour heatmap.
- `/open-interest`: 3D open-interest chart.
- `/volatility-skew`: strike/price skew chart with expiration chip filtering.

## Open Interest

- Source: `IExchangeService.OptionsChain` tickers.
- Metric: `OptionChainTicker.OpenInterest` parsed from options ticker payload.
- Charts:
  - Calls
  - Puts
- Axes (for each chart):
  - X: strike
  - Y: expiration date
  - Z: aggregated open interest (sum for strike + expiration)
- Filters: base/quote selectors on page.

## Volatility Skew

- Source: `IExchangeService.OptionsChain` tickers.
- Axes:
  - X: strike
  - Y: IV %
- Data:
  - line = mark IV
  - bid/ask triangles = bid IV / ask IV (only when exactly one expiration is selected)
  - tooltip shows price and IV for mark/bid/ask
  - additional 3D surface:
    - X: strike
    - Y: expiration date
    - Z: IV %
- Filters:
  - instrument selector (`BASE/QUOTE`, from Bybit settings option base/quote lists)
  - Call/Put switch
  - expiration chips with add/remove behavior
