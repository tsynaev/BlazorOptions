using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using BlazorOptions.Services;
using Microsoft.AspNetCore.Components.Web;

namespace BlazorOptions.ViewModels;

public sealed class ClosedPositionsViewModel
{
    private readonly PositionBuilderViewModel _positionBuilder;
    private readonly TradingHistoryStorageService _storageService;
    private readonly PositionModel _position;
    private IReadOnlyList<TradingHistoryEntry> _entries = Array.Empty<TradingHistoryEntry>();
    private bool _isInitialized;
    private IReadOnlyList<ClosedPositionSummary> _summaries = Array.Empty<ClosedPositionSummary>();

    public ClosedPositionsViewModel(
        PositionBuilderViewModel positionBuilder,
        TradingHistoryStorageService storageService,
        PositionModel position)
    {
        _positionBuilder = positionBuilder;
        _storageService = storageService;
        _position = position;
    }

    public ObservableCollection<ClosedPositionModel> ClosedPositions => _position.ClosedPositions;

    public Guid PositionId => _position.Id;

    public IReadOnlyList<ClosedPositionSummary> Summaries => _summaries;

    public string AddSymbolInput { get; set; } = string.Empty;

    public void SetAddSymbolInput(IEnumerable<string> symbols)
    {
        var existing = SplitSymbols(AddSymbolInput ?? string.Empty);
        var combined = new List<string>(existing.Count + 8);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in existing)
        {
            if (seen.Add(symbol))
            {
                combined.Add(symbol);
            }
        }

        if (symbols is not null)
        {
            foreach (var symbol in symbols)
            {
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    continue;
                }

                var trimmed = symbol.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (seen.Add(trimmed))
                {
                    combined.Add(trimmed);
                }
            }
        }

        AddSymbolInput = combined.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, combined);

        _positionBuilder.NotifyStateChanged();
    }

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

        return summary ?? new ClosedPositionSummary(model.Symbol, model.SinceDate, 0, 0, 0, 0, 0);
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
        if (args.Key == "Enter" && (args.CtrlKey || args.MetaKey))
        {
            await AddSymbolAsync();
        }
    }

    public async Task AddSymbolAsync()
    {

        var input = AddSymbolInput ?? string.Empty;
        var symbols = SplitSymbols(input);
        if (symbols.Count == 0)
        {
            return;
        }

        var existing = new HashSet<string>(
            ClosedPositions.Select(item => item.Symbol),
            StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var symbol in symbols)
        {
            if (existing.Contains(symbol))
            {
                continue;
            }

            var closedPosition = new ClosedPositionModel
            {
                Symbol = symbol.ToUpperInvariant()
            };

            ClosedPositions.Add(closedPosition);
            existing.Add(symbol);
            added++;
        }

        AddSymbolInput = string.Empty;
        if (added > 0)
        {
            await PersistAndRefreshAsync();
        }
    }

    public async Task RemoveClosedPositionAsync(ClosedPositionModel closedPosition)
    {

        if (ClosedPositions.Contains(closedPosition))
        {
            ClosedPositions.Remove(closedPosition);
            await PersistAndRefreshAsync();
        }
    }

    public async Task SetSinceDateAsync(ClosedPositionModel closedPosition, DateTime? sinceDate)
    {

        if (closedPosition.SinceDate == sinceDate)
        {
            return;
        }

        closedPosition.SinceDate = sinceDate;
        await PersistAndRefreshAsync();
    }

    public Task SetSinceDatePartAsync(ClosedPositionModel closedPosition, DateTime? datePart)
    {

        var timePart = closedPosition.SinceDate?.TimeOfDay ?? TimeSpan.Zero;
        var combined = datePart.HasValue ? datePart.Value.Date + timePart : (DateTime?)null;
        return SetSinceDateAsync(closedPosition, combined);
    }

    public Task SetSinceTimePartAsync(ClosedPositionModel closedPosition, TimeSpan? timePart)
    {

        var datePart = closedPosition.SinceDate?.Date;
        if (!datePart.HasValue && timePart.HasValue)
        {
            datePart = DateTime.Today;
        }

        var combined = datePart.HasValue ? datePart.Value.Date + (timePart ?? TimeSpan.Zero) : (DateTime?)null;
        return SetSinceDateAsync(closedPosition, combined);
    }

    public Task SetIncludeInChartAsync(bool include)
    {
        _position.IncludeClosedPositions = include;
        return PersistAndRefreshAsync();
    }


    public async Task<IReadOnlyList<TradingHistoryEntry>> GetTradesForSymbolAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return Array.Empty<TradingHistoryEntry>();
        }

        var entries = await _storageService.LoadBySymbolAsync(symbol);
        return TradingHistoryViewModel.RecalculateForSymbol(entries);
    }

    public async Task<string> GetRawJsonForSymbolAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return string.Empty;
        }

        var entries = await _storageService.LoadBySymbolAsync(symbol);
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var payload = new List<JsonElement>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.RawJson))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(entry.RawJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    payload.Add(item.Clone());
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                payload.Add(root.Clone());
            }
        }

        if (payload.Count == 0)
        {
            return string.Empty;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(payload, options);
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
        _positionBuilder.UpdateChart();
        _positionBuilder.NotifyStateChanged();
    }

    private static IReadOnlyList<string> SplitSymbols(string input)
    {
        var tokens = input
            .Split(new[] { '\r', '\n', '\t', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return tokens;
    }
}


