namespace BlazorOptions.ViewModels;

public sealed record VolatilitySkewSurfaceOptions(
    string BaseAsset,
    string QuoteAsset,
    IReadOnlyList<double> Strikes,
    IReadOnlyList<string> Expirations,
    IReadOnlyList<VolatilitySkewSurfacePoint> Data,
    double MinIv,
    double MaxIv);

public sealed record VolatilitySkewSurfacePoint(
    int StrikeIndex,
    int ExpirationIndex,
    double IvPercent);
