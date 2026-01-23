using BlazorOptions.ViewModels;
using System.Text.Json;
using Microsoft.JSInterop;

namespace BlazorOptions.Services;

public class TradingHistoryStorageService
{
    private readonly IJSRuntime _jsRuntime;
    private bool _isInitialized;

    public TradingHistoryStorageService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        try
        {
            await _jsRuntime.InvokeVoidAsync("tradingHistoryDb.init");
        }
        catch (JSException)
        {
            await EnsureScriptLoadedAsync();
            await _jsRuntime.InvokeVoidAsync("tradingHistoryDb.init");
        }
        _isInitialized = true;
    }

    public async Task<TradingHistoryMeta> LoadMetaAsync()
    {
        await InitializeAsync();
        var element = await _jsRuntime.InvokeAsync<JsonElement>("tradingHistoryDb.getMeta");
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            return new TradingHistoryMeta();
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (!element.TryGetProperty("value", out var valueElement))
            {
                element.TryGetProperty("Value", out valueElement);
            }

            if (valueElement.ValueKind != JsonValueKind.Undefined)
            {
                return DeserializeMetaElement(valueElement);
            }
        }

        return DeserializeMetaElement(element);
    }

    private static TradingHistoryMeta DeserializeMetaElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var payload = element.GetString();
            if (!string.IsNullOrWhiteSpace(payload))
            {
                return JsonSerializer.Deserialize<TradingHistoryMeta>(payload, CreateSerializerOptions()) ?? new TradingHistoryMeta();
            }
        }

        return element.Deserialize<TradingHistoryMeta>(CreateSerializerOptions()) ?? new TradingHistoryMeta();
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task SaveMetaAsync(TradingHistoryMeta meta)
    {
        await InitializeAsync();
        await _jsRuntime.InvokeVoidAsync("tradingHistoryDb.setMeta", meta);
    }

    public async Task SaveDailySummariesAsync(IEnumerable<TradingDailySummary> summaries)
    {
        await InitializeAsync();
        var payload = summaries.ToArray();
        if (payload.Length == 0)
        {
            return;
        }

        await _jsRuntime.InvokeAsync<int>("tradingHistoryDb.replaceDailySummaries", (object)payload);
    }

    public async Task<int> GetCountAsync()
    {
        await InitializeAsync();
        return await _jsRuntime.InvokeAsync<int>("tradingHistoryDb.getCount");
    }

    public async Task SaveTradesAsync(IEnumerable<TradingHistoryEntry> entries)
    {
        await InitializeAsync();
        var payload = entries.ToArray();
        if (payload.Length == 0)
        {
            return;
        }

        var written = await _jsRuntime.InvokeAsync<int>("tradingHistoryDb.putTrades", (object)payload);
        if (written == 0)
        {
            var jsError = await _jsRuntime.InvokeAsync<string?>("tradingHistoryDb.getLastError");
            Console.WriteLine($"TradingHistoryStorageService: No trades written (payload {payload.Length}). JS error: {jsError ?? "none"}");
        }
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadLatestAsync(int limit)
    {
        await InitializeAsync();
        return await _jsRuntime.InvokeAsync<TradingHistoryEntry[]>("tradingHistoryDb.fetchLatest", limit);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadAnyAsync(int limit)
    {
        await InitializeAsync();
        return await _jsRuntime.InvokeAsync<TradingHistoryEntry[]>("tradingHistoryDb.fetchAny", limit);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBeforeAsync(long? beforeTimestamp, string? beforeKey, int limit)
    {
        await InitializeAsync();
        return await _jsRuntime.InvokeAsync<TradingHistoryEntry[]>("tradingHistoryDb.fetchBefore", beforeTimestamp, beforeKey, limit);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBySymbolAsync(string symbol, string? category = null)
    {
        await InitializeAsync();
        var symbolKey = NormalizeSymbolKey(symbol);
        var categoryKey = NormalizeCategoryKey(category);
        return await _jsRuntime.InvokeAsync<TradingHistoryEntry[]>("tradingHistoryDb.fetchBySymbol", symbolKey, categoryKey);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadAllAscAsync()
    {
        await InitializeAsync();
        return await _jsRuntime.InvokeAsync<TradingHistoryEntry[]>("tradingHistoryDb.fetchAllAsc");
    }
 
    private static string NormalizeSymbolKey(string? symbol)
    {
        return string.IsNullOrWhiteSpace(symbol)
            ? string.Empty
            : symbol.Trim().ToUpperInvariant();
    }

    private static string? NormalizeCategoryKey(string? category)
    {
        return string.IsNullOrWhiteSpace(category)
            ? null
            : category.Trim().ToLowerInvariant();
    }

    private async Task EnsureScriptLoadedAsync()
    {
        const string ensureScript = """
            window.__ensureTradingHistoryDb = window.__ensureTradingHistoryDb || (function () {
                return function () {
                    if (window.tradingHistoryDb) {
                        return true;
                    }
                    return new Promise(function (resolve, reject) {
                        var script = document.createElement('script');
                        script.src = 'js/trading-history-db.js?v=3';
                        script.async = false;
                        script.onload = function () { resolve(true); };
                        script.onerror = function () { reject('Failed to load trading-history-db.js'); };
                        document.head.appendChild(script);
                    });
                };
            })();
            """;

        await _jsRuntime.InvokeVoidAsync("eval", ensureScript);
        await _jsRuntime.InvokeAsync<bool>("__ensureTradingHistoryDb");
    }

 
}

