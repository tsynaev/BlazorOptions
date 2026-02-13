using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using BlazorOptions.ViewModels;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public class BybitPositionService : BybitApiService
{
    private const string RequestPath = "/v5/position/list";
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;

    private const string DefaultSettleCoin = "USDT";
    private const int PageSize = 200;

    private List<string> _settleCoins = new() { DefaultSettleCoin };

    public BybitPositionService(HttpClient httpClient, IOptions<BybitSettings> bybitSettingsOptions)
        : base(httpClient)
    {
        _bybitSettingsOptions = bybitSettingsOptions;
    }

    public async Task<IReadOnlyList<ExchangePosition>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        var requests = new List<Task<IReadOnlyList<ExchangePosition>>>();
        foreach (var category in new[] { "linear", "inverse" })
        {
            foreach (var settleCoin in _settleCoins)
            {
                requests.Add(GetPositionsAsync(category, settleCoin, cancellationToken));
            }
        }

        requests.Add(GetPositionsAsync("option", null, cancellationToken));
        var batches = await Task.WhenAll(requests);
        return batches.SelectMany(batch => batch).ToList();
    }

    public async Task<IReadOnlyList<ExchangePosition>> GetPositionsAsync(string category, string? settleCoin, CancellationToken cancellationToken = default)
    {
        var settings = _bybitSettingsOptions.Value;
        var positions = new List<ExchangePosition>();
        string? cursor = null;

        while (true)
        {
            var queryParameters = BuildQueryParameters(category, settleCoin, cursor);
            var queryString = BuildQueryString(queryParameters.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal));
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
                return positions;
            }

            if (resultElement.TryGetProperty("list", out var listElement) && listElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in listElement.EnumerateArray())
                {
                    if (!TryReadString(entry, "symbol", out var symbol))
                    {
                        continue;
                    }

                    TryReadString(entry, "side", out var side);

                    var size = ReadDecimal(entry, "size");
                    var avgPrice = ReadDecimal(entry, "avgPrice");

                    positions.Add(new ExchangePosition(symbol, side, category, size, avgPrice));
                }
            }

            cursor = GetNextCursor(resultElement);
            if (string.IsNullOrWhiteSpace(cursor) || string.Equals(cursor, "0", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return positions;
    }

    private static List<KeyValuePair<string, string>> BuildQueryParameters(string category, string? settleCoin, string? cursor)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("category", category),
            new("limit", PageSize.ToString(CultureInfo.InvariantCulture))
        };

        if (!string.IsNullOrEmpty(settleCoin))
        {
            parameters.Add(new KeyValuePair<string, string>("settleCoin", settleCoin));
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            parameters.Add(new KeyValuePair<string, string>("cursor", cursor));
        }
        
        return parameters;
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
            return 0;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetDecimal(out var value):
                return value;
            case JsonValueKind.String:
                var raw = property.GetString();
                if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                break;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        var trimmed = property.GetRawText();
        return decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var fallback)
            ? fallback
            : 0;
    }

}

public sealed record ExchangePosition(
    string Symbol,
    string Side,
    string Category,
    decimal Size,
    decimal AvgPrice);
