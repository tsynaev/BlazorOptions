namespace BlazorOptions.ViewModels;

public enum AvailableLegSourceKind
{
    Position,
    Order
}

public sealed record AvailableLegCandidate(
    string Id,
    AvailableLegSourceKind Kind,
    string? OrderKind,
    string Symbol,
    LegType Type,
    decimal Size,
    decimal? Price,
    DateTime? ExpirationDate,
    decimal? Strike);
