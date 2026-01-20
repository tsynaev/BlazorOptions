using System;
using System.Collections.Generic;

namespace BlazorOptions.ViewModels;

public record EChartOptions(
    Guid PositionId,
    double[] Prices,
    string[] Labels,
    double? TemporaryPrice,
    IReadOnlyList<ChartCollectionSeries> Collections,
    double YMin,
    double YMax);

public record ChartCollectionSeries(
    Guid CollectionId,
    string Name,
    string Color,
    bool IsVisible,
    IReadOnlyList<double> ExpiryProfits,
    IReadOnlyList<double> TheoreticalProfits,
    double? TemporaryPnl,
    double? TemporaryExpiryPnl);
