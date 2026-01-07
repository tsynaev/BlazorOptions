using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class PositionBuilderViewModel
{
    private readonly OptionsService _optionsService;
    private readonly PositionStorageService _storageService;

    private static readonly ObservableCollection<OptionLegModel> EmptyLegs = new();

    public double TemporaryUnderlyingPrice { get; private set; }

    public IReadOnlyList<DateTime> ExpiryDateOptions { get; private set; } = Array.Empty<DateTime>();

    public int SelectedExpiryIndex { get; private set; }

    public DateTime SelectedValuationDate { get; private set; } = DateTime.UtcNow.Date;

    public bool HasExpiryDateOptions => ExpiryDateOptions.Count > 0;

    public string SelectedValuationDateLabel => SelectedValuationDate.ToString("yyyy-MM-dd");

    public string[] ExpiryDateLabels { get; private set; } = Array.Empty<string>();

    public string FirstExpiryDateLabel => ExpiryDateOptions.Count > 0
        ? ExpiryDateOptions[0].ToString("yyyy-MM-dd")
        : "--";

    public string LastExpiryDateLabel => ExpiryDateOptions.Count > 0
        ? ExpiryDateOptions[^1].ToString("yyyy-MM-dd")
        : "--";

    public PositionBuilderViewModel(OptionsService optionsService, PositionStorageService storageService)
    {
        _optionsService = optionsService;
        _storageService = storageService;
    }

    public ObservableCollection<PositionModel> Positions { get; } = new();

    public PositionModel? SelectedPosition { get; private set; }

    public ObservableCollection<OptionLegModel> Legs => SelectedPosition?.Legs ?? EmptyLegs;

    public EChartOptions ChartConfig { get; private set; } = new(Guid.Empty, Array.Empty<double>(), Array.Empty<string>(), Array.Empty<double>(), Array.Empty<double>(), null, null, null, 0, 0);

    public async Task InitializeAsync()
    {
        var storedPositions = await _storageService.LoadPositionsAsync();

        if (storedPositions.Count == 0)
        {
            var defaultPosition = CreateDefaultPosition();
            Positions.Add(defaultPosition);
            SelectedPosition = defaultPosition;
            TemporaryUnderlyingPrice = CalculateAnchorPrice(Legs);
            UpdateChart();
            UpdateTemporaryPnls();
            await PersistPositionsAsync();
            return;
        }

        foreach (var position in storedPositions)
        {
            Positions.Add(position);
        }

        SelectedPosition = Positions.FirstOrDefault();
        TemporaryUnderlyingPrice = CalculateAnchorPrice(Legs);
        UpdateChart();
        UpdateTemporaryPnls();
    }

    public async Task AddLegAsync()
    {
        if (SelectedPosition is null)
        {
            return;
        }

        SelectedPosition.Legs.Add(new OptionLegModel
        {
            Type = OptionLegType.Call,
            Strike = 3300,
            Price = 150,
            Size = 1,
            ImpliedVolatility = 70,
            ExpirationDate = DateTime.UtcNow.Date.AddMonths(1)
        });

        UpdateTemporaryPnls();
        await PersistPositionsAsync();
    }

    public async Task<bool> RemoveLegAsync(OptionLegModel leg)
    {
        if (SelectedPosition is null)
        {
            return false;
        }

        if (SelectedPosition.Legs.Contains(leg))
        {
            SelectedPosition.Legs.Remove(leg);
            await PersistPositionsAsync();
            UpdateTemporaryPnls();
            return true;
        }

        return false;
    }

    public async Task AddPositionAsync(string? pair = null)
    {
        var position = CreateDefaultPosition(pair ?? $"Position {Positions.Count + 1}");
        Positions.Add(position);
        SelectedPosition = position;
        TemporaryUnderlyingPrice = CalculateAnchorPrice(Legs);
        UpdateChart();
        UpdateTemporaryPnls();
        await PersistPositionsAsync();
    }

    public async Task<bool> SelectPositionAsync(Guid positionId)
    {
        var position = Positions.FirstOrDefault(p => p.Id == positionId);

        if (position is null)
        {
            return false;
        }

        SelectedPosition = position;
        TemporaryUnderlyingPrice = CalculateAnchorPrice(Legs);
        UpdateChart();
        UpdateTemporaryPnls();
        await Task.CompletedTask;
        return true;
    }

    public async Task UpdatePairAsync(PositionModel position, string pair)
    {
        position.Pair = pair;
        await PersistPositionsAsync();
    }

    public async Task PersistPositionsAsync()
    {
        await _storageService.SavePositionsAsync(Positions);
    }

    public void UpdateChart()
    {
        var legs = SelectedPosition?.Legs ?? Enumerable.Empty<OptionLegModel>();
        var activeLegs = legs.Where(l => l.IsIncluded).ToList();
        RefreshExpiryDateOptions(legs);
        var valuationDate = SelectedValuationDate;
        var (xs, profits, theoreticalProfits) = _optionsService.GeneratePosition(activeLegs, 180, valuationDate);

        var labels = xs.Select(x => x.ToString("0")).ToArray();
        var minProfit = Math.Min(profits.Min(), theoreticalProfits.Min());
        var maxProfit = Math.Max(profits.Max(), theoreticalProfits.Max());
        var tempPnl = activeLegs.Any() ? _optionsService.CalculateTotalTheoreticalProfit(activeLegs, TemporaryUnderlyingPrice, valuationDate) : (double?)null;
        var tempExpiryPnl = activeLegs.Any() ? _optionsService.CalculateTotalProfit(activeLegs, TemporaryUnderlyingPrice) : (double?)null;

        if (tempPnl.HasValue)
        {
            minProfit = Math.Min(minProfit, tempPnl.Value);
            maxProfit = Math.Max(maxProfit, tempPnl.Value);
        }

        if (tempExpiryPnl.HasValue)
        {
            minProfit = Math.Min(minProfit, tempExpiryPnl.Value);
            maxProfit = Math.Max(maxProfit, tempExpiryPnl.Value);
        }

        var range = Math.Abs(maxProfit - minProfit);
        var padding = Math.Max(10, range * 0.1);

        var positionId = SelectedPosition?.Id ?? Guid.Empty;

        ChartConfig = new EChartOptions(positionId, xs, labels, profits, theoreticalProfits, TemporaryUnderlyingPrice, tempPnl, tempExpiryPnl, minProfit - padding, maxProfit + padding);
    }

    public void SetTemporaryUnderlyingPrice(double price)
    {
        TemporaryUnderlyingPrice = price;
        UpdateTemporaryPnls();
        UpdateChart();
    }

    public void SetValuationDateIndex(int index)
    {
        if (index < 0 || index >= ExpiryDateOptions.Count)
        {
            return;
        }

        SelectedExpiryIndex = index;
        SelectedValuationDate = ExpiryDateOptions[index];
        UpdateTemporaryPnls();
        UpdateChart();
    }

    public void UpdateTemporaryPnls()
    {
        var price = TemporaryUnderlyingPrice;

        foreach (var leg in Legs)
        {
            leg.TemporaryPnl = leg.IsIncluded
                ? _optionsService.CalculateLegTheoreticalProfit(leg, price, SelectedValuationDate)
                : 0;
        }
    }

    public async Task<bool> RemovePositionAsync(Guid positionId)
    {
        var positionIndex = Positions.ToList().FindIndex(p => p.Id == positionId);

        if (positionIndex < 0)
        {
            return false;
        }

        var removedPosition = Positions[positionIndex];
        Positions.RemoveAt(positionIndex);

        if (SelectedPosition?.Id == removedPosition.Id)
        {
            if (Positions.Count == 0)
            {
                SelectedPosition = null;
            }
            else
            {
                var nextIndex = Math.Min(positionIndex, Positions.Count - 1);
                SelectedPosition = Positions[nextIndex];
            }
        }

        TemporaryUnderlyingPrice = CalculateAnchorPrice(Legs);
        UpdateChart();
        UpdateTemporaryPnls();
        await PersistPositionsAsync();
        return true;
    }

    private PositionModel CreateDefaultPosition(string? pair = null)
    {
        var position = new PositionModel
        {
            Pair = pair ?? "ETH/USDT"
        };

        position.Legs.Add(new OptionLegModel
        {
            Type = OptionLegType.Call,
            Strike = 3400,
            Price = 180,
            Size = 1,
            ImpliedVolatility = 75,
            ExpirationDate = DateTime.UtcNow.Date.AddMonths(2)
        });

        position.Legs.Add(new OptionLegModel
        {
            Type = OptionLegType.Put,
            Strike = 3200,
            Price = 120,
            Size = 1,
            ImpliedVolatility = 70,
            ExpirationDate = DateTime.UtcNow.Date.AddMonths(2)
        });

        return position;
    }

    private static double CalculateAnchorPrice(IEnumerable<OptionLegModel> legs)
    {
        var activeLegs = legs.Where(l => l.IsIncluded).ToList();

        if (activeLegs.Count == 0)
        {
            return 1000;
        }

        return activeLegs.Average(l => l.Strike > 0 ? l.Strike : l.Price);
    }

    private void RefreshExpiryDateOptions(IEnumerable<OptionLegModel> legs)
    {
        var options = BuildExpiryDateOptions(legs);

        if (options.Count == 0)
        {
            options.Add(DateTime.UtcNow.Date);
        }

        ExpiryDateOptions = options;
        ExpiryDateLabels = BuildExpiryDateLabels(options);

        var currentDate = SelectedValuationDate == default ? DateTime.UtcNow.Date : SelectedValuationDate.Date;
        var index = options.IndexOf(currentDate);

        if (index < 0)
        {
            currentDate = FindClosestDate(options, currentDate);
            index = options.IndexOf(currentDate);
        }

        SelectedValuationDate = currentDate;
        SelectedExpiryIndex = index;
    }

    private static List<DateTime> BuildExpiryDateOptions(IEnumerable<OptionLegModel> legs)
    {
        var uniqueDates = legs
            .Select(l => l.ExpirationDate.Date)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        if (uniqueDates.Count == 0)
        {
            return new List<DateTime> { DateTime.UtcNow.Date };
        }

        var today = DateTime.UtcNow.Date;
        var rangeStart = uniqueDates.Min();
        var rangeEnd = uniqueDates.Max();
        var options = new SortedSet<DateTime>(uniqueDates) { today };

        for (var i = 0; i < uniqueDates.Count - 1; i++)
        {
            var start = uniqueDates[i];
            var end = uniqueDates[i + 1];
            var midpoint = start.AddDays((end - start).TotalDays / 2).Date;

            if (midpoint > start && midpoint < end)
            {
                options.Add(midpoint);
            }
        }

        rangeStart = today < rangeStart ? today : rangeStart;
        rangeEnd = today > rangeEnd ? today : rangeEnd;

        AddFridays(options, rangeStart, rangeEnd);
        EnsureMinimumSteps(options, rangeStart, rangeEnd, 20);

        return options.OrderBy(d => d).ToList();
    }

    private static string[] BuildExpiryDateLabels(IReadOnlyList<DateTime> options)
    {
        if (options.Count == 0)
        {
            return Array.Empty<string>();
        }

        var labels = new string[options.Count];
        var previousYear = options[0].Year;
        var previousMonth = options[0].Month;
        var culture = CultureInfo.CurrentCulture;
        var dateFormat = culture.DateTimeFormat;

        for (var i = 0; i < options.Count; i++)
        {
            var date = options[i];
            if (i == 0 || i == options.Count - 1)
            {
                labels[i] = string.Empty;
                previousYear = date.Year;
                previousMonth = date.Month;
                continue;
            }
            var isNewYear = i == 0 || date.Year != previousYear;
            var isNewMonth = i == 0 || date.Month != previousMonth;

            var primaryLabel = isNewYear
                ? $"{date.ToString("MMM", culture)}\u00A0{date:dd}\u00A0{date:yyyy}"
                : isNewMonth
                    ? $"{date.ToString("MMM", culture)}\u00A0{date:dd}"
                    : date.ToString("dd", culture);

            var dayOfWeek = dateFormat.GetShortestDayName(date.DayOfWeek);
            labels[i] = $"{primaryLabel}\n{dayOfWeek}";

            previousYear = date.Year;
            previousMonth = date.Month;
        }

        return labels;
    }

    private static void AddFridays(SortedSet<DateTime> options, DateTime start, DateTime end)
    {
        var firstFriday = start;

        while (firstFriday.DayOfWeek != DayOfWeek.Friday)
        {
            firstFriday = firstFriday.AddDays(1);
        }

        for (var date = firstFriday; date <= end; date = date.AddDays(7))
        {
            options.Add(date);
        }
    }

    private static void EnsureMinimumSteps(SortedSet<DateTime> options, DateTime start, DateTime end, int minimumSteps)
    {
        if (options.Count >= minimumSteps)
        {
            return;
        }

        var effectiveEnd = end;
        var spanDays = Math.Max(0, (int)(end - start).TotalDays);

        if (spanDays + 1 < minimumSteps)
        {
            effectiveEnd = start.AddDays(minimumSteps - 1);
        }

        var rangeDays = Math.Max(1, (int)(effectiveEnd - start).TotalDays);
        var target = Math.Max(minimumSteps, options.Count);
        var stepDays = Math.Max(1, (int)Math.Ceiling(rangeDays / (double)(target - 1)));

        for (var date = start; date <= effectiveEnd; date = date.AddDays(stepDays))
        {
            options.Add(date);
        }
    }

    private static DateTime FindClosestDate(IReadOnlyList<DateTime> options, DateTime target)
    {
        var closest = options[0];
        var smallestDistance = Math.Abs((options[0] - target).TotalDays);

        for (var i = 1; i < options.Count; i++)
        {
            var distance = Math.Abs((options[i] - target).TotalDays);

            if (distance < smallestDistance)
            {
                smallestDistance = distance;
                closest = options[i];
            }
        }

        return closest;
    }

}
