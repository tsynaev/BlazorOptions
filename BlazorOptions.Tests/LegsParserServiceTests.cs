using System.Globalization;
using System.Reflection;
using BlazorOptions.Services;
using BlazorOptions.ViewModels;
using FluentAssertions;

namespace BlazorOptions.Tests;

[TestClass]
public sealed class LegsParserServiceTests
{
    private sealed class TestTelemetryService : ITelemetryService
    {
        public System.Diagnostics.Activity? StartActivity(string name, System.Diagnostics.ActivityKind kind = System.Diagnostics.ActivityKind.Internal)
        {
            return null;
        }
    }

    [TestMethod]
    public void ParseLegs_ParsesBuySellJsonArray()
    {
        var service = CreateService();
        var input = "[\"buy ETH-27MAR26-2200-P\",\"sell ETH-27MAR26-1800-P\",\"sell ETH-27MAR26-2500-P\"]";

        var legs = service.ParseLegs(input, defaultSize: 10m, defaultExpiration: null, baseAsset: "ETH");

        legs.Should().HaveCount(3);
        legs[0].Type.Should().Be(LegType.Put);
        legs[0].Strike.Should().Be(2200m);
        legs[0].Size.Should().Be(10m);
        legs[1].Type.Should().Be(LegType.Put);
        legs[1].Strike.Should().Be(1800m);
        legs[1].Size.Should().Be(-10m);
    }

    [TestMethod]
    public void ParseLegs_UsesDefaultSizeWhenMissing()
    {
        var service = CreateService();
        var input = "C 3000";

        var legs = service.ParseLegs(input, defaultSize: 10m, defaultExpiration: null, baseAsset: "ETH");

        legs.Should().HaveCount(1);
        legs[0].Size.Should().Be(10m);
        legs[0].Type.Should().Be(LegType.Call);
        legs[0].Strike.Should().Be(3000m);
    }

    [TestMethod]
    public void ParseLegs_UsesDefaultExpirationWhenMissing()
    {
        var service = CreateService();
        var input = "+1 P 2500";
        var defaultExpiration = DateTime.UtcNow.Date.AddDays(30);

        var legs = service.ParseLegs(input, defaultSize: 1m, defaultExpiration: defaultExpiration, baseAsset: "ETH");

        legs.Should().HaveCount(1);
        legs[0].ExpirationDate.Should().Be(defaultExpiration.Date);
    }

    [TestMethod]
    public void ParseLegs_ParsesFutureFromSizeOnly()
    {
        var service = CreateService();
        var legs = service.ParseLegs("3", defaultSize: 10m, defaultExpiration: null, baseAsset: "ETH");

        legs.Should().HaveCount(1);
        legs[0].Type.Should().Be(LegType.Future);
        legs[0].Size.Should().Be(3m);
    }

    [TestMethod]
    public void ParseLegs_ParsesFutureWithPriceOverride()
    {
        var service = CreateService();
        var legs = service.ParseLegs("-3 F @2350", defaultSize: 10m, defaultExpiration: null, baseAsset: "ETH");

        legs.Should().HaveCount(1);
        legs[0].Type.Should().Be(LegType.Future);
        legs[0].Size.Should().Be(-3m);
        legs[0].Price.Should().Be(2350m);
    }

    [TestMethod]
    public async Task ApplyTickerDefaultsAsync_FillsStrikePriceIvAndSymbol()
    {
        var service = CreateService();
        var baseAsset = "ETH";
        var expiration = new DateTime(2026, 12, 20);
        var tickers = BuildTickers(baseAsset, expiration);

        var leg = new LegModel
        {
            Type = LegType.Call,
            Size = 5m,
            ExpirationDate = expiration
        };

        SeedTickers(service, baseAsset, tickers);

        await service.ApplyTickerDefaultsAsync(new[] { leg }, baseAsset, underlyingPrice: 2100m);

        leg.Strike.Should().Be(2000m);
        var expectedSymbol = $"ETH-{expiration.ToString("ddMMMyy", CultureInfo.InvariantCulture)}-2000-C".ToUpperInvariant();
        leg.Symbol.Should().Be(expectedSymbol);
        leg.Price.Should().Be(55m);
        leg.ImpliedVolatility.Should().Be(75m);
    }

    [TestMethod]
    public void BuildPreviewDescription_UsesTickerDefaults()
    {
        var service = CreateService();
        var baseAsset = "ETH";
        var expiration = new DateTime(2026, 12, 20);
        var tickers = BuildTickers(baseAsset, expiration);

        var leg = new LegModel
        {
            Type = LegType.Put,
            Size = -2m,
            ExpirationDate = expiration
        };

        SeedTickers(service, baseAsset, tickers);
        var description = service.BuildPreviewDescription(new[] { leg }, underlyingPrice: 1900m, baseAsset: baseAsset);

        description.Should().Contain("Sell 2 PUT");
        description.Should().Contain("2000");
        description.Should().Contain("@");
    }

    private static LegsParserService CreateService()
    {
        var httpClient = new HttpClient();
        var exchangeService = new ExchangeService();
        var telemetryService = new TestTelemetryService();
        var optionsChainService = new OptionsChainService(httpClient, exchangeService, telemetryService);
        return new LegsParserService(optionsChainService, telemetryService, exchangeService);
    }

    private static void SeedTickers(LegsParserService service, string baseAsset, List<OptionChainTicker> tickers)
    {
        var optionsField = typeof(LegsParserService)
            .GetField("_optionsChainService", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("OptionsChainService field not found.");
        var optionsChainService = optionsField.GetValue(service) as OptionsChainService
            ?? throw new AssertFailedException("OptionsChainService instance is null.");

        var cacheField = typeof(OptionsChainService)
            .GetField("_cachedTickers", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("OptionsChainService cache field not found.");
        var cache = cacheField.GetValue(optionsChainService) as Dictionary<string, List<OptionChainTicker>>
            ?? throw new AssertFailedException("OptionsChainService cache is null.");
        cache[baseAsset] = tickers;
    }

    private static List<OptionChainTicker> BuildTickers(string baseAsset, DateTime expiration)
    {
        var expirationToken = expiration.ToString("ddMMMyy", CultureInfo.InvariantCulture).ToUpperInvariant();
        return new List<OptionChainTicker>
        {
            new OptionChainTicker(
                symbol: $"{baseAsset}-{expirationToken}-2000-C",
                baseAsset: baseAsset,
                expirationDate: expiration,
                strike: 2000m,
                type: LegType.Call,
                underlyingPrice: 2000m,
                markPrice: 50m,
                lastPrice: 49m,
                markIv: 0.75m,
                bidPrice: 45m,
                askPrice: 55m,
                bidIv: 0.7m,
                askIv: 0.8m,
                delta: 0.5m,
                gamma: null,
                vega: null,
                theta: null),
            new OptionChainTicker(
                symbol: $"{baseAsset}-{expirationToken}-2000-P",
                baseAsset: baseAsset,
                expirationDate: expiration,
                strike: 2000m,
                type: LegType.Put,
                underlyingPrice: 2000m,
                markPrice: 60m,
                lastPrice: 58m,
                markIv: 0.8m,
                bidPrice: 55m,
                askPrice: 65m,
                bidIv: 0.78m,
                askIv: 0.82m,
                delta: -0.5m,
                gamma: null,
                vega: null,
                theta: null)
        };
    }
}
