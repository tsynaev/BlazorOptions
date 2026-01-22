using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public interface ILegsCollectionDialogService
{
    Task<IReadOnlyList<LegModel>?> ShowOptionChainDialogAsync(
        PositionModel position,
        LegsCollectionModel collection,
        double? underlyingPrice);

    Task<IReadOnlyList<BybitPosition>?> ShowBybitPositionsDialogAsync(
        string? baseAsset,
        string? quoteAsset,
        IReadOnlyList<LegModel> existingLegs);

    Task<bool?> ConfirmRemoveCollectionAsync(string collectionName);

    Task ShowPortfolioSettingsAsync(Guid collectionId);
}
