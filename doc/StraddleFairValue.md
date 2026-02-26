# Straddle Fair Value

Route: `/straddle-fair`

## Purpose

- Estimates fair value for a 1-week ATM straddle from historical completed weekly ranges.
- Compares fair value with current market ATM call + put.
- Shows edge as `Actual - Fair` and percent difference.

## Inputs

- Underlying symbol (default `ETHUSDT`)
- Weeks to average (default `6`)
- Method:
  - `Range`
  - `Parkinson`

## Data Used

- Current underlying price from market candles.
- Last `N` completed weeks from hourly candles (current partial week excluded).
- ATM option prices from current options chain for expiry closest to 1 week.

## Weekly Formulas

- Range method:
  - `D_i = (H_i - L_i) / Close_i`
  - `sigma_week = mean(D_i) / 1.596`
- Parkinson method:
  - `sigma_i = ln(H_i / L_i) / sqrt(4 ln 2)`
  - `sigma_week = mean(sigma_i)`

## Fair Straddle

- `E|dS| = S * sigma_week * sqrt(2/pi)`
- `FairStraddle = E|dS|`

Constants:
- `sqrt(2/pi) = 0.7978845608`
- `Range->sigma factor = 1.596`

## Output

- Historical weekly table (`H`, `L`, `Close`, `D_i` or `sigma_i`)
- Summary metrics:
  - avg range
  - weekly sigma
  - `S`
  - fair straddle
  - actual straddle
  - difference and difference percent
- Edge highlight is green/red by sign of `Actual - Fair`.
