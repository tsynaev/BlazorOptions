# BlazorOptions Agent Guide

- You write excellent code with clear comments so that those with less skill can easily understand exactly what is going on. 
- Use MVVM: keep UI logic in view models (place them under `BlazorOptions/ViewModels`), and keep Blazor pages thin.
- Use INotifyPropertyChanged implemented in Bindable class, to detect when change state in razor component
- Try to keep child ViewModels independent from parent ViewModels. Use event to notify parent about changes. 
  The changes can be raised after some operation after user action or system event. 
  You have to avoid calling viewmodel event on every property changes.
- Preserve user-authored code structure and logic when applying fixes. Make minimal targeted changes on top of existing implementation; do not refactor user code unless explicitly requested.
- For responsiveness, apply pricing-context updates in a single batched pass per collection instead of updating each leg immediately.
- For performance, avoid heavy LINQ in Razor; precompute grouped or sorted data in view models.
- For list rendering stability, add `@key` to repeated items when possible.
- For JS-rendered components, override `ShouldRender` to avoid unnecessary Blazor re-renders.
- Keep response compression enabled for WASM/static payloads on the server.

- Prefer MudBlazor components for layout, inputs, and actions instead of raw HTML.
- Use ECharts for chart rendering.
- Register new services or view models with dependency injection in `Program.cs`.
- Dialog title must be shown only once: use the MudDialog header title from `ShowAsync(...)` and do not repeat the same heading inside dialog content.
- Exchange integrations must be accessed through `IExchangeService` and its child interfaces (`IOrdersService`, `IPositionsService`, `ITickersService`, `IOptionsChainService`, `IFuturesInstrumentsService`) rather than using Bybit concrete services directly in view models.
- Position aggregate and persistence models (`PositionModel`, `LegsCollectionModel`, `LegModel`, `ClosedModel`, `ClosedPositionModel`) live in `BlazorOptions.API` and are reused by Frontend/Server.
- Position persistence uses API models directly; DTO types and mappers are removed.
- API project namespaces must use `BlazorOptions.API.*` (do not place API classes under `BlazorOptions.ViewModels`).
- `PositionModel` may contain pure persisted-state selectors such as effective-leg filtering, but must not depend on pricing services, exchange services, chart sampling, or UI runtime state.
- `PositionModel.Clone()` should be the shared way to copy position state for UI projections; do not keep ad-hoc clone helpers in view models.
- The position page currently works with a single portfolio/collection. Multi-portfolio workflows are planned for the future `Options Calculator` feature.
- Position page price context must use the dated future ticker when there is exactly one included futures leg with expiration; otherwise fall back to the base/quote ticker to avoid ambiguous contract selection.
- `IndexPrice` is shared across all legs for the same base/quote pair. Leg-specific valuation differences must come from each leg's spread versus index, not from per-expiration index substitution.
- On the position page, pricing-context rebroadcast can stay synchronous because the typical leg count is small; avoid async batching unless the collection size meaningfully grows.
- Exchange ticker updates must carry separate mark and index prices; do not overload one field for both market mark and underlying context.
- On the position page, `IndexPrice` is the simulation/chart input. In non-live mode, option and futures marks must preserve their observed spread versus index instead of using index price as mark directly.
- Futures UI display should prefer `MarkPrice` over `IndexPrice` in cards and edit placeholders when showing the current market price to the user.
- Position page header `% P/L` must use finite max gain as denominator for capped-profit strategies; if payoff is not reliably capped, fall back to included non-futures entry value.
- Dashboard position card `% P/L` must match the position page denominator logic exactly.
- Shared position/dashboard `% P/L` logic should live in a reusable calculator that may depend on `OptionsService` and exchange read services; do not duplicate payoff-based denominator logic across view models.
- `HomeDashboardViewModel` must not calculate position or leg P/L locally; it should consume calculator outputs for total/percent/leg snapshot values.
- Dashboard position-card presentation logic must live in `PositionCardViewModel.ApplyPosition(...)`; `HomeDashboardViewModel` should stay limited to loading, caching, exchange snapshot application, and grouping.
- Dashboard option-chain ensure/loading for a card must happen inside `PositionCardViewModel`, not as a batch preload in `HomeDashboardViewModel`.
- Exchange snapshot leg sync (`Order`/`Active`/`Missing`, executed-order conversion, reference-id matching) must be shared between `PositionViewModel` flow and `PositionCardViewModel` flow through one reusable service; do not keep a dashboard-specific copy.
- `HomeDashboardViewModel` should fetch raw exchange positions/orders once and pass that snapshot down; `PositionCardViewModel` should apply the snapshot to its local card state instead of dashboard mutating models first.
- `HomeDashboardViewModel` should not store exchange snapshot copies for cards. `PositionCardViewModel` should read positions/orders from the exchange-service snapshot/cache directly.
- Position page `Day Min/Max` markers must render separate `3W` and `4W` pairs. Each pair uses the next expiry at or after 3 weeks / 4 weeks, ATM call and put prices for that expiry, and a symmetric `2 * theta` offset where theta is derived from the ATM call/put tickers.
- Position valuation timeline and expiry-state logic must use the full UTC expiration timestamp. Do not widen same-day expirations to end-of-day or compare expiries by date-only.
- For Bybit dated symbols that encode only the calendar date (for example `BTC-27MAR26-70000-C-USDT`), interpret expiry as `08:00 UTC` on that date unless an exchange payload provides a more precise delivery timestamp.
- Position persistence payloads should be versioned in JSON. Save the latest version and handle older payload migrations in the persistence layer instead of adding legacy fields to current models.

## Coding

- Write production-ready code with clear, concise comments.
- Each comment should briefly explain why a specific solution or decision was chosen (not what the code does).
- Keep comments short, consistent in style, and avoid redundancy.
- Use SOLID principles in code.


## Task completion
- Run `dotnet build --no-restore` before completing the task.
- fix errors and warnings (if possible)
- Performance is critical; aim for responsive interactions especially in chart and leg editing flows.
- Minimize memory usage where possible, particularly during chart rendering and leg operations.
- When feature behavior changes, update existing docs or create new `.md` files under the `doc` folder.
- When the user asks to commit, write the commit message based on all changes currently present in the working directory, not only the most recent requested edit.

## Testing
- don't run tests
- don't create screenshots

## Formating

- use UTF-8 encoding for `.cs` files
- use windows line ending (CR LF) in cs files
- avoid mixed line endings: after edits, ensure the entire `.cs` file is normalized to CRLF


## Documentation Updates

- When important decisions, constraints, conventions, or architectural points are discussed in issues, chats, or PRs, **AGENTS.md must be updated**.
- AGENTS.md is the **source of truth** for agent behavior, assumptions, and project-wide rules.
- If a discussion changes or invalidates an existing rule, the outdated entry must be **updated or removed**, not duplicated.
- Documentation structure:
- End-user feature descriptions must be written in Markdown and stored under `doc/` (user point of view, no class/variable names).
- Developer-facing implementation/architecture documentation must be stored under `doc/dev/` (technical details, classes, services, and code-level decisions are allowed).
- Write documentation in terms of supported behavior and available feature paths. Avoid framing docs around what the app should not do.
