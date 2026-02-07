using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using BlazorChart.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BlazorChart.Components;

public sealed partial class PayoffChart : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter] public ObservableCollection<StrategySeries> Strategies { get; set; } = new();
    [Parameter] public double? SelectedPrice { get; set; }
    [Parameter] public ObservableCollection<PriceMarker> Markers { get; set; } = new();
    [Parameter] public ObservableCollection<CandlePoint> Candles { get; set; } = new();
    [Parameter] public bool ShowCandles { get; set; } = true;
    [Parameter] public bool ShowLegends { get; set; } = true;
    [Parameter] public bool IsDarkTheme { get; set; }
    [Parameter] public EventCallback<double?> SelectedPriceChanged { get; set; }
    [Parameter] public EventCallback<ChartRange> RangeChanged { get; set; }
    [Parameter] public EventCallback<TimeRange> TimeRangeChanged { get; set; }

    private ElementReference _chartDiv;
    private IJSObjectReference? _module;
    private DotNetObjectReference<PayoffChart>? _dotNetRef;
    private string? _instanceId;
    private ObservableCollection<StrategySeries>? _lastStrategies;
    private double? _lastSelectedPrice;
    private ObservableCollection<PriceMarker>? _lastMarkers;
    private ObservableCollection<CandlePoint>? _lastCandles;
    private bool _lastShowCandles;
    private bool _lastIsDarkTheme;
    private TimeRange? _lastTimeRangeFromUser;
    private ChartRange? _lastRangeFromUser;
    private IReadOnlyList<StrategySeries>? _subscribedStrategies;
    private readonly Dictionary<string, CancellationTokenSource> _debounceTokens = new();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        _module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/payoffChart.js");
        _dotNetRef = DotNetObjectReference.Create(this);
        _instanceId = await _module.InvokeAsync<string>("init", _chartDiv, _dotNetRef);
        await _module.InvokeVoidAsync("setOption", _instanceId, BuildOption());

        _lastStrategies = Strategies;
        _lastSelectedPrice = SelectedPrice;
        _lastMarkers = Markers;
        _lastCandles = Candles;
        _lastShowCandles = ShowCandles;
        _lastIsDarkTheme = IsDarkTheme;
        SubscribeStrategies(Strategies);
        SubscribeCollection(Strategies);
        SubscribeMarkers(Markers);
        SubscribeCandles(Candles);

        if (SelectedPrice.HasValue)
        {
            await _module.InvokeVoidAsync("setSelectedPrice", _instanceId, SelectedPrice);
        }

        if (Markers.Count > 0)
        {
            await _module.InvokeVoidAsync("setMarkers", _instanceId, Markers);
        }

        if (ShowCandles)
        {
            await _module.InvokeVoidAsync("updateCandles", _instanceId, Candles);
        }

    }

    protected override async Task OnParametersSetAsync()
    {
        if (_module == null || _instanceId == null)
        {
            return;
        }

        if (!ReferenceEquals(_lastStrategies, Strategies))
        {
            UnsubscribeCollection(_lastStrategies);
            await ScheduleOptionUpdate();
            _lastStrategies = Strategies;
            SubscribeStrategies(Strategies);
            SubscribeCollection(Strategies);
        }

        if (_lastSelectedPrice != SelectedPrice)
        {
            await ScheduleSelectedUpdate();
            _lastSelectedPrice = SelectedPrice;
        }

        if (!ReferenceEquals(_lastMarkers, Markers))
        {
            UnsubscribeMarkers(_lastMarkers);
            await ScheduleMarkersUpdate();
            _lastMarkers = Markers;
            SubscribeMarkers(Markers);
        }

        if (!ReferenceEquals(_lastCandles, Candles))
        {
            UnsubscribeCandles(_lastCandles);
            await ScheduleOptionUpdate();
            _lastCandles = Candles;
            SubscribeCandles(Candles);
        }

        if (_lastShowCandles != ShowCandles)
        {
            await ScheduleOptionUpdate();
            _lastShowCandles = ShowCandles;
        }

        if (_lastIsDarkTheme != IsDarkTheme)
        {
            await ScheduleOptionUpdate();
            _lastIsDarkTheme = IsDarkTheme;
        }

    }

    [JSInvokable]
    public async Task OnChartClick(double price)
    {
        Console.WriteLine($"[PayoffChart.OnChartClick] price={price} utc={DateTime.UtcNow:O}");

        _lastSelectedPrice = price;
        if (_module != null && _instanceId != null)
        {
            await _module.InvokeVoidAsync("setSelectedPrice", _instanceId, price);
        }

        if (SelectedPriceChanged.HasDelegate)
        {
            _ = SelectedPriceChanged.InvokeAsync(price);
        }
    }

    [JSInvokable]
    public async Task OnChartClickWithTiming(double price, double clientNow)
    {
        Console.WriteLine($"[PayoffChart.OnChartClickWithTiming] price={price} clientNow={clientNow} utc={DateTime.UtcNow:O}");

        await OnChartClick(price);
    }

    [JSInvokable]
    public async Task OnRangeChanged(double xMin, double xMax, double yMin, double yMax)
    {
        _lastRangeFromUser = new ChartRange(xMin, xMax, yMin, yMax);
        if (RangeChanged.HasDelegate)
        {
            await RangeChanged.InvokeAsync(_lastRangeFromUser);
        }
    }

    [JSInvokable]
    public async Task OnTimeRangeChanged(double min, double max)
    {
        _lastTimeRangeFromUser = new TimeRange(min, max);
        if (TimeRangeChanged.HasDelegate)
        {
            await TimeRangeChanged.InvokeAsync(_lastTimeRangeFromUser);
        }
    }

    private object BuildOption()
    {
        var series = new List<object>();
        var xRange = GetXRange();
        var xMin = _lastRangeFromUser?.XMin ?? xRange.min;
        var xMax = _lastRangeFromUser?.XMax ?? xRange.max;
        var yMin = _lastRangeFromUser?.YMin;
        var yMax = _lastRangeFromUser?.YMax;
        var axisText = IsDarkTheme ? "#cbd5f5" : "#4b5563";
        var axisLine = IsDarkTheme ? "#475569" : "#9ca3af";
        var splitLine = IsDarkTheme ? "rgba(148,163,184,0.25)" : "rgba(148,163,184,0.35)";
        var tooltipBg = IsDarkTheme ? "rgba(15,23,42,0.9)" : "rgba(255,255,255,0.95)";
        var tooltipText = IsDarkTheme ? "#e2e8f0" : "#111827";
        var timeRange = _lastTimeRangeFromUser;
        var timeBounds = GetTimeRange();
        var timeMin = timeRange?.Min ?? timeBounds.min;
        var timeMax = timeRange?.Max ?? timeBounds.max;
        if (ShowCandles)
        {
            var tickerSeries = BuildTickerSeries();
            if (tickerSeries.Count > 0)
            {
                series.AddRange(tickerSeries);
            }
        }

        foreach (var strategy in Strategies.Where(s => s.Visible))
        {
            series.Add(BuildSeries(strategy, isTemp: true));
            series.Add(BuildSeries(strategy, isTemp: false));

            if (strategy.ShowBreakEvens)
            {
                var expiredBe = GetBreakEvens(strategy.ExpiredPnl);
                var tempBe = FilterOverlappingBreakEvens(GetBreakEvens(strategy.TempPnl), expiredBe, GetStep(strategy.ExpiredPnl));
                series.Add(BuildBreakEvenSeries(strategy, expiredBe, "be-exp"));
                if (tempBe.Count > 0)
                {
                    series.Add(BuildBreakEvenSeries(strategy, tempBe, "be-temp"));
                }
            }
        }

        series.Add(new
        {
            id = "__selected__",
            name = "Selected",
            type = "line",
            data = Array.Empty<double[]>(),
            silent = true,
            lineStyle = new { opacity = 0 },
            tooltip = new { show = false },
            markLine = new
            {
                symbol = "none",
                label = new { show = false },
                lineStyle = new { color = IsDarkTheme ? "#e2e8f0" : "#111827", width = 1 }
            }
        });

        return new
        {
            animation = false,
            backgroundColor = IsDarkTheme ? "#0f172a" : "#ffffff",
            grid = new { containLabel = true, left = 0, right = ShowCandles ? 60 : 18, top = 20, bottom = 40 },
            tooltip = new
            {
                trigger = "axis",
                axisPointer = new { type = "cross" },
                show = true,
                showContent = true,
                alwaysShowContent = false,
                triggerOn = "mousemove|click",
                backgroundColor = tooltipBg,
                textStyle = new { color = tooltipText },
                borderWidth = 0
            },
            legend = ShowLegends ? new
            {
                type = "scroll",
                show = true,
                orient = "horizontal",
                bottom = 2,
                left = 10,
                right = 10,
                itemWidth = 12,
                itemHeight = 8,
                textStyle = new { fontSize = 11, color = axisText },
                data = Strategies.Where(s => s.Visible).Select(s => s.Name).Distinct().ToArray(),
                selectedMode = true,
                hoverLink = false
            } : null,
            xAxis = new
            {
                type = "value",
                nameLocation = "middle",
                nameGap = 30,
                axisLabel = new { formatter = "{value}", fontSize = 10, color = axisText },
                nameTextStyle = new { fontSize = 11, color = axisText },
                axisLine = new { lineStyle = new { color = axisLine } },
                axisTick = new { lineStyle = new { color = axisLine } },
                splitLine = new { lineStyle = new { color = splitLine } },
                min = xRange.min,
                max = xRange.max
            },
            yAxis = ShowCandles
                ? new object[]
                {
                    new
                    {
                        type = "value",
                        nameLocation = "middle",
                        nameGap = 45,
                        axisLabel = new { formatter = "{value}", fontSize = 10, color = axisText },
                        nameTextStyle = new { fontSize = 11, color = axisText },
                        axisLine = new { lineStyle = new { color = axisLine } },
                        axisTick = new { lineStyle = new { color = axisLine } },
                        splitLine = new { lineStyle = new { color = splitLine } },
                        min = yMin,
                        max = yMax
                    },
                    new
                    {
                        type = "time",
                        position = "right",
                        inverse = true,
                        boundaryGap = false,
                        axisLabel = new { show = true, fontSize = 9, color = axisText },
                        axisLine = new { show = true, lineStyle = new { color = axisLine } },
                        axisTick = new { show = true, lineStyle = new { color = axisLine } },
                        splitLine = new { show = false },
                        min = timeMin,
                        max = timeMax
                    }
                }
                : new object[]
                {
                    new
                    {
                        type = "value",
                        nameLocation = "middle",
                        nameGap = 45,
                        axisLabel = new { formatter = "{value}", fontSize = 10, color = axisText },
                        nameTextStyle = new { fontSize = 11, color = axisText },
                        axisLine = new { lineStyle = new { color = axisLine } },
                        axisTick = new { lineStyle = new { color = axisLine } },
                        splitLine = new { lineStyle = new { color = splitLine } },
                        min = yMin,
                        max = yMax
                    }
                },
            dataZoom = Array.Empty<object>(),
            series
        };
    }

    private static object BuildSeries(StrategySeries strategy, bool isTemp)
    {
        var points = isTemp ? strategy.TempPnl : strategy.ExpiredPnl;
        var suffix = isTemp ? "Temp" : "Expired";

        return new
        {
            id = $"{strategy.Id}-{suffix.ToLowerInvariant()}",
            name = strategy.Name,
            payoffKind = suffix,
            strategyName = strategy.Name,
            type = "line",
            smooth = false,
            showSymbol = false,
            emphasis = new { focus = "none" },
            itemStyle = new { color = strategy.Color },
            lineStyle = new
            {
                color = strategy.Color,
                width = 2,
                type = isTemp ? "dashed" : "solid"
            },
            data = points.Select(p => new[] { p.Price, p.Pnl }).ToArray()
        };
    }

    private static object BuildBreakEvenSeries(StrategySeries strategy, IReadOnlyList<double> points, string suffix)
    {
        return new
        {
            id = $"{strategy.Id}-{suffix}",
            name = strategy.Name,
            type = "scatter",
            silent = true,
            showSymbol = true,
            symbol = "circle",
            symbolSize = 6,
            itemStyle = new { color = strategy.Color },
            label = new
            {
                show = true,
                formatter = "{c0}",
                position = "top",
                fontSize = 9,
                color = strategy.Color
            },
            tooltip = new { show = false },
            data = points.Select(p => new[] { p, 0d }).ToArray()
        };
    }

    private List<object> BuildTickerSeries()
    {
        var result = new List<object>();
        var candleData = (Candles == null || Candles.Count == 0)
            ? Array.Empty<object[]>()
            : Candles.Select(c => new object[] { c.Time, c.Open, c.Close, c.Low, c.High }).ToArray();

        result.Add(new
        {
            id = "__ticker_candles__",
            name = "Ticker Candles",
            type = "custom",
            coordinateSystem = "cartesian2d",
            renderKind = "tickerCandles",
            xAxisIndex = 0,
            yAxisIndex = 1,
            data = candleData,
            silent = true,
            tooltip = new { show = false },
            z = 0,
            zlevel = 0,
            skipTooltip = true
        });

        result.Add(new
        {
            id = "__ticker_line__",
            name = "Ticker Line",
            type = "line",
            xAxisIndex = 0,
            yAxisIndex = 1,
            data = candleData.Select(c => new object[] { c[2], c[0] }).ToArray(),
            silent = true,
            tooltip = new { show = false },
            showSymbol = false,
            lineStyle = new { color = "rgba(107,114,128,0.35)", width = 1 },
            z = 0,
            zlevel = 0,
            skipTooltip = true
        });

        return result;
    }

    private static List<double> GetBreakEvens(IReadOnlyList<PayoffPoint> points)
    {
        var result = new List<double>();
        if (points.Count < 2)
        {
            return result;
        }

        var epsilon = GetStep(points) * 0.5;
        for (var i = 1; i < points.Count; i++)
        {
            var prev = points[i - 1];
            var curr = points[i];

            if (Math.Abs(prev.Pnl) < 1e-9)
            {
                AddDistinct(result, prev.Price, epsilon);
            }

            if (Math.Abs(curr.Pnl) < 1e-9)
            {
                AddDistinct(result, curr.Price, epsilon);
                continue;
            }

            if ((prev.Pnl > 0 && curr.Pnl < 0) || (prev.Pnl < 0 && curr.Pnl > 0))
            {
                var t = prev.Pnl / (prev.Pnl - curr.Pnl);
                var price = prev.Price + t * (curr.Price - prev.Price);
                AddDistinct(result, price, epsilon);
            }
        }

        return result;
    }

    private static double GetStep(IReadOnlyList<PayoffPoint> points)
    {
        if (points.Count < 2)
        {
            return 1;
        }

        return Math.Abs(points[^1].Price - points[0].Price) / (points.Count - 1);
    }

    private static List<double> FilterOverlappingBreakEvens(IReadOnlyList<double> temp, IReadOnlyList<double> expired, double step)
    {
        var epsilon = step * 0.5;
        var result = new List<double>();
        foreach (var price in temp)
        {
            var overlaps = expired.Any(exp => Math.Abs(exp - price) <= epsilon);
            if (!overlaps)
            {
                AddDistinct(result, price, epsilon);
            }
        }

        return result;
    }

    private static void AddDistinct(List<double> list, double value, double epsilon)
    {
        foreach (var existing in list)
        {
            if (Math.Abs(existing - value) <= epsilon)
            {
                return;
            }
        }

        list.Add(value);
    }


    private (double? min, double? max) GetXRange()
    {
        if (Strategies.Count == 0)
        {
            return (null, null);
        }

        double? min = null;
        double? max = null;

        foreach (var strategy in Strategies)
        {
            UpdateRange(strategy.TempPnl, ref min, ref max);
            UpdateRange(strategy.ExpiredPnl, ref min, ref max);
        }

        return (min, max);
    }

    private (double? min, double? max) GetTimeRange()
    {
        if (Candles == null || Candles.Count == 0)
        {
            return (null, null);
        }

        var min = Candles.Min(c => c.Time);
        var max = Candles.Max(c => c.Time);

        return (min, max);
    }

    private static void UpdateRange(IReadOnlyList<PayoffPoint> points, ref double? min, ref double? max)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var value = points[i].Price;
            min = !min.HasValue || value < min ? value : min;
            max = !max.HasValue || value > max ? value : max;
        }
    }

    
    private async Task DebounceJsAsync(string key, int delayMs, Func<Task> action)
    {
        if (_debounceTokens.TryGetValue(key, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }

        var cts = new CancellationTokenSource();
        _debounceTokens[key] = cts;
        var token = cts.Token;
        try
        {
            await Task.Delay(delayMs, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        await action();
    }

    private void CancelAllDebounces()
    {
        foreach (var entry in _debounceTokens.Values)
        {
            entry.Cancel();
            entry.Dispose();
        }

        _debounceTokens.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_module != null && _instanceId != null)
        {
            await _module.InvokeVoidAsync("dispose", _instanceId);
        }

        CancelAllDebounces();
        UnsubscribeStrategies(_lastStrategies);
        UnsubscribeCollection(_lastStrategies);
        UnsubscribeMarkers(_lastMarkers);
        UnsubscribeCandles(_lastCandles);
        _dotNetRef?.Dispose();
        if (_module != null)
        {
            await _module.DisposeAsync();
        }
    }

    private void SubscribeStrategies(IReadOnlyList<StrategySeries>? strategies)
    {
        if (strategies == null)
        {
            return;
        }

        UnsubscribeStrategies(_subscribedStrategies);
        foreach (var strategy in strategies)
        {
            strategy.PropertyChanged += OnStrategyPropertyChanged;
        }
        _subscribedStrategies = strategies;
    }

    private void UnsubscribeStrategies(IReadOnlyList<StrategySeries>? strategies)
    {
        if (strategies == null)
        {
            return;
        }

        foreach (var strategy in strategies)
        {
            strategy.PropertyChanged -= OnStrategyPropertyChanged;
        }
        if (ReferenceEquals(_subscribedStrategies, strategies))
        {
            _subscribedStrategies = null;
        }
    }

    private async void OnStrategyPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StrategySeries.Visible)
            && e.PropertyName != nameof(StrategySeries.ShowBreakEvens)
            && e.PropertyName != nameof(StrategySeries.TempPnl)
            && e.PropertyName != nameof(StrategySeries.ExpiredPnl)
            && e.PropertyName != nameof(StrategySeries.Color)
            && e.PropertyName != nameof(StrategySeries.Name))
        {
            return;
        }

        if (_module == null || _instanceId == null)
        {
            return;
        }

        await DebounceJsAsync("strategies", 500, async () =>
        {
            if (_module == null || _instanceId == null)
            {
                return;
            }

            await _module.InvokeVoidAsync("setOption", _instanceId, BuildOption());
        });
    }

    private async Task ScheduleOptionUpdate()
    {
        if (_module == null || _instanceId == null)
        {
            return;
        }

        await DebounceJsAsync("option", 150, () => _module.InvokeVoidAsync("setOption", _instanceId, BuildOption()).AsTask());
    }

    private async Task ScheduleMarkersUpdate()
    {
        if (_module == null || _instanceId == null)
        {
            return;
        }

        await DebounceJsAsync("markers", 200, () => _module.InvokeVoidAsync("setMarkers", _instanceId, Markers).AsTask());
    }

    private async Task ScheduleSelectedUpdate()
    {
        if (_module == null || _instanceId == null)
        {
            return;
        }

        await DebounceJsAsync("selected", 200, () => _module.InvokeVoidAsync("setSelectedPrice", _instanceId, SelectedPrice).AsTask());
    }

    private async Task ScheduleCandlesUpdate()
    {
        if (_module == null || _instanceId == null)
        {
            return;
        }

        await DebounceJsAsync("candles", 200, () => _module.InvokeVoidAsync("updateCandles", _instanceId, Candles).AsTask());
    }

    private void SubscribeCollection(ObservableCollection<StrategySeries>? strategies)
    {
        if (strategies == null)
        {
            return;
        }

        strategies.CollectionChanged -= OnStrategiesCollectionChanged;
        strategies.CollectionChanged += OnStrategiesCollectionChanged;
    }

    private void UnsubscribeCollection(ObservableCollection<StrategySeries>? strategies)
    {
        if (strategies == null)
        {
            return;
        }

        strategies.CollectionChanged -= OnStrategiesCollectionChanged;
    }

    private async void OnStrategiesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_module == null || _instanceId == null)
        {
            return;
        }

        SubscribeStrategies(Strategies);
        await ScheduleOptionUpdate();
    }

    private void SubscribeMarkers(ObservableCollection<PriceMarker>? markers)
    {
        if (markers == null)
        {
            return;
        }

        markers.CollectionChanged -= OnMarkersCollectionChanged;
        markers.CollectionChanged += OnMarkersCollectionChanged;
    }

    private void UnsubscribeMarkers(ObservableCollection<PriceMarker>? markers)
    {
        if (markers == null)
        {
            return;
        }

        markers.CollectionChanged -= OnMarkersCollectionChanged;
    }

    private async void OnMarkersCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_module == null || _instanceId == null)
        {
            return;
        }

        await ScheduleMarkersUpdate();
    }

    private void SubscribeCandles(ObservableCollection<CandlePoint>? candles)
    {
        if (candles == null)
        {
            return;
        }

        candles.CollectionChanged -= OnCandlesCollectionChanged;
        candles.CollectionChanged += OnCandlesCollectionChanged;
    }

    private void UnsubscribeCandles(ObservableCollection<CandlePoint>? candles)
    {
        if (candles == null)
        {
            return;
        }

        candles.CollectionChanged -= OnCandlesCollectionChanged;
    }

    private async void OnCandlesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_module == null || _instanceId == null)
        {
            return;
        }

        if (!ShowCandles)
        {
            return;
        }

        await ScheduleCandlesUpdate();
    }
}
