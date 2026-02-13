# Volume Heatmap

Route: `/volume-heatmap`

## What It Does

- Loads 1H klines with volume for the selected `base/quote` symbol.
- Uses a selectable rolling interval from current UTC time.
- Default interval is 6 months.
- Supports metric switch:
  - avg volume per hour
  - avg absolute diff between open and close per hour
- Aggregates total volume into a `7 x 24` matrix:
- Aggregates average 1H volume into a `7 x 24` matrix:
  - rows: weekday (`Mon..Sun`)
  - columns: hour (`00..23`, UTC)
- Marks the max-volume cell on the chart.
- Renders with `MudChart` using `ChartType.HeatMap` and legend labels enabled (`ChartOptions.ShowLegendLabels = true`).

## Data Source

- Asset pairs come from `IFuturesInstrumentsService.GetTradingPairsAsync()`.
- Candle volume comes from `ITickersService.GetCandlesWithVolumeAsync(...)`.
- Both are accessed through `IExchangeService` in the view model.
