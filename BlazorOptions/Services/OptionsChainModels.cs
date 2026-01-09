using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public class OptionChainTicker
{
    public OptionChainTicker(
        string symbol,
        string baseAsset,
        DateTime expirationDate,
        double strike,
        OptionLegType type,
        double markPrice,
        double markIv,
        double? delta,
        double? gamma,
        double? vega,
        double? theta)
    {
        Symbol = symbol;
        BaseAsset = baseAsset;
        ExpirationDate = expirationDate;
        Strike = strike;
        Type = type;
        MarkPrice = markPrice;
        MarkIv = markIv;
        Delta = delta;
        Gamma = gamma;
        Vega = vega;
        Theta = theta;
    }

    public string Symbol { get; }

    public string BaseAsset { get; }

    public DateTime ExpirationDate { get; }

    public double Strike { get; }

    public OptionLegType Type { get; }

    public double MarkPrice { get; }

    public double MarkIv { get; }

    public double? Delta { get; }

    public double? Gamma { get; }

    public double? Vega { get; }

    public double? Theta { get; }
}
