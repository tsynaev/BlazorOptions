# Data Pages

The Data section groups market analytics tools used for option/futures analysis.

## Routes

- `/data`: entry page with links to tools.
- `/volume-heatmap`: weekday/hour heatmap.
- `/open-interest`: open interest charts for calls and puts.
- `/volatility-skew`: implied volatility skew with expiration filters.

## Open Interest

- Shows separate charts for calls and puts.
- Axes:
- X: strike
- Y: expiration date
- Z/value: open interest
- Uses selected instrument filters.

## Volatility Skew

- Y-axis shows IV (%), X-axis shows strike.
- Line shows mark IV.
- Bid/ask markers are shown for single-expiration view.
- Multiple selected expirations are displayed as separate lines.
- Tooltips show IV and prices for mark/bid/ask.

## Related Docs

- [Volume Heatmap](VolumeHeatmap.md)
- Developer details: `doc/dev/Data.md`
