using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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


    private static readonly string[] PositionExpirationFormats = { "ddMMMyy", "ddMMMyyyy" };

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
        _activePositionsService.PositionUpdated += HandleActivePositionUpdated;
        _activePositionsService.PositionsUpdated += HandleActivePositionsSnapshot;
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

        await SelectedPosition.UpdateCollectionVisibilityAsync(collection.Collection, isVisible);
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
        var position = SelectedPosition?.Position;
        var collections = position?.Collections ?? Enumerable.Empty<LegsCollectionModel>();
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

    private double GetClosedPositionsTotal(PositionModel? position)
    {
        if (position is null || !position.IncludeClosedPositions || position.ClosedPositions is null)
        {
            return 0;
        }

        return position.ClosedPositionsNetTotal;
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

    public double? GetLegLastPrice(LegModel leg, string? baseAsset = null)
    {
        var resolvedBaseAsset = baseAsset ?? SelectedPosition?.Position?.BaseAsset;
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
        var resolvedBaseAsset = baseAsset ?? SelectedPosition?.Position?.BaseAsset;
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
        var resolvedBaseAsset = baseAsset ?? SelectedPosition?.Position?.BaseAsset;
        var ticker = _optionsChainService.FindTickerForLeg(leg, resolvedBaseAsset);
        if (ticker is null)
        {
            return null;
        }

        return ticker.MarkPrice > 0 ? ticker.MarkPrice : null;
    }

    public double? GetLegMarketPrice(LegModel leg, string? baseAsset = null)
    {
        var resolvedBaseAsset = baseAsset ?? SelectedPosition?.Position?.BaseAsset;
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


    public string? GetLegSymbol(LegModel leg, string? baseAsset = null)
    {
        if (!string.IsNullOrWhiteSpace(leg.Symbol))
        {
            return leg.Symbol;
        }

        var resolvedBaseAsset = baseAsset ?? SelectedPosition?.Position?.BaseAsset;
        var ticker = _optionsChainService.FindTickerForLeg(leg, resolvedBaseAsset);
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
                position);
    }

    private Task InitializeSelectedPositionAsync()
    {
        return SelectedPosition?.InitializeAsync() ?? Task.CompletedTask;
    }

    private double ResolveLegEntryPrice(LegModel leg, string? baseAsset)
    {
        if (leg.Price.HasValue)
        {
            return leg.Price.Value;
        }

        return GetLegMarketPrice(leg, baseAsset) ?? 0;
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

    internal LegModel? CreateLegFromBybitPosition(BybitPosition position, string baseAsset, string category)
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

 
    public event Action<BybitPosition>? ActivePositionUpdated;
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
        _activePositionsService.PositionUpdated -= HandleActivePositionUpdated;
        _activePositionsService.PositionsUpdated -= HandleActivePositionsSnapshot;
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











