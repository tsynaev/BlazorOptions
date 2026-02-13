using System.Collections.ObjectModel;
using BlazorChart.Models;
using BlazorOptions.API.Positions;
using BlazorOptions.Diagnostics;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class PositionBuilderViewModel : IAsyncDisposable
{
    private static readonly TimeSpan CandleBucket = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultCandlesWindow = TimeSpan.FromHours(48);
    private const int MaxChartCandles = 5000;
    private readonly OptionsService _optionsService;
    private readonly IPositionsPort _positionsPort;
    private readonly IExchangeService _exchangeService;
    private readonly LegsCollectionViewModelFactory _collectionFactory;
    private readonly ClosedPositionsViewModelFactory _closedPositionsFactory;
    private readonly INotifyUserService _notifyUserService;
    private bool _isInitialized;
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

    private bool _isDisposed;


    public decimal? SelectedPrice => SelectedPosition?.SelectedPrice;

    public decimal? LivePrice => SelectedPosition?.LivePrice;

    public bool IsLive => SelectedPosition?.IsLive ?? false;

    public DateTime ValuationDate => SelectedPosition?.ValuationDate ?? DateTime.UtcNow.Date;

    public DateTime MaxExpiryDate { get; private set; } = DateTime.UtcNow.Date;

    public int MaxExpiryDays { get; private set; }

    public int SelectedDayOffset { get; private set; }

    public string DaysToExpiryLabel => $"{SelectedDayOffset} days";

    public event Action<decimal?>? LivePriceChanged;

    public PositionBuilderViewModel(
        OptionsService optionsService,
        IPositionsPort positionsPort,
        IExchangeService exchangeService,
        LegsCollectionViewModelFactory collectionFactory,
        ClosedPositionsViewModelFactory closedPositionsFactory,
        INotifyUserService notifyUserService)
    {
        _optionsService = optionsService;
        _positionsPort = positionsPort;
        _exchangeService = exchangeService;
        _collectionFactory = collectionFactory;
        _closedPositionsFactory = closedPositionsFactory;
        _notifyUserService = notifyUserService;
    }

    public ObservableCollection<PositionModel> Positions { get; } = new();


    public PositionViewModel? SelectedPosition { get; private set; }


    public ObservableCollection<StrategySeries> ChartStrategies { get; } = new();

    public ObservableCollection<PriceMarker> ChartMarkers { get; } = new();
    public ObservableCollection<CandlePoint> ChartCandles { get; } = new();

    public double? ChartSelectedPrice { get; private set; }
    public ChartRange? ChartRange => _chartRangeOverride;
    public TimeRange? ChartTimeRange => _chartTimeRange;
    public bool ShowCandles { get; private set; }

    public async Task InitializeAsync(Guid? preferredPositionId = null)
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        var storedPositions = await _positionsPort.LoadPositionsAsync();

        if (storedPositions.Count == 0)
        {
            var defaultPosition = CreateDefaultPosition();
            Positions.Add(defaultPosition);
            await SetSelectedPositionAsync(defaultPosition);

            _ = ScheduleInitialChartUpdateAsync();

            UpdateLegTickerSubscription();
            await SelectedPosition!.EnsureLiveSubscriptionAsync();
            await PersistPositionAsync(defaultPosition);
            return;
        }

        foreach (var position in storedPositions)
        {
            NormalizeCollections(position);
            Positions.Add(position);
        }

        var selectedPosition = preferredPositionId.HasValue
            ? Positions.FirstOrDefault(position => position.Id == preferredPositionId.Value)
            : null;

        selectedPosition ??= Positions.FirstOrDefault();



        await SetSelectedPositionAsync(selectedPosition);

        _ = ScheduleInitialChartUpdateAsync();

        UpdateLegTickerSubscription();
        await SelectedPosition!.EnsureLiveSubscriptionAsync();
    }



    public async Task AddPositionAsync(string? name = null, string? baseAsset = null, string? quoteAsset = null, bool includeSampleLegs = true, IReadOnlyList<LegModel>? initialLegs = null)
    {
        PositionModel position = CreateDefaultPosition(name ?? $"Position {Positions.Count + 1}", baseAsset, quoteAsset, includeSampleLegs, initialLegs);
        Positions.Add(position);
        await SetSelectedPositionAsync(position);

        UpdateChart();

        await PersistPositionAsync(position);
        UpdateLegTickerSubscription();
        await SelectedPosition!.EnsureLiveSubscriptionAsync();
    }

    public async Task<bool> SelectPositionAsync(Guid positionId)
    {
        var position = Positions.FirstOrDefault(p => p.Id == positionId);

        if (position is null)
        {
            return false;
        }

        await SetSelectedPositionAsync(position);

        UpdateChart();

        // Don't reconnect ticker or refresh chain when switching tabs.
        UpdateLegTickerSubscription(refresh: false);
        await SelectedPosition!.EnsureLiveSubscriptionAsync();
        await Task.CompletedTask;
        return true;
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

    public async Task UpdateNameAsync(PositionModel position, string name)
    {
        position.Name = name;
        await PersistPositionAsync(position);
        if (SelectedPosition?.Position?.Id == position.Id)
        {
        }

        UpdateLegTickerSubscription();
    }

    public void UpdateChartRange(ChartRange range)
    {
        _chartRangeOverride = range;
        if (SelectedPosition?.Position is not null)
        {
            SelectedPosition.Position.ChartRange = range;
            _ = QueuePersistPositionsAsync(SelectedPosition.Position);
        }
        UpdateChart();
        OnChange?.Invoke();
    }



    public async Task UpdateCollectionVisibilityAsync(Guid collectionId, bool isVisible)
    {
        if (SelectedPosition is null)
        {
            return;
        }

        var collection = SelectedPosition.Collections.FirstOrDefault(item => item.Collection.Id == collectionId);
        if (collection is null)
        {
            return;
        }

        await collection.SetVisibilityAsync(isVisible);
    }

    public async Task<bool> RemoveCollectionAsync(Guid collectionId)
    {
        if (SelectedPosition is null)
        {
            return false;
        }

        var collection = SelectedPosition.Collections.FirstOrDefault(item => item.Collection.Id == collectionId);
        if (collection is null)
        {
            return false;
        }

        return await SelectedPosition.RemoveCollectionAsync(collection.Collection);
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
                _queuedPersistPosition = SelectedPosition?.Position;
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

    internal void NotifyLivePriceChanged(decimal? price)
    {
        if (IsLive)
        {
            UpdateChartSelectedPrice(price);
        }

        LivePriceChanged?.Invoke(price);
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

        var utc = timestampUtc.Kind == DateTimeKind.Utc
            ? timestampUtc
            : timestampUtc.ToUniversalTime();
        var bucketTime = AlignToBucket(utc, CandleBucket);
        var bucketMs = new DateTimeOffset(bucketTime).ToUnixTimeMilliseconds();
        var value = (double)price.Value;

        if (ChartCandles.Count > 0 && _lastCandleBucketTime == bucketMs)
        {
            var last = ChartCandles[ChartCandles.Count - 1];
            var updated = last with
            {
                High = Math.Max(last.High, value),
                Low = Math.Min(last.Low, value),
                Close = value
            };
            ChartCandles[ChartCandles.Count - 1] = updated;
            return;
        }

        ChartCandles.Add(new CandlePoint(bucketMs, value, value, value, value));
        _lastCandleBucketTime = bucketMs;

        while (ChartCandles.Count > MaxChartCandles)
        {
            ChartCandles.RemoveAt(0);
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

        if (target is null && SelectedPosition?.Position is null)
        {
            return;
        }

        try
        {
            var position = target ?? SelectedPosition?.Position;
            if (position is not null)
            {
                await PersistPositionAsync(position);
            }
        }
        catch
        {
            // ignore to keep background persistence from crashing
        }
    }

    public void UpdateChart()
    {
        using var activity = ActivitySources.Telemetry.StartActivity($"{nameof(PositionBuilderViewModel)}.{nameof(UpdateChart)}");

        var position = SelectedPosition;
        if (position?.Position is null)
        {
            return;
        }

        var collections = position.Collections ?? Enumerable.Empty<LegsCollectionViewModel>();
        var allLegs = collections.SelectMany(collection => collection.Legs).ToList();
        var visibleCollections = collections.Where(collection => collection.IsVisible).ToList();
        var rangeLegs = visibleCollections.SelectMany(collection => collection.Legs).ToList();
        var closedPositionsTotal = position.Position.Closed.Include ? position.Position.Closed.TotalNet : 0;

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
                showBreakEvens: true,
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
        var resolvedBaseAsset = baseAsset ?? SelectedPosition?.Position?.BaseAsset;
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



    public void SetSelectedPrice(decimal price)
    {
        UpdateSelectedPrice(price, refresh: true);
    }

    public void UpdateSelectedPrice(decimal? price, bool refresh)
    {
        if (SelectedPosition is null)
        {
            return;
        }

        SelectedPosition.SetSelectedPrice(price);
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

        if (SelectedPosition is null)
        {
            return;
        }

        SelectedPosition.SetIsLive(isEnabled);

        if (!IsLive)
        {
            await SelectedPosition.StopTickerAsync();
            UpdateChart();
            OnChange?.Invoke();
            return;
        }

        UpdateLegTickerSubscription();
        SyncChartSelectedPrice();
        OnChange?.Invoke();
    }

    public void SetValuationDateFromOffset(int dayOffset)
    {
        var clampedOffset = Math.Clamp(dayOffset, 0, MaxExpiryDays);
        SelectedDayOffset = clampedOffset;
        SelectedPosition?.SetValuationDate(DateTime.UtcNow.Date.AddDays(clampedOffset));

        UpdateChart();
    }

    public void SetValuationDate(DateTime date)
    {
        var today = DateTime.UtcNow.Date;
        var clampedDate = date.Date < today ? today : date.Date > MaxExpiryDate ? MaxExpiryDate : date.Date;
        SelectedPosition?.SetValuationDate(clampedDate);
        SelectedDayOffset = Math.Clamp((clampedDate - today).Days, 0, MaxExpiryDays);
        UpdateChart();
    }

    public void ResetValuationDateToToday()
    {
        SetValuationDate(DateTime.UtcNow.Date);
    }


    public async Task<bool> RemovePositionAsync(PositionModel position)
    {

        Positions.Remove(position);
        await _positionsPort.DeletePositionAsync(position.Id);

        await SetSelectedPositionAsync(Positions.FirstOrDefault());


        UpdateChart();

        if (SelectedPosition?.Position is not null)
        {
            await PersistPositionAsync(SelectedPosition.Position);
        }
        return true;
    }

    private Task PersistPositionAsync(PositionModel position)
    {
        return _positionsPort.SavePositionAsync(position);
    }

    private PositionModel CreateDefaultPosition(string? name = null, string? baseAsset = null, string? quoteAsset = null, bool includeSampleLegs = true, IReadOnlyList<LegModel>? initialLegs = null)
    {
        var position = new PositionModel
        {
            Name = name ?? "Position",
            BaseAsset = string.IsNullOrWhiteSpace(baseAsset) ? "ETH" : baseAsset.Trim().ToUpperInvariant(),
            QuoteAsset = string.IsNullOrWhiteSpace(quoteAsset) ? "USDT" : quoteAsset.Trim().ToUpperInvariant()
        };

        var collection = PositionViewModel.CreateCollection(position, PositionViewModel.GetNextCollectionName(position));
        if (initialLegs is not null && initialLegs.Count > 0)
        {
            foreach (var leg in initialLegs)
            {
                collection.Legs.Add(leg.Clone());
            }
        }
        else if (includeSampleLegs)
        {
            collection.Legs.Add(new LegModel
            {
                Type = LegType.Call,
                Strike = 3400,
                Price = 180,
                Size = 1,
                ImpliedVolatility = 75,
                ExpirationDate = DateTime.UtcNow.Date.AddMonths(2)
            });

            collection.Legs.Add(new LegModel
            {
                Type = LegType.Put,
                Strike = 3200,
                Price = 120,
                Size = 1,
                ImpliedVolatility = 70,
                ExpirationDate = DateTime.UtcNow.Date.AddMonths(2)
            });
        }

        position.Collections.Add(collection);

        return position;
    }

    private void NormalizeCollections(PositionModel position)
    {
        if (position.Collections.Count == 0)
        {
            position.Collections.Add(PositionViewModel.CreateCollection(position, PositionViewModel.GetNextCollectionName(position)));
        }

        if (position.Closed is null)
        {
            position.Closed = new ClosedModel();
        }
    }

    private async Task SetSelectedPositionAsync(PositionModel? position)
    {
        if (SelectedPosition is not null)
        {
            SelectedPosition.Dispose();
        }

        if (position == null)
        {
            SelectedPosition = null;
            _chartRangeOverride = null;
            _chartTimeRange = null;
            ChartCandles.Clear();
            _lastCandleBucketTime = null;
            return;
        }

        await _exchangeService.OptionsChain.EnsureBaseAssetAsync(position.BaseAsset);

        SelectedPosition = new PositionViewModel(
                this,
                _positionsPort,
                _collectionFactory,
                _closedPositionsFactory,
                _notifyUserService,
                _exchangeService
                )
            {
                Position = position
            };

       _chartRangeOverride = position.ChartRange;
       _chartTimeRange = null;
       ChartCandles.Clear();
       _lastCandleBucketTime = null;

       await SelectedPosition.InitializeAsync();
       if (ShowCandles)
       {
           await LoadMissingCandlesForCurrentTimeRangeAsync();
       }
    }


    private decimal ResolveLegEntryPrice(LegViewModel leg)
    {
        if (leg.Leg.Type == LegType.Future && !leg.Leg.Price.HasValue)
        {
            return leg.CurrentPrice
                ?? leg.MarkPrice
                ?? 0;
        }

        return leg.Leg.Price
            ?? leg.PlaceholderPrice
            ?? leg.MarkPrice
            ?? 0;
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

  


    private void RefreshValuationDateBounds(IEnumerable<LegViewModel> legs)
    {
        var today = DateTime.UtcNow.Date;
        var expirations = legs
            .Select(l => l.Leg.ExpirationDate)
            .Where(exp => exp.HasValue)
            .Select(exp => exp!.Value)
            .ToList();

        MaxExpiryDate = expirations.Any()
            ? expirations.Max()
            : today;

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

        SelectedPosition?.SetValuationDate(clampedDate);
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

    private void UpdateLegTickerSubscription(bool refresh = true)
    {
        _ = UpdateLegTickerSubscriptionAsync(refresh);
    }

    private async Task UpdateLegTickerSubscriptionAsync(bool refresh)
    {
        if (SelectedPosition?.Position is null)
        {
            return;
        }

        var baseAsset = SelectedPosition?.Position.BaseAsset;
        _exchangeService.OptionsChain.TrackLegs(EnumerateAllLegs(), baseAsset);

        if (refresh)
        {
            await _exchangeService.OptionsChain.RefreshAsync(baseAsset);
            _exchangeService.OptionsChain.TrackLegs(EnumerateAllLegs(), baseAsset);

            UpdateChart();
            OnChange?.Invoke();
        }
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
        var originalIv = model.ImpliedVolatility ?? 0;
        var resolvedIv = impliedVolatility ?? 0;
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

    private static decimal NormalizeIv(decimal value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value <= 3 ? value * 100 : value;
    }

    private IEnumerable<LegModel> ResolveLegsForCalculation(IEnumerable<LegViewModel> legs)
    {
        var baseAsset = SelectedPosition?.Position?.BaseAsset;

        foreach (var leg in legs)
        {
            yield return ResolveLegForCalculation(leg, baseAsset);
        }
    }

    private IEnumerable<LegModel> EnumerateAllLegs()
    {
        if (SelectedPosition?.Position is null)
        {
            return Array.Empty<LegModel>();
        }

        return SelectedPosition.Position.Collections.SelectMany(collection => collection.Legs);
    }

    public event Action? OnChange;

    public void NotifyStateChanged()
    {
        OnChange?.Invoke();
    }

    public void RefreshLegTickerSubscription()
    {
        UpdateLegTickerSubscription();
    }

    public async ValueTask DisposeAsync()
    {
        _isDisposed = true;
        if (SelectedPosition is not null)
        {
            await SelectedPosition.StopTickerAsync();
        }
        CancelAndDispose(ref _persistQueueCts, _persistQueueLock);
        CancelAndDispose(ref _chartUpdateCts, _chartUpdateLock);
        CancelAndDispose(ref _candlesLoadCts, _candlesLoadLock);
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
            // ignore: racing disposal
        }
        finally
        {
            toDispose.Dispose();
        }
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
        var position = SelectedPosition?.Position;
        var baseAsset = position?.BaseAsset?.Trim();
        var quoteAsset = position?.QuoteAsset?.Trim();
        if (string.IsNullOrWhiteSpace(baseAsset) || string.IsNullOrWhiteSpace(quoteAsset))
        {
            return null;
        }

        return $"{baseAsset}{quoteAsset}"
            .Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToUpperInvariant();
    }
}











