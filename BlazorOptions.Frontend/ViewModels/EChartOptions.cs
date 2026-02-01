using System;
using System.Collections.Generic;

namespace BlazorOptions.ViewModels;

public record EChartOptions(
    Guid PositionId,
    decimal[] Prices,
    string[] Labels,
    decimal? TemporaryPrice,
    IReadOnlyList<ChartCollectionSeries> Collections,
    decimal YMin,
    decimal YMax);

public record ChartCollectionSeries(
    Guid CollectionId,
    string Name,
    string Color,
    bool IsVisible,
    IReadOnlyList<decimal> ExpiryProfits,
    IReadOnlyList<decimal> TheoreticalProfits,
    decimal? TemporaryPnl,
    decimal? TemporaryExpiryPnl);
