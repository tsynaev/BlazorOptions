using System.Globalization;
using System.Text.Json;
using BlazorOptions.ViewModels;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public sealed class BybitOrderService : BybitApiService
{
    private const string RequestPath = "/v5/order/realtime";
    private const string DefaultSettleCoin = "USDT";
    private const int PageSize = 50;
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;

    public BybitOrderService(HttpClient httpClient, IOptions<BybitSettings> bybitSettingsOptions)
        : base(httpClient)
    {
        _bybitSettingsOptions = bybitSettingsOptions;
    }

    public async Task<IReadOnlyList<BybitOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        var settings = _bybitSettingsOptions.Value;
        var allOrders = new List<BybitOrder>();

        foreach (var category in new[] { "linear", "inverse" })
        {
            allOrders.AddRange(await GetOrdersByCategoryAsync(settings, category, DefaultSettleCoin, cancellationToken));
        }

        allOrders.AddRange(await GetOrdersByCategoryAsync(settings, "option", null, cancellationToken));
        return allOrders;
    }

    private async Task<IReadOnlyList<BybitOrder>> GetOrdersByCategoryAsync(
        BybitSettings settings,
        string category,
        string? settleCoin,
        CancellationToken cancellationToken)
    {
        var orders = new List<BybitOrder>();
        string? cursor = null;

        while (true)
        {
            var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["category"] = category,
                ["limit"] = PageSize.ToString(CultureInfo.InvariantCulture),
                ["settleCoin"] = settleCoin,
                ["cursor"] = cursor
            };

            var queryString = BuildQueryString(parameters);
            var payload = await SendSignedRequestAsync(
                HttpMethod.Get,
                RequestPath,
                settings,
                queryString,
                cancellationToken: cancellationToken);

            using var document = JsonDocument.Parse(payload);
            ThrowIfRetCodeError(document.RootElement);
            if (!document.RootElement.TryGetProperty("result", out var resultElement))
            {
                return orders;
            }

            if (resultElement.TryGetProperty("list", out var listElement) && listElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in listElement.EnumerateArray())
                {
                    if (!TryReadString(entry, "symbol", out var symbol))
                    {
                        continue;
                    }

                    if (!TryReadString(entry, "orderStatus", out var orderStatus) || !IsOpenOrderStatus(orderStatus))
                    {
                        continue;
                    }

                    TryReadString(entry, "orderId", out var orderId);
                    TryReadString(entry, "side", out var side);
                    TryReadString(entry, "orderType", out var orderType);
                    var qty = ReadDecimal(entry, "qty");
                    if (qty == 0)
                    {
                        qty = ReadDecimal(entry, "leavesQty");
                    }

                    var price = ReadNullableDecimal(entry, "price")
                        ?? ReadNullableDecimal(entry, "avgPrice")
                        ?? ReadNullableDecimal(entry, "triggerPrice");

                    orders.Add(new BybitOrder(orderId, symbol, side, category, orderType, orderStatus, qty, price));
                }
            }

            cursor = GetNextCursor(resultElement);
            if (string.IsNullOrWhiteSpace(cursor) || string.Equals(cursor, "0", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return orders;
    }

    private static bool IsOpenOrderStatus(string status)
    {
        return status.Equals("New", StringComparison.OrdinalIgnoreCase)
            || status.Equals("PartiallyFilled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Untriggered", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetNextCursor(JsonElement resultElement)
    {
        if (resultElement.TryGetProperty("nextPageCursor", out var cursorElement)
            && cursorElement.ValueKind == JsonValueKind.String)
        {
            return cursorElement.GetString();
        }

        return null;
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? string.Empty,
            _ => property.GetRawText().Trim()
        };

        return !string.IsNullOrWhiteSpace(value);
    }

    private static decimal ReadDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0m;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var numeric))
        {
            return numeric;
        }

        var raw = property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();

        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    private static decimal? ReadNullableDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var numeric))
        {
            return numeric;
        }

        var raw = property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();

        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}

public sealed record BybitOrder(
    string OrderId,
    string Symbol,
    string Side,
    string Category,
    string OrderType,
    string OrderStatus,
    decimal Qty,
    decimal? Price);
