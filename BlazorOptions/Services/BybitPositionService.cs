using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace BlazorOptions.Services;

public class BybitPositionService
{
    private const string ApiBaseUrl = "https://api.bybit.com";
    private const string RequestPath = "/v5/position/list";
    private readonly HttpClient _httpClient;
    private readonly ExchangeSettingsService _exchangeSettingsService;

    public BybitPositionService(HttpClient httpClient, ExchangeSettingsService exchangeSettingsService)
    {
        _httpClient = httpClient;
        _exchangeSettingsService = exchangeSettingsService;
    }

    public async Task<IReadOnlyList<BybitPosition>> GetPositionsAsync(string baseCoin, string category, string? settleCoin = null, CancellationToken cancellationToken = default)
    {
        var settings = await _exchangeSettingsService.LoadBybitSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.ApiSecret))
        {
            throw new InvalidOperationException("Bybit API key and secret are required to load positions.");
        }

        var queryParameters = BuildQueryParameters(baseCoin, category, settleCoin);
        var queryString = BuildQueryString(queryParameters);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var signature = CreateSignature(settings.ApiSecret, BuildPreSign(timestamp, settings.ApiKey, queryString));

        var requestUri = string.IsNullOrEmpty(queryString) ? $"{ApiBaseUrl}{RequestPath}" : $"{ApiBaseUrl}{RequestPath}?{queryString}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("X-BAPI-API-KEY", settings.ApiKey);
        request.Headers.Add("X-BAPI-TIMESTAMP", timestamp);
        request.Headers.Add("X-BAPI-SIGN-TYPE", "2");
        request.Headers.Add("X-BAPI-RECV-WINDOW", "5000");
        request.Headers.Add("X-BAPI-SIGN", signature);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

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

    private static string BuildQueryString(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        return string.Join("&", parameters
            .OrderBy(parameter => parameter.Key, StringComparer.Ordinal)
            .Select(parameter => $"{parameter.Key}={Uri.EscapeDataString(parameter.Value)}"));
    }

    private static string BuildPreSign(string timestamp, string apiKey, string queryString)
    {
        const int recvWindow = 5000;
        return timestamp + apiKey + recvWindow + queryString;
    }

    private static string CreateSignature(string secret, string payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(payloadBytes);

        var builder = new StringBuilder(hashBytes.Length * 2);
        foreach (var b in hashBytes)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static void ThrowIfRetCodeError(JsonElement rootElement)
    {
        if (!rootElement.TryGetProperty("retCode", out var retCodeElement))
        {
            return;
        }

        if (!TryReadInt(retCodeElement, out var retCode) || retCode == 0)
        {
            return;
        }

        var message = rootElement.TryGetProperty("retMsg", out var retMsgElement)
            ? retMsgElement.GetString()
            : null;

        throw new InvalidOperationException($"Bybit API error {retCode}: {message ?? "Unknown error"}");
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

    private static bool TryReadInt(JsonElement element, out int value)
    {
        value = 0;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = element.GetString();
        return int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }
}

public sealed record BybitPosition(
    string Symbol,
    string Side,
    string Category,
    double Size,
    double AvgPrice);
