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
- Turning live off disposes the exchange ticker subscription; turning live on re-subscribes.
- When live is off, option ticker subscriptions are stopped and bid/ask display is hidden.
- When live is off, mark price is calculated using Black-Scholes with option-chain IV and the latest underlying price.
- Option chain updates use `OptionsChainService.SubscribeAsync` (no global events); multiple handlers supported.

## Persistence
- Positions are stored on the server via the positions API (no browser/local storage).
- Active Bybit positions are still refreshed via REST snapshot on initial connect and reconnect, then kept up to date by websocket updates.
- Deleting a position removes it from the server store immediately.

## Typical workflow
1) Create a position or duplicate an existing collection.
2) Add legs using quick input or manual entry.
3) Visualize payoff and adjust settings (visibility, included legs).
4) Changes are saved to the server whenever you edit positions.

## Legs parsing
See `doc/LegParsing.md` for the full parsing rules, defaults, and UI preview behavior.

## UI updates
- The legacy tabs were replaced with a borderless dropdown so selecting positions works on mobile and still keeps the URL in sync via `/positions/{positionId}` links.
- Switching positions now updates the route, preserves the previously opened chart after a refresh, and shows an error alert with recovery actions when someone navigates to a nonexistent position ID.
- Chart recalculation now runs only when a leg field used in charting changes (include/type/strike/expiry/size/price/IV); other leg edits persist without forcing a chart refresh.
- Chart data is regenerated using the user-adjusted axis range whenever the payoff chart range changes, and the last range is saved per position.
- Payoff chart styling adapts to the current light/dark theme.
- Chart recalculation ignores pricing-context updates (live/selected price changes) to keep price selection responsive.
- Pricing context updates (selected/live price, valuation date) are applied in a single batched pass per collection to keep the UI responsive.
- Initial payoff chart rendering is deferred until after the first UI paint to keep page load snappy.
- Notes persist when the text field loses focus, instead of on every keystroke.
- The positions header now shows base/settle assets and combined total P&L (temp + closed).
