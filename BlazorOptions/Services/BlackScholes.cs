using System;

namespace BlazorOptions;

public class BlackScholes
{
    private const double RiskFreeRate = 0.0;

    public double CalculatePrice(double underlyingPrice, double strike, double impliedVolatility, DateTime expirationDate, bool isCall)
    {
        var adjustedUnderlying = Math.Max(underlyingPrice, 1e-6);
        var adjustedStrike = Math.Max(strike, 1e-6);
        var timeToExpiry = Math.Max((expirationDate - DateTime.UtcNow).TotalDays / 365.0, 0);
        var volatility = Math.Max(impliedVolatility / 100.0, 0);

        if (timeToExpiry <= 0 || volatility <= 0)
        {
            var intrinsic = Math.Max(isCall ? adjustedUnderlying - adjustedStrike : adjustedStrike - adjustedUnderlying, 0);
            return intrinsic;
        }

        var sqrtTime = Math.Sqrt(timeToExpiry);
        var d1 = (Math.Log(adjustedUnderlying / adjustedStrike) + (RiskFreeRate + 0.5 * volatility * volatility) * timeToExpiry) / (volatility * sqrtTime);
        var d2 = d1 - volatility * sqrtTime;
        var discountFactor = Math.Exp(-RiskFreeRate * timeToExpiry);

        return isCall
            ? adjustedUnderlying * StandardNormalCdf(d1) - adjustedStrike * discountFactor * StandardNormalCdf(d2)
            : adjustedStrike * discountFactor * StandardNormalCdf(-d2) - adjustedUnderlying * StandardNormalCdf(-d1);
    }

    private static double StandardNormalCdf(double x)
    {
        var sign = x < 0 ? -1 : 1;
        var absX = Math.Abs(x) / Math.Sqrt(2.0);

        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        var t = 1.0 / (1.0 + p * absX);
        var y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-absX * absX);

        return 0.5 * (1.0 + sign * y);
    }
}
