using BlazorOptions.Services;
using System.Text.Json;

namespace BlazorOptions.ViewModels;

public sealed class HomeDashboardViewModel : Bindable, IAsyncDisposable
{
    private const string DashboardCacheStorageKey = "blazor-options-dashboard-cache";
    private static readonly JsonSerializerOptions DashboardCacheJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IPositionsPort _positionsPort;
    private readonly IExchangeServiceFactory _exchangeServiceFactory;
    private readonly ILocalStorageService _localStorageService;
    private readonly DvolIndexService _dvolIndexService;
    private readonly PositionCardViewModelFactory _positionCardViewModelFactory;
    private readonly ExchangeConnectionsService _exchangeConnectionsService;
    private bool _isLoading;
    private string? _errorMessage;
    private IReadOnlyList<PositionCardViewModel> _positions = Array.Empty<PositionCardViewModel>();
    private IReadOnlyList<HomeExchangeGroupModel> _groups = Array.Empty<HomeExchangeGroupModel>();
    private AccountRiskSettings _riskSettings = AccountRiskSettingsStorage.Default;
    private CancellationTokenSource? _chartLoadCts;
    private readonly Dictionary<string, IExchangeService> _exchangeServicesByConnectionId = new(StringComparer.OrdinalIgnoreCase);

    public HomeDashboardViewModel(
        IPositionsPort positionsPort,
        IExchangeServiceFactory exchangeServiceFactory,
        ILocalStorageService localStorageService,
        DvolIndexService dvolIndexService,
        PositionCardViewModelFactory positionCardViewModelFactory,
        ExchangeConnectionsService exchangeConnectionsService)
    {
        _positionsPort = positionsPort;
        _exchangeServiceFactory = exchangeServiceFactory;
        _localStorageService = localStorageService;
        _dvolIndexService = dvolIndexService;
        _positionCardViewModelFactory = positionCardViewModelFactory;
        _exchangeConnectionsService = exchangeConnectionsService;
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

    public IReadOnlyList<PositionCardViewModel> Positions
    {
        get => _positions;
        private set => SetField(ref _positions, value);
    }

    public IReadOnlyList<HomeExchangeGroupModel> Groups
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
                SetCards(await BuildCardsAsync(cachedModels, null, withChart: true, includeTempLine: false));
            }

            var serverModels = (await _positionsPort.LoadPositionsAsync()).ToArray();
            await EnsureExchangeServicesAsync(serverModels);
            var pricesBySymbol = await LoadCurrentPricesAsync(serverModels);
            SetCards(await BuildCardsAsync(serverModels, pricesBySymbol, withChart: hasCachedModels, includeTempLine: true));

            IReadOnlyList<PositionModel> modelsForCharts = serverModels;
            await SaveCachedPositionsAsync(serverModels);

            _ = WarmUpChartsAsync(modelsForCharts, pricesBySymbol, _chartLoadCts.Token);
            _ = WarmUpDvolAsync(_chartLoadCts.Token);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            if (Positions.Count == 0)
            {
                Positions = Array.Empty<PositionCardViewModel>();
                Groups = Array.Empty<HomeExchangeGroupModel>();
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
        foreach (var group in GroupByExchangeConnection(models))
        {
            var exchangeService = GetOrCreateExchangeService(group.Key);

            foreach (var model in group)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var symbolKey = BuildPriceKey(model.ExchangeConnectionId, model.BaseAsset, model.QuoteAsset);
                pricesBySymbol.TryGetValue(symbolKey, out var currentPrice);
                var card = await _positionCardViewModelFactory.CreateAsync(
                    model,
                    exchangeService,
                    currentPrice,
                    withChart: true,
                    includeTempLine: true,
                    _riskSettings);
                ReplaceCard(card);
                await Task.Yield();
            }
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

    private async Task<PositionCardViewModel[]> BuildCardsAsync(
        IReadOnlyList<PositionModel> models,
        IReadOnlyDictionary<string, decimal?>? pricesBySymbol,
        bool withChart,
        bool includeTempLine)
    {
        var cards = new List<PositionCardViewModel>(models.Count);
        foreach (var group in GroupByExchangeConnection(models))
        {
            var exchangeService = GetOrCreateExchangeService(group.Key);

            foreach (var model in group)
            {
                decimal? currentPrice = null;
                if (pricesBySymbol is not null)
                {
                    var symbol = BuildPriceKey(model.ExchangeConnectionId, model.BaseAsset, model.QuoteAsset);
                    pricesBySymbol.TryGetValue(symbol, out currentPrice);
                }

                cards.Add(await _positionCardViewModelFactory.CreateAsync(
                    model,
                    exchangeService,
                    currentPrice,
                    withChart,
                    includeTempLine,
                    _riskSettings));
            }
        }

        return cards.ToArray();
    }

    private async Task<Dictionary<string, decimal?>> LoadCurrentPricesAsync(IReadOnlyList<PositionModel> models)
    {
        var result = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in GroupByExchangeConnection(models))
        {
            var exchangeService = GetOrCreateExchangeService(group.Key);
            var symbols = group
                .Select(model => BuildPriceKey(model.ExchangeConnectionId, model.BaseAsset, model.QuoteAsset))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var symbolKey in symbols)
            {
                var symbol = symbolKey.Split('|')[1];
                result[symbolKey] = await TryLoadCurrentPriceAsync(exchangeService, symbol);
            }
        }

        return result;
    }

    private static IReadOnlyList<IGrouping<string, PositionModel>> GroupByExchangeConnection(IReadOnlyList<PositionModel> models)
    {
        return models
            .GroupBy(model => NormalizeExchangeConnectionId(model.ExchangeConnectionId), StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static PositionModel[] ClonePositions(IReadOnlyList<PositionModel> models)
    {
        return models.Select(model => model.Clone()).ToArray();
    }

    private static async Task<decimal?> TryLoadCurrentPriceAsync(IExchangeService exchangeService, string symbol)
    {
        try
        {
            var toUtc = DateTime.UtcNow;
            var fromUtc = toUtc.AddHours(-2);
            var candles = await exchangeService.Tickers.GetCandlesWithVolumeAsync(symbol, fromUtc, toUtc, 60);
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

    private void SetCards(IReadOnlyList<PositionCardViewModel> cards)
    {
        var existingDvolByAssetGroup = Groups
            .SelectMany(group => group.AssetGroups)
            .ToDictionary(group => BuildGroupKey(group.ExchangeName, group.AssetPair), group => group.DvolChart, StringComparer.OrdinalIgnoreCase);
        var existingExchangeServiceByConnection = Groups
            .ToDictionary(group => group.ExchangeConnectionId, group => group.ExchangeService, StringComparer.OrdinalIgnoreCase);

        Positions = cards;
        Groups = cards
            .GroupBy(card => NormalizeExchangeConnectionId(card.ExchangeConnectionId), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.First().ExchangeName, StringComparer.OrdinalIgnoreCase)
            .Select(exchangeGroup =>
            {
                var firstCard = exchangeGroup.First();
                var exchangeService = existingExchangeServiceByConnection.TryGetValue(exchangeGroup.Key, out var existingExchangeService)
                    ? existingExchangeService
                    : GetOrCreateExchangeService(exchangeGroup.Key);
                var assetGroups = exchangeGroup
                    .GroupBy(card => card.AssetPair, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(assetGroup =>
                    {
                        var assetFirstCard = assetGroup.First();
                        var groupKey = BuildGroupKey(assetFirstCard.ExchangeName, assetGroup.Key);
                        var dvol = existingDvolByAssetGroup.TryGetValue(groupKey, out var existing)
                            ? existing
                            : HomeDvolChartModel.Loading(ResolveBaseAsset(assetGroup.Key));
                        return new HomePositionAssetGroupModel(
                            assetFirstCard.ExchangeName,
                            assetGroup.Key,
                            assetGroup.ToArray(),
                            dvol);
                    })
                    .ToArray();

                return new HomeExchangeGroupModel(
                    exchangeGroup.Key,
                    firstCard.ExchangeName,
                    assetGroups,
                    exchangeService);
            })
            .ToArray();
    }

    private async Task EnsureExchangeServicesAsync(IReadOnlyList<PositionModel> models)
    {
        var activeConnectionIds = GroupByExchangeConnection(models)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staleConnectionIds = _exchangeServicesByConnectionId.Keys
            .Where(connectionId => !activeConnectionIds.Contains(connectionId))
            .ToArray();

        foreach (var staleConnectionId in staleConnectionIds)
        {
            if (_exchangeServicesByConnectionId.Remove(staleConnectionId, out var staleExchangeService))
            {
                await staleExchangeService.DisposeAsync();
            }
        }

        foreach (var connectionId in activeConnectionIds)
        {
            GetOrCreateExchangeService(connectionId);
        }
    }

    private IExchangeService GetOrCreateExchangeService(string? exchangeConnectionId)
    {
        var normalizedConnectionId = NormalizeExchangeConnectionId(exchangeConnectionId);
        if (_exchangeServicesByConnectionId.TryGetValue(normalizedConnectionId, out var existingExchangeService))
        {
            return existingExchangeService;
        }

        var exchangeService = _exchangeServiceFactory.Create(normalizedConnectionId);
        _exchangeServicesByConnectionId[normalizedConnectionId] = exchangeService;
        return exchangeService;
    }

    public async ValueTask DisposeAsync()
    {
        _chartLoadCts?.Cancel();
        _chartLoadCts?.Dispose();
        _chartLoadCts = null;

        foreach (var exchangeService in _exchangeServicesByConnectionId.Values)
        {
            await exchangeService.DisposeAsync();
        }

        _exchangeServicesByConnectionId.Clear();
    }

    private void ReplaceCard(PositionCardViewModel updatedCard)
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
        var exchangeGroups = Groups.ToArray();
        foreach (var exchangeGroup in exchangeGroups)
        {
            foreach (var assetGroup in exchangeGroup.AssetGroups)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var baseAsset = ResolveBaseAsset(assetGroup.AssetPair);
                if (string.IsNullOrWhiteSpace(baseAsset))
                {
                    ReplaceGroupDvol(exchangeGroup.ExchangeConnectionId, assetGroup.AssetPair, HomeDvolChartModel.Unavailable(string.Empty));
                    continue;
                }

                var cachedChart = await _dvolIndexService.LoadCachedAsync(baseAsset);
                if (cachedChart is not null)
                {
                    ReplaceGroupDvol(exchangeGroup.ExchangeConnectionId, assetGroup.AssetPair, BuildDvolModel(cachedChart));
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
                        ReplaceGroupDvol(exchangeGroup.ExchangeConnectionId, assetGroup.AssetPair, HomeDvolChartModel.Unavailable(baseAsset));
                    }

                    continue;
                }

                ReplaceGroupDvol(exchangeGroup.ExchangeConnectionId, assetGroup.AssetPair, BuildDvolModel(chart));
            }
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

    private void ReplaceGroupDvol(string exchangeConnectionId, string assetPair, HomeDvolChartModel dvolChart)
    {
        if (Groups.Count == 0)
        {
            return;
        }

        var exchangeGroups = Groups.ToArray();
        var exchangeIndex = Array.FindIndex(exchangeGroups, group => string.Equals(group.ExchangeConnectionId, exchangeConnectionId, StringComparison.OrdinalIgnoreCase));
        if (exchangeIndex < 0)
        {
            return;
        }

        var assetGroups = exchangeGroups[exchangeIndex].AssetGroups.ToArray();
        var assetIndex = Array.FindIndex(assetGroups, group => string.Equals(group.AssetPair, assetPair, StringComparison.OrdinalIgnoreCase));
        if (assetIndex < 0)
        {
            return;
        }

        assetGroups[assetIndex] = assetGroups[assetIndex] with { DvolChart = dvolChart };
        exchangeGroups[exchangeIndex] = exchangeGroups[exchangeIndex] with { AssetGroups = assetGroups };
        Groups = exchangeGroups;
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

    private static string BuildGroupKey(string exchangeName, string assetPair)
    {
        return $"{exchangeName}|{assetPair}";
    }

    private static string BuildPriceKey(string? exchangeConnectionId, string? baseAsset, string? quoteAsset)
    {
        var connectionId = NormalizeExchangeConnectionId(exchangeConnectionId);
        var symbol = $"{baseAsset}{quoteAsset}".ToUpperInvariant();
        return $"{connectionId}|{symbol}";
    }

    private static string NormalizeExchangeConnectionId(string? exchangeConnectionId)
    {
        return string.IsNullOrWhiteSpace(exchangeConnectionId)
            ? ExchangeConnectionModel.BybitMainId
            : exchangeConnectionId.Trim();
    }
}



public sealed record HomeExchangeGroupModel(
    string ExchangeConnectionId,
    string ExchangeName,
    IReadOnlyList<HomePositionAssetGroupModel> AssetGroups,
    IExchangeService ExchangeService);

public sealed record HomePositionAssetGroupModel(
    string ExchangeName,
    string AssetPair,
    IReadOnlyList<PositionCardViewModel> Positions,
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

