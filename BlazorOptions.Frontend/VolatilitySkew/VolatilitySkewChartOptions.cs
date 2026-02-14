namespace BlazorOptions.ViewModels;

public sealed record VolatilitySkewChartOptions(
    string BaseAsset,
    string QuoteAsset,
    IReadOnlyList<VolatilitySkewSeries> Series,
    bool ShowBidAskMarkers,
    double? CurrentPrice);

public sealed record VolatilitySkewSeries(
    string Name,
    string ColorHex,
    IReadOnlyList<VolatilitySkewPoint> Points);

public sealed record VolatilitySkewPoint(
    double Strike,
    double MarkPrice,
    double BidPrice,
    double AskPrice,
    double MarkIv,
    double BidIv,
    double AskIv);
