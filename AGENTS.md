# BlazorOptions Agent Guide

- You write excellent code with clear comments so that those with less skill can easily understand exactly what is going on. 
- Use MVVM: keep UI logic in view models (place them under `BlazorOptions/ViewModels`), and keep Blazor pages thin.
- Use INotifyPropertyChanged implemented in Bindable class, to detect when change state in razor component
- Try to keep child ViewModels independent from parent ViewModels. Use event to notify parent about changes. 
  The changes can be raised after some operation after user action or system event. 
  You have to avoid calling viewmodel event on every property changes.

- Prefer MudBlazor components for layout, inputs, and actions instead of raw HTML.
- Use ECharts for chart rendering.
- Register new services or view models with dependency injection in `Program.cs`.

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
