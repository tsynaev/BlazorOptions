using BlazorOptions.API.Common;
using BlazorOptions.API.Positions;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class HomeDashboardViewModel : Bindable
{
    private readonly IPositionsPort _positionsPort;
    private readonly OptionsService _optionsService;
    private readonly IExchangeService _exchangeService;
    private bool _isLoading;
    private string? _errorMessage;
    private IReadOnlyList<HomePositionCardModel> _positions = Array.Empty<HomePositionCardModel>();
    private IReadOnlyList<HomePositionGroupModel> _groups = Array.Empty<HomePositionGroupModel>();

    public HomeDashboardViewModel(
        IPositionsPort positionsPort,
        OptionsService optionsService,
        IExchangeService exchangeService)
    {
        _positionsPort = positionsPort;
        _optionsService = optionsService;
        _exchangeService = exchangeService;
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

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var models = await _positionsPort.LoadPositionsAsync();
            await EnsureOptionChainsAsync(models);
            var pricesBySymbol = await LoadCurrentPricesAsync(models);
            var cards = models
                .Select(model =>
                {
                    var symbol = $"{model.BaseAsset}{model.QuoteAsset}".ToUpperInvariant();
                    pricesBySymbol.TryGetValue(symbol, out var currentPrice);
                    return BuildCard(model, currentPrice);
                })
                .ToArray();
            Positions = cards;
            Groups = cards
                .GroupBy(card => card.AssetPair, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new HomePositionGroupModel(group.Key, group.ToArray()))
                .ToArray();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Positions = Array.Empty<HomePositionCardModel>();
            Groups = Array.Empty<HomePositionGroupModel>();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private HomePositionCardModel BuildCard(PositionModel model, decimal? exchangePrice)
    {
        var legs = model.Collections
            .SelectMany(collection => collection.Legs)
            .Where(leg => leg.IsIncluded)
            .ToList();

        var chart = BuildChart(legs, model.ChartRange);
        var entryValue = legs.Sum(ResolveEntryValue);
        var currentPrice = exchangePrice ?? ResolveCurrentPrice(chart, model.ChartRange);
        var tempPnl = legs.Count > 0
            ? _optionsService.CalculateTotalProfit(legs, currentPrice)
            : 0m;
        var closed = model.Closed.Include ? model.Closed.TotalNet : 0m;
        var totalPnl = tempPnl + closed;
        var pnlPercent = entryValue > 0m ? (totalPnl / entryValue) * 100m : (decimal?)null;
        var currentPricePercent = ResolveCurrentPricePercent(currentPrice, chart.XMin, chart.XMax);

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
            exchangePrice,
            currentPricePercent);
    }

    private HomeMiniChartModel BuildChart(IReadOnlyList<LegModel> legs, BlazorChart.Models.ChartRange? range)
    {
        var (xs, profits, _) = _optionsService.GeneratePosition(
            legs,
            points: 48,
            xMinOverride: range?.XMin,
            xMaxOverride: range?.XMax);

        var labels = xs
            .Select(value => Math.Round(value, 0).ToString("0"))
            .ToArray();
        IReadOnlyList<string> sparseLabels = BuildSparseLabels(labels, 8);
        var values = profits
            .Select(value => (double)value)
            .ToArray();

        return new HomeMiniChartModel(sparseLabels, values, (double)xs[0], (double)xs[^1]);
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
        if (entryValue > 0)
        {
            pnlPercent = pnl / entryValue * 100m;
        }

        var severityClass = ResolveLegSeverityClass(pnlPercent);
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
            pnl);
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

    private static string ResolveLegSeverityClass(decimal? pnlPercent)
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
        if (loss >= 30m)
        {
            return "home-leg-critical";
        }

        if (loss >= 20m)
        {
            return "home-leg-high";
        }

        if (loss >= 10m)
        {
            return "home-leg-medium";
        }

        return "home-leg-low";
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
            .Where(model => model.Collections.SelectMany(collection => collection.Legs).Any(leg => leg.Type != LegType.Future))
            .Select(model => model.BaseAsset?.Trim())
            .Where(asset => !string.IsNullOrWhiteSpace(asset))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var baseAsset in baseAssets)
        {
            try
            {
                await _exchangeService.OptionsChain.EnsureBaseAssetAsync(baseAsset!);
            }
            catch
            {
                // Home dashboard should stay resilient when option-chain refresh fails.
            }
        }
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
    decimal? CurrentPrice,
    double? CurrentPricePercent);

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
    decimal Pnl);

public sealed record HomePositionGroupModel(
    string AssetPair,
    IReadOnlyList<HomePositionCardModel> Positions);

public sealed record HomeMiniChartModel(
    IReadOnlyList<string> XLabels,
    IReadOnlyList<double> Values,
    double XMin,
    double XMax);
