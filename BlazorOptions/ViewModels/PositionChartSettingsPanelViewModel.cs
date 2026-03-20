using BlazorOptions.API.Common;
using BlazorOptions.API.Positions;
using MudBlazor;

namespace BlazorOptions.ViewModels;

public sealed class PositionChartSettingsPanelViewModel : Bindable
{
    private readonly PositionViewModel _positionViewModel;
    private readonly Func<decimal?, Task> _selectedPriceChanged;
    private readonly Func<DateTime, Task> _valuationDateChanged;
    private readonly Func<Task> _toggleLive;
    private readonly Func<Task> _toggleCandles;
    private readonly Func<Task> _toggleDayMinMaxMarkers;
    private readonly Func<Task> _toggleOrderMarkers;
    private readonly Func<Task> _toggleSkewShift;

    public PositionChartSettingsPanelViewModel(
        PositionViewModel positionViewModel,
        Func<decimal?, Task> selectedPriceChanged,
        Func<DateTime, Task> valuationDateChanged,
        Func<Task> toggleLive,
        Func<Task> toggleCandles,
        Func<Task> toggleDayMinMaxMarkers,
        Func<Task> toggleOrderMarkers,
        Func<Task> toggleSkewShift)
    {
        _positionViewModel = positionViewModel;
        _selectedPriceChanged = selectedPriceChanged;
        _valuationDateChanged = valuationDateChanged;
        _toggleLive = toggleLive;
        _toggleCandles = toggleCandles;
        _toggleDayMinMaxMarkers = toggleDayMinMaxMarkers;
        _toggleOrderMarkers = toggleOrderMarkers;
        _toggleSkewShift = toggleSkewShift;
    }

    public string TotalPnlText => $"Total P/L: {FormatPrice(ResolveTotalCombinedPnl())} ({FormatPercent(ResolveTotalCombinedPnlPercent())})";

    public Color TotalPnlColor => ResolvePnlColor(ResolveTotalCombinedPnl());

    public bool IsLive => _positionViewModel.IsLive;

    public bool ShowCandles => _positionViewModel.ShowCandles;

    public bool ShowDayMinMaxMarkers => _positionViewModel.ShowDayMinMaxMarkers;

    public bool ShowOrderMarkers => _positionViewModel.ShowOrderMarkers;

    public bool ShowSkewShift => _positionViewModel.ShowSkewShift;

    public decimal? SelectedPrice => _positionViewModel.SelectedPrice;

    public DateTime MinDateTimeUtc => DateTime.UtcNow;

    public DateTime MaxDateTimeUtc
    {
        get
        {
            var now = DateTime.UtcNow;
            var max = _positionViewModel.MaxExpiryDate.Date.AddDays(1).AddTicks(-1);
            return max > now ? max : now;
        }
    }

    public DateTime SelectedDateTimeUtc => _positionViewModel.ValuationDate;

    public IReadOnlyList<DateTime> ExpirationDatesUtc => _positionViewModel.Collections
        .SelectMany(collection => collection.Legs)
        .Where(leg => leg.Leg.IsIncluded && leg.Leg.ExpirationDate.HasValue)
        .Select(leg => DateTime.SpecifyKind(leg.Leg.ExpirationDate!.Value, DateTimeKind.Utc))
        .Distinct()
        .OrderBy(date => date)
        .ToArray();

    public Task ToggleLiveAsync() => _toggleLive();

    public Task ToggleCandlesAsync() => _toggleCandles();

    public Task ToggleDayMinMaxMarkersAsync() => _toggleDayMinMaxMarkers();

    public Task ToggleOrderMarkersAsync() => _toggleOrderMarkers();

    public Task ToggleSkewShiftAsync() => _toggleSkewShift();

    public Task SetSelectedPriceAsync(decimal? price) => _selectedPriceChanged(price);

    public Task SetValuationDateAsync(DateTime dateTimeUtc) => _valuationDateChanged(dateTimeUtc);

    private decimal? ResolveTotalTempPnl()
    {
        decimal total = 0m;
        var hasValue = false;
        foreach (var collection in _positionViewModel.Collections)
        {
            if (!collection.Collection.IsVisible)
            {
                continue;
            }

            var value = collection.TotalTempPnl;
            if (!value.HasValue)
            {
                continue;
            }

            total += value.Value;
            hasValue = true;
        }

        return hasValue ? total : null;
    }

    private decimal? ResolveClosedNetPnl()
    {
        var closed = _positionViewModel.Position.Closed;
        if (closed is null || !closed.Include)
        {
            return null;
        }

        return closed.TotalNet;
    }

    private decimal? ResolveTotalCombinedPnl()
    {
        var temp = ResolveTotalTempPnl();
        var closed = ResolveClosedNetPnl();

        if (!temp.HasValue && !closed.HasValue)
        {
            return null;
        }

        return (temp ?? 0m) + (closed ?? 0m);
    }

    private decimal ResolvePortfolioEntryValue()
    {
        decimal total = 0m;
        foreach (var collection in _positionViewModel.Collections)
        {
            if (!collection.Collection.IsVisible)
            {
                continue;
            }

            foreach (var leg in collection.Legs)
            {
                if (!leg.Leg.IsIncluded || leg.Leg.Type == LegType.Future || !leg.Leg.Price.HasValue)
                {
                    continue;
                }

                total += Math.Abs(leg.Leg.Size * leg.Leg.Price.Value);
            }
        }

        return total;
    }

    private decimal? ResolveTotalCombinedPnlPercent()
    {
        var totalPnl = ResolveTotalCombinedPnl();
        if (!totalPnl.HasValue)
        {
            return null;
        }

        var entryValue = ResolvePortfolioEntryValue();
        if (entryValue <= 0m)
        {
            return null;
        }

        return totalPnl.Value / entryValue * 100m;
    }

    private static string FormatPrice(decimal? price)
    {
        return price.HasValue ? price.Value.ToString("0.00") : "-";
    }

    private static string FormatPercent(decimal? value)
    {
        return value.HasValue
            ? $"{value.Value:+0.00;-0.00;0.00}%"
            : "-";
    }

    private static Color ResolvePnlColor(decimal? value)
    {
        if (!value.HasValue)
        {
            return Color.Default;
        }

        return value.Value >= 0m ? Color.Success : Color.Error;
    }
}
