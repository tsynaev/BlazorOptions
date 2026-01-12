using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public class OptionChainTicker
{
    public OptionChainTicker(
        string symbol,
        string baseAsset,
        DateTime expirationDate,
        double strike,
        LegType type,
        double markPrice,
        double markIv,
        double bidPrice,
        double askPrice,
        double bidIv,
        double askIv,
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
        BidPrice = bidPrice;
        AskPrice = askPrice;
        BidIv = bidIv;
        AskIv = askIv;
        Delta = delta;
        Gamma = gamma;
        Vega = vega;
        Theta = theta;
    }

    public string Symbol { get; }

    public string BaseAsset { get; }

    public DateTime ExpirationDate { get; }

    public double Strike { get; }

    public LegType Type { get; }

    public double MarkPrice { get; }

    public double MarkIv { get; }

    public double BidPrice { get; }

    public double AskPrice { get; }

    public double BidIv { get; }

    public double AskIv { get; }

    public double? Delta { get; }

    public double? Gamma { get; }

    public double? Vega { get; }

    public double? Theta { get; }
}
