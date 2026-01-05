using System;
using System.Collections.Generic;
using System.Linq;
using BlazorOptions.ViewModels;

namespace BlazorOptions
{
    // Simple helper that generates payoff points for an options long call
    public class OptionsService
    {
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

        public (double[] xs, double[] profits) GeneratePosition(IEnumerable<OptionLegModel> legs, int points = 200)
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

            var xs = new double[points];
            var profits = new double[points];
            var step = (end - start) / (points - 1);

            for (int i = 0; i < points; i++)
            {
                var s = start + step * i;
                xs[i] = s;
                profits[i] = CalculateProfitForPrice(activeLegs, s);
            }

            return (xs, profits);
        }

        private static double CalculateProfitForPrice(IEnumerable<OptionLegModel> legs, double underlyingPrice)
        {
            double total = 0;

            foreach (var leg in legs)
            {
                switch (leg.Type)
                {
                    case OptionLegType.Call:
                        total += (Math.Max(underlyingPrice - leg.Strike, 0) - leg.Price) * leg.Size;
                        break;
                    case OptionLegType.Put:
                        total += (Math.Max(leg.Strike - underlyingPrice, 0) - leg.Price) * leg.Size;
                        break;
                    case OptionLegType.Future:
                        total += (underlyingPrice - leg.Price) * leg.Size;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            return total;
        }
    }
}
