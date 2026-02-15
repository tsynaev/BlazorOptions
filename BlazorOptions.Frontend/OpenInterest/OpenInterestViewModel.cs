using BlazorOptions.Services;
using BlazorOptions.API.Positions;

namespace BlazorOptions.ViewModels;

public sealed class OpenInterestViewModel : Bindable
{
    private readonly IExchangeService _exchangeService;
    private IReadOnlyList<ExchangeTradingPair> _tradingPairs = Array.Empty<ExchangeTradingPair>();
    private string _baseAsset = "BTC";
    private string _quoteAsset = "USDT";
    private bool _isLoading;
    private string? _errorMessage;
    private OpenInterestChartOptions? _callChart;
    private OpenInterestChartOptions? _putChart;
    private IReadOnlyList<string> _baseAssets = Array.Empty<string>();
    private IReadOnlyList<string> _quoteAssets = Array.Empty<string>();
    private bool _isInitialized;

    public OpenInterestViewModel(IExchangeService exchangeService)
    {
        _exchangeService = exchangeService;
    }

    public string BaseAsset
    {
        get => _baseAsset;
        set => SetField(ref _baseAsset, value);
    }

    public string QuoteAsset
    {
        get => _quoteAsset;
        set => SetField(ref _quoteAsset, value);
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

    public OpenInterestChartOptions? CallChart
    {
        get => _callChart;
        private set => SetField(ref _callChart, value);
    }

    public OpenInterestChartOptions? PutChart
    {
        get => _putChart;
        private set => SetField(ref _putChart, value);
    }

    public IReadOnlyList<string> BaseAssets
    {
        get => _baseAssets;
        private set => SetField(ref _baseAssets, value);
    }

    public IReadOnlyList<string> QuoteAssets
    {
        get => _quoteAssets;
        private set => SetField(ref _quoteAssets, value);
    }

    public string Symbol => $"{BaseAsset}{QuoteAsset}";

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        await LoadAssetPairsAsync();
    }

    public Task SetBaseAssetAsync(string value)
    {
        var normalized = NormalizeAsset(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            BaseAsset = normalized;
            RefreshQuoteAssets();
        }

        return Task.CompletedTask;
    }

    public Task SetQuoteAssetAsync(string value)
    {
        var normalized = NormalizeAsset(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            QuoteAsset = normalized;
        }

        return Task.CompletedTask;
    }

    public async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            await _exchangeService.OptionsChain.UpdateTickersAsync(BaseAsset);
            var tickers = _exchangeService.OptionsChain.GetTickersByBaseAsset(BaseAsset)
                .Where(t => t.OpenInterest.HasValue && t.OpenInterest.Value > 0m)
                .Where(t => MatchesQuote(t.Symbol, QuoteAsset))
                .ToList();

            CallChart = BuildChart(BaseAsset, QuoteAsset, tickers.Where(t => t.Type == LegType.Call).ToList());
            PutChart = BuildChart(BaseAsset, QuoteAsset, tickers.Where(t => t.Type == LegType.Put).ToList());
            if (tickers.Count == 0)
            {
                ErrorMessage = $"No open interest data for {BaseAsset}/{QuoteAsset}.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            CallChart = null;
            PutChart = null;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadAssetPairsAsync()
    {
        try
        {
            _tradingPairs = await _exchangeService.FuturesInstruments.GetTradingPairsAsync();
            BaseAssets = _tradingPairs
                .Select(p => p.BaseAsset)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToArray();
            RefreshQuoteAssets();

            if (BaseAssets.Count > 0 && !BaseAssets.Contains(BaseAsset, StringComparer.OrdinalIgnoreCase))
            {
                BaseAsset = BaseAssets[0];
                RefreshQuoteAssets();
            }

            if (QuoteAssets.Count > 0 && !QuoteAssets.Contains(QuoteAsset, StringComparer.OrdinalIgnoreCase))
            {
                QuoteAsset = QuoteAssets[0];
            }
        }
        catch
        {
            BaseAssets = new[] { BaseAsset };
            QuoteAssets = new[] { QuoteAsset };
        }
    }

    private void RefreshQuoteAssets()
    {
        var quotes = _tradingPairs
            .Where(p => string.Equals(p.BaseAsset, BaseAsset, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.QuoteAsset)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToArray();

        if (quotes.Length == 0)
        {
            quotes = _tradingPairs
                .Select(p => p.QuoteAsset)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToArray();
        }

        QuoteAssets = quotes;
        if (quotes.Length > 0 && !quotes.Contains(QuoteAsset, StringComparer.OrdinalIgnoreCase))
        {
            QuoteAsset = quotes[0];
        }
    }

    private static OpenInterestChartOptions? BuildChart(
        string baseAsset,
        string quoteAsset,
        IReadOnlyList<OptionChainTicker> tickers)
    {
        if (tickers.Count == 0)
        {
            return null;
        }

        var strikes = tickers
            .Select(t => t.Strike)
            .Distinct()
            .OrderBy(v => v)
            .ToList();

        var expirations = tickers
            .Select(t => t.ExpirationDate.Date)
            .Distinct()
            .OrderBy(v => v)
            .ToList();

        var strikeIndex = strikes
            .Select((value, index) => new { value, index })
            .ToDictionary(x => x.value, x => x.index);
        var expirationIndex = expirations
            .Select((value, index) => new { value, index })
            .ToDictionary(x => x.value, x => x.index);

        var aggregate = new Dictionary<(int Strike, int Expiration), double>();
        foreach (var ticker in tickers)
        {
            if (!ticker.OpenInterest.HasValue)
            {
                continue;
            }

            var key = (
                strikeIndex[ticker.Strike],
                expirationIndex[ticker.ExpirationDate.Date]);
            aggregate.TryGetValue(key, out var current);
            aggregate[key] = current + (double)ticker.OpenInterest.Value;
        }

        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        var points = new List<OpenInterestBar3DPoint>(aggregate.Count);
        foreach (var item in aggregate)
        {
            min = Math.Min(min, item.Value);
            max = Math.Max(max, item.Value);
            points.Add(new OpenInterestBar3DPoint(item.Key.Strike, item.Key.Expiration, item.Value));
        }

        if (!double.IsFinite(min)) min = 0;
        if (!double.IsFinite(max)) max = 0;

        return new OpenInterestChartOptions(
            baseAsset,
            quoteAsset,
            strikes.Select(v => v.ToString("0.##")).ToArray(),
            expirations.Select(v => v.ToString("yyyy-MM-dd")).ToArray(),
            points,
            min,
            max);
    }

    private static bool MatchesQuote(string symbol, string quoteAsset)
    {
        if (string.IsNullOrWhiteSpace(quoteAsset))
        {
            return true;
        }

        var parts = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 5)
        {
            return true;
        }

        return string.Equals(parts[4], quoteAsset, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAsset(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }
}
