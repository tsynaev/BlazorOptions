using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class LegViewModel : IDisposable
{
    private readonly LegsCollectionViewModel _collectionViewModel;
    private readonly OptionsService _optionsService;
    private readonly OptionsChainService _optionsChainService;
    private readonly ExchangeTickerService _exchangeTicker;
    private readonly IExchangeService _exchangeService;
    private readonly ITelemetryService _telemetryService;
    private readonly SemaphoreSlim _subscriptionLock = new(1, 1);
    private IDisposable? _subscriptionRegistration;
    private decimal? _currentPrice;
    private bool _isLive;
 
    private LegModel? _leg;
    private List<DateTime?> _cachedExpirations = new List<DateTime?>();
    private IReadOnlyList<decimal> _cachedStrikes = Array.Empty<decimal>();
    private DateTime _valuationDate;
 


    public LegViewModel(
        LegsCollectionViewModel collectionViewModel,
        OptionsService optionsService,
        OptionsChainService optionsChainService,
        ExchangeTickerService exchangeTicker,
        IExchangeService exchangeService,
        ITelemetryService telemetryService)
    {
        _collectionViewModel = collectionViewModel;
        _optionsService = optionsService;
        _optionsChainService = optionsChainService;
        _exchangeTicker = exchangeTicker;
        _exchangeService = exchangeService;
        _telemetryService = telemetryService;
    }

    public LegModel Leg
    {
        get => _leg ?? throw new InvalidOperationException("Leg is not set.");
        set
        {
            _leg = value;

            RefreshExpDatesAndStrikes();
            _ = RefreshSubscriptionAsync();
        }
    }

    public IEnumerable<LegType> LegTypes => Enum.GetValues<LegType>();

    public event Action? Changed;
    public event Func<Task>? Removed;

    public decimal? PlaceholderPrice => Leg.Price.HasValue ? null : GetEntryPriceSuggestion();

    public decimal? Bid { get; private set; }
    public decimal? Ask { get; private set; }

    public decimal? MarkPrice { get; private set; }

    public decimal? ChainIv { get; private set; }

    public decimal? TempPnl { get; private set; }

 

    public decimal? TempPnlPercent => ResolveTempPnlPercent();

    public IReadOnlyList<DateTime?> AvailableExpirations => _cachedExpirations;

    public IReadOnlyList<decimal> AvailableStrikes => _cachedStrikes;

    public decimal? CurrentPrice
    {
        get => _currentPrice;
        set
        {
            if (_currentPrice == value)
            {
                return;
            }

            _currentPrice = value;

            CalculateLegTheoreticalProfit();

            Changed?.Invoke();
        }
    }

    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (_isLive == value)
            {
                return;
            }

            _isLive = value;
            if (!_isLive)
            {
                _subscriptionRegistration?.Dispose();
                _subscriptionRegistration = null;
            }
            else
            {
                CalculateLegTheoreticalProfit();
                _ = RefreshSubscriptionAsync();
            }
            Changed?.Invoke();
        }
    }

    public DateTime ValuationDate
    {
        get => _valuationDate;
        set
        {
            if (_valuationDate == value)
            {
                return;
            }

            _valuationDate = value;

            CalculateLegTheoreticalProfit();
            Changed?.Invoke();
        }
    }

    public void UpdateLegIvAsync(decimal? iv)
    {
        using var activity = _telemetryService.StartActivity("LegViewModel.UpdateLegIv");
        if (Leg.IsReadOnly)
        {
            return;
        }

        Leg.ImpliedVolatility = iv;
    }


    public void UpdateLegIncludedAsync(bool include)
    {
        using var activity = _telemetryService.StartActivity("LegViewModel.UpdateIncluded");
        Leg.IsIncluded = include;
        Changed?.Invoke();
    }

    public void UpdateLegTypeAsync(LegType type)
    {
        using var activity = _telemetryService.StartActivity("LegViewModel.UpdateType");
        if (Leg.IsReadOnly)
        {
            return;
        }

        Leg.Type = type;
        RefreshExpDatesAndStrikes();
        Changed?.Invoke();
    }

    public void UpdateLegStrikeAsync(decimal? strike)
    {
        using var activity = _telemetryService.StartActivity("LegViewModel.UpdateStrike");
        if (Leg.IsReadOnly)
        {
            return;
        }

        Leg.Strike = strike;
        Changed?.Invoke();
    }

    public void UpdateLegSizeAsync(decimal size)
    {
        using var activity = _telemetryService.StartActivity("LegViewModel.UpdateSize");
        if (Leg.IsReadOnly)
        {
            return;
        }

        Leg.Size = size;
        Changed?.Invoke();
    }

    public void UpdateLegExpirationAsync(DateTime? date)
    {
        using var activity = _telemetryService.StartActivity("LegViewModel.UpdateExpiration");

        if (Leg.IsReadOnly)
        {
            return;
        }

        if (date.HasValue)
        {
            Leg.ExpirationDate = date.Value;
        }

        RefreshExpDatesAndStrikes();
        Changed?.Invoke();
    }

    public async Task UpdateLegPriceAsync(decimal? price)
    {
        using var activity = _telemetryService.StartActivity("LegViewModel.UpdatePrice");
        if (Leg.IsReadOnly)
        {
            return;
        }

        Leg.Price = price;

        Changed?.Invoke();
    }

    public async Task RemoveLegAsync()
    {
        using var activity = _telemetryService.StartActivity("LegViewModel.RemoveLeg");
        await _collectionViewModel.RemoveLegAsync(Leg);
        if (Removed is not null)
        {
            await Removed.Invoke();
        }
    }

    public void UpdateLeg(LegModel leg)
    {
        _leg = leg;

        _ = RefreshSubscriptionAsync();
    }

    public bool Update(BybitPosition position)
    {
        if (position is null || string.IsNullOrWhiteSpace(position.Symbol))
        {
            return false;
        }

        if (!Leg.IsReadOnly)
        {
            return false;
        }

        var symbol = Leg.Symbol;
        if (!string.Equals(symbol, position.Symbol, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var updated = false;
        var signedSize = DetermineSignedSize(position);

        if (Math.Abs(Leg.Size - signedSize) > 0.0001m)
        {
            Leg.Size = signedSize;
            updated = true;
        }

        if (!Leg.Price.HasValue || Math.Abs(Leg.Price.Value - position.AvgPrice) > 0.0001m)
        {
            Leg.Price = position.AvgPrice;
            updated = true;
        }

        if (string.IsNullOrWhiteSpace(Leg.Symbol))
        {
            Leg.Symbol = position.Symbol;
            updated = true;
        }

        if (updated)
        {
            Changed?.Invoke();
        }

        return updated;
    }

    public void Dispose()
    {
        _subscriptionRegistration?.Dispose();
    }


   
    private async Task RefreshSubscriptionAsync()
    {
        using var activity = _telemetryService.StartActivity("LegViewModel.RefreshSubscription");
        if (_leg is null)
        {
            return;
        }

        if (!_isLive)
        {
            return;
        }

        await _subscriptionLock.WaitAsync();
        try
        {

            _subscriptionRegistration?.Dispose();
            _subscriptionRegistration = null;

            if (string.IsNullOrWhiteSpace(Leg.Symbol))
            {
                return;
            }

            if (Leg.Type == LegType.Future)
            {
                _subscriptionRegistration = await _exchangeTicker.SubscribeAsync(Leg.Symbol, HandleLinearTicker);
            }
            else
            {

                _subscriptionRegistration = await _optionsChainService.SubscribeAsync(Leg.Symbol, HandleOptionTicker);
            }
        }
        finally
        {
            _subscriptionLock.Release();
        }
    }

    private Task HandleLinearTicker(ExchangePriceUpdate e)
    {

        TempPnl = ResolveFuturesPnl(e.Price);

        Changed?.Invoke();

        return Task.CompletedTask;
    }

    private async Task HandleOptionTicker(OptionChainTicker ticker)
    {
        if (!_isLive)
        {
            return;
        }

        Bid = ticker.BidPrice > 0 ? ticker.BidPrice : null;
        Ask = ticker.AskPrice > 0 ? ticker.AskPrice : null;
        MarkPrice = ticker.MarkPrice > 0 ? ticker.MarkPrice : null;
        ChainIv = NormalizeIv(ticker.MarkIv)
            ?? NormalizeIv(ticker.BidIv)
            ?? NormalizeIv(ticker.AskIv);

        if (!Leg.ImpliedVolatility.HasValue || Leg.ImpliedVolatility.Value <= 0)
        {
            if (ChainIv.HasValue && ChainIv.Value > 0)
            {
                Leg.ImpliedVolatility = ChainIv.Value;
            }
        }

        TempPnl = ResolveOptionTempPnl(ticker.UnderlyingPrice);

        Changed?.Invoke();
    }



    private void RefreshExpDatesAndStrikes()
    {
        using var activity = _telemetryService.StartActivity("LegViewModel.RefreshExpDatesAndStrikes");

        var tickers = _optionsChainService.GetTickersByBaseAsset(_collectionViewModel.BaseAsset, Leg.Type);

        _cachedExpirations = tickers.Select(ticker => (DateTime?)ticker.ExpirationDate.Date)
            .Distinct()
            .OrderBy(item => item)
            .ToList();

        if (Leg.Type == LegType.Future)
        {
            _cachedExpirations.Insert(0, null);

            _cachedStrikes = Array.Empty<decimal>();

            return;
        }


        _cachedStrikes = tickers
            .Where(ticker => ticker.ExpirationDate.Date == Leg.ExpirationDate?.Date)
            .Select(ticker => ticker.Strike)
            .Distinct()
            .OrderBy(item => item)
            .ToList();


    }


  
    
    private static string FormatExpiration(DateTime date)
    {
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string FormatStrike(double strike)
    {
        return strike.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static decimal? NormalizeIv(decimal? value)
    {
        if (!value.HasValue || value.Value <= 0)
        {
            return null;
        }

        return value.Value <= 3 ? value.Value * 100 : value.Value;
    }

    private void CalculateLegTheoreticalProfit()
    {
        if (IsLive) return;

        if (_currentPrice == null)
        {
            TempPnl = null;
            return;
        }

        if (Leg.Type == LegType.Future)
        {
            TempPnl = ResolveFuturesPnl(_currentPrice);
        }
        else
        {
            TempPnl = _optionsService.CalculateLegTheoreticalProfit(Leg, _currentPrice.Value, ValuationDate);
        }
    }

    private decimal? ResolveFuturesPnl(decimal? price)
    {
        if (price == null) return null;

        var entryPrice = Leg.Price;

        if (entryPrice.HasValue)
        {
            return (price.Value - entryPrice.Value) * Leg.Size;
        }

        return null;


    }

    private decimal ResolveOptionTempPnl(decimal? underlyingPrice)
    {
        var closingPnl = ResolveClosingPnl();
        if (closingPnl.HasValue)
        {
            return closingPnl.Value;
        }


        var entryPrice = ResolveEntryPrice();
        if (!IsLive)
        {
            if (!underlyingPrice.HasValue)
            {
                return 0;
            }

            var iv = Leg.ImpliedVolatility ?? ChainIv;
            var calculationLeg = new LegModel
            {
                Type = Leg.Type,
                Strike = Leg.Strike,
                ExpirationDate = Leg.ExpirationDate,
                Size = Leg.Size,
                Price = entryPrice,
                ImpliedVolatility = iv
            };

            return _optionsService.CalculateLegTheoreticalProfit(calculationLeg, underlyingPrice.Value, ValuationDate);
        }

        var marketPrice = GetMarketPrice();
        if (!marketPrice.HasValue)
        {
            return 0;
        }

        return (marketPrice.Value - entryPrice) * Leg.Size;
    }

    private decimal? ResolveClosingPnl()
    {
        var collection = _collectionViewModel.Collection;
        var legs = collection?.Legs;
        if (legs is null || legs.Count == 0)
        {
            return null;
        }

        var matching = legs
            .Where(leg => !ReferenceEquals(leg, Leg) && IsMatchingLeg(leg, Leg))
            .ToList();

        if (matching.Count == 0)
        {
            return null;
        }

        var netSize = matching.Sum(leg => leg.Size);
        if (Math.Abs(netSize) < 0.0001m)
        {
            return null;
        }

        if (Math.Sign(netSize) == Math.Sign(Leg.Size))
        {
            return null;
        }

        var closingSize = Math.Min(Math.Abs(Leg.Size), Math.Abs(netSize));
        if (closingSize < 0.0001m)
        {
            return null;
        }

        var existingLegs = matching
            .Where(leg => Math.Sign(leg.Size) == Math.Sign(netSize))
            .ToList();

        var existingEntry = ResolveWeightedEntryPrice(existingLegs);
        if (!existingEntry.HasValue)
        {
            return null;
        }

        var currentEntry = ResolveEntryPriceForLeg(Leg);
        if (!currentEntry.HasValue)
        {
            return null;
        }

        return (currentEntry.Value - existingEntry.Value) * closingSize * Math.Sign(netSize);
    }

    private decimal? ResolveTempPnlPercent()
    {
        if (!Leg.Price.HasValue)
        {
            return null;
        }

 
        var entryPrice = ResolveEntryPrice();
        var positionValue = entryPrice * Leg.Size;
        if (Math.Abs(positionValue) < 0.0001m)
        {
            return null;
        }

        return TempPnl / Math.Abs(positionValue) * 100m;
    }

    private decimal ResolveEntryPrice()
    {
        if (Leg.Price.HasValue)
        {
            return Leg.Price.Value;
        }

        return GetMarketPrice() ?? 0;
    }

    private decimal? ResolveEntryPriceForLeg(LegModel leg)
    {
        if (leg.Price.HasValue)
        {
            return leg.Price.Value;
        }

        var baseAsset = _collectionViewModel.Position?.Position.BaseAsset;
        var ticker = _optionsChainService.FindTickerForLeg(leg, baseAsset);
        if (ticker is null)
        {
            return null;
        }

        if (leg.Size >= 0)
        {
            if (ticker.AskPrice > 0)
            {
                return ticker.AskPrice;
            }

            if (ticker.MarkPrice > 0)
            {
                return ticker.MarkPrice;
            }

            if (ticker.BidPrice > 0)
            {
                return ticker.BidPrice;
            }

            return null;
        }

        if (ticker.BidPrice > 0)
        {
            return ticker.BidPrice;
        }

        if (ticker.MarkPrice > 0)
        {
            return ticker.MarkPrice;
        }

        if (ticker.AskPrice > 0)
        {
            return ticker.AskPrice;
        }

        return null;
    }

    private static bool IsMatchingLeg(LegModel left, LegModel right)
    {
        if (left.Type != right.Type)
        {
            return false;
        }

        if (!IsStrikeMatch(left.Strike, right.Strike))
        {
            return false;
        }

        return IsDateMatch(left.ExpirationDate, right.ExpirationDate);
    }

    private static bool IsStrikeMatch(decimal? left, decimal? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return true;
        }

        if (!left.HasValue || !right.HasValue)
        {
            return false;
        }

        return Math.Abs(left.Value - right.Value) < 0.01m;
    }

    private static bool IsDateMatch(DateTime? left, DateTime? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return true;
        }

        if (!left.HasValue || !right.HasValue)
        {
            return false;
        }

        return left.Value.Date == right.Value.Date;
    }

    private decimal? ResolveWeightedEntryPrice(IEnumerable<LegModel> legs)
    {
        decimal totalSize = 0;
        decimal totalCost = 0;

        foreach (var leg in legs)
        {
            var entry = ResolveEntryPriceForLeg(leg);
            if (!entry.HasValue)
            {
                continue;
            }

            var size = Math.Abs(leg.Size);
            if (size < 0.0001m)
            {
                continue;
            }

            totalSize += size;
            totalCost += entry.Value * size;
        }

        if (totalSize < 0.0001m)
        {
            return null;
        }

        return totalCost / totalSize;
    }


    private decimal? GetMarketPrice()
    {
        var markPrice = MarkPrice;

        if (Leg.Size < 0)
        {
            return Ask ?? markPrice ?? Bid;
        }

        if (Leg.Size > 0)
        {
            return Bid ?? markPrice ?? Ask;
        }

        return markPrice ?? Bid ?? Ask;
    }

    private decimal? GetEntryPriceSuggestion()
    {
        var markPrice = MarkPrice;

        if (Leg.Size >= 0)
        {
            return Ask ?? markPrice ?? Bid;
        }

        return Bid ?? markPrice ?? Ask;
    }



    private static decimal DetermineSignedSize(BybitPosition position)
    {
        var magnitude = Math.Abs(position.Size);
        if (magnitude < 0.0001m)
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(position.Side))
        {
            var normalized = position.Side.Trim();
            if (string.Equals(normalized, "Sell", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Short", StringComparison.OrdinalIgnoreCase))
            {
                return -magnitude;
            }
        }

        return magnitude;
    }
}
