# Home Dashboard

Route: `/`

## Overview

- Home page now renders dashboard cards for all saved positions.
- Cards are grouped by `base/quote` asset pair.
- Cards and non-chart data are shown first; mini charts are then rendered asynchronously one-by-one to keep initial page load responsive.
- While a card chart is warming up, the card shows a chart skeleton placeholder.
- Each card shows:
  - position name in `base/quote - name` format
  - total P/L
  - P/L percent from entry value
  - mini line chart (payoff snapshot)
  - mini chart includes both expiry P/L and temp/theoretical P/L lines
  - included legs in quick-add style
  - notes
  - leg chips with loss-severity colors:
    - green: PnL >= 0%
    - yellow: loss < 10%
    - amber: loss 10-20%
    - orange: loss 20-30%
    - red: loss >= 30% (critical)
  - chip tooltip with leg details (type, size, entry, mark, PnL, PnL%)

## Calculation Notes

- Entry value: sum of `abs(size * price)` for included legs with price.
- Futures legs use `0` entry value for PnL% scaling.
- Current temp P/L: theoretical payoff at midpoint price of saved chart x-range (or chart midpoint fallback).
- Total temp P/L: current temp P/L + closed net P/L.
- Mini chart P/L curve is shifted by closed net P/L (realized P/L baseline).
- Leg chip severity uses Account Settings thresholds:
  - options: `Max loss for options (%)`
  - futures: `Max loss for futures (%)`
  - for negative PnL%, chips transition from yellow to orange and become red at/above max loss.
