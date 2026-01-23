using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class LegViewModel : IDisposable
{
    private readonly LegsCollectionViewModel _collectionViewModel;
    private readonly OptionsService _optionsService;
    private readonly OptionsChainService _optionsChainService;
    private readonly SemaphoreSlim _subscriptionLock = new(1, 1);
    private IDisposable? _subscriptionRegistration;
    private string? _symbol;
    private double? _currentPrice;
    private bool _isLive;
    private double? _lastBid;
    private double? _lastAsk;
    private double? _lastMarkPrice;
    private double? _lastChainIv;
    private DateTime _valuationDate;
    private LegModel? _leg;

    public LegViewModel(
        LegsCollectionViewModel collectionViewModel,
        OptionsService optionsService,
        OptionsChainService optionsChainService)
    {
        _collectionViewModel = collectionViewModel;
        _optionsService = optionsService;
        _optionsChainService = optionsChainService;

        _collectionViewModel.ActivePositionUpdated += HandleActivePositionUpdated;

    }

    public LegModel Leg
    {
        get => _leg ?? throw new InvalidOperationException("Leg is not set.");
        set
        {
            _leg = value;
            UpdateSymbol();
            _ = RefreshSubscriptionAsync();
        }
    }

    public IEnumerable<LegType> LegTypes => Enum.GetValues<LegType>();

    public event Action? Changed;
    public event Func<Task>? Removed;

    public double? PlaceholderPrice => Leg.Price.HasValue ? null : GetMarketPrice();

    public BidAsk BidAsk => ResolveBidAsk();

    public double? MarkPrice => _isLive ? (_lastMarkPrice ?? _collectionViewModel.GetLegMarkPrice(Leg)) : null;

    public double? ChainIv => _isLive ? (_lastChainIv ?? _collectionViewModel.GetLegMarkIv(Leg)) : null;

    public double TempPnl => ResolveTempPnl();

    public double? TempPnlPercent => ResolveTempPnlPercent();

    public double? CurrentPrice
    {
        get => _currentPrice;
        set
        {
            if (_currentPrice == value)
            {
                return;
            }

            _currentPrice = value;
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
                ClearTickerCache();
            }
            else
            {
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
            Changed?.Invoke();
        }
    }

    public Task UpdateLegIvAsync(double? iv)
    {
        return _collectionViewModel.UpdateLegIvAsync(Leg, iv);
    }

    public async Task UpdateLegIncludedAsync(bool include)
    {
        await _collectionViewModel.UpdateLegIncludedAsync(Leg, include);
        Changed?.Invoke();
    }

    public async Task UpdateLegTypeAsync(LegType type)
    {
        await _collectionViewModel.UpdateLegTypeAsync(Leg, type);
        Changed?.Invoke();
    }

    public async Task UpdateLegStrikeAsync(double? strike)
    {
        await _collectionViewModel.UpdateLegStrikeAsync(Leg, strike);
        Changed?.Invoke();
    }

    public async Task UpdateLegSizeAsync(double size)
    {
        await _collectionViewModel.UpdateLegSizeAsync(Leg, size);
        Changed?.Invoke();
    }

    public async Task UpdateLegExpirationAsync(DateTime? date)
    {
        await _collectionViewModel.UpdateLegExpirationAsync(Leg, date);
        Changed?.Invoke();
    }

    public async Task UpdateLegPriceAsync(double? price)
    {
        await _collectionViewModel.UpdateLegPriceAsync(Leg, price);
        Changed?.Invoke();
    }

    public async Task RemoveLegAsync()
    {
        await _collectionViewModel.RemoveLegAsync(Leg);
        if (Removed is not null)
        {
            await Removed.Invoke();
        }
    }

    public void UpdateLeg(LegModel leg)
    {
        _leg = leg;
        UpdateSymbol();
        ClearTickerCache();
        _ = RefreshSubscriptionAsync();
    }

    public void Dispose()
    {
        _collectionViewModel.ActivePositionUpdated -= HandleActivePositionUpdated;
        _subscriptionRegistration?.Dispose();
    }

    private void HandleActivePositionUpdated(BybitPosition position)
    {
        if (position is null || string.IsNullOrWhiteSpace(position.Symbol))
        {
            return;
        }

        if (string.Equals(position.Symbol, _symbol, StringComparison.OrdinalIgnoreCase))
        {
            Changed?.Invoke();
        }
    }

    private void UpdateSymbol()
    {
        if (_leg is null)
        {
            _symbol = null;
            return;
        }

        _symbol = _collectionViewModel.GetLegSymbol(Leg);
    }

    private async Task RefreshSubscriptionAsync()
    {
        if (_leg is null)
        {
            return;
        }

        var symbol = _collectionViewModel.GetLegSymbol(Leg);
        await _subscriptionLock.WaitAsync();
        try
        {
            if (string.Equals(symbol, _symbol, StringComparison.OrdinalIgnoreCase) && _subscriptionRegistration is not null)
            {
                return;
            }

            _subscriptionRegistration?.Dispose();
            _subscriptionRegistration = null;
            _symbol = symbol;

            if (string.IsNullOrWhiteSpace(symbol))
            {
                return;
            }

            var subscription = new OptionsChainService.OptionChainSubscription(symbol);
            _subscriptionRegistration = await _optionsChainService.SubscribeAsync(subscription, HandleTicker);
        }
        finally
        {
            _subscriptionLock.Release();
        }
    }

    private void HandleTicker(OptionChainTicker ticker)
    {
        if (!_isLive)
        {
            return;
        }

        _lastBid = ticker.BidPrice > 0 ? ticker.BidPrice : null;
        _lastAsk = ticker.AskPrice > 0 ? ticker.AskPrice : null;
        _lastMarkPrice = ticker.MarkPrice > 0 ? ticker.MarkPrice : null;
        _lastChainIv = NormalizeIv(ticker.MarkIv)
            ?? NormalizeIv(ticker.BidIv)
            ?? NormalizeIv(ticker.AskIv);

        if (!Leg.ImpliedVolatility.HasValue || Leg.ImpliedVolatility.Value <= 0)
        {
            if (_lastChainIv.HasValue && _lastChainIv.Value > 0)
            {
                Leg.ImpliedVolatility = _lastChainIv.Value;
            }
        }

        Changed?.Invoke();
    }

    private BidAsk ResolveBidAsk()
    {
        if (!_isLive)
        {
            return default;
        }

        if (_lastBid.HasValue || _lastAsk.HasValue)
        {
            var fallback = _collectionViewModel.GetLegBidAsk(Leg);
            var bid = _lastBid ?? fallback.Bid;
            var ask = _lastAsk ?? fallback.Ask;
            return new BidAsk(bid, ask);
        }

        return _collectionViewModel.GetLegBidAsk(Leg);
    }

    private void ClearTickerCache()
    {
        _lastBid = null;
        _lastAsk = null;
        _lastMarkPrice = null;
        _lastChainIv = null;
    }

    private static double? NormalizeIv(double? value)
    {
        if (!value.HasValue || value.Value <= 0)
        {
            return null;
        }

        return value.Value <= 3 ? value.Value * 100 : value.Value;
    }

    private double CalculateLegTheoreticalProfit(LegModel leg, double underlyingPrice, DateTime? valuationDate = null)
    {
        return _optionsService.CalculateLegTheoreticalProfit(leg, underlyingPrice, valuationDate);
    }

    private double ResolveTempPnl()
    {

        var entryPrice = ResolveEntryPrice();
        if (Leg.Type == LegType.Future)
        {
            var underlyingPrice = ResolveFutureUnderlyingPrice();
            if (!underlyingPrice.HasValue)
            {
                return 0;
            }

            return (underlyingPrice.Value - entryPrice) * Leg.Size;
        }

        if (!IsLive)
        {
            var underlyingPrice = ResolveUnderlyingPrice();
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

            return CalculateLegTheoreticalProfit(
                calculationLeg,
                underlyingPrice.Value,
                ValuationDate);
        }

        var marketPrice = GetMarketPrice();
        if (!marketPrice.HasValue)
        {
            return 0;
        }

        return (marketPrice.Value - entryPrice) * Leg.Size;
    }

    private double? ResolveTempPnlPercent()
    {
        if (!Leg.Price.HasValue)
        {
            return null;
        }

        var entryPrice = ResolveEntryPrice();
        var positionValue = entryPrice * Leg.Size;
        if (Math.Abs(positionValue) < 0.0001)
        {
            return null;
        }

        return ResolveTempPnl() / Math.Abs(positionValue) * 100;
    }

    private double ResolveEntryPrice()
    {
        if (Leg.Price.HasValue)
        {
            return Leg.Price.Value;
        }

        return GetMarketPrice() ?? 0;
    }

    private double? ResolveUnderlyingPrice()
    {
        return CurrentPrice;
    }

    private double? ResolveFutureUnderlyingPrice()
    {
        var marketPrice = GetMarketPrice();
        if (marketPrice.HasValue)
        {
            return marketPrice;
        }

        var baseAsset = _collectionViewModel.Position?.BaseAsset;
        if (!string.IsNullOrWhiteSpace(Leg.Symbol))
        {
            if (string.IsNullOrWhiteSpace(baseAsset))
            {
                return null;
            }

            if (!Leg.Symbol.StartsWith(baseAsset, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return CurrentPrice;
    }

    private double? GetMarketPrice()
    {
        var bidAsk = BidAsk;
        var markPrice = MarkPrice;

        if (Leg.Size < 0)
        {
            return bidAsk.Ask ?? markPrice ?? bidAsk.Bid;
        }

        if (Leg.Size > 0)
        {
            return bidAsk.Bid ?? markPrice ?? bidAsk.Ask;
        }

        return markPrice ?? bidAsk.Bid ?? bidAsk.Ask;
    }
}
