using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public class OptionChainTicker
{
    public OptionChainTicker(
        string symbol,
        string baseAsset,
        DateTime expirationDate,
        decimal strike,
        LegType type,
        decimal underlyingPrice,
        decimal markPrice,
        decimal lastPrice,
        decimal markIv,
        decimal bidPrice,
        decimal askPrice,
        decimal bidIv,
        decimal askIv,
        decimal? delta,
        decimal? gamma,
        decimal? vega,
        decimal? theta)
    {
        Symbol = symbol;
        BaseAsset = baseAsset;
        ExpirationDate = expirationDate;
        Strike = strike;
        Type = type;
        UnderlyingPrice = underlyingPrice;
        MarkPrice = markPrice;
        LastPrice = lastPrice;
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

    public decimal Strike { get; }

    public LegType Type { get; }

    public decimal? UnderlyingPrice { get; set; }
    public decimal MarkPrice { get; }

    public decimal LastPrice { get; }

    public decimal MarkIv { get; }

    public decimal BidPrice { get; }

    public decimal AskPrice { get; }

    public decimal BidIv { get; }

    public decimal AskIv { get; }

    public decimal? Delta { get; }

    public decimal? Gamma { get; }

    public decimal? Vega { get; }

    public decimal? Theta { get; }
  
}

