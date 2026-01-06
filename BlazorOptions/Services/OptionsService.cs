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

        public (double[] xs, double[] profits, double[] theoreticalProfits) GeneratePosition(IEnumerable<OptionLegModel> legs, int points = 200)
        {
            var activeLegs = legs.Where(l => l.IsIncluded).ToList();
            var anchor = activeLegs.Count > 0
                ? activeLegs.Average(l => l.Strike > 0 ? l.Strike : l.Price)
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

            for (int i = 0; i < count; i++)
            {
                var s = start + adjustedStep * i;
                xs[i] = Math.Round(s, 2);
                profits[i] = CalculateTotalProfit(activeLegs, s);
                theoreticalProfits[i] = CalculateTotalTheoreticalProfit(activeLegs, s);
            }

            return (xs, profits, theoreticalProfits);
        }

        public double CalculateLegProfit(OptionLegModel leg, double underlyingPrice)
        {
            return leg.Type switch
            {
                OptionLegType.Call => (Math.Max(underlyingPrice - leg.Strike, 0) - leg.Price) * leg.Size,
                OptionLegType.Put => (Math.Max(leg.Strike - underlyingPrice, 0) - leg.Price) * leg.Size,
                OptionLegType.Future => (underlyingPrice - leg.Price) * leg.Size,
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        public double CalculateLegTheoreticalProfit(OptionLegModel leg, double underlyingPrice)
        {
            return leg.Type switch
            {
                OptionLegType.Call => (_blackScholes.CalculatePrice(underlyingPrice, leg.Strike, leg.ImpliedVolatility, leg.ExpirationDate, true) - leg.Price) * leg.Size,
                OptionLegType.Put => (_blackScholes.CalculatePrice(underlyingPrice, leg.Strike, leg.ImpliedVolatility, leg.ExpirationDate, false) - leg.Price) * leg.Size,
                OptionLegType.Future => (underlyingPrice - leg.Price) * leg.Size,
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        public double CalculateTotalTheoreticalProfit(IEnumerable<OptionLegModel> legs, double underlyingPrice)
        {
            double total = 0;

            foreach (var leg in legs)
            {
                total += CalculateLegTheoreticalProfit(leg, underlyingPrice);
            }

            return total;
        }

        public double CalculateTotalProfit(IEnumerable<OptionLegModel> legs, double underlyingPrice)
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
