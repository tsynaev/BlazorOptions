using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlazorOptions;
using BlazorOptions.Diagnostics;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class LegViewModel : IDisposable
{
    private readonly LegsCollectionViewModel _collectionViewModel;
    private readonly OptionsService _optionsService;
    private readonly IExchangeService _exchangeService;
    private readonly BlackScholes _blackScholes;
    private readonly SemaphoreSlim _subscriptionLock = new(1, 1);
    private IDisposable? _subscriptionRegistration;
    private decimal? _currentPrice;
    private bool _isLive;
    private string? _statusMessage;
 
    private LegModel? _leg;
    private List<DateTime?> _cachedExpirations = new List<DateTime?>();
    private IReadOnlyList<decimal> _cachedStrikes = Array.Empty<decimal>();
    private DateTime _valuationDate;
    private bool _suppressLegNotifications;
    private bool _pendingChartUpdate;
    private bool _pendingDataUpdate;
    private bool _usedInitialLiveMarkSnapshot;
    private IReadOnlyList<LegLinkedOrderModel> _linkedOrders = Array.Empty<LegLinkedOrderModel>();

    // Keep chart updates tied only to leg fields that affect payoff calculations.
    private static readonly HashSet<string> ChartRelevantLegProperties = new(StringComparer.Ordinal)
    {
        nameof(LegModel.IsIncluded),
        nameof(LegModel.Type),
        nameof(LegModel.Strike),
        nameof(LegModel.ExpirationDate),
        nameof(LegModel.Size),
        nameof(LegModel.Price),
        nameof(LegModel.ImpliedVolatility)
    };
 


    public LegViewModel(
        LegsCollectionViewModel collectionViewModel,
        OptionsService optionsService,
        IExchangeService exchangeService,
        BlackScholes blackScholes)
    {
        _collectionViewModel = collectionViewModel;
        _optionsService = optionsService;
        _exchangeService = exchangeService;
        _blackScholes = blackScholes;
    }

    public LegModel Leg
    {
        get => _leg ?? throw new InvalidOperationException("Leg is not set.");
        set
        {
            if (ReferenceEquals(_leg, value))
            {
                return;
            }

            if (_leg is not null)
            {
                DetachLegModel(_leg);
            }

            _leg = value;
            AttachLegModel(_leg);

            RefreshExpDatesAndStrikes();
            ResetGreeks();
            _ = RefreshSubscriptionAsync();
        }
    }

    public IEnumerable<LegType> LegTypes => Enum.GetValues<LegType>();

    public event Action<LegsCollectionUpdateKind>? Changed;
    public event Func<Task>? Removed;

    public decimal? PlaceholderPrice => Leg.Price.HasValue ? null : GetEntryPriceSuggestion();

    public decimal? Bid { get; private set; }
    public decimal? Ask { get; private set; }

    public decimal? MarkPrice { get; private set; }

    public decimal? ChainIv { get; private set; }

    public decimal? PnL => ResolvePnlFromMarkPrice();

    public decimal? Delta { get; private set; }

    public decimal? Gamma { get; private set; }

    public decimal? Vega { get; private set; }

    public decimal? Theta { get; private set; }

    public IReadOnlyList<LegLinkedOrderModel> LinkedOrders => _linkedOrders;

    public string StatusMessage => _statusMessage ?? string.Empty;

    public decimal? PnLPercent => ResolvePnlPercent();

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
            if (!_isLive)
            {
                Changed?.Invoke(LegsCollectionUpdateKind.PricingContextUpdated);
            }
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
                Changed?.Invoke(LegsCollectionUpdateKind.ViewModelDataUpdated);
            }
            else
            {
                CalculateLegTheoreticalProfit();
            }
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
            if (!_isLive)
            {
                Changed?.Invoke(LegsCollectionUpdateKind.PricingContextUpdated);
            }
        }
    }

    public void UpdateLegIvAsync(decimal? iv)
    {
        using var activity = ActivitySources.Telemetry.StartActivity("LegViewModel.UpdateLegIv");
        if (Leg.IsReadOnly)
        {
            return;
        }

        RunLegUpdate(() => Leg.ImpliedVolatility = iv);
    }


    public void UpdateLegIncludedAsync(bool include)
    {
        using var activity = ActivitySources.Telemetry.StartActivity("LegViewModel.UpdateIncluded");
        RunLegUpdate(() => Leg.IsIncluded = include);
    }

    public void UpdateLegTypeAsync(LegType type)
    {
        using var activity = ActivitySources.Telemetry.StartActivity("LegViewModel.UpdateType");
        if (Leg.IsReadOnly)
        {
            return;
        }

        RunLegUpdate(() =>
        {
            Leg.Type = type;
            if (type == LegType.Future)
            {
                Leg.Strike = null;
                Leg.ImpliedVolatility = null;
            }
            ResetGreeks();
            SyncLegSymbol();
            RefreshExpDatesAndStrikes();
        });
    }

    public void UpdateLegStrikeAsync(decimal? strike)
    {
        using var activity = ActivitySources.Telemetry.StartActivity("LegViewModel.UpdateStrike");
        if (Leg.IsReadOnly)
        {
            return;
        }

        RunLegUpdate(() =>
        {
            Leg.Strike = strike;
            ResetGreeks();
            SyncLegSymbol();
        });
    }

    public void UpdateLegSizeAsync(decimal size)
    {
        using var activity = ActivitySources.Telemetry.StartActivity("LegViewModel.UpdateSize");
        if (Leg.IsReadOnly)
        {
            return;
        }

        RunLegUpdate(() => Leg.Size = size);
    }

    public void UpdateLegExpirationAsync(DateTime? date)
    {
        using var activity = ActivitySources.Telemetry.StartActivity("LegViewModel.UpdateExpiration");

        if (Leg.IsReadOnly)
        {
            return;
        }

        RunLegUpdate(() =>
        {
            Leg.ExpirationDate = date;

            ResetGreeks();
            SyncLegSymbol();
            RefreshExpDatesAndStrikes();
        });
    }

    public async Task UpdateLegPriceAsync(decimal? price)
    {
        using var activity = ActivitySources.Telemetry.StartActivity("LegViewModel.UpdatePrice");
        if (Leg.IsReadOnly)
        {
            return;
        }

        RunLegUpdate(() => Leg.Price = price);
    }

    public async Task RemoveLegAsync()
    {
        using var activity = ActivitySources.Telemetry.StartActivity("LegViewModel.RemoveLeg");
        await _collectionViewModel.RemoveLegAsync(Leg);
        if (Removed is not null)
        {
            await Removed.Invoke();
        }
    }


    public bool SetLegStatus(LegStatus status, string? message)
    {
        var normalizedMessage = status == LegStatus.Missing
            ? (string.IsNullOrWhiteSpace(message) ? "Exchange position not found." : message)
            : string.Empty;
        if (Leg.Status == status && string.Equals(_statusMessage, normalizedMessage, StringComparison.Ordinal))
        {
            return false;
        }

        Leg.Status = status;
        _statusMessage = normalizedMessage;
        return true;
    }

    public bool Update(ExchangePosition position)
    {
        if (position is null || string.IsNullOrWhiteSpace(position.Symbol))
        {
            return false;
        }

        // Position stream must not mutate order legs.
        if (!Leg.IsReadOnly || Leg.Status == LegStatus.Order)
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

        _pendingChartUpdate = false;
        _pendingDataUpdate = false;
        _suppressLegNotifications = true;
        try
        {
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
        }
        finally
        {
            _suppressLegNotifications = false;
        }

        FlushPendingLegNotifications(updated);

        return updated;
    }

    public void Dispose()
    {
        if (_leg is not null)
        {
            DetachLegModel(_leg);
        }

        _subscriptionRegistration?.Dispose();
    }

    private void AttachLegModel(LegModel leg)
    {
        leg.PropertyChanged += HandleLegPropertyChanged;
    }

    private void DetachLegModel(LegModel leg)
    {
        leg.PropertyChanged -= HandleLegPropertyChanged;
    }

    private void HandleLegPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var isChartRelevant = IsChartRelevantProperty(e.PropertyName);
        if (_suppressLegNotifications)
        {
            if (isChartRelevant)
            {
                _pendingChartUpdate = true;
            }
            else
            {
                _pendingDataUpdate = true;
            }

            return;
        }

        Changed?.Invoke(isChartRelevant
            ? LegsCollectionUpdateKind.LegModelChanged
            : LegsCollectionUpdateKind.LegModelDataChanged);
    }

    private static bool IsChartRelevantProperty(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return true;
        }

        return ChartRelevantLegProperties.Contains(propertyName);
    }

    // Batch multiple leg property changes into a single collection update event.
    private void RunLegUpdate(Action update)
    {
        _pendingChartUpdate = false;
        _pendingDataUpdate = false;
        _suppressLegNotifications = true;
        try
        {
            update();
        }
        finally
        {
            _suppressLegNotifications = false;
        }

        FlushPendingLegNotifications(true);
    }

    private void FlushPendingLegNotifications(bool hasChanges)
    {
        if (!hasChanges)
        {
            _pendingChartUpdate = false;
            _pendingDataUpdate = false;
            return;
        }

        if (_pendingChartUpdate)
        {
            Changed?.Invoke(LegsCollectionUpdateKind.LegModelChanged);
        }
        else if (_pendingDataUpdate)
        {
            Changed?.Invoke(LegsCollectionUpdateKind.LegModelDataChanged);
        }

        _pendingChartUpdate = false;
        _pendingDataUpdate = false;
    }

    private async Task RefreshSubscriptionAsync()
    {
        using var activity = ActivitySources.Telemetry.StartActivity("LegViewModel.RefreshSubscription");
        if (_leg is null)
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
                _subscriptionRegistration = await _exchangeService.Tickers.SubscribeAsync(Leg.Symbol, HandleLinearTicker);
            }
            else
            {

                _subscriptionRegistration = await _exchangeService.OptionsChain.SubscribeAsync(Leg.Symbol, HandleOptionTicker);
            }
        }
        finally
        {
            _subscriptionLock.Release();
        }
    }

    private Task HandleLinearTicker(ExchangePriceUpdate e)
    {
        var changed = false;

        var nextMark = ResolveLiveMarkPrice(e.Price, null);
        if (MarkPrice != nextMark)
        {
            MarkPrice = nextMark;
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke(LegsCollectionUpdateKind.ViewModelDataUpdated);
        }

        return Task.CompletedTask;
    }

    private async Task HandleOptionTicker(OptionChainTicker ticker)
    {
        var changed = false;

        decimal? nextBid = ticker.BidPrice > 0 ? ticker.BidPrice : null;
        if (Bid != nextBid)
        {
            Bid = nextBid;
            changed = true;
        }

        decimal? nextAsk = ticker.AskPrice > 0 ? ticker.AskPrice : null;
        if (Ask != nextAsk)
        {
            Ask = nextAsk;
            changed = true;
        }

        decimal? nextMark = ResolveLiveMarkPrice(ticker.UnderlyingPrice, ticker);
        if (MarkPrice != nextMark)
        {
            MarkPrice = nextMark;
            changed = true;
        }

        if (Delta != ticker.Delta)
        {
            Delta = ticker.Delta;
            changed = true;
        }

        if (Gamma != ticker.Gamma)
        {
            Gamma = ticker.Gamma;
            changed = true;
        }

        if (Vega != ticker.Vega)
        {
            Vega = ticker.Vega;
            changed = true;
        }

        if (Theta != ticker.Theta)
        {
            Theta = ticker.Theta;
            changed = true;
        }

        var chainIv = NormalizeIv(ticker.MarkIv)
            ?? NormalizeIv(ticker.BidIv)
            ?? NormalizeIv(ticker.AskIv);
        if (ChainIv != chainIv)
        {
            ChainIv = chainIv;
            changed = true;
        }

        if (!Leg.ImpliedVolatility.HasValue || Leg.ImpliedVolatility.Value <= 0)
        {
            if (ChainIv.HasValue && ChainIv.Value > 0)
            {
                Leg.ImpliedVolatility = ChainIv.Value;
            }
        }

        if (!changed)
        {
            return;
        }
        Changed?.Invoke(LegsCollectionUpdateKind.ViewModelDataUpdated);
    }

    private void SyncLegSymbol()
    {
        var baseAsset = _collectionViewModel.Position?.Position.BaseAsset;
        var quoteAsset = _collectionViewModel.Position?.Position.QuoteAsset;
        Leg.Symbol = _exchangeService.FormatSymbol(Leg, baseAsset, quoteAsset);
    }


    private void RefreshExpDatesAndStrikes()
    {
        using var activity = ActivitySources.Telemetry.StartActivity("LegViewModel.RefreshExpDatesAndStrikes");

        if (Leg.Type == LegType.Future)
        {
            var baseAsset = _collectionViewModel.BaseAsset;
            var quoteAsset = _collectionViewModel.Position?.Position.QuoteAsset;
            var cachedExpirations = _exchangeService.FuturesInstruments.GetCachedExpirations(baseAsset, quoteAsset).ToList();

            if (!cachedExpirations.Contains(null))
            {
                cachedExpirations.Insert(0, null);
            }

            _cachedExpirations = cachedExpirations;
            _cachedStrikes = Array.Empty<decimal>();
            return;
        }

        var tickers = _exchangeService.OptionsChain.GetTickersByBaseAsset(_collectionViewModel.BaseAsset, Leg.Type);

        //TODO: _cachedExpirations _cachedStrikes must be replaced with IExchangeService.GetAvailableStrikes(Leg) and  IExchangeService.GetAvailableExpDates(Leg)

        _cachedExpirations = tickers.Select(ticker => (DateTime?)ticker.ExpirationDate.Date)
            .Distinct()
            .OrderBy(item => item)
            .ToList();

        _cachedStrikes = tickers
            .Where(ticker => ticker.ExpirationDate.Date == Leg.ExpirationDate?.Date)
            .Select(ticker => ticker.Strike)
            .Distinct()
            .OrderBy(item => item)
            .ToList();


    }

    public async Task EnsureFutureExpirationsLoadedAsync()
    {
        var baseAsset = _collectionViewModel.Position?.Position.BaseAsset;
        var quoteAsset = _collectionViewModel.Position?.Position.QuoteAsset;
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return;
        }

        await _exchangeService.FuturesInstruments.EnsureExpirationsAsync(baseAsset, quoteAsset);

        if (Leg.Type == LegType.Future)
        {
            RefreshExpDatesAndStrikes();
            Changed?.Invoke(LegsCollectionUpdateKind.ViewModelDataUpdated);
        }
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

        if (!_usedInitialLiveMarkSnapshot)
        {
            MarkPrice = ResolveLiveMarkPrice();
            _usedInitialLiveMarkSnapshot = true;
        }
        else
        {
            MarkPrice = ResolveNonLiveMarkPrice();
        }

    }

    private void RefreshNonLiveMarketSnapshot()
    {
        if (IsLive)
        {
            return;
        }

        if (Leg.Type == LegType.Future)
        {
            MarkPrice = _currentPrice;
            Bid = null;
            Ask = null;
            ChainIv = null;
            ResetGreeks();
            return;
        }

        var baseAsset = _collectionViewModel.Position?.Position.BaseAsset;
        var ticker = _exchangeService.OptionsChain.FindTickerForLeg(Leg, baseAsset);
        if (ticker is null)
        {
            MarkPrice = null;
            Bid = null;
            Ask = null;
            ChainIv = null;
            ResetGreeks();
            return;
        }

        Bid = ticker.BidPrice > 0 ? ticker.BidPrice : null;
        Ask = ticker.AskPrice > 0 ? ticker.AskPrice : null;
        var ivPercent = NormalizeIv(ticker.MarkIv)
            ?? NormalizeIv(ticker.BidIv)
            ?? NormalizeIv(ticker.AskIv);

        ChainIv = ivPercent;
        Delta = ticker.Delta;
        Gamma = ticker.Gamma;
        Vega = ticker.Vega;
        Theta = ticker.Theta;

        // Keep leg IV populated from option chain even in non-live mode.
        if (ivPercent.HasValue && ivPercent.Value > 0
            && (!Leg.ImpliedVolatility.HasValue || Leg.ImpliedVolatility.Value <= 0))
        {
            Leg.ImpliedVolatility = ivPercent.Value;
        }
    }

    private decimal? ResolvePnlFromMarkPrice()
    {
        if (!Leg.Price.HasValue || !MarkPrice.HasValue)
        {
            return null;
        }

        return (MarkPrice.Value - Leg.Price.Value) * Leg.Size;
    }

    private decimal ResolveOrderExpectedPnl()
    {
        var closingPnl = ResolveClosingPnl();
        if (closingPnl.HasValue)
        {
            return closingPnl.Value;
        }

        // No matching opposite leg: execution at own order price means zero expected P/L.
        return 0m;
    }

    public bool UpdateLinkedOrders(IReadOnlyList<ExchangeOrder> orders)
    {
        if (Leg.Status == LegStatus.Order || string.IsNullOrWhiteSpace(Leg.Symbol) || orders.Count == 0)
        {
            if (_linkedOrders.Count == 0)
            {
                return false;
            }

            _linkedOrders = Array.Empty<LegLinkedOrderModel>();
            return true;
        }

        var symbol = Leg.Symbol.Trim();
        var linked = orders
            .Where(order =>
                !string.IsNullOrWhiteSpace(order.Symbol) &&
                string.Equals(order.Symbol.Trim(), symbol, StringComparison.OrdinalIgnoreCase))
            .Select(order =>
            {
                var qty = Math.Abs(order.Qty);
                var expectedPnl = ResolveExpectedPnlForOrder(order);
                var newAverageEntryPrice = ResolveExpectedAverageEntryPriceForOrder(order);
                var id = string.IsNullOrWhiteSpace(order.OrderId) ? symbol : order.OrderId.Trim();
                var kind = ResolveOrderKindLabel(order);
                return new LegLinkedOrderModel(id, kind, order.Side, qty, order.Price, expectedPnl, newAverageEntryPrice);
            })
            .OrderBy(order => order.Side, StringComparer.OrdinalIgnoreCase)
            .ThenBy(order => order.OrderId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (AreSameLinkedOrders(_linkedOrders, linked))
        {
            return false;
        }

        _linkedOrders = linked;
        return true;
    }

    private decimal? ResolveExpectedPnlForOrder(ExchangeOrder order)
    {
        if (!order.Price.HasValue || !Leg.Price.HasValue)
        {
            return null;
        }

        var orderSignedSize = DetermineSignedSize(order);
        if (Math.Abs(orderSignedSize) < 0.0001m || Math.Abs(Leg.Size) < 0.0001m)
        {
            return null;
        }

        // Only opposite-side orders close part of this leg and produce expected realized P/L.
        if (Math.Sign(orderSignedSize) == Math.Sign(Leg.Size))
        {
            return 0m;
        }

        var closeQty = Math.Min(Math.Abs(orderSignedSize), Math.Abs(Leg.Size));
        var pnlPerUnit = order.Price.Value - Leg.Price.Value;
        var legSide = Math.Sign(Leg.Size);
        return pnlPerUnit * closeQty * legSide;
    }

    private decimal? ResolveExpectedAverageEntryPriceForOrder(ExchangeOrder order)
    {
        if (!order.Price.HasValue || !Leg.Price.HasValue)
        {
            return null;
        }

        var orderSignedSize = DetermineSignedSize(order);
        var currentSize = Leg.Size;
        if (Math.Abs(orderSignedSize) < 0.0001m || Math.Abs(currentSize) < 0.0001m)
        {
            return null;
        }

        // Only same-side orders increase the existing position and change weighted entry.
        if (Math.Sign(orderSignedSize) != Math.Sign(currentSize))
        {
            return null;
        }

        var currentAbs = Math.Abs(currentSize);
        var addAbs = Math.Abs(orderSignedSize);
        var total = currentAbs + addAbs;
        if (total < 0.0001m)
        {
            return null;
        }

        return ((Leg.Price.Value * currentAbs) + (order.Price.Value * addAbs)) / total;
    }

    private static decimal DetermineSignedSize(ExchangeOrder order)
    {
        var magnitude = Math.Abs(order.Qty);
        if (magnitude < 0.0001m)
        {
            return 0m;
        }

        if (!string.IsNullOrWhiteSpace(order.Side)
            && (string.Equals(order.Side, "Sell", StringComparison.OrdinalIgnoreCase)
                || string.Equals(order.Side, "Short", StringComparison.OrdinalIgnoreCase)))
        {
            return -magnitude;
        }

        return magnitude;
    }

    private static string? ResolveOrderKindLabel(ExchangeOrder order)
    {
        var stopOrderType = order.StopOrderType?.Trim();
        if (!string.IsNullOrWhiteSpace(stopOrderType))
        {
            if (stopOrderType.Contains("TakeProfit", StringComparison.OrdinalIgnoreCase))
            {
                return "TP";
            }

            if (stopOrderType.Contains("StopLoss", StringComparison.OrdinalIgnoreCase)
                || stopOrderType.Contains("Stop", StringComparison.OrdinalIgnoreCase))
            {
                return "SL";
            }
        }

        if (string.Equals(order.OrderStatus, "Untriggered", StringComparison.OrdinalIgnoreCase))
        {
            return "Conditional";
        }

        return null;
    }

    private static bool AreSameLinkedOrders(IReadOnlyList<LegLinkedOrderModel> left, IReadOnlyList<LegLinkedOrderModel> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            var l = left[i];
            var r = right[i];
            if (!string.Equals(l.OrderId, r.OrderId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(l.OrderKind, r.OrderKind, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(l.Side, r.Side, StringComparison.OrdinalIgnoreCase)
                || l.Quantity != r.Quantity
                || l.Price != r.Price
                || l.ExpectedPnl != r.ExpectedPnl
                || l.NewAverageEntryPrice != r.NewAverageEntryPrice)
            {
                return false;
            }
        }

        return true;
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

    private decimal? ResolvePnlPercent()
    {
        if (Leg.Status == LegStatus.Missing)
        {
            return null;
        }

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

        return PnL / Math.Abs(positionValue) * 100m;
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
        var ticker = _exchangeService.OptionsChain.FindTickerForLeg(leg, baseAsset);
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

    private decimal? ResolveLiveMarkPrice(decimal? liveUnderlyingPrice = null, OptionChainTicker? optionTicker = null)
    {
        if (Leg.Status == LegStatus.Missing)
        {
            return ResolveMarkPriceFromMissingRealized();
        }

        if (Leg.Status == LegStatus.Order)
        {
            return ResolveOrderExecutionMarkPrice();
        }

        if (Leg.Type == LegType.Future)
        {
            return liveUnderlyingPrice ?? _currentPrice;
        }

        var ticker = optionTicker;
        if (ticker is null)
        {
            var baseAsset = _collectionViewModel.Position?.Position.BaseAsset;
            ticker = _exchangeService.OptionsChain.FindTickerForLeg(Leg, baseAsset);
        }

        if (ticker is null)
        {
            return null;
        }

        if (ticker.MarkPrice > 0)
        {
            return ticker.MarkPrice;
        }

        if (ticker.BidPrice > 0 && ticker.AskPrice > 0)
        {
            return (ticker.BidPrice + ticker.AskPrice) / 2m;
        }

        if (ticker.LastPrice > 0)
        {
            return ticker.LastPrice;
        }

        return null;
    }

    private decimal? ResolveNonLiveMarkPrice()
    {
        if (Leg.Status == LegStatus.Missing)
        {
            return ResolveMarkPriceFromMissingRealized();
        }

        if (Leg.Status == LegStatus.Order)
        {
            return ResolveOrderExecutionMarkPrice();
        }

        if (Leg.Type == LegType.Future)
        {
            return _currentPrice;
        }

        if (!_currentPrice.HasValue)
        {
            return null;
        }

        var iv = Leg.ImpliedVolatility ?? ChainIv;
        if (!iv.HasValue || iv.Value <= 0 || !Leg.Strike.HasValue || !Leg.ExpirationDate.HasValue)
        {
            return ResolveLiveMarkPrice();
        }

        return _blackScholes.CalculatePriceDecimal(
            _currentPrice.Value,
            Leg.Strike.Value,
            iv.Value,
            Leg.ExpirationDate.Value,
            Leg.Type == LegType.Call,
            ValuationDate);
    }

    private decimal? ResolveOrderExecutionMarkPrice()
    {
        if (!Leg.Price.HasValue || Math.Abs(Leg.Size) < 0.0000001m)
        {
            return null;
        }

        var expectedPnl = ResolveOrderExpectedPnl();
        return Leg.Price.Value + (expectedPnl / Leg.Size);
    }

    private decimal? ResolveMarkPriceFromMissingRealized()
    {
        if (!Leg.Price.HasValue || Math.Abs(Leg.Size) < 0.0000001m)
        {
            return null;
        }

        var realized = ResolveMissingRealizedPnl();
        if (!realized.HasValue)
        {
            return null;
        }

        return Leg.Price.Value + (realized.Value / Leg.Size);
    }

    private decimal? ResolveMissingRealizedPnl()
    {
        var symbol = Leg.Symbol?.Trim();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        var closed = _collectionViewModel.Position?.Position?.Closed?.Positions;
        if (closed is null || closed.Count == 0)
        {
            return null;
        }

        var net = closed
            .Where(item => !string.IsNullOrWhiteSpace(item.Symbol)
                && string.Equals(item.Symbol.Trim(), symbol, StringComparison.OrdinalIgnoreCase))
            .Sum(item => item.Realized - item.FeeTotal);

        return net;
    }



    private static decimal DetermineSignedSize(ExchangePosition position)
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

    private void ResetGreeks()
    {
        Delta = null;
        Gamma = null;
        Vega = null;
        Theta = null;
    }

}
