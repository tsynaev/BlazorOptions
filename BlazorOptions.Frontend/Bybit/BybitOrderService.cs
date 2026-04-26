using System.Globalization;
using System.Text.Json;
using BlazorOptions.ViewModels;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public sealed class BybitOrderService : BybitApiService, IOrdersService
{
    private const int PageSize = 50;
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;

    public BybitOrderService(HttpClient httpClient, IOptions<BybitSettings> bybitSettingsOptions)
        : base(httpClient)
    {
        _bybitSettingsOptions = bybitSettingsOptions;
    }

    public async Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        var settings = _bybitSettingsOptions.Value;
        var batches = new List<IReadOnlyList<ExchangeOrder>>();
        var defaultSettleCoin = string.IsNullOrWhiteSpace(settings.DefaultSettleCoin)
            ? "USDT"
            : settings.DefaultSettleCoin.Trim().ToUpperInvariant();
        foreach (var request in new[]
                 {
                     (Category: "linear", SettleCoin: defaultSettleCoin),
                     (Category: "inverse", SettleCoin: defaultSettleCoin),
                     (Category: "option", SettleCoin: (string?)null)
                 })
        {
            var batch = await TryGetOrdersByCategoryAsync(settings, request.Category, request.SettleCoin, cancellationToken);
            if (batch.Count > 0)
            {
                batches.Add(batch);
            }
        }

        return batches.SelectMany(batch => batch).ToList();
    }

    private async Task<IReadOnlyList<ExchangeOrder>> TryGetOrdersByCategoryAsync(
        BybitSettings settings,
        string category,
        string? settleCoin,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetOrdersByCategoryAsync(settings, category, settleCoin, cancellationToken);
        }
        catch
        {
            // Demo/main accounts can reject categories that are not enabled; keep the rest of the snapshot usable.
            return Array.Empty<ExchangeOrder>();
        }
    }

    public ValueTask<IDisposable> SubscribeAsync(
        Func<IReadOnlyList<ExchangeOrder>, Task> handler,
        CancellationToken cancellationToken = default)
    {
        // Snapshot-only service; realtime updates are provided by ActiveOrdersService.
        return new ValueTask<IDisposable>(new SubscriptionRegistration(() => { }));
    }

    private async Task<IReadOnlyList<ExchangeOrder>> GetOrdersByCategoryAsync(
        BybitSettings settings,
        string category,
        string? settleCoin,
        CancellationToken cancellationToken)
    {
        var orders = new List<ExchangeOrder>();
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
                settings.OrderRealtimeUri,
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
                    if (!entry.TryReadString("symbol", out var symbol))
                    {
                        continue;
                    }

                    if (!entry.TryReadString("orderStatus", out var orderStatus) || !IsOpenOrderStatus(orderStatus))
                    {
                        continue;
                    }

                    entry.TryReadString("orderId", out var orderId);
                    entry.TryReadString("side", out var side);
                    entry.TryReadString("orderType", out var orderType);
                    entry.TryReadString("stopOrderType", out var stopOrderType);
                    var qty = entry.ReadDecimal("qty");
                    if (qty == 0)
                    {
                        qty = entry.ReadDecimal("leavesQty");
                    }

                    var price = ResolveOrderPrice(entry);

                    orders.Add(new ExchangeOrder(orderId, symbol, side, category, orderType, orderStatus, qty, price, stopOrderType));
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

    private static decimal? ResolveOrderPrice(JsonElement element)
    {
        return FirstPositive(
            element.ReadNullableDecimal("price"),
            element.ReadNullableDecimal("avgPrice"),
            element.ReadNullableDecimal("triggerPrice"),
            element.ReadNullableDecimal("takeProfit"),
            element.ReadNullableDecimal("stopLoss"),
            element.ReadNullableDecimal("tpLimitPrice"),
            element.ReadNullableDecimal("slLimitPrice"));
    }

    private static decimal? FirstPositive(params decimal?[] values)
    {
        foreach (var value in values)
        {
            if (value.HasValue && value.Value > 0)
            {
                return value.Value;
            }
        }

        return null;
    }
}

public sealed record ExchangeOrder(
    string OrderId,
    string Symbol,
    string Side,
    string Category,
    string OrderType,
    string OrderStatus,
    decimal Qty,
    decimal? Price,
    string? StopOrderType);
