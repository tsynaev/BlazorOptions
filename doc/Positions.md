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
- Legs are displayed in status order: `Active`, `Order`, `New`, `Missing`.
- Include closed symbols and shift chart by closed net P/L when needed.
- See payoff curve, order markers, linked-order markers, and optional candles.
- See IV-based day range markers:
- `Day Max = day open + ATM call extrinsic` (next 3-4 week expiry)
- `Day Min = day open - ATM put extrinsic` (next 3-4 week expiry)
- Marker labels include the corresponding call/put IV used for the range.
- Included `Order` and `New` legs show order markers (`@price`) on the chart; option markers are placed at implied underlying execution level.
- Linked-order markers are shown for active legs and include all linked orders for that leg.
- Review per-leg P/L, IV, Greeks, and linked-order expected P/L.
- Portfolio header shows only total Greeks for included legs.
- Each non-futures leg card Greeks row includes a tiny unlabeled switch (with tooltip) to toggle between total leg Greek contribution and per-size Greek values.
- In non-live mode, when a chart/temporary price is selected, option Greeks in leg cards and portfolio totals are recalculated for that selected underlying price and valuation date.
- For legs that are already expired at the selected valuation date, both temp and expiry chart contributions are locked to that leg P/L at selected price (horizontal contribution). This includes dated futures; perpetual futures are never expired.
- `Order` status legs display current market mark values in the card.
- `New` legs with both empty entry price and empty IV show no entry price in the card.
- In leg edit dialog, clearing price or IV shows placeholders from current market mark values when available.
- IV is not auto-filled into the leg when empty; market IV is shown as placeholder until user sets a value.
- Expiration in leg edit dialog uses a combobox for both options and futures (futures include `Perpetual`).
- Only existing expiration dates for the current base/quote context can be selected.
- Leg edit dialog footer shows the resolved leg symbol for quick verification.
- For same-side linked orders, the card also shows the expected new average entry price after execution.
- Linked orders are labeled by kind when available: `TP`, `SL`, or `Conditional`.
- Linked order chips can be toggled to simulate execution; chart and leg P/L use only activated linked orders.
- Linked orders are available only on `Active` legs; `New`, `Order`, and `Missing` legs cannot keep linked orders.
- In chart payoff simulation, activated linked orders are treated as additional legs with their own entry prices; the active leg keeps its own entry price.

## Chart And Controls

- Header shows combined total P/L (temporary + closed).
- Header also shows Bybit wallet totals (`Equity`, `Wallet`, `Available`) when available.
- Header total P/L also shows portfolio P/L percent, using included non-futures leg entry value as denominator.
- `Live` and `Candles` are switched with chips.
- `Day Min/Max` chip toggles IV-based day range markers.
- `Orders` chip toggles order and linked-order markers on the chart.
- Valuation date/time is set with a full-width timeline bar from `now` to latest included-leg expiration.
- Expiration dates are shown as thick vertical markers with short labels.
- Clicking/tapping the bar selects valuation date/time at that point; `X` resets selection to now.
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
