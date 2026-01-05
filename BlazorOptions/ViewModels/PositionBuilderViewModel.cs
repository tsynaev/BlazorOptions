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

    public PositionBuilderViewModel(OptionsService optionsService, PositionStorageService storageService)
    {
        _optionsService = optionsService;
        _storageService = storageService;
    }

    public ObservableCollection<PositionModel> Positions { get; } = new();

    public PositionModel? SelectedPosition { get; private set; }

    public ObservableCollection<OptionLegModel> Legs => SelectedPosition?.Legs ?? EmptyLegs;

    public EChartConfig ChartConfig { get; private set; } = new(Guid.Empty, Array.Empty<string>(), Array.Empty<double>(), 0, 0);

    public async Task InitializeAsync()
    {
        var storedPositions = await _storageService.LoadPositionsAsync();

        if (storedPositions.Count == 0)
        {
            var defaultPosition = CreateDefaultPosition();
            Positions.Add(defaultPosition);
            SelectedPosition = defaultPosition;
            UpdateChart();
            await PersistPositionsAsync();
            return;
        }

        foreach (var position in storedPositions)
        {
            Positions.Add(position);
        }

        SelectedPosition = Positions.FirstOrDefault();
        UpdateChart();
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
            return true;
        }

        return false;
    }

    public async Task AddPositionAsync(string? pair = null)
    {
        var position = CreateDefaultPosition(pair ?? $"Position {Positions.Count + 1}");
        Positions.Add(position);
        SelectedPosition = position;
        UpdateChart();
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
        UpdateChart();
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
        var (xs, profits) = _optionsService.GeneratePosition(legs, 180);

        var labels = xs.Select(x => x.ToString("0")).ToArray();
        var minProfit = profits.Min();
        var maxProfit = profits.Max();
        var range = Math.Abs(maxProfit - minProfit);
        var padding = Math.Max(10, range * 0.1);

        var positionId = SelectedPosition?.Id ?? Guid.Empty;

        ChartConfig = new EChartConfig(positionId, labels, profits, minProfit - padding, maxProfit + padding);
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

        UpdateChart();
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

    public record EChartConfig(Guid PositionId, string[] Labels, IReadOnlyList<double> Profits, double YMin, double YMax);
}
