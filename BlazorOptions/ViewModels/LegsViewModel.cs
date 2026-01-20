using System.Collections.ObjectModel;
using BlazorOptions.Shared;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace BlazorOptions.ViewModels;

public class LegsViewModel
{
    private readonly PositionBuilderViewModel _positionBuilder;
    private readonly IDialogService _dialogService;

    public LegsViewModel(PositionBuilderViewModel positionBuilder, IDialogService dialogService)
    {
        _positionBuilder = positionBuilder;
        _dialogService = dialogService;
    }

    public ObservableCollection<LegModel> Legs => _positionBuilder.Legs;

    public ObservableCollection<LegsCollectionModel> Collections => _positionBuilder.Collections;

    public LegsCollectionModel? SelectedCollection => _positionBuilder.SelectedCollection;

    public Guid? SelectedCollectionId => SelectedCollection?.Id;

    public IEnumerable<LegType> LegTypes => Enum.GetValues<LegType>();

    public string QuickLegInput
    {
        get => _positionBuilder.QuickLegInput;
        set => _positionBuilder.QuickLegInput = value;
    }

    public bool HasActivePosition => _positionBuilder.SelectedPosition is not null;

    public bool HasActiveCollection => _positionBuilder.SelectedCollection is not null;

    public async Task OnQuickLegKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            await AddQuickLegAsync();
        }
    }

    public async Task AddQuickLegAsync()
    {
        await _positionBuilder.AddLegFromTextAsync(QuickLegInput);
    }

    public async Task AddLegAsync()
    {
        if (_positionBuilder.SelectedPosition is null || _positionBuilder.SelectedCollection is null)
        {
            return;
        }

        var parameters = new DialogParameters
        {
            [nameof(OptionChainDialog.Position)] = _positionBuilder.SelectedPosition,
            [nameof(OptionChainDialog.Collection)] = _positionBuilder.SelectedCollection,
            [nameof(OptionChainDialog.UnderlyingPrice)] = _positionBuilder.SelectedPrice ?? _positionBuilder.LivePrice
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
            return;
        }

        await _positionBuilder.UpdateLegsAsync(legs);
        _positionBuilder.NotifyStateChanged();
    }

    public Task SelectCollectionAsync(Guid? collectionId)
    {
        if (!collectionId.HasValue)
        {
            return Task.CompletedTask;
        }

        return _positionBuilder.SelectCollectionAsync(collectionId.Value);
    }

    public async Task AddCollectionAsync()
    {
        await _positionBuilder.AddCollectionAsync();
    }

    public async Task DuplicateCollectionAsync()
    {
        await _positionBuilder.DuplicateCollectionAsync();
    }

    public async Task RemoveLegAsync(LegModel leg)
    {
        if (await _positionBuilder.RemoveLegAsync(leg))
        {
            _positionBuilder.UpdateChart();
            _positionBuilder.NotifyStateChanged();
        }
    }

    public async Task UpdateLegIncludedAsync(LegModel leg, bool include)
    {
        leg.IsIncluded = include;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegTypeAsync(LegModel leg, LegType type)
    {
        leg.Type = type;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegStrikeAsync(LegModel leg, double? strike)
    {
        leg.Strike = strike;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegExpirationAsync(LegModel leg, DateTime? date)
    {
        if (date.HasValue)
        {
            leg.ExpirationDate = date.Value;
        }

        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegSizeAsync(LegModel leg, double size)
    {
        leg.Size = size;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegPriceAsync(LegModel leg, double? price)
    {
        leg.Price = price;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegIvAsync(LegModel leg, double? iv)
    {
        leg.ImpliedVolatility = iv;
        await PersistAndRefreshAsync();
    }

    private async Task PersistAndRefreshAsync()
    {
        await _positionBuilder.PersistPositionsAsync();
        _positionBuilder.UpdateTemporaryPnls();
        _positionBuilder.UpdateChart();
        _positionBuilder.NotifyStateChanged();
    }
}
