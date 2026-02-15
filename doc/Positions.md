# Positions

The Positions feature lets you build, manage, and visualize option strategies and related legs.

## Core capabilities
- Create and manage multiple positions (portfolios) with custom names and colors.
- Add, remove, and reorder collections of legs within a position.
- Add legs manually or by parsing quick input (e.g., size/type/strike/expiry, defaulting to the next expiry at least 7 days out).
- Create a new position with a quick leg list (JSON array or comma/semicolon/newline separated entries) and per-asset defaults for size/expiration stored in local storage.
- Duplicate collections to iterate on variants.
- Load live positions from Bybit (options/linear/inverse) into a collection.
- Create positions from an Active Positions panel that mirrors your Bybit account holdings.
- Read-only legs without a matching Bybit position are flagged with a warning icon and tooltip.
- Leg status: New (editable), Active (matched to exchange), Missing (no exchange match).
- Toggle leg inclusion and collection visibility to control what is shown in charts.
- Live price tracking and automatic leg price refresh from option chain data.
- Payoff chart updates for visible collections and selected valuation date.
- Closed positions panel with per-symbol summaries (size, avg entry/close, close P&L, fee) and a "since" date filter (date + time).
- Closed positions totals (P&L + fee) can be included in charts when enabled.
- Toggling the closed-positions include checkbox immediately shifts chart strategies by closed `Net` P&L (`TotalClosePnl - TotalFee`) and persists the updated state.
- Trades dialog for a symbol shows cumulative P&L starting from the chosen "since" date.

## View-model structure
- `PositionBuilderViewModel` owns the positions list, payoff chart series (strategies/markers/selected price), and server persistence.
- `PositionViewModel` owns per-position state: selected/live price, live status, valuation date, and manages the ticker subscription.
- `LegsCollectionViewModel` owns a collection and creates `LegViewModel` + `QuickAddViewModel`.
- `LegViewModel` subscribes to option tickers directly and calculates temp P&L.
- `ClosedPositionsViewModel` manages closed positions, summaries, and trading-history refresh.

## Live price and subscriptions
- Exchange ticker subscription is per-position and managed by `PositionViewModel`.
- `ExchangeTickerService` handles Bybit-only subscription, throttles updates internally, and resolves settings from local storage via options.
- `ITickersService.SubscribeAsync` works in both non-live and live modes: handlers receive updates from explicit `ITickersService.UpdateTickersAsync(...)` calls, and websocket-driven updates only when `ITickersService.IsLive` is `true`.
- If a symbol already has a cached ticker/price snapshot, subscribe callbacks are invoked immediately on subscribe for both `ITickersService` and `IOptionsChainService`.
- `IOptionsChainService` follows the same live/update contract: `SubscribeAsync` is always allowed, `UpdateTickersAsync(...)` pushes fresh snapshots to subscribers, and websocket updates are delivered only when `IOptionsChainService.IsLive` is `true`.
- Turning live off disposes the exchange ticker subscription; turning live on re-subscribes.
- When chart candles are enabled, live ticker updates are aggregated into 1-hour OHLC candles and pushed to the payoff chart.
- Enabling candles triggers an immediate historical candle load for the current chart time range (default window is 48 hours).
- Candle timeframe defaults to 1H for both initial history load and ongoing live aggregation.
- When the chart time range is changed, only missing candle segments are requested and merged into the local candle cache.
- Live leg/ticker UI updates are debounced to reduce render pressure during rapid market updates.
- When live is off, option ticker subscriptions are stopped and bid/ask display is hidden.
- When live is off, mark price is calculated using Black-Scholes with option-chain IV and the latest underlying price.
- Option chain updates use `OptionsChainService.SubscribeAsync` (no global events); multiple handlers supported.
- Positions page subscribes to both active positions and open orders snapshots while open.
- Open-order snapshots update order chips and existing order legs in-place; closed orders are removed from chips and order legs.
- Each exchange-sourced leg stores a stable `ReferenceId` (order ID for orders, symbol+side key for positions) so realtime updates target the correct leg.
- When an open order disappears and a matching exchange position appears, the leg is converted from `LegStatus.Order` to `LegStatus.Active` instead of being removed.
- Active-position snapshots update position legs; when an active exchange position is fully closed, the leg is copied to closed positions and the existing read-only leg is kept, marked `Missing`, and excluded from payoff (`IsIncluded = false`).
- Position legs and order legs are synchronized separately so active position legs do not get converted to `LegStatus.Order`.
- Persisted legs are treated as orders only when their IDs use the `order:` prefix, which prevents legacy active legs from being misclassified after reload.

## Persistence
- Positions are stored on the server via the positions API (no browser/local storage).
- The latest payoff chart axis range is cached per position in browser local storage to restore the view quickly between reloads.
- The last selected chart time interval is cached in browser local storage and restored as a rolling range from the current time when the page is loaded.
- Active Bybit positions are still refreshed via REST snapshot on initial connect and reconnect, then kept up to date by websocket updates.
- Deleting a position removes it from the server store immediately.
- Exchange positions/orders snapshots are reused across collections for a short TTL to avoid repeated parallel HTTP requests during page initialization.

## Typical workflow
1) Create a position or duplicate an existing collection.
2) Add legs using quick input or manual entry.
3) Visualize payoff and adjust settings (visibility, included legs).
4) Changes are saved to the server whenever you edit positions.

## Legs parsing
See `doc/LegParsing.md` for the full parsing rules, defaults, and UI preview behavior.

## UI updates
- The position page is route-driven and opens a single position by ID via `/position/{positionId}` (legacy `/positions/{positionId}` is still accepted).
- Opening `/positions` without an ID shows guidance to open a position from the dashboard.
- Chart recalculation now runs only when a leg field used in charting changes (include/type/strike/expiry/size/price/IV); other leg edits persist without forcing a chart refresh.
- Chart data is regenerated using the user-adjusted axis range whenever the payoff chart range changes, and the last range is saved per position.
- Auto-centering caused by selected-price updates is no longer persisted as user chart range, which prevents accidental tiny range saves during page initialization.
- Payoff chart styling adapts to the current light/dark theme.
- When there is no live or selected price, the payoff chart leaves the selected price marker unset (null).
- Chart recalculation ignores pricing-context updates (live/selected price changes) to keep price selection responsive.
- Pricing context updates (selected/live price, valuation date) are applied in a single batched pass per collection to keep the UI responsive.
- Initial payoff chart rendering is deferred until after the first UI paint to keep page load snappy.
- Notes persist when the text field loses focus, instead of on every keystroke.
- The positions header shows combined total P&L (temp + closed).
- Position labels in the selector use the `{baseAsset}/{quoteAsset} - {name}` format.
- Futures legs now use exchange-provided expiration lists (including Perpetual) and no strike/IV inputs.
- Futures expiration dates are fetched when the leg edit dialog opens, then reused from cache for subsequent edits.
- Each leg collection shows available exchange positions and open orders as chips; clicking a chip adds it as a leg.
- Removing a read-only leg adds it back to chips only if it is still present in cached subscription snapshots (no extra exchange request on remove).
- Removing a leg with `LegStatus.Missing` automatically adds its symbol to closed positions if not already tracked, and shows a 3-second user notification.
- Order chips are shown per exchange order (not collapsed by symbol), so multiple orders on one symbol remain selectable.
- Legs created from open orders are added with `IsIncluded = false` and `LegStatus = Order`.
- Order legs are rendered on the payoff chart as vertical markers for quick placement context.
- The positions header includes a `Candles` switch chip to show/hide ticker candles on the payoff chart.
