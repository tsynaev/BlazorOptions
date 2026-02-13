using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed record PositionCreateRequest(
    string Name,
    string BaseAsset,
    string QuoteAsset,
    IReadOnlyList<ExchangePosition> SelectedBybitPositions,
    IReadOnlyList<LegModel> InitialLegs);
