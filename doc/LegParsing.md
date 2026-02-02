# Leg Parsing

The leg parsing feature supports quick input for creating legs in positions and collections.

## Supported input
- JSON arrays or entries separated by commas, semicolons, or new lines.
- Size and action keywords:
  - `+1 C 3400`
  - `-1 P 3200`
  - `buy ETH-27MAR26-2200-P`
  - `sell ETH-27MAR26-1800-P`
- Futures shortcuts:
  - `3` is treated as a futures leg size (buy 3).
  - `-3 F @2350` sets size and entry price.

## Defaults
- Missing size uses the per-asset default size stored in local storage.
- Missing expiration uses the per-asset default expiration:
  - The next expiry at least 7 days out.
  - If a stored default date is in the past, it is refreshed automatically.

## UI preview
- Previews are shown as colored chips:
  - Buy = green
  - Sell = red
- Chips appear in:
  - Create-position dialog
  - Quick-add panel
