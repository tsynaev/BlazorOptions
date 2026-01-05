using System.Collections.ObjectModel;
using System.Linq;

namespace BlazorOptions.ViewModels;

public class PositionBuilderViewModel
{
    private readonly OptionsService _optionsService;

    public PositionBuilderViewModel(OptionsService optionsService)
    {
        _optionsService = optionsService;
    }

    public string Pair { get; set; } = "ETH/USDT";

    public ObservableCollection<OptionLegModel> Legs { get; } = new();

    public EChartConfig ChartConfig { get; private set; } = new(Array.Empty<string>(), Array.Empty<double>(), 0, 0);

    public void InitializeDefaultPosition()
    {
        Legs.Clear();
        Legs.Add(new OptionLegModel
        {
            Type = OptionLegType.Call,
            Strike = 3400,
            Price = 180,
            Size = 1,
            ImpliedVolatility = 75,
            ExpirationDate = DateTime.UtcNow.Date.AddMonths(2)
        });

        Legs.Add(new OptionLegModel
        {
            Type = OptionLegType.Put,
            Strike = 3200,
            Price = 120,
            Size = 1,
            ImpliedVolatility = 70,
            ExpirationDate = DateTime.UtcNow.Date.AddMonths(2)
        });

        UpdateChart();
    }

    public void AddLeg()
    {
        Legs.Add(new OptionLegModel
        {
            Type = OptionLegType.Call,
            Strike = 3300,
            Price = 150,
            Size = 1,
            ImpliedVolatility = 70,
            ExpirationDate = DateTime.UtcNow.Date.AddMonths(1)
        });
    }

    public bool RemoveLeg(OptionLegModel leg)
    {
        if (Legs.Contains(leg))
        {
            Legs.Remove(leg);
            return true;
        }

        return false;
    }

    public void UpdateChart()
    {
        var (xs, profits) = _optionsService.GeneratePosition(Legs, 180);

        var labels = xs.Select(x => x.ToString("0")).ToArray();
        var minProfit = profits.Min();
        var maxProfit = profits.Max();
        var range = Math.Abs(maxProfit - minProfit);
        var padding = Math.Max(10, range * 0.1);

        ChartConfig = new EChartConfig(labels, profits, minProfit - padding, maxProfit + padding);
    }

    public record EChartConfig(string[] Labels, IReadOnlyList<double> Profits, double YMin, double YMax);
}
