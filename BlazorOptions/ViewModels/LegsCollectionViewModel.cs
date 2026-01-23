using System.Collections.ObjectModel;
using BlazorOptions.Services;
using Microsoft.AspNetCore.Components.Web;

namespace BlazorOptions.ViewModels;

public readonly record struct BidAsk(double? Bid, double? Ask);


public sealed class LegsCollectionViewModel : IDisposable
{
    private readonly PositionBuilderViewModel _positionBuilder;
    private readonly ILegsCollectionDialogService _dialogService;
    private readonly LegViewModelFactory _legViewModelFactory;
    private readonly INotifyUserService _notifyUserService;
    private string? _baseAsset;
    private double? _currentPrice;
    private bool _isLive;
    private DateTime _valuationDate;
    private LegsCollectionModel _collection = default!;
    private readonly Dictionary<string, LegViewModel> _legViewModels = new(StringComparer.Ordinal);
    private readonly ObservableCollection<LegViewModel> _legs = new();

    public LegsCollectionViewModel(
        PositionBuilderViewModel positionBuilder,
        ILegsCollectionDialogService dialogService,
        OptionsChainService optionsChainService,
        LegViewModelFactory legViewModelFactory,
        INotifyUserService notifyUserService)
    {
        _positionBuilder = positionBuilder;
        _dialogService = dialogService;
        _legViewModelFactory = legViewModelFactory;
        _notifyUserService = notifyUserService;

        QuickAdd = new QuickAddViewModel(_notifyUserService, optionsChainService);
        QuickAdd.LegCreated += HandleQuickAddLegCreated;

    }

    public PositionModel Position { get; set; } = default!;
    public PositionBuilderViewModel PositionBuilder => _positionBuilder;

    public string? BaseAsset
    {
        get => _baseAsset;
        set {
           _baseAsset = value;
           QuickAdd.BaseAsset = value;
        }
    }

    public double? CurrentPrice
    {
        get => _currentPrice;
        set
        {
            if (_currentPrice == value)
            {
                return;
            }

            _currentPrice = value;
            foreach (var leg in _legs)
            {
                leg.CurrentPrice = value;
            }
        }
    }

    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (_isLive == value)
            {
                return;
            }

            _isLive = value;
            foreach (var leg in _legs)
            {
                leg.IsLive = value;
            }
        }
    }

    public DateTime ValuationDate
    {
        get => _valuationDate;
        set
        {
            if (_valuationDate == value)
            {
                return;
            }

            _valuationDate = value;
            foreach (var leg in _legs)
            {
                leg.ValuationDate = value;
            }
        }
    }

    public LegsCollectionModel Collection
    {
        get => _collection;
        set
        {
            _collection = value;
            QuickAdd.Collection = value;
            SyncLegViewModels();
        } 
    }

    public ObservableCollection<LegViewModel> Legs => _legs;

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

    public bool IsVisible
    {
        get => Collection.IsVisible;
        set => _ = SetVisibilityAsync(value);
    }

    public async Task AddLegAsync()
    {
        if (!EnsureActivePosition(out var positionViewModel))
        {
            return;
        }

        if (Position is null)
        {
            return;
        }

        var legs = await _dialogService.ShowOptionChainDialogAsync(
            Position,
            Collection,
            CurrentPrice);
        if (legs is null)
        {
            return;
        }

        await positionViewModel!.UpdateLegsAsync(Collection, legs);
        SyncLegViewModels();
    }

    public Task AddQuickLegAsync()
    {
        if (!EnsureActivePosition(out _))
        {
            return Task.CompletedTask;
        }

        SyncQuickAddPrice();
        return QuickAdd.AddQuickLegAsync();
    }

    public Task OnQuickLegKeyDown(KeyboardEventArgs args)
    {
        if (!EnsureActivePosition(out _))
        {
            return Task.CompletedTask;
        }

        SyncQuickAddPrice();
        return QuickAdd.OnQuickLegKeyDown(args);
    }

    public async Task DuplicateCollectionAsync()
    {
        if (!EnsureActivePosition(out var positionViewModel))
        {
            return;
        }

        await positionViewModel!.DuplicateCollectionAsync(Collection);
    }

    public async Task LoadBybitPositionsAsync()
    {
        if (!EnsureActivePosition(out var positionViewModel))
        {
            return;
        }

        if (Position is null)
        {
            return;
        }

        var positions = await _dialogService.ShowBybitPositionsDialogAsync(
            Position.BaseAsset,
            Position.QuoteAsset,
            Collection.Legs.ToList());
        if (positions is null)
        {
            return;
        }

        await AddBybitPositionsToCollectionAsync(positions);
    }

    public async Task AddBybitPositionsToCollectionAsync(IReadOnlyList<BybitPosition> positions)
    {
        if (!EnsureActivePosition(out _))
        {
            return;
        }

        if (positions.Count == 0)
        {
            return;
        }

        var baseAsset = Position?.BaseAsset?.Trim();
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            _notifyUserService.NotifyUser("Specify a base asset before loading Bybit positions.");
            return;
        }

        var added = 0;

        foreach (var bybitPosition in positions)
        {
            if (Math.Abs(bybitPosition.Size) < 0.0001)
            {
                continue;
            }

            var leg = _positionBuilder.CreateLegFromBybitPosition(bybitPosition, baseAsset, bybitPosition.Category);
            if (leg is null)
            {
                continue;
            }

            var existing = PositionBuilderViewModel.FindMatchingLeg(Collection.Legs, leg);
            if (existing is null)
            {
                Collection.Legs.Add(leg);
                added++;
            }
            else
            {
                existing.Price = leg.Price;
                existing.Size = leg.Size;
            }
        }

        if (added == 0)
        {
            return;
        }

        SyncLegViewModels();
        await RaiseUpdatedAsync();
    }

    public async Task SetVisibilityAsync(bool isVisible)
    {
        if (!EnsureActivePosition(out var positionViewModel))
        {
            return;
        }

        await positionViewModel!.UpdateCollectionVisibilityAsync(Collection, isVisible);
    }

    public async Task SetColorAsync(string color)
    {
        if (!EnsureActivePosition(out _))
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
        if (!EnsureActivePosition(out _))
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
        if (!EnsureActivePosition(out var positionViewModel))
        {
            return;
        }

        var confirmed = await _dialogService.ConfirmRemoveCollectionAsync(Collection.Name);

        if (confirmed != true)
        {
            return;
        }

        await positionViewModel!.RemoveCollectionAsync(Collection);
    }

    public async Task OpenSettingsAsync()
    {
        await _dialogService.ShowPortfolioSettingsAsync(Collection.Id);
    }

    public async Task RemoveLegAsync(LegModel leg)
    {
        if (!EnsureActivePosition(out _))
        {
            return;
        }

        if (!Collection.Legs.Contains(leg))
        {
            return;
        }

        Collection.Legs.Remove(leg);
        RemoveLegViewModel(leg);
        await RaiseUpdatedAsync();
        await RaiseLegRemovedAsync(leg);
    }

    public async Task UpdateLegIncludedAsync(LegModel leg, bool include)
    {
        if (!EnsureActivePosition(out _))
        {
            return;
        }

        leg.IsIncluded = include;
        await RaiseUpdatedAsync();
        await RaiseLegUpdatedAsync(leg);
    }

    public async Task UpdateLegTypeAsync(LegModel leg, LegType type)
    {
        if (!EnsureActivePosition(out _) || leg.IsReadOnly)
        {
            return;
        }

        leg.Type = type;
        await RaiseUpdatedAsync();
        await RaiseLegUpdatedAsync(leg);
    }

    public async Task UpdateLegStrikeAsync(LegModel leg, double? strike)
    {
        if (!EnsureActivePosition(out _) || leg.IsReadOnly)
        {
            return;
        }

        leg.Strike = strike;
        await RaiseUpdatedAsync();
        await RaiseLegUpdatedAsync(leg);
    }

    public async Task UpdateLegExpirationAsync(LegModel leg, DateTime? date)
    {
        if (!EnsureActivePosition(out _) || leg.IsReadOnly)
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
        if (!EnsureActivePosition(out _) || leg.IsReadOnly)
        {
            return;
        }

        leg.Size = size;
        await RaiseUpdatedAsync();
        await RaiseLegUpdatedAsync(leg);
    }

    public async Task UpdateLegPriceAsync(LegModel leg, double? price)
    {
        if (!EnsureActivePosition(out _) || leg.IsReadOnly)
        {
            return;
        }

        leg.Price = price;
        await RaiseUpdatedAsync();
        await RaiseLegUpdatedAsync(leg);
    }

    public async Task UpdateLegIvAsync(LegModel leg, double? iv)
    {
        if (!EnsureActivePosition(out _) || leg.IsReadOnly)
        {
            return;
        }

        leg.ImpliedVolatility = iv;
        await RaiseUpdatedAsync();
        await RaiseLegUpdatedAsync(leg);
    }

    public double? GetLegMarkIv(LegModel leg)
    {
        return _positionBuilder.GetLegMarkIv(leg, Position?.BaseAsset);
    }

    public double? GetLegLastPrice(LegModel leg)
    {
        return _positionBuilder.GetLegLastPrice(leg, Position?.BaseAsset);
    }

    public BidAsk GetLegBidAsk(LegModel leg)
    {
        return _positionBuilder.GetLegBidAsk(leg, Position?.BaseAsset);
    }

    public double? GetLegMarkPrice(LegModel leg)
    {
        return _positionBuilder.GetLegMarkPrice(leg, Position?.BaseAsset);
    }

 
    public string? GetLegSymbol(LegModel leg)
    {
        return _positionBuilder.GetLegSymbol(leg, Position?.BaseAsset);
    }

    public event Action<BybitPosition>? ActivePositionUpdated
    {
        add => _positionBuilder.ActivePositionUpdated += value;
        remove => _positionBuilder.ActivePositionUpdated -= value;
    }

    private bool EnsureActivePosition(out PositionViewModel? positionViewModel)
    {
        positionViewModel = _positionBuilder.SelectedPosition;
        if (positionViewModel is null || positionViewModel.Position.Id != Position.Id)
        {
            positionViewModel = null;
            return false;
        }

        return true;
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
        await RaiseUpdatedAsync();
        await RaiseLegAddedAsync(leg);
    }

    private void SyncQuickAddPrice()
    {
        QuickAdd.Price = CurrentPrice;
        QuickAdd.BaseAsset = Position?.BaseAsset;
    }

    public void Dispose()
    {
        QuickAdd.LegCreated -= HandleQuickAddLegCreated;
        foreach (var viewModel in _legViewModels.Values)
        {
            DetachLegViewModel(viewModel);
            viewModel.Dispose();
        }
        _legViewModels.Clear();
        _legs.Clear();
    }

    private void RemoveLegViewModel(LegModel leg)
    {
        var key = leg.Id ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            key = leg.GetHashCode().ToString();
        }

        if (_legViewModels.TryGetValue(key, out var viewModel))
        {
            DetachLegViewModel(viewModel);
            viewModel.Dispose();
            _legViewModels.Remove(key);
            _legs.Remove(viewModel);
        }
    }

    private void SyncLegViewModels()
    {
        var ordered = new List<LegViewModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var leg in Collection.Legs)
        {
            var key = leg.Id ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                key = leg.GetHashCode().ToString();
            }

            if (!_legViewModels.TryGetValue(key, out var viewModel))
            {
                viewModel = _legViewModelFactory.Create(_positionBuilder, this, leg);
                _legViewModels[key] = viewModel;
                AttachLegViewModel(viewModel);
            }
            else
            {
                viewModel.UpdateLeg(leg);
            }

            viewModel.CurrentPrice = _currentPrice;
            viewModel.IsLive = _isLive;
            viewModel.ValuationDate = _valuationDate;
            ordered.Add(viewModel);
            seen.Add(key);
        }

        var toRemove = _legViewModels.Keys
            .Where(key => !seen.Contains(key))
            .ToList();
        foreach (var key in toRemove)
        {
            var viewModel = _legViewModels[key];
            DetachLegViewModel(viewModel);
            viewModel.Dispose();
            _legViewModels.Remove(key);
        }

        _legs.Clear();
        foreach (var viewModel in ordered)
        {
            _legs.Add(viewModel);
        }
    }

    private void AttachLegViewModel(LegViewModel viewModel)
    {
        viewModel.Changed += HandleLegViewModelChanged;
        viewModel.Removed += HandleLegViewModelRemoved;
    }

    private void DetachLegViewModel(LegViewModel viewModel)
    {
        viewModel.Changed -= HandleLegViewModelChanged;
        viewModel.Removed -= HandleLegViewModelRemoved;
    }

    private Task HandleLegViewModelRemoved()
    {
        return Task.CompletedTask;
    }

    private void HandleLegViewModelChanged()
    {
    }
}












