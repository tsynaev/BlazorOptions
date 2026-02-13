using BlazorOptions.Shared;
using BlazorOptions.ViewModels;
using MudBlazor;

namespace BlazorOptions.Services;

public sealed class LegsCollectionDialogService : ILegsCollectionDialogService
{
    private readonly IDialogService _dialogService;

    public LegsCollectionDialogService(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public async Task<IReadOnlyList<LegModel>?> ShowOptionChainDialogAsync(
        PositionModel position,
        LegsCollectionModel collection,
        decimal? underlyingPrice)
    {
        var parameters = new DialogParameters
        {
            [nameof(OptionChainDialog.Position)] = position,
            [nameof(OptionChainDialog.Collection)] = collection,
            [nameof(OptionChainDialog.UnderlyingPrice)] = underlyingPrice
        };

        var options = new DialogOptions
        {
            CloseOnEscapeKey = true,
            MaxWidth = MaxWidth.Large,
            FullWidth = true
        };

        var dialog = await _dialogService.ShowAsync<OptionChainDialog>("Add leg", parameters, options);
        var result = await dialog.Result;

        if (result is null || result.Canceled || result.Data is not IEnumerable<LegModel> legs)
        {
            return null;
        }

        return legs.ToList();
    }

    public async Task<IReadOnlyList<ExchangePosition>?> ShowBybitPositionsDialogAsync(
        string? baseAsset,
        string? quoteAsset,
        IReadOnlyList<LegModel> existingLegs)
    {
        var parameters = new DialogParameters
        {
            [nameof(BybitPositionsDialog.InitialBaseAsset)] = baseAsset,
            [nameof(BybitPositionsDialog.InitialQuoteAsset)] = quoteAsset,
            [nameof(BybitPositionsDialog.ExistingLegs)] = existingLegs
        };

        var options = new DialogOptions
        {
            CloseOnEscapeKey = true,
            MaxWidth = MaxWidth.Large,
            FullWidth = true
        };

        var dialog = await _dialogService.ShowAsync<BybitPositionsDialog>("Add Bybit positions", parameters, options);
        var result = await dialog.Result;

        if (result is null || result.Canceled || result.Data is not IReadOnlyList<ExchangePosition> positions)
        {
            return null;
        }

        return positions;
    }

    public Task<bool?> ConfirmRemoveCollectionAsync(string collectionName)
    {
        return _dialogService.ShowMessageBox(
            "Remove portfolio?",
            $"Delete {collectionName} and its legs?",
            yesText: "Delete",
            cancelText: "Cancel");
    }

    public Task ShowPortfolioSettingsAsync(Guid collectionId)
    {
        var parameters = new DialogParameters
        {
            [nameof(PortfolioSettingsDialog.CollectionId)] = collectionId
        };

        var options = new DialogOptions
        {
            CloseOnEscapeKey = true,
            MaxWidth = MaxWidth.Small,
            FullWidth = true
        };

        return _dialogService.ShowAsync<PortfolioSettingsDialog>("Portfolio settings", parameters, options);
    }
}
