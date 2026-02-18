# Volume Heatmap

Route: `/volume-heatmap`

## What It Does

- Loads 1-hour market data for the selected symbol.
- Uses a rolling interval from current UTC time (default: last 6 months).
- Lets you switch metric:
- average volume per hour
- average absolute difference between open and close per hour
- Aggregates data into a `7 x 24` heatmap:
- rows: weekdays (`Mon..Sun`)
- columns: hours (`00..23`, UTC)
- Highlights stronger activity areas with color intensity.

## Related Docs

- [Data Pages](Data.md)
- Developer details: `doc/dev/Data.md`
