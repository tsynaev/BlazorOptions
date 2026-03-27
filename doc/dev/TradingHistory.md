# Trading History

## Derivatives position-after handling

For Bybit `linear` and `inverse` `TRADE` entries, the raw payload `size` field is the authoritative position size after the fill.

Use that value when recalculating `Size after`, average price, and realized PnL. This prevents close-only or reduce-only fills from reopening an opposite position when the reported execution `qty` is larger than the remaining position.
