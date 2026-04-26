using System.Globalization;
using BlazorOptions.API.TradingHistory;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class TradingHistoryViewModel
{
    private const string SelectedExchangeStorageKey = "tradingHistory:selectedExchangeConnectionId";
    private const int PageLimit = 100;
    private static readonly TimeSpan WindowSize = TimeSpan.FromDays(7);
    private const string UnauthorizedMessage = "Sign in to view trading history.";
    private readonly IExchangeServiceFactory _exchangeServiceFactory;
    private readonly ITradingHistoryPort _tradingHistoryPort;
    private readonly ExchangeConnectionsService _exchangeConnectionsService;
    private readonly ILocalStorageService _localStorageService;
    private IExchangeService? _exchangeServiceHandle;
    private string? _exchangeServiceConnectionId;
    private TradingHistoryMeta _meta = new();
    private IReadOnlyList<TradingSummaryRow>? _summaryBySymbolCache;
    private IReadOnlyList<TradingPnlByCoinRow>? _pnlByCoinCache;
    private DailyPnlChartOptions? _dailyPnlChartCache;
    private bool _isInitialized;
    private bool _isBackgroundInitialized;
    private bool _isLoadingSummary;
    private bool _isLoadingDailyChart;
    private int _totalCount;
    private string? _selectedExchangeConnectionId;

    public TradingHistoryViewModel(
        IExchangeServiceFactory exchangeServiceFactory,
        ITradingHistoryPort tradingHistoryPort,
        ExchangeConnectionsService exchangeConnectionsService,
        ILocalStorageService localStorageService)
    {
        _exchangeServiceFactory = exchangeServiceFactory;
        _tradingHistoryPort = tradingHistoryPort;
        _exchangeConnectionsService = exchangeConnectionsService;
        _localStorageService = localStorageService;
    }

    public event Action? OnChange;

    public int TotalTransactionsCount => _totalCount;

    public IReadOnlyList<string> Categories { get; } = new[] { "linear", "inverse", "spot", "option" };

    public IReadOnlyList<ExchangeConnectionModel> ExchangeConnections { get; private set; } = Array.Empty<ExchangeConnectionModel>();

    public string SelectedExchangeConnectionId => _selectedExchangeConnectionId ?? "bybit-main";

    public DateTime? RegistrationDate { get; private set; }

    public bool IsRegistrationDateRequired => !_meta.RegistrationTimeMs.HasValue;

    public bool IsLoading { get; private set; }

    public bool IsLoadingOlder { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset? LastLoadedAt { get; private set; }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        try
        {
            await InitializeExchangeSelectionAsync();
            await LoadExchangeStateAsync();
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = ResolveErrorMessage(ex);
            OnChange?.Invoke();
        }
    }

    public async Task InitializeForBackgroundAsync()
    {
        if (_isInitialized || _isBackgroundInitialized)
        {
            return;
        }

        _isBackgroundInitialized = true;
        try
        {
            await InitializeExchangeSelectionAsync();
            await LoadExchangeStateAsync();
            _ = StartBackgroundLoadIfPossibleAsync();
        }
        catch
        {
        }
    }

    public async Task LoadLatestAsync()
    {
        if (IsLoading || IsLoadingOlder)
        {
            return;
        }

        ErrorMessage = null;
        IsLoading = true;
        OnChange?.Invoke();

        try
        {
            var registrationTime = _meta.RegistrationTimeMs;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var category in Categories)
            {
                long startTime = 0;

                if (!_meta.LatestSyncedTimeMsByCategory.TryGetValue(category, out startTime))
                {
                    if (registrationTime.HasValue)
                    {
                        startTime = registrationTime.Value;
                    }
                    else
                    {
                        ErrorMessage = "Select your registration date before loading transactions.";
                        return;
                    }
                }

                var lastStartTime = startTime;
                var forwardCursor = startTime;
                var categoryHadTrades = false;

                while (forwardCursor < nowMs)
                {
                    var endTime = Math.Min(forwardCursor + (long)WindowSize.TotalMilliseconds, nowMs);
                    var cursor = (string?)null;

                    while (true)
                    {
                        var query = new ExchangeTransactionQuery
                        {
                            Category = category,
                            Limit = PageLimit,
                            Cursor = cursor,
                            StartTime = forwardCursor,
                            EndTime = endTime
                        };

                        var page = await ExchangeService.TransactionHistory.GetTransactionsPageAsync(query, CancellationToken.None);
                        if (page.Items.Count > 0)
                        {
                            categoryHadTrades = true;

                            foreach (var item in page.Items)
                            {
                                if (item.Timestamp.HasValue && item.Timestamp.Value > lastStartTime)
                                {
                                    lastStartTime = item.Timestamp.Value + 1;
                                }
                            }

                            var entries = page.Items.Select(MapRecordToEntry).ToList();
                            var orderedAsc = TradingHistoryEntryOrdering.OrderAscending(entries).ToList();

                            if (orderedAsc.Count > 0)
                            {
                                await _tradingHistoryPort.SaveTradesAsync(orderedAsc, SelectedExchangeConnectionId);
                            }
                        }

                        if (string.IsNullOrWhiteSpace(page.NextCursor))
                        {
                            break;
                        }

                        cursor = page.NextCursor;
                    }

                    if (!categoryHadTrades)
                    {
                        lastStartTime = forwardCursor;
                    }

                    forwardCursor = endTime + 1;
                    _meta.LatestSyncedTimeMsByCategory[category] = lastStartTime;
                }
            }

            await _tradingHistoryPort.SaveMetaAsync(_meta, SelectedExchangeConnectionId);
            _meta = await _tradingHistoryPort.LoadMetaAsync(SelectedExchangeConnectionId);

            InvalidateSummaryCache();
            _ = EnsureSummaryCacheAsync();
            LastLoadedAt = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            ErrorMessage = ResolveErrorMessage(ex);
        }
        finally
        {
            IsLoading = false;
            OnChange?.Invoke();
        }
    }

    public async Task SetRegistrationDateAsync(DateTime? date)
    {
        RegistrationDate = date?.Date;
        _meta.RegistrationTimeMs = date.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(date.Value.Date, DateTimeKind.Local)).ToUnixTimeMilliseconds()
            : null;

        await _tradingHistoryPort.SaveMetaAsync(_meta, SelectedExchangeConnectionId);
        OnChange?.Invoke();
    }

    public async Task SetSelectedExchangeConnectionAsync(string? exchangeConnectionId)
    {
        var normalizedConnectionId = _exchangeConnectionsService.GetConnectionOrDefault(exchangeConnectionId).Id;
        if (string.Equals(SelectedExchangeConnectionId, normalizedConnectionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectedExchangeConnectionId = normalizedConnectionId;
        await _localStorageService.SetItemAsync(SelectedExchangeStorageKey, normalizedConnectionId);
        ErrorMessage = null;
        LastLoadedAt = null;
        _totalCount = 0;
        await LoadExchangeStateAsync();
        OnChange?.Invoke();
    }

    public Task RecalculateAsync()
    {
        return RecalculateAsync(null);
    }

    public async Task RecalculateAsync(DateTime? fromDate)
    {
        if (_isLoadingSummary)
        {
            return;
        }

        _isLoadingSummary = true;
        try
        {
            var startTimestamp = fromDate.HasValue
                ? GetStartTimestamp(fromDate.Value)
                : (long?)null;

            await _tradingHistoryPort.RecalculateAsync(startTimestamp, SelectedExchangeConnectionId);
            _meta = await _tradingHistoryPort.LoadMetaAsync(SelectedExchangeConnectionId);

            UpdateRegistrationDate();
            InvalidateSummaryCache();
            _ = EnsureSummaryCacheAsync();
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = ResolveErrorMessage(ex);
            OnChange?.Invoke();
        }
        finally
        {
            _isLoadingSummary = false;
        }
    }

    public async Task<TradingHistoryResult> LoadEntriesAsync(string? baseAsset, int startIndex, int count)
    {
        try
        {
            var result = await _tradingHistoryPort.LoadEntriesAsync(baseAsset, startIndex, count, SelectedExchangeConnectionId);
            _totalCount = result.TotalEntries;
            return result;
        }
        catch (Exception ex)
        {
            ErrorMessage = ResolveErrorMessage(ex);
            OnChange?.Invoke();
        }

        return new TradingHistoryResult();
    }

    public IReadOnlyList<TradingSummaryRow> GetSummaryBySymbol()
    {
        if (_summaryBySymbolCache is not null)
        {
            return _summaryBySymbolCache;
        }

        _ = EnsureSummaryCacheAsync();
        return Array.Empty<TradingSummaryRow>();
    }

    public IReadOnlyList<TradingPnlByCoinRow> GetRealizedPnlBySettleCoin()
    {
        if (_pnlByCoinCache is not null)
        {
            return _pnlByCoinCache;
        }

        _ = EnsureSummaryCacheAsync();
        return Array.Empty<TradingPnlByCoinRow>();
    }

    public DailyPnlChartOptions? GetDailyRealizedPnlChart()
    {
        if (_dailyPnlChartCache is not null)
        {
            return _dailyPnlChartCache;
        }

        _ = EnsureDailyPnlChartAsync();
        return null;
    }

    private async Task EnsureSummaryCacheAsync()
    {
        if (_summaryBySymbolCache is not null || _pnlByCoinCache is not null || _isLoadingSummary)
        {
            return;
        }

        _isLoadingSummary = true;
        try
        {
            var summaryTask = _tradingHistoryPort.LoadSummaryBySymbolAsync(SelectedExchangeConnectionId);
            var pnlTask = _tradingHistoryPort.LoadPnlBySettleCoinAsync(SelectedExchangeConnectionId);
            await Task.WhenAll(summaryTask, pnlTask);

            _summaryBySymbolCache = MapSummaryRows(summaryTask.Result);
            _pnlByCoinCache = MapPnlRows(pnlTask.Result);
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = ResolveErrorMessage(ex);
            OnChange?.Invoke();
        }
        finally
        {
            _isLoadingSummary = false;
        }
    }

    private async Task EnsureDailyPnlChartAsync()
    {
        if (_dailyPnlChartCache is not null || _isLoadingDailyChart)
        {
            return;
        }

        _isLoadingDailyChart = true;
        try
        {
            var utcNow = DateTimeOffset.UtcNow;
            var startDate = new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day, 0, 0, 0, TimeSpan.Zero).AddDays(-29);
            var fromTimestamp = startDate.ToUnixTimeMilliseconds();
            var toTimestamp = utcNow.ToUnixTimeMilliseconds();

            var rows = await _tradingHistoryPort.LoadDailyPnlAsync(fromTimestamp, toTimestamp, SelectedExchangeConnectionId);
            _dailyPnlChartCache = BuildDailyPnlChart(rows, startDate, 30);
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = ResolveErrorMessage(ex);
            OnChange?.Invoke();
        }
        finally
        {
            _isLoadingDailyChart = false;
        }
    }

    private void UpdateRegistrationDate()
    {
        if (_meta.RegistrationTimeMs.HasValue)
        {
            RegistrationDate = DateTimeOffset.FromUnixTimeMilliseconds(_meta.RegistrationTimeMs.Value)
                .ToLocalTime()
                .Date;
        }
    }

    private async Task StartBackgroundLoadIfPossibleAsync()
    {
        if (_totalCount <= 0 || IsLoading || IsLoadingOlder)
        {
            return;
        }

        try
        {
            await LoadLatestAsync();
        }
        catch
        {
        }
    }

    private IExchangeService ExchangeService => SwitchExchangeService(SelectedExchangeConnectionId);

    private async Task InitializeExchangeSelectionAsync()
    {
        ExchangeConnections = _exchangeConnectionsService.GetConnections();
        var storedConnectionId = _localStorageService.GetItem(SelectedExchangeStorageKey);
        _selectedExchangeConnectionId = _exchangeConnectionsService.GetConnectionOrDefault(storedConnectionId).Id;
        await Task.CompletedTask;
    }

    private async Task LoadExchangeStateAsync()
    {
        _meta = await _tradingHistoryPort.LoadMetaAsync(SelectedExchangeConnectionId);
        UpdateRegistrationDate();
        InvalidateSummaryCache();
        _ = EnsureSummaryCacheAsync();
    }

    private IExchangeService SwitchExchangeService(string? exchangeConnectionId)
    {
        if (_exchangeServiceHandle is not null
            && string.Equals(_exchangeServiceConnectionId, exchangeConnectionId, StringComparison.OrdinalIgnoreCase))
        {
            return _exchangeServiceHandle;
        }

        var previousService = _exchangeServiceHandle;
        _exchangeServiceHandle = _exchangeServiceFactory.Create(exchangeConnectionId);
        _exchangeServiceConnectionId = exchangeConnectionId;
        previousService?.Dispose();
        return _exchangeServiceHandle;
    }

    private static bool IsRangeLoaded(int startIndex, int count, int loadedStartIndex, int loadedCount)
    {
        if (loadedCount == 0 || count <= 0)
        {
            return false;
        }

        return startIndex >= loadedStartIndex && startIndex + count <= loadedStartIndex + loadedCount;
    }


    private static int ClampTotal(long total)
    {
        if (total <= 0)
        {
            return 0;
        }

        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    private static string ResolveErrorMessage(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            return UnauthorizedMessage;
        }

        if (ex is ProblemDetailsException problem)
        {
            return problem.Details.Detail ?? problem.Details.Title ?? ex.Message;
        }

        return ex.Message;
    }

    private TradingHistoryEntry MapRecordToEntry(TradingTransactionRaw raw)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var size = raw.Qty ?? raw.Size ?? 0m;
        var price = raw.TradePrice ?? 0m;
        var fee = raw.Fee ?? 0m;
        var change = raw.Change ?? 0m;
        var cashFlow = raw.CashFlow ?? 0m;

        return new TradingHistoryEntry
        {
            Id = raw.UniqueKey,
            Timestamp = raw.Timestamp ?? 0,
            Symbol = raw.Symbol,
            Category = raw.Category,
            TransactionType = raw.TransactionType,
            Side = raw.Side,
            Size = size,
            Price = price,
            Fee = fee,
            Currency = raw.Currency,
            Change = change,
            CashFlow = cashFlow,
            OrderId = raw.OrderId,
            OrderLinkId = raw.OrderLinkId,
            TradeId = raw.TradeId,
            RawJson = raw.RawJson,
            ChangedAt = now,
            Calculated = new TradingTransactionCalculated()
        };
    }

    private IReadOnlyList<TradingSummaryRow> MapSummaryRows(
        IReadOnlyList<BlazorOptions.API.TradingHistory.TradingSummaryBySymbolRow> summary)
    {
        return summary
            .Select(row => new TradingSummaryRow
            {
                Category = row.Category,
                GroupKey = $"{row.Category}/{row.SettleCoin}".TrimEnd('/'),
                Symbol = row.Symbol,
                SettleCoin = row.SettleCoin,
                Trades = row.Trades,
                TotalQty = FormatDecimal(row.TotalQty),
                TotalValue = $"{FormatDecimal(row.TotalValue)} {row.SettleCoin}".Trim(),
                TotalFees = $"{FormatDecimal(row.TotalFees)} {row.SettleCoin}".Trim(),
                RealizedPnl = $"{FormatDecimal(row.RealizedPnl)} {row.SettleCoin}".Trim()
            })
            .OrderByDescending(row => row.Trades)
            .ThenBy(row => row.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<TradingPnlByCoinRow> MapPnlRows(
        IReadOnlyList<BlazorOptions.API.TradingHistory.TradingPnlByCoinRow> rows)
    {
        return rows
            .Select(row => new TradingPnlByCoinRow
            {
                SettleCoin = row.SettleCoin,
                RealizedPnl = $"{FormatDecimal(row.RealizedPnl)} {row.SettleCoin}".Trim(),
                Fees = $"{FormatDecimal(row.Fees)} {row.SettleCoin}".Trim(),
                NetPnl = $"{FormatDecimal(row.NetPnl)} {row.SettleCoin}".Trim()
            })
            .OrderBy(row => row.SettleCoin, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DailyPnlChartOptions? BuildDailyPnlChart(
        IReadOnlyList<TradingDailyPnlRow> rows,
        DateTimeOffset startDate,
        int dayCount)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var dayKeys = Enumerable.Range(0, dayCount)
            .Select(offset => startDate.AddDays(offset))
            .Select(day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToArray();
        var byDay = dayKeys.ToDictionary(
            key => key,
            _ => new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var coins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Day) || !byDay.TryGetValue(row.Day, out var totals))
            {
                continue;
            }

            var settleCoin = string.IsNullOrWhiteSpace(row.SettleCoin) ? "_unknown" : row.SettleCoin;
            var realized = row.RealizedPnl;

            totals.TryGetValue(settleCoin, out var current);
            totals[settleCoin] = current + realized;
            coins.Add(settleCoin);
        }

        if (coins.Count == 0)
        {
            return null;
        }

        var orderedCoins = coins.OrderBy(coin => coin, StringComparer.OrdinalIgnoreCase).ToArray();
        var series = new List<DailyPnlSeries>(orderedCoins.Length);
        var hasValue = false;
        var min = 0m;
        var max = 0m;

        for (var coinIndex = 0; coinIndex < orderedCoins.Length; coinIndex++)
        {
            var coin = orderedCoins[coinIndex];
            var values = new decimal[dayKeys.Length];

            for (var i = 0; i < dayKeys.Length; i++)
            {
                var amount = 0m;
                if (byDay.TryGetValue(dayKeys[i], out var totals) &&
                    totals.TryGetValue(coin, out var found))
                {
                    amount = found;
                }

                values[i] = amount;
                if (!hasValue)
                {
                    min = amount;
                    max = amount;
                    hasValue = true;
                }
                else
                {
                    min = Math.Min(min, amount);
                    max = Math.Max(max, amount);
                }
            }

            series.Add(new DailyPnlSeries(coin, values));
        }

        if (!hasValue)
        {
            return null;
        }

        min = Math.Min(min, 0m);
        max = Math.Max(max, 0m);

        return new DailyPnlChartOptions(dayKeys, series, min, max);
    }

    private void InvalidateSummaryCache()
    {
        _summaryBySymbolCache = null;
        _pnlByCoinCache = null;
        _dailyPnlChartCache = null;
    }

    private static long GetStartTimestamp(DateTime date)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Local);
        return new DateTimeOffset(local).ToUnixTimeMilliseconds();
    }

    private static string GetSettleCoin(string? currency)
    {
        var settleCoin = string.IsNullOrWhiteSpace(currency)
            ? "_unknown"
            : currency;

        return string.IsNullOrWhiteSpace(settleCoin) ? "_unknown" : settleCoin;
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.##########", CultureInfo.InvariantCulture);
    }
}
