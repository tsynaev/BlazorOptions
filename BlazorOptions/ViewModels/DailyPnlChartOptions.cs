using System.Collections.Generic;

namespace BlazorOptions.ViewModels;

public sealed record DailyPnlChartOptions(
    string[] Days,
    IReadOnlyList<DailyPnlSeries> Series,
    decimal? YMin,
    decimal? YMax);

public sealed record DailyPnlSeries(
    string Name,
    decimal[] Values,
    string? Color = null);
