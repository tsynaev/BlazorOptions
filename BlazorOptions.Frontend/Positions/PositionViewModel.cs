using BlazorOptions.API.Positions;
using BlazorOptions.Diagnostics;
using BlazorOptions.Services;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace BlazorOptions.ViewModels;

public sealed class PositionViewModel : IDisposable
{
    private static readonly TimeSpan InitialPriceFetchTimeout = TimeSpan.FromSeconds(2);
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
    private readonly IPositionsPort _positionsPort;
    private readonly LegsCollectionViewModelFactory _collectionFactory;
    private readonly ClosedPositionsViewModelFactory _closedPositionsFactory;
    private readonly INotifyUserService _notifyUserService;
    private readonly IExchangeService _exchangeService;
    private decimal? _currentPrice;
    private DateTime _valuationDate = DateTime.UtcNow;
    private decimal? _selectedPrice;
    private decimal? _livePrice;
    private bool _isLive = false;
    private IDisposable? _tickerSubscription;
    private IDisposable? _positionsSubscription;
    private string? _currentSymbol;
    private PositionModel _position = null!;
    private bool _suppressNotesPersist;

    public PositionViewModel(
        PositionBuilderViewModel positionBuilder,
        IPositionsPort positionsPort,
        LegsCollectionViewModelFactory collectionFactory,
        ClosedPositionsViewModelFactory closedPositionsFactory,
        INotifyUserService notifyUserService,
        IExchangeService exchangeService)
    {
        _positionBuilder = positionBuilder;
        _positionsPort = positionsPort;
        _collectionFactory = collectionFactory;
        _closedPositionsFactory = closedPositionsFactory;
        _notifyUserService = notifyUserService;
        _exchangeService = exchangeService;
       
  
    }

    public PositionModel Position
    {
        get => _position;
        set
        {
            if (_position is not null)
            {
                throw new InvalidOperationException("Position is already set.");
            }

            _position = value;
            if (_position.Collections.Count == 0)
            {
                _position.Collections.Add(CreateCollection(_position, GetNextCollectionName(_position)));
            }
            if (_position.Closed is null)
            {
                _position.Closed = new ClosedModel();
            }
            Collections = new ObservableCollection<LegsCollectionViewModel>();

            foreach (var collection in _position.Collections)
            {
                Collections.Add(CreateCollectionViewModel(collection));
            }

            ClosedPositions = _closedPositionsFactory.Create(_positionBuilder, _position);

            ClosedPositions.Model.PropertyChanged += OnClosedPositionsChanged;
            ClosedPositions.UpdatedCompleted += OnClosedPositionsUpdated;

            _position.PropertyChanged += HandlePositionPropertyChanged;
        }
    }

    private async Task OnClosedPositionsUpdated()
    {
        await PersistPositionAsync();
        _positionBuilder.QueueChartUpdate();
    }


    private void OnClosedPositionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ClosedModel.TotalNet):
            case nameof(ClosedModel.Include):
                _positionBuilder.QueueChartUpdate();
                break;
        }
    }

    public ObservableCollection<LegsCollectionViewModel> Collections { get; private set; } = new();

    public ClosedPositionsViewModel ClosedPositions { get; private set; } = null!;

    public decimal? SelectedPrice => _selectedPrice;

    public decimal? LivePrice => _livePrice;

    public bool IsLive => _isLive;

    public DateTime ValuationDate => _valuationDate;

    public async Task InitializeAsync()
    {

        UpdateDerivedState();
        _ = TryHydrateSelectedPriceFromExchangeAsync();

        _ = EnsureLiveSubscriptionAsync();
        _positionsSubscription = await _exchangeService.Positions.SubscribeAsync(HandleActivePositionsSnapshot);

        await ClosedPositions.InitializeAsync();
        await RefreshExchangeMissingFlagsAsync();

    }

    public async Task AddCollectionAsync()
    {
        var collection = CreateCollection(Position, GetNextCollectionName(Position));
        Position.Collections.Add(collection);
        Collections.Add(CreateCollectionViewModel(collection));

        await HandleCollectionUpdatedAsync(LegsCollectionUpdateKind.CollectionChanged);
    }

    public async Task DuplicateCollectionAsync(LegsCollectionModel source)
    {
        var collection = CreateCollection(Position, GetNextCollectionName(Position), source.Legs.Select(x=>x.Clone()));
        Position.Collections.Add(collection);
        Collections.Add(CreateCollectionViewModel(collection));

        await HandleCollectionUpdatedAsync(LegsCollectionUpdateKind.CollectionChanged);
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

        await HandleCollectionUpdatedAsync(LegsCollectionUpdateKind.CollectionChanged);
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

    private async Task HandleCollectionUpdatedAsync(LegsCollectionUpdateKind updateKind)
    {
        var updateChart = updateKind == LegsCollectionUpdateKind.LegModelChanged
            || updateKind == LegsCollectionUpdateKind.CollectionChanged;
        var refreshTicker = updateKind == LegsCollectionUpdateKind.CollectionChanged;
        var persist = updateKind == LegsCollectionUpdateKind.LegModelChanged
            || updateKind == LegsCollectionUpdateKind.LegModelDataChanged
            || updateKind == LegsCollectionUpdateKind.CollectionChanged;


        if (updateChart)
        {
            _positionBuilder.QueueChartUpdate();
        }

        if (persist)
        {
            await _positionBuilder.QueuePersistPositionsAsync(Position);
        }
        if (refreshTicker)
        {
            _positionBuilder.RefreshLegTickerSubscription();
        }
        if (!updateChart)
        {
            _positionBuilder.NotifyStateChanged();
        }
    }

    public async Task PersistPositionAsync()
    {
        using var activity =
            ActivitySources.Telemetry.StartActivity($"{nameof(PositionViewModel)}.{nameof(PersistPositionAsync)}");

        var dto = PositionDtoMapper.ToDto(Position);
        await _positionsPort.SavePositionAsync(dto);
    }

    private void HandlePositionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressNotesPersist)
        {
            return;
        }

        if (e.PropertyName == nameof(PositionModel.Notes))
        {
            _ = PersistNotesAsync();
        }
    }

    private async Task PersistNotesAsync()
    {
        if (Position is null)
        {
            return;
        }

        _suppressNotesPersist = true;
        try
        {
            await _positionBuilder.QueuePersistPositionsAsync(Position);
        }
        finally
        {
            _suppressNotesPersist = false;
        }
    }

    public void Dispose()
    {
        _ = StopTickerAsync();
        _positionsSubscription?.Dispose();
        _positionsSubscription = null;
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

    private async Task HandleActivePositionsSnapshot(IReadOnlyList<BybitPosition> positions)
    {
        foreach (var position in positions)
        {
            ApplyActivePositionUpdate(position);
        }

        await UpdateExchangeMissingFlagsAsync(positions);
    }

    private void ApplyActivePositionUpdate(BybitPosition position)
    {
        if (position is null || string.IsNullOrWhiteSpace(position.Symbol))
        {
            return;
        }

        var collections = Collections;
        if (collections is null)
        {
            return;
        }

        foreach (var collection in collections)
        {
            foreach (var legViewModel in collection.Legs)
            {
                legViewModel.Update(position);
            }
        }
    }

    private async Task RefreshExchangeMissingFlagsAsync()
    {
        var positions = (await _exchangeService.Positions.GetPositionsAsync()).ToList();
        await UpdateExchangeMissingFlagsAsync(positions);
    }

    private async Task UpdateExchangeMissingFlagsAsync(IReadOnlyList<BybitPosition> positions)
    {
        if (Collections.Count == 0)
        {
            return;
        }

        var tasks = Collections.Select(collection => collection.UpdateExchangeMissingFlagsAsync(positions));
        await Task.WhenAll(tasks);
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
        _tickerSubscription = await _exchangeService.Tickers.SubscribeAsync(symbol, HandlePriceUpdated);
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
        _positionBuilder.AppendTickerPrice(update.Price, update.Timestamp);
        _positionBuilder.NotifyLivePriceChanged(update.Price);

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

    private async Task TryHydrateSelectedPriceFromExchangeAsync()
    {
        if (_isLive || _selectedPrice.HasValue)
        {
            return;
        }

        var symbol = NormalizeSymbol(Position);
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        IDisposable? registration = null;
        var priceTcs = new TaskCompletionSource<decimal?>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            registration = await _exchangeService.Tickers.SubscribeAsync(symbol, update =>
            {
                if (string.Equals(update.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                {
                    priceTcs.TrySetResult(update.Price);
                }

                return Task.CompletedTask;
            });

            var completed = await Task.WhenAny(priceTcs.Task, Task.Delay(InitialPriceFetchTimeout));
            if (completed != priceTcs.Task)
            {
                return;
            }

            var fetchedPrice = await priceTcs.Task;
            if (!fetchedPrice.HasValue || fetchedPrice.Value <= 0)
            {
                return;
            }

            if (!_isLive && !_selectedPrice.HasValue)
            {
                _positionBuilder.UpdateSelectedPrice(fetchedPrice.Value, refresh: false);
            }

            _positionBuilder.AppendTickerPrice(fetchedPrice, DateTime.UtcNow);
        }
        catch
        {
            // Ignore fetch failures, the page can still function without a bootstrap price.
        }
        finally
        {
            registration?.Dispose();
        }
    }
}
