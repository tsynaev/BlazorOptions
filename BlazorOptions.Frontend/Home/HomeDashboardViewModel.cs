using BlazorOptions.Services;
using System.Text.Json;

namespace BlazorOptions.ViewModels;

public sealed class HomeDashboardViewModel : Bindable
{
    private const string DashboardCacheStorageKey = "blazor-options-dashboard-cache";
    private static readonly string[] PositionExpirationFormats = { "ddMMMyy", "ddMMMyyyy" };
    private static readonly JsonSerializerOptions DashboardCacheJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IPositionsPort _positionsPort;
    private readonly IExchangeService _exchangeService;
    private readonly ILocalStorageService _localStorageService;
    private readonly INavigationService _navigationService;
    private readonly DvolIndexService _dvolIndexService;
    private readonly PositionCardViewModelFactory _positionCardViewModelFactory;
    private bool _isLoading;
    private string? _errorMessage;
    private IReadOnlyList<PositionCardViewModel> _positions = Array.Empty<PositionCardViewModel>();
    private IReadOnlyList<HomePositionGroupModel> _groups = Array.Empty<HomePositionGroupModel>();
    private AccountRiskSettings _riskSettings = AccountRiskSettingsStorage.Default;
    private CancellationTokenSource? _chartLoadCts;

    public HomeDashboardViewModel(
        IPositionsPort positionsPort,
        IExchangeService exchangeService,
        ILocalStorageService localStorageService,
        INavigationService navigationService,
        DvolIndexService dvolIndexService,
        PositionCardViewModelFactory positionCardViewModelFactory)
    {
        _positionsPort = positionsPort;
        _exchangeService = exchangeService;
        _localStorageService = localStorageService;
        _navigationService = navigationService;
        _dvolIndexService = dvolIndexService;
        _positionCardViewModelFactory = positionCardViewModelFactory;
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
                SetCards(await BuildCardsAsync(cachedModels, null, withChart: true, includeTempLine: false));
            }

            var serverModels = (await _positionsPort.LoadPositionsAsync()).ToArray();
            var pricesBySymbol = await LoadCurrentPricesAsync(serverModels);
            SetCards(await BuildCardsAsync(serverModels, pricesBySymbol, withChart: hasCachedModels, includeTempLine: true));

            IReadOnlyList<PositionModel> modelsForCharts = serverModels;
            try
            {
                await LoadExchangeSnapshotAsync();
                SetCards(await BuildCardsAsync(serverModels, pricesBySymbol, withChart: hasCachedModels, includeTempLine: true));

                await SaveCachedPositionsAsync(serverModels);
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
                Positions = Array.Empty<PositionCardViewModel>();
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
            var card = await _positionCardViewModelFactory.CreateAsync(
                model,
                currentPrice,
                withChart: true,
                includeTempLine: true,
                _riskSettings);
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

    private async Task<PositionCardViewModel[]> BuildCardsAsync(
        IReadOnlyList<PositionModel> models,
        IReadOnlyDictionary<string, decimal?>? pricesBySymbol,
        bool withChart,
        bool includeTempLine)
    {
        var cards = new List<PositionCardViewModel>(models.Count);
        foreach (var model in models)
        {
            decimal? currentPrice = null;
            if (pricesBySymbol is not null)
            {
                var symbol = $"{model.BaseAsset}{model.QuoteAsset}".ToUpperInvariant();
                pricesBySymbol.TryGetValue(symbol, out currentPrice);
            }

            cards.Add(await _positionCardViewModelFactory.CreateAsync(
                model,
                currentPrice,
                withChart,
                includeTempLine,
                _riskSettings));
        }

        return cards.ToArray();
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

    private static PositionModel[] ClonePositions(IReadOnlyList<PositionModel> models)
    {
        return models.Select(model => model.Clone()).ToArray();
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

    private void SetCards(IReadOnlyList<PositionCardViewModel> cards)
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



public sealed record HomePositionGroupModel(
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

