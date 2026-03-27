# Positions (Technical)

Route: `/position/{positionId}`

## Scope

- One position per page.
- Position state is loaded by id and persisted through the server positions API.
- Exchange integrations are accessed through `IExchangeService` child interfaces.
- `PositionModel` stores only the current active portfolio state (`Legs`, `Color`).
- Position payload JSON is versioned.
- The persistence layer always saves the latest payload version and converts older payloads during load.

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
- `ExchangePriceUpdate` carries both `MarkPrice` and `IndexPrice`; consumers must choose the correct field instead of treating one price as both mark and underlying.
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
- Same-symbol open orders are auto-linked only to `Active` legs; `New` legs do not consume order chips.
- Symbol is re-formatted on every editable leg field change to keep symbol text aligned with current leg state.
- When symbol changes (for example futures expiry/perpetual switch), leg ticker subscription is refreshed so mark/placeholder price comes from that exact leg symbol.
- Position-level price context prefers the single distinct included dated-future symbol when one exists; otherwise it falls back to the base/quote symbol. This keeps live price, initial snapshot price, and candles aligned with the selected contract without guessing across multiple dated futures legs.
- `LegsCollectionViewModel` broadcasts one shared `IndexPrice` for the current base/quote pair to every leg. Per-leg valuation differences come from each leg's own `Spread`, not from per-expiration index remapping.
- Pricing-context rebroadcast in `LegsCollectionViewModel` is synchronous because the page typically works with fewer than 10 legs; the extra async/yield path was removed as unnecessary complexity.
- `LegViewModel.IndexPrice` is the chart/user index context shared across the current base/quote pair. In live mode, displayed mark uses live market mark. In non-live mode, each leg preserves its own observed `Spread` versus index and prices from `IndexPrice + Spread`:
- options: `underlying = IndexPrice + Spread`, then Black-Scholes uses that underlying
- futures: `mark = IndexPrice + Spread`
- Futures UI display is mark-first: leg cards and edit-dialog price placeholders prefer `MarkPrice` over `IndexPrice`.

## Chart Behavior

- Recalculate chart only for chart-relevant changes.
- Persist user chart range per position.
- Persist selected candle interval and restore as rolling range from current time.
- Candles default to `1H` and `48h` window.
- Range changes load only missing candle segments.

## Responsive Layout

- `Position.razor` should remain a layout shell that chooses between desktop and mobile composition.
- Repeated section markup is split into shared components (`PositionChartSettingsPanel`, `PositionNotesPanel`, `PositionEquityPanel`, `PositionMoreActionsPanel`, `PortfoliosPanel`) so desktop/mobile layout changes do not duplicate business-facing UI blocks.
- Panel components should receive one child view model each (`PositionChartSettingsPanelViewModel`, `PositionNotesPanelViewModel`, `PositionEquityPanelViewModel`, `PositionMoreActionsPanelViewModel`) instead of many primitive parameters and callbacks.
- Mobile keeps a fixed top shell (title, chart, tabs) and renders section content below it; desktop keeps the chart column and details column as separate regions.
- The position page works with a single portfolio backed by `PositionModel`. Legacy unversioned payloads that still use `Collections` are converted into `PositionModel.Legs` before the page view model is created.

## Marker Rules

- Included `Order` and `New` legs: compact order markers.
- Linked orders: compact markers with expected P/L and sign-based color.
- Marker visibility toggles are controlled in `PositionViewModel`:
- `ShowDayMinMaxMarkers` for IV day-range markers
- `ShowOrderMarkers` for order + linked-order markers
- IV day range markers are added when data is available:
- day open is taken from first 1H candle of valuation day (or today for future valuation dates)
- expiry is nearest in the 3-4 week window (fallback: nearest available)
- ATM strike is selected from strikes that have both call and put
- marker prices use extrinsic-only premium:
- `Day Max = open + max(callPrice - intrinsicCall, 0)`
- `Day Min = open - max(putPrice - intrinsicPut, 0)`
- marker labels also show call/put IV from the selected ATM tickers.
- Linked order marker text switches by scenario:
- closing/reducing order => `PnL`
- opening/increasing order => projected `Avg` entry
- Linked-order markers are rendered for active legs and include all linked orders on those legs.
- Linked order simulation is user-toggleable per linked order (`IsActivated`); payoff chart calculations include each activated linked order as a separate synthetic leg with its own entry price.
- The original active leg is always calculated with its own entry price; linked-order activation does not replace that base leg entry.
- Only `Active` legs may own linked orders. Transitioning a leg to `New`, `Order`, or `Missing` clears linked orders for that leg.
- Selected price marker is `null` when no selected/live price is available.

## Linked Order Projection

- Opposite-side linked orders show expected closing P/L contribution.
- Same-side linked orders show projected weighted average entry after execution.
- Order kind labels are inferred from exchange payload:
- `TP` from `stopOrderType` containing `TakeProfit`
- `SL` from `stopOrderType` containing `StopLoss`/`Stop`
- `Conditional` when order status is `Untriggered` and no TP/SL tag is present
- Open-order chips are suppressed when the same order is already represented as a linked order on an active leg.
- Option linked-order markers on payoff chart use an implied underlying-price solve (Black-Scholes inversion) from order option price.
- `Order` status leg cards show live/current market mark values (not execution-derived synthetic marks).
- Leg edit dialog uses mark-based placeholders for cleared price/IV fields and uses `MudSelect` expiration editing for options and futures.
- Expiration updates are validated in `LegViewModel` so only currently available expirations are accepted (`null` is allowed only for futures/perpetual).
