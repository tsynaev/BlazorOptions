using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class PositionBuilderViewModel : IAsyncDisposable
{
    private readonly OptionsService _optionsService;
    private readonly PositionStorageService _storageService;
    private readonly ExchangeSettingsService _exchangeSettingsService;
    private readonly ExchangeTickerService _exchangeTickerService;
    private readonly OptionsChainService _optionsChainService;
    private CancellationTokenSource? _tickerCts;
    private string? _currentSymbol;
    private TimeSpan _livePriceUpdateInterval = TimeSpan.FromMilliseconds(1000);
    private DateTime _lastLivePriceUpdateUtc = DateTime.MinValue;
    private static readonly Regex QuickAddAtRegex = new(@"@\s*(?<value>\d+(?:\.\d+)?)\s*(?<percent>%?)", RegexOptions.Compiled);
    private static readonly Regex QuickAddDateWithYearRegex = new(@"(?i)\b\d{1,2}[A-Z]{3}\d{2,4}\b", RegexOptions.Compiled);
    private static readonly Regex QuickAddDateWithoutYearRegex = new(@"(?i)\b\d{1,2}[A-Z]{3}\b", RegexOptions.Compiled);
    private static readonly Regex QuickAddNumericDateRegex = new(@"\b\d{4}\b", RegexOptions.Compiled);

    private static readonly ObservableCollection<OptionLegModel> EmptyLegs = new();
    private static readonly ObservableCollection<LegsCollectionModel> EmptyCollections = new();
    private static readonly string[] CollectionPalette =
    {
        "#1976D2",
        "#9C27B0",
        "#009688",
        "#FF9800",
        "#E91E63",
        "#3F51B5",
        "#4CAF50",
        "#795548",
        "#607D8B"
    };

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
        OptionsChainService optionsChainService)
    {
        _optionsService = optionsService;
        _storageService = storageService;
        _exchangeSettingsService = exchangeSettingsService;
        _exchangeTickerService = exchangeTickerService;
        _optionsChainService = optionsChainService;
        _exchangeTickerService.PriceUpdated += HandlePriceUpdated;
        _optionsChainService.ChainUpdated += HandleChainUpdated;
    }

    public ObservableCollection<PositionModel> Positions { get; } = new();

    public PositionModel? SelectedPosition { get; private set; }

    public ObservableCollection<LegsCollectionModel> Collections => SelectedPosition?.Collections ?? EmptyCollections;

    public LegsCollectionModel? SelectedCollection { get; private set; }

    public ObservableCollection<OptionLegModel> Legs => SelectedCollection?.Legs ?? EmptyLegs;

    public EChartOptions ChartConfig { get; private set; } = new(Guid.Empty, Array.Empty<double>(), Array.Empty<string>(), null, Array.Empty<ChartCollectionSeries>(), null, 0, 0);

    public async Task InitializeAsync()
    {
        var storedPositions = await _storageService.LoadPositionsAsync();

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
    }

    public async Task AddLegAsync()
    {
        if (SelectedCollection is null)
        {
            return;
        }

        SelectedCollection.Legs.Add(new OptionLegModel
        {
            Type = OptionLegType.Call,
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

        var leg = new OptionLegModel
        {
            Type = type,
            Strike = tickerMatch.Strike,
            ExpirationDate = expirationDate.Date,
            Size = size,
            Price = RoundPrice(priceOverride ?? tickerMatch.MarkPrice),
            ImpliedVolatility = ivOverride ?? 0,
            ChainSymbol = tickerMatch.Symbol
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

    public async Task UpdateLegsAsync(IEnumerable<OptionLegModel> legs)
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

    public async Task<bool> RemoveLegAsync(OptionLegModel leg)
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

    public async Task AddPositionAsync(string? pair = null)
    {
        var position = CreateDefaultPosition(pair ?? $"Position {Positions.Count + 1}");
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

    public async Task UpdatePairAsync(PositionModel position, string pair)
    {
        position.Pair = pair;
        UpdateAssetsFromPair(position);
        await PersistPositionsAsync();
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

    public async Task PersistPositionsAsync()
    {
        await _storageService.SavePositionsAsync(Positions);
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
        var activeCollectionId = SelectedCollection?.Id;

        ChartConfig = new EChartOptions(positionId, xs, labels, displayPrice, chartCollections, activeCollectionId, minProfit - padding, maxProfit + padding);
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

        foreach (var leg in EnumerateAllLegs())
        {
            if (!leg.IsIncluded)
            {
                leg.TemporaryPnl = 0;
                continue;
            }

            if (IsLive && leg.Type != OptionLegType.Future)
            {
                var ticker = _optionsChainService.FindTickerForLeg(leg, baseAsset);
                if (ticker is not null)
                {
                    leg.TemporaryPnl = (ticker.MarkPrice - leg.Price) * leg.Size;
                    continue;
                }
            }

            var calculationLeg = ResolveLegForCalculation(leg, baseAsset);
            leg.TemporaryPnl = _optionsService.CalculateLegTheoreticalProfit(calculationLeg, price, SelectedValuationDate);
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
        Positions.RemoveAt(positionIndex);

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
        await UpdateTickerSubscriptionAsync();
        return true;
    }

    private PositionModel CreateDefaultPosition(string? pair = null)
    {
        var position = new PositionModel
        {
            Pair = pair ?? "ETH/USDT"
        };
        UpdateAssetsFromPair(position);

        var collection = CreateCollection(position, GetNextCollectionName(position));
        collection.Legs.Add(new OptionLegModel
        {
            Type = OptionLegType.Call,
            Strike = 3400,
            Price = 180,
            Size = 1,
            ImpliedVolatility = 75,
            ExpirationDate = DateTime.UtcNow.Date.AddMonths(2)
        });

        collection.Legs.Add(new OptionLegModel
        {
            Type = OptionLegType.Put,
            Strike = 3200,
            Price = 120,
            Size = 1,
            ImpliedVolatility = 70,
            ExpirationDate = DateTime.UtcNow.Date.AddMonths(2)
        });

        position.Collections.Add(collection);
        position.ActiveCollectionId = collection.Id;

        return position;
    }

    private static void UpdateAssetsFromPair(PositionModel position)
    {
        if (string.IsNullOrWhiteSpace(position.Pair))
        {
            return;
        }

        var parts = position.Pair.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            position.BaseAsset = parts[0];
            position.QuoteAsset = parts[1];
        }
    }

    private void NormalizeCollections(PositionModel position)
    {
        if (position.Collections.Count == 0)
        {
            if (position.Legs.Count > 0)
            {
                var migrated = CreateCollection(position, GetNextCollectionName(position), position.Legs.Select(CloneLeg));
                position.Collections.Add(migrated);
                position.Legs.Clear();
            }
            else
            {
                position.Collections.Add(CreateCollection(position, GetNextCollectionName(position)));
            }
        }

        if (!position.ActiveCollectionId.HasValue)
        {
            position.ActiveCollectionId = position.Collections.FirstOrDefault()?.Id;
        }
    }

    private void SetSelectedCollection(PositionModel position, LegsCollectionModel? collection = null)
    {
        var resolved = collection;
        if (resolved is null)
        {
            resolved = position.ActiveCollectionId.HasValue
                ? position.Collections.FirstOrDefault(item => item.Id == position.ActiveCollectionId.Value)
                : position.Collections.FirstOrDefault();
        }

        SelectedCollection = resolved ?? position.Collections.FirstOrDefault();
        position.ActiveCollectionId = SelectedCollection?.Id;
    }

    private LegsCollectionModel CreateCollection(PositionModel position, string name, IEnumerable<OptionLegModel>? legs = null)
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

    private static OptionLegModel CloneLeg(OptionLegModel leg)
    {
        return new OptionLegModel
        {
            Id = Guid.NewGuid(),
            IsIncluded = leg.IsIncluded,
            Type = leg.Type,
            Strike = leg.Strike,
            ExpirationDate = leg.ExpirationDate,
            Size = leg.Size,
            Price = leg.Price,
            ImpliedVolatility = leg.ImpliedVolatility,
            ChainSymbol = leg.ChainSymbol
        };
    }

    private static double CalculateAnchorPrice(IEnumerable<OptionLegModel> legs)
    {
        var activeLegs = legs.Where(l => l.IsIncluded).ToList();

        if (activeLegs.Count == 0)
        {
            return 1000;
        }

        return activeLegs.Average(l => l.Strike > 0 ? l.Strike : l.Price);
    }

    private void RefreshValuationDateBounds(IEnumerable<OptionLegModel> legs)
    {
        var today = DateTime.UtcNow.Date;
        MaxExpiryDate = legs.Any()
            ? legs.Max(l => l.ExpirationDate.Date)
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
        if (SelectedPosition is null)
        {
            return;
        }

        _optionsChainService.TrackLegs(EnumerateAllLegs(), SelectedPosition.BaseAsset);

        if (IsLive)
        {
            _ = _optionsChainService.RefreshAsync(SelectedPosition?.BaseAsset);
        }
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

        var pair = position.Pair?.Trim();
        if (string.IsNullOrWhiteSpace(pair))
        {
            return string.Empty;
        }

        return pair.Replace("/", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToUpperInvariant();
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
        if (!IsLive)
        {
            return;
        }

        UpdateTemporaryPnls();
        UpdateChart();
        OnChange?.Invoke();
    }

    private OptionLegModel ResolveLegForCalculation(OptionLegModel leg, string? baseAsset)
    {
        var impliedVolatility = leg.ImpliedVolatility;
        if (impliedVolatility <= 0)
        {
            var ticker = _optionsChainService.FindTickerForLeg(leg, baseAsset);
            if (ticker is not null && ticker.MarkIv > 0)
            {
                impliedVolatility = ticker.MarkIv;
            }
        }

        if (Math.Abs(impliedVolatility - leg.ImpliedVolatility) < 0.0001)
        {
            return leg;
        }

        return new OptionLegModel
        {
            Id = leg.Id,
            IsIncluded = leg.IsIncluded,
            Type = leg.Type,
            Strike = leg.Strike,
            ExpirationDate = leg.ExpirationDate,
            Size = leg.Size,
            Price = leg.Price,
            ImpliedVolatility = impliedVolatility,
            ChainSymbol = leg.ChainSymbol
        };
    }

    private IEnumerable<OptionLegModel> ResolveLegsForCalculation(IEnumerable<OptionLegModel> legs)
    {
        var baseAsset = SelectedPosition?.BaseAsset;

        foreach (var leg in legs)
        {
            yield return ResolveLegForCalculation(leg, baseAsset);
        }
    }

    private IEnumerable<OptionLegModel> EnumerateAllLegs()
    {
        if (SelectedPosition is null)
        {
            return Array.Empty<OptionLegModel>();
        }

        return SelectedPosition.Collections.SelectMany(collection => collection.Legs);
    }

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
        await StopTickerAsync();
    }

    private void Notify(string message)
    {
        NotificationRequested?.Invoke(message);
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
            return SelectedCollection.Legs.Last().ExpirationDate.Date;
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

    private static bool TryFindLegType(string[] tokens, int startIndex, out OptionLegType type, out int typeIndex)
    {
        for (var i = startIndex; i < tokens.Length; i++)
        {
            var normalized = tokens[i].Trim().Trim(',', ';').ToUpperInvariant();
            switch (normalized)
            {
                case "C":
                case "CALL":
                case "CALLS":
                    type = OptionLegType.Call;
                    typeIndex = i;
                    return true;
                case "P":
                case "PUT":
                case "PUTS":
                    type = OptionLegType.Put;
                    typeIndex = i;
                    return true;
            }
        }

        type = OptionLegType.Call;
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
