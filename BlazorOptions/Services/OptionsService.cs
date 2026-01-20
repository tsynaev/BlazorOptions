using System;
using System.Collections.Generic;
using System.Linq;
using BlazorOptions.ViewModels;

namespace BlazorOptions
{
    // Simple helper that generates payoff points for an options long call
    public class OptionsService
    {
        private readonly BlackScholes _blackScholes;

        public OptionsService(BlackScholes blackScholes)
        {
            _blackScholes = blackScholes;
        }

        // Generates underlying price points (xs) and profit values (for a long call)
        // profit = max(S - K, 0) - premium, multiplied by quantity
        public (double[] xs, double[] profits) GenerateLongCall(double strike, double premium, double quantity = 1, int points = 100)
        {
            if (points < 10) points = 10;

            // range around strike (50% below to 50% above)
            var start = Math.Max(0, strike * 0.5);
            var end = Math.Max(strike + 1, strike * 1.5);
            var xs = new double[points];
            var profits = new double[points];
            var step = (end - start) / (points - 1);

            for (int i = 0; i < points; i++)
            {
                var s = start + step * i;
                xs[i] = s;
                var payoff = Math.Max(s - strike, 0.0) - premium;
                profits[i] = payoff * quantity;
            }

            return (xs, profits);
        }

        public (double[] xs, double[] profits, double[] theoreticalProfits) GeneratePosition(IEnumerable<LegModel> legs, int points = 200, DateTime? valuationDate = null)
        {
            var activeLegs = legs.Where(l => l.IsIncluded).ToList();
            var anchor = activeLegs.Count > 0
                ? activeLegs.Average(l => l.Strike.HasValue && l.Strike.Value > 0 ? l.Strike.Value : (l.Price ?? 0))
                : 1000;

            var start = Math.Max(0, anchor * 0.5);
            var end = Math.Max(anchor + 1, anchor * 1.5);

            if (points < 20)
            {
                points = 20;
            }

            var roughStep = (end - start) / (points - 1);
            var niceStep = CalculateNiceStep(roughStep);

            start = Math.Floor(start / niceStep) * niceStep;
            end = Math.Ceiling(end / niceStep) * niceStep;

            var steps = Math.Max(20, (int)Math.Min(points - 1, Math.Ceiling((end - start) / niceStep)));
            var adjustedStep = (end - start) / steps;
            var count = steps + 1;

            var xs = new double[count];
            var profits = new double[count];
            var theoreticalProfits = new double[count];
            var evaluationDate = valuationDate ?? DateTime.UtcNow;

            for (int i = 0; i < count; i++)
            {
                var s = start + adjustedStep * i;
                xs[i] = Math.Round(s, 2);
                profits[i] = CalculateTotalProfit(activeLegs, s);
                theoreticalProfits[i] = CalculateTotalTheoreticalProfit(activeLegs, s, evaluationDate);
            }

            return (xs, profits, theoreticalProfits);
        }

        public double CalculateLegProfit(LegModel leg, double underlyingPrice)
        {
            var strike = leg.Strike ?? 0;
            var entryPrice = leg.Price ?? 0;
            return leg.Type switch
            {
                LegType.Call => (Math.Max(underlyingPrice - strike, 0) - entryPrice) * leg.Size,
                LegType.Put => (Math.Max(strike - underlyingPrice, 0) - entryPrice) * leg.Size,
                LegType.Future => (underlyingPrice - entryPrice) * leg.Size,
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        public double CalculateLegTheoreticalProfit(LegModel leg, double underlyingPrice, DateTime? valuationDate = null)
        {
            var evaluationDate = valuationDate ?? DateTime.UtcNow;
            var strike = leg.Strike;
            var expiration = leg.ExpirationDate;
            var impliedVolatility = leg.ImpliedVolatility;

            var entryPrice = leg.Price ?? 0;
            return leg.Type switch
            {
                LegType.Call when strike.HasValue && expiration.HasValue && impliedVolatility.HasValue =>
                    (_blackScholes.CalculatePrice(underlyingPrice, strike.Value, impliedVolatility.Value, expiration.Value, true, evaluationDate) - entryPrice) * leg.Size,
                LegType.Put when strike.HasValue && expiration.HasValue && impliedVolatility.HasValue =>
                    (_blackScholes.CalculatePrice(underlyingPrice, strike.Value, impliedVolatility.Value, expiration.Value, false, evaluationDate) - entryPrice) * leg.Size,
                LegType.Future => (underlyingPrice - entryPrice) * leg.Size,
                _ => 0
            };
        }

        public double CalculateTotalTheoreticalProfit(IEnumerable<LegModel> legs, double underlyingPrice, DateTime? valuationDate = null)
        {
            var evaluationDate = valuationDate ?? DateTime.UtcNow;
            double total = 0;

            foreach (var leg in legs)
            {
                total += CalculateLegTheoreticalProfit(leg, underlyingPrice, evaluationDate);
            }

            return total;
        }

        public double CalculateTotalProfit(IEnumerable<LegModel> legs, double underlyingPrice)
        {
            double total = 0;

            foreach (var leg in legs)
            {
                total += CalculateLegProfit(leg, underlyingPrice);
            }

            return total;
        }

        private static double CalculateNiceStep(double step)
        {
            if (step <= 0)
            {
                return 1;
            }

            var exponent = Math.Floor(Math.Log10(step));
            var fraction = step / Math.Pow(10, exponent);
            double niceFraction;

            if (fraction <= 1)
            {
                niceFraction = 1;
            }
            else if (fraction <= 2)
            {
                niceFraction = 2;
            }
            else if (fraction <= 5)
            {
                niceFraction = 5;
            }
            else
            {
                niceFraction = 10;
            }

            return niceFraction * Math.Pow(10, exponent);
        }
    }
}
