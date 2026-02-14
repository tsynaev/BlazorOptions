namespace BlazorOptions.ViewModels;

public sealed record OpenInterestChartOptions(
    string BaseAsset,
    string QuoteAsset,
    string[] Strikes,
    string[] Expirations,
    IReadOnlyList<OpenInterestBar3DPoint> Data,
    double MinValue,
    double MaxValue);

public sealed record OpenInterestBar3DPoint(
    int StrikeIndex,
    int ExpirationIndex,
    double Value);
