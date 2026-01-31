using BlazorOptions.ViewModels;
using Microsoft.JSInterop;
using System.Text.Json.Serialization;

namespace BlazorOptions.Services;

public class TradingHistoryStorageService
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ITelemetryService _telemetryService;
    private bool _isInitialized;

    private const string DatabaseName = "blazor-options-db";
    private const int DatabaseVersion = 4;
    private const string TradesStore = "trades";
    private const string MetaStore = "tradeMeta";
    private const string DailyStore = "dailySummaries";
    private const string MetaKey = "state";
    private const long MaxTimestampKey = 9007199254740991L;

    private const string IndexTimestamp = "timestamp";
    private const string IndexTimestampUnique = "timestamp_unique";
    private const string IndexSymbol = "symbol";
    private const string IndexSymbolTimestamp = "symbol_timestamp";
    private const string IndexSymbolCategoryTimestamp = "symbol_category_timestamp";

    public TradingHistoryStorageService(IJSRuntime jsRuntime, ITelemetryService telemetryService)
    {
        _jsRuntime = jsRuntime;
        _telemetryService = telemetryService;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        try
        {
            await _jsRuntime.InvokeVoidAsync("indexedDbFramework.openDb", BuildConfig());
        }
        catch (JSException)
        {
            await EnsureFrameworkLoadedAsync();
            await _jsRuntime.InvokeVoidAsync("indexedDbFramework.openDb", BuildConfig());
        }
        _isInitialized = true;
    }

    public async Task<TradingHistoryMeta> LoadMetaAsync()
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadMetaAsync");
        await InitializeAsync();
        var record = await _jsRuntime.InvokeAsync<TradingMetaRecord?>("indexedDbFramework.getById", MetaStore, MetaKey);
        return record?.Value ?? new TradingHistoryMeta();
    }

    public async Task SaveMetaAsync(TradingHistoryMeta meta)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.SaveMetaAsync");
        await InitializeAsync();
        var record = new TradingMetaRecord
        {
            Key = MetaKey,
            Value = meta
        };
        await _jsRuntime.InvokeVoidAsync("indexedDbFramework.put", MetaStore, record);
    }

    public async Task SaveDailySummariesAsync(IEnumerable<TradingDailySummary> summaries)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.SaveDailySummariesAsync");
        await InitializeAsync();
        var payload = summaries.ToArray();
        if (payload.Length == 0)
        {
            return;
        }

        await _jsRuntime.InvokeVoidAsync("indexedDbFramework.clear", DailyStore);
        await _jsRuntime.InvokeVoidAsync("indexedDbFramework.putMany", DailyStore, payload);
    }

    public async Task<int> GetCountAsync()
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.GetCountAsync");
        await InitializeAsync();
        return await _jsRuntime.InvokeAsync<int>("indexedDbFramework.count", TradesStore);
    }

    public async Task SaveTradesAsync(IEnumerable<TradingHistoryEntry> entries)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.SaveTradesAsync");
        await InitializeAsync();
        var payload = entries.Select(PrepareEntryForStorage).ToArray();
        if (payload.Length == 0)
        {
            return;
        }

        await _jsRuntime.InvokeVoidAsync("indexedDbFramework.putMany", TradesStore, payload);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadLatestAsync(int limit)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadLatestAsync");
        await InitializeAsync();
        return await FetchByIndexRangeAsync(IndexTimestampUnique, null, "prev", limit);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadAnyAsync(int limit)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadAnyAsync");
        await InitializeAsync();
        return await FetchByIndexRangeAsync(IndexTimestampUnique, null, "prev", limit);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBeforeAsync(long? beforeTimestamp, string? beforeKey, int limit)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadBeforeAsync");
        await InitializeAsync();
        IndexedDbRange? range = null;
        if (beforeTimestamp.HasValue && !string.IsNullOrWhiteSpace(beforeKey))
        {
            range = new IndexedDbRange
            {
                Upper = new object[] { beforeTimestamp.Value, beforeKey },
                UpperOpen = true
            };
        }

        return await FetchByIndexRangeAsync(IndexTimestampUnique, range, "prev", limit);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBySymbolAsync(string symbol, string? category = null)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadBySymbolAsync");
        await InitializeAsync();
        var symbolValue = NormalizeSymbol(symbol);
        var categoryValue = NormalizeCategory(category);
        return await FetchSymbolRangeAsync(symbolValue, categoryValue, null, "prev", null);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBySymbolSinceAsync(string symbol, long? sinceTimestamp, string? category = null)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadBySymbolSinceAsync");
        await InitializeAsync();
        var symbolValue = NormalizeSymbol(symbol);
        var categoryValue = NormalizeCategory(category);
        var since = sinceTimestamp ?? 0;
        return await FetchSymbolRangeAsync(symbolValue, categoryValue, since, "prev", null);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBySymbolSummaryAsync(string symbol, string? category = null)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadBySymbolSummaryAsync");
        await InitializeAsync();
        var symbolValue = NormalizeSymbol(symbol);
        var categoryValue = NormalizeCategory(category);
        var records = await FetchSymbolRangeAsync(symbolValue, categoryValue, null, "prev", null);
        return records.Select(CreateSummaryEntry).ToList();
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBySymbolSummarySinceAsync(string symbol, long? sinceTimestamp, string? category = null)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadBySymbolSummarySinceAsync");
        await InitializeAsync();
        var symbolValue = NormalizeSymbol(symbol);
        var categoryValue = NormalizeCategory(category);
        var since = sinceTimestamp ?? 0;
        var records = await FetchSymbolRangeAsync(symbolValue, categoryValue, since, "prev", null);
        return records.Select(CreateSummaryEntry).ToList();
    }

    public async Task<TradingHistoryLatestInfo> LoadLatestBySymbolMetaAsync(string symbol, string? category = null)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadLatestBySymbolMetaAsync");
        await InitializeAsync();
        var symbolValue = NormalizeSymbol(symbol);
        var categoryValue = NormalizeCategory(category);

        var range = CreateSymbolRange(symbolValue, categoryValue, null);
        var result = await _jsRuntime.InvokeAsync<IndexedDbLatestMeta>("indexedDbFramework.getLatestMetaByIndexRange",
            TradesStore,
            ResolveSymbolIndex(categoryValue),
            range,
            "prev");

        return new TradingHistoryLatestInfo
        {
            Timestamp = result.Timestamp,
            Ids = result.Ids ?? new List<string>()
        };
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadAllAscAsync()
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadAllAscAsync");
        await InitializeAsync();
        return await FetchByIndexRangeAsync(IndexTimestampUnique, null, "next", null);
    }

    private async Task<List<TradingHistoryEntry>> FetchByIndexRangeAsync(string indexName, IndexedDbRange? range, string direction, int? limit)
    {
        var records = await _jsRuntime.InvokeAsync<TradingHistoryEntry[]>("indexedDbFramework.getByIndexRangeCursor",
            TradesStore,
            indexName,
            range,
            direction,
            limit);
        return records?.ToList() ?? new List<TradingHistoryEntry>();
    }

    private async Task<List<TradingHistoryEntry>> FetchSymbolRangeAsync(string symbolValue, string? categoryValue, long? sinceTimestamp, string direction, int? limit)
    {
        var range = CreateSymbolRange(symbolValue, categoryValue, sinceTimestamp);
        var indexName = ResolveSymbolIndex(categoryValue);
        var records = await _jsRuntime.InvokeAsync<TradingHistoryEntry[]>("indexedDbFramework.getByIndexRangeCursor",
            TradesStore,
            indexName,
            range,
            direction,
            limit);
        return records?.ToList() ?? new List<TradingHistoryEntry>();
    }

    private static string ResolveSymbolIndex(string? categoryValue)
    {
        return string.IsNullOrWhiteSpace(categoryValue) ? IndexSymbolTimestamp : IndexSymbolCategoryTimestamp;
    }

    private static IndexedDbRange CreateSymbolRange(string symbolValue, string? categoryValue, long? sinceTimestamp)
    {
        var hasSince = sinceTimestamp.HasValue && sinceTimestamp.Value > 0;
        if (string.IsNullOrWhiteSpace(categoryValue))
        {
            if (hasSince)
            {
                return new IndexedDbRange
                {
                    Lower = new object[] { symbolValue, sinceTimestamp!.Value },
                    Upper = new object[] { symbolValue, MaxTimestampKey }
                };
            }

            return new IndexedDbRange
            {
                Lower = new object[] { symbolValue, 0L },
                Upper = new object[] { symbolValue, MaxTimestampKey }
            };
        }

        if (hasSince)
        {
            return new IndexedDbRange
            {
                Lower = new object[] { symbolValue, categoryValue, sinceTimestamp!.Value },
                Upper = new object[] { symbolValue, categoryValue, MaxTimestampKey }
            };
        }

        return new IndexedDbRange
        {
            Lower = new object[] { symbolValue, categoryValue, 0L },
            Upper = new object[] { symbolValue, categoryValue, MaxTimestampKey }
        };
    }

    private static TradingHistoryEntry CreateSummaryEntry(TradingHistoryEntry entry)
    {
        return new TradingHistoryEntry
        {
            Id = entry.Id,
            Timestamp = entry.Timestamp,
            Symbol = entry.Symbol,
            Category = entry.Category,
            TransactionType = entry.TransactionType,
            Side = entry.Side,
            Size = entry.Size,
            Price = entry.Price,
            Fee = entry.Fee,
            Currency = entry.Currency
        };
    }

    private static IndexedDbConfig BuildConfig()
    {
        return new IndexedDbConfig
        {
            Name = DatabaseName,
            Version = DatabaseVersion,
            Stores = new List<IndexedDbStore>
            {
                new()
                {
                    Name = TradesStore,
                    KeyPath = "id",
                    AutoIncrement = false,
                    Indexes = new List<IndexedDbIndex>
                    {
                        new() { Name = IndexTimestamp, KeyPath = "timestamp", Unique = false },
                        new() { Name = IndexTimestampUnique, KeyPath = new object[] { "timestamp", "id" }, Unique = false },
                        new() { Name = IndexSymbol, KeyPath = "symbol", Unique = false },
                        new() { Name = IndexSymbolTimestamp, KeyPath = new object[] { "symbol", "timestamp" }, Unique = false },
                        new() { Name = IndexSymbolCategoryTimestamp, KeyPath = new object[] { "symbol", "category", "timestamp" }, Unique = false }
                    }
                },
                new()
                {
                    Name = MetaStore,
                    KeyPath = "key",
                    AutoIncrement = false,
                    Indexes = new List<IndexedDbIndex>()
                },
                new()
                {
                    Name = DailyStore,
                    KeyPath = "key",
                    AutoIncrement = false,
                    Indexes = new List<IndexedDbIndex>
                    {
                        new() { Name = "day", KeyPath = "day", Unique = false },
                        new() { Name = "symbol_day", KeyPath = new object[] { "symbol", "day" }, Unique = false }
                    }
                }
            }
        };
    }

    private static string NormalizeSymbol(string? symbol)
    {
        return symbol ?? string.Empty;
    }

    private static string? NormalizeCategory(string? category)
    {
        return category;
    }

    private static TradingHistoryEntry PrepareEntryForStorage(TradingHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            entry.Id = Guid.NewGuid().ToString("N");
        }

        if (!entry.Timestamp.HasValue || entry.Timestamp.Value <= 0)
        {
            entry.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        entry.Symbol = NormalizeSymbol(entry.Symbol);
        entry.Category = NormalizeCategory(entry.Category) ?? string.Empty;

        return entry;
    }

    private async Task EnsureFrameworkLoadedAsync()
    {
        const string ensureScript = """
            window.__ensureIndexedDbFramework = window.__ensureIndexedDbFramework || (function () {
                return function () {
                    if (window.indexedDbFramework) {
                        return true;
                    }
                    return new Promise(function (resolve, reject) {
                        var head = document.head || document.getElementsByTagName('head')[0];
                        var frameworkScript = document.querySelector('script[data-indexeddb-framework]');

                        function loadFramework() {
                            if (window.indexedDbFramework) {
                                resolve(true);
                                return;
                            }
                            if (!frameworkScript) {
                                frameworkScript = document.createElement('script');
                                frameworkScript.src = 'js/indexeddb-framework.js';
                                frameworkScript.async = false;
                                frameworkScript.setAttribute('data-indexeddb-framework', 'true');
                                frameworkScript.onload = function () { resolve(true); };
                                frameworkScript.onerror = function () { reject('Failed to load indexeddb-framework.js'); };
                                head.appendChild(frameworkScript);
                            } else if (!frameworkScript.dataset.loaded) {
                                frameworkScript.onload = function () { resolve(true); };
                                frameworkScript.onerror = function () { reject('Failed to load indexeddb-framework.js'); };
                            } else {
                                resolve(true);
                            }
                        }

                        loadFramework();
                    });
                };
            })();
            """;

        await _jsRuntime.InvokeVoidAsync("eval", ensureScript);
        await _jsRuntime.InvokeAsync<bool>("__ensureIndexedDbFramework");
    }
}

public sealed class TradingHistoryLatestInfo
{
    public long? Timestamp { get; set; }

    public List<string> Ids { get; set; } = new();
}

public sealed class TradingMetaRecord
{
    public string Key { get; set; } = string.Empty;

    public TradingHistoryMeta Value { get; set; } = new();
}

public sealed class IndexedDbConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("stores")]
    public List<IndexedDbStore> Stores { get; set; } = new();
}

public sealed class IndexedDbStore
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("keyPath")]
    public string? KeyPath { get; set; }

    [JsonPropertyName("autoIncrement")]
    public bool AutoIncrement { get; set; }

    [JsonPropertyName("indexes")]
    public List<IndexedDbIndex> Indexes { get; set; } = new();
}

public sealed class IndexedDbIndex
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("keyPath")]
    public object? KeyPath { get; set; }

    [JsonPropertyName("unique")]
    public bool Unique { get; set; }

    [JsonPropertyName("multiEntry")]
    public bool MultiEntry { get; set; }
}

public sealed class IndexedDbRange
{
    [JsonPropertyName("lower")]
    public object? Lower { get; set; }

    [JsonPropertyName("upper")]
    public object? Upper { get; set; }

    [JsonPropertyName("lowerOpen")]
    public bool LowerOpen { get; set; }

    [JsonPropertyName("upperOpen")]
    public bool UpperOpen { get; set; }
}

public sealed class IndexedDbLatestMeta
{
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }

    [JsonPropertyName("ids")]
    public List<string>? Ids { get; set; }
}
