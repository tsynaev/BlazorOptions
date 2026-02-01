using BlazorOptions.ViewModels;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlazorOptions.Services;

public abstract class BybitApiService
{
    private const string BaseUrl = "https://api.bybit.com";
    private const string RecvWindow = "5000";
    private readonly HttpClient _httpClient;

    protected BybitApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    protected static string BuildQueryString(IDictionary<string, string?> parameters)
    {
        return string.Join("&", parameters
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value!)}"));
    }

    protected async Task<string> SendSignedRequestAsync(
        HttpMethod method,
        string path,
        BybitSettings settings,
        string? queryString = null,
        string? body = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.ApiSecret))
        {
            throw new InvalidOperationException("Bybit API key and secret are required.");
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var payload = method == HttpMethod.Get ? (queryString ?? string.Empty) : (body ?? string.Empty);
        var signature = Sign($"{timestamp}{settings.ApiKey}{RecvWindow}{payload}", settings.ApiSecret);

        var uri = string.IsNullOrWhiteSpace(queryString)
            ? $"{BaseUrl}{path}"
            : $"{BaseUrl}{path}?{queryString}";

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-BAPI-API-KEY", settings.ApiKey);
        request.Headers.Add("X-BAPI-SIGN", signature);
        request.Headers.Add("X-BAPI-TIMESTAMP", timestamp);
        request.Headers.Add("X-BAPI-RECV-WINDOW", RecvWindow);
        request.Headers.Add("X-BAPI-SIGN-TYPE", "2");

        if (method != HttpMethod.Get)
        {
            request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payloadText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Bybit request failed ({(int)response.StatusCode}): {payloadText}");
        }

        return payloadText;
    }

    protected static void ThrowIfRetCodeError(JsonElement rootElement)
    {
        if (!rootElement.TryGetProperty("retCode", out var retCodeElement))
        {
            return;
        }

        if (retCodeElement.TryGetInt32(out var retCode) && retCode == 0)
        {
            return;
        }

        if (retCodeElement.ValueKind == JsonValueKind.String
            && int.TryParse(retCodeElement.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            && parsed == 0)
        {
            return;
        }

        var message = rootElement.TryGetProperty("retMsg", out var retMsgElement)
            ? retMsgElement.GetString()
            : "Bybit returned an error.";

        var finalCode = retCodeElement.ValueKind == JsonValueKind.String
            ? retCodeElement.GetString()
            : retCodeElement.GetRawText();

        throw new InvalidOperationException($"Bybit error {finalCode}: {message}");
    }

    private static string Sign(string preSign, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(preSign));
        var builder = new StringBuilder(hash.Length * 2);

        foreach (var value in hash)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
