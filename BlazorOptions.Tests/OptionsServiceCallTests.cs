using BlazorOptions;
using FluentAssertions;

namespace BlazorOptions.Tests;

[TestClass]
public sealed class OptionsServiceCallTests
{
    [TestMethod]
    public void CalculateLegProfit_BuyCall_UsesLimitedLossAndUnlimitedUpside()
    {
        var service = CreateService();
        var leg = new LegModel
        {
            IsIncluded = true,
            Type = LegType.Call,
            Strike = 100m,
            Price = 12m,
            Size = 2m
        };

        // Long call must lose only premium below strike and gain intrinsic minus premium above strike.
        service.CalculateLegProfit(leg, 80m).Should().Be(-24m);
        service.CalculateLegProfit(leg, 100m).Should().Be(-24m);
        service.CalculateLegProfit(leg, 130m).Should().Be(36m);
    }

    [TestMethod]
    public void CalculateLegProfit_SellCall_UsesCappedGainAndShortUpsideLoss()
    {
        var service = CreateService();
        var leg = new LegModel
        {
            IsIncluded = true,
            Type = LegType.Call,
            Strike = 100m,
            Price = 12m,
            Size = -2m
        };

        // Short call must keep premium below strike and lose intrinsic above strike.
        service.CalculateLegProfit(leg, 80m).Should().Be(24m);
        service.CalculateLegProfit(leg, 100m).Should().Be(24m);
        service.CalculateLegProfit(leg, 130m).Should().Be(-36m);
    }

    [TestMethod]
    public void GeneratePosition_BuyCall_KeepsLossFlatBelowStrike()
    {
        var service = CreateService();
        var leg = new LegModel
        {
            IsIncluded = true,
            Type = LegType.Call,
            Strike = 100m,
            Price = 10m,
            Size = 1m
        };

        var (_, profits, _) = service.GeneratePosition(new[] { leg }, points: 41, xMinOverride: 50d, xMaxOverride: 150d);

        profits[0].Should().Be(-10m);
        profits[10].Should().Be(-10m);
        profits[20].Should().Be(-10m);
        profits[^1].Should().Be(40m);
    }

    [TestMethod]
    public void GeneratePosition_SellCall_KeepsGainFlatBelowStrike()
    {
        var service = CreateService();
        var leg = new LegModel
        {
            IsIncluded = true,
            Type = LegType.Call,
            Strike = 100m,
            Price = 10m,
            Size = -1m
        };

        var (_, profits, _) = service.GeneratePosition(new[] { leg }, points: 41, xMinOverride: 50d, xMaxOverride: 150d);

        profits[0].Should().Be(10m);
        profits[10].Should().Be(10m);
        profits[20].Should().Be(10m);
        profits[^1].Should().Be(-40m);
    }

    private static OptionsService CreateService()
    {
        return new OptionsService(new BlackScholes());
    }
}
