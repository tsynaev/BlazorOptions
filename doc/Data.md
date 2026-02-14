# Data Pages

## Routes

- `/data`: entry page with links to data tools.
- `/volume-heatmap`: existing weekday/hour heatmap.
- `/open-interest`: 3D open-interest chart.
- `/volatility-skew`: placeholder page for future analytics.

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
