# Frontend Project

BlazorOptions.Frontend contains the client-side view models and services that drive the UI. The BlazorOptions UI project now focuses on pages, components, and composition, and references the frontend library.

## Folder layout
Frontend code is organized by feature instead of layer. Feature folders (Account, Auth, Bybit, Positions, ClosedPositions, TradingHistory, Options, Dialogs, Navigation, Sync, Common, Diagnostics) group view models and related services together.

## Guidance
- Add new view models and related client services to the most relevant feature folder.
- Keep Blazor pages and components in `BlazorOptions`.
- Keep `BlazorOptions.Frontend` free of MudBlazor, ASP.NET Core, and JS interop dependencies.
- Prefer interfaces for services that access HTTP or browser storage when a seam is needed for testing or alternate implementations.
- Telemetry activities use `ActivitySources.Telemetry` and are enabled only when local storage contains the `telemetry` key with a value other than `false`.
- Home dashboard DVOL loading is handled in `Home/DvolIndexService.cs`:
  - fetches Deribit volatility index (`get_volatility_index_data`) by base asset.
  - requests 1D resolution data for a longer lookback window to keep chart readable.
  - uses browser local storage cache key prefix `home.dvol.v1.` with short TTL to avoid repeated network calls.
  - read flow is cache-first then refresh: cached chart can be rendered immediately, then updated after network response.
- DVOL rendering uses `Shared/DvolChart.razor` + `wwwroot/js/dvolChart.js` (ECharts) to support:
  - larger chart area in dashboard group card,
  - right-side Y axis,
  - axis crosshair tooltip with date label,
  - candlestick rendering with 1-year average mark line.
