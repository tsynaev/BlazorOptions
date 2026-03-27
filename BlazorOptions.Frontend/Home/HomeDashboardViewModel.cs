using BlazorOptions.API.Common;
using BlazorOptions.API.Positions;
using BlazorOptions.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlazorOptions.ViewModels;

public sealed class HomeDashboardViewModel : Bindable
{
    private const string DashboardCacheStorageKey = "blazor-options-dashboard-cache";
    private static readonly string[] PositionExpirationFormats = { "ddMMMyy", "ddMMMyyyy" };
    private static readonly JsonSerializerOptions DashboardCacheJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IPositionsPort _positionsPort;
    private readonly OptionsService _optionsService;
    private readonly IExchangeService _exchangeService;
    private readonly ILocalStorageService _localStorageService;
    private readonly INavigationService _navigationService;
    private readonly DvolIndexService _dvolIndexService;
    private bool _isLoading;
    private string? _errorMessage;
    private IReadOnlyList<HomePositionCardModel> _positions = Array.Empty<HomePositionCardModel>();
    private IReadOnlyList<HomePositionGroupModel> _groups = Array.Empty<HomePositionGroupModel>();
    private AccountRiskSettings _riskSettings = AccountRiskSettingsStorage.Default;
    private CancellationTokenSource? _chartLoadCts;

    public HomeDashboardViewModel(
        IPositionsPort positionsPort,
        OptionsService optionsService,
        IExchangeService exchangeService,
        ILocalStorageService localStorageService,
        INavigationService navigationService,
        DvolIndexService dvolIndexService)
    {
        _positionsPort = positionsPort;
        _optionsService = optionsService;
        _exchangeService = exchangeService;
        _localStorageService = localStorageService;
        _navigationService = navigationService;
        _dvolIndexService = dvolIndexService;
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

    public IReadOnlyList<HomePositionCardModel> Positions
    {
        get => _positions;
        private set => SetField(ref _positions, value);
    }

    public IReadOnlyList<HomePositionGroupModel> Groups
    {
        get => _groups;
        private set => SetField(ref _groups, value);
    }

    public async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        _chartLoadCts?.Cancel();
        _chartLoadCts?.Dispose();
        _chartLoadCts = new CancellationTokenSource();

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            _riskSettings = await LoadRiskSettingsAsync();
            var cachedModels = await LoadCachedPositionsAsync();
            var hasCachedModels = cachedModels.Count > 0;
            if (cachedModels.Count > 0)
            {
                SetCards(BuildCards(cachedModels, null, withChart: true, includeTempLine: false));
            }

            var serverModels = (await _positionsPort.LoadPositionsAsync()).ToArray();
            await EnsureOptionChainsAsync(serverModels);
            var pricesBySymbol = await LoadCurrentPricesAsync(serverModels);
            SetCards(BuildCards(serverModels, pricesBySymbol, withChart: hasCachedModels, includeTempLine: true));

            IReadOnlyList<PositionModel> modelsForCharts = serverModels;
            try
            {
                var exchangeSnapshot = await LoadExchangeSnapshotAsync();
                var actualModels = ClonePositions(serverModels);
                ApplyExchangeSnapshot(actualModels, exchangeSnapshot.Positions, exchangeSnapshot.Orders);
                SetCards(BuildCards(actualModels, pricesBySymbol, withChart: hasCachedModels, includeTempLine: true));
                await SaveCachedPositionsAsync(actualModels);
                modelsForCharts = actualModels;
            }
            catch
            {
                await SaveCachedPositionsAsync(serverModels);
            }

            _ = WarmUpChartsAsync(modelsForCharts, pricesBySymbol, _chartLoadCts.Token);
            _ = WarmUpDvolAsync(_chartLoadCts.Token);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            if (Positions.Count == 0)
            {
                Positions = Array.Empty<HomePositionCardModel>();
                Groups = Array.Empty<HomePositionGroupModel>();
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task WarmUpChartsAsync(
        IReadOnlyList<PositionModel> models,
        IReadOnlyDictionary<string, decimal?> pricesBySymbol,
        CancellationToken cancellationToken)
    {
        foreach (var model in models)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var symbol = $"{model.BaseAsset}{model.QuoteAsset}".ToUpperInvariant();
            pricesBySymbol.TryGetValue(symbol, out var currentPrice);
            var card = BuildCard(model, currentPrice, withChart: true, includeTempLine: true);
            ReplaceCard(card);
            await Task.Yield();
        }
    }

    public async Task<bool> RemovePositionAsync(Guid positionId)
    {
        if (positionId == Guid.Empty)
        {
            return false;
        }

        await _positionsPort.DeletePositionAsync(positionId);
        await LoadAsync();
        return true;
    }

    public async Task<Guid?> CreatePositionAsync()
    {
        var initialName = $"Position {Positions.Count + 1}";
        var dialogViewModel = await _navigationService.NavigateToAsync<PositionCreateDialogViewModel>(viewModel =>
            viewModel.InitializeAsync(initialName, "ETH", "USDT"));

        var position = await dialogViewModel.WaitForResultAsync();
        if (position is null)
        {
            return null;
        }

        await _positionsPort.SavePositionAsync(position);
        await LoadAsync();
        return position.Id;
    }

    private HomePositionCardModel[] BuildCards(
        IReadOnlyList<PositionModel> models,
        IReadOnlyDictionary<string, decimal?>? pricesBySymbol,
        bool withChart,
        bool includeTempLine)
    {
        return models
            .Select(model =>
            {
                decimal? currentPrice = null;
                if (pricesBySymbol is not null)
                {
                    var symbol = $"{model.BaseAsset}{model.QuoteAsset}".ToUpperInvariant();
                    pricesBySymbol.TryGetValue(symbol, out currentPrice);
                }

                return BuildCard(model, currentPrice, withChart, includeTempLine);
            })
            .ToArray();
    }

    private HomePositionCardModel BuildCard(PositionModel model, decimal? exchangePrice, bool withChart, bool includeTempLine)
    {
        var sourceLegs = ResolveDashboardLegs(model);
        var legs = PrepareLegsForChart(sourceLegs, model.BaseAsset);

        var entryValue = legs.Sum(ResolveEntryValue);
        var currentPrice = exchangePrice ?? ResolveFallbackCurrentPrice(model.ChartRange, legs);
        var tempPnl = legs.Sum(leg => ResolveLegPnl(leg, currentPrice, model.BaseAsset));
        var theoreticalAtCurrent = legs.Count > 0
            ? _optionsService.CalculateTotalTheoreticalProfit(legs, currentPrice, DateTime.UtcNow)
            : 0m;
        var realizedPnl = model.Closed.Include ? model.Closed.TotalNet : 0m;
        var tempOffset = tempPnl - theoreticalAtCurrent;
        var chart = withChart
            ? BuildChart(legs, model.ChartRange, realizedPnl, tempOffset, includeTempLine)
            : HomeMiniChartModel.Empty;
        var breakEvens = BuildBreakEvensOnly(legs, model.ChartRange, realizedPnl);
        var greekSummary = BuildGreekSummary(legs, model.BaseAsset);
        var totalPnl = tempPnl + realizedPnl;
        var pnlPercent = entryValue > 0m ? (totalPnl / entryValue) * 100m : (decimal?)null;
        var currentPricePercent = withChart
            && exchangePrice.HasValue
            ? ResolveCurrentPricePercent(currentPrice, chart.XMin, chart.XMax)
            : null;

        var assetPair = $"{model.BaseAsset}/{model.QuoteAsset}";
        return new HomePositionCardModel(
            model.Id,
            assetPair,
            model.Name,
            totalPnl,
            pnlPercent,
            entryValue,
            legs.Select(leg => BuildLegChip(leg, currentPrice, model.BaseAsset))
                .ToArray(),
            model.Notes,
            chart,
            breakEvens,
            greekSummary,
            exchangePrice,
            currentPricePercent,
            withChart);
    }

    private static List<LegModel> ResolveDashboardLegs(PositionModel model)
    {
        var allLegs = model.Legs.ToList();
        var hasActiveLegs = allLegs.Any(leg => leg.Status == LegStatus.Active);

        return allLegs
            .Where(leg => leg.IsIncluded)
            .Where(leg => !hasActiveLegs || (leg.Status != LegStatus.New && leg.Status != LegStatus.Order))
            .ToList();
    }

    private IReadOnlyList<decimal> BuildBreakEvensOnly(
        IReadOnlyList<LegModel> legs,
        BlazorChart.Models.ChartRange? range,
        decimal pnlShift)
    {
        if (!ShouldShowDashboardBreakEvens(legs, DateTime.UtcNow))
        {
            return Array.Empty<decimal>();
        }

        var (xs, profits, _) = _optionsService.GeneratePosition(
            legs,
            // Use denser sampling because BE can be missed on coarse grids.
            points: 120,
            xMinOverride: range?.XMin,
            xMaxOverride: range?.XMax);

        var breakEvens = ResolveBreakEvens(xs, profits, pnlShift);
        if (breakEvens.Count > 0)
        {
            return breakEvens;
        }

        // If the current chart range does not cross zero P/L, search full auto-range.
        var (fullXs, fullProfits, _) = _optionsService.GeneratePosition(
            legs,
            points: 240);

        return ResolveBreakEvens(fullXs, fullProfits, pnlShift);
    }

    private HomeGreekSummary BuildGreekSummary(IReadOnlyList<LegModel> legs, string? baseAsset)
    {
        decimal totalDelta = 0m;
        decimal totalGamma = 0m;
        decimal totalVega = 0m;
        decimal totalTheta = 0m;

        foreach (var leg in legs)
        {
            if (leg.Type == LegType.Future)
            {
                totalDelta += leg.Size;
                continue;
            }

            var ticker = _exchangeService.OptionsChain.FindTickerForLeg(leg, baseAsset);
            if (ticker is null)
            {
                continue;
            }

            if (ticker.Delta.HasValue)
            {
                totalDelta += ticker.Delta.Value * leg.Size;
            }

            if (ticker.Gamma.HasValue)
            {
                totalGamma += ticker.Gamma.Value * leg.Size;
            }

            if (ticker.Vega.HasValue)
            {
                totalVega += ticker.Vega.Value * leg.Size;
            }

            if (ticker.Theta.HasValue)
            {
                totalTheta += ticker.Theta.Value * leg.Size;
            }
        }

        return new HomeGreekSummary(totalDelta, totalGamma, totalVega, totalTheta);
    }

    private HomeMiniChartModel BuildChart(
        IReadOnlyList<LegModel> legs,
        BlazorChart.Models.ChartRange? range,
        decimal pnlShift,
        decimal tempOffset,
        bool includeTempLine)
    {
        var (xs, profits, theoreticalProfits) = _optionsService.GeneratePosition(
            legs,
            points: 48,
            xMinOverride: range?.XMin,
            xMaxOverride: range?.XMax);

        var labels = xs
            .Select(value => Math.Round(value, 0).ToString("0"))
            .ToArray();
        IReadOnlyList<string> sparseLabels = BuildSparseLabels(labels, 8);
        var values = profits
            .Select(value => (double)(value + pnlShift))
            .ToArray();
        var tempValues = includeTempLine
            ? theoreticalProfits
                .Select(value => (double)(value + pnlShift + tempOffset))
                .ToArray()
            : Array.Empty<double>();
        var breakEvens = ShouldShowDashboardBreakEvens(legs, DateTime.UtcNow)
            ? ResolveBreakEvens(xs, profits, pnlShift)
            : Array.Empty<decimal>();

        return new HomeMiniChartModel(sparseLabels, values, tempValues, (double)xs[0], (double)xs[^1], breakEvens, includeTempLine);
    }

    private static bool ShouldShowDashboardBreakEvens(IReadOnlyList<LegModel> legs, DateTime nowUtc)
    {
        if (legs.Count == 0)
        {
            return false;
        }

        var hasOpenLeg = false;
        foreach (var leg in legs)
        {
            if (!leg.ExpirationDate.HasValue || leg.ExpirationDate.Value > nowUtc)
            {
                hasOpenLeg = true;
                break;
            }
        }

        return hasOpenLeg;
    }

    private static IReadOnlyList<decimal> ResolveBreakEvens(IReadOnlyList<decimal> xs, IReadOnlyList<decimal> profits, decimal pnlShift)
    {
        var result = new List<decimal>();
        if (xs.Count == 0 || profits.Count == 0)
        {
            return result;
        }

        var count = Math.Min(xs.Count, profits.Count);
        var step = count > 1
            ? Math.Abs(xs[count - 1] - xs[0]) / (count - 1)
            : 1m;
        var epsilonX = Math.Max(0.01m, step * 0.5m);

        var maxAbsY = 0m;
        for (var i = 0; i < count; i++)
        {
            var y = Math.Abs(profits[i] + pnlShift);
            if (y > maxAbsY)
            {
                maxAbsY = y;
            }
        }

        // Relative epsilon keeps BE detection stable for both small and large P/L scales.
        var epsilonY = Math.Max(0.01m, maxAbsY * 0.0005m);

        for (var i = 0; i < count - 1; i++)
        {
            var x0 = xs[i];
            var x1 = xs[i + 1];
            var y0 = profits[i] + pnlShift;
            var y1 = profits[i + 1] + pnlShift;

            if (Math.Abs(y0) <= epsilonY)
            {
                AddDistinctBreakEven(result, x0, epsilonX);
                continue;
            }

            if (Math.Abs(y1) <= epsilonY)
            {
                AddDistinctBreakEven(result, x1, epsilonX);
                continue;
            }

            if ((y0 < 0m && y1 > 0m) || (y0 > 0m && y1 < 0m))
            {
                var slope = y1 - y0;
                if (Math.Abs(slope) < 0.0000001m)
                {
                    continue;
                }

                var t = -y0 / slope;
                var breakeven = x0 + (x1 - x0) * t;
                AddDistinctBreakEven(result, breakeven, epsilonX);
            }
        }

        return result
            .OrderBy(value => value)
            .ToArray();
    }

    private static void AddDistinctBreakEven(List<decimal> list, decimal value, decimal epsilonX)
    {
        var rounded = decimal.Round(value, 2);
        for (var i = 0; i < list.Count; i++)
        {
            if (Math.Abs(list[i] - rounded) <= epsilonX)
            {
                return;
            }
        }

        list.Add(rounded);
    }

    private static decimal ResolveCurrentPrice(HomeMiniChartModel chart, BlazorChart.Models.ChartRange? range)
    {
        if (range is not null && range.XMax > range.XMin)
        {
            return (decimal)((range.XMin + range.XMax) / 2d);
        }

        if (chart.XLabels.Count == 0)
        {
            return 0m;
        }

        var mid = chart.XLabels.Count / 2;
        return decimal.TryParse(chart.XLabels[mid], out var parsed)
            ? parsed
            : 0m;
    }

    private static double? ResolveCurrentPricePercent(decimal currentPrice, double xMin, double xMax)
    {
        if (xMax <= xMin)
        {
            return null;
        }

        var percent = ((double)currentPrice - xMin) / (xMax - xMin) * 100d;
        return Math.Clamp(percent, 0d, 100d);
    }

    private static decimal ResolveEntryValue(LegModel leg)
    {
        if (leg.Type == LegType.Future)
        {
            // Futures entry value is treated as zero for PnL percent scaling.
            return 0m;
        }

        if (!leg.Price.HasValue)
        {
            return 0m;
        }

        return Math.Abs(leg.Size * leg.Price.Value);
    }

    private static string FormatQuickAdd(LegModel leg)
    {
        var sign = leg.Size >= 0 ? "+" : "-";
        var size = Math.Abs(leg.Size).ToString("0.####");

        return leg.Type switch
        {
            LegType.Future => $"{sign}{size} F",
            LegType.Call => $"{sign}{size} C {(leg.Strike ?? 0):0.####}",
            LegType.Put => $"{sign}{size} P {(leg.Strike ?? 0):0.####}",
            _ => $"{sign}{size}"
        };
    }

    private HomeLegChipModel BuildLegChip(LegModel leg, decimal currentPrice, string? baseAsset)
    {
        var entryValue = ResolveEntryValue(leg);
        var markPrice = ResolveLegMarkPrice(leg, currentPrice, baseAsset);
        var entry = leg.Price ?? 0m;
        var pnl = markPrice.HasValue
            ? (markPrice.Value - entry) * leg.Size
            : _optionsService.CalculateLegProfit(leg, currentPrice);
        decimal? pnlPercent = null;
        if (leg.Type == LegType.Future)
        {
            pnlPercent = ResolveFuturesPnlPercent(leg, currentPrice);
        }
        else if (entryValue > 0)
        {
            pnlPercent = pnl / entryValue * 100m;
        }

        var severityClass = ResolveLegSeverityClass(leg.Type, pnlPercent, _riskSettings);
        var currentValue = ResolveCurrentValue(leg, markPrice);
        return new HomeLegChipModel(
            FormatQuickAdd(leg),
            leg.ExpirationDate,
            pnlPercent,
            severityClass,
            leg.Symbol,
            leg.Type,
            leg.Size,
            leg.Price,
            markPrice,
            pnl,
            entryValue,
            currentValue);
    }

    private static decimal ResolveCurrentValue(LegModel leg, decimal? markPrice)
    {
        if (!markPrice.HasValue)
        {
            return 0m;
        }

        return Math.Abs(leg.Size * markPrice.Value);
    }

    private decimal? ResolveLegMarkPrice(LegModel leg, decimal currentPrice, string? baseAsset)
    {
        if (leg.Type == LegType.Future)
        {
            return currentPrice;
        }

        var ticker = _exchangeService.OptionsChain.FindTickerForLeg(leg, baseAsset);
        return ResolveOptionMarkPrice(ticker);
    }

    private List<LegModel> PrepareLegsForChart(IReadOnlyList<LegModel> legs, string? baseAsset)
    {
        var prepared = new List<LegModel>(legs.Count);
        foreach (var leg in legs)
        {
            var clone = leg.Clone();
            if (clone.Type != LegType.Future)
            {
                var ticker = _exchangeService.OptionsChain.FindTickerForLeg(clone, baseAsset);
                if (ticker is not null)
                {
                    var ivPercent = NormalizeIv(ticker.MarkIv)
                        ?? NormalizeIv(ticker.BidIv)
                        ?? NormalizeIv(ticker.AskIv);

                    if ((!clone.ImpliedVolatility.HasValue || clone.ImpliedVolatility.Value <= 0m) && ivPercent.HasValue)
                    {
                        clone.ImpliedVolatility = ivPercent.Value;
                    }

                    if (!clone.Price.HasValue || clone.Price.Value <= 0m)
                    {
                        clone.Price = ResolveOptionMarkPrice(ticker);
                    }
                }
            }

            prepared.Add(clone);
        }

        return prepared;
    }

    private static decimal? NormalizeIv(decimal? value)
    {
        if (!value.HasValue || value.Value <= 0m)
        {
            return null;
        }

        return value.Value <= 3m ? value.Value * 100m : value.Value;
    }

    private decimal ResolveLegPnl(LegModel leg, decimal currentPrice, string? baseAsset)
    {
        var mark = ResolveLegMarkPrice(leg, currentPrice, baseAsset);
        if (mark.HasValue)
        {
            var entry = leg.Price ?? 0m;
            return (mark.Value - entry) * leg.Size;
        }

        return _optionsService.CalculateLegProfit(leg, currentPrice);
    }

    private static decimal? ResolveOptionMarkPrice(OptionChainTicker? ticker)
    {
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

    private static string ResolveLegSeverityClass(LegType type, decimal? pnlPercent, AccountRiskSettings riskSettings)
    {
        if (!pnlPercent.HasValue)
        {
            return "home-leg-neutral";
        }

        if (pnlPercent.Value >= 0)
        {
            return "home-leg-profit";
        }

        var loss = Math.Abs(pnlPercent.Value);
        var maxLoss = type == LegType.Future
            ? Math.Max(1m, riskSettings.MaxLossFuturesPercent)
            : Math.Max(1m, riskSettings.MaxLossOptionPercent);

        if (loss >= maxLoss)
        {
            return "home-leg-critical";
        }

        if (loss >= maxLoss * 0.66m)
        {
            return "home-leg-high";
        }

        return "home-leg-low";
    }

    private static decimal? ResolveFuturesPnlPercent(LegModel leg, decimal currentPrice)
    {
        if (!leg.Price.HasValue || leg.Price.Value == 0m)
        {
            return null;
        }

        var entry = leg.Price.Value;
        var movePercent = (currentPrice - entry) / entry * 100m;
        var side = Math.Sign(leg.Size);
        if (side == 0)
        {
            return null;
        }

        return movePercent * side;
    }

    private static decimal ResolveFallbackCurrentPrice(BlazorChart.Models.ChartRange? range, IReadOnlyList<LegModel> legs)
    {
        if (range is not null && range.XMax > range.XMin)
        {
            return (decimal)((range.XMin + range.XMax) / 2d);
        }

        var anchor = legs.Count > 0
            ? legs.Average(l => l.Strike.HasValue && l.Strike.Value > 0 ? l.Strike.Value : (l.Price ?? 0m))
            : 0m;
        return anchor > 0m ? anchor : 0m;
    }

    private static IReadOnlyList<string> BuildSparseLabels(IReadOnlyList<string> labels, int maxVisible)
    {
        if (labels.Count <= maxVisible || maxVisible <= 0)
        {
            return labels;
        }

        var step = Math.Max(1, (int)Math.Ceiling(labels.Count / (double)maxVisible));
        var sparse = new string[labels.Count];
        for (var i = 0; i < labels.Count; i++)
        {
            if (i == 0 || i == labels.Count - 1 || i % step == 0)
            {
                sparse[i] = labels[i];
            }
            else
            {
                sparse[i] = string.Empty;
            }
        }

        return sparse;
    }

    private async Task<Dictionary<string, decimal?>> LoadCurrentPricesAsync(IReadOnlyList<PositionModel> models)
    {
        var symbols = models
            .Select(model => $"{model.BaseAsset}{model.QuoteAsset}".ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in symbols)
        {
            result[symbol] = await TryLoadCurrentPriceAsync(symbol);
        }

        return result;
    }

    private async Task EnsureOptionChainsAsync(IReadOnlyList<PositionModel> models)
    {
        var baseAssets = models
            .Where(model => model.Legs.Any(leg => leg.Type != LegType.Future))
            .Select(model => model.BaseAsset?.Trim())
            .Where(asset => !string.IsNullOrWhiteSpace(asset))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var baseAsset in baseAssets)
        {
            try
            {
                await _exchangeService.OptionsChain.EnsureTickersForBaseAssetAsync(baseAsset!);
            }
            catch
            {
                // Home dashboard should stay resilient when option-chain refresh fails.
            }
        }
    }

    private async Task<AccountRiskSettings> LoadRiskSettingsAsync()
    {
        try
        {
            var payload = await _localStorageService.GetItemAsync(AccountRiskSettingsStorage.StorageKey);
            return AccountRiskSettingsStorage.Parse(payload);
        }
        catch
        {
            return AccountRiskSettingsStorage.Default;
        }
    }

    private async Task<IReadOnlyList<PositionModel>> LoadCachedPositionsAsync()
    {
        try
        {
            var payload = await _localStorageService.GetItemAsync(DashboardCacheStorageKey);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return Array.Empty<PositionModel>();
            }

            return PositionPayloadSerializer.DeserializeMany(payload, DashboardCacheJsonOptions);
        }
        catch
        {
            return Array.Empty<PositionModel>();
        }
    }

    private async Task SaveCachedPositionsAsync(IReadOnlyList<PositionModel> models)
    {
        try
        {
            var payload = PositionPayloadSerializer.SerializeMany(models, DashboardCacheJsonOptions);
            await _localStorageService.SetItemAsync(DashboardCacheStorageKey, payload);
        }
        catch
        {
            // Keep dashboard resilient when cache persistence fails.
        }
    }

    private async Task<(IReadOnlyList<ExchangePosition> Positions, IReadOnlyList<ExchangeOrder> Orders)> LoadExchangeSnapshotAsync()
    {
        var positionsTask = _exchangeService.Positions.GetPositionsAsync();
        var ordersTask = _exchangeService.Orders.GetOpenOrdersAsync();
        await Task.WhenAll(positionsTask, ordersTask);

        return (
            positionsTask.Result?.ToArray() ?? Array.Empty<ExchangePosition>(),
            ordersTask.Result?.ToArray() ?? Array.Empty<ExchangeOrder>());
    }

    private void ApplyExchangeSnapshot(
        IReadOnlyList<PositionModel> models,
        IReadOnlyList<ExchangePosition> positions,
        IReadOnlyList<ExchangeOrder> orders)
    {
        foreach (var model in models)
        {
            ApplyExchangeSnapshot(model, positions, orders);
        }
    }

    private void ApplyExchangeSnapshot(
        PositionModel model,
        IReadOnlyList<ExchangePosition> positions,
        IReadOnlyList<ExchangeOrder> orders)
    {
        var baseAsset = model.BaseAsset?.Trim();
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return;
        }

        var activeLegs = positions
            .Select(position => CreateLegFromExchangePosition(position, baseAsset, position.Category))
            .Where(leg => leg is not null)
            .Cast<LegModel>()
            .ToArray();

        var orderLegs = orders
            .Select(order => CreateLegFromExchangeOrder(order, baseAsset))
            .Where(leg => leg is not null && !string.IsNullOrWhiteSpace(leg.ReferenceId))
            .Cast<LegModel>()
            .ToArray();

        SyncDashboardOrderLegs(model.Legs, orderLegs, activeLegs);
        SyncDashboardReadOnlyLegs(model.Legs, activeLegs);
    }

    private static void SyncDashboardOrderLegs(
        ObservableCollection<LegModel> legs,
        IReadOnlyList<LegModel> orderLegs,
        IReadOnlyList<LegModel> activeLegs)
    {
        var openOrderLegs = orderLegs
            .Where(leg => !string.IsNullOrWhiteSpace(leg.ReferenceId))
            .ToDictionary(leg => leg.ReferenceId!, StringComparer.OrdinalIgnoreCase);
        var existingOrderLegs = legs
            .Where(leg => leg.Status == LegStatus.Order)
            .ToList();

        foreach (var existing in existingOrderLegs)
        {
            var referenceId = existing.ReferenceId
                ?? ExtractReferenceIdFromCandidateId(existing.Id)
                ?? BuildFallbackOrderReference(existing.Symbol, existing.Size, existing.Price);
            existing.ReferenceId = referenceId;

            if (!openOrderLegs.TryGetValue(referenceId, out var snapshotLeg))
            {
                if (TryConvertDashboardExecutedOrderToActive(legs, existing, activeLegs))
                {
                    continue;
                }

                legs.Remove(existing);
                continue;
            }

            ApplyDashboardOrderLegSnapshot(existing, snapshotLeg);
        }
    }

    private static void SyncDashboardReadOnlyLegs(
        ObservableCollection<LegModel> legs,
        IReadOnlyList<LegModel> activeLegs)
    {
        var lookup = activeLegs
            .Where(leg => !string.IsNullOrWhiteSpace(leg.Symbol))
            .GroupBy(leg => leg.Symbol!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var leg in legs)
        {
            if (leg.Status == LegStatus.Order)
            {
                continue;
            }

            if (!leg.IsReadOnly)
            {
                leg.Status = LegStatus.New;
                continue;
            }

            var symbol = leg.Symbol?.Trim();
            if (string.IsNullOrWhiteSpace(symbol)
                || !lookup.TryGetValue(symbol, out var matches))
            {
                leg.Status = LegStatus.Missing;
                leg.IsIncluded = false;
                continue;
            }

            var match = matches.FirstOrDefault(item =>
                Math.Sign(item.Size) == Math.Sign(leg.Size)
                && item.Type == leg.Type
                && item.ExpirationDate?.Date == leg.ExpirationDate?.Date
                && item.Strike == leg.Strike);

            if (match is null)
            {
                leg.Status = LegStatus.Missing;
                leg.IsIncluded = false;
                continue;
            }

            ApplyDashboardActiveLegSnapshot(leg, match, forceInclude: false);
        }
    }

    private static void ApplyDashboardOrderLegSnapshot(LegModel target, LegModel snapshot)
    {
        target.Type = snapshot.Type;
        target.Strike = snapshot.Strike;
        target.ExpirationDate = snapshot.ExpirationDate;
        target.Size = snapshot.Size;
        target.Price = snapshot.Price;
        target.Symbol = snapshot.Symbol;
        target.IsReadOnly = true;
        target.Status = LegStatus.Order;
        target.ReferenceId = snapshot.ReferenceId;
    }

    private static bool TryConvertDashboardExecutedOrderToActive(
        ObservableCollection<LegModel> legs,
        LegModel orderLeg,
        IReadOnlyList<LegModel> activeLegs)
    {
        var matchedPositionLeg = activeLegs.FirstOrDefault(positionLeg =>
            string.Equals(positionLeg.Symbol, orderLeg.Symbol, StringComparison.OrdinalIgnoreCase)
            && Math.Sign(positionLeg.Size) == Math.Sign(orderLeg.Size)
            && positionLeg.Type == orderLeg.Type
            && positionLeg.ExpirationDate?.Date == orderLeg.ExpirationDate?.Date
            && positionLeg.Strike == orderLeg.Strike);

        if (matchedPositionLeg is null)
        {
            return false;
        }

        if (legs.Any(existing =>
                !ReferenceEquals(existing, orderLeg)
                && existing.IsReadOnly
                && existing.Status != LegStatus.Order
                && string.Equals(existing.Symbol, matchedPositionLeg.Symbol, StringComparison.OrdinalIgnoreCase)
                && Math.Sign(existing.Size) == Math.Sign(matchedPositionLeg.Size)))
        {
            legs.Remove(orderLeg);
            return true;
        }

        ApplyDashboardActiveLegSnapshot(orderLeg, matchedPositionLeg, forceInclude: true);
        if (orderLeg.Id.StartsWith("order:", StringComparison.OrdinalIgnoreCase))
        {
            orderLeg.Id = Guid.NewGuid().ToString("N");
        }

        return true;
    }

    private static void ApplyDashboardActiveLegSnapshot(LegModel target, LegModel snapshot, bool forceInclude)
    {
        target.Status = LegStatus.Active;
        target.IsReadOnly = true;
        if (forceInclude)
        {
            target.IsIncluded = true;
        }

        target.Type = snapshot.Type;
        target.Strike = snapshot.Strike;
        target.ExpirationDate = snapshot.ExpirationDate;
        target.Size = snapshot.Size;
        target.Price = snapshot.Price;
        target.Symbol = snapshot.Symbol;
        target.ReferenceId = snapshot.ReferenceId;
    }

    private LegModel? CreateLegFromExchangePosition(ExchangePosition position, string baseAsset, string category)
    {
        if (string.IsNullOrWhiteSpace(position.Symbol))
        {
            return null;
        }

        DateTime? expiration = null;
        decimal? strike = null;
        var type = LegType.Future;
        if (string.Equals(category, "option", StringComparison.OrdinalIgnoreCase))
        {
            if (!_exchangeService.TryParseSymbol(position.Symbol, out var parsedBase, out var parsedExpiration, out var parsedStrike, out var parsedType))
            {
                return null;
            }

            if (!string.Equals(parsedBase, baseAsset, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            expiration = parsedExpiration;
            strike = parsedStrike;
            type = parsedType;
        }
        else
        {
            if (!position.Symbol.StartsWith(baseAsset, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (TryParseFutureExpiration(position.Symbol, out var parsedFutureExpiration))
            {
                expiration = parsedFutureExpiration;
            }
        }

        var size = DetermineSignedSize(position);
        if (Math.Abs(size) < 0.0001m)
        {
            return null;
        }

        return new LegModel
        {
            ReferenceId = BuildPositionReferenceId(position.Symbol, size),
            IsReadOnly = true,
            Type = type,
            Strike = strike,
            ExpirationDate = expiration,
            Size = size,
            Price = position.AvgPrice,
            ImpliedVolatility = null,
            Symbol = position.Symbol,
            Status = LegStatus.Active
        };
    }

    private LegModel? CreateLegFromExchangeOrder(ExchangeOrder order, string baseAsset)
    {
        if (string.IsNullOrWhiteSpace(order.Symbol))
        {
            return null;
        }

        DateTime? expiration = null;
        decimal? strike = null;
        var type = LegType.Future;
        if (string.Equals(order.Category, "option", StringComparison.OrdinalIgnoreCase))
        {
            if (!_exchangeService.TryParseSymbol(order.Symbol, out var parsedBase, out var parsedExpiration, out var parsedStrike, out var parsedType))
            {
                return null;
            }

            if (!string.Equals(parsedBase, baseAsset, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            expiration = parsedExpiration;
            strike = parsedStrike;
            type = parsedType;
        }
        else
        {
            if (!order.Symbol.StartsWith(baseAsset, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (TryParseFutureExpiration(order.Symbol, out var parsedFutureExpiration))
            {
                expiration = parsedFutureExpiration;
            }
        }

        var size = DetermineSignedSize(order);
        if (Math.Abs(size) < 0.0001m)
        {
            return null;
        }

        return new LegModel
        {
            Id = BuildOrderCandidateId(order),
            ReferenceId = BuildOrderReferenceId(order),
            IsReadOnly = true,
            Type = type,
            Strike = strike,
            ExpirationDate = expiration,
            Size = size,
            Price = order.Price,
            ImpliedVolatility = null,
            Symbol = order.Symbol,
            Status = LegStatus.Order
        };
    }

    private static PositionModel[] ClonePositions(IReadOnlyList<PositionModel> models)
    {
        return models.Select(ClonePosition).ToArray();
    }

    private static PositionModel ClonePosition(PositionModel model)
    {
        return new PositionModel
        {
            Id = model.Id,
            BaseAsset = model.BaseAsset,
            QuoteAsset = model.QuoteAsset,
            Name = model.Name,
            Notes = model.Notes,
            CreationTimeUtc = model.CreationTimeUtc,
            Color = model.Color,
            Legs = new(model.Legs.Select(leg => leg.Clone())),
            ChartRange = model.ChartRange,
            Closed = new ClosedModel
            {
                Include = model.Closed.Include,
                TotalClosePnl = model.Closed.TotalClosePnl,
                TotalFee = model.Closed.TotalFee,
                Positions = new(model.Closed.Positions.Select(CloneClosedPosition))
            }
        };
    }

    private static ClosedPositionModel CloneClosedPosition(ClosedPositionModel model)
    {
        return new ClosedPositionModel
        {
            Id = model.Id,
            Symbol = model.Symbol,
            SinceDate = model.SinceDate,
            FirstTradeTimestamp = model.FirstTradeTimestamp,
            LastProcessedTimestamp = model.LastProcessedTimestamp,
            LastProcessedIdsAtTimestamp = model.LastProcessedIdsAtTimestamp.ToList(),
            PositionSize = model.PositionSize,
            AvgPrice = model.AvgPrice,
            EntryQty = model.EntryQty,
            EntryValue = model.EntryValue,
            CloseQty = model.CloseQty,
            CloseValue = model.CloseValue,
            Realized = model.Realized,
            FeeTotal = model.FeeTotal
        };
    }

    private async Task<decimal?> TryLoadCurrentPriceAsync(string symbol)
    {
        try
        {
            var toUtc = DateTime.UtcNow;
            var fromUtc = toUtc.AddHours(-2);
            var candles = await _exchangeService.Tickers.GetCandlesWithVolumeAsync(symbol, fromUtc, toUtc, 60);
            var last = candles
                .OrderBy(c => c.Time)
                .LastOrDefault();

            return last is null ? null : (decimal?)last.Close;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseFutureExpiration(string symbol, out DateTime expiration)
    {
        expiration = default;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var tokens = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length >= 2
            && DateTime.TryParseExact(tokens[1], PositionExpirationFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            expiration = parsed.Date;
            return true;
        }

        var match = Regex.Match(symbol, @"(\d{8}|\d{6})$");
        if (!match.Success)
        {
            return false;
        }

        var format = match.Groups[1].Value.Length == 8 ? "yyyyMMdd" : "yyMMdd";
        return DateTime.TryParseExact(match.Groups[1].Value, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out expiration);
    }

    private static string BuildPositionReferenceId(string? symbol, decimal size)
    {
        var normalizedSymbol = string.IsNullOrWhiteSpace(symbol) ? string.Empty : symbol.Trim().ToUpperInvariant();
        return $"position:{normalizedSymbol}:{Math.Sign(size)}";
    }

    private static string BuildOrderCandidateId(ExchangeOrder order)
    {
        return $"order:{BuildOrderReferenceId(order)}";
    }

    private static string BuildOrderReferenceId(ExchangeOrder order)
    {
        if (!string.IsNullOrWhiteSpace(order.OrderId))
        {
            return order.OrderId.Trim();
        }

        return BuildFallbackOrderReference(order.Symbol, DetermineSignedSize(order), order.Price);
    }

    private static string BuildFallbackOrderReference(string? symbol, decimal size, decimal? price)
    {
        var normalizedSymbol = string.IsNullOrWhiteSpace(symbol) ? string.Empty : symbol.Trim().ToUpperInvariant();
        var side = Math.Sign(size).ToString(CultureInfo.InvariantCulture);
        var qty = Math.Abs(size).ToString("0.########", CultureInfo.InvariantCulture);
        var normalizedPrice = price.HasValue ? price.Value.ToString("0.########", CultureInfo.InvariantCulture) : "mkt";
        return $"{normalizedSymbol}:{side}:{qty}:{normalizedPrice}";
    }

    private static string? ExtractReferenceIdFromCandidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var value = id.Trim();
        return value.StartsWith("order:", StringComparison.OrdinalIgnoreCase)
            ? value["order:".Length..]
            : value;
    }

    private static decimal DetermineSignedSize(ExchangePosition position)
    {
        var magnitude = Math.Abs(position.Size);
        if (magnitude < 0.0001m)
        {
            return 0m;
        }

        if (!string.IsNullOrWhiteSpace(position.Side))
        {
            var normalized = position.Side.Trim();
            if (string.Equals(normalized, "Sell", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Short", StringComparison.OrdinalIgnoreCase))
            {
                return -magnitude;
            }
        }

        return magnitude;
    }

    private static decimal DetermineSignedSize(ExchangeOrder order)
    {
        var magnitude = Math.Abs(order.Qty);
        if (magnitude < 0.0001m)
        {
            return 0m;
        }

        if (!string.IsNullOrWhiteSpace(order.Side)
            && string.Equals(order.Side.Trim(), "Sell", StringComparison.OrdinalIgnoreCase))
        {
            return -magnitude;
        }

        return magnitude;
    }

    private void SetCards(IReadOnlyList<HomePositionCardModel> cards)
    {
        var existingDvolByGroup = Groups
            .ToDictionary(group => group.AssetPair, group => group.DvolChart, StringComparer.OrdinalIgnoreCase);

        Positions = cards;
        Groups = cards
            .GroupBy(card => card.AssetPair, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var dvol = existingDvolByGroup.TryGetValue(group.Key, out var existing)
                    ? existing
                    : HomeDvolChartModel.Loading(ResolveBaseAsset(group.Key));
                return new HomePositionGroupModel(group.Key, group.ToArray(), dvol);
            })
            .ToArray();
    }

    private void ReplaceCard(HomePositionCardModel updatedCard)
    {
        if (Positions.Count == 0)
        {
            return;
        }

        var cards = Positions.ToArray();
        var index = Array.FindIndex(cards, card => card.Id == updatedCard.Id);
        if (index < 0)
        {
            return;
        }

        cards[index] = updatedCard;
        SetCards(cards);
    }

    private async Task WarmUpDvolAsync(CancellationToken cancellationToken)
    {
        var groups = Groups.ToArray();
        foreach (var group in groups)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var baseAsset = ResolveBaseAsset(group.AssetPair);
            if (string.IsNullOrWhiteSpace(baseAsset))
            {
                ReplaceGroupDvol(group.AssetPair, HomeDvolChartModel.Unavailable(string.Empty));
                continue;
            }

            var cachedChart = await _dvolIndexService.LoadCachedAsync(baseAsset);
            if (cachedChart is not null)
            {
                ReplaceGroupDvol(group.AssetPair, BuildDvolModel(cachedChart));
            }

            var chart = await _dvolIndexService.RefreshChartAsync(baseAsset, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (chart is null || chart.Candles.Count == 0)
            {
                if (cachedChart is null)
                {
                    ReplaceGroupDvol(group.AssetPair, HomeDvolChartModel.Unavailable(baseAsset));
                }
                continue;
            }

            ReplaceGroupDvol(group.AssetPair, BuildDvolModel(chart));
        }
    }

    private static HomeDvolChartModel BuildDvolModel(DvolChartData chart)
    {
        return HomeDvolChartModel.Ready(
            chart.BaseAsset,
            chart.XLabels,
            chart.Candles,
            chart.LatestValue,
            chart.AverageLastYear);
    }

    private void ReplaceGroupDvol(string assetPair, HomeDvolChartModel dvolChart)
    {
        if (Groups.Count == 0)
        {
            return;
        }

        var groups = Groups.ToArray();
        var index = Array.FindIndex(groups, group => string.Equals(group.AssetPair, assetPair, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        groups[index] = groups[index] with { DvolChart = dvolChart };
        Groups = groups;
    }

    private static string ResolveBaseAsset(string assetPair)
    {
        if (string.IsNullOrWhiteSpace(assetPair))
        {
            return string.Empty;
        }

        var separatorIndex = assetPair.IndexOf('/');
        if (separatorIndex <= 0)
        {
            return assetPair.Trim().ToUpperInvariant();
        }

        return assetPair[..separatorIndex].Trim().ToUpperInvariant();
    }
}

public sealed record HomePositionCardModel(
    Guid Id,
    string AssetPair,
    string Name,
    decimal TotalPnl,
    decimal? PnlPercent,
    decimal EntryValue,
    IReadOnlyList<HomeLegChipModel> QuickAddLegs,
    string Notes,
    HomeMiniChartModel Chart,
    IReadOnlyList<decimal> BreakEvens,
    HomeGreekSummary GreekSummary,
    decimal? CurrentPrice,
    double? CurrentPricePercent,
    bool IsChartReady);

public sealed record HomeGreekSummary(
    decimal TotalDelta,
    decimal TotalGamma,
    decimal TotalVega,
    decimal TotalTheta);

public sealed record HomeLegChipModel(
    string Text,
    DateTime? ExpirationDate,
    decimal? PnlPercent,
    string SeverityClass,
    string? Symbol,
    LegType Type,
    decimal Size,
    decimal? EntryPrice,
    decimal? MarkPrice,
    decimal Pnl,
    decimal EntryValue,
    decimal CurrentValue);

public sealed record HomePositionGroupModel(
    string AssetPair,
    IReadOnlyList<HomePositionCardModel> Positions,
    HomeDvolChartModel DvolChart);

public sealed record HomeDvolChartModel(
    string BaseAsset,
    IReadOnlyList<string> XLabels,
    IReadOnlyList<DvolCandlePoint> Candles,
    double? LatestValue,
    double? AverageLastYear,
    bool IsLoading,
    bool IsAvailable)
{
    public static HomeDvolChartModel Loading(string baseAsset) => new(
        baseAsset,
        Array.Empty<string>(),
        Array.Empty<DvolCandlePoint>(),
        null,
        null,
        true,
        false);

    public static HomeDvolChartModel Unavailable(string baseAsset) => new(
        baseAsset,
        Array.Empty<string>(),
        Array.Empty<DvolCandlePoint>(),
        null,
        null,
        false,
        false);

    public static HomeDvolChartModel Ready(
        string baseAsset,
        IReadOnlyList<string> xLabels,
        IReadOnlyList<DvolCandlePoint> candles,
        double latestValue,
        double averageLastYear) => new(
        baseAsset,
        xLabels,
        candles,
        latestValue,
        averageLastYear,
        false,
        true);
}

public sealed record HomeMiniChartModel(
    IReadOnlyList<string> XLabels,
    IReadOnlyList<double> Values,
    IReadOnlyList<double> TempValues,
    double XMin,
    double XMax,
    IReadOnlyList<decimal> BreakEvens,
    bool HasTempLine)
{
    public static HomeMiniChartModel Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<double>(),
        Array.Empty<double>(),
        0d,
        0d,
        Array.Empty<decimal>(),
        false);
}
