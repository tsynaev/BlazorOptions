using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using System.Threading;

namespace BlazorOptions.Services;

public class BybitPositionService : BybitApiService
{
    private const string RequestPath = "/v5/position/list";
    private readonly ExchangeSettingsService _exchangeSettingsService;

    public BybitPositionService(HttpClient httpClient, ExchangeSettingsService exchangeSettingsService)
        : base(httpClient)
    {
        _exchangeSettingsService = exchangeSettingsService;
    }

    public async Task<IReadOnlyList<BybitPosition>> GetPositionsAsync(string baseCoin, string category, string? settleCoin = null, CancellationToken cancellationToken = default)
    {
        var settings = await _exchangeSettingsService.LoadBybitSettingsAsync();
        var queryParameters = BuildQueryParameters(baseCoin, category, settleCoin);
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
            return [];
        }

        if (!resultElement.TryGetProperty("list", out var listElement) || listElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var positions = new List<BybitPosition>();

        foreach (var entry in listElement.EnumerateArray())
        {
            if (!TryReadString(entry, "symbol", out var symbol))
            {
                continue;
            }

            TryReadString(entry, "side", out var side);

            var size = ReadDouble(entry, "size");
            var avgPrice = ReadDouble(entry, "avgPrice");

            positions.Add(new BybitPosition(symbol, side, category, size, avgPrice));
        }

        return positions;
    }

    private static List<KeyValuePair<string, string>> BuildQueryParameters(string baseCoin, string category, string? settleCoin)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("category", category)
        };

        if (!string.IsNullOrWhiteSpace(settleCoin))
        {
            parameters.Add(new("settleCoin", settleCoin.Trim().ToUpperInvariant()));
        }

        if (!string.IsNullOrWhiteSpace(baseCoin))
        {
            parameters.Add(new("baseCoin", baseCoin.Trim().ToUpperInvariant()));
        }

        return parameters;
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

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetDouble(out var value):
                return value;
            case JsonValueKind.String:
                var raw = property.GetString();
                if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
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
        return double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var fallback)
            ? fallback
            : 0;
    }

}

public sealed record BybitPosition(
    string Symbol,
    string Side,
    string Category,
    double Size,
    double AvgPrice);
