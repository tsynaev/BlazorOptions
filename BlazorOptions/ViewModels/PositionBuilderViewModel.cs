using System;
using System.Collections.ObjectModel;
using System.Linq;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class PositionBuilderViewModel
{
    private readonly OptionsService _optionsService;
    private readonly PositionStorageService _storageService;

    private static readonly ObservableCollection<OptionLegModel> EmptyLegs = new();

    public double TemporaryUnderlyingPrice { get; private set; }

    public DateTime SelectedValuationDate { get; private set; } = DateTime.UtcNow.Date;

    public DateTime MaxExpiryDate { get; private set; } = DateTime.UtcNow.Date;

    public int MaxExpiryDays { get; private set; }

    public int SelectedDayOffset { get; private set; }

    public string DaysToExpiryLabel => $"{(MaxExpiryDate - SelectedValuationDate).Days:+#;-#;0} days";

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
        RefreshValuationDateBounds(legs);
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

    public void SetValuationDateFromOffset(int dayOffset)
    {
        var clampedOffset = Math.Clamp(dayOffset, 0, MaxExpiryDays);
        SelectedDayOffset = clampedOffset;
        SelectedValuationDate = DateTime.UtcNow.Date.AddDays(clampedOffset);
        UpdateTemporaryPnls();
        UpdateChart();
    }

    public void SetValuationDate(DateTime date)
    {
        var today = DateTime.UtcNow.Date;
        var clampedDate = date.Date < today ? today : date.Date > MaxExpiryDate ? MaxExpiryDate : date.Date;
        SelectedValuationDate = clampedDate;
        SelectedDayOffset = Math.Clamp((SelectedValuationDate - today).Days, 0, MaxExpiryDays);
        UpdateTemporaryPnls();
        UpdateChart();
    }

    public void ResetValuationDateToToday()
    {
        SetValuationDate(DateTime.UtcNow.Date);
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

    private void RefreshValuationDateBounds(IEnumerable<OptionLegModel> legs)
    {
        var today = DateTime.UtcNow.Date;
        MaxExpiryDate = legs.Any()
            ? legs.Max(l => l.ExpirationDate.Date)
            : today;

        if (MaxExpiryDate < today)
        {
            MaxExpiryDate = today;
        }

        MaxExpiryDays = Math.Max(0, (MaxExpiryDate - today).Days);
        var clampedDate = SelectedValuationDate == default ? today : SelectedValuationDate.Date;
        if (clampedDate < today)
        {
            clampedDate = today;
        }
        else if (clampedDate > MaxExpiryDate)
        {
            clampedDate = MaxExpiryDate;
        }

        var clampedOffset = Math.Clamp((clampedDate - today).Days, 0, MaxExpiryDays);
        var shouldUpdatePnls = clampedDate != SelectedValuationDate || clampedOffset != SelectedDayOffset;

        SelectedValuationDate = clampedDate;
        SelectedDayOffset = clampedOffset;

        if (shouldUpdatePnls)
        {
            UpdateTemporaryPnls();
        }
    }

}
