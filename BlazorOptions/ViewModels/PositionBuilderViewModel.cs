using System.Collections.ObjectModel;
using System.Linq;
using MudBlazor;
using MudBlazor.Charts;

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

    public List<ChartSeries> ChartSeries { get; private set; } = new();

    public string[] XAxisLabels { get; private set; } = Array.Empty<string>();

    public ChartOptions ChartOptions { get; } = new()
    {
        InterpolationOption = InterpolationOption.NaturalSpline,
        YAxisFormat = "0.##"
    };

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

    public void RemoveLeg(OptionLegModel leg)
    {
        if (Legs.Contains(leg))
        {
            Legs.Remove(leg);
        }
    }

    public void UpdateChart()
    {
        var (xs, profits) = _optionsService.GeneratePosition(Legs, 180);

        ChartSeries = new List<ChartSeries>
        {
            new()
            {
                Name = "P/L at Expiry",
                Data = profits
            }
        };

        XAxisLabels = xs.Select(x => x.ToString("0")).ToArray();

        var minProfit = profits.Min();
        var maxProfit = profits.Max();
        var range = Math.Abs(maxProfit - minProfit);
        var tickSpacing = range <= 0 ? 10 : Math.Max(5, range / 6);

        ChartOptions.YAxisTicks = (int)Math.Ceiling(tickSpacing);
    }
}
