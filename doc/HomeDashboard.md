# Home Dashboard

Route: `/`

## Overview

- Home page now renders dashboard cards for all saved positions.
- Cards are grouped by `base/quote` asset pair.
- Each card shows:
  - position name in `base/quote - name` format
  - total P/L
  - P/L percent from entry value
  - mini line chart (payoff snapshot)
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
- Current P/L: payoff at midpoint price of saved chart x-range (or chart midpoint fallback).
- Total P/L: current P/L + closed net P/L when closed positions are included.
