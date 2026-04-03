using BlazorOptions.ViewModels;
using FluentAssertions;
using BlazorOptions.Services;

namespace BlazorOptions.Tests;

[TestClass]
public sealed class PositionPnlCalculatorTests
{
    private static PositionPnlCalculator CreateCalculator()
    {
        return new PositionPnlCalculator(new OptionsService(new BlackScholes()), new ExchangeService());
    }

    [TestMethod]
    public void ResolveEntryValue_SumsIncludedNonFuturesOnly()
    {
        var calculator = CreateCalculator();
        var legs = new[]
        {
            new LegModel { IsIncluded = true, Type = LegType.Call, Size = 2m, Price = 10m },
            new LegModel { IsIncluded = true, Type = LegType.Put, Size = -3m, Price = 4m },
            new LegModel { IsIncluded = true, Type = LegType.Future, Size = 1m, Price = 100m },
            new LegModel { IsIncluded = false, Type = LegType.Call, Size = 5m, Price = 7m }
        };

        calculator.ResolveEntryValue(legs).Should().Be(32m);
    }

    [TestMethod]
    public void ResolveBoundedMaxGain_ReturnsInteriorPeakOnly()
    {
        var calculator = CreateCalculator();
        var bounded = new decimal[] { -100m, 0m, 330m, 0m, -100m };
        var unbounded = new decimal[] { -10m, 0m, 10m, 20m, 30m };

        calculator.ResolveBoundedMaxGain(bounded).Should().Be(330m);
        calculator.ResolveBoundedMaxGain(unbounded).Should().BeNull();
    }

    [TestMethod]
    public void ResolvePnlPercent_PrefersBoundedMaxGainAndFallsBackToEntryValue()
    {
        var calculator = CreateCalculator();

        calculator.ResolvePnlPercent(68m, 330m, 498m).Should().BeApproximately(20.6060606061m, 0.0000001m);
        calculator.ResolvePnlPercent(68m, null, 498m).Should().BeApproximately(13.6546184739m, 0.0000001m);
    }

    [TestMethod]
    public void ResolveBoundedMaxGain_FromLegs_UsesOptionsServicePayoffSampling()
    {
        var calculator = CreateCalculator();
        var legs = new[]
        {
            new LegModel { IsIncluded = true, Type = LegType.Call, Strike = 100m, Price = 10m, Size = -1m },
            new LegModel { IsIncluded = true, Type = LegType.Call, Strike = 120m, Price = 3m, Size = 1m }
        };

        calculator.ResolveBoundedMaxGain(legs, range: null, realizedPnl: 0m).Should().Be(7m);
    }

    [TestMethod]
    public void ResolvePnl_ReturnsTotalAndPercentFromPositionModel()
    {
        var calculator = CreateCalculator();
        var position = new PositionModel
        {
            BaseAsset = "ETH",
            QuoteAsset = "USDT",
            Closed = new ClosedModel { Include = false },
            Legs = new System.Collections.ObjectModel.ObservableCollection<LegModel>
            {
                new() { IsIncluded = true, Type = LegType.Call, Strike = 100m, Price = 10m, Size = -1m },
                new() { IsIncluded = true, Type = LegType.Call, Strike = 120m, Price = 3m, Size = 1m }
            }
        };

        var (totalPnl, percentPnl) = calculator.ResolvePnl(position, 110m);

        totalPnl.Should().Be(7m);
        percentPnl.Should().Be(100m);
    }
}
