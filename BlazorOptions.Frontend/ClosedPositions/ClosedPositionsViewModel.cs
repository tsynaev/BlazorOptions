using System.Collections.ObjectModel;

namespace BlazorOptions.ViewModels;

public sealed class ClosedPositionsViewModel : Bindable
{
    private readonly PositionViewModel _positionViewModel;
    private ObservableCollection<ClosedPositionViewModel> _closedPositions;
    private ClosedModel _model;
    private string? _baseAsset;

    public ClosedPositionsViewModel(PositionViewModel positionViewModel)
    {
        _positionViewModel = positionViewModel;
        _closedPositions = new ObservableCollection<ClosedPositionViewModel>();
        _model = new ClosedModel();
    }

    public ObservableCollection<ClosedPositionViewModel> ClosedPositions
    {
        get => _closedPositions;
        private set => _closedPositions = value;
    }

    public string? BaseAsset
    {
        get => _baseAsset;
        set => _baseAsset = value;
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

        _positionViewModel.NotifyStateChanged();
    }

    public ClosedModel Model
    {
        get => _model;
        set
        {
            if (!SetField(ref _model, value))
            {
                return;
            }

            ObservableCollection<ClosedPositionViewModel> positions = new();
            foreach (ClosedPositionModel model in _model.Positions)
            {
                positions.Add(CreatePositionViewModel(model));
            }

            ClosedPositions = positions;
        }
    }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task OnAddSymbolKeyDown(string key, bool ctrlKey, bool metaKey)
    {
        if (string.Equals(key, "Enter", StringComparison.Ordinal) && (ctrlKey || metaKey))
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

        var added = false;
        foreach (var symbol in symbols)
        {
            if (existing.Contains(symbol))
            {
                continue;
            }

            var model = new ClosedPositionModel { Symbol = symbol.ToUpperInvariant() };
            Model.Positions.Add(model);
            ClosedPositions.Add(CreatePositionViewModel(model));
            existing.Add(symbol);
            added = true;
        }

        AddSymbolInput = string.Empty;
        if (added)
        {
            await RaiseUpdateCompleted();
        }
    }

    public bool HasSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var normalized = symbol.Trim();
        return ClosedPositions.Any(item =>
            string.Equals(item.Model.Symbol, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<int> RemoveSymbolAsync(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return 0;
        }

        var normalized = symbol.Trim();
        var matches = ClosedPositions
            .Where(item => string.Equals(item.Model.Symbol, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            return 0;
        }

        foreach (var viewModel in matches)
        {
            viewModel.UpdateCompleted -= OnPositionUpdated;
            viewModel.Removed -= OnPositionRemoved;
            ClosedPositions.Remove(viewModel);
            Model.Positions.Remove(viewModel.Model);
        }

        await RaiseUpdateCompleted();
        return matches.Count;
    }

    public async Task<bool> UpdateSymbolAsync(ClosedPositionViewModel viewModel, string? symbol)
    {
        var normalized = symbol?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return false;
        }

        if (ClosedPositions.Any(item =>
                !ReferenceEquals(item, viewModel) &&
                string.Equals(item.Model.Symbol, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        await viewModel.SetSymbolAsync(normalized);
        return true;
    }

    public async Task SetIncludeAsync(bool include)
    {
        if (Model.Include == include)
        {
            return;
        }

        Model.Include = include;
        await _positionViewModel.QueuePersistPositionsAsync(_positionViewModel.Position);
        _positionViewModel.QueueChartUpdate();
    }

    public DateTime? ResolveEffectiveSinceDateLocal(DateTime? sinceDate)
    {
        if (sinceDate.HasValue)
        {
            return sinceDate.Value;
        }

        var creationTimeUtc = _positionViewModel.Position?.CreationTimeUtc;
        if (!creationTimeUtc.HasValue)
        {
            return null;
        }

        // Dialogs and date pickers work with local values; persisted creation time stays UTC on the model.
        return creationTimeUtc.Value.ToLocalTime();
    }

    internal ClosedPositionViewModel CreatePositionViewModel(ClosedPositionModel model)
    {
        var closedPosition = new ClosedPositionViewModel
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
        await RaiseUpdateCompleted();
    }

    private Task OnPositionUpdated()
    {
        return RaiseUpdateCompleted();
    }

    private async Task RaiseUpdateCompleted()
    {
        if (UpdatedCompleted is not null)
        {
            await UpdatedCompleted.Invoke();
        }
    }

    private static IReadOnlyList<string> SplitSymbols(string input)
    {
        return input
            .Split(new[] { '\r', '\n', '\t', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
