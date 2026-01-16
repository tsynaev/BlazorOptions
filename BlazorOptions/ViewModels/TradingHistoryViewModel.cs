using System.Globalization;
using System.Text.Json;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class TradingHistoryViewModel
{
    private const int PageLimit = 100;
    private const int PageSize = 50;
    private static readonly TimeSpan WindowSize = TimeSpan.FromDays(7);
    private const string NoMoreCursor = "__END__";
    private readonly ExchangeSettingsService _settingsService;
    private readonly BybitTransactionService _transactionService;
    private readonly TradingHistoryStorageService _storageService;
    private TradingHistoryState _state = new();
    private IReadOnlyList<TradingSummaryRow>? _summaryBySymbolCache;
    private IReadOnlyList<TradingPnlByCoinRow>? _pnlByCoinCache;

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

    public int TotalTransactionsCount => _state.Transactions.Count;

    private readonly Dictionary<string, TradingTransaction> _viewCache = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Categories { get; } = new[] { "linear", "inverse", "spot", "option" };

    public DateTime? RegistrationDate { get; private set; }

    public bool IsRegistrationDateRequired => _state.Transactions.Count == 0 && !_state.RegistrationTimeMs.HasValue;

    public bool IsLoading { get; private set; }

    public bool IsLoadingOlder { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset? LastLoadedAt { get; private set; }

    public async Task InitializeAsync()
    {
        _state = await _storageService.LoadStateAsync();
        if (_state.RegistrationTimeMs.HasValue)
        {
            RegistrationDate = DateTimeOffset.FromUnixTimeMilliseconds(_state.RegistrationTimeMs.Value)
                .ToLocalTime()
                .Date;
        }
        if (NeedsRecalculation(_state.Transactions))
        {
            _state.Transactions = ApplyCalculatedFields(_state.Transactions);
        }
        _viewCache.Clear();
        InvalidateSummaryCache();
        OnChange?.Invoke();
    }

    /// <summary>
    /// Fetches new transactions forward from the last synced time (or registration date) up to now.
    /// </summary>
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

            if (IsRegistrationDateRequired)
            {
                ErrorMessage = "Select your registration date before loading transactions.";
                return;
            }

            var latest = new List<TradingTransactionRecord>();
            var registrationTime = _state.RegistrationTimeMs;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var category in Categories)
            {
                var startTime = GetForwardStartTime(category, registrationTime);
                if (!startTime.HasValue)
                {
                    continue;
                }

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
                    _state.LatestSyncedTimeMsByCategory[category] = forwardCursor;
                }
            }

            _state.Transactions = ApplyCalculatedFields(MergeTransactions(_state.Transactions, latest));
            _viewCache.Clear();
            InvalidateSummaryCache();
            await _storageService.SaveStateAsync(_state);
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
        _state.RegistrationTimeMs = date.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(date.Value.Date, DateTimeKind.Local)).ToUnixTimeMilliseconds()
            : null;

        await _storageService.SaveStateAsync(_state);
        _viewCache.Clear();
        InvalidateSummaryCache();
        OnChange?.Invoke();
    }

    /// <summary>
    /// Backfills older transactions for infinite scroll using stored cursors or oldest timestamps.
    /// </summary>
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

            var older = new List<TradingTransactionRecord>();

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
                    EndTime = HasCursor(cursor) ? null : GetOldestTimestamp(category)
                };

                var page = await _transactionService.GetTransactionsPageAsync(settings, query, CancellationToken.None);
                older.AddRange(page.Items);
                _state.OldestCursorByCategory[category] = string.IsNullOrWhiteSpace(page.NextCursor)
                    ? NoMoreCursor
                    : page.NextCursor;
            }

            _state.Transactions = ApplyCalculatedFields(MergeTransactions(_state.Transactions, older));
            _viewCache.Clear();
            InvalidateSummaryCache();
            await _storageService.SaveStateAsync(_state);
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

    private long? GetLatestTimestamp(string category)
    {
        var latest = _state.Transactions
            .Select(record => BybitTransactionService.ParseRaw(record))
            .Where(item => string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Timestamp ?? 0)
            .DefaultIfEmpty(0)
            .Max();

        return latest > 0 ? latest + 1 : null;
    }

    private long? GetForwardStartTime(string category, long? registrationTime)
    {
        if (_state.LatestSyncedTimeMsByCategory.TryGetValue(category, out var synced))
        {
            return synced;
        }

        return GetLatestTimestamp(category) ?? registrationTime;
    }

    private long? GetOldestTimestamp(string category)
    {
        var oldest = _state.Transactions
            .Select(record => BybitTransactionService.ParseRaw(record))
            .Where(item => string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Timestamp ?? long.MaxValue)
            .DefaultIfEmpty(long.MaxValue)
            .Min();

        if (oldest == long.MaxValue)
        {
            return null;
        }

        return oldest > 0 ? oldest - 1 : null;
    }

    private string? GetCursor(string category)
    {
        return _state.OldestCursorByCategory.TryGetValue(category, out var cursor)
            ? cursor
            : null;
    }

    private static bool HasCursor(string? cursor)
    {
        return !string.IsNullOrWhiteSpace(cursor);
    }

    private static List<TradingTransactionRecord> MergeTransactions(
        IReadOnlyList<TradingTransactionRecord> existing,
        IReadOnlyList<TradingTransactionRecord> latest)
    {
        var merged = new Dictionary<string, TradingTransactionRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in existing)
        {
            if (!string.IsNullOrWhiteSpace(item.UniqueKey))
            {
                merged[item.UniqueKey] = item;
            }
        }

        foreach (var item in latest)
        {
            var key = string.IsNullOrWhiteSpace(item.UniqueKey)
                ? Guid.NewGuid().ToString("N")
                : item.UniqueKey;
            merged[key] = item;
        }

        return merged.Values
            .OrderByDescending(item => BybitTransactionService.ParseRaw(item).Timestamp ?? 0)
            .ThenByDescending(item => BybitTransactionService.ParseRaw(item).TimeLabel, StringComparer.Ordinal)
            .ToList();
    }

    private static List<TradingTransactionRecord> ApplyCalculatedFields(
        IReadOnlyList<TradingTransactionRecord> orderedDesc)
    {
        var itemsDesc = orderedDesc.ToArray();
        if (itemsDesc.Length == 0)
        {
            return new List<TradingTransactionRecord>();
        }

        var itemsAsc = itemsDesc
            .Select((item, index) => (item, index))
            .OrderBy(entry => BybitTransactionService.ParseRaw(entry.item).Timestamp ?? long.MinValue)
            .ThenBy(entry => entry.index)
            .ToArray();

        var sizeBySymbol = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var avgBySymbol = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var cumulativeBySettleCoin = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var calculatedByIndex = new TradingTransactionCalculated?[itemsDesc.Length];

        foreach (var entry in itemsAsc)
        {
            var raw = BybitTransactionService.ParseRaw(entry.item);
            var symbol = string.IsNullOrWhiteSpace(raw.Symbol) ? "_unknown" : raw.Symbol;
            var qty = Round10(ParseDecimal(raw.Qty) ?? 0m);
            var price = ParseDecimal(raw.TradePrice) ?? 0m;
            var fee = ParseDecimal(raw.Fee) ?? 0m;
            var side = (raw.Side ?? string.Empty).Trim();
            var type = (raw.TransactionType ?? string.Empty).Trim();

            if (string.Equals(type, "SETTLEMENT", StringComparison.OrdinalIgnoreCase))
            {
                qty = 0m;
                price = 0m;
            }
            else if (string.Equals(type, "DELIVERY", StringComparison.OrdinalIgnoreCase))
            {
                var position = 0m;
                var delivery = 0m;
                var strike = 0m;
                if (entry.item.Data.Count > 0 && entry.item.Data[0].ValueKind == JsonValueKind.Object)
                {
                    var primary = entry.item.Data[0];
                    position = ReadDecimal(primary, "position");
                    delivery = ReadDecimal(primary, "deliveryPrice", "tradePrice", "price", "execPrice");
                    strike = ReadDecimal(primary, "strike");
                }
                var optType = '\0';
                if (TryGetOptionDetails(raw.Symbol, out var symbolOptType, out var symbolStrike))
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
            else if (!string.Equals(type, "TRADE", StringComparison.OrdinalIgnoreCase))
            {
                qty = 0m;
            }

            var qtySigned = Round10(string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase) ? -qty : qty);

            sizeBySymbol.TryGetValue(symbol, out var posBefore);
            avgBySymbol.TryGetValue(symbol, out var avgBefore);
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

            var settleCoin = string.IsNullOrWhiteSpace(raw.Currency) ? "_unknown" : raw.Currency;
            if (string.IsNullOrWhiteSpace(settleCoin))
            {
                settleCoin = "_unknown";
            }

            cumulativeBySettleCoin.TryGetValue(settleCoin, out var cumulativeBefore);
            var cumulativeAfter = realized + cumulativeBefore - fee;

            sizeBySymbol[symbol] = posAfter;
            avgBySymbol[symbol] = avgAfter;
            cumulativeBySettleCoin[settleCoin] = cumulativeAfter;

            calculatedByIndex[entry.index] = new TradingTransactionCalculated
            {
                SizeAfter = FormatDecimal(posAfter),
                AvgPriceAfter = FormatDecimal(avgAfter),
                RealizedPnl = FormatDecimal(realized),
                CumulativePnl = $"{FormatDecimal(cumulativeAfter)} {settleCoin}".Trim()
            };
        }

        return itemsDesc
            .Select((item, index) => calculatedByIndex[index] is { } calculated
                ? item with { Calculated = calculated }
                : item)
            .ToList();
    }

    private TradingTransaction BuildViewItem(TradingTransactionRecord record)
    {
        var raw = BybitTransactionService.ParseRaw(record);
        var calculated = record.Calculated;
        var qtyValue = ParseDecimal(raw.Qty) ?? 0m;
        var priceValue = ParseDecimal(raw.TradePrice) ?? 0m;
        var value = FormatDecimal(qtyValue * priceValue);

        return new TradingTransaction
        {
            UniqueKey = record.UniqueKey,
            TimeLabel = raw.TimeLabel,
            Timestamp = raw.Timestamp,
            Category = raw.Category,
            Symbol = raw.Symbol,
            TransactionType = raw.TransactionType,
            TransSubType = raw.TransSubType,
            Side = raw.Side,
            Funding = raw.Funding,
            OrderLinkId = raw.OrderLinkId,
            OrderId = raw.OrderId,
            Fee = raw.Fee,
            Change = raw.Change,
            CashFlow = raw.CashFlow,
            FeeRate = raw.FeeRate,
            BonusChange = raw.BonusChange,
            Size = raw.Size,
            Qty = raw.Qty,
            CashBalance = raw.CashBalance,
            Currency = raw.Currency,
            TradePrice = raw.TradePrice,
            TradeId = raw.TradeId,
            ExtraFees = raw.ExtraFees,
            SizeAfter = calculated?.SizeAfter ?? string.Empty,
            AvgPriceAfter = calculated?.AvgPriceAfter ?? string.Empty,
            RealizedPnl = calculated?.RealizedPnl ?? string.Empty,
            CumulativePnl = calculated?.CumulativePnl ?? string.Empty,
            Value = value
        };
    }

    private static bool NeedsRecalculation(IReadOnlyList<TradingTransactionRecord> records)
    {
        foreach (var record in records)
        {
            if (record.Calculated is null)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(record.Calculated.SizeAfter)
                && string.IsNullOrWhiteSpace(record.Calculated.AvgPriceAfter)
                && string.IsNullOrWhiteSpace(record.Calculated.RealizedPnl)
                && string.IsNullOrWhiteSpace(record.Calculated.CumulativePnl))
            {
                return true;
            }
        }

        return false;
    }

    public async Task EnsureRangeAsync(int startIndex, int count)
    {
        var needed = startIndex + count;
        if (needed <= _state.Transactions.Count)
        {
            return;
        }

        if (IsLoadingOlder || IsLoading)
        {
            return;
        }

        await LoadOlderAsync();
    }

    public IReadOnlyList<TradingTransaction> GetRange(int startIndex, int count)
    {
        var skip = Math.Max(0, startIndex);
        var take = Math.Max(0, count);
        var slice = _state.Transactions.Skip(skip).Take(take);
        var results = new List<TradingTransaction>(take);

        foreach (var record in slice)
        {
            if (!_viewCache.TryGetValue(record.UniqueKey, out var view))
            {
                view = BuildViewItem(record);
                _viewCache[record.UniqueKey] = view;
            }

            results.Add(view);
        }

        return results;
    }

    public IReadOnlyList<TradingTransaction> GetTradesForSymbol(string symbol, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return Array.Empty<TradingTransaction>();
        }

        var items = new List<TradingTransaction>();
        foreach (var record in _state.Transactions)
        {
            var raw = BybitTransactionService.ParseRaw(record);
            if (!string.Equals(raw.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(category)
                && !string.Equals(raw.Category, category, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!_viewCache.TryGetValue(record.UniqueKey, out var view))
            {
                view = BuildViewItem(record);
                _viewCache[record.UniqueKey] = view;
            }

            items.Add(view);
        }

        items = items
            .OrderByDescending(item => item.Timestamp ?? 0)
            .ThenByDescending(item => item.TimeLabel, StringComparer.Ordinal)
            .ToList();

        var cumulativeBySettleCoin = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (!TryParseWithTrailingCurrency(item.RealizedPnl, out var realized))
            {
                realized = ParseDecimal(item.RealizedPnl) ?? 0m;
            }

            if (!TryParseWithTrailingCurrency(item.Fee, out var fee))
            {
                fee = ParseDecimal(item.Fee) ?? 0m;
            }

            var settleCoin = !string.IsNullOrWhiteSpace(item.Currency)
                ? item.Currency
                : "_unknown";
            if (string.IsNullOrWhiteSpace(settleCoin))
            {
                settleCoin = "_unknown";
            }

            cumulativeBySettleCoin.TryGetValue(settleCoin, out var cumulativeBefore);
            var cumulativeAfter = realized + cumulativeBefore - fee;
            cumulativeBySettleCoin[settleCoin] = cumulativeAfter;

            items[i] = item with
            {
                CumulativePnl = $"{FormatDecimal(cumulativeAfter)} {settleCoin}".Trim()
            };
        }

        return items;
    }

    public IReadOnlyList<TradingSummaryRow> GetSummaryBySymbol()
    {
        if (_summaryBySymbolCache is not null)
        {
            return _summaryBySymbolCache;
        }

        var summary = new Dictionary<string, (string Category, string Symbol, string Coin, int Trades, decimal Qty, decimal Value, decimal Fees, decimal Realized)>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in _state.Transactions)
        {
            var raw = BybitTransactionService.ParseRaw(record);
            var symbol = string.IsNullOrWhiteSpace(raw.Symbol) ? "_unknown" : raw.Symbol;
            var category = string.IsNullOrWhiteSpace(raw.Category) ? "_unknown" : raw.Category;
            var settleCoin = GetSettleCoin(raw);
            var key = $"{category}|{symbol}|{settleCoin}";

            if (!summary.TryGetValue(key, out var totals))
            {
                totals = (category, symbol, settleCoin, 0, 0m, 0m, 0m, 0m);
            }

            totals.Trades += 1;
            totals.Qty += ParseDecimal(raw.Qty) ?? 0m;
            totals.Value += (ParseDecimal(raw.TradePrice) ?? 0m) * (ParseDecimal(raw.Qty) ?? 0m);
            totals.Fees += ParseDecimal(raw.Fee) ?? 0m;
            totals.Realized += ParseDecimal(record.Calculated?.RealizedPnl) ?? 0m;

            summary[key] = totals;
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

        return _summaryBySymbolCache;
    }

    public IReadOnlyList<TradingPnlByCoinRow> GetRealizedPnlBySettleCoin()
    {
        if (_pnlByCoinCache is not null)
        {
            return _pnlByCoinCache;
        }

        var totals = new Dictionary<string, (decimal Realized, decimal Fees)>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in _state.Transactions)
        {
            var raw = BybitTransactionService.ParseRaw(record);
            var settleCoin = GetSettleCoin(raw);

            if (!totals.TryGetValue(settleCoin, out var values))
            {
                values = (0m, 0m);
            }

            values.Realized += ParseDecimal(record.Calculated?.RealizedPnl) ?? 0m;
            values.Fees += ParseDecimal(raw.Fee) ?? 0m;
            totals[settleCoin] = values;
        }

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

        return _pnlByCoinCache;
    }

    public string GetRawJsonForSymbol(string symbol, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return string.Empty;
        }

        var records = _state.Transactions
            .Select(record => (record, raw: BybitTransactionService.ParseRaw(record)))
            .Where(entry => string.Equals(entry.raw.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(category)
                || string.Equals(entry.raw.Category, category, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.raw.Timestamp ?? 0)
            .ThenByDescending(entry => entry.raw.TimeLabel, StringComparer.Ordinal)
            .SelectMany(entry => entry.record.Data)
            .ToList();

        if (records.Count == 0)
        {
            return string.Empty;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(records, options);
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

    private static bool TryParseWithTrailingCurrency(string? value, out decimal parsed)
    {
        parsed = 0m;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var token = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return decimal.TryParse(token, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
    }

    private static string GetSettleCoin(TradingTransactionRaw raw)
    {
        var settleCoin = string.IsNullOrWhiteSpace(raw.Currency)
            ? "_unknown"
            : raw.Currency;

        return string.IsNullOrWhiteSpace(settleCoin) ? "_unknown" : settleCoin;
    }

    private void InvalidateSummaryCache()
    {
        _summaryBySymbolCache = null;
        _pnlByCoinCache = null;
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    private static decimal Round10(decimal value)
    {
        return Math.Round(value, 10, MidpointRounding.AwayFromZero);
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
}
