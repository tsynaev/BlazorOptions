using System;
using System.Collections.ObjectModel;
using System.Linq;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class PositionViewModel : IDisposable
{
    private static readonly string[] CollectionPalette =
    {
        "#00A6FB",
        "#FF6F61",
        "#7DDE92",
        "#F9C74F",
        "#9B5DE5",
        "#F15BB5",
        "#00BBF9",
        "#00F5D4",
        "#B5179E"
    };

    private readonly PositionBuilderViewModel _positionBuilder;
    private readonly LegsCollectionViewModelFactory _collectionFactory;
    private readonly ClosedPositionsViewModelFactory _closedPositionsFactory;
    private readonly INotifyUserService _notifyUserService;
    private readonly ExchangeTickerService _exchangeTickerService;
    private readonly ActivePositionsService _activePositionsService;
    private readonly OptionsChainService _optionsChainService;
    private decimal? _currentPrice;
    private DateTime _valuationDate = DateTime.UtcNow;
    private decimal? _selectedPrice;
    private decimal? _livePrice;
    private bool _isLive = true;
    private IDisposable? _tickerSubscription;
    private string? _currentSymbol;
    private PositionModel _position;

    public PositionViewModel(
        PositionBuilderViewModel positionBuilder,
        LegsCollectionViewModelFactory collectionFactory,
        ClosedPositionsViewModelFactory closedPositionsFactory,
        INotifyUserService notifyUserService,
        ExchangeTickerService exchangeTickerService,
        ActivePositionsService activePositionsService,
        OptionsChainService optionsChainService)
    {
        _positionBuilder = positionBuilder;
        _collectionFactory = collectionFactory;
        _closedPositionsFactory = closedPositionsFactory;
        _notifyUserService = notifyUserService;
        _exchangeTickerService = exchangeTickerService;
        _activePositionsService = activePositionsService;
        _optionsChainService = optionsChainService;
       
  
    }

    public PositionModel Position
    {
        get => _position;
        set
        {
            _position = value;
            if (_position.Collections.Count == 0)
            {
                _position.Collections.Add(CreateCollection(_position, GetNextCollectionName(_position)));
            }
            if (_position.ClosedPositions is null)
            {
                _position.ClosedPositions = new ObservableCollection<ClosedPositionModel>();
            }
            Collections = new ObservableCollection<LegsCollectionViewModel>();

            foreach (var collection in _position.Collections)
            {
                Collections.Add(CreateCollectionViewModel(collection));
            }

            ClosedPositions = _closedPositionsFactory.Create(_positionBuilder, _position);

  
        }
    }

    public ObservableCollection<LegsCollectionViewModel> Collections { get; private set; }

    public ClosedPositionsViewModel ClosedPositions { get; private set; }

    public decimal? SelectedPrice => _selectedPrice;

    public decimal? LivePrice => _livePrice;

    public bool IsLive => _isLive;

    public DateTime ValuationDate => _valuationDate;

    public async Task InitializeAsync()
    {

        UpdateDerivedState();

        _ = EnsureLiveSubscriptionAsync();
        _activePositionsService.PositionUpdated += HandleActivePositionUpdated;
        _activePositionsService.PositionsUpdated += HandleActivePositionsSnapshot;

        await ClosedPositions.InitializeAsync();

    }

    public async Task AddCollectionAsync()
    {
        var collection = CreateCollection(Position, GetNextCollectionName(Position));
        Position.Collections.Add(collection);
        Collections.Add(CreateCollectionViewModel(collection));

        await HandleCollectionUpdatedAsync();
    }

    public async Task DuplicateCollectionAsync(LegsCollectionModel source)
    {
        var collection = CreateCollection(Position, GetNextCollectionName(Position), source.Legs.Select(x=>x.Clone()));
        Position.Collections.Add(collection);
        Collections.Add(CreateCollectionViewModel(collection));

        await HandleCollectionUpdatedAsync();
    }

    public async Task<bool> RemoveCollectionAsync(LegsCollectionModel collection)
    {
        if (Position.Collections.Count <= 1)
        {
            _notifyUserService.NotifyUser("At least one portfolio is required.");
            return false;
        }

        var removed = Position.Collections.Remove(collection);
        if (!removed)
        {
            return false;
        }

        var viewModel = Collections.FirstOrDefault(item => ReferenceEquals(item.Collection, collection));
        if (viewModel is not null)
        {
            Collections.Remove(viewModel);
        }

        await HandleCollectionUpdatedAsync();
        return true;
    }


    public static LegsCollectionModel CreateCollection(PositionModel position, string name, IEnumerable<LegModel>? legs = null)
    {
        var collection = new LegsCollectionModel
        {
            Name = name,
            Color = GetNextCollectionColor(position),
            IsVisible = true
        };

        if (legs is not null)
        {
            foreach (var leg in legs)
            {
                collection.Legs.Add(leg);
            }
        }

        return collection;
    }

    public static string GetNextCollectionName(PositionModel position)
    {
        return $"Portfolio {position.Collections.Count + 1}";
    }

    public static string GetNextCollectionColor(PositionModel position)
    {
        var usedColors = position.Collections.Select(collection => collection.Color)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var color in CollectionPalette)
        {
            if (!usedColors.Contains(color))
            {
                return color;
            }
        }

        return CollectionPalette[position.Collections.Count % CollectionPalette.Length];
    }



    private LegsCollectionViewModel CreateCollectionViewModel(LegsCollectionModel collection)
    {
        var viewModel = _collectionFactory.Create(this, collection);
        viewModel.Updated += HandleCollectionUpdatedAsync;
        viewModel.CurrentPrice = _currentPrice;
        viewModel.ValuationDate = _valuationDate;
        viewModel.IsLive = _isLive;
        return viewModel;
    }

    private async Task HandleCollectionUpdatedAsync()
    {
        await HandleCollectionUpdatedAsync(updateChart: true, refreshTicker: true);
    }

    private async Task HandleCollectionUpdatedAsync(bool updateChart, bool refreshTicker)
    {
        if (updateChart)
        {
            _positionBuilder.UpdateChart();
        }

        await _positionBuilder.QueuePersistPositionsAsync(Position);
        if (refreshTicker)
        {
            _positionBuilder.RefreshLegTickerSubscription();
        }
        _positionBuilder.NotifyStateChanged();
    }

    public void Dispose()
    {
        _ = StopTickerAsync();
        _activePositionsService.PositionUpdated -= HandleActivePositionUpdated;
        _activePositionsService.PositionsUpdated -= HandleActivePositionsSnapshot;
        foreach (var collection in Collections)
        {
            if (collection is null)
            {
                continue;
            }

            collection.Updated -= HandleCollectionUpdatedAsync;
            collection.Dispose();
        }
    }

    private Task HandleActivePositionsSnapshot(IReadOnlyList<BybitPosition> positions)
    {
        foreach (var position in positions)
        {
            ApplyActivePositionUpdate(position);
        }

        return Task.CompletedTask;
    }

    private void HandleActivePositionUpdated(BybitPosition position)
    {
        ApplyActivePositionUpdate(position);
    }

    private void ApplyActivePositionUpdate(BybitPosition position)
    {
        if (position is null || string.IsNullOrWhiteSpace(position.Symbol))
        {
            return;
        }

        var updated = false;
        var collections = Collections;
        if (collections is null)
        {
            return;
        }

        foreach (var collection in collections)
        {
            foreach (var legViewModel in collection.Legs)
            {
                if (legViewModel.Update(position))
                {
                    updated = true;
                }
            }
        }


        if (updated)
        {
            _positionBuilder.UpdateChart();
            _positionBuilder.NotifyStateChanged();
        }
    }

    internal void SetSelectedPrice(decimal? price)
    {
        if (_selectedPrice == price)
        {
            return;
        }

        _selectedPrice = price;
        UpdateDerivedState();
    }

    internal void SetLivePrice(decimal? price)
    {
        if (_livePrice == price)
        {
            return;
        }

        _livePrice = price;
        UpdateDerivedState();
    }

    internal void SetIsLive(bool isLive)
    {
        if (_isLive == isLive)
        {
            return;
        }

        _isLive = isLive;
        if (!_isLive && !_selectedPrice.HasValue)
        {
            _selectedPrice = _livePrice;
        }

        UpdateDerivedState();
        if (_isLive)
        {
            _ = EnsureLiveSubscriptionAsync();
        }
        else
        {
            _ = StopTickerAsync();
        }
    }

    internal void SetValuationDate(DateTime date)
    {
        if (_valuationDate == date)
        {
            return;
        }

        _valuationDate = date;
        UpdateDerivedState();
    }

    private void UpdateDerivedState()
    {
        var currentPrice = _isLive ? _livePrice : _selectedPrice ?? _livePrice;

        if (_currentPrice == currentPrice
            && Collections.All(collection => collection.IsLive == _isLive && collection.ValuationDate == _valuationDate))
        {
            return;
        }

        _currentPrice = currentPrice;
        foreach (var collection in Collections)
        {
            collection.CurrentPrice = currentPrice;
            collection.IsLive = _isLive;
            collection.ValuationDate = _valuationDate;
        }
    }



    internal async Task EnsureLiveSubscriptionAsync()
    {
        if (!_isLive)
        {
            return;
        }

        var symbol = NormalizeSymbol(Position);
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        if (_tickerSubscription is not null)
        {
            return;
        }

        _currentSymbol = symbol;

        _tickerSubscription?.Dispose();
        _tickerSubscription = await _exchangeTickerService.SubscribeAsync(symbol, HandlePriceUpdated);
    }

    internal async Task StopTickerAsync()
    {
        _tickerSubscription?.Dispose();
        _tickerSubscription = null;
        _currentSymbol = null;
        await Task.CompletedTask;
    }

    private Task HandlePriceUpdated(ExchangePriceUpdate update)
    {
        if (!_isLive)
        {
            return Task.CompletedTask;
        }

        if (!string.Equals(update.Symbol, _currentSymbol, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        SetLivePrice(update.Price);
        _positionBuilder.UpdateChart();
        _positionBuilder.NotifyStateChanged();

        return Task.CompletedTask;
    }

    private static string NormalizeSymbol(PositionModel position)
    {
        var baseAsset = position.BaseAsset?.Trim();
        var quoteAsset = position.QuoteAsset?.Trim();

        if (!string.IsNullOrWhiteSpace(baseAsset) && !string.IsNullOrWhiteSpace(quoteAsset))
        {
            return $"{baseAsset}{quoteAsset}".Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        }

        return string.Empty;
    }
}
