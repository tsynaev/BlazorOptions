namespace BlazorOptions.ViewModels;

public sealed record VolatilitySkewExpirationChip(
    DateTime ExpirationDate,
    string Label,
    bool IsSelected,
    string ColorHex);
