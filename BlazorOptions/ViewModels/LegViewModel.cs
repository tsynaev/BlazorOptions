using System.Globalization;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class LegViewModel : IDisposable
{
    private readonly LegsCollectionViewModel _collectionViewModel;
    private readonly OptionsService _optionsService;
    private readonly OptionsChainService _optionsChainService;
    private readonly SemaphoreSlim _subscriptionLock = new(1, 1);
    private IDisposable? _subscriptionRegistration;
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

    public double? PlaceholderPrice => Leg.Price.HasValue ? null : GetEntryPriceSuggestion();

    public BidAsk BidAsk => ResolveBidAsk();

    public double? MarkPrice => _isLive ? (_lastMarkPrice ?? ResolveFallbackMarkPrice()) : null;

    public double? ChainIv => _isLive ? (_lastChainIv ?? ResolveFallbackIv()) : null;

    public double TempPnl => ResolveTempPnl();

    public double? TempPnlPercent => ResolveTempPnlPercent();

    public IReadOnlyList<DateTime> AvailableExpirations => GetAvailableExpirations();

    public IReadOnlyList<double> AvailableStrikes => GetAvailableStrikes();

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

        if (Math.Abs(Leg.Size - signedSize) > 0.0001)
        {
            Leg.Size = signedSize;
            updated = true;
        }

        if (!Leg.Price.HasValue || Math.Abs(Leg.Price.Value - position.AvgPrice) > 0.0001)
        {
            Leg.Price = position.AvgPrice;
            updated = true;
        }

        if (string.IsNullOrWhiteSpace(Leg.Symbol))
        {
            Leg.Symbol = position.Symbol;
            UpdateSymbol();
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

    private void UpdateSymbol()
    {
        _ = _leg;
    }

    public Task<IEnumerable<DateTime?>> SearchExpirationsAsync(string? value, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var items = AvailableExpirations;
        if (items.Count == 0)
        {
            return Task.FromResult(Enumerable.Empty<DateTime?>());
        }

        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(items.Select(item => (DateTime?)item));
        }

        return Task.FromResult(items
            .Where(item => FormatExpiration(item).Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(item => (DateTime?)item));
    }

    public Task<IEnumerable<double?>> SearchStrikesAsync(string? value, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var items = AvailableStrikes;
        if (items.Count == 0)
        {
            return Task.FromResult(Enumerable.Empty<double?>());
        }

        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(items.Select(item => (double?)item));
        }

        return Task.FromResult(items
            .Where(item => FormatStrike(item).Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(item => (double?)item));
    }

    private async Task RefreshSubscriptionAsync()
    {
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

            var subscription = new OptionsChainService.OptionChainSubscription(Leg.Symbol);
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
            return new BidAsk(_lastBid, _lastAsk);
        }

        var ticker = GetFallbackTicker();
        if (ticker is null)
        {
            return default;
        }

        var bid = ticker.BidPrice > 0 ? ticker.BidPrice : (double?)null;
        var ask = ticker.AskPrice > 0 ? ticker.AskPrice : (double?)null;
        return new BidAsk(bid, ask);
    }

    private void ClearTickerCache()
    {
        _lastBid = null;
        _lastAsk = null;
        _lastMarkPrice = null;
        _lastChainIv = null;
    }

    private IReadOnlyList<OptionChainTicker> GetOptionTickers()
    {
        if (Leg.Type == LegType.Future)
        {
            return Array.Empty<OptionChainTicker>();
        }

        var baseAsset = _collectionViewModel.Position?.Position.BaseAsset;
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return Array.Empty<OptionChainTicker>();
        }

        return _optionsChainService.GetSnapshot()
            .Where(ticker => string.Equals(ticker.BaseAsset, baseAsset, StringComparison.OrdinalIgnoreCase)
                             && ticker.Type == Leg.Type)
            .ToList();
    }

    private IReadOnlyList<DateTime> GetAvailableExpirations()
    {
        var tickers = GetOptionTickers();
        if (tickers.Count == 0)
        {
            return Array.Empty<DateTime>();
        }

        return tickers
            .Select(ticker => ticker.ExpirationDate.Date)
            .Distinct()
            .OrderBy(item => item)
            .ToList();
    }

    private IReadOnlyList<double> GetAvailableStrikes()
    {
        var tickers = GetOptionTickers();
        if (tickers.Count == 0)
        {
            return Array.Empty<double>();
        }

        var expiration = Leg.ExpirationDate?.Date;

        return tickers
            .Where(ticker => !expiration.HasValue || ticker.ExpirationDate.Date == expiration.Value)
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
        var closingPnl = ResolveClosingPnl();
        if (closingPnl.HasValue)
        {
            return closingPnl.Value;
        }

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

    private double? ResolveClosingPnl()
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
        if (Math.Abs(netSize) < 0.0001)
        {
            return null;
        }

        if (Math.Sign(netSize) == Math.Sign(Leg.Size))
        {
            return null;
        }

        var closingSize = Math.Min(Math.Abs(Leg.Size), Math.Abs(netSize));
        if (closingSize < 0.0001)
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

    private double? ResolveEntryPriceForLeg(LegModel leg)
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

    private static bool IsStrikeMatch(double? left, double? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return true;
        }

        if (!left.HasValue || !right.HasValue)
        {
            return false;
        }

        return Math.Abs(left.Value - right.Value) < 0.01;
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

    private double? ResolveWeightedEntryPrice(IEnumerable<LegModel> legs)
    {
        double totalSize = 0;
        double totalCost = 0;

        foreach (var leg in legs)
        {
            var entry = ResolveEntryPriceForLeg(leg);
            if (!entry.HasValue)
            {
                continue;
            }

            var size = Math.Abs(leg.Size);
            if (size < 0.0001)
            {
                continue;
            }

            totalSize += size;
            totalCost += entry.Value * size;
        }

        if (totalSize < 0.0001)
        {
            return null;
        }

        return totalCost / totalSize;
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

        var baseAsset = _collectionViewModel.Position?.Position.BaseAsset;
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

    private double? GetEntryPriceSuggestion()
    {
        var bidAsk = BidAsk;
        var markPrice = MarkPrice;

        if (Leg.Size >= 0)
        {
            return bidAsk.Ask ?? markPrice ?? bidAsk.Bid;
        }

        return bidAsk.Bid ?? markPrice ?? bidAsk.Ask;
    }

    private OptionChainTicker? GetFallbackTicker()
    {
        return _optionsChainService.FindTickerForLeg(Leg);
    }

    private double? ResolveFallbackMarkPrice()
    {
        var ticker = GetFallbackTicker();
        if (ticker is null)
        {
            return null;
        }

        return ticker.MarkPrice > 0 ? ticker.MarkPrice : null;
    }

    private double? ResolveFallbackIv()
    {
        var ticker = GetFallbackTicker();
        if (ticker is null)
        {
            return null;
        }

        return NormalizeIv(ticker.MarkIv)
            ?? NormalizeIv(ticker.BidIv)
            ?? NormalizeIv(ticker.AskIv);
    }

    private static double DetermineSignedSize(BybitPosition position)
    {
        var magnitude = Math.Abs(position.Size);
        if (magnitude < 0.0001)
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
