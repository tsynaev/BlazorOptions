using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class PositionCardViewModel
{
    private readonly OptionsService _optionsService;
    private readonly PositionPnlCalculator _positionPnlCalculator;
    private readonly ExchangeSnapshotLegSyncService _exchangeSnapshotLegSyncService;
    private readonly ExchangeConnectionsService _exchangeConnectionsService;

    public PositionCardViewModel(
        OptionsService optionsService,
        PositionPnlCalculator positionPnlCalculator,
        ExchangeSnapshotLegSyncService exchangeSnapshotLegSyncService,
        ExchangeConnectionsService exchangeConnectionsService)
    {
        _optionsService = optionsService;
        _positionPnlCalculator = positionPnlCalculator;
        _exchangeSnapshotLegSyncService = exchangeSnapshotLegSyncService;
        _exchangeConnectionsService = exchangeConnectionsService;
    }

    public Guid Id { get; private set; }
    public string ExchangeConnectionId { get; private set; } = string.Empty;
    public string AssetPair { get; private set; } = string.Empty;
    public string ExchangeName { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public decimal TotalPnl { get; private set; }
    public decimal? PnlPercent { get; private set; }
    public IReadOnlyList<LegChipModel> QuickAddLegs { get; private set; } = Array.Empty<LegChipModel>();
    public string Notes { get; private set; } = string.Empty;
    public MiniChartModel Chart { get; private set; } = MiniChartModel.Empty;
    public IReadOnlyList<decimal> BreakEvens { get; private set; } = Array.Empty<decimal>();
    public GreekSummary GreekSummary { get; private set; } = new(0m, 0m, 0m, 0m);
    public decimal? CurrentPrice { get; private set; }
    public double? CurrentPricePercent { get; private set; }
    public bool IsChartReady { get; private set; }

    public async Task ApplyPositionAsync(
        PositionModel model,
        IExchangeService exchangeService,
        decimal? exchangePrice,
        bool withChart,
        bool includeTempLine,
        AccountRiskSettings riskSettings)
    {
        var effectiveModel = model.Clone();
        await ApplyExchangeSnapshotAsync(effectiveModel, exchangeService);
        await EnsureOptionChainAsync(effectiveModel, exchangeService);

        var legs = _positionPnlCalculator.PrepareLegsForChart(effectiveModel, exchangeService);
        var currentPrice = exchangePrice ?? ResolveFallbackCurrentPrice(effectiveModel.ChartRange, legs);
        var (totalPnl, pnlPercent) = _positionPnlCalculator.ResolvePnl(effectiveModel, currentPrice, exchangeService);
        var tempPnl = _positionPnlCalculator.ResolveTempPnl(effectiveModel, currentPrice, exchangeService);
        var theoreticalAtCurrent = legs.Count > 0
            ? _optionsService.CalculateTotalTheoreticalProfit(legs, currentPrice, DateTime.UtcNow)
            : 0m;
        var realizedPnl = effectiveModel.Closed.Include ? effectiveModel.Closed.TotalNet : 0m;
        var tempOffset = tempPnl - theoreticalAtCurrent;
        var chart = withChart
            ? BuildChart(legs, effectiveModel.ChartRange, realizedPnl, tempOffset, includeTempLine)
            : MiniChartModel.Empty;

        Id = effectiveModel.Id;
        ExchangeConnectionId = effectiveModel.ExchangeConnectionId ?? string.Empty;
        AssetPair = $"{effectiveModel.BaseAsset}/{effectiveModel.QuoteAsset}";
        ExchangeName = _exchangeConnectionsService.GetDisplayName(effectiveModel.ExchangeConnectionId);
        Name = effectiveModel.Name;
        TotalPnl = totalPnl;
        PnlPercent = pnlPercent;
        QuickAddLegs = legs.Select(leg => BuildLegChip(leg, currentPrice, effectiveModel.BaseAsset, exchangeService, riskSettings)).ToArray();
        Notes = effectiveModel.Notes;
        Chart = chart;
        BreakEvens = BuildBreakEvensOnly(legs, effectiveModel.ChartRange, realizedPnl);
        GreekSummary = BuildGreekSummary(legs, effectiveModel.BaseAsset, exchangeService);
        CurrentPrice = exchangePrice;
        CurrentPricePercent = withChart && exchangePrice.HasValue
            ? ResolveCurrentPricePercent(currentPrice, chart.XMin, chart.XMax)
            : null;
        IsChartReady = withChart;
    }

    private async Task ApplyExchangeSnapshotAsync(PositionModel model, IExchangeService exchangeService)
    {
        var positionsTask = exchangeService.Positions.GetPositionsAsync();
        var ordersTask = exchangeService.Orders.GetOpenOrdersAsync();
        await Task.WhenAll(positionsTask, ordersTask);

        var positions = positionsTask.Result?.ToArray() ?? Array.Empty<ExchangePosition>();
        var orders = ordersTask.Result?.ToArray() ?? Array.Empty<ExchangeOrder>();
        if (positions.Length == 0 && orders.Length == 0)
        {
            return;
        }

        var baseAsset = model.BaseAsset?.Trim();
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return;
        }

        var activeLegs = positions
            .Select(position => _exchangeSnapshotLegSyncService.CreateLegFromExchangePosition(position, baseAsset, position.Category, include: true))
            .Where(leg => leg is not null)
            .Cast<LegModel>()
            .ToArray();

        var orderLegs = orders
            .Select(order => _exchangeSnapshotLegSyncService.CreateLegFromExchangeOrder(order, baseAsset, include: false))
            .Where(leg => leg is not null && !string.IsNullOrWhiteSpace(leg.ReferenceId))
            .Cast<LegModel>()
            .ToArray();

        _exchangeSnapshotLegSyncService.SyncOrderLegs(model.Legs, orderLegs, activeLegs);
        _exchangeSnapshotLegSyncService.SyncReadOnlyLegs(model.Legs, activeLegs);
    }

    private async Task EnsureOptionChainAsync(PositionModel model, IExchangeService exchangeService)
    {
        var baseAsset = model.BaseAsset?.Trim();
        if (string.IsNullOrWhiteSpace(baseAsset)
            || !model.Legs.Any(leg => leg.Type is not (LegType.Future or LegType.Spot)))
        {
            return;
        }

        try
        {
            await exchangeService.OptionsChain.EnsureTickersForBaseAssetAsync(baseAsset);
        }
        catch
        {
            // Card rendering should stay resilient when option-chain refresh fails.
        }
    }

    private IReadOnlyList<decimal> BuildBreakEvensOnly(
        IReadOnlyList<LegModel> legs,
        BlazorChart.Models.ChartRange? range,
        decimal pnlShift)
    {
        if (!ShouldShowDashboardBreakEvens(legs, DateTime.UtcNow))
        {
            return Array.Empty<decimal>();
        }

        var (xs, profits, _) = _optionsService.GeneratePosition(
            legs,
            points: 120,
            xMinOverride: range?.XMin,
            xMaxOverride: range?.XMax);

        var breakEvens = ResolveBreakEvens(xs, profits, pnlShift);
        if (breakEvens.Count > 0)
        {
            return breakEvens;
        }

        var (fullXs, fullProfits, _) = _optionsService.GeneratePosition(legs, points: 240);
        return ResolveBreakEvens(fullXs, fullProfits, pnlShift);
    }

    private GreekSummary BuildGreekSummary(IReadOnlyList<LegModel> legs, string? baseAsset, IExchangeService exchangeService)
    {
        decimal totalDelta = 0m;
        decimal totalGamma = 0m;
        decimal totalVega = 0m;
        decimal totalTheta = 0m;

        foreach (var leg in legs)
        {
            if (leg.Type is LegType.Future or LegType.Spot)
            {
                totalDelta += leg.Size;
                continue;
            }

            var ticker = exchangeService.OptionsChain.FindTickerForLeg(leg, baseAsset);
            if (ticker is null)
            {
                continue;
            }

            if (ticker.Delta.HasValue)
            {
                totalDelta += ticker.Delta.Value * leg.Size;
            }

            if (ticker.Gamma.HasValue)
            {
                totalGamma += ticker.Gamma.Value * leg.Size;
            }

            if (ticker.Vega.HasValue)
            {
                totalVega += ticker.Vega.Value * leg.Size;
            }

            if (ticker.Theta.HasValue)
            {
                totalTheta += ticker.Theta.Value * leg.Size;
            }
        }

        return new GreekSummary(totalDelta, totalGamma, totalVega, totalTheta);
    }

    private MiniChartModel BuildChart(
        IReadOnlyList<LegModel> legs,
        BlazorChart.Models.ChartRange? range,
        decimal pnlShift,
        decimal tempOffset,
        bool includeTempLine)
    {
        var (xs, profits, theoreticalProfits) = _optionsService.GeneratePosition(
            legs,
            points: 48,
            xMinOverride: range?.XMin,
            xMaxOverride: range?.XMax);

        var labels = xs.Select(value => Math.Round(value, 0).ToString("0")).ToArray();
        IReadOnlyList<string> sparseLabels = BuildSparseLabels(labels, 8);
        var values = profits.Select(value => (double)(value + pnlShift)).ToArray();
        var tempValues = includeTempLine
            ? theoreticalProfits.Select(value => (double)(value + pnlShift + tempOffset)).ToArray()
            : Array.Empty<double>();
        var breakEvens = ShouldShowDashboardBreakEvens(legs, DateTime.UtcNow)
            ? ResolveBreakEvens(xs, profits, pnlShift)
            : Array.Empty<decimal>();

        return new MiniChartModel(sparseLabels, values, tempValues, (double)xs[0], (double)xs[^1], breakEvens, includeTempLine);
    }

    private LegChipModel BuildLegChip(LegModel leg, decimal currentPrice, string? baseAsset, IExchangeService exchangeService, AccountRiskSettings riskSettings)
    {
        var snapshot = _positionPnlCalculator.ResolveLegSnapshot(leg, currentPrice, baseAsset, exchangeService);
        var severityClass = ResolveLegSeverityClass(leg.Type, snapshot.PnlPercent, riskSettings);
        return new LegChipModel(
            FormatQuickAdd(leg),
            leg.ExpirationDate,
            snapshot.PnlPercent,
            severityClass,
            leg.Symbol,
            leg.Type,
            leg.Size,
            leg.Price,
            snapshot.MarkPrice,
            snapshot.Pnl,
            snapshot.EntryValue,
            snapshot.CurrentValue);
    }

    private static bool ShouldShowDashboardBreakEvens(IReadOnlyList<LegModel> legs, DateTime nowUtc)
    {
        if (legs.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            if (!leg.ExpirationDate.HasValue || leg.ExpirationDate.Value > nowUtc)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<decimal> ResolveBreakEvens(IReadOnlyList<decimal> xs, IReadOnlyList<decimal> profits, decimal pnlShift)
    {
        var result = new List<decimal>();
        if (xs.Count == 0 || profits.Count == 0)
        {
            return result;
        }

        var count = Math.Min(xs.Count, profits.Count);
        var step = count > 1 ? Math.Abs(xs[count - 1] - xs[0]) / (count - 1) : 1m;
        var epsilonX = Math.Max(0.01m, step * 0.5m);

        var maxAbsY = 0m;
        for (var i = 0; i < count; i++)
        {
            var y = Math.Abs(profits[i] + pnlShift);
            if (y > maxAbsY)
            {
                maxAbsY = y;
            }
        }

        var epsilonY = Math.Max(0.01m, maxAbsY * 0.0005m);

        for (var i = 0; i < count - 1; i++)
        {
            var x0 = xs[i];
            var x1 = xs[i + 1];
            var y0 = profits[i] + pnlShift;
            var y1 = profits[i + 1] + pnlShift;

            if (Math.Abs(y0) <= epsilonY)
            {
                AddDistinctBreakEven(result, x0, epsilonX);
                continue;
            }

            if (Math.Abs(y1) <= epsilonY)
            {
                AddDistinctBreakEven(result, x1, epsilonX);
                continue;
            }

            if ((y0 < 0m && y1 > 0m) || (y0 > 0m && y1 < 0m))
            {
                var slope = y1 - y0;
                if (Math.Abs(slope) < 0.0000001m)
                {
                    continue;
                }

                var t = -y0 / slope;
                var breakeven = x0 + (x1 - x0) * t;
                AddDistinctBreakEven(result, breakeven, epsilonX);
            }
        }

        return result.OrderBy(value => value).ToArray();
    }

    private static void AddDistinctBreakEven(List<decimal> list, decimal value, decimal epsilonX)
    {
        var rounded = decimal.Round(value, 2);
        for (var i = 0; i < list.Count; i++)
        {
            if (Math.Abs(list[i] - rounded) <= epsilonX)
            {
                return;
            }
        }

        list.Add(rounded);
    }

    private static double? ResolveCurrentPricePercent(decimal currentPrice, double xMin, double xMax)
    {
        if (xMax <= xMin)
        {
            return null;
        }

        var percent = ((double)currentPrice - xMin) / (xMax - xMin) * 100d;
        return Math.Clamp(percent, 0d, 100d);
    }

    private static string FormatQuickAdd(LegModel leg)
    {
        var sign = leg.Size >= 0 ? "+" : "-";
        var size = Math.Abs(leg.Size).ToString("0.####");

        return leg.Type switch
        {
            LegType.Future => $"{sign}{size} F",
            LegType.Spot => $"{sign}{size} S",
            LegType.Call => $"{sign}{size} C {(leg.Strike ?? 0):0.####}",
            LegType.Put => $"{sign}{size} P {(leg.Strike ?? 0):0.####}",
            _ => $"{sign}{size}"
        };
    }

    private static string ResolveLegSeverityClass(LegType type, decimal? pnlPercent, AccountRiskSettings riskSettings)
    {
        if (!pnlPercent.HasValue)
        {
            return "home-leg-neutral";
        }

        if (pnlPercent.Value >= 0m)
        {
            return "home-leg-profit";
        }

        var loss = Math.Abs(pnlPercent.Value);
        var maxLoss = type is LegType.Future or LegType.Spot
            ? Math.Max(1m, riskSettings.MaxLossFuturesPercent)
            : Math.Max(1m, riskSettings.MaxLossOptionPercent);

        if (loss >= maxLoss)
        {
            return "home-leg-critical";
        }

        if (loss >= maxLoss * 0.66m)
        {
            return "home-leg-high";
        }

        return "home-leg-low";
    }

    private static decimal ResolveFallbackCurrentPrice(BlazorChart.Models.ChartRange? range, IReadOnlyList<LegModel> legs)
    {
        if (range is not null && range.XMax > range.XMin)
        {
            return (decimal)((range.XMin + range.XMax) / 2d);
        }

        var anchor = legs.Count > 0
            ? legs.Average(l => l.Strike.HasValue && l.Strike.Value > 0 ? l.Strike.Value : (l.Price ?? 0m))
            : 0m;

        return anchor > 0m ? anchor : 0m;
    }

    private static IReadOnlyList<string> BuildSparseLabels(IReadOnlyList<string> labels, int maxVisible)
    {
        if (labels.Count <= maxVisible || maxVisible <= 0)
        {
            return labels;
        }

        var step = Math.Max(1, (int)Math.Ceiling(labels.Count / (double)maxVisible));
        var sparse = new string[labels.Count];
        for (var i = 0; i < labels.Count; i++)
        {
            sparse[i] = i == 0 || i == labels.Count - 1 || i % step == 0
                ? labels[i]
                : string.Empty;
        }

        return sparse;
    }
}

public sealed class PositionCardViewModelFactory
{
    private readonly OptionsService _optionsService;
    private readonly PositionPnlCalculator _positionPnlCalculator;
    private readonly ExchangeSnapshotLegSyncService _exchangeSnapshotLegSyncService;
    private readonly ExchangeConnectionsService _exchangeConnectionsService;

    public PositionCardViewModelFactory(
        OptionsService optionsService,
        PositionPnlCalculator positionPnlCalculator,
        ExchangeSnapshotLegSyncService exchangeSnapshotLegSyncService,
        ExchangeConnectionsService exchangeConnectionsService)
    {
        _optionsService = optionsService;
        _positionPnlCalculator = positionPnlCalculator;
        _exchangeSnapshotLegSyncService = exchangeSnapshotLegSyncService;
        _exchangeConnectionsService = exchangeConnectionsService;
    }

    public async Task<PositionCardViewModel> CreateAsync(
        PositionModel model,
        IExchangeService exchangeService,
        decimal? exchangePrice,
        bool withChart,
        bool includeTempLine,
        AccountRiskSettings riskSettings)
    {
        var viewModel = new PositionCardViewModel(
            _optionsService,
            _positionPnlCalculator,
            _exchangeSnapshotLegSyncService,
            _exchangeConnectionsService);
        await viewModel.ApplyPositionAsync(model, exchangeService, exchangePrice, withChart, includeTempLine, riskSettings);
        return viewModel;
    }
}

public sealed record MiniChartModel(
    IReadOnlyList<string> XLabels,
    IReadOnlyList<double> Values,
    IReadOnlyList<double> TempValues,
    double XMin,
    double XMax,
    IReadOnlyList<decimal> BreakEvens,
    bool HasTempLine)
{
    public static MiniChartModel Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<double>(),
        Array.Empty<double>(),
        0d,
        0d,
        Array.Empty<decimal>(),
        false);
}

public sealed record GreekSummary(
    decimal TotalDelta,
    decimal TotalGamma,
    decimal TotalVega,
    decimal TotalTheta);

public sealed record LegChipModel(
    string Text,
    DateTime? ExpirationDate,
    decimal? PnlPercent,
    string SeverityClass,
    string? Symbol,
    LegType Type,
    decimal Size,
    decimal? EntryPrice,
    decimal? MarkPrice,
    decimal Pnl,
    decimal EntryValue,
    decimal CurrentValue);

