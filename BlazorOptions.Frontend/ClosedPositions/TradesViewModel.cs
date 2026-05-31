using BlazorOptions.API.TradingHistory;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class TradesViewModel : Bindable
{
    private static readonly TimeSpan RealtimeReloadDelay = TimeSpan.FromMilliseconds(250);
    private readonly ClosedPositionsViewModel _closedPositionsViewModel;
    private readonly ITransactionHistoryService _transactionHistory;
    private readonly object _realtimeReloadLock = new();
    private IReadOnlyList<PositionTradeSummaryRow> _trades = Array.Empty<PositionTradeSummaryRow>();
    private IDisposable? _realtimeSubscription;
    private CancellationTokenSource? _realtimeReloadCts;
    private bool _isInitialized;
    private bool _isLoading;
    private decimal _totalClosePnl;
    private decimal _totalFee;

    public TradesViewModel(
        ClosedPositionsViewModel closedPositionsViewModel,
        ITransactionHistoryService transactionHistory)
    {
        _closedPositionsViewModel = closedPositionsViewModel;
        _transactionHistory = transactionHistory;
    }

    public IReadOnlyList<PositionTradeSummaryRow> Trades
    {
        get => _trades;
        private set => SetField(ref _trades, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    public decimal TotalClosePnl
    {
        get => _totalClosePnl;
        private set
        {
            if (SetField(ref _totalClosePnl, value))
            {
                OnPropertyChanged(nameof(TotalNet));
            }
        }
    }

    public decimal TotalFee
    {
        get => _totalFee;
        private set
        {
            if (SetField(ref _totalFee, value))
            {
                OnPropertyChanged(nameof(TotalNet));
            }
        }
    }

    public decimal TotalNet => TotalClosePnl - TotalFee;

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        await _transactionHistory.InitializeAsync();
        _realtimeSubscription = await _transactionHistory.SubscribeExecutionsAsync(
            HandleRealtimeExecutionsAsync);
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        IsLoading = true;
        try
        {
            var requests = new List<TradingHistoryRequest>();
            var sinceDateBySymbol = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            foreach (var closedPosition in _closedPositionsViewModel.ClosedPositions)
            {
                var symbol = closedPosition.Model.Symbol?.Trim();
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    continue;
                }

                var sinceDateLocal = _closedPositionsViewModel.ResolveEffectiveSinceDateLocal(closedPosition.Model.SinceDate);
                var sinceDateUtc = sinceDateLocal.HasValue
                    ? DateTime.SpecifyKind(sinceDateLocal.Value, DateTimeKind.Local).ToUniversalTime()
                    : (DateTime?)null;
                var normalizedSymbol = symbol.ToUpperInvariant();
                requests.Add(new TradingHistoryRequest
                {
                    Symbol = normalizedSymbol,
                    Category = null,
                    SinceDateUtc = sinceDateUtc
                });
                sinceDateBySymbol[normalizedSymbol] = closedPosition.Model.SinceDate;
            }

            if (requests.Count == 0)
            {
                Trades = Array.Empty<PositionTradeSummaryRow>();
                TotalClosePnl = 0m;
                TotalFee = 0m;
                return;
            }

            var entries = await _transactionHistory.LoadBySymbolsAsync(requests.ToArray());
            var summaries = TradeSummaryBuilder.BuildTradeSummaries(entries);
            var rows = summaries.Select(summary =>
            {
                sinceDateBySymbol.TryGetValue(summary.Symbol, out var sinceDate);
                return new PositionTradeSummaryRow
                {
                    Key = $"{summary.Symbol}|{summary.EntryStartTimestamp}|{summary.EntryEndTimestamp}|{summary.CloseStartTimestamp}|{summary.CloseEndTimestamp}|{summary.Direction}",
                    Symbol = summary.Symbol,
                    SinceDate = sinceDate,
                    Summary = summary
                };
            })
            .OrderByDescending(item => item.Summary.EntryStartTimestamp)
            .ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();

            Trades = rows;
            var totalFee = rows.Sum(item => item.Summary.Fee);
            var totalNet = rows.Sum(item => item.Summary.Pnl ?? 0m);
            TotalFee = totalFee;
            TotalClosePnl = totalNet + totalFee;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Dispose()
    {
        _realtimeSubscription?.Dispose();
        _realtimeSubscription = null;

        lock (_realtimeReloadLock)
        {
            _realtimeReloadCts?.Cancel();
            _realtimeReloadCts?.Dispose();
            _realtimeReloadCts = null;
        }
    }

    private Task HandleRealtimeExecutionsAsync(IReadOnlyList<TradingHistoryEntry> executions)
    {
        if (executions.Count == 0 || !HasTrackedExecution(executions))
        {
            return Task.CompletedTask;
        }

        return QueueRealtimeReloadAsync();
    }

    private bool HasTrackedExecution(IReadOnlyList<TradingHistoryEntry> executions)
    {
        var trackedSymbols = _closedPositionsViewModel.ClosedPositions
            .Select(position => position.Model.Symbol?.Trim())
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(symbol => symbol!.ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (trackedSymbols.Count == 0)
        {
            return false;
        }

        foreach (var execution in executions)
        {
            if (trackedSymbols.Contains(execution.Symbol?.Trim()?.ToUpperInvariant() ?? string.Empty))
            {
                return true;
            }
        }

        return false;
    }

    private async Task QueueRealtimeReloadAsync()
    {
        CancellationToken token;
        lock (_realtimeReloadLock)
        {
            _realtimeReloadCts?.Cancel();
            _realtimeReloadCts?.Dispose();
            _realtimeReloadCts = new CancellationTokenSource();
            token = _realtimeReloadCts.Token;
        }

        try
        {
            // Burst fills arrive together; reloading once keeps the UI responsive.
            await Task.Delay(RealtimeReloadDelay, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }
}
