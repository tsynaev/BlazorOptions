using BlazorOptions.API.TradingHistory;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class TradingHistorySelectionDialogViewModel
{
    private const string UnauthorizedMessage = "Sign in to view trading history.";
    private readonly ITradingHistoryPort _tradingHistoryPort;
    private readonly HashSet<string> _closedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource<IReadOnlyList<string>?>? _selectionTcs;
    private bool _isLoading;
    private string? _errorMessage;
    private long _totalEntries;
    private string? _baseAssetFilter;
    private int _sourceStartIndex;
    private int _virtualStartIndex;
    private long _lastSourceTotal;
    private bool _isSourceExhausted;
    private const int FetchBatchSize = 200;

    public TradingHistorySelectionDialogViewModel(ITradingHistoryPort tradingHistoryPort)
    {
        _tradingHistoryPort = tradingHistoryPort;
    }

    public event Action? OnChange;

    public bool IsLoading => _isLoading;

    public string? ErrorMessage => _errorMessage;

    public long TotalEntries => _totalEntries;

    public IReadOnlyCollection<string> SelectedSymbols => _selectedSymbols;

    public Task InitializeAsync(IEnumerable<string> closedSymbols, string? baseAsset)
    {
        _closedSymbols.Clear();
        _selectedSymbols.Clear();
        _baseAssetFilter = string.IsNullOrWhiteSpace(baseAsset) ? null : baseAsset.Trim();
        _sourceStartIndex = 0;
        _virtualStartIndex = 0;
        _lastSourceTotal = 0;
        _isSourceExhausted = false;

        if (closedSymbols is not null)
        {
            foreach (var symbol in closedSymbols)
            {
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    continue;
                }

                var normalized = symbol.Trim();
                _closedSymbols.Add(normalized);
            }
        }

        _selectionTcs = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _errorMessage = null;
        OnChange?.Invoke();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>?> WaitForSelectionAsync()
    {
        return _selectionTcs?.Task ?? Task.FromResult<IReadOnlyList<string>?>(null);
    }

    public bool IsSymbolSelected(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        return _selectedSymbols.Contains(symbol.Trim());
    }

    public void SetSymbolSelected(string? symbol, bool isSelected)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        var normalized = symbol.Trim();
        if (_closedSymbols.Contains(normalized))
        {
            return;
        }

        var changed = isSelected
            ? _selectedSymbols.Add(normalized)
            : _selectedSymbols.Remove(normalized);

        if (changed)
        {
            OnChange?.Invoke();
        }
    }

    public void ToggleSymbolSelection(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        var normalized = symbol.Trim();
        if (_closedSymbols.Contains(normalized))
        {
            return;
        }

        if (_selectedSymbols.Contains(normalized))
        {
            _selectedSymbols.Remove(normalized);
        }
        else
        {
            _selectedSymbols.Add(normalized);
        }

        OnChange?.Invoke();
    }

    public void SelectClosedSymbols()
    {
        _selectedSymbols.Clear();
        OnChange?.Invoke();
    }

    public void ClearSelection()
    {
        if (_selectedSymbols.Count == 0)
        {
            return;
        }

        _selectedSymbols.Clear();
        OnChange?.Invoke();
    }

    public void ConfirmSelection()
    {
        var ordered = _selectedSymbols
            .OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _selectionTcs?.TrySetResult(ordered);
    }

    public void CancelSelection()
    {
        _selectionTcs?.TrySetResult(null);
    }

    public async Task<TradingHistoryResult> LoadEntriesAsync(int startIndex, int limit)
    {
        if (limit <= 0)
        {
            limit = 50;
        }

        _isLoading = true;
        try
        {
            if (startIndex < 0)
            {
                startIndex = 0;
            }

            if (startIndex != _virtualStartIndex)
            {
                ResetPaging();
            }

            var toSkip = startIndex - _virtualStartIndex;
            var collected = new List<TradingHistoryEntry>(limit);

            while (!_isSourceExhausted && (toSkip > 0 || collected.Count < limit))
            {
                var pageSize = Math.Max(FetchBatchSize, limit);
                var page = await _tradingHistoryPort.LoadEntriesAsync(_baseAssetFilter, _sourceStartIndex, pageSize);
                _lastSourceTotal = page.TotalEntries;

                if (page.Entries.Count == 0)
                {
                    _isSourceExhausted = true;
                    break;
                }

                _sourceStartIndex += page.Entries.Count;

                var filtered = ApplyClosedSymbolFilter(page.Entries);
                if (filtered.Count == 0)
                {
                    continue;
                }

                if (toSkip > 0)
                {
                    if (filtered.Count <= toSkip)
                    {
                        toSkip -= filtered.Count;
                        _virtualStartIndex += filtered.Count;
                        continue;
                    }

                    filtered = filtered.Skip(toSkip).ToList();
                    _virtualStartIndex += toSkip;
                    toSkip = 0;
                }

                var remaining = limit - collected.Count;
                if (remaining <= 0)
                {
                    break;
                }

                if (filtered.Count > remaining)
                {
                    collected.AddRange(filtered.Take(remaining));
                    _virtualStartIndex += remaining;
                }
                else
                {
                    collected.AddRange(filtered);
                    _virtualStartIndex += filtered.Count;
                }
            }

            _totalEntries = _isSourceExhausted ? _virtualStartIndex : _lastSourceTotal;
            _errorMessage = null;
            return new TradingHistoryResult
            {
                Entries = collected,
                TotalEntries = _totalEntries > int.MaxValue ? int.MaxValue : (int)_totalEntries
            };
        }
        catch (Exception ex)
        {
            _errorMessage = ResolveErrorMessage(ex);
            OnChange?.Invoke();
        }
        finally
        {
            _isLoading = false;
        }

        return new TradingHistoryResult();
    }

    private IReadOnlyList<TradingHistoryEntry> ApplyClosedSymbolFilter(IReadOnlyList<TradingHistoryEntry> entries)
    {
        if (_closedSymbols.Count == 0 || entries.Count == 0)
        {
            return entries;
        }

        return entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Symbol)
                            && !_closedSymbols.Contains(entry.Symbol.Trim()))
            .ToList();
    }

    private void ResetPaging()
    {
        _sourceStartIndex = 0;
        _virtualStartIndex = 0;
        _lastSourceTotal = 0;
        _isSourceExhausted = false;
    }

    private static string ResolveErrorMessage(Exception ex)
    {
        if (ex is ProblemDetailsException problem && problem.Details is not null)
        {
            if (problem.Details.Status == 401)
            {
                return UnauthorizedMessage;
            }

            if (!string.IsNullOrWhiteSpace(problem.Details.Detail))
            {
                return problem.Details.Detail;
            }
        }

        return ex.Message;
    }
}
