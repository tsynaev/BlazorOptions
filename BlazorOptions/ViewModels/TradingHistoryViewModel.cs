using System.Globalization;
using System.Linq;
using System.Text.Json;
using BlazorOptions.Services;
using Microsoft.Extensions.Options;

namespace BlazorOptions.ViewModels;

public class TradingHistoryViewModel
{
    private const int PageLimit = 100;
    private const int PageSize = 100;
    private static readonly TimeSpan WindowSize = TimeSpan.FromDays(7);
    private const string NoMoreCursor = "__END__";
    private readonly BybitTransactionService _transactionService;
    private readonly TradingHistoryStorageService _storageService;
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;
    private TradingHistoryMeta _meta = new();
    private IReadOnlyList<TradingSummaryRow>? _summaryBySymbolCache;
    private IReadOnlyList<TradingPnlByCoinRow>? _pnlByCoinCache;
    private DailyPnlChartOptions? _dailyPnlChartCache;
    private readonly List<TradingHistoryEntry> _loadedEntries = new();
    private bool _isInitialized;
    private bool _isBackgroundInitialized;
    private bool _isLoadingRange;
    private bool _isLoadingSummary;
    private long? _loadedBeforeTimestamp;
    private string? _loadedBeforeKey;
    private int _totalCount;

    public TradingHistoryViewModel(
        BybitTransactionService transactionService,
        TradingHistoryStorageService storageService,
        IOptions<BybitSettings> bybitSettingsOptions)
    {
        _transactionService = transactionService;
        _storageService = storageService;
        _bybitSettingsOptions = bybitSettingsOptions;
    }

    public event Action? OnChange;

    public int TotalTransactionsCount => _totalCount;

    public IReadOnlyList<string> Categories { get; } = new[] { "linear", "inverse", "spot", "option" };

    public DateTime? RegistrationDate { get; private set; }

    public bool IsRegistrationDateRequired => !_meta.RegistrationTimeMs.HasValue;

    public bool IsLoading { get; private set; }

    public bool IsLoadingOlder { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset? LastLoadedAt { get; private set; }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        await _storageService.InitializeAsync();
        _meta = await _storageService.LoadMetaAsync();
        _totalCount = await _storageService.GetCountAsync();
        UpdateRegistrationDate();
        await LoadLatestPageFromDbAsync();
        InvalidateSummaryCache();
        _ = EnsureSummaryCacheAsync();
        OnChange?.Invoke();
    }

    public async Task InitializeForBackgroundAsync()
    {
        if (_isInitialized || _isBackgroundInitialized)
        {
            return;
        }

        _isBackgroundInitialized = true;
        await _storageService.InitializeAsync();
        _meta = await _storageService.LoadMetaAsync();
        _totalCount = await _storageService.GetCountAsync();
        UpdateRegistrationDate();

        _ = StartBackgroundLoadIfPossibleAsync();
    }

    public async Task LoadLatestAsync()
    {
        if (IsLoading || IsLoadingOlder)
        {
            return;
        }

        ErrorMessage = null;
        IsLoading = true;
        OnChange?.Invoke();

        try
        {
            var settings = _bybitSettingsOptions.Value;

            if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.ApiSecret))
            {
                ErrorMessage = "Bybit API credentials are missing. Add them in the Bybit settings page.";
                return;
            }

            var registrationTime = _meta.RegistrationTimeMs;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
         

            foreach (var category in Categories)
            {

                var latest = new List<TradingTransactionRaw>();
                long startTime = 0;

                if (!_meta.LatestSyncedTimeMsByCategory.TryGetValue(category, out startTime))
                {
                    if (registrationTime.HasValue)
                    {
                        startTime = registrationTime.Value;
                    }
                    else
                    {
                        ErrorMessage = "Select your registration date before loading transactions.";
                        return;
                    }
                }


                var lastStartTime = startTime;
                var forwardCursor = startTime;
                var categoryHadTrades = false;

                while (forwardCursor < nowMs)
                {
                    var endTime = Math.Min(forwardCursor + (long)WindowSize.TotalMilliseconds, nowMs);
                    var cursor = (string?)null;

                    while (true)
                    {
                        var query = new BybitTransactionQuery
                        {
                            AccountType = "UNIFIED",
                            Category = category,
                            Limit = PageLimit,
                            Cursor = cursor,
                            StartTime = forwardCursor,
                            EndTime = endTime
                        };

                        var page = await _transactionService.GetTransactionsPageAsync(settings, query, CancellationToken.None);
                        if (page.Items.Count > 0)
                        {
                            categoryHadTrades = true;

                            foreach (var item in page.Items)
                            {
                                if (item.Timestamp.HasValue && item.Timestamp.Value > lastStartTime)
                                {
                                    lastStartTime = item.Timestamp.Value + 1;
                                }
                            }

                            latest.AddRange(page.Items);
                        }

                        if (string.IsNullOrWhiteSpace(page.NextCursor))
                        {
                            break;
                        }

                        cursor = page.NextCursor;
                    }

                    if (!categoryHadTrades) lastStartTime = forwardCursor;

                    forwardCursor = endTime + 1;
                    _meta.LatestSyncedTimeMsByCategory[category] = lastStartTime;
                }

                var entries = latest.Select(MapRecordToEntry).ToList();
                var orderedAsc = entries
                    .OrderBy(entry => entry.Timestamp)
                    .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                    .ToList();

                var calculationState = new CalculationState(_meta);
                ApplyCalculatedFields(orderedAsc, calculationState);
                calculationState.ApplyToMeta(_meta);

                if (orderedAsc.Count > 0)
                {
                    var maxTime = orderedAsc.Max(entry => entry.Timestamp);

                    if (maxTime > _meta.CalculatedThroughTimestamp) _meta.CalculatedThroughTimestamp = maxTime;
                }

                await _storageService.SaveTradesAsync(entries);
                await _storageService.SaveMetaAsync(_meta);

            }

    
            _totalCount = await _storageService.GetCountAsync();
            await LoadLatestPageFromDbAsync();

            InvalidateSummaryCache();
            _ = EnsureSummaryCacheAsync();
            LastLoadedAt = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
            OnChange?.Invoke();
        }
    }

    public async Task SetRegistrationDateAsync(DateTime? date)
    {
        RegistrationDate = date?.Date;
        _meta.RegistrationTimeMs = date.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(date.Value.Date, DateTimeKind.Local)).ToUnixTimeMilliseconds()
            : null;

        await _storageService.SaveMetaAsync(_meta);
        OnChange?.Invoke();
    }


    public Task RecalculateAsync()
    {
        return RecalculateAsync(null);
    }

    public async Task RecalculateAsync(DateTime? fromDate)
    {
        if (_isLoadingSummary)
        {
            return;
        }

        _isLoadingSummary = true;
        try
        {
            var entries = await _storageService.LoadAllAscAsync();
            if (entries.Count == 0)
            {
                _meta.SizeBySymbol.Clear();
                _meta.AvgPriceBySymbol.Clear();
                _meta.CumulativeBySettleCoin.Clear();
                _meta.CalculatedThroughTimestamp = null;
                _meta.RequiresRecalculation = false;
                await _storageService.SaveMetaAsync(_meta);
                return;
            }

            var state = new CalculationState();
            if (fromDate.HasValue)
            {
                var startTimestamp = GetStartTimestamp(fromDate.Value);
                var before = new List<TradingHistoryEntry>(entries.Count);
                var after = new List<TradingHistoryEntry>(entries.Count);

                foreach (var entry in entries)
                {
                    if (entry.Timestamp.HasValue && entry.Timestamp.Value > 0 && entry.Timestamp.Value < startTimestamp)
                    {
                        before.Add(entry);
                    }
                    else
                    {
                        after.Add(entry);
                    }
                }

                ApplyCalculatedFields(before, state, updateEntries: false);
                ApplyCalculatedFields(after, state);
                await _storageService.SaveTradesAsync(after);
            }
            else
            {
                ApplyCalculatedFields(entries, state);
                await _storageService.SaveTradesAsync(entries);
            }

            state.ApplyToMeta(_meta);
            _meta.CalculatedThroughTimestamp = entries.Max(entry => entry.Timestamp);
            _meta.RequiresRecalculation = false;
            await _storageService.SaveMetaAsync(_meta);
            InvalidateSummaryCache();
            _ = EnsureSummaryCacheAsync();
            await LoadLatestPageFromDbAsync();
            OnChange?.Invoke();
        }
        finally
        {
            _isLoadingSummary = false;
        }
    }

    public async Task EnsureRangeAsync(int startIndex, int count)
    {
        if (_loadedEntries.Count == 0)
        {
            await LoadLatestPageFromDbAsync();
        }

        var needed = startIndex + count;
        if (needed <= _loadedEntries.Count)
        {
            return;
        }

        if (_isLoadingRange)
        {
            return;
        }

        _isLoadingRange = true;
        try
        {
            while (_loadedEntries.Count < needed)
            {
                var batch = await _storageService.LoadBeforeAsync(_loadedBeforeTimestamp, _loadedBeforeKey, PageSize);
                if (batch.Count == 0)
                {
                    break;
                }

                _loadedEntries.AddRange(batch);
                UpdatePagingCursor();
            }
        }
        finally
        {
            _isLoadingRange = false;
        }
    }

    public IReadOnlyList<TradingHistoryEntry> GetRange(int startIndex, int count)
    {
        var skip = Math.Max(0, startIndex);
        var take = Math.Max(0, count);
        var results = new List<TradingHistoryEntry>(take);

        for (var i = skip; i < Math.Min(_loadedEntries.Count, skip + take); i++)
        {
            var entry = _loadedEntries[i];
            results.Add(entry);
        }

        return results;
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> GetTradesForSymbolAsync(string symbol, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return Array.Empty<TradingHistoryEntry>();
        }

        var entries = await _storageService.LoadBySymbolAsync(symbol, category);
        return RecalculateForSymbol(entries);
    }

    public static IReadOnlyList<TradingHistoryEntry> RecalculateForSymbol(IReadOnlyList<TradingHistoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return Array.Empty<TradingHistoryEntry>();
        }

        var orderedAsc = entries
            .OrderBy(entry => entry.Timestamp ?? 0)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToList();

        var state = new CalculationState();
        ApplyCalculatedFields(orderedAsc, state);

        return orderedAsc
            .OrderByDescending(item => item.Timestamp ?? 0)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<string> GetRawJsonForSymbolAsync(string symbol, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return string.Empty;
        }

        var entries = await _storageService.LoadBySymbolAsync(symbol, category);
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var payload = new List<JsonElement>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.RawJson))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(entry.RawJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    payload.Add(item.Clone());
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                payload.Add(root.Clone());
            }
        }

        if (payload.Count == 0)
        {
            return string.Empty;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(payload, options);
    }

    public IReadOnlyList<TradingSummaryRow> GetSummaryBySymbol()
    {
        if (_summaryBySymbolCache is not null)
        {
            return _summaryBySymbolCache;
        }

        _ = EnsureSummaryCacheAsync();
        return Array.Empty<TradingSummaryRow>();
    }

    public IReadOnlyList<TradingPnlByCoinRow> GetRealizedPnlBySettleCoin()
    {
        if (_pnlByCoinCache is not null)
        {
            return _pnlByCoinCache;
        }

        _ = EnsureSummaryCacheAsync();
        return Array.Empty<TradingPnlByCoinRow>();
    }

    public DailyPnlChartOptions? GetDailyRealizedPnlChart()
    {
        if (_dailyPnlChartCache is not null)
        {
            return _dailyPnlChartCache;
        }

        _ = EnsureSummaryCacheAsync();
        return null;
    }

    private async Task EnsureSummaryCacheAsync()
    {
        if (_summaryBySymbolCache is not null || _pnlByCoinCache is not null || _isLoadingSummary)
        {
            return;
        }

        _isLoadingSummary = true;
        try
        {
            var entries = await _storageService.LoadAllAscAsync();
            BuildSummaryCaches(entries);
            var dailySummaries = BuildDailySummaries(entries);
            await _storageService.SaveDailySummariesAsync(dailySummaries);
            _dailyPnlChartCache = BuildDailyPnlChart(entries);
            OnChange?.Invoke();
        }
        finally
        {
            _isLoadingSummary = false;
        }
    }

    private async Task LoadLatestPageFromDbAsync()
    {
        _loadedEntries.Clear();
        var entries = await _storageService.LoadLatestAsync(PageSize);
        _loadedEntries.AddRange(entries);
        if (_totalCount == 0 && _loadedEntries.Count > 0)
        {
            _totalCount = _loadedEntries.Count;
        }
        if (_loadedEntries.Count == 0)
        {
            var fallback = await _storageService.LoadAnyAsync(PageSize);
            _loadedEntries.AddRange(fallback);
            if (_totalCount == 0 && _loadedEntries.Count > 0)
            {
                _totalCount = _loadedEntries.Count;
            }
        }
        UpdatePagingCursor();
    }

    private void UpdatePagingCursor()
    {
        var last = _loadedEntries.LastOrDefault();
        if (last is null)
        {
            _loadedBeforeTimestamp = null;
            _loadedBeforeKey = null;
            return;
        }

        _loadedBeforeTimestamp = last.Timestamp;
        _loadedBeforeKey = last.Id;
    }

    private void UpdateRegistrationDate()
    {
        if (_meta.RegistrationTimeMs.HasValue)
        {
            RegistrationDate = DateTimeOffset.FromUnixTimeMilliseconds(_meta.RegistrationTimeMs.Value)
                .ToLocalTime()
                .Date;
        }
    }


    private async Task StartBackgroundLoadIfPossibleAsync()
    {
        if (_totalCount <= 0 || IsLoading || IsLoadingOlder)
        {
            return;
        }

        try
        {
            var settings = _bybitSettingsOptions.Value;
            if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.ApiSecret))
            {
                return;
            }

            await LoadLatestAsync();
        }
        catch
        {
        }
    }

    private TradingHistoryEntry MapRecordToEntry(TradingTransactionRaw raw)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var size = raw.Qty ?? raw.Size ?? 0m;
        var price = raw.TradePrice ?? 0m;
        var fee = raw.Fee ?? 0m;
        var change = raw.Change ?? 0m;
        var cashFlow = raw.CashFlow ?? 0m;

        return new TradingHistoryEntry
        {
            Id = raw.UniqueKey,
            Timestamp = raw.Timestamp ?? 0,
            Symbol = raw.Symbol,
            Category = raw.Category,
            TransactionType = raw.TransactionType,
            Side = raw.Side,
            Size = size,
            Price = price,
            Fee = fee,
            Currency = raw.Currency,
            Change = change,
            CashFlow = cashFlow,
            OrderId = raw.OrderId,
            OrderLinkId = raw.OrderLinkId,
            TradeId = raw.TradeId,
            RawJson = raw.RawJson,
            ChangedAt = now,
            Calculated = new TradingTransactionCalculated()
        };
    }


    private void BuildSummaryCaches(IReadOnlyList<TradingHistoryEntry> entries)
    {
        var summary = new Dictionary<string, (string Category, string Symbol, string Coin, int Trades, decimal Qty, decimal Value, decimal Fees, decimal Realized)>(StringComparer.OrdinalIgnoreCase);
        var totals = new Dictionary<string, (decimal Realized, decimal Fees)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var symbol = string.IsNullOrWhiteSpace(entry.Symbol) ? "_unknown" : entry.Symbol;
            var category = string.IsNullOrWhiteSpace(entry.Category) ? "_unknown" : entry.Category;
            var settleCoin = GetSettleCoin(entry.Currency);
            var key = $"{category}|{symbol}|{settleCoin}";

            if (!summary.TryGetValue(key, out var value))
            {
                value = (category, symbol, settleCoin, 0, 0m, 0m, 0m, 0m);
            }

            value.Trades += 1;
            value.Qty += entry.Size;
            value.Value += entry.Price * entry.Size;
            value.Fees += entry.Fee;
            value.Realized += ParseDecimal(entry.Calculated?.RealizedPnl);
            summary[key] = value;

            if (!totals.TryGetValue(settleCoin, out var pnlTotals))
            {
                pnlTotals = (0m, 0m);
            }

            pnlTotals.Realized += ParseDecimal(entry.Calculated?.RealizedPnl);
            pnlTotals.Fees += entry.Fee;
            totals[settleCoin] = pnlTotals;
        }

        _summaryBySymbolCache = summary
            .Select(entry => new TradingSummaryRow
            {
                Category = entry.Value.Category,
                GroupKey = $"{entry.Value.Category}/{entry.Value.Coin}".TrimEnd('/'),
                Symbol = entry.Value.Symbol,
                SettleCoin = entry.Value.Coin,
                Trades = entry.Value.Trades,
                TotalQty = FormatDecimal(entry.Value.Qty),
                TotalValue = $"{FormatDecimal(entry.Value.Value)} {entry.Value.Coin}".Trim(),
                TotalFees = $"{FormatDecimal(entry.Value.Fees)} {entry.Value.Coin}".Trim(),
                RealizedPnl = $"{FormatDecimal(entry.Value.Realized)} {entry.Value.Coin}".Trim()
            })
            .OrderByDescending(row => row.Trades)
            .ThenBy(row => row.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _pnlByCoinCache = totals
            .Select(entry => new TradingPnlByCoinRow
            {
                SettleCoin = entry.Key,
                RealizedPnl = $"{FormatDecimal(entry.Value.Realized)} {entry.Key}".Trim(),
                Fees = $"{FormatDecimal(entry.Value.Fees)} {entry.Key}".Trim(),
                NetPnl = $"{FormatDecimal(entry.Value.Realized - entry.Value.Fees)} {entry.Key}".Trim()
            })
            .OrderBy(row => row.SettleCoin, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<TradingDailySummary> BuildDailySummaries(IReadOnlyList<TradingHistoryEntry> entries)
    {
        var byDay = new Dictionary<string, TradingDailySummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var day = GetDayKey(entry.Timestamp);
            if (string.IsNullOrWhiteSpace(day))
            {
                continue;
            }

            var symbolKey = NormalizeSymbolKey(entry.Symbol);
            var category = entry.Category ?? string.Empty;
            var key = $"{symbolKey}|{day}";

            if (!byDay.TryGetValue(key, out var summary))
            {
                summary = new TradingDailySummary
                {
                    Key = key,
                    Day = day,
                    SymbolKey = symbolKey,
                    Symbol = entry.Symbol ?? string.Empty,
                    Category = category,
                    TotalSize = 0m,
                    TotalValue = 0m,
                    TotalFee = 0m
                };
            }

            var qty = entry.Size;
            var price = entry.Price;
            var fee = entry.Fee;

            summary.TotalSize += qty;
            summary.TotalValue += qty * price;
            summary.TotalFee += fee;
            byDay[key] = summary;
        }

        return byDay.Values
            .OrderBy(item => item.Day, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SymbolKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DailyPnlChartOptions? BuildDailyPnlChart(IReadOnlyList<TradingHistoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        var utcNow = DateTimeOffset.UtcNow;
        var startDate = new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day, 0, 0, 0, TimeSpan.Zero).AddDays(-29);
        var startTimestamp = startDate.ToUnixTimeMilliseconds();
        var dayKeys = Enumerable.Range(0, 30)
            .Select(offset => startDate.AddDays(offset))
            .Select(day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToArray();
        var byDay = dayKeys.ToDictionary(
            key => key,
            _ => new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var coins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (!entry.Timestamp.HasValue || entry.Timestamp.Value < startTimestamp)
            {
                continue;
            }

            var day = GetDayKey(entry.Timestamp);
            if (string.IsNullOrWhiteSpace(day) || !byDay.TryGetValue(day, out var totals))
            {
                continue;
            }

            var settleCoin = GetSettleCoin(entry.Currency);
            var realized = ParseDecimal(entry.Calculated?.RealizedPnl);

            totals.TryGetValue(settleCoin, out var current);
            totals[settleCoin] = current + realized;
            coins.Add(settleCoin);
        }

        if (coins.Count == 0)
        {
            return null;
        }

        var orderedCoins = coins.OrderBy(coin => coin, StringComparer.OrdinalIgnoreCase).ToArray();
        var series = new List<DailyPnlSeries>(orderedCoins.Length);
        var hasValue = false;
        var min = 0m;
        var max = 0m;

        for (var coinIndex = 0; coinIndex < orderedCoins.Length; coinIndex++)
        {
            var coin = orderedCoins[coinIndex];
            var values = new decimal[dayKeys.Length];

            for (var i = 0; i < dayKeys.Length; i++)
            {
                var amount = 0m;
                if (byDay.TryGetValue(dayKeys[i], out var totals) &&
                    totals.TryGetValue(coin, out var found))
                {
                    amount = found;
                }

                values[i] = amount;
                if (!hasValue)
                {
                    min = amount;
                    max = amount;
                    hasValue = true;
                }
                else
                {
                    min = Math.Min(min, amount);
                    max = Math.Max(max, amount);
                }
            }

            series.Add(new DailyPnlSeries(coin, values));
        }

        if (!hasValue)
        {
            return null;
        }

        min = Math.Min(min, 0m);
        max = Math.Max(max, 0m);

        return new DailyPnlChartOptions(dayKeys, series, min, max);
    }

    private static string GetDayKey(long? timestamp)
    {
        if (!timestamp.HasValue || timestamp.Value <= 0)
        {
            return string.Empty;
        }

        var date = DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value).UtcDateTime;
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }



    private void InvalidateSummaryCache()
    {
        _summaryBySymbolCache = null;
        _pnlByCoinCache = null;
        _dailyPnlChartCache = null;
    }

    private static void ApplyCalculatedFields(IReadOnlyList<TradingHistoryEntry> entries, CalculationState state, bool updateEntries = true)
    {
        foreach (var entry in entries)
        {
            var qty = Round10(entry.Size);
            var price = entry.Price;
            var fee = entry.Fee;
            var side = (entry.Side ?? string.Empty).Trim();
            var type = (entry.TransactionType ?? string.Empty).Trim();

            if (string.Equals(type, "SETTLEMENT", StringComparison.OrdinalIgnoreCase))
            {
                qty = 0m;
                price = 0m;
            }
            else if (string.Equals(type, "DELIVERY", StringComparison.OrdinalIgnoreCase))
            {
                ApplyDeliveryDetails(entry, ref qty, ref price);
            }
            else if (!string.Equals(type, "TRADE", StringComparison.OrdinalIgnoreCase))
            {
                qty = 0m;
            }

            var qtySigned = Round10(string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase) ? -qty : qty);

            state.SizeBySymbol.TryGetValue(entry.Symbol, out var posBefore);
            state.AvgPriceBySymbol.TryGetValue(entry.Symbol, out var avgBefore);
            posBefore = Round10(posBefore);

            var closeQty = Math.Sign(qtySigned) == -Math.Sign(posBefore)
                ? Round10(Math.Min(Math.Abs(qtySigned), Math.Abs(posBefore)))
                : 0m;
            var openQty = Round10(qtySigned - Math.Sign(qtySigned) * closeQty);

            var cashBefore = -avgBefore * posBefore;
            var cashAfter = cashBefore + (-avgBefore * closeQty * Math.Sign(qtySigned)) + (-price * openQty);
            var posAfter = Round10(posBefore + qtySigned);
            var avgAfter = Math.Abs(posAfter) < 0.000000001m ? 0m : -cashAfter / posAfter;

            var realized = 0m;
            if (closeQty != 0m)
            {
                realized = posBefore > 0m
                    ? (price - avgBefore) * closeQty
                    : (avgBefore - price) * closeQty;
            }

            var settleCoin = GetSettleCoin(entry.Currency);
            state.CumulativeBySettleCoin.TryGetValue(settleCoin, out var cumulativeBefore);
            var cumulativeAfter = realized + cumulativeBefore - fee;

            state.SizeBySymbol[entry.Symbol] = posAfter;
            state.AvgPriceBySymbol[entry.Symbol] = avgAfter;
            state.CumulativeBySettleCoin[settleCoin] = cumulativeAfter;

            if (updateEntries)
            {
                entry.Calculated = new TradingTransactionCalculated
                {
                    SizeAfter = FormatDecimal(posAfter),
                    AvgPriceAfter = FormatDecimal(avgAfter),
                    RealizedPnl = FormatDecimal(realized),
                    CumulativePnl = $"{FormatDecimal(cumulativeAfter)} {settleCoin}".Trim()
                };
                entry.ChangedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }
    }

    private static long GetStartTimestamp(DateTime date)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Local);
        return new DateTimeOffset(local).ToUnixTimeMilliseconds();
    }

    private static void ApplyDeliveryDetails(TradingHistoryEntry entry, ref decimal qty, ref decimal price)
    {
        if (string.IsNullOrWhiteSpace(entry.RawJson))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(entry.RawJson);
            var primary = doc.RootElement;
            if (primary.ValueKind == JsonValueKind.Array)
            {
                primary = primary.EnumerateArray().FirstOrDefault();
            }

            if (primary.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var position = ReadDecimal(primary, "position");
            var delivery = ReadDecimal(primary, "deliveryPrice", "tradePrice", "price", "execPrice");
            var strike = ReadDecimal(primary, "strike");
            var optType = '\0';

            if (TryGetOptionDetails(entry.Symbol, out var symbolOptType, out var symbolStrike))
            {
                optType = symbolOptType;
                if (strike == 0m)
                {
                    strike = symbolStrike;
                }
            }

            var intrinsicCall = Math.Max(delivery - strike, 0m);
            var intrinsicPut = Math.Max(strike - delivery, 0m);

            if (optType == 'C')
            {
                price = intrinsicCall;
            }
            else if (optType == 'P')
            {
                price = intrinsicPut;
            }

            if (position != 0m)
            {
                qty = Math.Abs(position);
            }
        }
        catch
        {
        }
    }

    private static decimal ReadDecimal(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var raw = value.GetString();
                if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return 0m;
    }

    private static bool TryGetOptionDetails(string? symbol, out char optType, out decimal strike)
    {
        optType = '\0';
        strike = 0m;

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var parts = symbol.Trim().ToUpperInvariant()
            .Split('-', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "C" || parts[i] == "P")
            {
                optType = parts[i][0];
                if (i > 0)
                {
                    decimal.TryParse(parts[i - 1], NumberStyles.Any, CultureInfo.InvariantCulture, out strike);
                }
                return true;
            }
        }

        return false;
    }

    private static string NormalizeSymbolKey(string? symbol)
    {
        return string.IsNullOrWhiteSpace(symbol)
            ? string.Empty
            : symbol.Trim().ToUpperInvariant();
    }

    private static string NormalizeCategoryKey(string? category)
    {
        return string.IsNullOrWhiteSpace(category)
            ? string.Empty
            : category.Trim().ToLowerInvariant();
    }

    private static string GetSettleCoin(string? currency)
    {
        var settleCoin = string.IsNullOrWhiteSpace(currency)
            ? "_unknown"
            : currency;

        return string.IsNullOrWhiteSpace(settleCoin) ? "_unknown" : settleCoin;
    }

    private static decimal ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0m;
        }

        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    private static decimal Round10(decimal value)
    {
        return Math.Round(value, 10, MidpointRounding.AwayFromZero);
    }

    private sealed class CalculationState
    {
        public Dictionary<string, decimal> SizeBySymbol { get; }
        public Dictionary<string, decimal> AvgPriceBySymbol { get; }
        public Dictionary<string, decimal> CumulativeBySettleCoin { get; }

        public CalculationState()
        {
            SizeBySymbol = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            AvgPriceBySymbol = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            CumulativeBySettleCoin = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        public CalculationState(TradingHistoryMeta meta)
        {
            SizeBySymbol = new Dictionary<string, decimal>(meta.SizeBySymbol, StringComparer.OrdinalIgnoreCase);
            AvgPriceBySymbol = new Dictionary<string, decimal>(meta.AvgPriceBySymbol, StringComparer.OrdinalIgnoreCase);
            CumulativeBySettleCoin = new Dictionary<string, decimal>(meta.CumulativeBySettleCoin, StringComparer.OrdinalIgnoreCase);
        }

        public void ApplyToMeta(TradingHistoryMeta meta)
        {
            meta.SizeBySymbol = new Dictionary<string, decimal>(SizeBySymbol, StringComparer.OrdinalIgnoreCase);
            meta.AvgPriceBySymbol = new Dictionary<string, decimal>(AvgPriceBySymbol, StringComparer.OrdinalIgnoreCase);
            meta.CumulativeBySettleCoin = new Dictionary<string, decimal>(CumulativeBySettleCoin, StringComparer.OrdinalIgnoreCase);
        }
    }

}






