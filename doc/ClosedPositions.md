# Closed Positions

## Overview
Closed positions now show cached calculation results immediately when a position is opened. Each row updates itself by fetching only recent trades since the last processed timestamp.

## Row behavior
- The table shows stored values from `ClosedPositionModel` on first render.
- A spinner appears next to the symbol while recalculation is running.
- The refresh icon next to the symbol triggers a full recalculation from the `Since` date (or the default lookback window if `Since` is empty).

## Incremental recalculation
Each closed position tracks:
- `LastProcessedTimestamp`
- `LastProcessedIdsAtTimestamp`
- cached calculation fields (size, averages, PnL, fees)

On recalculation, only trades newer than the last processed timestamp are loaded, and the cached totals are updated incrementally.
