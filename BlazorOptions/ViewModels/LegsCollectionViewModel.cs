using System.Collections.ObjectModel;
using BlazorOptions.Services;
using Microsoft.AspNetCore.Components.Web;

namespace BlazorOptions.ViewModels;

public readonly record struct BidAsk(double? Bid, double? Ask);


public sealed class LegsCollectionViewModel
{
    private readonly PositionBuilderViewModel _positionBuilder;
    private readonly ILegsCollectionDialogService _dialogService;
    private string? _baseAsset;
    private LegsCollectionModel _collection;

    public LegsCollectionViewModel(
        PositionBuilderViewModel positionBuilder,
        ILegsCollectionDialogService dialogService,
        OptionsChainService optionsChainService)
    {
        _positionBuilder = positionBuilder;
        _dialogService = dialogService;



        QuickAdd = new QuickAddViewModel(positionBuilder, optionsChainService);
        QuickAdd.LegCreated += HandleQuickAddLegCreated;

    }

    public string? BaseAsset
    {
        get => _baseAsset;
        set {
           _baseAsset = value;
           QuickAdd.BaseAsset = value;
        }
    }

    public LegsCollectionModel Collection
    {
        get => _collection;
        set
        {
            _collection = value;
            QuickAdd.Collection = value;
        } 
    }

    public ObservableCollection<LegModel> Legs => Collection.Legs;

    public IEnumerable<LegType> LegTypes => Enum.GetValues<LegType>();

    public QuickAddViewModel QuickAdd { get; }

    public event Func<LegModel, Task>? LegAdded;
    public event Func<LegModel, Task>? LegRemoved;
    public event Func<Task>? Updated;

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

    public bool IsVisible
    {
        get => Collection.IsVisible;
        set => _ = SetVisibilityAsync(value);
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

        var legs = await _dialogService.ShowOptionChainDialogAsync(
            _positionBuilder.SelectedPosition,
            Collection,
            _positionBuilder.SelectedPrice ?? _positionBuilder.LivePrice);
        if (legs is null)
        {
            return;
        }

        await _positionBuilder.UpdateLegsAsync(legs);
        await RaiseUpdatedAsync();
    }

    public Task AddQuickLegAsync()
    {
        SyncQuickAddPrice();
        return QuickAdd.AddQuickLegAsync();
    }

    public Task OnQuickLegKeyDown(KeyboardEventArgs args)
    {
        SyncQuickAddPrice();
        return QuickAdd.OnQuickLegKeyDown(args);
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

        var positions = await _dialogService.ShowBybitPositionsDialogAsync(
            _positionBuilder.SelectedPosition.BaseAsset,
            _positionBuilder.SelectedPosition.QuoteAsset,
            Collection.Legs.ToList());
        if (positions is null)
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
        await RaiseUpdatedAsync();
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
        await RaiseUpdatedAsync();
    }

    public async Task RemoveCollectionAsync()
    {
        if (_positionBuilder.SelectedPosition is null)
        {
            return;
        }

        var confirmed = await _dialogService.ConfirmRemoveCollectionAsync(Collection.Name);

        if (confirmed != true)
        {
            return;
        }

        await _positionBuilder.RemoveCollectionAsync(Collection.Id);
    }

    public async Task OpenSettingsAsync()
    {
        await _dialogService.ShowPortfolioSettingsAsync(Collection.Id);
    }

    public async Task RemoveLegAsync(LegModel leg)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        if (!Collection.Legs.Contains(leg))
        {
            return;
        }

        Collection.Legs.Remove(leg);
        await RaiseUpdatedAsync();
        await RaiseLegRemovedAsync(leg);
    }

    public async Task UpdateLegIncludedAsync(LegModel leg, bool include)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        leg.IsIncluded = include;
        await RaiseUpdatedAsync();
        await RaiseLegUpdatedAsync(leg);
    }

    public async Task UpdateLegTypeAsync(LegModel leg, LegType type)
    {
        if (!EnsureActiveCollection() || leg.IsReadOnly)
        {
            return;
        }

        leg.Type = type;
        await RaiseUpdatedAsync();
        await RaiseLegUpdatedAsync(leg);
    }

    public async Task UpdateLegStrikeAsync(LegModel leg, double? strike)
    {
        if (!EnsureActiveCollection() || leg.IsReadOnly)
        {
            return;
        }

        leg.Strike = strike;
        await RaiseUpdatedAsync();
        await RaiseLegUpdatedAsync(leg);
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

        await RaiseUpdatedAsync();
        await RaiseLegUpdatedAsync(leg);
    }

    public async Task UpdateLegSizeAsync(LegModel leg, double size)
    {
        if (!EnsureActiveCollection() || leg.IsReadOnly)
        {
            return;
        }

        leg.Size = size;
        await RaiseUpdatedAsync();
        await RaiseLegUpdatedAsync(leg);
    }

    public async Task UpdateLegPriceAsync(LegModel leg, double? price)
    {
        if (!EnsureActiveCollection() || leg.IsReadOnly)
        {
            return;
        }

        leg.Price = price;
        await RaiseUpdatedAsync();
        await RaiseLegUpdatedAsync(leg);
    }

    public async Task UpdateLegIvAsync(LegModel leg, double? iv)
    {
        if (!EnsureActiveCollection() || leg.IsReadOnly)
        {
            return;
        }

        leg.ImpliedVolatility = iv;
        await RaiseUpdatedAsync();
        await RaiseLegUpdatedAsync(leg);
    }

    public double? GetLegMarkIv(LegModel leg)
    {
        return _positionBuilder.GetLegMarkIv(leg);
    }

    public double? GetLegLastPrice(LegModel leg)
    {
        return _positionBuilder.GetLegLastPrice(leg);
    }

    public BidAsk GetLegBidAsk(LegModel leg)
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

    public double? GetLegTemporaryPnlPercent(LegModel leg)
    {
        return _positionBuilder.GetLegTemporaryPnlPercent(leg);
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

    public event Action<BybitPosition>? ActivePositionUpdated
    {
        add => _positionBuilder.ActivePositionUpdated += value;
        remove => _positionBuilder.ActivePositionUpdated -= value;
    }

    private bool EnsureActiveCollection()
    {
        return _positionBuilder.TrySetActiveCollection(Collection.Id);
    }

    private Task RaiseUpdatedAsync()
    {
        return Updated?.Invoke() ?? Task.CompletedTask;
    }

    private Task RaiseLegRemovedAsync(LegModel leg)
    {
        return LegRemoved?.Invoke(leg) ?? Task.CompletedTask;
    }

    private Task RaiseLegAddedAsync(LegModel leg)
    {
        return LegAdded?.Invoke(leg) ?? Task.CompletedTask;
    }

    private Task RaiseLegUpdatedAsync(LegModel leg)
    {
        return RaiseLegRemovedAsync(leg)
            .ContinueWith(_ => RaiseLegAddedAsync(leg))
            .Unwrap();
    }

    private async Task HandleQuickAddLegCreated(LegModel leg)
    {
        _ = leg;
        await _positionBuilder.PersistPositionsAsync();
        _positionBuilder.UpdateTemporaryPnls();
        _positionBuilder.UpdateChart();
        _positionBuilder.RefreshLegTickerSubscription();
        _positionBuilder.NotifyStateChanged();
        await RaiseUpdatedAsync();
        await RaiseLegAddedAsync(leg);
    }

    private void SyncQuickAddPrice()
    {
        QuickAdd.Price = _positionBuilder.SelectedPrice ?? _positionBuilder.LivePrice;
        QuickAdd.BaseAsset = _positionBuilder.SelectedPosition?.BaseAsset;
    }
}












