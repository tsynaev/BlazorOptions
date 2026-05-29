using System.ComponentModel;

namespace BlazorOptions.ViewModels;

public sealed class ClosedPositionViewModel : Bindable
{
    public Func<ClosedPositionViewModel, Task>? Removed;
    public Func<Task>? UpdateCompleted;

    private ClosedPositionModel _model;

    public ClosedPositionViewModel()
    {
        _model = new ClosedPositionModel();
    }

    public ClosedPositionModel Model
    {
        get => _model;
        init
        {
            if (Equals(value, _model))
            {
                return;
            }

            value.PropertyChanged += ModelPropertyChanged;
            _model = value;
            OnPropertyChanged();
        }
    }

    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Model));
    }

    public async Task SetSymbolAsync(string? symbol)
    {
        var normalized = symbol?.Trim().ToUpperInvariant() ?? string.Empty;
        if (string.Equals(Model.Symbol, normalized, StringComparison.Ordinal))
        {
            return;
        }

        Model.Symbol = normalized;
        Model.ResetCalculationCache();
        await RaiseUpdateCompleted();
    }

    public async Task SetSinceDateAsync(DateTime? sinceDate)
    {
        if (Model.SinceDate == sinceDate)
        {
            return;
        }

        Model.SinceDate = sinceDate;
        await RaiseUpdateCompleted();
    }

    public Task SetSinceTimeAsync(TimeSpan? timePart)
    {
        var datePart = Model.SinceDate?.Date;
        if (!datePart.HasValue && timePart.HasValue)
        {
            datePart = DateTime.Today;
        }

        var combined = datePart.HasValue ? datePart.Value.Date + (timePart ?? TimeSpan.Zero) : (DateTime?)null;
        return SetSinceDateAsync(combined);
    }

    public async Task SetSinceDateTimeAsync(DateTime? sinceDate, TimeSpan? timePart)
    {
        var datePart = sinceDate?.Date;
        if (!datePart.HasValue && timePart.HasValue)
        {
            datePart = DateTime.Today;
        }

        var combined = datePart.HasValue ? datePart.Value + (timePart ?? TimeSpan.Zero) : (DateTime?)null;
        await SetSinceDateAsync(combined);
    }

    public async Task RemoveClosedPositionAsync()
    {
        if (Removed is not null)
        {
            await Removed.Invoke(this);
        }
    }

    private async Task RaiseUpdateCompleted()
    {
        if (UpdateCompleted is not null)
        {
            await UpdateCompleted.Invoke();
        }
    }
}
