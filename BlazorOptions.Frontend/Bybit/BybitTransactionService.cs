using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorOptions.API.TradingHistory;
using BlazorOptions.ViewModels;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public class BybitTransactionService : BybitApiService, ITransactionHistoryService
{
    private const string AccountType = "UNIFIED";
    private const int PageLimit = 100;
    private static readonly TimeSpan WindowSize = TimeSpan.FromDays(7);

    private readonly IOptions<BybitSettings> _bybitSettingsOptions;
    private readonly IBybitPrivateStreamService _privateStreamService;
    private readonly ITradingHistoryPort _tradingHistoryPort;
    private readonly string _exchangeConnectionId;
    private readonly object _syncRoot = new();
    private readonly HashSet<string> _pendingCategories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Func<IReadOnlyList<TradingHistoryEntry>, Task>> _subscribers = new();
    private IDisposable? _topicSubscription;
    private IDisposable? _reconnectSubscription;
    private Task? _syncLoopTask;
    private bool _isInitialized;

    public BybitTransactionService(
        HttpClient httpClient,
        IOptions<BybitSettings> bybitSettingsOptions,
        IBybitPrivateStreamService privateStreamService,
        ITradingHistoryPort tradingHistoryPort,
        string exchangeConnectionId)
        : base(httpClient)
    {
        _bybitSettingsOptions = bybitSettingsOptions;
        _privateStreamService = privateStreamService;
        _tradingHistoryPort = tradingHistoryPort;
        _exchangeConnectionId = exchangeConnectionId;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        _topicSubscription = await _privateStreamService.SubscribeTopicAsync(
            "execution",
            HandleExecutionTopicAsync,
            cancellationToken);
        _reconnectSubscription = await _privateStreamService.SubscribeReconnectAsync(
            HandleReconnectAsync,
            cancellationToken);
        _ = EnqueueSyncAsync(GetAllCategories());
    }

    public async Task<ExchangeTransactionPage> GetTransactionsPageAsync(
        ExchangeTransactionQuery query,
        CancellationToken cancellationToken = default)
    {
        return await GetTransactionsPageAsync(_bybitSettingsOptions.Value, query, cancellationToken);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBySymbolAsync(
        string symbol,
        string? category,
        DateTime? sinceDateUtc,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _tradingHistoryPort.LoadBySymbolAsync(symbol, category, sinceDateUtc, _exchangeConnectionId);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBySymbolsAsync(
        TradingHistoryRequest[] requests,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _tradingHistoryPort.LoadBySymbolsAsync(requests, _exchangeConnectionId);
    }

    public async ValueTask<IDisposable> SubscribeExecutionsAsync(
        Func<IReadOnlyList<TradingHistoryEntry>, Task> handler,
        CancellationToken cancellationToken = default)
    {
        if (handler is null)
        {
            return new SubscriptionRegistration(() => { });
        }

        await InitializeAsync(cancellationToken);
        lock (_syncRoot)
        {
            _subscribers.Add(handler);
        }

        return new SubscriptionRegistration(() =>
        {
            lock (_syncRoot)
            {
                _subscribers.Remove(handler);
            }
        });
    }

    public async Task<ExchangeTransactionPage> GetTransactionsPageAsync(
        BybitSettings settings,
        ExchangeTransactionQuery query,
        CancellationToken cancellationToken)
    {
        var queryString = BuildQueryString(query);
        var payload = await SendSignedRequestAsync(
            HttpMethod.Get,
            settings.TransactionLogUri,
            settings,
            queryString,
            cancellationToken: cancellationToken);

        return ParseTransactions(payload, query.Category);
    }

    private static string BuildQueryString(ExchangeTransactionQuery query)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["category"] = query.Category,
            ["limit"] = Math.Clamp(query.Limit, 1, 100).ToString(CultureInfo.InvariantCulture),
            ["accountType"] = AccountType
        };

        if (query.StartTime.HasValue)
        {
            parameters["startTime"] = query.StartTime.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (query.EndTime.HasValue)
        {
            parameters["endTime"] = query.EndTime.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            parameters["cursor"] = query.Cursor;
        }

        return BuildQueryString(parameters);
    }

    private static ExchangeTransactionPage ParseTransactions(string payload, string categoryFallback)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        ThrowIfRetCodeError(root);

        if (!root.TryGetProperty("result", out var resultElement))
        {
            return new ExchangeTransactionPage(Array.Empty<TradingTransactionRaw>(), null);
        }

        string? nextCursor = null;
        if (resultElement.TryGetProperty("nextPageCursor", out var cursorElement)
            && cursorElement.ValueKind == JsonValueKind.String)
        {
            nextCursor = cursorElement.GetString();
        }

        if (!resultElement.TryGetProperty("list", out var listElement)
            || listElement.ValueKind != JsonValueKind.Array)
        {
            return new ExchangeTransactionPage(Array.Empty<TradingTransactionRaw>(), nextCursor);
        }

        var grouped = new Dictionary<string, List<JsonElement>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var entry in listElement.EnumerateArray())
        {
            var key = GetGroupingKey(entry, categoryFallback);
            if (!grouped.TryGetValue(key, out var bucket))
            {
                bucket = new List<JsonElement>();
                grouped[key] = bucket;
                order.Add(key);
            }

            bucket.Add(entry.Clone());
        }

        var items = new List<TradingTransactionRaw>(order.Count);
        foreach (var key in order)
        {
            var entries = grouped[key];
            var parsed = MapTransaction(entries, categoryFallback);
            items.Add(parsed);
        }

        return new ExchangeTransactionPage(items, nextCursor);
    }

    public static TradingTransactionRaw ParseRaw(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return new TradingTransactionRaw();
        }

        var category = JsonElementExtensions.ReadString(entry, "category");
        return MapTransaction(entry, category);
    }

    internal static TradingTransactionRaw MapTransaction(IReadOnlyList<JsonElement> entries, string categoryFallback)
    {
        if (entries.Count == 0)
        {
            return new TradingTransactionRaw();
        }

        if (IsSpotTrade(entries[0], categoryFallback))
        {
            return MapSpotTrade(entries, categoryFallback);
        }

        return MapTransaction(entries[0], categoryFallback);
    }

    internal static TradingTransactionRaw MapTransaction(JsonElement entry, string categoryFallback)
    {
        var timestamp = ReadTimestamp(entry, "transactionTime");
        var rawId = JsonElementExtensions.ReadString(entry, "id");
        var orderId = JsonElementExtensions.ReadString(entry, "orderId");
        var symbol = JsonElementExtensions.ReadString(entry, "symbol");
        var category = JsonElementExtensions.ReadString(entry, "category");
        if (string.IsNullOrWhiteSpace(category))
        {
            category = categoryFallback;
        }

        var type = JsonElementExtensions.ReadString(entry, "type");
        var transSubType = JsonElementExtensions.ReadString(entry, "transSubType");
        var side = JsonElementExtensions.ReadString(entry, "side");
        var funding = JsonElementExtensions.ReadNullableDecimal(entry, "funding");
        var orderLinkId = JsonElementExtensions.ReadString(entry, "orderLinkId");
        var bonusChange = JsonElementExtensions.ReadNullableDecimal(entry, "bonusChange");
        var size = JsonElementExtensions.ReadNullableDecimal(entry, "size");
        var cashBalance = JsonElementExtensions.ReadNullableDecimal(entry, "cashBalance");
        var tradeId = JsonElementExtensions.ReadString(entry, "tradeId");
        var extraFees = JsonElementExtensions.ReadString(entry, "extraFees");
        var feeRate = JsonElementExtensions.ReadNullableDecimal(entry, "feeRate");
        var qty = JsonElementExtensions.ReadNullableDecimal(entry, "qty");
        var price = JsonElementExtensions.ReadNullableDecimal(entry, "tradePrice");
        var fee = JsonElementExtensions.ReadNullableDecimal(entry, "fee");
        var currency = JsonElementExtensions.ReadString(entry, "currency");
        var change = JsonElementExtensions.ReadNullableDecimal(entry, "change");
        var cashFlow = JsonElementExtensions.ReadNullableDecimal(entry, "cashFlow");

        var uniqueKey = BuildUniqueKey(entry, categoryFallback);

        return new TradingTransactionRaw
        {
            UniqueKey = uniqueKey,
            RawJson = entry.GetRawText(),
            Category = category,
            Symbol = symbol,
            TransactionType = type,
            TransSubType = transSubType,
            Side = side,
            Funding = funding,
            OrderLinkId = orderLinkId,
            OrderId = orderId,
            Fee = fee,
            Change = change,
            CashFlow = cashFlow,
            FeeRate = feeRate,
            BonusChange = bonusChange,
            Size = size,
            Qty = qty,
            CashBalance = cashBalance,
            Currency = currency,
            TradePrice = price,
            TradeId = tradeId,
            ExtraFees = extraFees,
            Timestamp = timestamp,
        };
    }

    private async Task HandleExecutionTopicAsync(IReadOnlyList<JsonElement> entries)
    {
        var categories = entries
            .Select(entry => JsonElementExtensions.ReadString(entry, "category"))
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _ = EnqueueSyncAsync(categories);
        await Task.CompletedTask;
    }

    private Task HandleReconnectAsync()
    {
        _ = EnqueueSyncAsync(GetAllCategories());
        return Task.CompletedTask;
    }

    private Task EnqueueSyncAsync(IReadOnlyCollection<string> categories)
    {
        if (categories.Count == 0)
        {
            return Task.CompletedTask;
        }

        lock (_syncRoot)
        {
            foreach (var category in categories)
            {
                if (!string.IsNullOrWhiteSpace(category))
                {
                    _pendingCategories.Add(category.Trim().ToLowerInvariant());
                }
            }

            if (_syncLoopTask is null || _syncLoopTask.IsCompleted)
            {
                _syncLoopTask = RunSyncLoopAsync();
            }

            return _syncLoopTask;
        }
    }

    private async Task RunSyncLoopAsync()
    {
        while (true)
        {
            string[] categories;
            lock (_syncRoot)
            {
                if (_pendingCategories.Count == 0)
                {
                    return;
                }

                categories = _pendingCategories.ToArray();
                _pendingCategories.Clear();
            }

            try
            {
                var savedEntries = await SyncLatestAsync(categories);
                if (savedEntries.Count > 0)
                {
                    await NotifySubscribersAsync(savedEntries);
                }
            }
            catch
            {
                // Background history sync should retry on the next exchange event.
            }
        }
    }

    private async Task<IReadOnlyList<TradingHistoryEntry>> SyncLatestAsync(IReadOnlyCollection<string> categories)
    {
        var meta = await _tradingHistoryPort.LoadMetaAsync(_exchangeConnectionId);
        var registrationTime = meta.RegistrationTimeMs;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var savedEntries = new List<TradingHistoryEntry>();

        foreach (var category in categories)
        {
            long startTime;
            if (!meta.LatestSyncedTimeMsByCategory.TryGetValue(category, out startTime))
            {
                if (registrationTime.HasValue)
                {
                    startTime = registrationTime.Value;
                }
                else
                {
                    continue;
                }
            }

            var lastStartTime = startTime;
            var forwardCursor = startTime;
            var categoryHadTrades = false;

            while (forwardCursor < nowMs)
            {
                var endTime = Math.Min(forwardCursor + (long)WindowSize.TotalMilliseconds, nowMs);
                string? cursor = null;

                while (true)
                {
                    var query = new ExchangeTransactionQuery
                    {
                        Category = category,
                        Limit = PageLimit,
                        Cursor = cursor,
                        StartTime = forwardCursor,
                        EndTime = endTime
                    };

                    var page = await GetTransactionsPageAsync(query, CancellationToken.None);
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

                        var entries = page.Items
                            .Select(TradingHistoryEntryMapper.MapRecordToEntry)
                            .ToList();
                        var orderedAsc = TradingHistoryEntryOrdering.OrderAscending(entries).ToList();

                        if (orderedAsc.Count > 0)
                        {
                            var saveResult = await _tradingHistoryPort.SaveTradesAsync(orderedAsc, _exchangeConnectionId);
                            if (saveResult.InsertedIds.Count > 0)
                            {
                                var insertedIds = saveResult.InsertedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                                foreach (var entry in orderedAsc)
                                {
                                    if (insertedIds.Contains(entry.Id))
                                    {
                                        savedEntries.Add(entry);
                                    }
                                }
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(page.NextCursor))
                    {
                        break;
                    }

                    cursor = page.NextCursor;
                }

                if (!categoryHadTrades)
                {
                    lastStartTime = forwardCursor;
                }

                forwardCursor = endTime + 1;
                meta.LatestSyncedTimeMsByCategory[category] = lastStartTime;
            }
        }

        await _tradingHistoryPort.SaveMetaAsync(meta, _exchangeConnectionId);
        return savedEntries;
    }

    private async Task NotifySubscribersAsync(IReadOnlyList<TradingHistoryEntry> entries)
    {
        Func<IReadOnlyList<TradingHistoryEntry>, Task>[] handlers;
        lock (_syncRoot)
        {
            handlers = _subscribers.ToArray();
        }

        foreach (var handler in handlers)
        {
            await handler.Invoke(entries);
        }
    }

    private static string[] GetAllCategories()
    {
        return ["linear", "inverse", "spot", "option"];
    }

    private static string GetGroupingKey(JsonElement entry, string categoryFallback)
    {
        if (IsSpotTrade(entry, categoryFallback))
        {
            var orderId = JsonElementExtensions.ReadString(entry, "orderId");
            var tradeId = JsonElementExtensions.ReadString(entry, "tradeId");
            var timestamp = ReadTimestamp(entry, "transactionTime");
            var symbol = JsonElementExtensions.ReadString(entry, "symbol");
            var side = JsonElementExtensions.ReadString(entry, "side");

            if (!string.IsNullOrWhiteSpace(orderId))
            {
                return $"spot|{orderId}|{timestamp}|{symbol}|{side}";
            }

            if (!string.IsNullOrWhiteSpace(tradeId))
            {
                return $"spot|{tradeId}|{timestamp}|{symbol}|{side}";
            }

            return $"spot|{symbol}|{timestamp}|{side}";
        }

        return BuildUniqueKey(entry, categoryFallback);
    }

    private static string BuildUniqueKey(JsonElement entry, string categoryFallback)
    {
        var timestamp = ReadTimestamp(entry, "transactionTime");
        var orderId = JsonElementExtensions.ReadString(entry, "orderId");
        var rawId = JsonElementExtensions.ReadString(entry, "id");
        var symbol = JsonElementExtensions.ReadString(entry, "symbol");
        var type = JsonElementExtensions.ReadString(entry, "type");
        var side = JsonElementExtensions.ReadString(entry, "side");
        var execPrice = JsonElementExtensions.ReadString(entry, "tradePrice");
        var qty = JsonElementExtensions.ReadString(entry, "qty");
        var fee = JsonElementExtensions.ReadString(entry, "fee");
        var currency = JsonElementExtensions.ReadString(entry, "currency");
        var category = JsonElementExtensions.ReadString(entry, "category");
        if (string.IsNullOrWhiteSpace(category))
        {
            category = categoryFallback;
        }

        if (!string.IsNullOrWhiteSpace(rawId))
        {
            return rawId;
        }

        if (!string.IsNullOrWhiteSpace(orderId))
        {
            return $"{orderId}|{timestamp}|{qty}|{fee}|{currency}|{side}";
        }

        return $"{type}|{symbol}|{timestamp}|{qty}|{execPrice}|{fee}|{currency}|{category}|{side}";
    }

    private static TradingTransactionRaw MapSpotTrade(IReadOnlyList<JsonElement> entries, string categoryFallback)
    {
        var primary = entries[0];
        var symbol = JsonElementExtensions.ReadString(primary, "symbol");
        var side = JsonElementExtensions.ReadString(primary, "side");
        var type = JsonElementExtensions.ReadString(primary, "type");
        var transSubType = JsonElementExtensions.ReadString(primary, "transSubType");
        var funding = JsonElementExtensions.ReadNullableDecimal(primary, "funding");
        var orderLinkId = JsonElementExtensions.ReadString(primary, "orderLinkId");
        var orderId = JsonElementExtensions.ReadString(primary, "orderId");
        var bonusChange = JsonElementExtensions.ReadNullableDecimal(primary, "bonusChange");
        var size = JsonElementExtensions.ReadNullableDecimal(primary, "size");
        var cashBalance = JsonElementExtensions.ReadNullableDecimal(primary, "cashBalance");
        var tradeId = JsonElementExtensions.ReadString(primary, "tradeId");
        var extraFees = JsonElementExtensions.ReadString(primary, "extraFees");
        var timestamp = ReadTimestamp(primary, "transactionTime");
        var tradePrice = JsonElementExtensions.ReadString(primary, "tradePrice");
        var tradePriceValue = JsonElementExtensions.ReadNullableDecimal(primary, "tradePrice") ?? 0m;

        var currencies = entries
            .Select(entry => JsonElementExtensions.ReadString(entry, "currency"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var quote = currencies.FirstOrDefault(cur =>
            !string.IsNullOrWhiteSpace(symbol)
            && symbol.EndsWith(cur, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(quote) && currencies.Count == 1)
        {
            quote = currencies[0];
        }

        var baseCoin = currencies.FirstOrDefault(cur => !string.Equals(cur, quote, StringComparison.OrdinalIgnoreCase))
            ?? currencies.FirstOrDefault()
            ?? string.Empty;

        var baseLeg = entries.FirstOrDefault(entry =>
            string.Equals(JsonElementExtensions.ReadString(entry, "currency"), baseCoin, StringComparison.OrdinalIgnoreCase));
        var baseQty = baseLeg.ValueKind != JsonValueKind.Undefined
            ? Math.Abs(JsonElementExtensions.ReadDecimal(baseLeg, "qty"))
            : 0m;

        var execQty = FormatDecimal(baseQty);

        var feeTotal = 0m;
        foreach (var entry in entries)
        {
            var fee = JsonElementExtensions.ReadDecimal(entry, "fee");
            if (fee == 0m)
            {
                continue;
            }

            var feeCurrency = JsonElementExtensions.ReadString(entry, "currency");
            if (string.Equals(feeCurrency, quote, StringComparison.OrdinalIgnoreCase))
            {
                feeTotal += fee;
            }
            else if (string.Equals(feeCurrency, baseCoin, StringComparison.OrdinalIgnoreCase))
            {
                feeTotal += fee * tradePriceValue;
            }
        }

        var uniqueKey = !string.IsNullOrWhiteSpace(orderId)
            ? $"{orderId}|{timestamp}|{symbol}|{side}"
            : $"{symbol}|{timestamp}|{side}|{tradePrice}|{execQty}|{quote}";

        var rawJson = JsonSerializer.Serialize(entries);

        return new TradingTransactionRaw
        {
            UniqueKey = uniqueKey,
            RawJson = rawJson,
            Category = "spot",
            Symbol = symbol,
            TransactionType = type,
            TransSubType = transSubType,
            Side = side,
            Funding = funding,
            OrderLinkId = orderLinkId,
            OrderId = orderId,
            Fee = feeTotal,
            Change = null,
            CashFlow = null,
            FeeRate = JsonElementExtensions.ReadNullableDecimal(primary, "feeRate"),
            BonusChange = bonusChange,
            Size = size,
            Qty = baseQty,
            CashBalance = cashBalance,
            Currency = quote ?? string.Empty,
            TradePrice = tradePriceValue,
            TradeId = tradeId,
            ExtraFees = extraFees,
            Timestamp = timestamp,
        };
    }

    private static bool IsSpotTrade(JsonElement entry, string categoryFallback)
    {
        var category = JsonElementExtensions.ReadString(entry, "category");
        if (string.IsNullOrWhiteSpace(category))
        {
            category = categoryFallback;
        }

        var type = JsonElementExtensions.ReadString(entry, "type");

        return string.Equals(category, "spot", StringComparison.OrdinalIgnoreCase)
               && string.Equals(type, "TRADE", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatDecimal(decimal? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.##########", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static long? ReadTimestamp(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var raw = value.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                if (long.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                if (DateTimeOffset.TryParse(
                        raw,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsedDate))
                {
                    return parsedDate.ToUnixTimeMilliseconds();
                }

                if (DateTimeOffset.TryParseExact(
                        raw,
                        new[] { "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss" },
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsedExact))
                {
                    return parsedExact.ToUnixTimeMilliseconds();
                }
            }
        }

        return null;
    }
}
