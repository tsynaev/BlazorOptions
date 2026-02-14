using BlazorOptions.API.Common;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class VolatilitySkewViewModel : Bindable
{
    private static readonly string[] SeriesPalette =
    [
        "#38bdf8",
        "#22c55e",
        "#f59e0b",
        "#f97316",
        "#ec4899",
        "#a78bfa",
        "#ef4444"
    ];

    private readonly IExchangeService _exchangeService;
    private List<OptionChainTicker> _cachedTickers = new();
    private string _baseAsset = "BTC";
    private string _quoteAsset = "USDT";
    private string _selectedInstrument = "BTC/USDT";
    private bool _isLoading;
    private string? _errorMessage;
    private IReadOnlyList<string> _availableInstruments = Array.Empty<string>();
    private IReadOnlyList<VolatilitySkewExpirationChip> _expirationChips = Array.Empty<VolatilitySkewExpirationChip>();
    private VolatilitySkewChartOptions? _chart;
    private LegType _selectedType = LegType.Call;

    public VolatilitySkewViewModel(IExchangeService exchangeService)
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

    public string SelectedInstrument
    {
        get => _selectedInstrument;
        private set => SetField(ref _selectedInstrument, value);
    }

    public IReadOnlyList<string> AvailableInstruments
    {
        get => _availableInstruments;
        private set => SetField(ref _availableInstruments, value);
    }

    public IReadOnlyList<VolatilitySkewExpirationChip> ExpirationChips
    {
        get => _expirationChips;
        private set => SetField(ref _expirationChips, value);
    }

    public VolatilitySkewChartOptions? Chart
    {
        get => _chart;
        private set => SetField(ref _chart, value);
    }

    public LegType SelectedType
    {
        get => _selectedType;
        private set => SetField(ref _selectedType, value);
    }

    public async Task InitializeAsync()
    {
        await LoadTradingPairsAsync();
    }

    public Task SetInstrumentAsync(string value)
    {
        if (!TryParseInstrument(value, out var baseAsset, out var quoteAsset))
        {
            return Task.CompletedTask;
        }

        if (string.Equals(BaseAsset, baseAsset, StringComparison.OrdinalIgnoreCase)
            && string.Equals(QuoteAsset, quoteAsset, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        BaseAsset = baseAsset;
        QuoteAsset = quoteAsset;
        SelectedInstrument = FormatInstrument(baseAsset, quoteAsset);
        ClearLoadedData();
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
            await _exchangeService.OptionsChain.RefreshAsync(BaseAsset);
            _cachedTickers = _exchangeService.OptionsChain.GetTickersByBaseAsset(BaseAsset)
                .Where(t => MatchesQuote(t.Symbol, QuoteAsset))
                .Where(t => t.MarkIv > 0m)
                .ToList();

            BuildExpirationChips();
            RebuildChart();

            if (_cachedTickers.Count == 0)
            {
                ErrorMessage = $"No option chain data for {BaseAsset}/{QuoteAsset}.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _cachedTickers.Clear();
            ExpirationChips = Array.Empty<VolatilitySkewExpirationChip>();
            Chart = null;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public Task ToggleExpirationAsync(DateTime expirationDate)
    {
        if (ExpirationChips.Count == 0)
        {
            return Task.CompletedTask;
        }

        var updated = ExpirationChips
            .Select(chip => chip.ExpirationDate == expirationDate
                ? chip with { IsSelected = !chip.IsSelected }
                : chip)
            .ToArray();

        ExpirationChips = updated;
        RebuildChart();
        return Task.CompletedTask;
    }

    public Task SetOptionTypeAsync(LegType type)
    {
        if (type is not LegType.Call and not LegType.Put)
        {
            return Task.CompletedTask;
        }

        if (SelectedType == type)
        {
            return Task.CompletedTask;
        }

        SelectedType = type;
        BuildExpirationChips();
        RebuildChart();
        return Task.CompletedTask;
    }

    private async Task LoadTradingPairsAsync()
    {
        try
        {
            var bases = await _exchangeService.OptionsChain.GetAvailableBaseAssetsAsync();
            var instruments = new List<string>();
            foreach (var baseAsset in bases)
            {
                var quotes = await _exchangeService.OptionsChain.GetAvailableQuoteAssetsAsync(baseAsset);
                instruments.AddRange(quotes.Select(quoteAsset => FormatInstrument(baseAsset, quoteAsset)));
            }

            AvailableInstruments = instruments
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToArray();

            if (AvailableInstruments.Count == 0)
            {
                AvailableInstruments = [FormatInstrument(BaseAsset, QuoteAsset)];
            }

            if (!AvailableInstruments.Contains(SelectedInstrument, StringComparer.OrdinalIgnoreCase))
            {
                SelectedInstrument = AvailableInstruments[0];
            }

            if (TryParseInstrument(SelectedInstrument, out var selectedBase, out var selectedQuote))
            {
                BaseAsset = selectedBase;
                QuoteAsset = selectedQuote;
            }
        }
        catch
        {
            AvailableInstruments = [FormatInstrument(BaseAsset, QuoteAsset)];
            SelectedInstrument = AvailableInstruments[0];
        }
    }

    private void BuildExpirationChips()
    {
        var expirationDates = _cachedTickers
            .Where(t => t.Type == SelectedType)
            .Select(t => t.ExpirationDate.Date)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        if (expirationDates.Length == 0)
        {
            ExpirationChips = Array.Empty<VolatilitySkewExpirationChip>();
            return;
        }

        var currentSelection = ExpirationChips
            .Where(x => x.IsSelected)
            .Select(x => x.ExpirationDate.Date)
            .ToHashSet();

        if (currentSelection.Count == 0)
        {
            currentSelection.Add(expirationDates[0]);
        }

        ExpirationChips = expirationDates
            .Select((exp, index) => new VolatilitySkewExpirationChip(
                exp,
                exp.ToString("yyyy-MM-dd"),
                currentSelection.Contains(exp),
                SeriesPalette[index % SeriesPalette.Length]))
            .ToArray();
    }

    private void RebuildChart()
    {
        var selectedExpirations = ExpirationChips
            .Where(chip => chip.IsSelected)
            .Select(chip => chip.ExpirationDate.Date)
            .ToHashSet();

        if (selectedExpirations.Count == 0 || _cachedTickers.Count == 0)
        {
            Chart = null;
            return;
        }

        var series = new List<VolatilitySkewSeries>(selectedExpirations.Count);
        var colorByExpiration = ExpirationChips.ToDictionary(c => c.ExpirationDate.Date, c => c.ColorHex);
        var currentPrice = _cachedTickers
            .Where(t => t.Type == SelectedType)
            .Where(t => selectedExpirations.Contains(t.ExpirationDate.Date))
            .Select(t => t.UnderlyingPrice)
            .Where(x => x.HasValue && x.Value > 0m)
            .Select(x => (double)x!.Value)
            .DefaultIfEmpty()
            .Average();

        foreach (var expiration in selectedExpirations.OrderBy(x => x))
        {
            var points = _cachedTickers
                .Where(t => t.Type == SelectedType)
                .Where(t => t.ExpirationDate.Date == expiration)
                .GroupBy(t => t.Strike)
                .OrderBy(g => g.Key)
                .Select(g => new VolatilitySkewPoint(
                    (double)g.Key,
                    g.Average(x => (double)x.MarkPrice),
                    g.Average(x => (double)x.BidPrice),
                    g.Average(x => (double)x.AskPrice),
                    g.Average(x => ToIvPercent(x.MarkIv)),
                    g.Average(x => ToIvPercent(x.BidIv)),
                    g.Average(x => ToIvPercent(x.AskIv))))
                .ToArray();

            if (points.Length == 0)
            {
                continue;
            }

            var color = colorByExpiration.TryGetValue(expiration, out var mappedColor)
                ? mappedColor
                : SeriesPalette[series.Count % SeriesPalette.Length];
            series.Add(new VolatilitySkewSeries(expiration.ToString("yyyy-MM-dd"), color, points));
        }

        if (series.Count == 0)
        {
            Chart = null;
            return;
        }

        Chart = new VolatilitySkewChartOptions(
            BaseAsset,
            QuoteAsset,
            series,
            series.Count == 1,
            currentPrice > 0d ? currentPrice : null);
    }

    private void ClearLoadedData()
    {
        _cachedTickers.Clear();
        ExpirationChips = Array.Empty<VolatilitySkewExpirationChip>();
        Chart = null;
        ErrorMessage = null;
    }

    private static string NormalizeAsset(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }

    private static string FormatInstrument(string baseAsset, string quoteAsset)
    {
        return $"{NormalizeAsset(baseAsset)}/{NormalizeAsset(quoteAsset)}";
    }

    private static bool TryParseInstrument(string? instrument, out string baseAsset, out string quoteAsset)
    {
        baseAsset = string.Empty;
        quoteAsset = string.Empty;
        if (string.IsNullOrWhiteSpace(instrument))
        {
            return false;
        }

        var parts = instrument.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        baseAsset = NormalizeAsset(parts[0]);
        quoteAsset = NormalizeAsset(parts[1]);
        return !string.IsNullOrWhiteSpace(baseAsset) && !string.IsNullOrWhiteSpace(quoteAsset);
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

    private static double ToIvPercent(decimal iv)
    {
        if (iv <= 0m)
        {
            return 0d;
        }

        // Bybit can return IV as ratio (0.62) or already scaled (62).
        return (double)(iv <= 3m ? iv * 100m : iv);
    }
}
