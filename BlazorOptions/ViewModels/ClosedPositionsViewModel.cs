using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.AspNetCore.Components.Web;

namespace BlazorOptions.ViewModels;

public sealed class ClosedPositionsViewModel
{
    private readonly PositionBuilderViewModel _positionBuilder;
    private readonly PositionModel _position;
    private IReadOnlyList<TradingHistoryEntry> _entries = Array.Empty<TradingHistoryEntry>();
    private bool _isInitialized;
    private IReadOnlyList<ClosedPositionSummary> _summaries = Array.Empty<ClosedPositionSummary>();

    public ClosedPositionsViewModel(
        PositionBuilderViewModel positionBuilder,
        PositionModel position)
    {
        _positionBuilder = positionBuilder;
        _position = position;
    }

    public ObservableCollection<ClosedPositionModel> ClosedPositions => _position.ClosedPositions;

    public IReadOnlyList<ClosedPositionSummary> Summaries => _summaries;

    public string AddSymbolInput { get; set; } = string.Empty;

    public bool HasActivePosition => _positionBuilder.SelectedPosition?.Id == _position.Id;

    public bool IncludeInChart
    {
        get => _position.IncludeClosedPositions;
        set => _ = SetIncludeInChartAsync(value);
    }

    public double TotalClosePnl => _summaries.Sum(item => item.ClosePnl);

    public double TotalFee => _summaries.Sum(item => item.Fee);

    public double TotalNet => TotalClosePnl + TotalFee;

    public ClosedPositionSummary GetSummary(ClosedPositionModel? model)
    {
        if (model is null)
        {
            return new ClosedPositionSummary(string.Empty, null, 0, 0, 0, 0, 0);
        }

        var summary = _summaries.FirstOrDefault(item =>
            string.Equals(item.Symbol, model.Symbol, StringComparison.OrdinalIgnoreCase));

        return summary ?? new ClosedPositionSummary(model.Symbol, model.SinceDate?.Date, 0, 0, 0, 0, 0);
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _entries = await _positionBuilder.RefreshTradingHistoryAsync(ClosedPositions);
        _isInitialized = true;
        RefreshSummaries();
        NotifyChartRefresh();
    }

    public async Task OnAddSymbolKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            await AddSymbolAsync();
        }
    }

    public async Task AddSymbolAsync()
    {
        if (!EnsureActivePosition())
        {
            return;
        }

        var symbol = (AddSymbolInput ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        if (ClosedPositions.Any(item => string.Equals(item.Symbol, symbol, StringComparison.OrdinalIgnoreCase)))
        {
            AddSymbolInput = string.Empty;
            return;
        }

        var closedPosition = new ClosedPositionModel
        {
            Symbol = symbol.ToUpperInvariant()
        };

        ClosedPositions.Add(closedPosition);
        AddSymbolInput = string.Empty;
        await PersistAndRefreshAsync();
    }

    public async Task RemoveClosedPositionAsync(ClosedPositionModel closedPosition)
    {
        if (!EnsureActivePosition())
        {
            return;
        }

        if (ClosedPositions.Contains(closedPosition))
        {
            ClosedPositions.Remove(closedPosition);
            await PersistAndRefreshAsync();
        }
    }

    public async Task SetSinceDateAsync(ClosedPositionModel closedPosition, DateTime? sinceDate)
    {
        if (!EnsureActivePosition())
        {
            return;
        }

        var normalized = sinceDate?.Date;
        if (closedPosition.SinceDate?.Date == normalized)
        {
            return;
        }

        closedPosition.SinceDate = normalized;
        await PersistAndRefreshAsync();
    }

    public Task SetIncludeInChartAsync(bool include)
    {
        if (!EnsureActivePosition())
        {
            return Task.CompletedTask;
        }

        _position.IncludeClosedPositions = include;
        return PersistAndRefreshAsync();
    }

    private bool EnsureActivePosition()
    {
        return _positionBuilder.SelectedPosition?.Id == _position.Id;
    }

    private async Task PersistAndRefreshAsync()
    {
        await _positionBuilder.PersistPositionsAsync(_position);
        _entries = await _positionBuilder.RefreshTradingHistoryAsync(ClosedPositions);
        RefreshSummaries();
        NotifyChartRefresh();
    }

    private void RefreshSummaries()
    {
        _summaries = ClosedPositionCalculator.BuildSummaries(ClosedPositions, _entries);
        _position.ClosedPositionsNetTotal = TotalNet;
    }

    private void NotifyChartRefresh()
    {
        if (_positionBuilder.SelectedPosition?.Id == _position.Id)
        {
            _positionBuilder.UpdateChart();
            _positionBuilder.NotifyStateChanged();
        }
    }
}


