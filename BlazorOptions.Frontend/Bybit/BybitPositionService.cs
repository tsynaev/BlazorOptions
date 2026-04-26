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
        var batches = new List<IReadOnlyList<ExchangePosition>>();
        foreach (var category in new[] { "linear", "inverse" })
        {
            foreach (var settleCoin in _settleCoins)
            {
                var batch = await TryGetPositionsAsync(category, settleCoin, cancellationToken);
                if (batch.Count > 0)
                {
                    batches.Add(batch);
                }
            }
        }

        var optionsBatch = await TryGetPositionsAsync("option", null, cancellationToken);
        if (optionsBatch.Count > 0)
        {
            batches.Add(optionsBatch);
        }

        return batches.SelectMany(batch => batch).ToList();
    }

    private async Task<IReadOnlyList<ExchangePosition>> TryGetPositionsAsync(string category, string? settleCoin, CancellationToken cancellationToken)
    {
        try
        {
            return await GetPositionsAsync(category, settleCoin, cancellationToken);
        }
        catch
        {
            // Demo/main accounts can reject categories that are not enabled; keep the rest of the snapshot usable.
            return Array.Empty<ExchangePosition>();
        }
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
                settings.PositionListUri,
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
                    if (!entry.TryReadString("symbol", out var symbol))
                    {
                        continue;
                    }

                    entry.TryReadString("side", out var side);

                    var size = entry.ReadDecimal("size");
                    var avgPrice = entry.ReadDecimal("avgPrice");

                    var createdTimeUtc = ReadDateTimeUtc(entry, "createdTime");
                    positions.Add(new ExchangePosition(symbol, side, category, size, avgPrice, createdTimeUtc));
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

    private static DateTime? ReadDateTimeUtc(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        long timestampMs;
        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetInt64(out var asLong):
                timestampMs = asLong;
                break;
            case JsonValueKind.String:
                var raw = property.GetString();
                if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
                {
                    return null;
                }

                timestampMs = parsedLong;
                break;
            default:
                return null;
        }

        if (timestampMs <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime;
        }
        catch
        {
            return null;
        }
    }

}

public sealed record ExchangePosition(
    string Symbol,
    string Side,
    string Category,
    decimal Size,
    decimal AvgPrice,
    DateTime? CreatedTimeUtc = null);
