using System;
using System.Collections.Generic;

namespace BlazorOptions.ViewModels;

public record EChartOptions(
    Guid PositionId,
    double[] Prices,
    string[] Labels,
    IReadOnlyList<double> Profits,
    IReadOnlyList<double> TheoreticalProfits,
    double? TemporaryPrice,
    double? TemporaryPnl,
    double? TemporaryExpiryPnl,
    double YMin,
    double YMax);
