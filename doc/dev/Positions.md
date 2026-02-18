# Positions (Technical)

Route: `/position/{positionId}`

## Scope

- One position per page.
- Position state is loaded by id and persisted through the server positions API.
- Exchange integrations are accessed through `IExchangeService` child interfaces.

## View Model Responsibilities

- `PositionViewModel`
- Owns position state (selected/live price, valuation date, candles, chart data).
- Coordinates chart refresh batching and persistence updates.
- Manages exchange subscriptions while the page is open.
- `LegsCollectionViewModel`
- Owns one portfolio collection.
- Synchronizes legs with exchange positions/orders and maintains available chips.
- `LegViewModel`
- Owns leg-level state (mark/bid/ask/IV/Greeks/PnL/status/linked orders).
- `ClosedPositionsViewModel`
- Owns closed symbols list, totals, recalculation state, and include-in-chart behavior.

## Data Sources

- `ITickersService`
- `SubscribeAsync` supports snapshot + live updates.
- `UpdateTickersAsync(...)` pushes refresh updates to subscribers.
- Cached values are pushed immediately on new subscriptions.
- `IOptionsChainService`
- Uses the same snapshot/live contract as tickers service.
- Cached values are pushed immediately on new subscriptions.
- `IPositionsService` and `IOrdersService`
- Page subscribes to both streams while open.
- Updates are merged into legs/chips state without full reload.

## Synchronization Rules

- Open orders update order legs in place.
- Closed orders remove matching order legs/chips unless converted to active positions.
- If an order disappears and a matching position appears, the leg becomes `Active`.
- Active position sync is processed separately from order sync to avoid status crossover.
- Closed active positions are copied to closed positions, then marked `Missing` and excluded in active legs.

## Chart Behavior

- Recalculate chart only for chart-relevant changes.
- Persist user chart range per position.
- Persist selected candle interval and restore as rolling range from current time.
- Candles default to `1H` and `48h` window.
- Range changes load only missing candle segments.

## Marker Rules

- Order legs: compact order markers.
- Linked orders: compact markers with expected P/L and sign-based color.
- Selected price marker is `null` when no selected/live price is available.
