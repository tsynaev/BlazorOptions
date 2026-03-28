# Straddle Fair Value

Route: `/straddle-fair`

## Purpose

- Estimates fair value for an ATM straddle for the selected tenor from historical completed weekly ranges.
- Compares fair value with the current market ATM call + put for the selected tenor.
- Shows edge as `Fair - Actual` and percent difference.

## Inputs

- Underlying symbol (default `ETHUSDT`)
- Weeks to average (default `6`)
- Tenor:
  - `1W`
  - `2W`
  - `3W`
  - `1M`
  - `3M`
- Method:
  - `Range`
  - `Parkinson`

## Data Used

- Current underlying price from market candles.
- Last `N` completed weeks from hourly candles (current partial week excluded).
- ATM option prices from current options chain for expiry closest to the selected tenor.

## Weekly Formulas

- Range method:
  - `D_i = (H_i - L_i) / Close_i`
  - `sigma_week = mean(D_i) / 1.596`
- Parkinson method:
  - `sigma_i = ln(H_i / L_i) / sqrt(4 ln 2)`
  - `sigma_week = mean(sigma_i)`

## Fair Straddle

- `E|dS| = S * sigma_week * sqrt(2/pi)`
- `sigma_tenor = sigma_week * sqrt(tenor_days / 7)`
- `FairStraddle = S * sigma_tenor * sqrt(2/pi)`

Constants:
- `sqrt(2/pi) = 0.7978845608`
- `Range->sigma factor = 1.596`

## Output

- Historical weekly table (`H`, `L`, `Close`, `D_i` or `sigma_i`)
- Summary metrics:
  - avg range
  - tenor sigma used for pricing
  - `S`
  - fair straddle
  - actual straddle
  - difference and difference percent
- Edge highlight is green/red by sign of `Fair - Actual`.
