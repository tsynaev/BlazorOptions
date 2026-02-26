using System.Collections.ObjectModel;
using BlazorOptions.API.Positions;
using BlazorOptions.Services;
using BlazorChart.Models;

namespace BlazorOptions.ViewModels;

public sealed class BlockTradesViewModel : Bindable
{
    private readonly AmberdataPositionsService _positionsService;
    private readonly OptionsService _optionsService;
    private readonly IExchangeService _exchangeService;
    private bool _isLoading;
    private string? _errorMessage;
    private IReadOnlyList<StrategyPosition> _positions = Array.Empty<StrategyPosition>();
    private IReadOnlyList<PositionTradeDetailRow> _selectedPositionDetails = Array.Empty<PositionTradeDetailRow>();
    private StrategyPosition? _selectedPosition;
    private decimal? _selectedUnderlyingPrice;
    private decimal? _openIndexPrice;
    private decimal? _selectedFuturesPrice;
    private decimal? _estimatedPnl;
    private decimal? _estimatedPnlPercent;
    private decimal? _investedAmount;
    private DateTime _dateStartUtc = DateTime.UtcNow.AddHours(-24);
    private DateTime _dateEndUtc = DateTime.UtcNow;
    private bool _showCandles;
    private IReadOnlyList<decimal> _lastPayoffPrices = Array.Empty<decimal>();
    private IReadOnlyList<decimal> _lastPayoffTemp = Array.Empty<decimal>();

    public BlockTradesViewModel(
        AmberdataPositionsService positionsService,
        OptionsService optionsService,
        IExchangeService exchangeService)
    {
        _positionsService = positionsService;
        _optionsService = optionsService;
        _exchangeService = exchangeService;
        ChartStrategies = new ObservableCollection<StrategySeries>();
        ChartMarkers = new ObservableCollection<PriceMarker>();
        ChartCandles = new ObservableCollection<CandlePoint>();
    }

    public ObservableCollection<StrategySeries> ChartStrategies { get; }
    public ObservableCollection<PriceMarker> ChartMarkers { get; }
    public ObservableCollection<CandlePoint> ChartCandles { get; }

    public IReadOnlyList<StrategyPosition> Positions
    {
        get => _positions;
        private set => SetField(ref _positions, value);
    }

    public StrategyPosition? SelectedPosition
    {
        get => _selectedPosition;
        private set => SetField(ref _selectedPosition, value);
    }

    public IReadOnlyList<PositionTradeDetailRow> SelectedPositionDetails
    {
        get => _selectedPositionDetails;
        private set => SetField(ref _selectedPositionDetails, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    public decimal? SelectedUnderlyingPrice
    {
        get => _selectedUnderlyingPrice;
        private set => SetField(ref _selectedUnderlyingPrice, value);
    }

    public DateTime DateStartUtc
    {
        get => _dateStartUtc;
        private set => SetField(ref _dateStartUtc, value);
    }

    public DateTime DateEndUtc
    {
        get => _dateEndUtc;
        private set => SetField(ref _dateEndUtc, value);
    }

    public decimal? SelectedFuturesPrice
    {
        get => _selectedFuturesPrice;
        private set => SetField(ref _selectedFuturesPrice, value);
    }

    public decimal? OpenIndexPrice
    {
        get => _openIndexPrice;
        private set => SetField(ref _openIndexPrice, value);
    }

    public decimal? EstimatedPnl
    {
        get => _estimatedPnl;
        private set => SetField(ref _estimatedPnl, value);
    }

    public decimal? EstimatedPnlPercent
    {
        get => _estimatedPnlPercent;
        private set => SetField(ref _estimatedPnlPercent, value);
    }

    public decimal? InvestedAmount
    {
        get => _investedAmount;
        private set => SetField(ref _investedAmount, value);
    }

    public void SetDateRange(DateTime startUtc, DateTime endUtc)
    {
        var normalizedStart = startUtc.ToUniversalTime();
        var normalizedEnd = endUtc.ToUniversalTime();
        if (normalizedEnd <= normalizedStart)
        {
            normalizedEnd = normalizedStart.AddHours(1);
        }

        DateStartUtc = normalizedStart;
        DateEndUtc = normalizedEnd;
    }

    public bool ShowCandles
    {
        get => _showCandles;
        private set => SetField(ref _showCandles, value);
    }

    public async Task SetShowCandlesAsync(bool isEnabled)
    {
        if (ShowCandles == isEnabled)
        {
            return;
        }

        ShowCandles = isEnabled;
        if (!ShowCandles)
        {
            ChartCandles.Clear();
            return;
        }

        await LoadCandlesAsync();
    }

    public async Task SetSelectedFuturesPriceAsync(decimal? price)
    {
        if (!price.HasValue)
        {
            // Clearing the field should reset to live exchange price.
            price = await GetCurrentExchangeFuturesPriceAsync()
                    ?? SelectedUnderlyingPrice;
        }

        if (price.HasValue && price.Value <= 0m)
        {
            price = null;
        }
        else if (price.HasValue)
        {
            price = decimal.Round(price.Value, 2, MidpointRounding.AwayFromZero);
        }

        SelectedFuturesPrice = price;
        RefreshMarkers();
        RecalculateEstimatedPnl();
    }

    public async Task LoadAsync(string? selectedId = null)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var items = await _positionsService.GetDeribitEthPositionsAsync(DateStartUtc, DateEndUtc);
            Positions = items
                .OrderByDescending(item => item.Quantity)
                .ThenByDescending(item => item.NumTrades ?? 0)
                .ToArray();

            var selected = ResolveSelectedById(selectedId) ?? Positions.FirstOrDefault();
            await SelectPositionAsync(selected);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Positions = Array.Empty<StrategyPosition>();
            ChartStrategies.Clear();
            SelectedPosition = null;
            SelectedUnderlyingPrice = null;
            OpenIndexPrice = null;
            SelectedFuturesPrice = null;
            EstimatedPnl = null;
            EstimatedPnlPercent = null;
            InvestedAmount = null;
            _lastPayoffPrices = Array.Empty<decimal>();
            _lastPayoffTemp = Array.Empty<decimal>();
            SelectedPositionDetails = Array.Empty<PositionTradeDetailRow>();
            ChartMarkers.Clear();
            ChartCandles.Clear();
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task ReloadAsync(string? selectedId = null)
    {
        _positionsService.Invalidate(DateStartUtc, DateEndUtc);
        await LoadAsync(selectedId);
    }

    public async Task SelectByIdAsync(string? id)
    {
        var selected = ResolveSelectedById(id) ?? Positions.FirstOrDefault();
        await SelectPositionAsync(selected);
    }

    public async Task SelectPositionAsync(StrategyPosition? position)
    {
        ErrorMessage = null;
        SelectedPosition = position;
        if (position is null)
        {
            ChartStrategies.Clear();
            SelectedUnderlyingPrice = null;
            OpenIndexPrice = null;
            SelectedFuturesPrice = null;
            EstimatedPnl = null;
            EstimatedPnlPercent = null;
            InvestedAmount = null;
            _lastPayoffPrices = Array.Empty<decimal>();
            _lastPayoffTemp = Array.Empty<decimal>();
            SelectedPositionDetails = Array.Empty<PositionTradeDetailRow>();
            ChartMarkers.Clear();
            ChartCandles.Clear();
            return;
        }

        try
        {
            var snapshot = await _positionsService.GetPositionSnapshotAsync(position, DateStartUtc, DateEndUtc);
            var legs = snapshot.Legs;
            SelectedPositionDetails = snapshot.Details;
            if (legs.Count == 0)
            {
                legs = new[] { position.Leg.Clone() };
            }
            await ApplyMarketContextAsync(legs);
            OpenIndexPrice = position.IndexPrice;
            InvestedAmount = ResolveInvestedAmount(position, SelectedPositionDetails);
            SelectedFuturesPrice = await GetCurrentExchangeFuturesPriceAsync() ?? SelectedUnderlyingPrice;
            RefreshMarkers();
            await LoadCandlesAsync();

            var (xs, profits, theoretical) = _optionsService.GeneratePosition(
                legs,
                points: 180,
                valuationDate: DateTime.UtcNow);
            _lastPayoffPrices = xs.ToArray();
            _lastPayoffTemp = theoretical.ToArray();
            RecalculateEstimatedPnl();

            var temp = BuildPayoffPoints(xs, theoretical);
            var exp = BuildPayoffPoints(xs, profits);
            var series = ChartStrategies.FirstOrDefault();
            if (series is null)
            {
                ChartStrategies.Add(new StrategySeries(
                    position.Id,
                    position.Instrument,
                    "#00A6FB",
                    showBreakEvens: true,
                    tempPnl: temp,
                    expiredPnl: exp,
                    visible: true));
            }
            else
            {
                series.Id = position.Id;
                series.Name = position.Instrument;
                series.TempPnl = temp;
                series.ExpiredPnl = exp;
                series.Visible = true;
                series.ShowBreakEvens = true;
            }
        }
        catch (Exception ex)
        {
            // Keep page responsive when details endpoint fails for a specific trade.
            ErrorMessage = $"Failed to load trade details: {ex.Message}";
            SelectedPositionDetails = Array.Empty<PositionTradeDetailRow>();
            var fallbackLegs = new[] { position.Leg.Clone() };
            await ApplyMarketContextAsync(fallbackLegs);
            OpenIndexPrice = position.IndexPrice;
            InvestedAmount = ResolveInvestedAmount(position, SelectedPositionDetails);
            SelectedFuturesPrice = await GetCurrentExchangeFuturesPriceAsync() ?? SelectedUnderlyingPrice;
            RefreshMarkers();
            await LoadCandlesAsync();

            var (xs, profits, theoretical) = _optionsService.GeneratePosition(
                fallbackLegs,
                points: 180,
                valuationDate: DateTime.UtcNow);
            _lastPayoffPrices = xs.ToArray();
            _lastPayoffTemp = theoretical.ToArray();
            RecalculateEstimatedPnl();
            var temp = BuildPayoffPoints(xs, theoretical);
            var exp = BuildPayoffPoints(xs, profits);
            ChartStrategies.Clear();
            ChartStrategies.Add(new StrategySeries(
                position.Id,
                position.Instrument,
                "#00A6FB",
                showBreakEvens: true,
                tempPnl: temp,
                expiredPnl: exp,
                visible: true));
        }
    }

    private StrategyPosition? ResolveSelectedById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return Positions.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Instrument, id, StringComparison.OrdinalIgnoreCase));
    }

    private async Task ApplyMarketContextAsync(IReadOnlyList<LegModel> legs)
    {
        var firstLeg = legs.FirstOrDefault();
        var baseAsset = ExtractBaseAsset(firstLeg?.Symbol);
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            SelectedUnderlyingPrice = null;
            return;
        }

        await _exchangeService.OptionsChain.UpdateTickersAsync(baseAsset);

        decimal? underlying = null;
        foreach (var leg in legs)
        {
            var ticker = _exchangeService.OptionsChain.FindTickerForLeg(leg, baseAsset);
            if (ticker is null)
            {
                continue;
            }

            if (!leg.ImpliedVolatility.HasValue || leg.ImpliedVolatility.Value <= 0m)
            {
                var iv = NormalizeIv(ticker.MarkIv) ?? NormalizeIv(ticker.BidIv) ?? NormalizeIv(ticker.AskIv);
                if (iv.HasValue)
                {
                    leg.ImpliedVolatility = iv.Value;
                }
            }

            if (ticker.UnderlyingPrice.HasValue)
            {
                underlying = ticker.UnderlyingPrice;
            }
        }

        SelectedUnderlyingPrice = underlying;
        RefreshMarkers();
        if (!SelectedFuturesPrice.HasValue && underlying.HasValue)
        {
            SelectedFuturesPrice = underlying.Value;
            RecalculateEstimatedPnl();
        }
    }

    private static string ExtractBaseAsset(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return string.Empty;
        }

        var dash = symbol.IndexOf('-');
        return (dash > 0 ? symbol[..dash] : symbol).Trim().ToUpperInvariant();
    }

    private static decimal? NormalizeIv(decimal? value)
    {
        if (!value.HasValue || value.Value <= 0m)
        {
            return null;
        }

        return value.Value <= 3m ? value.Value * 100m : value.Value;
    }

    private static IReadOnlyList<PayoffPoint> BuildPayoffPoints(IReadOnlyList<decimal> prices, IReadOnlyList<decimal> profits)
    {
        var result = new List<PayoffPoint>(Math.Min(prices.Count, profits.Count));
        for (var i = 0; i < prices.Count && i < profits.Count; i++)
        {
            result.Add(new PayoffPoint((double)prices[i], (double)profits[i]));
        }

        return result;
    }

    private void RefreshMarkers()
    {
        ChartMarkers.Clear();

        var openIndex = OpenIndexPrice;
        if (openIndex.HasValue && openIndex.Value > 0m)
        {
            ChartMarkers.Add(new PriceMarker((double)openIndex.Value, $"Open Index {openIndex.Value:0.##}", "#F59E0B"));
        }
    }

    private void RecalculateEstimatedPnl()
    {
        if (!SelectedFuturesPrice.HasValue || _lastPayoffPrices.Count == 0 || _lastPayoffTemp.Count == 0)
        {
            EstimatedPnl = null;
            EstimatedPnlPercent = null;
            return;
        }

        var x = SelectedFuturesPrice.Value;
        var y = InterpolatePnlAtPrice(_lastPayoffPrices, _lastPayoffTemp, x);
        EstimatedPnl = y;

        if (InvestedAmount.HasValue && InvestedAmount.Value > 0m)
        {
            EstimatedPnlPercent = decimal.Round((y / InvestedAmount.Value) * 100m, 2, MidpointRounding.AwayFromZero);
        }
        else
        {
            EstimatedPnlPercent = null;
        }
    }

    private static decimal InterpolatePnlAtPrice(IReadOnlyList<decimal> xs, IReadOnlyList<decimal> ys, decimal x)
    {
        var count = Math.Min(xs.Count, ys.Count);
        if (count == 0)
        {
            return 0m;
        }

        if (x <= xs[0])
        {
            return ys[0];
        }

        if (x >= xs[count - 1])
        {
            return ys[count - 1];
        }

        for (var i = 1; i < count; i++)
        {
            var x0 = xs[i - 1];
            var x1 = xs[i];
            if (x < x0 || x > x1)
            {
                continue;
            }

            var y0 = ys[i - 1];
            var y1 = ys[i];
            if (x1 == x0)
            {
                return y0;
            }

            var t = (x - x0) / (x1 - x0);
            return y0 + (y1 - y0) * t;
        }

        return ys[count - 1];
    }

    private async Task LoadCandlesAsync()
    {
        ChartCandles.Clear();
        if (!ShowCandles || SelectedPosition is null)
        {
            return;
        }

        var baseAsset = ExtractBaseAsset(SelectedPosition.Instrument);
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return;
        }

        var symbol = $"{baseAsset}USDT";
        var candles = await _exchangeService.Tickers.GetCandlesAsync(symbol, DateStartUtc, DateEndUtc);
        foreach (var candle in candles)
        {
            ChartCandles.Add(candle);
        }
    }

    private static decimal? ResolveInvestedAmount(StrategyPosition position, IReadOnlyList<PositionTradeDetailRow> details)
    {
        var netPremiumAbs = Math.Abs(position.NetPremium ?? 0m);
        if (netPremiumAbs > 0m)
        {
            return netPremiumAbs;
        }

        var gross = details
            .Select(item => item.SizeUsd ?? 0m)
            .Sum(value => Math.Abs(value));

        return gross > 0m ? gross : null;
    }

    private async Task<decimal?> GetCurrentExchangeFuturesPriceAsync()
    {
        if (SelectedPosition is null)
        {
            return null;
        }

        var baseAsset = ExtractBaseAsset(SelectedPosition.Instrument);
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return null;
        }

        await _exchangeService.OptionsChain.UpdateTickersAsync(baseAsset);
        var underlying = _exchangeService.OptionsChain
            .GetTickersByBaseAsset(baseAsset)
            .Select(item => item.UnderlyingPrice)
            .FirstOrDefault(value => value.HasValue && value.Value > 0m);

        if (underlying.HasValue)
        {
            SelectedUnderlyingPrice = underlying.Value;
        }

        return underlying;
    }
}
