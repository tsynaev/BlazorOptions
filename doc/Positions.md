# Positions

The Positions page is where you manage one position at a time and review its payoff.

## At A Glance

- Route: `/position/{positionId}`
- One position per page
- Data is saved on the server

## What You Can Do

- Manage multiple portfolios (collections) inside one position.
- Add legs manually, with quick add, from active exchange positions, or from open orders.
- When an active position chip is added as a leg, same-symbol open orders are automatically attached as linked orders to that leg.
- If only a `New` leg exists for a symbol, same-symbol exchange orders stay as separate order chips (they are not auto-linked to the `New` leg).
- Track leg statuses:
- `New`: local editable leg
- `Order`: open exchange order
- `Active`: active exchange position
- `Missing`: previously active leg no longer open on exchange
- Include closed symbols and shift chart by closed net P/L when needed.
- See payoff curve, order markers, linked-order markers, and optional candles.
- Order markers show order average price (`@price`) and option-order markers are placed at implied underlying execution level.
- Review per-leg P/L, IV, Greeks, and linked-order expected P/L.
- `Order` status legs display current market mark values in the card.
- For same-side linked orders, the card also shows the expected new average entry price after execution.
- Linked orders are labeled by kind when available: `TP`, `SL`, or `Conditional`.
- Linked order chips can be toggled to simulate execution; chart and leg P/L use only activated linked orders.

## Chart And Controls

- Header shows combined total P/L (temporary + closed).
- `Live` and `Candles` are switched with chips.
- User-selected chart range and candle interval are remembered per position.
- Default candles:
- interval: `1H`
- window: `48h`

## Mobile Notes

- Compact marker labels are used to reduce overlap.
- Controls are stacked for narrow screens.
- Available chips can scroll horizontally.
- Bottom navigation is used in portrait mode.

## Typical Workflow

1. Open a position from the dashboard.
2. Add or sync legs.
3. Adjust valuation date, selected price, live, and candles.
4. Review payoff and leg-level P/L.
5. Changes are saved automatically.

## Related Docs

- [Leg Parsing / Quick Add](LegParsing.md)
- [Closed Positions](ClosedPositions.md)
- Developer details: `doc/dev/Positions.md`
