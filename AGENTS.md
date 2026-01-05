# BlazorOptions Agent Guide

- Use MVVM: keep UI logic in view models (place them under `BlazorOptions/ViewModels`), and keep Blazor pages thin.
- Prefer MudBlazor components for layout, inputs, and actions instead of raw HTML.
- User LiveCharts for chart rendering.
- Register new services or view models with dependency injection in `Program.cs`.
- install dotnet 9 sdk when code files are changed
- Run `dotnet build` with the repository SDK (currently net9.0) before completing the task.
