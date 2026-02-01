using System.Collections.ObjectModel;
using BlazorOptions.API.TradingHistory;
using BlazorOptions.Services;
using Microsoft.AspNetCore.Components.Web;

namespace BlazorOptions.ViewModels;

public sealed class ClosedPositionsViewModel: Bindable
{

    private readonly PositionBuilderViewModel _positionBuilder;
    private readonly ITradingHistoryPort _tradingHistoryPort;
    private readonly ITelemetryService _telemetryService;
    private readonly IExchangeService _exchangeService;
    private bool _isInitialized;
    private ObservableCollection<ClosedPositionViewModel> _closedPositions;
    private decimal _totalClosePnl;
    private decimal _totalFee;
    private decimal _totalNet;
    private bool _includeInChart;
    private ClosedModel _model;

    public ClosedPositionsViewModel(
        PositionBuilderViewModel positionBuilder,
        ITradingHistoryPort tradingHistoryPort,
        ITelemetryService telemetryService,
        IExchangeService exchangeService)
    {
        _positionBuilder = positionBuilder;
        _tradingHistoryPort = tradingHistoryPort;
        _telemetryService = telemetryService;
        _exchangeService = exchangeService;
    }

    public ObservableCollection<ClosedPositionViewModel> ClosedPositions
    {
        get => _closedPositions;
        set => _closedPositions = value;
    }


    public event Func<Task>? UpdatedCompleted;


    public string AddSymbolInput { get; set; } = string.Empty;

    public void SetAddSymbolInput(IEnumerable<string> symbols)
    {
        var existing = SplitSymbols(AddSymbolInput);
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

 
    public ClosedModel Model
    {
        get => _model;
        set
        {
            if (SetField(ref _model, value))
            {
                ObservableCollection<ClosedPositionViewModel> positions = new();

                foreach (ClosedPositionModel model in _model.Positions)
                {
                    positions.Add(CreatePositionViewModel(model));
                }

                ClosedPositions = positions;
            }
        }
    }


    public Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return Task.CompletedTask;
        }

        _isInitialized = true;
        _ = RecalculateAllAsync(forceFull: false);
        return Task.CompletedTask;
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
            ClosedPositions.Select(item => item.Model.Symbol),
            StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var symbol in symbols)
        {
            if (existing.Contains(symbol))
            {
                continue;
            }

            var model = new ClosedPositionModel { Symbol = symbol.ToUpperInvariant() };
            Model.Positions.Add(model);

            ClosedPositionViewModel closedPosition = CreatePositionViewModel(model);
            

            ClosedPositions.Add(closedPosition);
        
            existing.Add(symbol);
            added++;
        }

        AddSymbolInput = string.Empty;
        if (added > 0)
        {
            foreach (var symbol in symbols)
            {
                var viewModel = ClosedPositions.FirstOrDefault(item =>
                    string.Equals(item.Model.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
                if (viewModel is not null)
                {
                    _ = viewModel.RecalculateAsync(forceFull: false);
                }
            }
        }

        await RaiseUpdateCompleted();
    }

    internal ClosedPositionViewModel CreatePositionViewModel(ClosedPositionModel model)
    {
        var closedPosition =
            new ClosedPositionViewModel(_tradingHistoryPort, _telemetryService, _exchangeService)
            {
                Model = model
            };

        closedPosition.UpdateCompleted += OnPositionUpdated;
        closedPosition.Removed += OnPositionRemoved;

        return closedPosition;
    }

    private async Task OnPositionRemoved(ClosedPositionViewModel viewModel)
    {
        viewModel.UpdateCompleted -= OnPositionUpdated;
        viewModel.Removed -= OnPositionRemoved;

        ClosedPositions.Remove(viewModel);
        Model.Positions.Remove(viewModel.Model);

        UpdateTotal();

        await RaiseUpdateCompleted();
    }


    private async Task RaiseUpdateCompleted()
    {
        if (UpdatedCompleted != null)
        {
            await UpdatedCompleted.Invoke();
        } 
    }
   

    private async Task OnPositionUpdated()
    {
        UpdateTotal();

        await RaiseUpdateCompleted();
    }

    private void UpdateTotal()
    {
        Model.TotalClosePnl = ClosedPositions.Sum(item => item.Model.Realized);
        Model.TotalFee = ClosedPositions.Sum(item => item.Model.FeeTotal); 
    }

    private async Task RecalculateAllAsync(bool forceFull)
    {
        foreach (var model in ClosedPositions)
        {
            await model.RecalculateAsync(forceFull);
            await Task.Yield();
        }
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


