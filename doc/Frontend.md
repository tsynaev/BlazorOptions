# Frontend Project

BlazorOptions.Frontend contains the client-side view models and services that drive the UI. The BlazorOptions UI project now focuses on pages, components, and composition, and references the frontend library.

## Moved folders
- `ViewModels/`
- `Services/`
- `Sync/`
- `Diagnostics/`

## Guidance
- Add new view models and related client services to `BlazorOptions.Frontend`.
- Keep Blazor pages and components in `BlazorOptions`.
- Keep `BlazorOptions.Frontend` free of MudBlazor, ASP.NET Core, and JS interop dependencies.
- Prefer interfaces for services that access HTTP or browser storage when a seam is needed for testing or alternate implementations.
