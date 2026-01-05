using System;

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
    }
}
