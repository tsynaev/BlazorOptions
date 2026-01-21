using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using BlazorOptions.Services;
using BlazorOptions.Sync;
using System.Linq.Expressions;

namespace BlazorOptions.ViewModels;

public class PositionBuilderViewModel : IAsyncDisposable
{
    private readonly OptionsService _optionsService;
    private readonly PositionStorageService _storageService;
    private readonly ExchangeSettingsService _exchangeSettingsService;
    private readonly ExchangeTickerService _exchangeTickerService;
    private readonly OptionsChainService _optionsChainService;
    private readonly BybitPositionService _bybitPositionService;
    private readonly ActivePositionsService _activePositionsService;
    private readonly PositionSyncService _positionSyncService;
    private CancellationTokenSource? _tickerCts;
    private string? _currentSymbol;
    private TimeSpan _livePriceUpdateInterval = TimeSpan.FromMilliseconds(1000);
    private DateTime _lastLivePriceUpdateUtc = DateTime.MinValue;
    private static readonly TimeSpan LegPriceUpdateInterval = TimeSpan.FromSeconds(1);
    private readonly Dictionary<string, DateTime> _lastLegPriceUpdateUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _temporaryPnls = new(StringComparer.Ordinal);
    private static readonly Regex QuickAddAtRegex = new(@"@\s*(?<value>\d+(?:\.\d+)?)\s*(?<percent>%?)", RegexOptions.Compiled);
    private static readonly Regex QuickAddDateWithYearRegex = new(@"(?i)\b\d{1,2}[A-Z]{3}\d{2,4}\b", RegexOptions.Compiled);
    private static readonly Regex QuickAddDateWithoutYearRegex = new(@"(?i)\b\d{1,2}[A-Z]{3}\b", RegexOptions.Compiled);
    private static readonly Regex QuickAddNumericDateRegex = new(@"\b\d{4}\b", RegexOptions.Compiled);
    private bool _isInitialized;
    private bool _suppressSync;

    private static readonly ObservableCollection<LegModel> EmptyLegs = new();
    private static readonly ObservableCollection<LegsCollectionModel> EmptyCollections = new();
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
    private static readonly string[] BybitPositionCategories = { "option", "linear", "inverse" };
    private static readonly string[] PositionExpirationFormats = { "ddMMMyy", "ddMMMyyyy" };

    public double? SelectedPrice { get; private set; }

    public double? LivePrice { get; private set; }

    public bool HasSelectedPrice => SelectedPrice.HasValue;

    public bool IsLive { get; private set; } = true;

    public string QuickLegInput { get; set; } = string.Empty;

    public DateTime SelectedValuationDate { get; private set; } = DateTime.UtcNow.Date;

    public DateTime MaxExpiryDate { get; private set; } = DateTime.UtcNow.Date;

    public int MaxExpiryDays { get; private set; }

    public int SelectedDayOffset { get; private set; }

    public string DaysToExpiryLabel => $"{SelectedDayOffset} days";

    public PositionBuilderViewModel(
        OptionsService optionsService,
        PositionStorageService storageService,
        ExchangeSettingsService exchangeSettingsService,
        ExchangeTickerService exchangeTickerService,
        OptionsChainService optionsChainService,
        BybitPositionService bybitPositionService,
        ActivePositionsService activePositionsService,
        PositionSyncService positionSyncService)
    {
        _optionsService = optionsService;
        _storageService = storageService;
        _exchangeSettingsService = exchangeSettingsService;
        _exchangeTickerService = exchangeTickerService;
        _optionsChainService = optionsChainService;
        _bybitPositionService = bybitPositionService;
        _activePositionsService = activePositionsService;
        _positionSyncService = positionSyncService;
        _exchangeTickerService.PriceUpdated += HandlePriceUpdated;
        _optionsChainService.ChainUpdated += HandleChainUpdated;
        _optionsChainService.TickerUpdated += HandleTickerUpdated;
        _activePositionsService.PositionUpdated += HandleActivePositionUpdated;
        _activePositionsService.PositionsUpdated += HandleActivePositionsSnapshot;
    }

    public ObservableCollection<PositionModel> Positions { get; } = new();

    public PositionModel? SelectedPosition { get; private set; }

    public ObservableCollection<LegsCollectionModel> Collections => SelectedPosition?.Collections ?? EmptyCollections;

    public LegsCollectionModel? SelectedCollection { get; private set; }

    public ObservableCollection<LegModel> Legs => SelectedCollection?.Legs ?? EmptyLegs;

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
        await _activePositionsService.InitializeAsync();

        if (storedPositions.Count == 0)
        {
            var defaultPosition = CreateDefaultPosition();
            Positions.Add(defaultPosition);
            SelectedPosition = defaultPosition;
            SetSelectedCollection(defaultPosition);
            LivePrice = CalculateAnchorPrice(Legs);
            UpdateChart();
            UpdateTemporaryPnls();
            await PersistPositionsAsync();
            await StartTickerAsync();
            UpdateLegTickerSubscription();
            await _positionSyncService.QueueLocalSnapshotAsync(Positions, deletedPositionIds);
            await _positionSyncService.InitializeAsync(ApplyServerSnapshotAsync, ApplyServerItemAsync);
            return;
        }

        foreach (var position in storedPositions)
        {
            NormalizeCollections(position);
            Positions.Add(position);
        }

        SelectedPosition = Positions.FirstOrDefault();
        if (SelectedPosition is not null)
        {
            SetSelectedCollection(SelectedPosition);
        }
        LivePrice = CalculateAnchorPrice(Legs);
        UpdateChart();
        UpdateTemporaryPnls();
        await StartTickerAsync();
        UpdateLegTickerSubscription();
        await _positionSyncService.QueueLocalSnapshotAsync(Positions, deletedPositionIds);
        await _positionSyncService.InitializeAsync(ApplyServerSnapshotAsync, ApplyServerItemAsync);
    }

    public async Task AddLegAsync()
    {
        if (SelectedCollection is null)
        {
            return;
        }

        SelectedCollection.Legs.Add(new LegModel
        {
            Type = LegType.Call,
            Strike = 3300,
            Price = 150,
            Size = 1,
            ImpliedVolatility = 70,
            ExpirationDate = DateTime.UtcNow.Date.AddMonths(1)
        });

        UpdateTemporaryPnls();
        await PersistPositionsAsync();
        UpdateLegTickerSubscription();
    }

    public async Task<bool> AddLegFromTextAsync(string? input)
    {
        if (SelectedCollection is null || SelectedPosition is null)
        {
            Notify("Select a position before adding a leg.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            Notify("Enter a leg expression like '+1 C 3400' or '+1 P'.");
            return false;
        }

        var trimmed = input.Trim();
        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var size = 1.0;
        var tokenIndex = 0;

        if (tokens.Length > 0 && TryParseNumber(tokens[0], out var parsedSize))
        {
            size = parsedSize;
            tokenIndex = 1;
        }

        if (Math.Abs(size) < 0.0001)
        {
            Notify("Leg size must be non-zero.");
            return false;
        }

        if (!TryFindLegType(tokens, tokenIndex, out var type, out var typeIndex))
        {
            Notify("Specify an option type (CALL or PUT).");
            return false;
        }

        var strike = TryParseStrike(tokens, typeIndex);
        var expiration = TryParseExpirationDate(trimmed, out var parsedExpiration)
            ? parsedExpiration
            : (DateTime?)null;
        var (priceOverride, ivOverride) = ParsePriceOverrides(trimmed);

        var baseAsset = SelectedPosition.BaseAsset;
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            Notify("The position base asset is missing.");
            return false;
        }

        var chainTickers = await LoadChainTickersAsync(baseAsset);
        if (chainTickers.Count == 0)
        {
            Notify("Option chain is unavailable. Please refresh and try again.");
            return false;
        }

        var expirationDate = expiration ?? ResolveDefaultExpirationDate();
        var candidates = chainTickers
            .Where(ticker => ticker.Type == type && ticker.ExpirationDate.Date == expirationDate.Date)
            .ToList();

        if (candidates.Count == 0)
        {
            Notify($"No contracts found for {type} expiring on {expirationDate:ddMMMyy}.");
            return false;
        }

        var resolvedStrike = strike ?? ResolveAtmStrike(candidates);
        var matchingTickers = candidates
            .Where(ticker => Math.Abs(ticker.Strike - resolvedStrike) < 0.01)
            .ToList();

        if (matchingTickers.Count != 1)
        {
            Notify("Ambiguous contract selection. Please refine the input.");
            return false;
        }

        var tickerMatch = matchingTickers[0];

        var leg = new LegModel
        {
            Type = type,
            Strike = tickerMatch.Strike,
            ExpirationDate = expirationDate.Date,
            Size = size,
            Price = RoundPrice(priceOverride ?? tickerMatch.MarkPrice),
            ImpliedVolatility = ivOverride ?? 0
        };

        SelectedCollection.Legs.Add(leg);
        QuickLegInput = string.Empty;
        UpdateTemporaryPnls();
        UpdateChart();
        await PersistPositionsAsync();
        UpdateLegTickerSubscription();
        OnChange?.Invoke();
        return true;
    }

    public async Task UpdateLegsAsync(IEnumerable<LegModel> legs)
    {
        if (SelectedCollection is null)
        {
            return;
        }

        SelectedCollection.Legs.Clear();
        foreach (var leg in legs)
        {
            SelectedCollection.Legs.Add(leg);
        }

        UpdateTemporaryPnls();
        UpdateChart();
        await PersistPositionsAsync();
        UpdateLegTickerSubscription();
    }

    public async Task<bool> RemoveLegAsync(LegModel leg)
    {
        if (SelectedCollection is null)
        {
            return false;
        }

        if (SelectedCollection.Legs.Contains(leg))
        {
            SelectedCollection.Legs.Remove(leg);
            await PersistPositionsAsync();
            UpdateTemporaryPnls();
            UpdateLegTickerSubscription();
            return true;
        }

        return false;
    }

    public async Task AddPositionAsync(string? name = null, string? baseAsset = null, string? quoteAsset = null, bool includeSampleLegs = true)
    {
        var position = CreateDefaultPosition(name ?? $"Position {Positions.Count + 1}", baseAsset, quoteAsset, includeSampleLegs);
        Positions.Add(position);
        SelectedPosition = position;
        SetSelectedCollection(position);
        LivePrice = CalculateAnchorPrice(Legs);
        UpdateChart();
        UpdateTemporaryPnls();
        await PersistPositionsAsync();
        await UpdateTickerSubscriptionAsync();
        UpdateLegTickerSubscription();
    }

    public async Task<bool> SelectPositionAsync(Guid positionId)
    {
        var position = Positions.FirstOrDefault(p => p.Id == positionId);

        if (position is null)
        {
            return false;
        }

        SelectedPosition = position;
        SetSelectedCollection(position);
        LivePrice = CalculateAnchorPrice(Legs);
        UpdateChart();
        UpdateTemporaryPnls();
        await UpdateTickerSubscriptionAsync();
        UpdateLegTickerSubscription();
        await Task.CompletedTask;
        return true;
    }

    public async Task UpdateNameAsync(PositionModel position, string name)
    {
        position.Name = name;
        await PersistPositionsAsync(position);
        if (SelectedPosition?.Id == position.Id)
        {
            await UpdateTickerSubscriptionAsync();
        }

        UpdateLegTickerSubscription();
    }

    public async Task AddCollectionAsync()
    {
        if (SelectedPosition is null)
        {
            return;
        }

        var collection = CreateCollection(SelectedPosition, GetNextCollectionName(SelectedPosition));
        SelectedPosition.Collections.Add(collection);
        SetSelectedCollection(SelectedPosition, collection);
        await PersistPositionsAsync();
        UpdateTemporaryPnls();
        UpdateChart();
        UpdateLegTickerSubscription();
        OnChange?.Invoke();
    }

    public async Task DuplicateCollectionAsync()
    {
        if (SelectedPosition is null || SelectedCollection is null)
        {
            return;
        }

        var collection = CreateCollection(SelectedPosition, GetNextCollectionName(SelectedPosition), SelectedCollection.Legs.Select(CloneLeg));
        SelectedPosition.Collections.Add(collection);
        SetSelectedCollection(SelectedPosition, collection);
        await PersistPositionsAsync();
        UpdateTemporaryPnls();
        UpdateChart();
        UpdateLegTickerSubscription();
        OnChange?.Invoke();
    }


    public async Task AddBybitPositionsToCollectionAsync(
        PositionModel position,
        LegsCollectionModel collection,
        IReadOnlyList<BybitPosition> positions)
    {
        if (position is null || collection is null || positions.Count == 0)
        {
            return;
        }

        if (!Positions.Contains(position) || !position.Collections.Contains(collection))
        {
            return;
        }

        var baseAsset = position.BaseAsset?.Trim();
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            Notify("Specify a base asset before loading Bybit positions.");
            return;
        }

        var added = 0;

        foreach (var bybitPosition in positions)
        {
            if (Math.Abs(bybitPosition.Size) < 0.0001)
            {
                continue;
            }

            var leg = CreateLegFromBybitPosition(bybitPosition, baseAsset, bybitPosition.Category);
            if (leg is null)
            {
                continue;
            }

            var existing = FindMatchingLeg(collection.Legs, leg);
            if (existing is null)
            {
                collection.Legs.Add(leg);
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

        UpdateTemporaryPnls();
        UpdateChart();
        await PersistPositionsAsync(position);
        UpdateLegTickerSubscription();
        OnChange?.Invoke();
    }

    public async Task SelectCollectionAsync(Guid collectionId)
    {
        if (SelectedPosition is null)
        {
            return;
        }

        var collection = SelectedPosition.Collections.FirstOrDefault(item => item.Id == collectionId);
        if (collection is null)
        {
            return;
        }

        SetSelectedCollection(SelectedPosition, collection);
        LivePrice = CalculateAnchorPrice(Legs);
        UpdateChart();
        UpdateTemporaryPnls();
        await PersistPositionsAsync();
        UpdateLegTickerSubscription();
        OnChange?.Invoke();
    }

    public bool TrySetActiveCollection(Guid collectionId)
    {
        if (SelectedPosition is null)
        {
            return false;
        }

        var collection = SelectedPosition.Collections.FirstOrDefault(item => item.Id == collectionId);
        if (collection is null)
        {
            return false;
        }

        SelectedCollection = collection;
        return true;
    }

    public async Task UpdateCollectionVisibilityAsync(Guid collectionId, bool isVisible)
    {
        if (SelectedPosition is null)
        {
            return;
        }

        var collection = SelectedPosition.Collections.FirstOrDefault(item => item.Id == collectionId);
        if (collection is null)
        {
            return;
        }

        collection.IsVisible = isVisible;
        await PersistPositionsAsync();
        UpdateChart();
        OnChange?.Invoke();
    }

    public async Task<bool> RemoveCollectionAsync(Guid collectionId)
    {
        if (SelectedPosition is null)
        {
            return false;
        }

        if (SelectedPosition.Collections.Count <= 1)
        {
            Notify("At least one portfolio is required.");
            return false;
        }

        var collection = SelectedPosition.Collections.FirstOrDefault(item => item.Id == collectionId);
        if (collection is null)
        {
            return false;
        }

        SelectedPosition.Collections.Remove(collection);

        if (SelectedCollection?.Id == collectionId)
        {
            SetSelectedCollection(SelectedPosition);
        }

        await PersistPositionsAsync();
        UpdateTemporaryPnls();
        UpdateChart();
        UpdateLegTickerSubscription();
        OnChange?.Invoke();
        return true;
    }

    public async Task PersistPositionsAsync(PositionModel? changedPosition = null)
    {
        await _storageService.SavePositionsAsync(Positions);
        if (!_suppressSync)
        {
            var positionToSync = changedPosition ?? SelectedPosition;
            if (positionToSync is null)
            {
                return;
            }

            if (SelectedPosition?.Id != positionToSync.Id)
            {
                return;
            }

            await _positionSyncService.NotifyLocalChangeAsync(positionToSync);
        }
    }

    public void UpdateChart()
    {
        var position = SelectedPosition;
        var collections = position?.Collections ?? Enumerable.Empty<LegsCollectionModel>();
        var allLegs = collections.SelectMany(collection => collection.Legs).ToList();
        var visibleCollections = collections.Where(collection => collection.IsVisible).ToList();
        var rangeLegs = visibleCollections.SelectMany(collection => collection.Legs).ToList();

        if (rangeLegs.Count == 0)
        {
            rangeLegs = allLegs;
        }

        RefreshValuationDateBounds(allLegs);
        var valuationDate = SelectedValuationDate;
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
                collection.Id,
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
        var positionId = position?.Id ?? Guid.Empty;

        ChartConfig = new EChartOptions(positionId, xs, labels, displayPrice, chartCollections, minProfit - padding, maxProfit + padding);
    }

    public double? GetLegMarkIv(LegModel leg, string? baseAsset = null)
    {
        var resolvedBaseAsset = baseAsset ?? SelectedPosition?.BaseAsset;
        var ticker = _optionsChainService.FindTickerForLeg(leg, resolvedBaseAsset);
        if (ticker is null || ticker.MarkIv <= 0)
        {
            return null;
        }

        return NormalizeIv(ticker.MarkIv);
    }

    public double? GetLegLastPrice(LegModel leg, string? baseAsset = null)
    {
        var resolvedBaseAsset = baseAsset ?? SelectedPosition?.BaseAsset;
        var ticker = _optionsChainService.FindTickerForLeg(leg, resolvedBaseAsset);
        if (ticker is null)
        {
            return null;
        }

        var lastPrice = ticker.LastPrice > 0 ? ticker.LastPrice : ticker.MarkPrice;
        return lastPrice > 0 ? lastPrice : null;
    }

    public BidAsk GetLegBidAsk(LegModel leg, string? baseAsset = null)
    {
        var resolvedBaseAsset = baseAsset ?? SelectedPosition?.BaseAsset;
        var ticker = _optionsChainService.FindTickerForLeg(leg, resolvedBaseAsset);
        if (ticker is null)
        {
            return default;
        }

        var bid = ticker.BidPrice > 0 ? ticker.BidPrice : (double?)null;
        var ask = ticker.AskPrice > 0 ? ticker.AskPrice : (double?)null;
        return new BidAsk(bid, ask);
    }

    public double? GetLegMarkPrice(LegModel leg, string? baseAsset = null)
    {
        var resolvedBaseAsset = baseAsset ?? SelectedPosition?.BaseAsset;
        var ticker = _optionsChainService.FindTickerForLeg(leg, resolvedBaseAsset);
        if (ticker is null)
        {
            return null;
        }

        return ticker.MarkPrice > 0 ? ticker.MarkPrice : null;
    }

    public double? GetLegMarketPrice(LegModel leg, string? baseAsset = null)
    {
        var resolvedBaseAsset = baseAsset ?? SelectedPosition?.BaseAsset;
        var ticker = _optionsChainService.FindTickerForLeg(leg, resolvedBaseAsset);
        if (ticker is null)
        {
            return null;
        }

        if (leg.Size < 0 && ticker.BidPrice > 0)
        {
            return ticker.BidPrice;
        }

        if (leg.Size >= 0 && ticker.AskPrice > 0)
        {
            return ticker.AskPrice;
        }

        return ticker.MarkPrice > 0 ? ticker.MarkPrice : null;
    }

    public double GetLegTemporaryPnl(LegModel leg)
    {
        return _temporaryPnls.TryGetValue(leg.Id, out var value) ? value : 0;
    }

    public double? GetLegTemporaryPnlPercent(LegModel leg)
    {
        if (!leg.Price.HasValue)
        {
            return null;
        }

        var baseAsset = SelectedPosition?.BaseAsset;
        var entryPrice = ResolveLegEntryPrice(leg, baseAsset);
        var positionValue = entryPrice * leg.Size;
        if (Math.Abs(positionValue) < 0.0001)
        {
            return null;
        }

        var tempPnl = GetLegTemporaryPnl(leg);
        return tempPnl / Math.Abs(positionValue) * 100;
    }

    public string? GetLegSymbol(LegModel leg)
    {
        if (!string.IsNullOrWhiteSpace(leg.Symbol))
        {
            return leg.Symbol;
        }

        var baseAsset = SelectedPosition?.BaseAsset;
        var ticker = _optionsChainService.FindTickerForLeg(leg, baseAsset);
        if (ticker is null)
        {
            return null;
        }

        return ticker.Symbol;
    }

    public void SetSelectedPrice(double price)
    {
        UpdateSelectedPrice(price, refresh: true);
    }

    public void ClearSelectedPrice()
    {
        UpdateSelectedPrice(null, refresh: true);
    }

    public void UpdateSelectedPrice(double? price, bool refresh)
    {
        SelectedPrice = price;
        if (!refresh)
        {
            return;
        }

        UpdateTemporaryPnls();
        UpdateChart();
    }

    public async Task SetIsLiveAsync(bool isEnabled)
    {
        if (IsLive == isEnabled)
        {
            return;
        }

        IsLive = isEnabled;

        if (!IsLive)
        {
            await StopTickerAsync();
            if (!SelectedPrice.HasValue)
            {
                SelectedPrice = LivePrice ?? CalculateAnchorPrice(Legs);
            }
            UpdateTemporaryPnls();
            UpdateChart();
            OnChange?.Invoke();
            return;
        }

        await UpdateTickerSubscriptionAsync();
        UpdateLegTickerSubscription();
        OnChange?.Invoke();
    }

    public void SetValuationDateFromOffset(int dayOffset)
    {
        var clampedOffset = Math.Clamp(dayOffset, 0, MaxExpiryDays);
        SelectedDayOffset = clampedOffset;
        SelectedValuationDate = DateTime.UtcNow.Date.AddDays(clampedOffset);
        UpdateTemporaryPnls();
        UpdateChart();
    }

    public void SetValuationDate(DateTime date)
    {
        var today = DateTime.UtcNow.Date;
        var clampedDate = date.Date < today ? today : date.Date > MaxExpiryDate ? MaxExpiryDate : date.Date;
        SelectedValuationDate = clampedDate;
        SelectedDayOffset = Math.Clamp((SelectedValuationDate - today).Days, 0, MaxExpiryDays);
        UpdateTemporaryPnls();
        UpdateChart();
    }

    public void ResetValuationDateToToday()
    {
        SetValuationDate(DateTime.UtcNow.Date);
    }

    public void UpdateTemporaryPnls()
    {
        var price = GetEffectivePrice();
        var baseAsset = SelectedPosition?.BaseAsset;
        _temporaryPnls.Clear();

        foreach (var leg in Positions.SelectMany(position => position.Collections).SelectMany(collection => collection.Legs))
        {
            if (!leg.IsIncluded)
            {
                _temporaryPnls[leg.Id] = 0;
                continue;
            }

            if (IsLive && leg.Type != LegType.Future)
            {
                var ticker = _optionsChainService.FindTickerForLeg(leg, baseAsset);
                if (ticker is not null)
                {
                    var entryPrice = ResolveLegEntryPrice(leg, baseAsset);
                    _temporaryPnls[leg.Id] = (ticker.MarkPrice - entryPrice) * leg.Size;
                    continue;
                }
            }

            var calculationLeg = ResolveLegForCalculation(leg, baseAsset);
            _temporaryPnls[leg.Id] = _optionsService.CalculateLegTheoreticalProfit(calculationLeg, price, SelectedValuationDate);
        }
    }

    public async Task<bool> RemovePositionAsync(Guid positionId)
    {
        var positionIndex = Positions.ToList().FindIndex(p => p.Id == positionId);

        if (positionIndex < 0)
        {
            return false;
        }

        var removedPosition = Positions[positionIndex];
        var removedWasSelected = SelectedPosition?.Id == removedPosition.Id;
        Positions.RemoveAt(positionIndex);
        await _storageService.MarkDeletedPositionAsync(positionId);

        if (SelectedPosition?.Id == removedPosition.Id)
        {
            if (Positions.Count == 0)
            {
                SelectedPosition = null;
                SelectedCollection = null;
            }
            else
            {
                var nextIndex = Math.Min(positionIndex, Positions.Count - 1);
                SelectedPosition = Positions[nextIndex];
                SetSelectedCollection(SelectedPosition);
            }
        }

        LivePrice = CalculateAnchorPrice(Legs);
        UpdateChart();
        UpdateTemporaryPnls();
        await PersistPositionsAsync();
        if (removedWasSelected)
        {
            await _positionSyncService.NotifyLocalChangeAsync(removedPosition, true);
        }
        await UpdateTickerSubscriptionAsync();
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

        var collection = CreateCollection(position, GetNextCollectionName(position));
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
            position.Collections.Add(CreateCollection(position, GetNextCollectionName(position)));
        }
    }

    private void SetSelectedCollection(PositionModel position, LegsCollectionModel? collection = null)
    {
        var resolved = collection;
        if (resolved is null)
        {
            resolved = position.Collections.FirstOrDefault();
        }

        SelectedCollection = resolved ?? position.Collections.FirstOrDefault();
    }

    private LegsCollectionModel CreateCollection(PositionModel position, string name, IEnumerable<LegModel>? legs = null)
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

    private static string GetNextCollectionName(PositionModel position)
    {
        return $"Portfolio {position.Collections.Count + 1}";
    }

    private static string GetNextCollectionColor(PositionModel position)
    {
        var usedColors = position.Collections.Select(collection => collection.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var color in CollectionPalette)
        {
            if (!usedColors.Contains(color))
            {
                return color;
            }
        }

        return CollectionPalette[position.Collections.Count % CollectionPalette.Length];
    }

    private static LegModel CloneLeg(LegModel leg)
    {
        return new LegModel
        {
            Id = leg.IsReadOnly ? leg.Id : Guid.NewGuid().ToString(),
            IsIncluded = leg.IsIncluded,
            IsReadOnly = leg.IsReadOnly,
            Type = leg.Type,
            Strike = leg.Strike,
            ExpirationDate = leg.ExpirationDate,
            Size = leg.Size,
            Price = leg.Price,
            ImpliedVolatility = leg.ImpliedVolatility
        };
    }

    private double ResolveLegEntryPrice(LegModel leg, string? baseAsset)
    {
        if (leg.Price.HasValue)
        {
            return leg.Price.Value;
        }

        return GetLegMarketPrice(leg, baseAsset) ?? 0;
    }

    private static LegModel? FindMatchingLeg(IEnumerable<LegModel> legs, LegModel candidate)
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

    private LegModel? CreateLegFromBybitPosition(BybitPosition position, string baseAsset, string category)
    {
        if (string.IsNullOrWhiteSpace(position.Symbol))
        {
            return null;
        }

        DateTime? expiration = null;
        double? strike = null;
        var type = LegType.Future;
        if (string.Equals(category, "option", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParsePositionSymbol(position.Symbol, out var parsedBase, out var parsedExpiration, out var parsedStrike, out var parsedType))
            {
                return null;
            }

            if (!string.Equals(parsedBase, baseAsset, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            expiration = parsedExpiration;
            strike = parsedStrike;
            type = parsedType;
        }
        else
        {
            if (!position.Symbol.StartsWith(baseAsset, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (TryParseFutureExpiration(position.Symbol, out var parsedFutureExpiration))
            {
                expiration = parsedFutureExpiration;
            }
        }
       

        var size = DetermineSignedSize(position);
        if (Math.Abs(size) < 0.0001)
        {
            return null;
        }

        var price = position.AvgPrice;

        return new LegModel
        {
            IsReadOnly = true,
            Type = type,
            Strike = strike,
            ExpirationDate = expiration,
            Size = size,
            Price = price,
            ImpliedVolatility = null,
            Symbol = position.Symbol
        };
    }

    private static bool TryParsePositionSymbol(string symbol, out string baseAsset, out DateTime expiration, out double strike, out LegType type)
    {
        baseAsset = string.Empty;
        expiration = default;
        strike = 0;
        type = LegType.Call;

        var parts = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
        {
            return false;
        }

        baseAsset = parts[0];
        if (!DateTime.TryParseExact(parts[1], PositionExpirationFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedExpiration))
        {
            return false;
        }

        expiration = parsedExpiration.Date;

        if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out strike))
        {
            return false;
        }

        var typeToken = parts[3].Trim();
        type = typeToken.Equals("P", StringComparison.OrdinalIgnoreCase) ? LegType.Put : LegType.Call;
        return true;
    }

    private static bool TryParseFutureExpiration(string symbol, out DateTime expiration)
    {
        expiration = default;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var tokens = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length >= 2 &&
            DateTime.TryParseExact(tokens[1], PositionExpirationFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            expiration = parsed.Date;
            return true;
        }

        var match = Regex.Match(symbol, @"(\d{8}|\d{6})$");
        if (!match.Success)
        {
            return false;
        }

        var format = match.Groups[1].Value.Length == 8 ? "yyyyMMdd" : "yyMMdd";
        return DateTime.TryParseExact(match.Groups[1].Value, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out expiration);
    }

    private static double DetermineSignedSize(BybitPosition position)
    {
        var magnitude = Math.Abs(position.Size);
        if (magnitude < 0.0001)
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(position.Side))
        {
            var normalized = position.Side.Trim();
            if (string.Equals(normalized, "Sell", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Short", StringComparison.OrdinalIgnoreCase))
            {
                return -magnitude;
            }
        }

        return magnitude;
    }

    private static double CalculateAnchorPrice(IEnumerable<LegModel> legs)
    {
        var activeLegs = legs.Where(l => l.IsIncluded).ToList();

        if (activeLegs.Count == 0)
        {
            return 1000;
        }

        return activeLegs.Average(l => l.Strike.HasValue && l.Strike.Value > 0 ? l.Strike.Value : (l.Price ?? 0));
    }

    private void RefreshValuationDateBounds(IEnumerable<LegModel> legs)
    {
        var today = DateTime.UtcNow.Date;
        var expirations = legs
            .Select(l => l.ExpirationDate)
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
        var clampedDate = SelectedValuationDate == default ? today : SelectedValuationDate.Date;
        if (clampedDate < today)
        {
            clampedDate = today;
        }
        else if (clampedDate > MaxExpiryDate)
        {
            clampedDate = MaxExpiryDate;
        }

        var clampedOffset = Math.Clamp((clampedDate - today).Days, 0, MaxExpiryDays);
        var shouldUpdatePnls = clampedDate != SelectedValuationDate || clampedOffset != SelectedDayOffset;

        SelectedValuationDate = clampedDate;
        SelectedDayOffset = clampedOffset;

        if (shouldUpdatePnls)
        {
            UpdateTemporaryPnls();
        }
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

        return CalculateAnchorPrice(Legs);
    }

    private async Task StartTickerAsync()
    {
        if (SelectedPosition is null)
        {
            return;
        }

        await UpdateTickerSubscriptionAsync();
    }

    private async Task UpdateTickerSubscriptionAsync()
    {
        if (!IsLive || SelectedPosition is null)
        {
            await StopTickerAsync();
            return;
        }

        var symbol = NormalizeSymbol(SelectedPosition);
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        if (string.Equals(_currentSymbol, symbol, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentSymbol = symbol;
        _tickerCts?.Cancel();
        _tickerCts?.Dispose();
        _tickerCts = new CancellationTokenSource();

        var settings = await _exchangeSettingsService.LoadBybitSettingsAsync();
        var url = _exchangeTickerService.ResolveWebSocketUrl(settings.WebSocketUrl);
        _livePriceUpdateInterval = TimeSpan.FromMilliseconds(Math.Max(100, settings.LivePriceUpdateIntervalMilliseconds));
        _lastLivePriceUpdateUtc = DateTime.MinValue;

        var subscription = new ExchangeTickerSubscription("Bybit", symbol, url);
        await _exchangeTickerService.ConnectAsync(subscription, _tickerCts.Token);
    }

    private void UpdateLegTickerSubscription()
    {
        _ = UpdateLegTickerSubscriptionAsync();
    }

    private async Task UpdateLegTickerSubscriptionAsync()
    {
        if (SelectedPosition is null)
        {
            return;
        }

        var baseAsset = SelectedPosition.BaseAsset;
        _optionsChainService.TrackLegs(EnumerateAllLegs(), baseAsset);

        await _optionsChainService.RefreshAsync(baseAsset);
        _optionsChainService.TrackLegs(EnumerateAllLegs(), baseAsset);
    }

    private async Task StopTickerAsync()
    {
        _tickerCts?.Cancel();
        _tickerCts?.Dispose();
        _tickerCts = null;
        _currentSymbol = null;
        await _exchangeTickerService.DisconnectAsync();
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

    private void HandlePriceUpdated(object? sender, ExchangePriceUpdate update)
    {
        if (!IsLive)
        {
            return;
        }

        if (!string.Equals(update.Symbol, _currentSymbol, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastLivePriceUpdateUtc < _livePriceUpdateInterval)
        {
            return;
        }

        _lastLivePriceUpdateUtc = now;
        LivePrice = (double)update.Price;
        UpdateTemporaryPnls();
        UpdateChart();
        OnChange?.Invoke();
    }

    private void HandleChainUpdated()
    {
        UpdateTemporaryPnls();
        UpdateChart();
        OnChange?.Invoke();
    }

    private void HandleTickerUpdated(OptionChainTicker ticker)
    {
        if (ticker is null || string.IsNullOrWhiteSpace(ticker.Symbol))
        {
            return;
        }

        if (UpdateLegFromTicker(ticker))
        {
            LegTickerUpdated?.Invoke(ticker);
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
        var signedSize = DetermineSignedSize(position);

        foreach (var leg in EnumerateAllLegs())
        {
            if (!leg.IsReadOnly)
            {
                continue;
            }

            var symbol = GetLegSymbol(leg);
            if (!string.Equals(symbol, position.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Math.Abs(leg.Size - signedSize) > 0.0001)
            {
                leg.Size = signedSize;
                updated = true;
            }

            if (!leg.Price.HasValue || Math.Abs(leg.Price.Value - position.AvgPrice) > 0.0001)
            {
                leg.Price = position.AvgPrice;
                updated = true;
            }

            if (string.IsNullOrWhiteSpace(leg.Symbol))
            {
                leg.Symbol = position.Symbol;
            }
        }

        if (updated)
        {
            UpdateTemporaryPnls();
            UpdateChart();
            OnChange?.Invoke();
        }

        ActivePositionUpdated?.Invoke(position);
    }

    private LegModel ResolveLegForCalculation(LegModel leg, string? baseAsset)
    {
        var resolvedPrice = ResolveLegEntryPrice(leg, baseAsset);
        var impliedVolatility = leg.ImpliedVolatility;
        if (!impliedVolatility.HasValue || impliedVolatility.Value <= 0)
        {
            var markIv = GetLegMarkIv(leg, baseAsset);
            if (markIv.HasValue)
            {
                impliedVolatility = markIv.Value;
            }
        }
        var originalIv = leg.ImpliedVolatility ?? 0;
        var resolvedIv = impliedVolatility ?? 0;
        var priceChanged = !leg.Price.HasValue || Math.Abs(resolvedPrice - leg.Price.Value) > 0.0001;
        if (!priceChanged && Math.Abs(resolvedIv - originalIv) < 0.0001)
        {
            return leg;
        }

        return new LegModel
        {
            Id = leg.Id,
            IsIncluded = leg.IsIncluded,
            Type = leg.Type,
            Strike = leg.Strike,
            ExpirationDate = leg.ExpirationDate,
            Size = leg.Size,
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

    private IEnumerable<LegModel> ResolveLegsForCalculation(IEnumerable<LegModel> legs)
    {
        var baseAsset = SelectedPosition?.BaseAsset;

        foreach (var leg in legs)
        {
            yield return ResolveLegForCalculation(leg, baseAsset);
        }
    }

    private IEnumerable<LegModel> EnumerateAllLegs()
    {
        if (SelectedPosition is null)
        {
            return Array.Empty<LegModel>();
        }

        return SelectedPosition.Collections.SelectMany(collection => collection.Legs);
    }

    private bool UpdateLegFromTicker(OptionChainTicker ticker)
    {
        var now = DateTime.UtcNow;
        if (_lastLegPriceUpdateUtc.TryGetValue(ticker.Symbol, out var lastUpdate) &&
            now - lastUpdate < LegPriceUpdateInterval)
        {
            return false;
        }

        var updated = false;

        foreach (var leg in EnumerateAllLegs())
        {
            if (!IsLegMatch(leg, ticker))
            {
                continue;
            }

            if (!leg.ImpliedVolatility.HasValue || leg.ImpliedVolatility.Value <= 0)
            {
                var normalizedIv = ticker.MarkIv > 0 ? NormalizeIv(ticker.MarkIv) : 0;
                if (normalizedIv > 0)
                {
                    leg.ImpliedVolatility = normalizedIv;
                }
            }
            updated = true;
        }

        if (updated)
        {
            _lastLegPriceUpdateUtc[ticker.Symbol] = now;
        }

        return updated;
    }

    private static bool IsLegMatch(LegModel leg, OptionChainTicker ticker)
    {
        if (!leg.ExpirationDate.HasValue || !leg.Strike.HasValue)
        {
            return false;
        }

        return leg.Type == ticker.Type
            && leg.ExpirationDate.Value.Date == ticker.ExpirationDate.Date
            && Math.Abs(leg.Strike.Value - ticker.Strike) < 0.01;
    }

    public readonly record struct BidAsk(double? Bid, double? Ask);

    public event Action<OptionChainTicker>? LegTickerUpdated;
    public event Action<BybitPosition>? ActivePositionUpdated;
    public event Action? OnChange;
    public event Action<string>? NotificationRequested;

    public void NotifyStateChanged()
    {
        OnChange?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _exchangeTickerService.PriceUpdated -= HandlePriceUpdated;
        _optionsChainService.ChainUpdated -= HandleChainUpdated;
        _optionsChainService.TickerUpdated -= HandleTickerUpdated;
        _activePositionsService.PositionUpdated -= HandleActivePositionUpdated;
        _activePositionsService.PositionsUpdated -= HandleActivePositionsSnapshot;
        await StopTickerAsync();
        await _positionSyncService.DisposeAsync();
    }

    private async Task ApplyServerSnapshotAsync(PositionSnapshotPayload payload, DateTime occurredUtc)
    {
        _ = occurredUtc;
        _suppressSync = true;
        try
        {
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

            SelectedPosition = Positions.FirstOrDefault();
            if (SelectedPosition is not null)
            {
                SetSelectedCollection(SelectedPosition);
            }

            LivePrice = CalculateAnchorPrice(Legs);
            UpdateTemporaryPnls();
            UpdateChart();
            await PersistPositionsAsync();
            await UpdateTickerSubscriptionAsync();
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
            var wasSelected = SelectedPosition?.Id == payload.PositionId;

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
                SelectedPosition = null;
                SelectedCollection = null;
            }
            else if (wasSelected || SelectedPosition is null)
            {
                SelectedPosition = Positions.FirstOrDefault(position => position.Id == payload.PositionId) ??
                                   Positions.FirstOrDefault();
                if (SelectedPosition is not null)
                {
                    SetSelectedCollection(SelectedPosition);
                }
            }

            LivePrice = CalculateAnchorPrice(Legs);
            UpdateTemporaryPnls();
            UpdateChart();
            OnChange?.Invoke();
            try
            {
                await PersistPositionsAsync();
            }
            catch
            {
            }

            _ = UpdateTickerSubscriptionAsync();
            UpdateLegTickerSubscription();
        }
        finally
        {
            _suppressSync = false;
        }
    }

    private void Notify(string message)
    {
        NotificationRequested?.Invoke(message);
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

    private async Task<IReadOnlyList<OptionChainTicker>> LoadChainTickersAsync(string baseAsset)
    {
        var snapshot = _optionsChainService.GetSnapshot()
            .Where(ticker => string.Equals(ticker.BaseAsset, baseAsset, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (snapshot.Count > 0)
        {
            return snapshot;
        }

        await _optionsChainService.RefreshAsync(baseAsset);
        return _optionsChainService.GetSnapshot()
            .Where(ticker => string.Equals(ticker.BaseAsset, baseAsset, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private double ResolveAtmStrike(IReadOnlyList<OptionChainTicker> candidates)
    {
        var referencePrice = SelectedPrice ?? LivePrice;
        if (!referencePrice.HasValue)
        {
            referencePrice = candidates.Average(ticker => ticker.Strike);
        }

        return candidates
            .OrderBy(ticker => Math.Abs(ticker.Strike - referencePrice.Value))
            .First()
            .Strike;
    }

    private DateTime ResolveDefaultExpirationDate()
    {
        if (SelectedCollection?.Legs.Count > 0)
        {
            var lastWithExpiration = SelectedCollection.Legs.LastOrDefault(leg => leg.ExpirationDate.HasValue);
            if (lastWithExpiration?.ExpirationDate.HasValue ?? false)
            {
                return lastWithExpiration.ExpirationDate.Value.Date;
            }
        }

        return GetNextMonthEndDate(DateTime.UtcNow.Date, 7);
    }

    private static DateTime GetNextMonthEndDate(DateTime reference, int minDaysOut)
    {
        var target = reference;
        while (true)
        {
            var candidate = new DateTime(target.Year, target.Month, DateTime.DaysInMonth(target.Year, target.Month));
            if ((candidate - reference.Date).TotalDays > minDaysOut)
            {
                return candidate;
            }

            target = target.AddMonths(1);
        }
    }

    private static bool TryParseNumber(string token, out double value)
    {
        var cleaned = token.Trim().Trim(',', ';');
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryFindLegType(string[] tokens, int startIndex, out LegType type, out int typeIndex)
    {
        for (var i = startIndex; i < tokens.Length; i++)
        {
            var normalized = tokens[i].Trim().Trim(',', ';').ToUpperInvariant();
            switch (normalized)
            {
                case "C":
                case "CALL":
                case "CALLS":
                    type = LegType.Call;
                    typeIndex = i;
                    return true;
                case "P":
                case "PUT":
                case "PUTS":
                    type = LegType.Put;
                    typeIndex = i;
                    return true;
            }
        }

        type = LegType.Call;
        typeIndex = -1;
        return false;
    }

    private static double? TryParseStrike(string[] tokens, int typeIndex)
    {
        for (var i = typeIndex + 1; i < tokens.Length; i++)
        {
            var token = tokens[i].Trim().Trim(',', ';');
            if (string.Equals(token, "@", StringComparison.Ordinal) || token.StartsWith("@", StringComparison.Ordinal))
            {
                break;
            }

            if (TryParseNumber(token, out var strike))
            {
                return strike;
            }
        }

        return null;
    }

    private static (double? price, double? iv) ParsePriceOverrides(string input)
    {
        var match = QuickAddAtRegex.Match(input);
        if (!match.Success)
        {
            return (null, null);
        }

        if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return (null, null);
        }

        return match.Groups["percent"].Value == "%"
            ? (null, value)
            : (value, null);
    }

    private static bool TryParseExpirationDate(string input, out DateTime expirationDate)
    {
        var match = QuickAddDateWithYearRegex.Match(input);
        if (match.Success)
        {
            if (DateTime.TryParseExact(match.Value.ToUpperInvariant(), new[] { "ddMMMyy", "ddMMMyyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                expirationDate = parsed.Date;
                return true;
            }
        }

        match = QuickAddDateWithoutYearRegex.Match(input);
        if (match.Success && DateTime.TryParseExact(match.Value.ToUpperInvariant(), "ddMMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedWithoutYear))
        {
            var candidate = BuildDateFromMonthDay(parsedWithoutYear.Month, parsedWithoutYear.Day);
            expirationDate = candidate;
            return true;
        }

        match = QuickAddNumericDateRegex.Match(input);
        if (match.Success && match.Value.Length == 4)
        {
            var day = int.Parse(match.Value[..2], CultureInfo.InvariantCulture);
            var month = int.Parse(match.Value.Substring(2, 2), CultureInfo.InvariantCulture);
            if (month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(DateTime.UtcNow.Year, month))
            {
                expirationDate = BuildDateFromMonthDay(month, day);
                return true;
            }
        }

        expirationDate = default;
        return false;
    }

    private static double RoundPrice(double value)
    {
        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    private static DateTime BuildDateFromMonthDay(int month, int day)
    {
        var today = DateTime.UtcNow.Date;
        var daysInMonth = DateTime.DaysInMonth(today.Year, month);
        var safeDay = Math.Min(day, daysInMonth);
        var candidate = new DateTime(today.Year, month, safeDay);
        if ((candidate - today).TotalDays <= 7)
        {
            candidate = candidate.AddYears(1);
        }

        return candidate;
    }

}








