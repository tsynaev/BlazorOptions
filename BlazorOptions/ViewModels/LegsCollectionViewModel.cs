using System.Collections.ObjectModel;
using BlazorOptions.Services;
using BlazorOptions.Shared;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace BlazorOptions.ViewModels;

public sealed class LegsCollectionViewModel
{
    private readonly PositionBuilderViewModel _positionBuilder;
    private readonly IDialogService _dialogService;

    public LegsCollectionViewModel(PositionBuilderViewModel positionBuilder, IDialogService dialogService, LegsCollectionModel collection)
    {
        _positionBuilder = positionBuilder;
        _dialogService = dialogService;
        Collection = collection;
    }

    public LegsCollectionModel Collection { get; }

    public ObservableCollection<LegModel> Legs => Collection.Legs;

    public IEnumerable<LegType> LegTypes => Enum.GetValues<LegType>();

    public string QuickLegInput { get; set; } = string.Empty;

    public string Name
    {
        get => Collection.Name;
        set => _ = SetNameAsync(value);
    }

    public string Color
    {
        get => Collection.Color;
        set => _ = SetColorAsync(value);
    }

    public bool HasActivePosition => _positionBuilder.SelectedPosition is not null;

    public bool IsSelected => _positionBuilder.SelectedCollection?.Id == Collection.Id;

    public bool IsVisible
    {
        get => Collection.IsVisible;
        set => _ = SetVisibilityAsync(value);
    }

    public async Task OnQuickLegKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            await AddQuickLegAsync();
        }
    }

    public async Task AddQuickLegAsync()
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        await _positionBuilder.AddLegFromTextAsync(QuickLegInput);
        QuickLegInput = string.Empty;
    }

    public async Task AddLegAsync()
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        if (_positionBuilder.SelectedPosition is null)
        {
            return;
        }

        var parameters = new DialogParameters
        {
            [nameof(OptionChainDialog.Position)] = _positionBuilder.SelectedPosition,
            [nameof(OptionChainDialog.Collection)] = Collection,
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

    public async Task DuplicateCollectionAsync()
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        await _positionBuilder.DuplicateCollectionAsync();
    }

    public async Task LoadBybitPositionsAsync()
    {
        if (!EnsureActiveCollection())
        {
            return;
        }
        if (_positionBuilder.SelectedPosition is null)
        {
            return;
        }

        var parameters = new DialogParameters
        {
            [nameof(BybitPositionsDialog.InitialBaseAsset)] = _positionBuilder.SelectedPosition.BaseAsset,
            [nameof(BybitPositionsDialog.InitialQuoteAsset)] = _positionBuilder.SelectedPosition.QuoteAsset,
            [nameof(BybitPositionsDialog.ExistingLegs)] = Collection.Legs.ToList()
        };

        var options = new DialogOptions
        {
            CloseOnEscapeKey = true,
            MaxWidth = MaxWidth.Large,
            FullWidth = true
        };

        var dialog = await _dialogService.ShowAsync<BybitPositionsDialog>("Add Bybit positions", parameters, options);
        var result = await dialog.Result;

        if (result is null || result.Canceled || result.Data is not IReadOnlyList<BybitPosition> positions)
        {
            return;
        }

        await _positionBuilder.AddBybitPositionsToCollectionAsync(_positionBuilder.SelectedPosition, Collection, positions);
    }

    public async Task SetVisibilityAsync(bool isVisible)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        await _positionBuilder.UpdateCollectionVisibilityAsync(Collection.Id, isVisible);
    }

    public async Task SetColorAsync(string color)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(color))
        {
            return;
        }

        Collection.Color = color;
        await _positionBuilder.PersistPositionsAsync();
        _positionBuilder.UpdateChart();
        _positionBuilder.NotifyStateChanged();
    }

    public async Task SetNameAsync(string name)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Collection.Name = name.Trim();
        await _positionBuilder.PersistPositionsAsync();
        _positionBuilder.UpdateChart();
        _positionBuilder.NotifyStateChanged();
    }

    public async Task RemoveCollectionAsync()
    {
        if (_positionBuilder.SelectedPosition is null)
        {
            return;
        }

        var confirmed = await _dialogService.ShowMessageBox(
            "Remove portfolio?",
            $"Delete {Collection.Name} and its legs?",
            yesText: "Delete",
            cancelText: "Cancel");

        if (confirmed != true)
        {
            return;
        }

        await _positionBuilder.RemoveCollectionAsync(Collection.Id);
    }

    public async Task OpenSettingsAsync()
    {
        var parameters = new DialogParameters
        {
            [nameof(PortfolioSettingsDialog.CollectionId)] = Collection.Id
        };

        var options = new DialogOptions
        {
            CloseOnEscapeKey = true,
            MaxWidth = MaxWidth.Small,
            FullWidth = true
        };

        await _dialogService.ShowAsync<PortfolioSettingsDialog>("Portfolio settings", parameters, options);
    }

    public async Task RemoveLegAsync(LegModel leg)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        if (await _positionBuilder.RemoveLegAsync(leg))
        {
            _positionBuilder.UpdateChart();
            _positionBuilder.NotifyStateChanged();
        }
    }

    public async Task UpdateLegIncludedAsync(LegModel leg, bool include)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        leg.IsIncluded = include;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegTypeAsync(LegModel leg, LegType type)
    {
        if (!EnsureActiveCollection() || leg.IsReadOnly)
        {
            return;
        }

        leg.Type = type;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegStrikeAsync(LegModel leg, double? strike)
    {
        if (!EnsureActiveCollection() || leg.IsReadOnly)
        {
            return;
        }

        leg.Strike = strike;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegExpirationAsync(LegModel leg, DateTime? date)
    {
        if (!EnsureActiveCollection() || leg.IsReadOnly)
        {
            return;
        }

        if (date.HasValue)
        {
            leg.ExpirationDate = date.Value;
        }

        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegSizeAsync(LegModel leg, double size)
    {
        if (!EnsureActiveCollection() || leg.IsReadOnly)
        {
            return;
        }

        leg.Size = size;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegPriceAsync(LegModel leg, double? price)
    {
        if (!EnsureActiveCollection() || leg.IsReadOnly)
        {
            return;
        }

        leg.Price = price;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegIvAsync(LegModel leg, double? iv)
    {
        if (!EnsureActiveCollection() || leg.IsReadOnly)
        {
            return;
        }

        leg.ImpliedVolatility = iv;
        await PersistAndRefreshAsync();
    }

    public double? GetLegMarkIv(LegModel leg)
    {
        return _positionBuilder.GetLegMarkIv(leg);
    }

    public double? GetLegLastPrice(LegModel leg)
    {
        return _positionBuilder.GetLegLastPrice(leg);
    }

    public PositionBuilderViewModel.BidAsk GetLegBidAsk(LegModel leg)
    {
        return _positionBuilder.GetLegBidAsk(leg);
    }

    public double? GetLegMarkPrice(LegModel leg)
    {
        return _positionBuilder.GetLegMarkPrice(leg);
    }

    public double? GetLegMarketPrice(LegModel leg)
    {
        return _positionBuilder.GetLegMarketPrice(leg);
    }

    public double GetLegTemporaryPnl(LegModel leg)
    {
        return _positionBuilder.GetLegTemporaryPnl(leg);
    }

    public string? GetLegSymbol(LegModel leg)
    {
        return _positionBuilder.GetLegSymbol(leg);
    }

    public event Action<OptionChainTicker>? LegTickerUpdated
    {
        add => _positionBuilder.LegTickerUpdated += value;
        remove => _positionBuilder.LegTickerUpdated -= value;
    }

    private bool EnsureActiveCollection()
    {
        return _positionBuilder.TrySetActiveCollection(Collection.Id);
    }

    private async Task PersistAndRefreshAsync()
    {
        await _positionBuilder.PersistPositionsAsync();
        _positionBuilder.UpdateTemporaryPnls();
        _positionBuilder.UpdateChart();
        _positionBuilder.NotifyStateChanged();
    }
}












