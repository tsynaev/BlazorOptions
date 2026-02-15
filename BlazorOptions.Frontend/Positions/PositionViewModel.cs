using BlazorOptions.Diagnostics;
using BlazorOptions.Services;
using BlazorChart.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace BlazorOptions.ViewModels;

public sealed class PositionViewModel : IDisposable
{
    private static readonly TimeSpan InitialPriceFetchTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CandleBucket = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultCandlesWindow = TimeSpan.FromHours(48);
    private const int MaxChartCandles = 5000;
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

    private readonly OptionsService _optionsService;
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
    private IDisposable? _ordersSubscription;
    private string? _currentSymbol;
    private PositionModel _position = null!;
    private bool _suppressNotesPersist;
    private readonly object _uiUpdateLock = new();
    private CancellationTokenSource? _uiUpdateCts;
    private static readonly TimeSpan UiUpdateDebounce = TimeSpan.FromMilliseconds(120);
    private readonly object _exchangeSnapshotLock = new();
    private Task<(IReadOnlyList<ExchangePosition> Positions, IReadOnlyList<ExchangeOrder> Orders)>? _exchangeSnapshotTask;
    private DateTime _exchangeSnapshotUtc;
    private static readonly TimeSpan ExchangeSnapshotTtl = TimeSpan.FromSeconds(10);
    private IReadOnlyList<ExchangePosition> _lastPositionsSnapshot = Array.Empty<ExchangePosition>();
    private IReadOnlyList<ExchangeOrder> _lastOrdersSnapshot = Array.Empty<ExchangeOrder>();
    private readonly object _persistQueueLock = new();
    private CancellationTokenSource? _persistQueueCts;
    private PositionModel? _queuedPersistPosition;
    private static readonly TimeSpan PersistQueueDelay = TimeSpan.FromMilliseconds(250);
    private readonly object _chartUpdateLock = new();
    private CancellationTokenSource? _chartUpdateCts;
    private static readonly TimeSpan ChartUpdateDelay = TimeSpan.FromMilliseconds(75);
    private ChartRange? _chartRangeOverride;
    private TimeRange? _chartTimeRange;
    private bool _initialChartScheduled;
    private long? _lastCandleBucketTime;
    private readonly object _candlesLoadLock = new();
    private CancellationTokenSource? _candlesLoadCts;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _isInitialized;
    private bool _isDisposed;

    public PositionViewModel(
        OptionsService optionsService,
        IPositionsPort positionsPort,
        LegsCollectionViewModelFactory collectionFactory,
        ClosedPositionsViewModelFactory closedPositionsFactory,
        INotifyUserService notifyUserService,
        IExchangeService exchangeService)
    {
        _optionsService = optionsService;
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

            ClosedPositions = _closedPositionsFactory.Create(this, _position);

            ClosedPositions.Model.PropertyChanged += OnClosedPositionsChanged;
            ClosedPositions.UpdatedCompleted += OnClosedPositionsUpdated;

            _position.PropertyChanged += HandlePositionPropertyChanged;
        }
    }

    private async Task OnClosedPositionsUpdated()
    {
        await QueuePersistPositionsAsync(Position);
        QueueChartUpdate();
    }


    private void OnClosedPositionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ClosedModel.TotalNet):
            case nameof(ClosedModel.Include):
                QueueChartUpdate();
                break;
        }
    }

    public ObservableCollection<LegsCollectionViewModel> Collections { get; private set; } = new();

    public ClosedPositionsViewModel ClosedPositions { get; private set; } = null!;

    public decimal? SelectedPrice => _selectedPrice;

    public decimal? LivePrice => _livePrice;

    public bool IsLive => _isLive;

    public DateTime ValuationDate => _valuationDate;
    public ObservableCollection<StrategySeries> ChartStrategies { get; } = new();
    public ObservableCollection<PriceMarker> ChartMarkers { get; } = new();
    public ObservableCollection<CandlePoint> ChartCandles { get; } = new();
    public double? ChartSelectedPrice { get; private set; }
    public ChartRange? ChartRange => _chartRangeOverride;
    public TimeRange? ChartTimeRange => _chartTimeRange;
    public bool ShowCandles { get; private set; }
    public DateTime MaxExpiryDate { get; private set; } = DateTime.UtcNow.Date;
    public int MaxExpiryDays { get; private set; }
    public int SelectedDayOffset { get; private set; }
    public string DaysToExpiryLabel => $"{SelectedDayOffset} days";


    public string? ErrorMessage { get; set; }

    public event Action? OnChange;
    public event Action<decimal?>? LivePriceChanged;

    public async Task InitializeAsync(Guid positionId)
    {
        await _initializeLock.WaitAsync();
        try
        {
            var storedPosition = await _positionsPort.LoadPositionAsync(positionId);
            if (storedPosition is null)
            {
                ErrorMessage = $"Position {positionId} not found.";
                return;
            }

            Position = storedPosition;

            ResetPricingContextForPositionSwitch();

            UpdateDerivedState();
            await TryHydrateSelectedPriceFromRecentCandleAsync();

            await _exchangeService.OptionsChain.UpdateTickersAsync(Position.BaseAsset);




            _ = EnsureLiveSubscriptionAsync();
            _positionsSubscription = await _exchangeService.Positions.SubscribeAsync(HandleActivePositionsSnapshot);
            _ordersSubscription = await _exchangeService.Orders.SubscribeAsync(HandleOpenOrdersSnapshot);

            await ClosedPositions.InitializeAsync();
            await RefreshExchangeMissingFlagsAsync();
            _ = ScheduleInitialChartUpdateAsync();

            _chartRangeOverride = storedPosition.ChartRange;
            _chartTimeRange = null;
            ChartCandles.Clear();
            _lastCandleBucketTime = null;

            OnChange?.Invoke();
            _isInitialized = true;

        }
        finally
        {
            _initializeLock.Release();
        }
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
        var persist = updateKind == LegsCollectionUpdateKind.LegModelChanged
            || updateKind == LegsCollectionUpdateKind.LegModelDataChanged
            || updateKind == LegsCollectionUpdateKind.CollectionChanged;


        if (updateChart)
        {
            QueueChartUpdate();
        }

        if (persist)
        {
            await QueuePersistPositionsAsync(Position);
        }

        if (!updateChart)
        {
            if (updateKind == LegsCollectionUpdateKind.PricingContextUpdated
                || updateKind == LegsCollectionUpdateKind.ViewModelDataUpdated)
            {
                QueueUiStateChanged();
            }
            else
            {
                NotifyStateChanged();
            }
        }
    }

    public async Task PersistPositionAsync()
    {
        using var activity =
            ActivitySources.Telemetry.StartActivity($"{nameof(PositionViewModel)}.{nameof(PersistPositionAsync)}");

        await _positionsPort.SavePositionAsync(Position);
    }

  

  
    public async Task UpdateNameAsync(PositionModel position, string name)
    {
        position.Name = name;
        await QueuePersistPositionsAsync(position);
    }

    public void UpdateChartRange(ChartRange range)
    {
        _chartRangeOverride = range;
        Position.ChartRange = range;
        _ = QueuePersistPositionsAsync(Position);
        UpdateChart();
        OnChange?.Invoke();
    }

    public void SetSelectedPrice(decimal price)
    {
        UpdateSelectedPrice(price, refresh: true);
    }

    public void UpdateSelectedPrice(decimal? price, bool refresh)
    {
        ApplySelectedPrice(price);
        SyncChartSelectedPrice();
        if (!refresh)
        {
            OnChange?.Invoke();
            return;
        }

        UpdateChart();
    }

    public void UpdateChartSelectedPrice(decimal? price)
    {
        if (!IsLive)
        {
            return;
        }

        ChartSelectedPrice = price.HasValue ? (double)price.Value : null;
    }

    public async Task SetIsLiveAsync(bool isEnabled)
    {
        if (IsLive == isEnabled)
        {
            return;
        }

        ApplyIsLive(isEnabled);

        if (!IsLive)
        {
            await StopTickerAsync();
            UpdateChart();
            OnChange?.Invoke();
            return;
        }

        SyncChartSelectedPrice();
        OnChange?.Invoke();
    }

    public void SetValuationDateFromOffset(int dayOffset)
    {
        var clampedOffset = Math.Clamp(dayOffset, 0, MaxExpiryDays);
        SelectedDayOffset = clampedOffset;
        ApplyValuationDate(DateTime.UtcNow.Date.AddDays(clampedOffset));
        UpdateChart();
    }

    public void SetValuationDate(DateTime date)
    {
        var today = DateTime.UtcNow.Date;
        var clampedDate = date.Date < today ? today : date.Date > MaxExpiryDate ? MaxExpiryDate : date.Date;
        ApplyValuationDate(clampedDate);
        SelectedDayOffset = Math.Clamp((clampedDate - today).Days, 0, MaxExpiryDays);
        UpdateChart();
    }

    public void ResetValuationDateToToday()
    {
        SetValuationDate(DateTime.UtcNow.Date);
    }

    public async Task<bool> RemovePositionAsync(PositionModel position)
    {
        await _positionsPort.DeletePositionAsync(position.Id);
        await StopTickerAsync();
        return true;
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
            await QueuePersistPositionsAsync(Position);
        }
        finally
        {
            _suppressNotesPersist = false;
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        CancelUiDebounce();
        _ = StopTickerAsync();
        CancelAndDispose(ref _persistQueueCts, _persistQueueLock);
        CancelAndDispose(ref _chartUpdateCts, _chartUpdateLock);
        CancelAndDispose(ref _candlesLoadCts, _candlesLoadLock);
        _positionsSubscription?.Dispose();
        _positionsSubscription = null;
        _ordersSubscription?.Dispose();
        _ordersSubscription = null;
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

    private async Task HandleActivePositionsSnapshot(IReadOnlyList<ExchangePosition> positions)
    {
        _lastPositionsSnapshot = positions?.ToArray() ?? Array.Empty<ExchangePosition>();
        UpdateCachedExchangeSnapshot(_lastPositionsSnapshot, _lastOrdersSnapshot);

        foreach (var position in _lastPositionsSnapshot)
        {
            ApplyActivePositionUpdate(position);
        }

        await MoveClosedExchangePositionsToClosedAsync(_lastPositionsSnapshot);
        await ApplyExchangeSnapshotsToCollectionsAsync();
        await UpdateExchangeMissingFlagsAsync(_lastPositionsSnapshot);
    }

    private async Task HandleOpenOrdersSnapshot(IReadOnlyList<ExchangeOrder> orders)
    {
        _lastOrdersSnapshot = orders?.ToArray() ?? Array.Empty<ExchangeOrder>();
        UpdateCachedExchangeSnapshot(_lastPositionsSnapshot, _lastOrdersSnapshot);
        await ApplyExchangeSnapshotsToCollectionsAsync();
    }

    private void ApplyActivePositionUpdate(ExchangePosition position)
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
        var snapshot = await GetExchangeSnapshotAsync(forceRefresh: true);
        _lastPositionsSnapshot = snapshot.Positions.ToArray();
        _lastOrdersSnapshot = snapshot.Orders.ToArray();
        UpdateCachedExchangeSnapshot(_lastPositionsSnapshot, _lastOrdersSnapshot);
        await ApplyExchangeSnapshotsToCollectionsAsync();
        await UpdateExchangeMissingFlagsAsync(snapshot.Positions);
    }

    private async Task UpdateExchangeMissingFlagsAsync(IReadOnlyList<ExchangePosition> positions)
    {
        if (Collections.Count == 0)
        {
            return;
        }

        var tasks = Collections.Select(collection => collection.UpdateExchangeMissingFlagsAsync(positions));
        await Task.WhenAll(tasks);
    }

    private async Task ApplyExchangeSnapshotsToCollectionsAsync()
    {
        if (Collections.Count == 0)
        {
            return;
        }

        var positions = _lastPositionsSnapshot;
        var orders = _lastOrdersSnapshot;
        var tasks = Collections.Select(collection => collection.ApplyExchangeSnapshotsAsync(positions, orders));
        await Task.WhenAll(tasks);
    }

    private async Task MoveClosedExchangePositionsToClosedAsync(IReadOnlyList<ExchangePosition> positions)
    {
        if (Collections.Count == 0 || ClosedPositions is null)
        {
            return;
        }

        var openKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var position in positions)
        {
            if (string.IsNullOrWhiteSpace(position.Symbol) || Math.Abs(position.Size) < 0.0001m)
            {
                continue;
            }

            openKeys.Add(BuildPositionKey(position.Symbol, DetermineSignedSize(position)));
        }

        var copiedSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in Collections)
        {
            var staleLegs = collection.Legs
                .Where(leg =>
                    leg.Leg.IsReadOnly
                    && leg.Leg.Status != LegStatus.Order
                    && !string.IsNullOrWhiteSpace(leg.Leg.Symbol)
                    && !openKeys.Contains(BuildPositionKey(leg.Leg.Symbol!, leg.Leg.Size)))
                .ToList();

            foreach (var legViewModel in staleLegs)
            {
                var leg = legViewModel.Leg;
                var alreadyArchived = leg.Status == LegStatus.Missing && !leg.IsIncluded;
                if (!alreadyArchived && !string.IsNullOrWhiteSpace(leg.Symbol))
                {
                    copiedSymbols.Add(leg.Symbol.Trim());
                }

                if (leg.IsIncluded)
                {
                    leg.IsIncluded = false;
                }

                legViewModel.SetLegStatus(LegStatus.Missing, "Exchange position not found for this leg.");
            }
        }

        if (copiedSymbols.Count == 0)
        {
            return;
        }

        ClosedPositions.SetAddSymbolInput(copiedSymbols);
        await ClosedPositions.AddSymbolAsync();
    }

    internal Task<(IReadOnlyList<ExchangePosition> Positions, IReadOnlyList<ExchangeOrder> Orders)> GetExchangeSnapshotAsync(bool forceRefresh = false)
    {
        lock (_exchangeSnapshotLock)
        {
            if (!forceRefresh && _exchangeSnapshotTask is not null)
            {
                if (!_exchangeSnapshotTask.IsCompleted)
                {
                    return _exchangeSnapshotTask;
                }

                if (_exchangeSnapshotTask.IsCompletedSuccessfully
                    && DateTime.UtcNow - _exchangeSnapshotUtc <= ExchangeSnapshotTtl)
                {
                    return _exchangeSnapshotTask;
                }
            }

            _exchangeSnapshotTask = FetchExchangeSnapshotAsync();
            return _exchangeSnapshotTask;
        }
    }

    internal (IReadOnlyList<ExchangePosition> Positions, IReadOnlyList<ExchangeOrder> Orders) GetCachedExchangeSnapshot()
    {
        return (_lastPositionsSnapshot, _lastOrdersSnapshot);
    }


    private void ApplySelectedPrice(decimal? price)
    {
        if (_selectedPrice == price)
        {
            return;
        }

        _selectedPrice = price;
        UpdateDerivedState();
    }

    private void ApplyLivePrice(decimal? price)
    {
        if (_livePrice == price)
        {
            return;
        }

        _livePrice = price;
        UpdateDerivedState();
    }

    private void ApplyIsLive(bool isLive)
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

    private void ApplyValuationDate(DateTime date)
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

        ApplyLivePrice(update.Price);
        AppendTickerPrice(update.Price, update.Timestamp);
        NotifyLivePriceChanged(update.Price);

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


    private async Task TryHydrateSelectedPriceFromRecentCandleAsync()
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

        try
        {
            var toUtc = DateTime.UtcNow;
            var fromUtc = toUtc.AddHours(-2);
            var candles = await _exchangeService.Tickers.GetCandlesWithVolumeAsync(symbol, fromUtc, toUtc, 60);
            var last = candles
                .OrderBy(c => c.Time)
                .LastOrDefault();
            if (last is null || last.Close <= 0)
            {
                return;
            }

            UpdateSelectedPrice((decimal)last.Close, refresh: false);
        }
        catch
        {
            // Keep page resilient if bootstrap candle request fails.
        }
    }

    private async Task<(IReadOnlyList<ExchangePosition> Positions, IReadOnlyList<ExchangeOrder> Orders)> FetchExchangeSnapshotAsync()
    {
        var positionsTask = _exchangeService.Positions.GetPositionsAsync();
        var ordersTask = _exchangeService.Orders.GetOpenOrdersAsync();
        await Task.WhenAll(positionsTask, ordersTask);

        var positions = positionsTask.Result?.ToList() ?? new List<ExchangePosition>();
        var orders = ordersTask.Result?.ToList() ?? new List<ExchangeOrder>();
        UpdateCachedExchangeSnapshot(positions, orders);

        return (positions, orders);
    }

    private void UpdateCachedExchangeSnapshot(
        IReadOnlyList<ExchangePosition> positions,
        IReadOnlyList<ExchangeOrder> orders)
    {
        lock (_exchangeSnapshotLock)
        {
            _exchangeSnapshotUtc = DateTime.UtcNow;
            _exchangeSnapshotTask = Task.FromResult((positions, orders));
        }
    }

    internal Task QueuePersistPositionsAsync(PositionModel? changedPosition = null)
    {
        lock (_persistQueueLock)
        {
            if (_isDisposed)
            {
                return Task.CompletedTask;
            }

            if (changedPosition is not null)
            {
                _queuedPersistPosition = changedPosition;
            }
            else if (_queuedPersistPosition is null)
            {
                _queuedPersistPosition = Position;
            }

            _persistQueueCts?.Cancel();
            _persistQueueCts?.Dispose();
            _persistQueueCts = new CancellationTokenSource();
            var token = _persistQueueCts.Token;
            _ = RunPersistQueueAsync(token);
        }

        return Task.CompletedTask;
    }

    public void QueueChartUpdate()
    {
        lock (_chartUpdateLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _chartUpdateCts?.Cancel();
            _chartUpdateCts?.Dispose();
            _chartUpdateCts = new CancellationTokenSource();
            var token = _chartUpdateCts.Token;
            _ = RunChartUpdateAsync(token);
        }
    }

    private async Task RunChartUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ChartUpdateDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        UpdateChart();
        OnChange?.Invoke();
    }

    private async Task RunPersistQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(PersistQueueDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        PositionModel? target;
        lock (_persistQueueLock)
        {
            target = _queuedPersistPosition;
            _queuedPersistPosition = null;
        }

        if (target is null && Position is null)
        {
            return;
        }

        try
        {
            var position = target ?? Position;
            if (position is not null)
            {
                await _positionsPort.SavePositionAsync(position);
            }
        }
        catch
        {
            // Keep background persistence resilient.
        }
    }

    public void UpdateChart()
    {
        using var activity = ActivitySources.Telemetry.StartActivity($"{nameof(PositionViewModel)}.{nameof(UpdateChart)}");

        if (Position is null)
        {
            return;
        }

        var collections = Collections ?? new ObservableCollection<LegsCollectionViewModel>();
        var allLegs = collections.SelectMany(collection => collection.Legs).ToList();
        var visibleCollections = collections.Where(collection => collection.IsVisible).ToList();
        var rangeLegs = visibleCollections.SelectMany(collection => collection.Legs).ToList();
        var closedPositionsTotal = Position.Closed.Include ? Position.Closed.TotalNet : 0m;

        if (rangeLegs.Count == 0)
        {
            rangeLegs = allLegs;
        }

        RefreshValuationDateBounds(allLegs);
        var valuationDate = ValuationDate;
        var rangeCalculationLegs = ResolveLegsForCalculation(rangeLegs).Where(leg => leg.IsIncluded).ToList();
        var (xs, _, _) = _optionsService.GeneratePosition(
            rangeCalculationLegs,
            180,
            valuationDate,
            _chartRangeOverride?.XMin,
            _chartRangeOverride?.XMax);
        var displayPrice = GetEffectivePrice();

        ChartStrategies.Clear();
        ChartMarkers.Clear();
        foreach (var collection in collections)
        {
            var collectionLegs = ResolveLegsForCalculation(collection.Legs).Where(leg => leg.IsIncluded).ToList();
            var profits = xs.Select(price => _optionsService.CalculateTotalProfit(collectionLegs, price)).ToArray();
            var theoreticalProfits = xs.Select(price => _optionsService.CalculateTotalTheoreticalProfit(collectionLegs, price, valuationDate)).ToArray();

            if (Math.Abs(closedPositionsTotal) > 0.0001m)
            {
                for (var i = 0; i < profits.Length; i++)
                {
                    profits[i] += closedPositionsTotal;
                }

                for (var i = 0; i < theoreticalProfits.Length; i++)
                {
                    theoreticalProfits[i] += closedPositionsTotal;
                }
            }

            var tempPoints = BuildPayoffPoints(xs, theoreticalProfits);
            var expiryPoints = BuildPayoffPoints(xs, profits);
            ChartStrategies.Add(new StrategySeries(
                collection.Collection.Id.ToString(),
                collection.Name,
                collection.Color,
                true,
                tempPoints,
                expiryPoints,
                collection.IsVisible));

            if (!collection.IsVisible)
            {
                continue;
            }

            foreach (var orderLeg in collection.Legs.Where(item => item.Leg.Status == LegStatus.Order))
            {
                if (!TryCreateOrderMarker(orderLeg, collection.Color, out var marker))
                {
                    continue;
                }

                ChartMarkers.Add(marker);
            }
        }

        ChartSelectedPrice = displayPrice.HasValue ? (double)displayPrice.Value : null;
    }

    private static bool TryCreateOrderMarker(LegViewModel leg, string color, out PriceMarker marker)
    {
        marker = default!;

        var price = leg.Leg.Type == LegType.Future
            ? leg.Leg.Price
            : leg.Leg.Strike;

        if (!price.HasValue)
        {
            return false;
        }

        var direction = leg.Leg.Size >= 0 ? "Buy" : "Sell";
        var symbol = string.IsNullOrWhiteSpace(leg.Leg.Symbol) ? leg.Leg.Type.ToString() : leg.Leg.Symbol!;
        var size = Math.Abs(leg.Leg.Size);
        marker = new PriceMarker((double)price.Value, $"Order {direction} {size:0.##}: {symbol}", color);
        return true;
    }

    private static IReadOnlyList<PayoffPoint> BuildPayoffPoints(IReadOnlyList<decimal> prices, IReadOnlyList<decimal> profits)
    {
        var count = Math.Min(prices.Count, profits.Count);
        if (count == 0)
        {
            return Array.Empty<PayoffPoint>();
        }

        var points = new PayoffPoint[count];
        for (var i = 0; i < count; i++)
        {
            points[i] = new PayoffPoint((double)prices[i], (double)profits[i]);
        }

        return points;
    }

    private void SyncChartSelectedPrice()
    {
        var price = GetEffectivePrice();
        ChartSelectedPrice = price.HasValue ? (double)price.Value : null;
    }

    public decimal? GetLegMarkIv(LegModel leg, string? baseAsset = null)
    {
        var resolvedBaseAsset = baseAsset ?? Position?.BaseAsset;
        var ticker = _exchangeService.OptionsChain.FindTickerForLeg(leg, resolvedBaseAsset);
        if (ticker is null || ticker.MarkIv <= 0)
        {
            if (ticker is null)
            {
                return null;
            }

            var bidIv = NormalizeIv(ticker.BidIv);
            if (bidIv > 0)
            {
                return bidIv;
            }

            var askIv = NormalizeIv(ticker.AskIv);
            return askIv > 0 ? askIv : null;
        }

        return NormalizeIv(ticker.MarkIv);
    }

    private static decimal NormalizeIv(decimal value)
    {
        if (value <= 0)
        {
            return 0m;
        }

        return value <= 3m ? value * 100m : value;
    }

    private decimal ResolveLegEntryPrice(LegViewModel leg)
    {
        if (leg.Leg.Type == LegType.Future && !leg.Leg.Price.HasValue)
        {
            return leg.CurrentPrice ?? leg.MarkPrice ?? 0m;
        }

        return leg.Leg.Price ?? leg.PlaceholderPrice ?? leg.MarkPrice ?? 0m;
    }

    private LegModel ResolveLegForCalculation(LegViewModel leg, string? baseAsset)
    {
        var resolvedPrice = ResolveLegEntryPrice(leg);
        var model = leg.Leg;
        var impliedVolatility = model.ImpliedVolatility;
        if (!impliedVolatility.HasValue || impliedVolatility.Value <= 0)
        {
            var markIv = GetLegMarkIv(model, baseAsset);
            if (markIv.HasValue)
            {
                impliedVolatility = markIv.Value;
            }
        }

        var originalIv = model.ImpliedVolatility ?? 0m;
        var resolvedIv = impliedVolatility ?? 0m;
        var priceChanged = !model.Price.HasValue || Math.Abs(resolvedPrice - model.Price.Value) > 0.0001m;
        if (!priceChanged && Math.Abs(resolvedIv - originalIv) < 0.0001m)
        {
            return model;
        }

        return new LegModel
        {
            Id = model.Id,
            IsIncluded = model.IsIncluded,
            Type = model.Type,
            Strike = model.Strike,
            ExpirationDate = model.ExpirationDate,
            Size = model.Size,
            Price = resolvedPrice,
            ImpliedVolatility = impliedVolatility
        };
    }

    private IEnumerable<LegModel> ResolveLegsForCalculation(IEnumerable<LegViewModel> legs)
    {
        var baseAsset = Position?.BaseAsset;
        foreach (var leg in legs)
        {
            yield return ResolveLegForCalculation(leg, baseAsset);
        }
    }

    private IEnumerable<LegModel> EnumerateAllLegs()
    {
        if (Position is null)
        {
            return Array.Empty<LegModel>();
        }

        return Position.Collections.SelectMany(collection => collection.Legs);
    }

    private void RefreshValuationDateBounds(IEnumerable<LegViewModel> legs)
    {
        var today = DateTime.UtcNow.Date;
        var expirations = legs
            .Select(l => l.Leg.ExpirationDate)
            .Where(exp => exp.HasValue)
            .Select(exp => exp!.Value)
            .ToList();

        MaxExpiryDate = expirations.Any() ? expirations.Max() : today;
        if (MaxExpiryDate < today)
        {
            MaxExpiryDate = today;
        }

        MaxExpiryDays = Math.Max(0, (MaxExpiryDate - today).Days);
        var currentValuationDate = ValuationDate;
        var clampedDate = currentValuationDate == default ? today : currentValuationDate.Date;
        if (clampedDate < today)
        {
            clampedDate = today;
        }
        else if (clampedDate > MaxExpiryDate)
        {
            clampedDate = MaxExpiryDate;
        }

        var clampedOffset = Math.Clamp((clampedDate - today).Days, 0, MaxExpiryDays);
        ApplyValuationDate(clampedDate);
        SelectedDayOffset = clampedOffset;
    }

    private decimal? GetEffectivePrice()
    {
        if (!IsLive)
        {
            if (SelectedPrice.HasValue)
            {
                return SelectedPrice.Value;
            }
        }
        else if (LivePrice.HasValue)
        {
            return LivePrice.Value;
        }

        return null;
    }


 
    public async Task SetShowCandlesAsync(bool isEnabled)
    {
        if (ShowCandles == isEnabled)
        {
            return;
        }

        ShowCandles = isEnabled;
        if (!ShowCandles)
        {
            OnChange?.Invoke();
            return;
        }

        await LoadMissingCandlesForCurrentTimeRangeAsync();
        OnChange?.Invoke();
    }

    public async Task UpdateChartTimeRangeAsync(TimeRange range)
    {
        _chartTimeRange = range;
        if (!ShowCandles)
        {
            OnChange?.Invoke();
            return;
        }

        await LoadMissingCandlesForCurrentTimeRangeAsync();
        OnChange?.Invoke();
    }

    internal void AppendTickerPrice(decimal? price, DateTime timestampUtc)
    {
        if (!ShowCandles || !price.HasValue || price.Value <= 0)
        {
            return;
        }

        var utc = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime();
        var bucketTime = AlignToBucket(utc, CandleBucket);
        var bucketMs = new DateTimeOffset(bucketTime).ToUnixTimeMilliseconds();
        var value = (double)price.Value;

        if (ChartCandles.Count > 0 && _lastCandleBucketTime == bucketMs)
        {
            var last = ChartCandles[ChartCandles.Count - 1];
            ChartCandles[ChartCandles.Count - 1] = last with
            {
                High = Math.Max(last.High, value),
                Low = Math.Min(last.Low, value),
                Close = value
            };
            return;
        }

        ChartCandles.Add(new CandlePoint(bucketMs, value, value, value, value));
        _lastCandleBucketTime = bucketMs;
        while (ChartCandles.Count > MaxChartCandles)
        {
            ChartCandles.RemoveAt(0);
        }
    }

    internal void NotifyLivePriceChanged(decimal? price)
    {
        if (IsLive)
        {
            UpdateChartSelectedPrice(price);
        }

        LivePriceChanged?.Invoke(price);
    }

    public void NotifyStateChanged()
    {
        OnChange?.Invoke();
    }

    private async Task ScheduleInitialChartUpdateAsync()
    {
        if (_initialChartScheduled)
        {
            return;
        }

        _initialChartScheduled = true;
        await Task.Yield();
        UpdateChart();
        OnChange?.Invoke();
    }

    private static DateTime AlignToBucket(DateTime timestampUtc, TimeSpan bucket)
    {
        var ticks = timestampUtc.Ticks - (timestampUtc.Ticks % bucket.Ticks);
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private async Task LoadMissingCandlesForCurrentTimeRangeAsync()
    {
        var symbol = GetCurrentSymbol();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        var (fromUtc, toUtc) = ResolveTimeWindowUtc();
        var requestedFromMs = new DateTimeOffset(fromUtc).ToUnixTimeMilliseconds();
        var requestedToMs = new DateTimeOffset(toUtc).ToUnixTimeMilliseconds();
        var bucketMs = (long)CandleBucket.TotalMilliseconds;
        var loadedRange = GetLoadedCandleRange();
        var needsFullLoad = loadedRange is null;
        var rangesToLoad = new List<(DateTime fromUtc, DateTime toUtc)>();

        if (loadedRange is null)
        {
            rangesToLoad.Add((fromUtc, toUtc));
        }
        else
        {
            var loaded = loadedRange.Value;
            if (requestedFromMs < loaded.minMs - bucketMs)
            {
                var leftToUtc = DateTimeOffset.FromUnixTimeMilliseconds(loaded.minMs).UtcDateTime;
                rangesToLoad.Add((fromUtc, leftToUtc));
            }

            if (requestedToMs > loaded.maxMs + bucketMs)
            {
                var rightFromUtc = DateTimeOffset.FromUnixTimeMilliseconds(loaded.maxMs).UtcDateTime;
                rangesToLoad.Add((rightFromUtc, toUtc));
            }
        }

        if (rangesToLoad.Count == 0)
        {
            if (ChartCandles.Count == 0)
            {
                AppendTickerPrice(GetEffectivePrice(), DateTime.UtcNow);
            }

            return;
        }

        CancellationToken token;
        lock (_candlesLoadLock)
        {
            _candlesLoadCts?.Cancel();
            _candlesLoadCts?.Dispose();
            _candlesLoadCts = new CancellationTokenSource();
            token = _candlesLoadCts.Token;
        }

        var fetchedCandles = new List<CandlePoint>();
        try
        {
            foreach (var range in rangesToLoad)
            {
                if (range.toUtc <= range.fromUtc)
                {
                    continue;
                }

                var candles = await _exchangeService.Tickers.GetCandlesAsync(symbol, range.fromUtc, range.toUtc, token);
                if (candles.Count > 0)
                {
                    fetchedCandles.AddRange(candles);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (needsFullLoad)
        {
            ChartCandles.Clear();
        }

        MergeCandles(fetchedCandles);
        _lastCandleBucketTime = ChartCandles.Count > 0 ? ChartCandles[^1].Time : null;
        if (ChartCandles.Count == 0)
        {
            AppendTickerPrice(GetEffectivePrice(), DateTime.UtcNow);
        }
    }

    private (DateTime fromUtc, DateTime toUtc) ResolveTimeWindowUtc()
    {
        if (_chartTimeRange is not null)
        {
            var from = DateTimeOffset.FromUnixTimeMilliseconds((long)_chartTimeRange.Min).UtcDateTime;
            var to = DateTimeOffset.FromUnixTimeMilliseconds((long)_chartTimeRange.Max).UtcDateTime;
            return from <= to ? (from, to) : (to, from);
        }

        var now = DateTime.UtcNow;
        var fromUtc = now - DefaultCandlesWindow;
        var toUtc = now;
        _chartTimeRange = new TimeRange(
            new DateTimeOffset(fromUtc).ToUnixTimeMilliseconds(),
            new DateTimeOffset(toUtc).ToUnixTimeMilliseconds());
        return (fromUtc, toUtc);
    }

    private (long minMs, long maxMs)? GetLoadedCandleRange()
    {
        if (ChartCandles.Count == 0)
        {
            return null;
        }

        return (ChartCandles.Min(c => c.Time), ChartCandles.Max(c => c.Time));
    }

    private void MergeCandles(IEnumerable<CandlePoint> candles)
    {
        var mergedByTime = new SortedDictionary<long, CandlePoint>();
        foreach (var existing in ChartCandles)
        {
            mergedByTime[existing.Time] = existing;
        }

        foreach (var candle in candles)
        {
            mergedByTime[candle.Time] = candle;
        }

        if (mergedByTime.Count == 0)
        {
            return;
        }

        ChartCandles.Clear();
        foreach (var candle in mergedByTime.Values)
        {
            ChartCandles.Add(candle);
        }

        while (ChartCandles.Count > MaxChartCandles)
        {
            ChartCandles.RemoveAt(0);
        }
    }

    private string? GetCurrentSymbol()
    {
        var baseAsset = Position?.BaseAsset?.Trim();
        var quoteAsset = Position?.QuoteAsset?.Trim();
        if (string.IsNullOrWhiteSpace(baseAsset) || string.IsNullOrWhiteSpace(quoteAsset))
        {
            return null;
        }

        return $"{baseAsset}{quoteAsset}".Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
    }


    private void ResetPricingContextForPositionSwitch()
    {
        _selectedPrice = null;
        _livePrice = null;
        _currentPrice = null;
        _isLive = false;
        _currentSymbol = null;
        _valuationDate = DateTime.UtcNow.Date;
        SelectedDayOffset = 0;
        MaxExpiryDate = DateTime.UtcNow.Date;
        MaxExpiryDays = 0;
        ChartSelectedPrice = null;
    }

    internal static LegModel? FindMatchingLeg(IEnumerable<LegModel> legs, LegModel candidate)
    {
        return legs.FirstOrDefault(leg =>
            leg.Type == candidate.Type
            && IsDateMatch(leg.ExpirationDate, candidate.ExpirationDate)
            && IsStrikeMatch(leg.Strike, candidate.Strike));
    }

    private static bool IsStrikeMatch(decimal? left, decimal? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return true;
        }

        if (!left.HasValue || !right.HasValue)
        {
            return false;
        }

        return Math.Abs(left.Value - right.Value) < 0.01m;
    }

    private static bool IsDateMatch(DateTime? left, DateTime? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return true;
        }

        if (!left.HasValue || !right.HasValue)
        {
            return false;
        }

        return left.Value.Date == right.Value.Date;
    }

    private void QueueUiStateChanged()
    {
        CancellationToken token;
        lock (_uiUpdateLock)
        {
            _uiUpdateCts?.Cancel();
            _uiUpdateCts?.Dispose();
            _uiUpdateCts = new CancellationTokenSource();
            token = _uiUpdateCts.Token;
        }

        _ = RunUiDebounceAsync(token);
    }

    private async Task RunUiDebounceAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(UiUpdateDebounce, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        NotifyStateChanged();
    }

    private void CancelUiDebounce()
    {
        lock (_uiUpdateLock)
        {
            if (_uiUpdateCts is null)
            {
                return;
            }

            _uiUpdateCts.Cancel();
            _uiUpdateCts.Dispose();
            _uiUpdateCts = null;
        }
    }

    private static string BuildPositionKey(string symbol, decimal size)
    {
        var side = Math.Sign(size);
        return $"{symbol.Trim().ToUpperInvariant()}|{side}";
    }

    private static decimal DetermineSignedSize(ExchangePosition position)
    {
        var magnitude = Math.Abs(position.Size);
        if (magnitude < 0.0001m)
        {
            return 0m;
        }

        if (!string.IsNullOrWhiteSpace(position.Side))
        {
            var normalized = position.Side.Trim();
            if (string.Equals(normalized, "Sell", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Short", StringComparison.OrdinalIgnoreCase))
            {
                return -magnitude;
            }
        }

        return magnitude;
    }

    public ValueTask DisposeAsync()
    {
        return DisposeAsyncCore();
    }

    private async ValueTask DisposeAsyncCore()
    {
        try
        {
            if (_position is not null)
            {
                // Flush latest edits (for example notes) before canceling debounce queues.
                await _positionsPort.SavePositionAsync(_position);
            }
        }
        catch
        {
            // Ignore persistence failures during disposal.
        }

        Dispose();
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source, object gate)
    {
        CancellationTokenSource? toDispose;
        lock (gate)
        {
            toDispose = source;
            source = null;
        }

        if (toDispose is null)
        {
            return;
        }

        try
        {
            if (!toDispose.IsCancellationRequested)
            {
                toDispose.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            toDispose.Dispose();
        }
    }
}
