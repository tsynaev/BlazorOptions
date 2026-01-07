# BlazorOptions Agent Guide

- Use MVVM: keep UI logic in view models (place them under `BlazorOptions/ViewModels`), and keep Blazor pages thin.
- Prefer MudBlazor components for layout, inputs, and actions instead of raw HTML.
- User ECharts for chart rendering.
- Register new services or view models with dependency injection in `Program.cs`.

## Task completion
- Run `dotnet build` before completing the task.
- fix errors and warnings (if possible)

## Testing
- don't run tests
- don't create screenshots
