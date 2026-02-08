# BlazorOptions Agent Guide

- You write excellent code with clear comments so that those with less skill can easily understand exactly what is going on. 
- Use MVVM: keep UI logic in view models (place them under `BlazorOptions/ViewModels`), and keep Blazor pages thin.
- Use INotifyPropertyChanged implemented in Bindable class, to detect when change state in razor component
- Try to keep child ViewModels independent from parent ViewModels. Use event to notify parent about changes. 
  The changes can be raised after some operation after user action or system event. 
  You have to avoid calling viewmodel event on every property changes.
- For responsiveness, apply pricing-context updates in a single batched pass per collection instead of updating each leg immediately.
- For performance, avoid heavy LINQ in Razor; precompute grouped or sorted data in view models.
- For list rendering stability, add `@key` to repeated items when possible.
- For JS-rendered components, override `ShouldRender` to avoid unnecessary Blazor re-renders.
- Keep response compression enabled for WASM/static payloads on the server.

- Prefer MudBlazor components for layout, inputs, and actions instead of raw HTML.
- Use ECharts for chart rendering.
- Register new services or view models with dependency injection in `Program.cs`.
- Exchange integrations must be accessed through `IExchangeService` and its child interfaces (`IOrdersService`, `IPositionsService`, `ITickersService`, `IOptionsChainService`, `IFuturesInstrumentsService`) rather than using Bybit concrete services directly in view models.

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

## Testing
- don't run tests
- don't create screenshots

## Formating

- use windows line ending (CR LF) in cs files
- avoid mixed line endings: after edits, ensure the entire `.cs` file is normalized to CRLF


## Documentation Updates

- When important decisions, constraints, conventions, or architectural points are discussed in issues, chats, or PRs, **AGENTS.md must be updated**.
- AGENTS.md is the **source of truth** for agent behavior, assumptions, and project-wide rules.
- If a discussion changes or invalidates an existing rule, the outdated entry must be **updated or removed**, not duplicated.

