using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed record PositionCreateRequest(
    string Name,
    string BaseAsset,
    string QuoteAsset,
    IReadOnlyList<BybitPosition> SelectedBybitPositions,
    IReadOnlyList<LegModel> InitialLegs);
