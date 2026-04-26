using System;
using System.Linq;
using System.Text.Json;
using BlazorOptions.API.Positions;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class PositionCreateViewModel : Bindable, IDisposable
{
    private const string DefaultsStoragePrefix = "positionCreateDefaults:";
    private const decimal DefaultSizeFallback = 10m;
    private const int DefaultMinDaysOut = 7;
    private readonly IExchangeServiceFactory _exchangeServiceFactory;
    private readonly ILocalStorageService _localStorageService;
    private readonly ILegsParserService _legsParserService;
    private readonly ExchangeConnectionsService _exchangeConnectionsService;
    private readonly ActivePositionsPanelViewModel _activePositions;
    private string? _initialName;
    private string? _initialBaseAsset;
    private string? _initialQuoteAsset;
    private string? _selectedExchangeConnectionId;
    private string? _currentBaseAsset;
    private decimal _defaultSize = DefaultSizeFallback;
    private DateTime? _defaultExpirationDate;
    private string _legInput = string.Empty;
    private string? _legInputError;
    private IReadOnlyList<LegPreviewItem> _legPreviewItems = Array.Empty<LegPreviewItem>();
    private string? _exchangeServiceConnectionId;
    private IExchangeService? _exchangeServiceHandle;

    public PositionCreateViewModel(
        IExchangeServiceFactory exchangeServiceFactory,
        ILocalStorageService localStorageService,
        ILegsParserService legsParserService,
        ExchangeConnectionsService exchangeConnectionsService,
        ActivePositionsPanelViewModel activePositions)
    {
        _exchangeServiceFactory = exchangeServiceFactory;
        _localStorageService = localStorageService;
        _legsParserService = legsParserService;
        _exchangeConnectionsService = exchangeConnectionsService;
        _activePositions = activePositions;
    }

    public IReadOnlyList<ExchangeConnectionModel> ExchangeConnections { get; private set; } = Array.Empty<ExchangeConnectionModel>();

    public ActivePositionsPanelViewModel ActivePositions => _activePositions;

    public string BaseAsset => ActivePositions.BaseAsset;

    public string QuoteAsset => ActivePositions.QuoteAsset;

    public string SelectedExchangeConnectionId
    {
        get => _selectedExchangeConnectionId ?? ExchangeConnectionModel.BybitMainId;
        private set => SetField(ref _selectedExchangeConnectionId, value);
    }

    public string LegInput
    {
        get => _legInput;
        set
        {
            if (SetField(ref _legInput, value))
            {
                UpdateLegPreviewDescriptions();
            }
        }
    }

    public string? LegInputError
    {
        get => _legInputError;
        private set => SetField(ref _legInputError, value);
    }

    public IReadOnlyList<LegPreviewItem> LegPreviewItems
    {
        get => _legPreviewItems;
        private set => SetField(ref _legPreviewItems, value);
    }

    public decimal DefaultSize
    {
        get => _defaultSize;
        private set => SetField(ref _defaultSize, value);
    }

    public DateTime? DefaultExpirationDate
    {
        get => _defaultExpirationDate;
        private set => SetField(ref _defaultExpirationDate, value);
    }

    public string? InitialName
    {
        get => _initialName;
        private set => SetField(ref _initialName, value);
    }

    public string? InitialBaseAsset
    {
        get => _initialBaseAsset;
        private set => SetField(ref _initialBaseAsset, value);
    }

    public string? InitialQuoteAsset
    {
        get => _initialQuoteAsset;
        private set => SetField(ref _initialQuoteAsset, value);
    }

    public async Task InitializeAsync(string? initialName, string? initialBaseAsset, string? initialQuoteAsset, string? initialExchangeConnectionId)
    {
        InitialName = string.IsNullOrWhiteSpace(initialName) ? "Position" : initialName.Trim();
        InitialBaseAsset = string.IsNullOrWhiteSpace(initialBaseAsset) ? null : initialBaseAsset.Trim();
        InitialQuoteAsset = string.IsNullOrWhiteSpace(initialQuoteAsset) ? null : initialQuoteAsset.Trim();
        ExchangeConnections = _exchangeConnectionsService.GetConnections();
        SelectedExchangeConnectionId = _exchangeConnectionsService.GetConnectionOrDefault(initialExchangeConnectionId).Id;
        LegInput = string.Empty;
        LegInputError = null;
        await UpdateBaseAssetAsync(InitialBaseAsset);
        await InitializeActivePositionsAsync(InitialBaseAsset, InitialQuoteAsset);

        if (string.IsNullOrWhiteSpace(initialExchangeConnectionId))
        {
            await AutoSelectConnectionWithPositionsAsync(InitialBaseAsset, InitialQuoteAsset);
        }
    }

    public async Task SetSelectedExchangeConnectionAsync(string? connectionId)
    {
        SelectedExchangeConnectionId = _exchangeConnectionsService.GetConnectionOrDefault(connectionId).Id;
        await InitializeActivePositionsAsync(ActivePositions.BaseAsset, ActivePositions.QuoteAsset);
        OnPropertyChanged(nameof(BaseAsset));
        OnPropertyChanged(nameof(QuoteAsset));
    }

    public async Task SetBaseAssetAsync(string value)
    {
        await ActivePositions.SetBaseAsset(value);
        await UpdateBaseAssetAsync(ActivePositions.BaseAsset);
        OnPropertyChanged(nameof(BaseAsset));
    }

    public async Task SetQuoteAssetAsync(string value)
    {
        await ActivePositions.SetQuoteAsset(value);
        OnPropertyChanged(nameof(QuoteAsset));
    }

    public async Task<PositionModel?> CreatePositionAsync(string? name)
    {
        var baseAsset = ActivePositions.BaseAsset.Trim().ToUpperInvariant();
        var quoteAsset = ActivePositions.QuoteAsset.Trim().ToUpperInvariant();
        var initialLegs = await TryBuildLegsAsync(baseAsset);
        if (initialLegs is null)
        {
            return null;
        }

        return CreatePositionModel(
            name,
            baseAsset,
            quoteAsset,
            initialLegs,
            ActivePositions.SelectedPositions.ToList());
    }

    public PositionModel CreatePositionModel(
        string? name,
        string? baseAsset,
        string? quoteAsset,
        IReadOnlyList<LegModel>? initialLegs,
        IReadOnlyList<ExchangePosition>? selectedBybitPositions)
    {
        var position = new PositionModel
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Position" : name.Trim(),
            BaseAsset = string.IsNullOrWhiteSpace(baseAsset) ? "ETH" : baseAsset.Trim().ToUpperInvariant(),
            QuoteAsset = string.IsNullOrWhiteSpace(quoteAsset) ? "USDT" : quoteAsset.Trim().ToUpperInvariant(),
            ExchangeConnectionId = SelectedExchangeConnectionId,
            CreationTimeUtc = ResolveCreationTimeUtc(selectedBybitPositions)
        };

        if (initialLegs is not null && initialLegs.Count > 0)
        {
            foreach (var leg in initialLegs)
            {
                var cloned = leg.Clone();
                cloned.Symbol = ResolveLegSymbolForPosition(cloned, position);
                position.Legs.Add(cloned);
            }
        }

        if (selectedBybitPositions is not null && selectedBybitPositions.Count > 0)
        {
            using var exchangeService = _exchangeServiceFactory.Create(SelectedExchangeConnectionId);
            foreach (var exchangePosition in selectedBybitPositions)
            {
                var size = DetermineSignedSize(exchangePosition);
                if (Math.Abs(size) < 0.0001m)
                {
                    continue;
                }

                if (!exchangeService.TryCreateLeg(exchangePosition.Symbol, size, position.BaseAsset, exchangePosition.Category, out var leg))
                {
                    continue;
                }

                leg.IsReadOnly = true;
                leg.Status = LegStatus.Active;
                leg.Price = exchangePosition.AvgPrice;
                leg.Symbol = exchangePosition.Symbol;
                leg.ImpliedVolatility = null;
                position.Legs.Add(leg);
            }
        }

        return position;
    }

    private string? ResolveLegSymbolForPosition(LegModel leg, PositionModel position)
    {
        using var exchangeService = _exchangeServiceFactory.Create(SelectedExchangeConnectionId);
        var formatted = exchangeService.FormatSymbol(leg, position.BaseAsset, position.QuoteAsset);
        if (!string.IsNullOrWhiteSpace(formatted))
        {
            return formatted;
        }

        return string.IsNullOrWhiteSpace(leg.Symbol) ? null : leg.Symbol.Trim().ToUpperInvariant();
    }

    public async Task UpdateBaseAssetAsync(string? baseAsset)
    {
        var normalized = NormalizeBaseAsset(baseAsset);
        if (string.Equals(_currentBaseAsset, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentBaseAsset = normalized;
        await LoadDefaultsAsync(normalized);
        UpdateLegPreviewDescriptions();
    }

    public async Task SetDefaultSizeAsync(decimal value)
    {
        var normalized = value <= 0 ? DefaultSizeFallback : Math.Abs(value);
        if (!SetField(ref _defaultSize, normalized))
        {
            return;
        }

        await SaveDefaultsAsync();
        UpdateLegPreviewDescriptions();
    }

    public async Task SetDefaultExpirationAsync(DateTime? date)
    {
        if (_currentBaseAsset is null)
        {
            return;
        }

        var resolved = await NormalizeDefaultExpirationAsync(_currentBaseAsset, date);
        if (!SetField(ref _defaultExpirationDate, resolved))
        {
            return;
        }

        await SaveDefaultsAsync();
        UpdateLegPreviewDescriptions();
    }

    public async Task<IReadOnlyList<LegModel>?> TryBuildLegsAsync(string? baseAsset)
    {
        LegInputError = null;

        var failed = false;
        var failedEntries = new List<string>();
        var parsedLegs = new List<LegModel>();
        var resolvedBaseAsset = NormalizeBaseAsset(baseAsset);
        var defaultSize = DefaultSize <= 0 ? DefaultSizeFallback : DefaultSize;
        var defaultExpiration = await EnsureDefaultExpirationAsync(resolvedBaseAsset);

        try
        {
            var legs = _legsParserService.ParseLegs(LegInput, defaultSize, defaultExpiration, resolvedBaseAsset);
            parsedLegs.AddRange(legs);
        }
        catch (LegsParserService.LegsParseException ex)
        {
            failed = true;
            failedEntries.Add(ex.Message);
        }

        if (!failed && parsedLegs.Count > 0)
        {
            await _legsParserService.ApplyTickerDefaultsAsync(parsedLegs, resolvedBaseAsset, null);
        }

        if (failed)
        {
            var sample = failedEntries.Count > 0 ? $" Example: {failedEntries[0]}" : string.Empty;
            LegInputError = $"One or more legs could not be parsed. Check the input format.{sample}";
            return null;
        }

        return parsedLegs;
    }

    private void UpdateLegPreviewDescriptions()
    {
        if (string.IsNullOrWhiteSpace(LegInput))
        {
            LegPreviewItems = Array.Empty<LegPreviewItem>();
            return;
        }

        var resolvedBaseAsset = NormalizeBaseAsset(_currentBaseAsset);
        var defaultSize = DefaultSize <= 0 ? DefaultSizeFallback : DefaultSize;
        var defaultExpiration = DefaultExpirationDate ?? DateTime.UtcNow.Date.AddDays(DefaultMinDaysOut);

        try
        {
            var legs = _legsParserService.ParseLegs(LegInput, defaultSize, defaultExpiration, resolvedBaseAsset);
            if (legs.Count == 0)
            {
                LegPreviewItems = Array.Empty<LegPreviewItem>();
                return;
            }

            var previews = legs
                .Select(leg => new LegPreviewItem(
                    _legsParserService.BuildPreviewDescription(new[] { leg }, null, resolvedBaseAsset),
                    leg.Size >= 0))
                .Where(item => !string.IsNullOrWhiteSpace(item.Description))
                .ToList();

            LegPreviewItems = previews;
        }
        catch (LegsParserService.LegsParseException)
        {
            LegPreviewItems = Array.Empty<LegPreviewItem>();
        }
    }


    private async Task LoadDefaultsAsync(string? baseAsset)
    {
        DefaultSize = DefaultSizeFallback;
        DefaultExpirationDate = await EnsureDefaultExpirationAsync(baseAsset);

        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return;
        }

        var stored = await _localStorageService.GetItemAsync(BuildDefaultsKey(baseAsset));
        var deserialized = PositionCreateDefaults.TryDeserialize(stored);
        if (deserialized is null)
        {
            return;
        }

        if (deserialized.DefaultSize > 0)
        {
            DefaultSize = deserialized.DefaultSize;
        }

        if (deserialized.DefaultExpirationDate.HasValue)
        {
            var resolved = await NormalizeDefaultExpirationAsync(baseAsset, deserialized.DefaultExpirationDate);
            DefaultExpirationDate = resolved;
            if (!DateTime.Equals(resolved?.Date, deserialized.DefaultExpirationDate.Value.Date))
            {
                await SaveDefaultsAsync();
            }
        }
    }

    private async Task<DateTime?> EnsureDefaultExpirationAsync(string? baseAsset)
    {
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return DateTime.UtcNow.Date.AddDays(DefaultMinDaysOut);
        }

        if (DefaultExpirationDate.HasValue && DefaultExpirationDate.Value.Date >= DateTime.UtcNow.Date)
        {
            return DefaultExpirationDate.Value.Date;
        }

        var resolved = await ResolveNextExpirationAsync(baseAsset);
        DefaultExpirationDate = resolved;
        await SaveDefaultsAsync();
        return resolved;
    }

    private async Task<DateTime?> NormalizeDefaultExpirationAsync(string baseAsset, DateTime? candidate)
    {
        if (!candidate.HasValue)
        {
            return await ResolveNextExpirationAsync(baseAsset);
        }

        var date = candidate.Value.Date;
        if (date < DateTime.UtcNow.Date)
        {
            return await ResolveNextExpirationAsync(baseAsset);
        }

        return date;
    }

    private async Task<DateTime?> ResolveNextExpirationAsync(string baseAsset)
    {
        using var exchangeService = _exchangeServiceFactory.Create(SelectedExchangeConnectionId);
        var next = ResolveNextExpirationFromSnapshot(baseAsset, exchangeService);
        if (next.HasValue)
        {
            return next.Value.Date;
        }

        await exchangeService.OptionsChain.UpdateTickersAsync(baseAsset);
        next = ResolveNextExpirationFromSnapshot(baseAsset, exchangeService);
        return next?.Date ?? DateTime.UtcNow.Date.AddDays(DefaultMinDaysOut);
    }

    private async Task InitializeActivePositionsAsync(string? baseAsset, string? quoteAsset)
    {
        var exchangeService = SwitchExchangeService(SelectedExchangeConnectionId);
        await _activePositions.InitializeAsync(exchangeService, baseAsset, quoteAsset, null);
    }

    private async Task AutoSelectConnectionWithPositionsAsync(string? baseAsset, string? quoteAsset)
    {
        if (_activePositions.Positions.Count > 0)
        {
            return;
        }

        foreach (var connection in ExchangeConnections)
        {
            if (string.Equals(connection.Id, SelectedExchangeConnectionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SelectedExchangeConnectionId = connection.Id;
            await InitializeActivePositionsAsync(baseAsset, quoteAsset);
            if (_activePositions.Positions.Count > 0)
            {
                OnPropertyChanged(nameof(SelectedExchangeConnectionId));
                OnPropertyChanged(nameof(BaseAsset));
                OnPropertyChanged(nameof(QuoteAsset));
                return;
            }
        }
    }

    private IExchangeService SwitchExchangeService(string? exchangeConnectionId)
    {
        if (_exchangeServiceHandle is not null &&
            string.Equals(_exchangeServiceConnectionId, exchangeConnectionId, StringComparison.OrdinalIgnoreCase))
        {
            return _exchangeServiceHandle;
        }

        var previousService = _exchangeServiceHandle;
        _exchangeServiceHandle = _exchangeServiceFactory.Create(exchangeConnectionId);
        _exchangeServiceConnectionId = exchangeConnectionId;
        previousService?.Dispose();
        return _exchangeServiceHandle;
    }

    private static DateTime? ResolveNextExpirationFromSnapshot(string baseAsset, IExchangeService exchangeService)
    {
        var minDate = DateTime.UtcNow.Date.AddDays(DefaultMinDaysOut);
        var expirations = exchangeService.OptionsChain
            .GetTickersByBaseAsset(baseAsset)
            .Select(ticker => ticker.ExpirationDate.Date)
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        if (expirations.Count == 0)
        {
            return null;
        }

        var next = expirations.FirstOrDefault(date => date >= minDate);
        return next != default ? next : expirations.Last();
    }

    private async Task SaveDefaultsAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentBaseAsset))
        {
            return;
        }

        var payload = PositionCreateDefaults.Serialize(new PositionCreateDefaults
        {
            DefaultSize = DefaultSize,
            DefaultExpirationDate = DefaultExpirationDate
        });

        await _localStorageService.SetItemAsync(BuildDefaultsKey(_currentBaseAsset), payload);
    }

    private static string NormalizeBaseAsset(string? baseAsset)
    {
        return string.IsNullOrWhiteSpace(baseAsset) ? string.Empty : baseAsset.Trim().ToUpperInvariant();
    }

    private static string BuildDefaultsKey(string baseAsset)
    {
        return $"{DefaultsStoragePrefix}{baseAsset.Trim().ToUpperInvariant()}";
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
            if (string.Equals(normalized, "Sell", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Short", StringComparison.OrdinalIgnoreCase))
            {
                return -magnitude;
            }
        }

        return magnitude;
    }

    private static DateTime? ResolveCreationTimeUtc(IReadOnlyList<ExchangePosition>? selectedBybitPositions)
    {
        if (selectedBybitPositions is null || selectedBybitPositions.Count == 0)
        {
            return DateTime.UtcNow;
        }

        var minCreated = selectedBybitPositions
            .Where(position => position.CreatedTimeUtc.HasValue)
            .Select(position => position.CreatedTimeUtc!.Value)
            .OrderBy(value => value)
            .FirstOrDefault();

        return minCreated != default ? minCreated : DateTime.UtcNow;
    }

    private sealed class PositionCreateDefaults
    {
        public decimal DefaultSize { get; set; } = DefaultSizeFallback;

        public DateTime? DefaultExpirationDate { get; set; }

        public static PositionCreateDefaults? TryDeserialize(string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<PositionCreateDefaults>(payload);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static string Serialize(PositionCreateDefaults defaults)
        {
            return JsonSerializer.Serialize(defaults);
        }
    }

    public void Dispose()
    {
        _activePositions.Dispose();
        _exchangeServiceHandle?.Dispose();
        _exchangeServiceHandle = null;
    }
}
