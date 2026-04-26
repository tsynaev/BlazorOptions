using BlazorOptions.Diagnostics;
using BlazorOptions.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;

namespace BlazorOptions.ViewModels;

public sealed class LegsCollectionViewModel : IDisposable
{
    private readonly LegViewModelFactory _legViewModelFactory;
    private readonly INotifyUserService _notifyUserService;
    private readonly ExchangeSnapshotLegSyncService _exchangeSnapshotLegSyncService;
    private string _baseAsset = string.Empty;
    private decimal? _currentPrice;
    private bool _isLive;
    private DateTime _valuationDate;
    private LegsCollectionModel _collection = default!;
    private readonly Dictionary<string, LegViewModel> _legViewModels = new(StringComparer.Ordinal);
    private readonly ObservableCollection<LegViewModel> _legs = new();
    private IReadOnlyList<LegGroup> _groupedLegs = Array.Empty<LegGroup>();
    private IExchangeService _exchangeService;
    private int _pricingUpdateVersion;
    private int _pricingUpdateScheduled;
    private bool _isLoadingAvailableLegs;
    private IReadOnlyList<AvailableLegCandidate> _availableLegCandidates = Array.Empty<AvailableLegCandidate>();
    private readonly object _dataUpdateLock = new();
    private CancellationTokenSource? _dataUpdateCts;
    private static readonly TimeSpan DataUpdateDebounce = TimeSpan.FromMilliseconds(120);
    private IReadOnlyList<ExchangeOrder> _latestOrdersSnapshot = Array.Empty<ExchangeOrder>();
    private bool _showAvailablePositionChips = false;


    private static readonly string[] PositionExpirationFormats = { "ddMMMyy", "ddMMMyyyy" };

    public LegsCollectionViewModel(
        LegViewModelFactory legViewModelFactory,
        INotifyUserService notifyUserService,
        ExchangeSnapshotLegSyncService exchangeSnapshotLegSyncService,
        IExchangeService exchangeService,
        ILegsParserService legsParserService)
    {
        _legViewModelFactory = legViewModelFactory;
        _notifyUserService = notifyUserService;
        _exchangeSnapshotLegSyncService = exchangeSnapshotLegSyncService;
        _exchangeService = exchangeService;

        QuickAdd = new QuickAddViewModel(_notifyUserService, legsParserService);
        QuickAdd.LegCreated += HandleQuickAddLegCreated;

    }

    public PositionViewModel Position { get; set; } = default!;

    public string BaseAsset
    {
        get => _baseAsset;
        set {
           _baseAsset = value;
           QuickAdd.BaseAsset = value;
        }
    }

    public decimal? CurrentPrice
    {
        get => _currentPrice;
        set
        {
            if (_currentPrice == value)
            {
                return;
            }

            _currentPrice = value;
            QuickAdd.Price = value;
            SchedulePricingUpdate();
        }
    }

    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (_isLive == value)
            {
                return;
            }

            _isLive = value;
            SchedulePricingUpdate();
        }
    }

    public DateTime ValuationDate
    {
        get => _valuationDate;
        set
        {
            if (_valuationDate == value)
            {
                return;
            }

            _valuationDate = value;
            SchedulePricingUpdate();
        }
    }

    // Apply pricing context changes in one pass to avoid UI stalls during rapid updates.
    private void SchedulePricingUpdate()
    {
        Interlocked.Increment(ref _pricingUpdateVersion);
        if (Interlocked.Exchange(ref _pricingUpdateScheduled, 1) == 1)
        {
            return;
        }

        var version = _pricingUpdateVersion;
        ApplyPricingUpdate(version);
        Interlocked.Exchange(ref _pricingUpdateScheduled, 0);
        if (version != _pricingUpdateVersion)
        {
            SchedulePricingUpdate();
        }
    }

    private void ApplyPricingUpdate(int version)
    {
        var isLive = _isLive;
        var valuationDate = _valuationDate;
        foreach (var leg in _legs)
        {
            leg.IndexPrice = _currentPrice;
            leg.IsLive = isLive;
            leg.ValuationDate = valuationDate;
        }
    }

    public LegsCollectionModel Collection
    {
        get => _collection;
        set
        {
            _collection = value;
            QuickAdd.Collection = value;
            SyncLegViewModels();
        } 
    }

    public ObservableCollection<LegViewModel> Legs => _legs;

    public IReadOnlyList<LegGroup> GroupedLegs => _groupedLegs;

    public QuickAddViewModel QuickAdd { get; }

    public decimal TotalDelta => SumDelta();

    public decimal TotalGamma => SumGreek(static leg => leg.Gamma);

    public decimal TotalVega => SumGreek(static leg => leg.Vega);

    public decimal TotalTheta => SumGreek(static leg => leg.Theta);

    public PortfolioGreekSummary GreekSummary => BuildGreekSummary();

    public decimal? TotalTempPnl => SumTempPnl();

    public bool IsLoadingAvailableLegs => _isLoadingAvailableLegs;

    public IReadOnlyList<AvailableLegCandidate> AvailableLegCandidates => _availableLegCandidates;

    public bool ShowAvailablePositionChips
    {
        get => _showAvailablePositionChips;
        set => _showAvailablePositionChips = value;
    }

    public event Func<LegModel, Task>? LegAdded;
    public event Func<LegModel, Task>? LegRemoved;
    public event Func<LegsCollectionUpdateKind, Task>? Updated;

    public string Name
    {
        get => Collection.Name;
        set => _ = SetNameAsync(value);
    }

    public string Color
    {
        get => Collection.Color;
        set => _ = SetColorAsync(value);
    }

    public bool IsVisible
    {
        get => Collection.IsVisible;
        set => _ = SetVisibilityAsync(value);
    }

    public async Task UpdateLegsAsync(LegsCollectionModel collection, IEnumerable<LegModel> legs)
    {
        collection.Legs.Clear();
        foreach (var leg in legs)
        {
            EnsureLegSymbol(leg);
            collection.Legs.Add(leg);
        }

        await RaiseUpdatedAsync(LegsCollectionUpdateKind.CollectionChanged);
    }

    public async Task AddQuickLegAsync()
    {
        using var activity = ActivitySources.Telemetry.StartActivity("LegsCollection.AddQuickLeg");
        SyncQuickAddPrice();
        await QuickAdd.AddQuickLegAsync();
    }

    public Task OnQuickLegKeyDown(string key)
    {
        SyncQuickAddPrice();
        return QuickAdd.OnQuickLegKeyDown(key);
    }

    public Task CopySymbolsToClosedPositionsAsync()
    {
        if (Position?.ClosedPositions is null)
        {
            return Task.CompletedTask;
        }

        var symbols = Collection.Legs
            .Select(leg => leg.Symbol)
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(symbol => symbol!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (symbols.Count == 0)
        {
            _notifyUserService.NotifyUser("No leg symbols available to copy.");
            return Task.CompletedTask;
        }

        Position.ClosedPositions.SetAddSymbolInput(symbols);
        return Task.CompletedTask;
    }

    public async Task RefreshAvailableLegCandidatesAsync()
    {
        if (Position is null)
        {
            _availableLegCandidates = Array.Empty<AvailableLegCandidate>();
            return;
        }

        var baseAsset = Position.Position.BaseAsset?.Trim();
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            _availableLegCandidates = Array.Empty<AvailableLegCandidate>();
            return;
        }

        _isLoadingAvailableLegs = true;
        await RaiseUpdatedAsync(LegsCollectionUpdateKind.ViewModelDataUpdated);

        try
        {
            var snapshot = await Position.GetExchangeSnapshotAsync();
            var walletSnapshot = await _exchangeService.Wallet.GetSnapshotAsync();
            await ApplyExchangeSnapshotsAsync(snapshot.Positions, snapshot.Orders, walletSnapshot);
        }
        catch (Exception ex)
        {
            _notifyUserService.NotifyUser($"Unable to load exchange positions/orders: {ex.Message}");
            _availableLegCandidates = Array.Empty<AvailableLegCandidate>();
        }
        finally
        {
            _isLoadingAvailableLegs = false;
            await RaiseUpdatedAsync(LegsCollectionUpdateKind.ViewModelDataUpdated);
        }
    }

    public async Task ApplyExchangeSnapshotsAsync(
        IReadOnlyList<ExchangePosition> positions,
        IReadOnlyList<ExchangeOrder> orders,
        ExchangeWalletSnapshot? walletSnapshot = null)
    {
        if (Position is null)
        {
            return;
        }

        var baseAsset = Position.Position.BaseAsset?.Trim();
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            if (_availableLegCandidates.Count > 0)
            {
                _availableLegCandidates = Array.Empty<AvailableLegCandidate>();
                await RaiseUpdatedAsync(LegsCollectionUpdateKind.ViewModelDataUpdated);
            }

            return;
        }

        var ordersSnapshot = orders ?? Array.Empty<ExchangeOrder>();
        _latestOrdersSnapshot = ordersSnapshot;

        var orderLegsChanged = SyncOrderLegsWithSnapshot(ordersSnapshot, positions, baseAsset);
        var candidates = BuildAvailableCandidates(positions, ordersSnapshot, walletSnapshot, baseAsset);
        var candidatesChanged = !AreSameCandidates(_availableLegCandidates, candidates);
        if (candidatesChanged)
        {
            _availableLegCandidates = candidates;
        }

        if (orderLegsChanged)
        {
            SyncLegViewModels();
            await RaiseUpdatedAsync(LegsCollectionUpdateKind.CollectionChanged);
        }

        var linkedOrdersChanged = UpdateLinkedOrdersForLegs();
        if (linkedOrdersChanged)
        {
            await RaiseUpdatedAsync(LegsCollectionUpdateKind.ViewModelDataUpdated);
        }

        if (candidatesChanged)
        {
            await RaiseUpdatedAsync(LegsCollectionUpdateKind.ViewModelDataUpdated);
        }
    }

    public async Task RefreshNonLiveMarketSnapshotsAsync()
    {
        if (_isLive)
        {
            return;
        }

        foreach (var legViewModel in _legs)
        {
            legViewModel.RefreshNonLiveSnapshot();
        }

        await RaiseUpdatedAsync(LegsCollectionUpdateKind.ViewModelDataUpdated);
    }

    public async Task AddAvailableCandidateAsLegAsync(AvailableLegCandidate candidate)
    {
        if (Position is null)
        {
            return;
        }

        var baseAsset = Position.Position.BaseAsset?.Trim();
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return;
        }

        LegModel leg = new LegModel
        {
            // Spot holdings become editable spot legs because wallet balances are not exchange position objects.
            IsReadOnly = candidate.Kind == AvailableLegSourceKind.Position || candidate.Kind == AvailableLegSourceKind.Order,
            IsIncluded = candidate.Kind != AvailableLegSourceKind.Order,
            Id = candidate.Kind == AvailableLegSourceKind.Order ? candidate.Id : Guid.NewGuid().ToString("N"),
            ReferenceId = candidate.Kind == AvailableLegSourceKind.Order
                ? ExtractReferenceIdFromCandidateId(candidate.Id)
                : candidate.Kind == AvailableLegSourceKind.Position
                    ? BuildPositionReferenceId(candidate.Symbol, candidate.Size)
                    : null,
            Type = candidate.Type,
            Status = candidate.Kind == AvailableLegSourceKind.Order
                ? LegStatus.Order
                : candidate.Kind == AvailableLegSourceKind.SpotPosition
                    ? LegStatus.Active
                    : LegStatus.Active,
            Strike = candidate.Strike,
            ExpirationDate = candidate.ExpirationDate,
            Size = candidate.Size,
            Price = candidate.Price,
            ImpliedVolatility = null,
            Symbol = candidate.Kind == AvailableLegSourceKind.SpotPosition ? null : candidate.Symbol
        };

        EnsureLegSymbol(leg);

        if (IsLegAlreadyInCollection(leg))
        {
            _notifyUserService.NotifyUser("This position/order is already in the collection.");
            return;
        }

        Collection.Legs.Add(leg);
        RemoveAvailableCandidate(candidate);
        if (candidate.Kind == AvailableLegSourceKind.Position && !string.IsNullOrWhiteSpace(leg.Symbol))
        {
            // Once active leg is added, same-symbol order chips should be represented as linked orders on that leg.
            RemoveOrderCandidatesForSymbol(leg.Symbol);
        }
        SyncLegViewModels();
        _ = UpdateLinkedOrdersForLegs();
        await RaiseUpdatedAsync(LegsCollectionUpdateKind.CollectionChanged);
        await RaiseUpdatedAsync(LegsCollectionUpdateKind.ViewModelDataUpdated);
    }

    public string FormatAvailableCandidate(AvailableLegCandidate candidate)
    {
        if (candidate.Kind == AvailableLegSourceKind.SpotPosition)
        {
            return $"Spot {candidate.Symbol} {Math.Abs(candidate.Size):0.##}";
        }

        var orderPrefix = candidate.Kind == AvailableLegSourceKind.Order && !string.IsNullOrWhiteSpace(candidate.OrderKind)
            ? $"{candidate.OrderKind} "
            : string.Empty;
        var direction = candidate.Size >= 0 ? "Buy" : "Sell";
        var absSize = Math.Abs(candidate.Size);
        var typeLabel = candidate.Type switch
        {
            LegType.Call => "CALL",
            LegType.Put => "PUT",
            _ => "FUT"
        };

        if (candidate.Type == LegType.Future)
        {
            return $"{orderPrefix}{direction} {absSize:0.##} {typeLabel} @ {FormatChipPrice(candidate.Price)}";
        }

        return $"{orderPrefix}{direction} {absSize:0.##} {typeLabel} {FormatChipStrike(candidate.Strike)} @ {FormatChipPrice(candidate.Price)}";
    }

    public IReadOnlyList<AvailableLegCandidate> GetVisibleAvailableLegCandidates(DateTime? expirationDate = null)
    {
        return _availableLegCandidates
            .Where(item => !expirationDate.HasValue || item.ExpirationDate?.Date == expirationDate.Value.Date)
            .Where(item => _showAvailablePositionChips || (item.Kind != AvailableLegSourceKind.Position && item.Kind != AvailableLegSourceKind.SpotPosition))
            .ToList();
    }

    private static string FormatChipStrike(decimal? strike)
    {
        return strike.HasValue
            ? strike.Value.ToString("0.##", CultureInfo.InvariantCulture)
            : "-";
    }

    private static string FormatChipPrice(decimal? price)
    {
        return price.HasValue
            ? price.Value.ToString("0.####", CultureInfo.InvariantCulture)
            : "mkt";
    }

    private bool IsLegAlreadyInCollection(LegModel candidate)
    {
        if (candidate.Status == LegStatus.Order && !string.IsNullOrWhiteSpace(candidate.Id))
        {
            return Collection.Legs.Any(leg =>
                leg.Status == LegStatus.Order
                && string.Equals(leg.Id, candidate.Id, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var leg in Collection.Legs)
        {
            if (BuildLegIdentity(leg) == BuildLegIdentity(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildLegIdentity(LegModel leg)
    {
        var symbol = string.IsNullOrWhiteSpace(leg.Symbol) ? string.Empty : leg.Symbol.Trim().ToUpperInvariant();
        var expiration = leg.ExpirationDate?.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? "-";
        var strike = leg.Strike.HasValue ? leg.Strike.Value.ToString("0.########", CultureInfo.InvariantCulture) : "-";
        var price = leg.Price.HasValue ? leg.Price.Value.ToString("0.########", CultureInfo.InvariantCulture) : "-";
        var size = Math.Abs(leg.Size).ToString("0.########", CultureInfo.InvariantCulture);
        var side = Math.Sign(leg.Size).ToString(CultureInfo.InvariantCulture);
        return $"{symbol}|{leg.Type}|{expiration}|{strike}|{side}|{size}|{price}|{(int)leg.Status}|{leg.IsIncluded}";
    }

    private void RemoveAvailableCandidate(AvailableLegCandidate candidate)
    {
        if (_availableLegCandidates.Count == 0)
        {
            return;
        }

        List<AvailableLegCandidate> updated;
        if (candidate.Kind == AvailableLegSourceKind.Position)
        {
            // Position chips are symbol-deduped, so remove by symbol.
            updated = _availableLegCandidates
                .Where(item => !(item.Kind == AvailableLegSourceKind.Position
                    && string.Equals(item.Symbol, candidate.Symbol, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        else
        {
            // Order chips are per-order entries, remove only the clicked one.
            updated = _availableLegCandidates.Where(item => item.Id != candidate.Id).ToList();
        }

        _availableLegCandidates = updated;
    }

    private void RemoveOrderCandidatesForSymbol(string symbol)
    {
        if (_availableLegCandidates.Count == 0 || string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        var normalized = symbol.Trim();
        _availableLegCandidates = _availableLegCandidates
            .Where(item =>
                item.Kind != AvailableLegSourceKind.Order
                || string.IsNullOrWhiteSpace(item.Symbol)
                || !string.Equals(item.Symbol.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void AddAvailableCandidateFromRemovedLeg(LegModel leg)
    {
        if (!leg.IsReadOnly || string.IsNullOrWhiteSpace(leg.Symbol))
        {
            return;
        }

        var symbol = leg.Symbol.Trim();
        var kind = leg.Status == LegStatus.Order ? AvailableLegSourceKind.Order : AvailableLegSourceKind.Position;
        var cachedSnapshot = Position?.GetCachedExchangeSnapshot();

        if (kind == AvailableLegSourceKind.Position && !IsPositionStillActiveInCache(leg, cachedSnapshot?.Positions))
        {
            return;
        }

        if (kind == AvailableLegSourceKind.Order && !IsOrderStillOpenInCache(leg, cachedSnapshot?.Orders))
        {
            return;
        }

        if (kind == AvailableLegSourceKind.Order && IsOrderLinkedToActiveLeg(symbol))
        {
            return;
        }

        if (kind == AvailableLegSourceKind.Position)
        {
            // Keep one position chip per symbol and avoid re-adding if a matching leg still exists.
            if (_availableLegCandidates.Any(item =>
                    item.Kind == AvailableLegSourceKind.Position
                    && string.Equals(item.Symbol, symbol, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (Collection.Legs.Any(item =>
                    item.IsReadOnly
                    && item.Status != LegStatus.Order
                    && string.Equals(item.Symbol, symbol, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var positionCandidate = new AvailableLegCandidate(
                Id: $"position:{symbol}:{Math.Sign(leg.Size)}",
                Kind: AvailableLegSourceKind.Position,
                OrderKind: null,
                Symbol: symbol,
                Type: leg.Type,
                Size: leg.Size,
                Price: leg.Price,
                ExpirationDate: leg.ExpirationDate,
                Strike: leg.Strike);

            _availableLegCandidates = _availableLegCandidates
                .Append(positionCandidate)
                .OrderBy(item => item.Kind)
                .ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return;
        }

        var orderId = string.IsNullOrWhiteSpace(leg.Id)
            ? BuildFallbackOrderReference(symbol, leg.Size, leg.Price)
            : leg.ReferenceId ?? ExtractReferenceIdFromCandidateId(leg.Id) ?? BuildFallbackOrderReference(symbol, leg.Size, leg.Price);
        if (!orderId.StartsWith("order:", StringComparison.OrdinalIgnoreCase))
        {
            orderId = $"order:{orderId}";
        }

        var orderCandidate = new AvailableLegCandidate(
            Id: orderId,
            Kind: AvailableLegSourceKind.Order,
            OrderKind: null,
            Symbol: symbol,
            Type: leg.Type,
            Size: leg.Size,
            Price: leg.Price,
            ExpirationDate: leg.ExpirationDate,
            Strike: leg.Strike);

        if (_availableLegCandidates.Any(item => item.Id == orderCandidate.Id))
        {
            return;
        }

        _availableLegCandidates = _availableLegCandidates
            .Append(orderCandidate)
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPositionStillActiveInCache(LegModel leg, IReadOnlyList<ExchangePosition>? positions)
    {
        if (positions is null || positions.Count == 0 || string.IsNullOrWhiteSpace(leg.Symbol))
        {
            return false;
        }

        var symbol = leg.Symbol.Trim();
        var legSide = Math.Sign(leg.Size);

        foreach (var position in positions)
        {
            if (string.IsNullOrWhiteSpace(position.Symbol))
            {
                continue;
            }

            if (!string.Equals(position.Symbol.Trim(), symbol, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var positionSide = Math.Sign(DetermineSignedSize(position));
            if (legSide == 0 || positionSide == 0 || positionSide == legSide)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOrderStillOpenInCache(LegModel leg, IReadOnlyList<ExchangeOrder>? orders)
    {
        if (orders is null || orders.Count == 0 || string.IsNullOrWhiteSpace(leg.Symbol))
        {
            return false;
        }

        var legReferenceId = ExtractReferenceIdFromCandidateId(leg.ReferenceId)
            ?? ExtractReferenceIdFromCandidateId(leg.Id);

        foreach (var order in orders)
        {
            var orderReferenceId = BuildOrderReferenceId(order);
            if (!string.IsNullOrWhiteSpace(legReferenceId)
                && string.Equals(orderReferenceId, legReferenceId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.Equals(order.Symbol?.Trim(), leg.Symbol.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Math.Sign(DetermineSignedSize(order)) != Math.Sign(leg.Size))
            {
                continue;
            }

            if (order.Qty != 0 && Math.Abs(Math.Abs(order.Qty) - Math.Abs(leg.Size)) > 0.0001m)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    internal LegModel? CreateLegFromExchangePosition(ExchangePosition position, string baseAsset, string category)
    {
        return _exchangeSnapshotLegSyncService.CreateLegFromExchangePosition(position, baseAsset, category, include: true);
    }

    internal LegModel? CreateLegFromExchangeOrder(ExchangeOrder order, string baseAsset)
    {
        return _exchangeSnapshotLegSyncService.CreateLegFromExchangeOrder(order, baseAsset, include: false);
    }

    private bool SyncOrderLegsWithSnapshot(
        IReadOnlyList<ExchangeOrder> orders,
        IReadOnlyList<ExchangePosition> positions,
        string baseAsset)
    {
        var changed = false;
        var openOrderLegs = orders
            .Select(order => CreateLegFromExchangeOrder(order, baseAsset))
            .Where(leg => leg is not null)
            .Cast<LegModel>()
            .ToArray();

        var openPositionLegs = positions
            .Select(position => CreateLegFromExchangePosition(position, baseAsset, position.Category))
            .Where(leg => leg is not null)
            .Cast<LegModel>()
            .ToArray();

        changed |= _exchangeSnapshotLegSyncService.SyncOrderLegs(
            Collection.Legs,
            openOrderLegs,
            openPositionLegs,
            onRemoved: RemoveLegViewModel);

        return changed;
    }

    private List<AvailableLegCandidate> BuildAvailableCandidates(
        IReadOnlyList<ExchangePosition> positions,
        IReadOnlyList<ExchangeOrder> orders,
        ExchangeWalletSnapshot? walletSnapshot,
        string baseAsset)
    {
        var candidates = new List<AvailableLegCandidate>();
        var knownPositionSymbols = new HashSet<string>(
            Collection.Legs
                .Where(leg => leg.Status != LegStatus.Order)
                .Select(leg => leg.Symbol)
                .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
                .Select(symbol => symbol!.Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var position in positions)
        {
            var leg = CreateLegFromExchangePosition(position, baseAsset, position.Category);
            if (leg is null || string.IsNullOrWhiteSpace(leg.Symbol))
            {
                continue;
            }

            var symbol = leg.Symbol.Trim();
            if (!knownPositionSymbols.Add(symbol))
            {
                continue;
            }

            candidates.Add(new AvailableLegCandidate(
                Id: $"position:{symbol}:{Math.Sign(leg.Size)}",
                Kind: AvailableLegSourceKind.Position,
                OrderKind: null,
                Symbol: symbol,
                Type: leg.Type,
                Size: leg.Size,
                Price: leg.Price,
                ExpirationDate: leg.ExpirationDate,
                Strike: leg.Strike));
        }

        foreach (var order in orders)
        {
            var leg = CreateLegFromExchangeOrder(order, baseAsset);
            if (leg is null || string.IsNullOrWhiteSpace(leg.Symbol))
            {
                continue;
            }

            // Keep order chips visible for new/manual legs; hide only when an active leg already links that symbol.
            if (Collection.Legs.Any(item =>
                    item.Status == LegStatus.Active
                    && !string.IsNullOrWhiteSpace(item.Symbol)
                    && string.Equals(item.Symbol.Trim(), leg.Symbol.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (IsOrderLinkedToActiveLeg(leg.Symbol))
            {
                continue;
            }

            if (Collection.Legs.Any(item => item.Status == LegStatus.Order && string.Equals(item.Id, leg.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            candidates.Add(new AvailableLegCandidate(
                Id: leg.Id,
                Kind: AvailableLegSourceKind.Order,
                OrderKind: ResolveOrderKindLabel(order),
                Symbol: leg.Symbol.Trim(),
                Type: leg.Type,
                Size: leg.Size,
                Price: leg.Price,
                ExpirationDate: leg.ExpirationDate,
                Strike: leg.Strike));
        }

        foreach (var spotCandidate in BuildSpotCandidates(walletSnapshot, baseAsset))
        {
            candidates.Add(spotCandidate);
        }

        return candidates
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<AvailableLegCandidate> BuildSpotCandidates(
        ExchangeWalletSnapshot? walletSnapshot,
        string baseAsset)
    {
        if (walletSnapshot?.Coins is null || string.IsNullOrWhiteSpace(baseAsset))
        {
            return Array.Empty<AvailableLegCandidate>();
        }

        var normalizedBaseAsset = baseAsset.Trim();
        return walletSnapshot.Coins
            .Where(coin => string.Equals(coin.Coin, normalizedBaseAsset, StringComparison.OrdinalIgnoreCase))
            .Select(coin => new
            {
                Coin = coin.Coin.Trim().ToUpperInvariant(),
                Size = coin.Equity ?? coin.WalletBalance ?? 0m
            })
            .Where(item => Math.Abs(item.Size) >= 0.0001m)
            .Select(item => new AvailableLegCandidate(
                Id: $"spot:{item.Coin}",
                Kind: AvailableLegSourceKind.SpotPosition,
                OrderKind: null,
                Symbol: item.Coin,
                Type: LegType.Spot,
                Size: item.Size,
                Price: null,
                ExpirationDate: null,
                Strike: null))
            .ToArray();
    }

    private bool IsOrderLinkedToActiveLeg(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var normalized = symbol.Trim();
        return _legs.Any(item =>
            item.Leg.Status == LegStatus.Active
            && !string.IsNullOrWhiteSpace(item.Leg.Symbol)
            && string.Equals(item.Leg.Symbol.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AreSameCandidates(IReadOnlyList<AvailableLegCandidate> left, IReadOnlyList<AvailableLegCandidate> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            var l = left[i];
            var r = right[i];
            if (!string.Equals(l.Id, r.Id, StringComparison.Ordinal)
                || l.Kind != r.Kind
                || !string.Equals(l.OrderKind, r.OrderKind, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(l.Symbol, r.Symbol, StringComparison.OrdinalIgnoreCase)
                || l.Type != r.Type
                || l.Size != r.Size
                || l.Price != r.Price
                || l.ExpirationDate != r.ExpirationDate
                || l.Strike != r.Strike)
            {
                return false;
            }
        }

        return true;
    }

    private static string? ResolveOrderKindLabel(ExchangeOrder order)
    {
        var stopOrderType = order.StopOrderType?.Trim();
        if (!string.IsNullOrWhiteSpace(stopOrderType))
        {
            if (stopOrderType.Contains("TakeProfit", StringComparison.OrdinalIgnoreCase))
            {
                return "TP";
            }

            if (stopOrderType.Contains("StopLoss", StringComparison.OrdinalIgnoreCase)
                || stopOrderType.Contains("Stop", StringComparison.OrdinalIgnoreCase))
            {
                return "SL";
            }
        }

        if (string.Equals(order.OrderStatus, "Untriggered", StringComparison.OrdinalIgnoreCase))
        {
            return "Conditional";
        }

        return null;
    }

  
    private static string BuildOrderReferenceId(ExchangeOrder order)
    {
        if (!string.IsNullOrWhiteSpace(order.OrderId))
        {
            return order.OrderId.Trim();
        }

        return BuildFallbackOrderReference(order.Symbol, DetermineSignedSize(order), order.Price);
    }

    private static string BuildPositionReferenceId(string? symbol, decimal size)
    {
        var normalizedSymbol = string.IsNullOrWhiteSpace(symbol) ? string.Empty : symbol.Trim().ToUpperInvariant();
        return $"position:{normalizedSymbol}:{Math.Sign(size)}";
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
            ? value.Substring("order:".Length)
            : value;
    }


   

    private static decimal DetermineSignedSize(ExchangePosition position)
    {
        var magnitude = Math.Abs(position.Size);
        if (magnitude < 0.0001m)
        {
            return 0;
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

    private static decimal DetermineSignedSize(ExchangeOrder order)
    {
        var magnitude = Math.Abs(order.Qty);
        if (magnitude < 0.0001m)
        {
            return 0m;
        }

        if (!string.IsNullOrWhiteSpace(order.Side))
        {
            var normalized = order.Side.Trim();
            if (string.Equals(normalized, "Sell", StringComparison.OrdinalIgnoreCase))
            {
                return -magnitude;
            }
        }

        return magnitude;
    }



    public async Task SetVisibilityAsync(bool isVisible)
    {
        Collection.IsVisible = isVisible;

        await RaiseUpdatedAsync(LegsCollectionUpdateKind.LegModelChanged);
    }

    public async Task SetColorAsync(string color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return;
        }

        Collection.Color = color;
        await RaiseUpdatedAsync(LegsCollectionUpdateKind.LegModelChanged);
    }

    public async Task SetNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Collection.Name = name.Trim();
        await RaiseUpdatedAsync(LegsCollectionUpdateKind.LegModelChanged);
    }

    public async Task RemoveLegAsync(LegModel leg)
    {
        if (!Collection.Legs.Contains(leg))
        {
            return;
        }

        await AddMissingLegToClosedPositionsIfNeededAsync(leg);
        Collection.Legs.Remove(leg);
        RemoveLegViewModel(leg);
        AddAvailableCandidateFromRemovedLeg(leg);
        RebuildLegGroups();
        await RaiseUpdatedAsync(LegsCollectionUpdateKind.CollectionChanged);
        await RaiseLegRemovedAsync(leg);
        await RaiseUpdatedAsync(LegsCollectionUpdateKind.ViewModelDataUpdated);
    }

    private async Task AddMissingLegToClosedPositionsIfNeededAsync(LegModel leg)
    {
        if (Position?.ClosedPositions is null
            || leg.Status != LegStatus.Missing
            || string.IsNullOrWhiteSpace(leg.Symbol))
        {
            return;
        }

        var symbol = leg.Symbol.Trim();
        var alreadyTracked = Position.ClosedPositions.ClosedPositions.Any(item =>
            string.Equals(item.Model.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (alreadyTracked)
        {
            return;
        }

        Position.ClosedPositions.SetAddSymbolInput(new[] { symbol });
        await Position.ClosedPositions.AddSymbolAsync();

        _notifyUserService.NotifyUser(
            $"Added {symbol.ToUpperInvariant()} to closed positions because the removed leg was missing on exchange.",
            visibleMilliseconds: 3000);
    }




    private Task RaiseUpdatedAsync(LegsCollectionUpdateKind updateKind)
    {
        return Updated?.Invoke(updateKind) ?? Task.CompletedTask;
    }

    private Task RaiseLegRemovedAsync(LegModel leg)
    {
        return LegRemoved?.Invoke(leg) ?? Task.CompletedTask;
    }

    private Task RaiseLegAddedAsync(LegModel leg)
    {
        return LegAdded?.Invoke(leg) ?? Task.CompletedTask;
    }


    private async Task HandleQuickAddLegCreated(LegModel leg)
    {
        SyncLegViewModels();
        await RaiseUpdatedAsync(LegsCollectionUpdateKind.CollectionChanged);
        await RaiseLegAddedAsync(leg);
    }

    private void SyncQuickAddPrice()
    {
        QuickAdd.Price = CurrentPrice;
        QuickAdd.BaseAsset = Position?.Position.BaseAsset;
    }

    public void Dispose()
    {
        CancelDataUpdateDebounce();
        QuickAdd.LegCreated -= HandleQuickAddLegCreated;
        foreach (var viewModel in _legViewModels.Values)
        {
            DetachLegViewModel(viewModel);
            viewModel.Dispose();
        }
        _legViewModels.Clear();
        _legs.Clear();
    }

    private void RemoveLegViewModel(LegModel leg)
    {
        var key = leg.Id ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            key = leg.GetHashCode().ToString();
        }

        if (_legViewModels.TryGetValue(key, out var viewModel))
        {
            DetachLegViewModel(viewModel);
            viewModel.Dispose();
            _legViewModels.Remove(key);
            _legs.Remove(viewModel);
        }
    }

    private void SyncLegViewModels()
    {
        var ordered = new List<LegViewModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var leg in Collection.Legs)
        {
            EnsureLegSymbol(leg);
            var key = leg.Id ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                key = leg.GetHashCode().ToString();
            }

            if (!_legViewModels.TryGetValue(key, out var viewModel))
            {
                viewModel = _legViewModelFactory.Create(this, leg, _exchangeService);
                _legViewModels[key] = viewModel;
                AttachLegViewModel(viewModel);
            }
            else
            {
                viewModel.Leg = leg;
            }

            viewModel.IndexPrice = _currentPrice;
            viewModel.IsLive = _isLive;
            viewModel.ValuationDate = _valuationDate;
            viewModel.UpdateLinkedOrders(_latestOrdersSnapshot);
            ordered.Add(viewModel);
            seen.Add(key);
        }

        var toRemove = _legViewModels.Keys
            .Where(key => !seen.Contains(key))
            .ToList();
        foreach (var key in toRemove)
        {
            var viewModel = _legViewModels[key];
            DetachLegViewModel(viewModel);
            viewModel.Dispose();
            _legViewModels.Remove(key);
        }

        _legs.Clear();
        foreach (var viewModel in ordered)
        {
            _legs.Add(viewModel);
        }

        RebuildLegGroups();
        SchedulePricingUpdate();
    }

    private bool UpdateLinkedOrdersForLegs()
    {
        var changed = false;
        foreach (var legViewModel in _legs)
        {
            changed |= legViewModel.UpdateLinkedOrders(_latestOrdersSnapshot);
        }

        return changed;
    }

    private void AttachLegViewModel(LegViewModel viewModel)
    {
        viewModel.Changed += HandleLegViewModelChanged;
        viewModel.Removed += HandleLegViewModelRemoved;
    }

    private void DetachLegViewModel(LegViewModel viewModel)
    {
        viewModel.Changed -= HandleLegViewModelChanged;
        viewModel.Removed -= HandleLegViewModelRemoved;
    }

    private Task HandleLegViewModelRemoved()
    {
        return Task.CompletedTask;
    }

    private void HandleLegViewModelChanged(LegsCollectionUpdateKind updateKind)
    {
        if (updateKind == LegsCollectionUpdateKind.PricingContextUpdated
            || updateKind == LegsCollectionUpdateKind.ViewModelDataUpdated)
        {
            if (updateKind == LegsCollectionUpdateKind.ViewModelDataUpdated)
            {
                SchedulePricingUpdate();
            }

            QueueDataUpdate(updateKind);
            return;
        }

        if (updateKind == LegsCollectionUpdateKind.LegModelChanged || updateKind == LegsCollectionUpdateKind.CollectionChanged)
        {
            RebuildLegGroups();
        }

        _ = RaiseUpdatedAsync(updateKind);
    }

    private void QueueDataUpdate(LegsCollectionUpdateKind updateKind)
    {
        CancellationToken token;
        lock (_dataUpdateLock)
        {
            _dataUpdateCts?.Cancel();
            _dataUpdateCts?.Dispose();
            _dataUpdateCts = new CancellationTokenSource();
            token = _dataUpdateCts.Token;
        }

        _ = RunDataUpdateDebounceAsync(updateKind, token);
    }

    private async Task RunDataUpdateDebounceAsync(LegsCollectionUpdateKind updateKind, CancellationToken token)
    {
        try
        {
            await Task.Delay(DataUpdateDebounce, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        await RaiseUpdatedAsync(updateKind);
    }

    private void CancelDataUpdateDebounce()
    {
        lock (_dataUpdateLock)
        {
            if (_dataUpdateCts is null)
            {
                return;
            }

            _dataUpdateCts.Cancel();
            _dataUpdateCts.Dispose();
            _dataUpdateCts = null;
        }
    }

    private void RebuildLegGroups()
    {
        if (_legs.Count == 0)
        {
            _groupedLegs = Array.Empty<LegGroup>();
            return;
        }

        // Cache the grouping to keep Razor rendering lightweight during frequent updates.
        var grouped = _legs
            .OrderBy(leg => leg.Leg.ExpirationDate ?? DateTime.MaxValue)
            .GroupBy(leg => leg.Leg.ExpirationDate?.Date)
            .Select(group =>
            {
                var legs = group
                    .OrderBy(leg => ResolveLegStatusOrder(leg.Leg.Status))
                    .ThenBy(leg => leg.Leg.Strike ?? decimal.MaxValue)
                    .ThenBy(leg => leg.Leg.Symbol ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(leg => leg.Leg.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new LegGroup(group.Key, legs);
            })
            .ToList();

        _groupedLegs = grouped;
    }

    private static int ResolveLegStatusOrder(LegStatus status)
    {
        return status switch
        {
            LegStatus.Active => 0,
            LegStatus.Order => 1,
            LegStatus.New => 2,
            LegStatus.Missing => 3,
            _ => 4
        };
    }

    private void EnsureLegSymbol(LegModel leg)
    {
        if (leg is null || !string.IsNullOrWhiteSpace(leg.Symbol))
        {
            return;
        }

        leg.Symbol = _exchangeService.FormatSymbol(leg, Position?.Position.BaseAsset, Position?.Position.QuoteAsset);

    }

    public async Task RefreshExchangeMissingFlagsAsync()
    {
        var positions = (await _exchangeService.Positions.GetPositionsAsync()).ToList();
        await UpdateExchangeMissingFlagsAsync(positions);
    }

    public async Task UpdateExchangeMissingFlagsAsync(IReadOnlyList<ExchangePosition> positions)
    {
        var hasChanges = false;
        var baseAsset = Position?.Position.BaseAsset?.Trim();
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return;
        }

        var activeLegs = positions
            .Select(position => CreateLegFromExchangePosition(position, baseAsset, position.Category))
            .Where(leg => leg is not null)
            .Cast<LegModel>()
            .ToArray();

        var before = Collection.Legs
            .Select(leg => $"{leg.Status}|{leg.IsIncluded}|{leg.Symbol}|{leg.Size}|{leg.Price}")
            .ToArray();
        _exchangeSnapshotLegSyncService.SyncReadOnlyLegs(Collection.Legs, activeLegs);
        var after = Collection.Legs
            .Select(leg => $"{leg.Status}|{leg.IsIncluded}|{leg.Symbol}|{leg.Size}|{leg.Price}")
            .ToArray();
        hasChanges = !before.SequenceEqual(after, StringComparer.Ordinal);

        foreach (var legViewModel in _legs)
        {
            var leg = legViewModel.Leg;
            if (leg.Status == LegStatus.Order || !leg.IsReadOnly)
            {
                continue;
            }

            hasChanges |= legViewModel.SetLegStatus(leg.Status, leg.Status == LegStatus.Missing ? "Exchange position not found for this leg." : null);
        }

        if (hasChanges)
        {
            await RaiseUpdatedAsync(LegsCollectionUpdateKind.ViewModelDataUpdated);
        }
    }

    private decimal SumDelta()
    {
        decimal total = 0;
        foreach (var leg in _legs)
        {
            if (!leg.Leg.IsIncluded)
            {
                continue;
            }

            if (leg.Leg.Type is LegType.Future or LegType.Spot)
            {
                total += leg.Leg.Size;
                continue;
            }

            var value = leg.Delta;
            if (value.HasValue)
            {
                total += value.Value * leg.Leg.Size;
            }
        }

        return total;
    }

    private decimal SumGreek(Func<LegViewModel, decimal?> selector)
    {
        decimal total = 0;
        foreach (var leg in _legs)
        {
            if (!leg.Leg.IsIncluded || leg.Leg.Type is LegType.Future or LegType.Spot)
            {
                continue;
            }

            var value = selector(leg);
            if (value.HasValue)
            {
                total += value.Value * leg.Leg.Size;
            }
        }

        return total;
    }

    private decimal? SumTempPnl()
    {
        decimal total = 0;
        var hasValue = false;
        foreach (var leg in _legs)
        {
            if (!leg.Leg.IsIncluded)
            {
                continue;
            }

            var value = leg.PnL;
            if (!value.HasValue)
            {
                continue;
            }

            total += value.Value;
            hasValue = true;
        }

        return hasValue ? total : null;
    }

    private PortfolioGreekSummary BuildGreekSummary()
    {
        decimal totalDelta = 0m;
        decimal totalGamma = 0m;
        decimal totalVega = 0m;
        decimal totalTheta = 0m;
        decimal grossSize = 0m;

        foreach (var leg in _legs)
        {
            if (!leg.Leg.IsIncluded)
            {
                continue;
            }

            grossSize += Math.Abs(leg.Leg.Size);

            if (leg.Leg.Type is LegType.Future or LegType.Spot)
            {
                totalDelta += leg.Leg.Size;
                continue;
            }

            if (leg.Delta.HasValue)
            {
                totalDelta += leg.Delta.Value * leg.Leg.Size;
            }

            if (leg.Gamma.HasValue)
            {
                totalGamma += leg.Gamma.Value * leg.Leg.Size;
            }

            if (leg.Vega.HasValue)
            {
                totalVega += leg.Vega.Value * leg.Leg.Size;
            }

            if (leg.Theta.HasValue)
            {
                totalTheta += leg.Theta.Value * leg.Leg.Size;
            }
        }

        var perDelta = grossSize > 0m ? totalDelta / grossSize : 0m;
        var perGamma = grossSize > 0m ? totalGamma / grossSize : 0m;
        var perVega = grossSize > 0m ? totalVega / grossSize : 0m;
        var perTheta = grossSize > 0m ? totalTheta / grossSize : 0m;

        return new PortfolioGreekSummary(
            totalDelta,
            totalGamma,
            totalVega,
            totalTheta,
            grossSize,
            perDelta,
            perGamma,
            perVega,
            perTheta);
    }
}

public sealed record PortfolioGreekSummary(
    decimal TotalDelta,
    decimal TotalGamma,
    decimal TotalVega,
    decimal TotalTheta,
    decimal GrossSize,
    decimal DeltaPerOneSize,
    decimal GammaPerOneSize,
    decimal VegaPerOneSize,
    decimal ThetaPerOneSize);
