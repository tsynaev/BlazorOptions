using BlazorOptions.Services;
using BlazorChart.Models;

namespace BlazorOptions.ViewModels;

public enum StraddleFairMethod
{
    Range,
    Parkinson
}

public sealed record WeekStat(
    DateTime Date,
    double High,
    double Low,
    double Close,
    double Range,
    double Sigma);

public sealed class StraddleFairValueViewModel : Bindable
{
    private readonly IExchangeService _exchangeService;
    private string _underlyingSymbol = "ETHUSDT";
    private int _weeksToAverage = 6;
    private StraddleFairMethod _method = StraddleFairMethod.Range;
    private bool _isLoading;
    private string? _errorMessage;
    private string? _warningMessage;
    private IReadOnlyList<WeekStat> _weeks = Array.Empty<WeekStat>();
    private double? _currentUnderlyingPrice;
    private double? _actualAtmCallPrice;
    private double? _actualAtmPutPrice;
    private double? _actualAtmStraddle;
    private double? _avgRange;
    private double? _sigmaWeek;
    private double? _fairStraddle;
    private double? _difference;
    private double? _differencePercent;
    private DateTime? _targetExpiry;
    private double? _targetStrike;
    private IReadOnlyList<ExchangeTradingPair> _tradingPairs = Array.Empty<ExchangeTradingPair>();
    private bool _isInitialized;

    public StraddleFairValueViewModel(IExchangeService exchangeService)
    {
        _exchangeService = exchangeService;
    }

    public string UnderlyingSymbol
    {
        get => _underlyingSymbol;
        private set => SetField(ref _underlyingSymbol, value);
    }

    public int WeeksToAverage
    {
        get => _weeksToAverage;
        private set => SetField(ref _weeksToAverage, value);
    }

    public StraddleFairMethod Method
    {
        get => _method;
        private set => SetField(ref _method, value);
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

    public string? WarningMessage
    {
        get => _warningMessage;
        private set => SetField(ref _warningMessage, value);
    }

    public IReadOnlyList<WeekStat> Weeks
    {
        get => _weeks;
        private set => SetField(ref _weeks, value);
    }

    public double? CurrentUnderlyingPrice
    {
        get => _currentUnderlyingPrice;
        private set => SetField(ref _currentUnderlyingPrice, value);
    }

    public double? ActualAtmCallPrice
    {
        get => _actualAtmCallPrice;
        private set => SetField(ref _actualAtmCallPrice, value);
    }

    public double? ActualAtmPutPrice
    {
        get => _actualAtmPutPrice;
        private set => SetField(ref _actualAtmPutPrice, value);
    }

    public double? ActualAtmStraddle
    {
        get => _actualAtmStraddle;
        private set => SetField(ref _actualAtmStraddle, value);
    }

    public double? AvgRange
    {
        get => _avgRange;
        private set => SetField(ref _avgRange, value);
    }

    public double? SigmaWeek
    {
        get => _sigmaWeek;
        private set => SetField(ref _sigmaWeek, value);
    }

    public double? FairStraddle
    {
        get => _fairStraddle;
        private set => SetField(ref _fairStraddle, value);
    }

    public double? Difference
    {
        get => _difference;
        private set => SetField(ref _difference, value);
    }

    public double? DifferencePercent
    {
        get => _differencePercent;
        private set => SetField(ref _differencePercent, value);
    }

    public DateTime? TargetExpiry
    {
        get => _targetExpiry;
        private set => SetField(ref _targetExpiry, value);
    }

    public double? TargetStrike
    {
        get => _targetStrike;
        private set => SetField(ref _targetStrike, value);
    }

    public bool HasEnoughHistory => Weeks.Count >= 2;

    public bool CanShowResult => CurrentUnderlyingPrice.HasValue
                                 && ActualAtmCallPrice.HasValue
                                 && ActualAtmPutPrice.HasValue
                                 && FairStraddle.HasValue
                                 && FairStraddle.Value > 0d
                                 && HasEnoughHistory;

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        await EnsureTradingPairsAsync();
        await RecalculateAsync();
    }

    public async Task SetUnderlyingSymbolAsync(string symbol)
    {
        var normalized = NormalizeSymbol(symbol);
        if (string.Equals(UnderlyingSymbol, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        UnderlyingSymbol = normalized;
        await RecalculateAsync();
    }

    public async Task SetWeeksToAverageAsync(int weeks)
    {
        var normalized = Math.Clamp(weeks, 2, 52);
        if (WeeksToAverage == normalized)
        {
            return;
        }

        WeeksToAverage = normalized;
        await RecalculateAsync();
    }

    public async Task SetMethodAsync(StraddleFairMethod method)
    {
        if (Method == method)
        {
            return;
        }

        Method = method;
        await RecalculateAsync();
    }

    public async Task RecalculateAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        WarningMessage = null;

        try
        {
            var symbol = NormalizeSymbol(UnderlyingSymbol);
            if (string.IsNullOrWhiteSpace(symbol))
            {
                ApplyEmptyResult("Underlying symbol is required.");
                return;
            }

            var currentPrice = await LoadCurrentPriceAsync(symbol);
            CurrentUnderlyingPrice = currentPrice;

            var weeklyStats = await LoadWeeklyStatsAsync(symbol, WeeksToAverage);
            Weeks = weeklyStats;

            if (weeklyStats.Count < 2)
            {
                WarningMessage = $"Only {weeklyStats.Count} completed weeks available. At least 2 weeks are recommended.";
            }

            var averageRange = StraddleMath.Avg(weeklyStats.Select(item => item.Range));
            AvgRange = averageRange;

            var sigmaWeek = Method switch
            {
                StraddleFairMethod.Range => averageRange / StraddleMath.RANGE_TO_SIGMA,
                _ => StraddleMath.Avg(weeklyStats.Select(item => item.Sigma))
            };
            SigmaWeek = sigmaWeek;

            FairStraddle = currentPrice.HasValue ? StraddleMath.FairStraddle(currentPrice.Value, sigmaWeek) : null;

            var actual = await LoadActualAtmStraddleAsync(symbol, currentPrice);
            ActualAtmCallPrice = actual.CallPrice;
            ActualAtmPutPrice = actual.PutPrice;
            ActualAtmStraddle = actual.CallPrice.HasValue && actual.PutPrice.HasValue
                ? actual.CallPrice.Value + actual.PutPrice.Value
                : null;
            TargetExpiry = actual.Expiry;
            TargetStrike = actual.Strike;

            if (ActualAtmStraddle.HasValue && FairStraddle.HasValue && FairStraddle.Value > 0d)
            {
                Difference = ActualAtmStraddle.Value - FairStraddle.Value;
                DifferencePercent = (ActualAtmStraddle.Value / FairStraddle.Value) - 1d;
            }
            else
            {
                Difference = null;
                DifferencePercent = null;
            }

            if (!CurrentUnderlyingPrice.HasValue)
            {
                WarningMessage = "Current underlying price is missing from market data.";
            }
            else if (!ActualAtmCallPrice.HasValue || !ActualAtmPutPrice.HasValue)
            {
                WarningMessage = "ATM option prices are missing for the selected symbol.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            ApplyEmptyResult("Failed to calculate fair straddle.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyEmptyResult(string warning)
    {
        WarningMessage = warning;
        Weeks = Array.Empty<WeekStat>();
        CurrentUnderlyingPrice = null;
        ActualAtmCallPrice = null;
        ActualAtmPutPrice = null;
        ActualAtmStraddle = null;
        AvgRange = null;
        SigmaWeek = null;
        FairStraddle = null;
        Difference = null;
        DifferencePercent = null;
        TargetExpiry = null;
        TargetStrike = null;
    }

    private async Task EnsureTradingPairsAsync()
    {
        if (_tradingPairs.Count > 0)
        {
            return;
        }

        try
        {
            _tradingPairs = await _exchangeService.FuturesInstruments.GetTradingPairsAsync();
        }
        catch
        {
            _tradingPairs = Array.Empty<ExchangeTradingPair>();
        }
    }

    private async Task<double?> LoadCurrentPriceAsync(string symbol)
    {
        var toUtc = DateTime.UtcNow;
        var fromUtc = toUtc.AddHours(-4);
        var candles = await _exchangeService.Tickers.GetCandlesWithVolumeAsync(symbol, fromUtc, toUtc, intervalMinutes: 60);
        if (candles.Count == 0)
        {
            return null;
        }

        var latest = candles.MaxBy(item => item.Time);
        return latest?.Close;
    }

    private async Task<IReadOnlyList<WeekStat>> LoadWeeklyStatsAsync(string symbol, int weeks)
    {
        var currentWeekStart = GetWeekStartUtc(DateTime.UtcNow);
        var fromUtc = currentWeekStart.AddDays(-7 * weeks);

        var candles = await _exchangeService.Tickers.GetCandlesWithVolumeAsync(symbol, fromUtc, currentWeekStart, intervalMinutes: 60);
        if (candles.Count == 0)
        {
            return Array.Empty<WeekStat>();
        }

        var grouped = candles
            .Select(item => new
            {
                Time = DateTimeOffset.FromUnixTimeMilliseconds(item.Time).UtcDateTime,
                Candle = item
            })
            .Where(item => item.Time >= fromUtc && item.Time < currentWeekStart)
            .GroupBy(item => GetWeekStartUtc(item.Time))
            .OrderByDescending(group => group.Key)
            .Take(weeks)
            .ToList();

        var result = new List<WeekStat>(grouped.Count);
        foreach (var group in grouped)
        {
            var ordered = group.OrderBy(item => item.Time).ToList();
            if (ordered.Count == 0)
            {
                continue;
            }

            var high = ordered.Max(item => item.Candle.High);
            var low = ordered.Min(item => item.Candle.Low);
            var close = ordered[^1].Candle.Close;
            var range = StraddleMath.CalcRange(high, low, close);
            var sigma = StraddleMath.CalcParkinson(high, low);

            result.Add(new WeekStat(group.Key, high, low, close, range, sigma));
        }

        return result
            .OrderByDescending(item => item.Date)
            .ToArray();
    }

    private async Task<(double? CallPrice, double? PutPrice, DateTime? Expiry, double? Strike)> LoadActualAtmStraddleAsync(string symbol, double? underlyingPrice)
    {
        var resolved = await ResolveSymbolPartsAsync(symbol);
        if (string.IsNullOrWhiteSpace(resolved.BaseAsset) || !underlyingPrice.HasValue || underlyingPrice.Value <= 0d)
        {
            return (null, null, null, null);
        }

        await _exchangeService.OptionsChain.UpdateTickersAsync(resolved.BaseAsset);

        var now = DateTime.UtcNow;
        var tickers = _exchangeService.OptionsChain.GetTickersByBaseAsset(resolved.BaseAsset)
            .Where(ticker => ticker.ExpirationDate.Date > now.Date)
            .Where(ticker => MatchesQuote(ticker.Symbol, resolved.QuoteAsset))
            .ToList();

        if (tickers.Count == 0)
        {
            return (null, null, null, null);
        }

        var expiry = ResolveTargetWeeklyExpiry(tickers.Select(t => t.ExpirationDate), now);
        if (!expiry.HasValue)
        {
            return (null, null, null, null);
        }

        var expiryTickers = tickers
            .Where(ticker => ticker.ExpirationDate.Date == expiry.Value.Date)
            .ToList();

        var strike = expiryTickers
            .Select(ticker => ticker.Strike)
            .Distinct()
            .OrderBy(value => Math.Abs((double)value - underlyingPrice.Value))
            .FirstOrDefault();

        var call = expiryTickers.FirstOrDefault(ticker => ticker.Type == LegType.Call && ticker.Strike == strike);
        var put = expiryTickers.FirstOrDefault(ticker => ticker.Type == LegType.Put && ticker.Strike == strike);

        return (
            ResolveOptionPrice(call),
            ResolveOptionPrice(put),
            expiry,
            (double)strike);
    }

    private static double? ResolveOptionPrice(OptionChainTicker? ticker)
    {
        if (ticker is null)
        {
            return null;
        }

        if (ticker.MarkPrice > 0m)
        {
            return (double)ticker.MarkPrice;
        }

        if (ticker.BidPrice > 0m && ticker.AskPrice > 0m)
        {
            return (double)((ticker.BidPrice + ticker.AskPrice) / 2m);
        }

        if (ticker.LastPrice > 0m)
        {
            return (double)ticker.LastPrice;
        }

        return null;
    }

    private async Task<(string BaseAsset, string QuoteAsset)> ResolveSymbolPartsAsync(string symbol)
    {
        await EnsureTradingPairsAsync();
        if (_tradingPairs.Count > 0)
        {
            var matched = _tradingPairs
                .Where(pair => symbol.StartsWith(pair.BaseAsset, StringComparison.OrdinalIgnoreCase)
                               && symbol.EndsWith(pair.QuoteAsset, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(pair => pair.BaseAsset.Length)
                .ThenByDescending(pair => pair.QuoteAsset.Length)
                .FirstOrDefault();

            if (matched is not null)
            {
                return (matched.BaseAsset.ToUpperInvariant(), matched.QuoteAsset.ToUpperInvariant());
            }
        }

        var baseAssets = _exchangeService.OptionsChain.GetCachedBaseAssets();
        var baseAsset = baseAssets
            .OrderByDescending(item => item.Length)
            .FirstOrDefault(item => symbol.StartsWith(item, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return (string.Empty, string.Empty);
        }

        var quoteAsset = symbol[baseAsset.Length..];
        return (baseAsset.ToUpperInvariant(), quoteAsset.ToUpperInvariant());
    }

    private static DateTime GetWeekStartUtc(DateTime valueUtc)
    {
        var date = valueUtc.Date;
        var shift = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-shift);
    }

    private static DateTime? ResolveTargetWeeklyExpiry(IEnumerable<DateTime> expirations, DateTime nowUtc)
    {
        var target = nowUtc.Date.AddDays(7);
        return expirations
            .Select(value => value.Date)
            .Distinct()
            .OrderBy(value => Math.Abs((value - target).TotalDays))
            .ThenBy(value => value)
            .FirstOrDefault();
    }

    private static bool MatchesQuote(string symbol, string quoteAsset)
    {
        if (string.IsNullOrWhiteSpace(quoteAsset))
        {
            return true;
        }

        return symbol.EndsWith(quoteAsset, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSymbol(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().Replace("/", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
    }
}
