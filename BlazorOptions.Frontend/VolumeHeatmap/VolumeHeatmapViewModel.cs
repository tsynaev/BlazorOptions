using BlazorOptions.API.Common;
using BlazorOptions.Services;
using BlazorChart.Models;

namespace BlazorOptions.ViewModels;

public sealed class VolumeHeatmapViewModel : Bindable
{
    private static readonly string[] Hours = Enumerable.Range(0, 24)
        .Select(hour => $"{hour:00}")
        .ToArray();

    private static readonly string[] Weekdays = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
    private static readonly IReadOnlyList<VolumeIntervalOption> IntervalOptions = new[]
    {
        new VolumeIntervalOption("7 days", TimeSpan.FromDays(7)),
        new VolumeIntervalOption("1 month", TimeSpan.FromDays(30)),
        new VolumeIntervalOption("3 months", TimeSpan.FromDays(91)),
        new VolumeIntervalOption("6 months", TimeSpan.FromDays(183)),
        new VolumeIntervalOption("12 months", TimeSpan.FromDays(365))
    };
    private static readonly IReadOnlyList<VolumeHeatmapMetricOption> MetricOptions = new[]
    {
        new VolumeHeatmapMetricOption("Avg volume per hour", VolumeHeatmapMetric.AvgVolumePerHour),
        new VolumeHeatmapMetricOption("Avg diff between open and close", VolumeHeatmapMetric.AvgOpenCloseDiffPerHour)
    };
    private readonly IExchangeService _exchangeService;
    private string _baseAsset = "BTC";
    private string _quoteAsset = "USDT";
    private bool _isLoading;
    private string? _errorMessage;
    private DateTime? _loadedAtUtc;
    private long _klinesCount;
    private VolumeHeatmapChartOptions? _chart;
    private IReadOnlyList<string> _baseAssets = Array.Empty<string>();
    private IReadOnlyList<string> _quoteAssets = Array.Empty<string>();
    private bool _isInitialized;
    private TimeSpan _selectedInterval = TimeSpan.FromDays(183);
    private VolumeHeatmapMetric _selectedMetric = VolumeHeatmapMetric.AvgVolumePerHour;
    private IReadOnlyList<CandleVolumePoint> _lastCandles = Array.Empty<CandleVolumePoint>();
    private DateTime _lastFromUtc;
    private DateTime _lastToUtc;

    public VolumeHeatmapViewModel(IExchangeService exchangeService)
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

    public DateTime? LoadedAtUtc
    {
        get => _loadedAtUtc;
        private set => SetField(ref _loadedAtUtc, value);
    }

    public long KlinesCount
    {
        get => _klinesCount;
        private set => SetField(ref _klinesCount, value);
    }

    public VolumeHeatmapChartOptions? Chart
    {
        get => _chart;
        private set => SetField(ref _chart, value);
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

    public IReadOnlyList<VolumeIntervalOption> AvailableIntervals => IntervalOptions;
    public IReadOnlyList<VolumeHeatmapMetricOption> AvailableMetrics => MetricOptions;

    public TimeSpan SelectedInterval
    {
        get => _selectedInterval;
        private set => SetField(ref _selectedInterval, value);
    }

    public VolumeHeatmapMetric SelectedMetric
    {
        get => _selectedMetric;
        private set => SetField(ref _selectedMetric, value);
    }

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
        if (string.IsNullOrWhiteSpace(normalized) || normalized == BaseAsset)
        {
            return Task.CompletedTask;
        }

        BaseAsset = normalized;
        return Task.CompletedTask;
    }

    public Task SetQuoteAssetAsync(string value)
    {
        var normalized = NormalizeAsset(value);
        if (string.IsNullOrWhiteSpace(normalized) || normalized == QuoteAsset)
        {
            return Task.CompletedTask;
        }

        QuoteAsset = normalized;
        return Task.CompletedTask;
    }

    public async Task LoadHeatmapAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var toUtc = DateTime.UtcNow;
            var fromUtc = toUtc - SelectedInterval;
            Chart = null;
            var candles = await _exchangeService.Tickers.GetCandlesWithVolumeAsync(
                Symbol,
                fromUtc,
                toUtc,
                60);

            _lastCandles = candles;
            _lastFromUtc = fromUtc;
            _lastToUtc = toUtc;
            KlinesCount = candles.Count;
            Chart = BuildChart(Symbol, fromUtc, toUtc, candles, SelectedMetric);
            LoadedAtUtc = DateTime.UtcNow;

            if (candles.Count == 0)
            {
                ErrorMessage = $"No candles returned for {Symbol}.";
            }
        }
        catch (Exception ex)
        {
            Chart = null;
            KlinesCount = 0;
            ErrorMessage = ex.Message;
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
            var pairs = await _exchangeService.FuturesInstruments.GetTradingPairsAsync();
            if (pairs.Count == 0)
            {
                BaseAssets = new[] { BaseAsset };
                QuoteAssets = new[] { QuoteAsset };
                return;
            }

            BaseAssets = pairs
                .Select(pair => pair.BaseAsset)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(asset => asset, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            QuoteAssets = pairs
                .Select(pair => pair.QuoteAsset)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(asset => asset, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (!BaseAssets.Contains(BaseAsset, StringComparer.OrdinalIgnoreCase))
            {
                BaseAsset = BaseAssets.FirstOrDefault() ?? BaseAsset;
            }

            if (!QuoteAssets.Contains(QuoteAsset, StringComparer.OrdinalIgnoreCase))
            {
                QuoteAsset = QuoteAssets.FirstOrDefault() ?? QuoteAsset;
            }
        }
        catch
        {
            BaseAssets = new[] { BaseAsset };
            QuoteAssets = new[] { QuoteAsset };
        }
    }

    private static VolumeHeatmapChartOptions BuildChart(
        string symbol,
        DateTime fromUtc,
        DateTime toUtc,
        IReadOnlyList<CandleVolumePoint> candles,
        VolumeHeatmapMetric metric)
    {
        var sumMatrix = new double[7, 24];
        var countMatrix = new int[7, 24];
        foreach (var candle in candles)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(candle.Time).UtcDateTime;
            var dayIndex = ToMondayFirstIndex(time.DayOfWeek);
            var hour = time.Hour;
            var value = metric switch
            {
                VolumeHeatmapMetric.AvgOpenCloseDiffPerHour => Math.Abs(candle.Close - candle.Open),
                _ => Math.Max(0, candle.Volume)
            };
            sumMatrix[dayIndex, hour] += value;
            countMatrix[dayIndex, hour]++;
        }

        var cells = new List<VolumeHeatmapCell>(7 * 24);
        VolumeHeatmapCell? maxCell = null;
        var minVolume = double.PositiveInfinity;
        double maxVolume = 0;
        for (var day = 0; day < 7; day++)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                double volume;
                if (countMatrix[day, hour] > 0)
                {
                    volume = sumMatrix[day, hour] / countMatrix[day, hour];
                }
                else
                {
                    volume = 0;
                }
                var cell = new VolumeHeatmapCell(hour, day, volume);
                cells.Add(cell);
                if (volume < minVolume)
                {
                    minVolume = volume;
                }
                if (volume > maxVolume)
                {
                    maxVolume = volume;
                    maxCell = cell;
                }
            }
        }

        return new VolumeHeatmapChartOptions(
            symbol,
            fromUtc,
            toUtc,
            metric,
            Hours,
            Weekdays,
            cells,
            maxCell,
            double.IsFinite(minVolume) ? minVolume : 0,
            maxVolume);
    }

    private static int ToMondayFirstIndex(DayOfWeek dayOfWeek)
    {
        return dayOfWeek switch
        {
            DayOfWeek.Monday => 0,
            DayOfWeek.Tuesday => 1,
            DayOfWeek.Wednesday => 2,
            DayOfWeek.Thursday => 3,
            DayOfWeek.Friday => 4,
            DayOfWeek.Saturday => 5,
            _ => 6
        };
    }

    private static string NormalizeAsset(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
    }

    public Task SetIntervalAsync(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero || interval == SelectedInterval)
        {
            return Task.CompletedTask;
        }

        SelectedInterval = interval;
        return Task.CompletedTask;
    }

    public Task SetMetricAsync(VolumeHeatmapMetric metric)
    {
        if (metric == SelectedMetric)
        {
            return Task.CompletedTask;
        }

        SelectedMetric = metric;
        if (_lastCandles.Count > 0)
        {
            Chart = BuildChart(Symbol, _lastFromUtc, _lastToUtc, _lastCandles, SelectedMetric);
        }

        return Task.CompletedTask;
    }
}

public sealed record VolumeIntervalOption(string Label, TimeSpan Duration);
