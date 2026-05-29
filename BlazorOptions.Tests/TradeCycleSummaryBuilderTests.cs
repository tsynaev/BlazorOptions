using BlazorOptions.API.TradingHistory;
using BlazorOptions.ViewModels;
using FluentAssertions;
using System.Globalization;

namespace BlazorOptions.Tests;

[TestClass]
public sealed class TradeSummaryBuilderTests
{
    [TestMethod]
    public void BuildTradeSummaries_BuildsShortFuturesCycle()
    {
        var entries = new[]
        {
            CreateEntry("2026-03-19T19:37:00Z", "SELL", 2.12m, "BTCUSDT", 2120.38m, 0.50m, -2.12m),
            CreateEntry("2026-03-19T19:44:00Z", "BUY", 2.12m, "BTCUSDT", 2127.43m, 0.60m, 0m)
        };

        var summaries = TradeSummaryBuilder.BuildTradeSummaries(entries);

        summaries.Should().ContainSingle();
        summaries[0].EntryPrice.Should().Be(2120.38m);
        summaries[0].Direction.Should().Be("Open/Close Short");
        summaries[0].Size.Should().Be(2.12m);
        summaries[0].ClosePrice.Should().Be(2127.43m);
        summaries[0].Fee.Should().Be(1.10m);
        summaries[0].Pnl.Should().Be((2120.38m - 2127.43m) * 2.12m - 1.10m);
        summaries[0].EntryStartTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-19T19:37:00Z"));
        summaries[0].EntryEndTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-19T19:37:00Z"));
        summaries[0].CloseStartTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-19T19:44:00Z"));
        summaries[0].CloseEndTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-19T19:44:00Z"));
    }

    [TestMethod]
    public void BuildTradeSummaries_BuildsLongOptionsCycleUsingOpeningVwap()
    {
        var entries = new[]
        {
            CreateEntry("2026-03-20T01:53:00Z", "BUY", 2m, "ETH-27MAR26-2200-C", 80m, 0.10m, 2m),
            CreateEntry("2026-03-20T10:15:00Z", "BUY", 3m, "ETH-27MAR26-2200-C", 79.3333333333m, 0.20m, 5m),
            CreateEntry("2026-03-20T19:44:00Z", "SELL", 5m, "ETH-27MAR26-2200-C", 68.7m, 0.30m, 0m)
        };

        var summaries = TradeSummaryBuilder.BuildTradeSummaries(entries);

        summaries.Should().ContainSingle();
        summaries[0].EntryPrice.Should().Be(79.6m);
        summaries[0].Direction.Should().Be("Open/Close Long");
        summaries[0].Size.Should().Be(5m);
        summaries[0].ClosePrice.Should().Be(68.7m);
        summaries[0].Fee.Should().Be(0.60m);
        summaries[0].Pnl.Should().Be((68.7m - 79.6m) * 5m - 0.60m);
        summaries[0].EntryStartTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-20T01:53:00Z"));
        summaries[0].EntryEndTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-20T10:15:00Z"));
        summaries[0].CloseStartTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-20T19:44:00Z"));
        summaries[0].CloseEndTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-20T19:44:00Z"));
    }

    [TestMethod]
    public void BuildTradeSummaries_BuildsTwoShortCycles()
    {
        var entries = new[]
        {
            CreateEntry("2026-03-20T14:32:00Z", "SELL", 0.1m, "BTCUSDT", 69959.691m, 0.05m, -0.1m),
            CreateEntry("2026-03-20T14:50:00Z", "BUY", 0.1m, "BTCUSDT", 70319.236m, 0.04m, 0m),
            CreateEntry("2026-03-20T15:00:00Z", "SELL", 0.1m, "BTCUSDT", 69732.6m, 0.03m, -0.1m),
            CreateEntry("2026-03-20T18:02:00Z", "BUY", 0.1m, "BTCUSDT", 69963.553m, 0.02m, 0m)
        };

        var summaries = TradeSummaryBuilder.BuildTradeSummaries(entries);

        summaries.Should().HaveCount(2);
        summaries[0].EntryPrice.Should().Be(69959.691m);
        summaries[0].Direction.Should().Be("Open/Close Short");
        summaries[0].Size.Should().Be(0.1m);
        summaries[0].ClosePrice.Should().Be(70319.236m);
        summaries[0].Fee.Should().Be(0.09m);
        summaries[0].Pnl.Should().Be((69959.691m - 70319.236m) * 0.1m - 0.09m);
        summaries[0].EntryStartTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-20T14:32:00Z"));
        summaries[0].EntryEndTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-20T14:32:00Z"));
        summaries[0].CloseStartTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-20T14:50:00Z"));
        summaries[0].CloseEndTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-20T14:50:00Z"));
        summaries[1].EntryPrice.Should().Be(69732.6m);
        summaries[1].Direction.Should().Be("Open/Close Short");
        summaries[1].Size.Should().Be(0.1m);
        summaries[1].ClosePrice.Should().Be(69963.553m);
        summaries[1].Fee.Should().Be(0.05m);
        summaries[1].Pnl.Should().Be((69732.6m - 69963.553m) * 0.1m - 0.05m);
        summaries[1].EntryStartTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-20T15:00:00Z"));
        summaries[1].EntryEndTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-20T15:00:00Z"));
        summaries[1].CloseStartTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-20T18:02:00Z"));
        summaries[1].CloseEndTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-20T18:02:00Z"));
    }

    [TestMethod]
    public void BuildTradeSummaries_IncludesOpenLongCycle()
    {
        var entries = new[]
        {
            CreateEntry("2026-03-22T09:00:00Z", "BUY", 2m, "ETH-28MAR26-2200-C", 12m, 0.15m, 2m),
            CreateEntry("2026-03-22T09:05:00Z", "BUY", 1m, "ETH-28MAR26-2200-C", 13m, 0.10m, 3m)
        };

        var summaries = TradeSummaryBuilder.BuildTradeSummaries(entries);

        summaries.Should().ContainSingle();
        summaries[0].Direction.Should().Be("Open Long");
        summaries[0].EntryPrice.Should().Be(12.333333333333333333333333333m);
        summaries[0].Size.Should().Be(3m);
        summaries[0].Fee.Should().Be(0.25m);
        summaries[0].CloseStartTimestamp.Should().BeNull();
        summaries[0].CloseEndTimestamp.Should().BeNull();
        summaries[0].ClosePrice.Should().BeNull();
        summaries[0].Pnl.Should().Be(-0.25m);
    }

    [TestMethod]
    public void BuildTradeSummaries_KeepsMultipleShortCyclesAcrossSeparateFlatResets()
    {
        var entries = new[]
        {
            CreateEntry("2026-03-22T00:01:00Z", "SELL", 0.01m, "ETHUSDT-26JUN26", 2074.54m, 0.01140997m, -0.01m),
            CreateEntry("2026-03-22T00:01:00Z", "SELL", 1.99m, "ETHUSDT-26JUN26", 2073.95m, 2.26993828m, -2m),
            CreateEntry("2026-03-24T20:21:00Z", "BUY", 2m, "ETHUSDT-26JUN26", 2155.63375m, 2.37619615m, 0m),
            CreateEntry("2026-03-26T09:31:00Z", "SELL", 2m, "ETHUSDT-26JUN26", 2100.1038m, 2.30767835m, -2m),
            CreateEntry("2026-03-26T13:45:00Z", "BUY", 2m, "ETHUSDT-26JUN26", 2105.2261m, 2.31798009m, 0m),
            CreateEntry("2026-03-27T10:37:00Z", "SELL", 2m, "ETHUSDT-26JUN26", 2031.79515m, 2.14578366m, -2m),
            CreateEntry("2026-03-27T11:34:00Z", "BUY", 1m, "ETHUSDT-26JUN26", 2007.7449m, 1.10425981m, -1m),
            CreateEntry("2026-03-27T13:37:00Z", "SELL", 3m, "ETHUSDT-26JUN26", 1980m, 1.188m, -4m),
            CreateEntry("2026-03-27T14:19:00Z", "BUY", 4m, "ETHUSDT-26JUN26", 1993.3m, 1.596m, 0m)
        };

        var summaries = TradeSummaryBuilder.BuildTradeSummaries(entries);

        summaries.Should().HaveCount(3);
        summaries[0].Size.Should().Be(2m);
        summaries[1].Size.Should().Be(2m);
        summaries[2].Size.Should().Be(5m);
        summaries[2].EntryStartTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-27T10:37:00Z"));
        summaries[2].CloseEndTimestamp.Should().Be(ToUnixTimeMilliseconds("2026-03-27T14:19:00Z"));
    }

    [TestMethod]
    public void BuildTradeSummaries_UsesProjectedSequenceForSameTimestampRows()
    {
        var entries = new[]
        {
            CreateEntry("2026-03-27T10:37:00Z", "SELL", 1m, "ETHUSDT-26JUN26", 2000m, 0.1m, -1m),
            CreateEntry("2026-03-27T10:37:00Z", "BUY", 1m, "ETHUSDT-26JUN26", 1990m, 0.1m, 0m),
            CreateEntry("2026-03-27T10:37:00Z", "SELL", 1m, "ETHUSDT-26JUN26", 1980m, 0.1m, -1m),
            CreateEntry("2026-03-27T10:37:00Z", "BUY", 1m, "ETHUSDT-26JUN26", 1970m, 0.1m, 0m)
        };

        var summaries = TradeSummaryBuilder.BuildTradeSummaries(entries);

        summaries.Should().HaveCount(2);
        summaries[0].EntryPrice.Should().Be(2000m);
        summaries[0].ClosePrice.Should().Be(1990m);
        summaries[1].EntryPrice.Should().Be(1980m);
        summaries[1].ClosePrice.Should().Be(1970m);
    }

    [TestMethod]
    public void BuildTradeSummaries_SplitsPartialCloseAndReopenIntoSeparateShortTrades()
    {
        var entries = new[]
        {
            CreateEntry("2026-03-27T10:37:23Z", "SELL", 0.01m, "ETHUSDT-26JUN26", 2034.73m, 0.01119102m, -0.01m),
            CreateEntry("2026-03-27T10:37:23Z", "SELL", 0.10m, "ETHUSDT-26JUN26", 2034m, 0.11187m, -0.11m),
            CreateEntry("2026-03-27T10:37:23Z", "SELL", 0.01m, "ETHUSDT-26JUN26", 2033.68m, 0.01118524m, -0.12m),
            CreateEntry("2026-03-27T10:37:23Z", "SELL", 0.01m, "ETHUSDT-26JUN26", 2033.47m, 0.01118409m, -0.13m),
            CreateEntry("2026-03-27T10:37:23Z", "SELL", 0.01m, "ETHUSDT-26JUN26", 2033.26m, 0.01118293m, -0.14m),
            CreateEntry("2026-03-27T10:37:23Z", "SELL", 0.01m, "ETHUSDT-26JUN26", 2032.84m, 0.01118062m, -0.15m),
            CreateEntry("2026-03-27T10:37:23Z", "SELL", 0.01m, "ETHUSDT-26JUN26", 2032.42m, 0.01117831m, -0.16m),
            CreateEntry("2026-03-27T10:37:23Z", "SELL", 0.01m, "ETHUSDT-26JUN26", 2032m, 0.011176m, -0.17m),
            CreateEntry("2026-03-27T10:37:23Z", "SELL", 0.01m, "ETHUSDT-26JUN26", 2031.79m, 0.01117485m, -0.18m),
            CreateEntry("2026-03-27T10:37:23Z", "SELL", 1.82m, "ETHUSDT-26JUN26", 2031.62m, 2.03365162m, -2m),
            CreateEntry("2026-03-27T11:34:03Z", "BUY", 0.01m, "ETHUSDT-26JUN26", 2007.58m, 0.01104169m, -1.99m),
            CreateEntry("2026-03-27T11:34:03Z", "BUY", 0.02m, "ETHUSDT-26JUN26", 2007.59m, 0.02208349m, -1.97m),
            CreateEntry("2026-03-27T11:34:03Z", "BUY", 0.97m, "ETHUSDT-26JUN26", 2007.75m, 1.07113463m, -1m),
            CreateEntry("2026-03-27T13:37:00Z", "SELL", 0.25m, "ETHUSDT-26JUN26", 1980m, 0.099m, -1.25m),
            CreateEntry("2026-03-27T13:37:01Z", "SELL", 0.04m, "ETHUSDT-26JUN26", 1980m, 0.01584m, -1.29m),
            CreateEntry("2026-03-27T13:37:01Z", "SELL", 0.29m, "ETHUSDT-26JUN26", 1980m, 0.11484m, -1.58m),
            CreateEntry("2026-03-27T13:37:01Z", "SELL", 0.28m, "ETHUSDT-26JUN26", 1980m, 0.11088m, -1.86m),
            CreateEntry("2026-03-27T13:37:01Z", "SELL", 0.29m, "ETHUSDT-26JUN26", 1980m, 0.11484m, -2.15m),
            CreateEntry("2026-03-27T13:37:01Z", "SELL", 0.29m, "ETHUSDT-26JUN26", 1980m, 0.11484m, -2.44m),
            CreateEntry("2026-03-27T13:37:01Z", "SELL", 0.28m, "ETHUSDT-26JUN26", 1980m, 0.11088m, -2.72m),
            CreateEntry("2026-03-27T13:37:01Z", "SELL", 0.29m, "ETHUSDT-26JUN26", 1980m, 0.11484m, -3.01m),
            CreateEntry("2026-03-27T13:37:01Z", "SELL", 0.29m, "ETHUSDT-26JUN26", 1980m, 0.11484m, -3.30m),
            CreateEntry("2026-03-27T13:37:01Z", "SELL", 0.29m, "ETHUSDT-26JUN26", 1980m, 0.11484m, -3.59m),
            CreateEntry("2026-03-27T13:37:01Z", "SELL", 0.12m, "ETHUSDT-26JUN26", 1980m, 0.04752m, -3.71m),
            CreateEntry("2026-03-27T13:37:01Z", "SELL", 0.29m, "ETHUSDT-26JUN26", 1980m, 0.11484m, -4m),
            CreateEntry("2026-03-27T14:19:38Z", "BUY", 0.52m, "ETHUSDT-26JUN26", 1993.3m, 0.2073032m, -3.48m),
            CreateEntry("2026-03-27T14:19:38Z", "BUY", 0.28m, "ETHUSDT-26JUN26", 1993.3m, 0.1116248m, -3.2m),
            CreateEntry("2026-03-27T14:19:38Z", "BUY", 0.29m, "ETHUSDT-26JUN26", 1993.3m, 0.1156114m, -2.91m),
            CreateEntry("2026-03-27T14:19:38Z", "BUY", 0.58m, "ETHUSDT-26JUN26", 1993.3m, 0.2312228m, -2.33m),
            CreateEntry("2026-03-27T14:19:38Z", "BUY", 0.28m, "ETHUSDT-26JUN26", 1993.3m, 0.1116248m, -2.05m),
            CreateEntry("2026-03-27T14:19:38Z", "BUY", 0.29m, "ETHUSDT-26JUN26", 1993.3m, 0.1156114m, -1.76m),
            CreateEntry("2026-03-27T14:19:39Z", "BUY", 0.29m, "ETHUSDT-26JUN26", 1993.3m, 0.1156114m, -1.47m),
            CreateEntry("2026-03-27T14:19:39Z", "BUY", 0.28m, "ETHUSDT-26JUN26", 1993.3m, 0.1116248m, -1.19m),
            CreateEntry("2026-03-27T14:19:39Z", "BUY", 0.29m, "ETHUSDT-26JUN26", 1993.3m, 0.1156114m, -0.9m),
            CreateEntry("2026-03-27T14:19:39Z", "BUY", 0.57m, "ETHUSDT-26JUN26", 1993.3m, 0.2272362m, -0.33m),
            CreateEntry("2026-03-27T14:19:39Z", "BUY", 0.29m, "ETHUSDT-26JUN26", 1993.3m, 0.1156114m, -0.04m),
            CreateEntry("2026-03-27T14:19:39Z", "BUY", 0.04m, "ETHUSDT-26JUN26", 1993.3m, 0.0159464m, 0m)
        };

        var summaries = TradeSummaryBuilder.BuildTradeSummaries(entries);

        summaries.Should().HaveCount(2);

        summaries[0].Direction.Should().Be("Open/Close Short");
        summaries[0].EntryPrice.Should().Be(2032.0722909090909090909090909m);
        summaries[0].Size.Should().Be(1m);
        summaries[0].ClosePrice.Should().Be(2007.7449m);
        summaries[0].Fee.Should().Be(2.22174714m);
        summaries[0].Pnl.Should().Be(21.8286437690909090909090909m);

        summaries[1].Direction.Should().Be("Open/Close Short");
        summaries[1].EntryPrice.Should().Be(1992.9480727272727272727272728m);
        summaries[1].Size.Should().Be(4m);
        summaries[1].ClosePrice.Should().Be(1993.3m);
        summaries[1].Fee.Should().Be(3.90012735m);
        summaries[1].Pnl.Should().Be(-5.3078364409090909090909088m);
    }

    [TestMethod]
    public void BuildTradeSummaries_SplitsIndependentCyclesBySymbol()
    {
        var entries = new[]
        {
            CreateEntry("2026-03-19T19:37:00Z", "SELL", 2m, "BTCUSDT", 100m, 0.50m, -2m),
            CreateEntry("2026-03-19T19:38:00Z", "BUY", 1m, "ETHUSDT", 50m, 0.20m, 1m),
            CreateEntry("2026-03-19T19:44:00Z", "BUY", 2m, "BTCUSDT", 90m, 0.60m, 0m),
            CreateEntry("2026-03-19T19:45:00Z", "SELL", 1m, "ETHUSDT", 55m, 0.25m, 0m)
        };

        var summaries = TradeSummaryBuilder.BuildTradeSummaries(entries);

        summaries.Should().HaveCount(2);
        summaries[0].Symbol.Should().Be("BTCUSDT");
        summaries[0].Direction.Should().Be("Open/Close Short");
        summaries[0].Pnl.Should().Be(18.90m);
        summaries[1].Symbol.Should().Be("ETHUSDT");
        summaries[1].Direction.Should().Be("Open/Close Long");
        summaries[1].Pnl.Should().Be(4.55m);
    }

    private static TradingHistoryEntry CreateEntry(string timestampUtc, string side, decimal size, string symbol, decimal price, decimal fee, decimal sizeAfter)
    {
        return new TradingHistoryEntry
        {
            Timestamp = ToUnixTimeMilliseconds(timestampUtc),
            TransactionType = "TRADE",
            Side = side,
            Size = size,
            Symbol = symbol,
            Price = price,
            Fee = fee,
            Calculated = new TradingTransactionCalculated
            {
                SizeAfter = sizeAfter
            }
        };
    }

    private static long ToUnixTimeMilliseconds(string timestampUtc)
    {
        return DateTimeOffset.Parse(timestampUtc, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
    }
}
