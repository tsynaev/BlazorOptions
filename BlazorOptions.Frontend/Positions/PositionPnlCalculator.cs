using BlazorOptions.API.Positions;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class PositionPnlCalculator
{
    private readonly OptionsService _optionsService;
    private readonly IExchangeService _exchangeService;

    public PositionPnlCalculator(OptionsService optionsService, IExchangeService exchangeService)
    {
        _optionsService = optionsService;
        _exchangeService = exchangeService;
    }

    public decimal ResolveEntryValue(IEnumerable<LegModel> legs)
    {
        decimal total = 0m;
        foreach (var leg in legs)
        {
            total += ResolveEntryValue(leg);
        }

        return total;
    }

    public decimal ResolveEntryValue(LegModel leg)
    {
        if (IsUnderlyingLegType(leg.Type) || !leg.Price.HasValue)
        {
            return 0m;
        }

        return Math.Abs(leg.Size * leg.Price.Value);
    }

    public decimal? ResolveBoundedMaxGain(IEnumerable<decimal> profits)
    {
        var values = profits as IReadOnlyList<decimal> ?? profits.ToArray();
        if (values.Count < 3)
        {
            return null;
        }

        decimal maxProfit = decimal.MinValue;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] > maxProfit)
            {
                maxProfit = values[i];
            }
        }

        if (maxProfit <= 0m)
        {
            return null;
        }

        for (var i = 1; i < values.Count - 1; i++)
        {
            if (Math.Abs(values[i] - maxProfit) <= 0.0001m)
            {
                return maxProfit;
            }
        }

        // Edge-only maxima indicate the sampled range likely clipped an unbounded payoff.
        return null;
    }

    public decimal? ResolveBoundedMaxGain(
        IReadOnlyList<LegModel> legs,
        BlazorChart.Models.ChartRange? range,
        decimal realizedPnl)
    {
        if (legs.Count == 0)
        {
            return null;
        }

        var (_, profits, _) = _optionsService.GeneratePosition(
            legs,
            points: 180,
            xMinOverride: range?.XMin,
            xMaxOverride: range?.XMax);

        return ResolveBoundedMaxGain(profits.Select(profit => profit + realizedPnl));
    }

    public decimal? ResolvePnlPercent(decimal? totalPnl, decimal? boundedMaxGain, decimal entryValue)
    {
        if (!totalPnl.HasValue)
        {
            return null;
        }

        var denominator = boundedMaxGain ?? entryValue;
        if (denominator <= 0m)
        {
            return null;
        }

        return totalPnl.Value / denominator * 100m;
    }

    public (decimal TotalPnl, decimal? PercentPnl) ResolvePnl(PositionModel positionModel, decimal indexPrice)
    {
        var preparedLegs = PrepareLegsForChart(positionModel);
        var entryValue = ResolveEntryValue(preparedLegs);
        var tempPnl = ResolveTempPnl(preparedLegs, indexPrice, positionModel.BaseAsset);
        var realizedPnl = positionModel.Closed.Include ? positionModel.Closed.TotalNet : 0m;
        var totalPnl = tempPnl + realizedPnl;
        var boundedMaxGain = ResolveBoundedMaxGain(preparedLegs, positionModel.ChartRange, realizedPnl);
        var percentPnl = ResolvePnlPercent(totalPnl, boundedMaxGain, entryValue);
        return (totalPnl, percentPnl);
    }

    public List<LegModel> PrepareLegsForChart(PositionModel positionModel)
    {
        return PrepareLegsForChart(positionModel.GetEffectiveLegs(), positionModel.BaseAsset);
    }

    public decimal ResolveTempPnl(PositionModel positionModel, decimal indexPrice)
    {
        var preparedLegs = PrepareLegsForChart(positionModel);
        return ResolveTempPnl(preparedLegs, indexPrice, positionModel.BaseAsset);
    }

    public (decimal EntryValue, decimal? MarkPrice, decimal Pnl, decimal? PnlPercent, decimal CurrentValue) ResolveLegSnapshot(
        LegModel leg,
        decimal indexPrice,
        string? baseAsset)
    {
        var entryValue = ResolveEntryValue(leg);
        var markPrice = ResolveLegMarkPrice(leg, indexPrice, baseAsset);
        var entry = leg.Price ?? 0m;
        var pnl = markPrice.HasValue
            ? (markPrice.Value - entry) * leg.Size
            : _optionsService.CalculateLegProfit(leg, indexPrice);

        decimal? pnlPercent = null;
        if (IsUnderlyingLegType(leg.Type))
        {
            pnlPercent = ResolveFuturesPnlPercent(leg, indexPrice);
        }
        else if (entryValue > 0m)
        {
            pnlPercent = pnl / entryValue * 100m;
        }

        var currentValue = markPrice.HasValue ? Math.Abs(leg.Size * markPrice.Value) : 0m;
        return (entryValue, markPrice, pnl, pnlPercent, currentValue);
    }

    private List<LegModel> PrepareLegsForChart(IReadOnlyList<LegModel> legs, string? baseAsset)
    {
        var prepared = new List<LegModel>(legs.Count);
        foreach (var leg in legs)
        {
            var clone = leg.Clone();
            if (!IsUnderlyingLegType(clone.Type))
            {
                var ticker = _exchangeService.OptionsChain.FindTickerForLeg(clone, baseAsset);
                if (ticker is not null)
                {
                    var ivPercent = NormalizeIv(ticker.MarkIv)
                        ?? NormalizeIv(ticker.BidIv)
                        ?? NormalizeIv(ticker.AskIv);

                    if ((!clone.ImpliedVolatility.HasValue || clone.ImpliedVolatility.Value <= 0m) && ivPercent.HasValue)
                    {
                        clone.ImpliedVolatility = ivPercent.Value;
                    }

                    if (!clone.Price.HasValue || clone.Price.Value <= 0m)
                    {
                        clone.Price = ResolveOptionMarkPrice(ticker);
                    }
                }
            }

            prepared.Add(clone);
        }

        return prepared;
    }

    private decimal ResolveTempPnl(IReadOnlyList<LegModel> legs, decimal currentPrice, string? baseAsset)
    {
        decimal total = 0m;
        foreach (var leg in legs)
        {
            total += ResolveLegPnl(leg, currentPrice, baseAsset);
        }

        return total;
    }

    private decimal ResolveLegPnl(LegModel leg, decimal currentPrice, string? baseAsset)
    {
        var mark = ResolveLegMarkPrice(leg, currentPrice, baseAsset);
        if (mark.HasValue)
        {
            var entry = leg.Price ?? 0m;
            return (mark.Value - entry) * leg.Size;
        }

        return _optionsService.CalculateLegProfit(leg, currentPrice);
    }

    private static decimal? ResolveFuturesPnlPercent(LegModel leg, decimal currentPrice)
    {
        if (!leg.Price.HasValue || leg.Price.Value == 0m)
        {
            return null;
        }

        var entry = leg.Price.Value;
        var movePercent = (currentPrice - entry) / entry * 100m;
        var side = Math.Sign(leg.Size);
        if (side == 0)
        {
            return null;
        }

        return movePercent * side;
    }

    private decimal? ResolveLegMarkPrice(LegModel leg, decimal currentPrice, string? baseAsset)
    {
        if (IsUnderlyingLegType(leg.Type))
        {
            return currentPrice;
        }

        return ResolveOptionMarkPrice(_exchangeService.OptionsChain.FindTickerForLeg(leg, baseAsset));
    }

    private static decimal? NormalizeIv(decimal? value)
    {
        if (!value.HasValue || value.Value <= 0m)
        {
            return null;
        }

        return value.Value <= 3m ? value.Value * 100m : value.Value;
    }

    private static decimal? ResolveOptionMarkPrice(OptionChainTicker? ticker)
    {
        if (ticker is null)
        {
            return null;
        }

        if (ticker.MarkPrice > 0)
        {
            return ticker.MarkPrice;
        }

        if (ticker.BidPrice > 0 && ticker.AskPrice > 0)
        {
            return (ticker.BidPrice + ticker.AskPrice) / 2m;
        }

        if (ticker.LastPrice > 0)
        {
            return ticker.LastPrice;
        }

        return null;
    }

    private static bool IsUnderlyingLegType(LegType type)
    {
        return type is LegType.Future or LegType.Spot;
    }
}
