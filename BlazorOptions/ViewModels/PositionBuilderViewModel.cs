using System.Collections.ObjectModel;
using System.Linq;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

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

    public ISeries[] Series { get; private set; } = Array.Empty<ISeries>();

    public Axis[] XAxes { get; private set; } = Array.Empty<Axis>();

    public Axis[] YAxes { get; private set; } = Array.Empty<Axis>();

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
        var points = xs.Select((x, i) => new ObservablePoint(x, profits[i])).ToArray();

        Series =
        [
            new LineSeries<ObservablePoint>
            {
                Values = points,
                Stroke = new SolidColorPaint(SKColors.MediumPurple, 2),
                Fill = new SolidColorPaint(SKColors.MediumPurple.WithAlpha(40)),
                GeometrySize = 0,
                Name = "P/L at Expiry"
            }
        ];

        XAxes =
        [
            new Axis
            {
                Name = "Underlying price (USDT)",
                Labeler = value => value.ToString("0"),
                MinLimit = xs.First(),
                MaxLimit = xs.Last()
            }
        ];

        var minProfit = profits.Min();
        var maxProfit = profits.Max();
        var padding = Math.Max(10, Math.Abs(maxProfit - minProfit) * 0.05);

        var minLimit = Math.Min(minProfit - padding, -padding);
        var maxLimit = Math.Max(maxProfit + padding, padding);

        YAxes =
        [
            new Axis
            {
                Name = "Profit / Loss",
                Labeler = value => value.ToString("0.##"),
                MinLimit = minLimit,
                MaxLimit = maxLimit
            }
        ];
    }
}
