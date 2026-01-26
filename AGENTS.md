# BlazorOptions Agent Guide

- Use MVVM: keep UI logic in view models (place them under `BlazorOptions/ViewModels`), and keep Blazor pages thin.
- Prefer MudBlazor components for layout, inputs, and actions instead of raw HTML.
- User ECharts for chart rendering.
- Register new services or view models with dependency injection in `Program.cs`.

## Task completion
- Run `dotnet build --no-restore` before completing the task.
- fix errors and warnings (if possible)
- Performance is critical; aim for responsive interactions especially in chart and leg editing flows.
- Minimize memory usage where possible, particularly during chart rendering and leg operations.

## Testing
- don't run tests
- don't create screenshots

## Formating

- use windows line ending (CR LF) in cs files
- avoid mixed line endings: after edits, ensure the entire `.cs` file is normalized to CRLF
