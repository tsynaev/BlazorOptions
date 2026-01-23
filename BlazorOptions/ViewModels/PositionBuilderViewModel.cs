using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using BlazorOptions.Services;
using BlazorOptions.Sync;

namespace BlazorOptions.ViewModels;

public class PositionBuilderViewModel : IAsyncDisposable
{
    private readonly OptionsService _optionsService;
    private readonly PositionStorageService _storageService;
    private readonly TradingHistoryStorageService _tradingHistoryStorageService;
    private readonly ExchangeTickerService _exchangeTickerService;
    private readonly OptionsChainService _optionsChainService;
    private readonly ActivePositionsService _activePositionsService;
    private readonly PositionSyncService _positionSyncService;
    private readonly LegsCollectionViewModelFactory _collectionFactory;
    private readonly ClosedPositionsViewModelFactory _closedPositionsFactory;
    private readonly INotifyUserService _notifyUserService;
    private bool _isInitialized;
    private bool _suppressSync;
    private IReadOnlyList<TradingHistoryEntry> _tradingHistoryEntries = Array.Empty<TradingHistoryEntry>();



    public double? SelectedPrice => SelectedPosition?.SelectedPrice;

    public double? LivePrice => SelectedPosition?.LivePrice;

    public bool IsLive => SelectedPosition?.IsLive ?? true;

    public DateTime ValuationDate => SelectedPosition?.ValuationDate ?? DateTime.UtcNow.Date;

    public DateTime MaxExpiryDate { get; private set; } = DateTime.UtcNow.Date;

    public int MaxExpiryDays { get; private set; }

    public int SelectedDayOffset { get; private set; }

    public string DaysToExpiryLabel => $"{SelectedDayOffset} days";

    public PositionBuilderViewModel(
        OptionsService optionsService,
        PositionStorageService storageService,
        TradingHistoryStorageService tradingHistoryStorageService,
        ExchangeTickerService exchangeTickerService,
        OptionsChainService optionsChainService,
        ActivePositionsService activePositionsService,
        PositionSyncService positionSyncService,
        LegsCollectionViewModelFactory collectionFactory,
        ClosedPositionsViewModelFactory closedPositionsFactory,
        INotifyUserService notifyUserService)
    {
        _optionsService = optionsService;
        _storageService = storageService;
        _tradingHistoryStorageService = tradingHistoryStorageService;
        _exchangeTickerService = exchangeTickerService;
        _optionsChainService = optionsChainService;
        _activePositionsService = activePositionsService;
        _positionSyncService = positionSyncService;
        _collectionFactory = collectionFactory;
        _closedPositionsFactory = closedPositionsFactory;
        _notifyUserService = notifyUserService;
    }

    public ObservableCollection<PositionModel> Positions { get; } = new();


    public PositionViewModel? SelectedPosition { get; private set; }


    public EChartOptions ChartConfig { get; private set; } = new(Guid.Empty, Array.Empty<double>(), Array.Empty<string>(), null, Array.Empty<ChartCollectionSeries>(), 0, 0);

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        var storedPositions = await _storageService.LoadPositionsAsync();
        var deletedPositionIds = await _storageService.LoadDeletedPositionsAsync();
        _tradingHistoryEntries = Array.Empty<TradingHistoryEntry>();
        await _activePositionsService.InitializeAsync();

        if (storedPositions.Count == 0)
        {
            var defaultPosition = CreateDefaultPosition();
            Positions.Add(defaultPosition);
            SetSelectedPosition(defaultPosition);
            await InitializeSelectedPositionAsync();
            UpdateChart();

            await PersistPositionsAsync();
            UpdateLegTickerSubscription();
            await SelectedPosition!.EnsureLiveSubscriptionAsync();
            await _positionSyncService.QueueLocalSnapshotAsync(Positions, deletedPositionIds);
            await _positionSyncService.InitializeAsync(ApplyServerSnapshotAsync, ApplyServerItemAsync);
            return;
        }

        foreach (var position in storedPositions)
        {
            NormalizeCollections(position);
            Positions.Add(position);
        }

        SetSelectedPosition(Positions.FirstOrDefault());
        await InitializeSelectedPositionAsync();
        UpdateChart();

        UpdateLegTickerSubscription();
        await SelectedPosition!.EnsureLiveSubscriptionAsync();
        await _positionSyncService.QueueLocalSnapshotAsync(Positions, deletedPositionIds);
        await _positionSyncService.InitializeAsync(ApplyServerSnapshotAsync, ApplyServerItemAsync);
    }



    public async Task AddPositionAsync(string? name = null, string? baseAsset = null, string? quoteAsset = null, bool includeSampleLegs = true)
    {
        var position = CreateDefaultPosition(name ?? $"Position {Positions.Count + 1}", baseAsset, quoteAsset, includeSampleLegs);
        Positions.Add(position);
        SetSelectedPosition(position);
        await InitializeSelectedPositionAsync();
        UpdateChart();

        await PersistPositionsAsync();
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

        SetSelectedPosition(position);
        await InitializeSelectedPositionAsync();
        UpdateChart();

        // Don't reconnect ticker or refresh chain when switching tabs.
        UpdateLegTickerSubscription(refresh: false);
        await SelectedPosition!.EnsureLiveSubscriptionAsync();
        await Task.CompletedTask;
        return true;
    }

    public async Task UpdateNameAsync(PositionModel position, string name)
    {
        position.Name = name;
        await PersistPositionsAsync(position);
        if (SelectedPosition?.Position?.Id == position.Id)
        {
        }

        UpdateLegTickerSubscription();
    }

    public async Task AddCollectionAsync()
    {
        if (SelectedPosition is null)
        {
            return;
        }

        await SelectedPosition.AddCollectionAsync();
    }

    public async Task DuplicateCollectionAsync()
    {
        if (SelectedPosition is null || SelectedPosition.Collections.Count == 0)
        {
            return;
        }

        var collection = SelectedPosition.Collections.First().Collection;
        await SelectedPosition.DuplicateCollectionAsync(collection);
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

    public async Task PersistPositionsAsync(PositionModel? changedPosition = null)
    {
        await _storageService.SavePositionsAsync(Positions);
        if (!_suppressSync)
        {
            var positionToSync = changedPosition ?? SelectedPosition?.Position;
            if (positionToSync is null)
            {
                return;
            }

            if (SelectedPosition?.Position?.Id != positionToSync.Id)
            {
                return;
            }

            await _positionSyncService.NotifyLocalChangeAsync(positionToSync);
        }
    }

    public void UpdateChart()
    {
        var position = SelectedPosition;
        var collections = position?.Collections ?? Enumerable.Empty<LegsCollectionViewModel>();
        var allLegs = collections.SelectMany(collection => collection.Legs).ToList();
        var visibleCollections = collections.Where(collection => collection.IsVisible).ToList();
        var rangeLegs = visibleCollections.SelectMany(collection => collection.Legs).ToList();
        var closedPositionsTotal = GetClosedPositionsTotal(position);

        if (rangeLegs.Count == 0)
        {
            rangeLegs = allLegs;
        }

        RefreshValuationDateBounds(allLegs);
        var valuationDate = ValuationDate;
        var rangeCalculationLegs = ResolveLegsForCalculation(rangeLegs).Where(leg => leg.IsIncluded).ToList();
        var (xs, _, _) = _optionsService.GeneratePosition(rangeCalculationLegs, 180, valuationDate);
        var labels = xs.Select(x => x.ToString("0")).ToArray();
        var displayPrice = GetEffectivePrice();

        var chartCollections = new List<ChartCollectionSeries>();
        var minProfit = 0.0;
        var maxProfit = 0.0;
        var hasProfit = false;

        foreach (var collection in collections)
        {
            var collectionLegs = ResolveLegsForCalculation(collection.Legs).Where(leg => leg.IsIncluded).ToList();
            var profits = xs.Select(price => _optionsService.CalculateTotalProfit(collectionLegs, price)).ToArray();
            var theoreticalProfits = xs.Select(price => _optionsService.CalculateTotalTheoreticalProfit(collectionLegs, price, valuationDate)).ToArray();
            var tempPnl = collectionLegs.Any()
                ? _optionsService.CalculateTotalTheoreticalProfit(collectionLegs, displayPrice, valuationDate)
                : (double?)null;
            var tempExpiryPnl = collectionLegs.Any()
                ? _optionsService.CalculateTotalProfit(collectionLegs, displayPrice)
                : (double?)null;

            if (Math.Abs(closedPositionsTotal) > 0.0001)
            {
                for (var i = 0; i < profits.Length; i++)
                {
                    profits[i] += closedPositionsTotal;
                }

                for (var i = 0; i < theoreticalProfits.Length; i++)
                {
                    theoreticalProfits[i] += closedPositionsTotal;
                }

                if (tempPnl.HasValue)
                {
                    tempPnl = tempPnl.Value + closedPositionsTotal;
                }

                if (tempExpiryPnl.HasValue)
                {
                    tempExpiryPnl = tempExpiryPnl.Value + closedPositionsTotal;
                }
            }

            if (collection.IsVisible)
            {
                foreach (var value in profits)
                {
                    if (!hasProfit)
                    {
                        minProfit = value;
                        maxProfit = value;
                        hasProfit = true;
                    }
                    else
                    {
                        minProfit = Math.Min(minProfit, value);
                        maxProfit = Math.Max(maxProfit, value);
                    }
                }

                foreach (var value in theoreticalProfits)
                {
                    if (!hasProfit)
                    {
                        minProfit = value;
                        maxProfit = value;
                        hasProfit = true;
                    }
                    else
                    {
                        minProfit = Math.Min(minProfit, value);
                        maxProfit = Math.Max(maxProfit, value);
                    }
                }

                if (tempPnl.HasValue)
                {
                    minProfit = Math.Min(minProfit, tempPnl.Value);
                    maxProfit = Math.Max(maxProfit, tempPnl.Value);
                }

                if (tempExpiryPnl.HasValue)
                {
                    minProfit = Math.Min(minProfit, tempExpiryPnl.Value);
                    maxProfit = Math.Max(maxProfit, tempExpiryPnl.Value);
                }
            }

            chartCollections.Add(new ChartCollectionSeries(
                collection.Collection.Id,
                collection.Name,
                collection.Color,
                collection.IsVisible,
                profits,
                theoreticalProfits,
                tempPnl,
                tempExpiryPnl));
        }

        if (!hasProfit)
        {
            minProfit = -10;
            maxProfit = 10;
        }

        var range = Math.Abs(maxProfit - minProfit);
        var padding = Math.Max(10, range * 0.1);
        var positionId = position?.Position.Id ?? Guid.Empty;

        ChartConfig = new EChartOptions(positionId, xs, labels, displayPrice, chartCollections, minProfit - padding, maxProfit + padding);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> RefreshTradingHistoryAsync(IEnumerable<ClosedPositionModel>? closedPositions = null)
    {
        if (closedPositions is null)
        {
            _tradingHistoryEntries = Array.Empty<TradingHistoryEntry>();
            return _tradingHistoryEntries;
        }

        var models = closedPositions
            .Where(position => position is not null && !string.IsNullOrWhiteSpace(position.Symbol))
            .ToList();

        if (models.Count == 0)
        {
            _tradingHistoryEntries = Array.Empty<TradingHistoryEntry>();
            return _tradingHistoryEntries;
        }

        var entries = new List<TradingHistoryEntry>();
        var addedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in models)
        {
            var symbol = model.Symbol.Trim();
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            var symbolEntries = await _tradingHistoryStorageService.LoadBySymbolAsync(symbol);
            foreach (var entry in symbolEntries)
            {
                var key = entry.Id;
                if (string.IsNullOrWhiteSpace(key) || addedKeys.Add(key))
                {
                    entries.Add(entry);
                }
            }
        }

        _tradingHistoryEntries = entries;
        return _tradingHistoryEntries;
    }

    private double GetClosedPositionsTotal(PositionViewModel? position)
    {
        if (position is null || !position.Position.IncludeClosedPositions || position.Position.ClosedPositions is null)
        {
            return 0;
        }

        return position.Position.ClosedPositionsNetTotal;
    }

    public double? GetLegMarkIv(LegModel leg, string? baseAsset = null)
    {
        var resolvedBaseAsset = baseAsset ?? SelectedPosition?.Position?.BaseAsset;
        var ticker = _optionsChainService.FindTickerForLeg(leg, resolvedBaseAsset);
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



    public void SetSelectedPrice(double price)
    {
        UpdateSelectedPrice(price, refresh: true);
    }

    public void UpdateSelectedPrice(double? price, bool refresh)
    {
        if (SelectedPosition is null)
        {
            return;
        }

        SelectedPosition.SetSelectedPrice(price);
        if (!refresh)
        {
            return;
        }

        UpdateChart();
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
        await _storageService.MarkDeletedPositionAsync(position.Id);

        SetSelectedPosition(Positions.FirstOrDefault());

        await InitializeSelectedPositionAsync();

        UpdateChart();

        await PersistPositionsAsync();
        await _positionSyncService.NotifyLocalChangeAsync(position, true);
        return true;
    }

    private PositionModel CreateDefaultPosition(string? name = null, string? baseAsset = null, string? quoteAsset = null, bool includeSampleLegs = true)
    {
        var position = new PositionModel
        {
            Name = name ?? "Position",
            BaseAsset = string.IsNullOrWhiteSpace(baseAsset) ? "ETH" : baseAsset.Trim().ToUpperInvariant(),
            QuoteAsset = string.IsNullOrWhiteSpace(quoteAsset) ? "USDT" : quoteAsset.Trim().ToUpperInvariant()
        };

        var collection = PositionViewModel.CreateCollection(position, PositionViewModel.GetNextCollectionName(position));
        if (includeSampleLegs)
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

        if (position.ClosedPositions is null)
        {
            position.ClosedPositions = new ObservableCollection<ClosedPositionModel>();
        }
    }

    private void SetSelectedPosition(PositionModel? position)
    {
        if (SelectedPosition is not null)
        {
            SelectedPosition.Dispose();
        }

        SelectedPosition = position is null
            ? null
            : new PositionViewModel(
                this,
                _collectionFactory,
                _closedPositionsFactory,
                _notifyUserService,
                _exchangeTickerService,
                _activePositionsService,
                position);
    }

    private Task InitializeSelectedPositionAsync()
    {
        return SelectedPosition?.InitializeAsync() ?? Task.CompletedTask;
    }

    private double ResolveLegEntryPrice(LegViewModel leg)
    {
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

    private static bool IsStrikeMatch(double? left, double? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return true;
        }

        if (!left.HasValue || !right.HasValue)
        {
            return false;
        }

        return Math.Abs(left.Value - right.Value) < 0.01;
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

    private double GetEffectivePrice()
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

        return 0;
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
        _optionsChainService.TrackLegs(EnumerateAllLegs(), baseAsset);

        if (refresh)
        {
            await _optionsChainService.RefreshAsync(baseAsset);
            _optionsChainService.TrackLegs(EnumerateAllLegs(), baseAsset);

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
        var priceChanged = !model.Price.HasValue || Math.Abs(resolvedPrice - model.Price.Value) > 0.0001;
        if (!priceChanged && Math.Abs(resolvedIv - originalIv) < 0.0001)
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

    private static double NormalizeIv(double value)
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
        if (SelectedPosition is not null)
        {
            await SelectedPosition.StopTickerAsync();
        }
        await _positionSyncService.DisposeAsync();
    }

    private async Task ApplyServerSnapshotAsync(PositionSnapshotPayload payload, DateTime occurredUtc)
    {
        _ = occurredUtc;
        _suppressSync = true;
        try
        {
            if (ArePositionsEquivalent(payload.Positions, Positions))
            {
                return;
            }

            Positions.Clear();
            if (payload.Positions.Count == 0)
            {
                var defaultPosition = CreateDefaultPosition();
                Positions.Add(defaultPosition);
            }
            else
            {
                foreach (var position in payload.Positions)
                {
                    NormalizeCollections(position);
                    Positions.Add(position);
                }
            }

            SetSelectedPosition(Positions.FirstOrDefault());
            await InitializeSelectedPositionAsync();

            UpdateChart();
            await PersistPositionsAsync();
            UpdateLegTickerSubscription();
            OnChange?.Invoke();
            await PruneDeletedPositionsAsync();

        }
        finally
        {
            _suppressSync = false;
        }
    }

    private async Task ApplyServerItemAsync(PositionItemSnapshotPayload payload, DateTime occurredUtc)
    {
        _ = occurredUtc;
        _suppressSync = true;
        try
        {
            var existingIndex = Positions.ToList().FindIndex(position => position.Id == payload.PositionId);
            var wasSelected = SelectedPosition?.Position?.Id == payload.PositionId;

            if (payload.IsDeleted)
            {
                if (existingIndex >= 0)
                {
                    Positions.RemoveAt(existingIndex);
                }
                await _storageService.RemoveDeletedPositionsAsync(new[] { payload.PositionId });
            }
            else if (payload.Position is not null)
            {
                NormalizeCollections(payload.Position);
                if (existingIndex >= 0)
                {
                    Positions.RemoveAt(existingIndex);
                    Positions.Insert(existingIndex, payload.Position);
                }
                else
                {
                    Positions.Add(payload.Position);
                }
            }

            if (Positions.Count == 0)
            {
                SetSelectedPosition(null);
            }
            else if (wasSelected || SelectedPosition?.Position is null)
            {
                SetSelectedPosition(Positions.FirstOrDefault(position => position.Id == payload.PositionId) ??
                                   Positions.FirstOrDefault());
                await InitializeSelectedPositionAsync();
            }

            UpdateChart();
            OnChange?.Invoke();
            try
            {
                await PersistPositionsAsync();
            }
            catch
            {
            }

            UpdateLegTickerSubscription();
        }
        finally
        {
            _suppressSync = false;
        }
    }

    private async Task PruneDeletedPositionsAsync()
    {
        var deletedIds = await _storageService.LoadDeletedPositionsAsync();
        if (deletedIds.Count == 0)
        {
            return;
        }

        var activeIds = Positions.Select(position => position.Id).ToHashSet();
        var resolved = deletedIds.Where(id => !activeIds.Contains(id)).ToList();
        if (resolved.Count > 0)
        {
            await _storageService.RemoveDeletedPositionsAsync(resolved);
        }
    }

    private static bool ArePositionsEquivalent(IReadOnlyList<PositionModel> incoming, IEnumerable<PositionModel> current)
    {
        var incomingJson = JsonSerializer.Serialize(incoming, SyncJson.SerializerOptions);
        var currentJson = JsonSerializer.Serialize(current, SyncJson.SerializerOptions);
        return string.Equals(incomingJson, currentJson, StringComparison.Ordinal);
    }

}











