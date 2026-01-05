# BlazorOptions Agent Guide

- Use MVVM: keep UI logic in view models (place them under `BlazorOptions/ViewModels`), and keep Blazor pages thin.
- Prefer MudBlazor components for layout, inputs, and actions instead of raw HTML.
- User ECharts for chart rendering.
- Register new services or view models with dependency injection in `Program.cs`.
- Run `dotnet build` before completing the task.
