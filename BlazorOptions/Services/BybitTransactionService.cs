using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public record BybitTransactionQuery
{
    public string? AccountType { get; init; }
    public string Category { get; init; } = "linear";
    public int Limit { get; init; } = 50;
    public string? Cursor { get; init; }
    public long? StartTime { get; init; }
    public long? EndTime { get; init; }
}

public record BybitTransactionPage(IReadOnlyList<TradingTransactionRecord> Items, string? NextCursor);

public class BybitTransactionService : BybitApiService
{
    public BybitTransactionService(HttpClient httpClient)
        : base(httpClient)
    {
    }

    public async Task<BybitTransactionPage> GetTransactionsPageAsync(
        BybitSettings settings,
        BybitTransactionQuery query,
        CancellationToken cancellationToken)
    {
        var queryString = BuildQueryString(query);
        var payload = await SendSignedRequestAsync(
            HttpMethod.Get,
            "/v5/account/transaction-log",
            settings,
            queryString,
            cancellationToken: cancellationToken);

        return ParseTransactions(payload, query.Category);
    }


    private static string BuildQueryString(BybitTransactionQuery query)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["category"] = query.Category,
            ["limit"] = Math.Clamp(query.Limit, 1, 100).ToString(CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(query.AccountType))
        {
            parameters["accountType"] = query.AccountType;
        }

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

    private static BybitTransactionPage ParseTransactions(string payload, string categoryFallback)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        ThrowIfRetCodeError(root);

        if (!root.TryGetProperty("result", out var resultElement))
        {
            return new BybitTransactionPage(Array.Empty<TradingTransactionRecord>(), null);
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
            return new BybitTransactionPage(Array.Empty<TradingTransactionRecord>(), nextCursor);
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

        var items = new List<TradingTransactionRecord>(order.Count);
        foreach (var key in order)
        {
            var entries = grouped[key];
            var parsed = MapTransaction(entries, categoryFallback);
            items.Add(new TradingTransactionRecord
            {
                UniqueKey = parsed.UniqueKey,
                Data = entries,
                Calculated = new TradingTransactionCalculated()
            });
        }

        return new BybitTransactionPage(items, nextCursor);
    }

    public static TradingTransactionRaw ParseRaw(TradingTransactionRecord record)
    {
        if (record.Data.Count == 0)
        {
            return new TradingTransactionRaw();
        }

        var category = ReadString(record.Data[0], "category");
        return MapTransaction(record.Data, category);
    }

    public static TradingTransactionRaw ParseRaw(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return new TradingTransactionRaw();
        }

        var category = ReadString(entry, "category");
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
        var timeLabel = FormatTimestamp(timestamp)
            ?? ReadString(entry, "transactionTime");
        _ = ReadTimestamp(entry, "transactionTime");
        var transactionId = ReadString(entry, "tradeId");
        var rawId = ReadString(entry, "id");
        var orderId = ReadString(entry, "orderId");
        var symbol = ReadString(entry, "symbol");
        var category = ReadString(entry, "category");
        if (string.IsNullOrWhiteSpace(category))
        {
            category = categoryFallback;
        }

        var type = ReadString(entry, "type");
        var transSubType = ReadString(entry, "transSubType");
        var side = ReadString(entry, "side");
        var funding = ReadString(entry, "funding");
        var orderLinkId = ReadString(entry, "orderLinkId");
        var bonusChange = ReadString(entry, "bonusChange");
        var size = ReadString(entry, "size");
        var cashBalance = ReadString(entry, "cashBalance");
        var tradeId = ReadString(entry, "tradeId");
        var extraFees = ReadString(entry, "extraFees");
        var feeRate = ReadString(entry, "feeRate");
        var qty = ReadString(entry, "qty");
        var price = ReadString(entry, "tradePrice");
        var fee = ReadString(entry, "fee");
        var currency = ReadString(entry, "currency");
        var change = ReadString(entry, "change");
        var cashFlow = ReadString(entry, "cashFlow");

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
            TimeLabel = string.IsNullOrWhiteSpace(timeLabel) ? "N/A" : timeLabel,
        };
    }

    private static string GetGroupingKey(JsonElement entry, string categoryFallback)
    {
        if (IsSpotTrade(entry, categoryFallback))
        {
            var orderId = ReadString(entry, "orderId");
            var tradeId = ReadString(entry, "tradeId");
            var timestamp = ReadTimestamp(entry, "transactionTime");
            var symbol = ReadString(entry, "symbol");
            var side = ReadString(entry, "side");

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
        var orderId = ReadString(entry, "orderId");
        var rawId = ReadString(entry, "id");
        var symbol = ReadString(entry, "symbol");
        var type = ReadString(entry, "type");
        var side = ReadString(entry, "side");
        var execPrice = ReadString(entry, "tradePrice");
        var qty = ReadString(entry, "qty");
        var fee = ReadString(entry, "fee");
        var currency = ReadString(entry, "currency");
        var category = ReadString(entry, "category");
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
        var symbol = ReadString(primary, "symbol");
        var side = ReadString(primary, "side");
        var type = ReadString(primary, "type");
        var transSubType = ReadString(primary, "transSubType");
        var funding = ReadString(primary, "funding");
        var orderLinkId = ReadString(primary, "orderLinkId");
        var orderId = ReadString(primary, "orderId");
        var bonusChange = ReadString(primary, "bonusChange");
        var size = ReadString(primary, "size");
        var cashBalance = ReadString(primary, "cashBalance");
        var tradeId = ReadString(primary, "tradeId");
        var extraFees = ReadString(primary, "extraFees");
        var timestamp = ReadTimestamp(primary, "transactionTime");
        var timeLabel = FormatTimestamp(timestamp)
            ?? ReadString(primary, "transactionTime");
        var execTimeLabel = timeLabel ?? "N/A";
        var tradePrice = ReadString(primary, "tradePrice");
        var tradePriceValue = TryParseDecimal(tradePrice) ?? 0m;

        var currencies = entries
            .Select(entry => ReadString(entry, "currency"))
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
            string.Equals(ReadString(entry, "currency"), baseCoin, StringComparison.OrdinalIgnoreCase));
        var baseQty = baseLeg.ValueKind != JsonValueKind.Undefined
            ? Math.Abs(ReadDecimal(baseLeg, "qty"))
            : 0m;

        var execQty = FormatDecimal(baseQty);

        var feeTotal = 0m;
        foreach (var entry in entries)
        {
            var fee = ReadDecimal(entry, "fee");
            if (fee == 0m)
            {
                continue;
            }

            var feeCurrency = ReadString(entry, "currency");
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
            Fee = FormatDecimal(feeTotal),
            Change = string.Empty,
            CashFlow = string.Empty,
            FeeRate = ReadString(primary, "feeRate"),
            BonusChange = bonusChange,
            Size = size,
            Qty = execQty,
            CashBalance = cashBalance,
            Currency = quote ?? string.Empty,
            TradePrice = tradePrice,
            TradeId = tradeId,
            ExtraFees = extraFees,
            Timestamp = timestamp,
            TimeLabel = string.IsNullOrWhiteSpace(timeLabel) ? "N/A" : timeLabel,
        };
    }

    private static bool IsSpotTrade(JsonElement entry, string categoryFallback)
    {
        var category = ReadString(entry, "category");
        if (string.IsNullOrWhiteSpace(category))
        {
            category = categoryFallback;
        }

        var type = ReadString(entry, "type");

        return string.Equals(category, "spot", StringComparison.OrdinalIgnoreCase)
               && string.Equals(type, "TRADE", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FormatTimestamp(long? timestamp)
    {
        if (!timestamp.HasValue)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value)
                .ToLocalTime()
                .ToString("g", CultureInfo.CurrentCulture);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)
                || value.ValueKind == JsonValueKind.Null
                || value.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            return value.GetRawText().Trim('"');
        }

        return string.Empty;
    }

    private static decimal? TryParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static decimal? TryMultiply(string? left, string? right)
    {
        var leftValue = TryParseDecimal(left);
        var rightValue = TryParseDecimal(right);

        if (!leftValue.HasValue || !rightValue.HasValue)
        {
            return null;
        }

        return leftValue.Value * rightValue.Value;
    }

    private static string FormatDecimal(decimal? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.##########", CultureInfo.InvariantCulture)
            : string.Empty;
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

    private static long? ReadLong(JsonElement element, params string[] names)
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
                if (long.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
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
