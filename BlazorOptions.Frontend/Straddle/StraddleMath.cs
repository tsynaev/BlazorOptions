namespace BlazorOptions.ViewModels;

public static class StraddleMath
{
    public const double SQRT_2_PI = 0.7978845608;
    public const double RANGE_TO_SIGMA = 1.596d;

    public static double CalcRange(double hi, double lo, double close)
    {
        if (close <= 0d || hi <= 0d || lo <= 0d || hi < lo)
        {
            return 0d;
        }

        return (hi - lo) / close;
    }

    public static double CalcParkinson(double hi, double lo)
    {
        if (hi <= 0d || lo <= 0d || hi < lo)
        {
            return 0d;
        }

        var logTerm = Math.Log(hi / lo);
        return logTerm / Math.Sqrt(4d * Math.Log(2d));
    }

    public static double Avg(IEnumerable<double> values)
    {
        var list = values.Where(v => double.IsFinite(v)).ToList();
        return list.Count == 0 ? 0d : list.Average();
    }

    public static double FairStraddle(double s, double sigmaWeek)
    {
        if (s <= 0d || sigmaWeek <= 0d || !double.IsFinite(s) || !double.IsFinite(sigmaWeek))
        {
            return 0d;
        }

        // Fair weekly ATM straddle proxy uses expected absolute move E|dS| = S * sigma * sqrt(2/pi).
        return s * sigmaWeek * SQRT_2_PI;
    }
}
