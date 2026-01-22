using System.Globalization;
using System.Linq;
using System.Text.Json;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class TradingHistoryViewModel
{
    private const int PageLimit = 100;
    private const int PageSize = 100;
    private static readonly TimeSpan WindowSize = TimeSpan.FromDays(7);
    private const string NoMoreCursor = "__END__";
    private readonly ExchangeSettingsService _settingsService;
    private readonly BybitTransactionService _transactionService;
    private readonly TradingHistoryStorageService _storageService;
    private TradingHistoryMeta _meta = new();
    private IReadOnlyList<TradingSummaryRow>? _summaryBySymbolCache;
    private IReadOnlyList<TradingPnlByCoinRow>? _pnlByCoinCache;
    private readonly List<TradingHistoryEntry> _loadedEntries = new();
    private bool _isInitialized;
    private bool _isLoadingRange;
    private bool _isLoadingSummary;
    private long? _loadedBeforeTimestamp;
    private string? _loadedBeforeKey;
    private int _totalCount;

    public TradingHistoryViewModel(
        ExchangeSettingsService settingsService,
        BybitTransactionService transactionService,
        TradingHistoryStorageService storageService)
    {
        _settingsService = settingsService;
        _transactionService = transactionService;
        _storageService = storageService;
    }

    public event Action? OnChange;

    public int TotalTransactionsCount => _totalCount;

    public IReadOnlyList<string> Categories { get; } = new[] { "linear", "inverse", "spot", "option" };

    public DateTime? RegistrationDate { get; private set; }

    public bool IsRegistrationDateRequired =>
        !_meta.RegistrationTimeMs.HasValue && !HasLatestSyncedTimes();

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
            var settings = await _settingsService.LoadBybitSettingsAsync();

            if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.ApiSecret))
            {
                ErrorMessage = "Bybit API credentials are missing. Add them in the Bybit settings page.";
                return;
            }

            var latest = new List<TradingTransactionRaw>();
            var registrationTime = _meta.RegistrationTimeMs;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var hasStart = false;

            foreach (var category in Categories)
            {
                var startTime = GetForwardStartTime(category, registrationTime);
                if (!startTime.HasValue)
                {
                    continue;
                }
                hasStart = true;

                var forwardCursor = startTime.Value;

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
                        latest.AddRange(page.Items);

                        if (string.IsNullOrWhiteSpace(page.NextCursor))
                        {
                            break;
                        }

                        cursor = page.NextCursor;
                    }

                    forwardCursor = endTime + 1;
                    _meta.LatestSyncedTimeMsByCategory[category] = forwardCursor;
                }
            }

            if (!hasStart)
            {
                ErrorMessage = "Select your registration date before loading transactions.";
                return;
            }

            var entries = latest.Select(MapRecordToEntry).ToList();
            var orderedAsc = entries
                .OrderBy(entry => entry.Timestamp)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                .ToList();

            var calculationState = new CalculationState(_meta);
            ApplyCalculatedFields(orderedAsc, calculationState);
            calculationState.ApplyToMeta(_meta);
            UpdateOldestSyncedTimes(entries);

            if (orderedAsc.Count > 0)
            {
                _meta.CalculatedThroughTimestamp = orderedAsc.Max(entry => entry.Timestamp);
            }

            await _storageService.SaveTradesAsync(entries);
            await _storageService.SaveMetaAsync(_meta);
            _totalCount = await _storageService.GetCountAsync();
            await LoadLatestPageFromDbAsync();
            if (_loadedEntries.Count == 0 && entries.Count > 0)
            {
                _loadedEntries.Clear();
                foreach (var entry in entries.OrderByDescending(item => item.Timestamp).ThenByDescending(item => item.Id, StringComparer.Ordinal))
                {
                    _loadedEntries.Add(entry);
                }

                if (_totalCount == 0)
                {
                    _totalCount = _loadedEntries.Count;
                }

                UpdatePagingCursor();
            }
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

    public async Task LoadOlderAsync()
    {
        if (IsLoading || IsLoadingOlder)
        {
            return;
        }

        ErrorMessage = null;
        IsLoadingOlder = true;
        OnChange?.Invoke();

        try
        {
            var settings = await _settingsService.LoadBybitSettingsAsync();

            if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.ApiSecret))
            {
                ErrorMessage = "Bybit API credentials are missing. Add them in the Bybit settings page.";
                return;
            }

            var older = new List<TradingTransactionRaw>();

            foreach (var category in Categories)
            {
                var cursor = GetCursor(category);
                if (cursor == NoMoreCursor)
                {
                    continue;
                }

                var query = new BybitTransactionQuery
                {
                    Category = category,
                    Limit = PageLimit,
                    Cursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor,
                    EndTime = HasCursor(cursor) ? null : GetOldestTimeForCategory(category)
                };

                var page = await _transactionService.GetTransactionsPageAsync(settings, query, CancellationToken.None);
                older.AddRange(page.Items);
                _meta.OldestCursorByCategory[category] = string.IsNullOrWhiteSpace(page.NextCursor)
                    ? NoMoreCursor
                    : page.NextCursor;
            }

            var entries = older.Select(MapRecordToEntry).ToList();
            if (entries.Count > 0)
            {
                _meta.RequiresRecalculation = true;
                UpdateOldestSyncedTimes(entries);
                await _storageService.SaveTradesAsync(entries);
                await _storageService.SaveMetaAsync(_meta);
                _totalCount = await _storageService.GetCountAsync();
                InvalidateSummaryCache();
                _ = RecalculateAsync();
                await LoadLatestPageFromDbAsync();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoadingOlder = false;
            OnChange?.Invoke();
        }
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
        return entries
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

    private TradingHistoryEntry MapRecordToEntry(TradingTransactionRaw raw)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return new TradingHistoryEntry
        {
            Id = raw.UniqueKey,
            Timestamp = raw.Timestamp ?? 0,
            Symbol = raw.Symbol,
            Category = raw.Category,
            TransactionType = raw.TransactionType,
            Side = raw.Side,
            Size = raw.Qty,
            Price = raw.TradePrice,
            Fee = raw.Fee,
            Currency = raw.Currency,
            Change = raw.Change,
            CashFlow = raw.CashFlow,
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
            value.Qty += ParseDecimal(entry.Size) ?? 0m;
            value.Value += (ParseDecimal(entry.Price) ?? 0m) * (ParseDecimal(entry.Size) ?? 0m);
            value.Fees += ParseDecimal(entry.Fee) ?? 0m;
            value.Realized += ParseDecimal(entry.Calculated?.RealizedPnl) ?? 0m;
            summary[key] = value;

            if (!totals.TryGetValue(settleCoin, out var pnlTotals))
            {
                pnlTotals = (0m, 0m);
            }

            pnlTotals.Realized += ParseDecimal(entry.Calculated?.RealizedPnl) ?? 0m;
            pnlTotals.Fees += ParseDecimal(entry.Fee) ?? 0m;
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

            var qty = ParseDecimal(entry.Size) ?? 0m;
            var price = ParseDecimal(entry.Price) ?? 0m;
            var fee = ParseDecimal(entry.Fee) ?? 0m;

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

    private static string GetDayKey(long? timestamp)
    {
        if (!timestamp.HasValue || timestamp.Value <= 0)
        {
            return string.Empty;
        }

        var date = DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value).UtcDateTime;
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private long? GetForwardStartTime(string category, long? registrationTime)
    {
        if (_meta.LatestSyncedTimeMsByCategory.TryGetValue(category, out var synced))
        {
            return synced;
        }

        if (registrationTime.HasValue)
        {
            return registrationTime.Value;
        }

        return null;
    }

    private bool HasLatestSyncedTimes()
    {
        foreach (var value in _meta.LatestSyncedTimeMsByCategory.Values)
        {
            if (value > 0)
            {
                return true;
            }
        }

        return false;
    }

    private long? GetOldestTimeForCategory(string category)
    {
        if (_meta.OldestSyncedTimeMsByCategory.TryGetValue(category, out var oldest))
        {
            return oldest > 1 ? oldest - 1 : oldest;
        }

        return _meta.RegistrationTimeMs;
    }

    private string? GetCursor(string category)
    {
        return _meta.OldestCursorByCategory.TryGetValue(category, out var cursor)
            ? cursor
            : null;
    }

    private static bool HasCursor(string? cursor)
    {
        return !string.IsNullOrWhiteSpace(cursor);
    }

    private void InvalidateSummaryCache()
    {
        _summaryBySymbolCache = null;
        _pnlByCoinCache = null;
    }

    private static void ApplyCalculatedFields(IReadOnlyList<TradingHistoryEntry> entries, CalculationState state, bool updateEntries = true)
    {
        foreach (var entry in entries)
        {
            var qty = Round10(ParseDecimal(entry.Size) ?? 0m);
            var price = ParseDecimal(entry.Price) ?? 0m;
            var fee = ParseDecimal(entry.Fee) ?? 0m;
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

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
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

    private void UpdateOldestSyncedTimes(IEnumerable<TradingHistoryEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!entry.Timestamp.HasValue || entry.Timestamp.Value <= 0)
            {
                continue;
            }

            var category = entry.Category ?? string.Empty;
            if (!_meta.OldestSyncedTimeMsByCategory.TryGetValue(category, out var existing) || entry.Timestamp.Value < existing)
            {
                _meta.OldestSyncedTimeMsByCategory[category] = entry.Timestamp.Value;
            }
        }
    }
}






