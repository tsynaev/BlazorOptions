using BlazorOptions.Services;
using Microsoft.AspNetCore.Components.Web;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BlazorOptions.ViewModels;

public sealed class LegsCollectionViewModel : IDisposable
{
    private readonly ILegsCollectionDialogService _dialogService;
    private readonly OptionsChainService _optionsChainService;
    private readonly LegViewModelFactory _legViewModelFactory;
    private readonly INotifyUserService _notifyUserService;
    private readonly ITelemetryService _telemetryService;
    private string _baseAsset;
    private decimal? _currentPrice;
    private bool _isLive;
    private DateTime _valuationDate;
    private LegsCollectionModel _collection = default!;
    private readonly Dictionary<string, LegViewModel> _legViewModels = new(StringComparer.Ordinal);
    private readonly ObservableCollection<LegViewModel> _legs = new();
    private IExchangeService _exchangeService;


    private static readonly string[] PositionExpirationFormats = { "ddMMMyy", "ddMMMyyyy" };

    public LegsCollectionViewModel(
        ILegsCollectionDialogService dialogService,
        OptionsChainService optionsChainService,
        LegViewModelFactory legViewModelFactory,
        INotifyUserService notifyUserService,
        ITelemetryService telemetryService, IExchangeService exchangeService)
    {
        _dialogService = dialogService;
        _optionsChainService = optionsChainService;
        _legViewModelFactory = legViewModelFactory;
        _notifyUserService = notifyUserService;
        _telemetryService = telemetryService;
        _exchangeService = exchangeService;

        QuickAdd = new QuickAddViewModel(_notifyUserService, optionsChainService, telemetryService, exchangeService);
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
            foreach (var leg in _legs)
            {
                leg.CurrentPrice = value;
            }

            QuickAdd.Price = value;
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
            foreach (var leg in _legs)
            {
                leg.IsLive = value;
            }
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
            foreach (var leg in _legs)
            {
                leg.ValuationDate = value;
            }
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

    public QuickAddViewModel QuickAdd { get; }

    public event Func<LegModel, Task>? LegAdded;
    public event Func<LegModel, Task>? LegRemoved;
    public event Func<Task>? Updated;

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

    public async Task AddLegAsync()
    {
        using var activity = _telemetryService.StartActivity("LegsCollection.AddLeg");
        if (Position is null)
        {
            return;
        }

        var legs = await _dialogService.ShowOptionChainDialogAsync(
            Position.Position,
            Collection,
            CurrentPrice);
        if (legs is null)
        {
            return;
        }

        await UpdateLegsAsync(Collection, legs);
        SyncLegViewModels();
    }

    public async Task UpdateLegsAsync(LegsCollectionModel collection, IEnumerable<LegModel> legs)
    {
        collection.Legs.Clear();
        foreach (var leg in legs)
        {
            EnsureLegSymbol(leg);
            collection.Legs.Add(leg);
        }

        await RaiseUpdatedAsync();
    }

    public async Task AddQuickLegAsync()
    {
        using var activity = _telemetryService.StartActivity("LegsCollection.AddQuickLeg");
        SyncQuickAddPrice();
        await QuickAdd.AddQuickLegAsync();
    }

    public Task OnQuickLegKeyDown(KeyboardEventArgs args)
    {
        SyncQuickAddPrice();
        return QuickAdd.OnQuickLegKeyDown(args);
    }

    public async Task DuplicateCollectionAsync()
    {
        await Position.DuplicateCollectionAsync(Collection);
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

    public async Task LoadBybitPositionsAsync()
    {
        if (Position is null)
        {
            return;
        }

        var positions = await _dialogService.ShowBybitPositionsDialogAsync(
            Position.Position.BaseAsset,
            Position.Position.QuoteAsset,
            Collection.Legs.ToList());
        if (positions is null)
        {
            return;
        }

        await AddBybitPositionsToCollectionAsync(positions);
    }

    public async Task AddBybitPositionsToCollectionAsync(IReadOnlyList<BybitPosition> positions)
    {
        if (positions.Count == 0)
        {
            return;
        }

        var baseAsset = Position.Position.BaseAsset?.Trim();
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            _notifyUserService.NotifyUser("Specify a base asset before loading Bybit positions.");
            return;
        }

        var added = 0;

        foreach (var bybitPosition in positions)
        {
            if (Math.Abs(bybitPosition.Size) < 0.0001m)
            {
                continue;
            }

            var leg = CreateLegFromBybitPosition(bybitPosition, baseAsset, bybitPosition.Category);
            if (leg is null)
            {
                continue;
            }

            EnsureLegSymbol(leg);
            var existing = PositionBuilderViewModel.FindMatchingLeg(Collection.Legs, leg);
            if (existing is null)
            {
                Collection.Legs.Add(leg);
                added++;
            }
            else
            {
                existing.Price = leg.Price;
                existing.Size = leg.Size;
            }
        }

        if (added == 0)
        {
            return;
        }

        SyncLegViewModels();
        await RaiseUpdatedAsync();
    }

    internal LegModel? CreateLegFromBybitPosition(BybitPosition position, string baseAsset, string category)
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

        var price = position.AvgPrice;

        return new LegModel
        {
            IsReadOnly = true,
            Type = type,
            Strike = strike,
            ExpirationDate = expiration,
            Size = size,
            Price = price,
            ImpliedVolatility = null,
            Symbol = position.Symbol
        };
    }


    private static bool TryParseFutureExpiration(string symbol, out DateTime expiration)
    {
        expiration = default;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var tokens = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length >= 2 &&
            DateTime.TryParseExact(tokens[1], PositionExpirationFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
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

    private static decimal DetermineSignedSize(BybitPosition position)
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



    public async Task SetVisibilityAsync(bool isVisible)
    {
        Collection.IsVisible = isVisible;

        await RaiseUpdatedAsync();
    }

    public async Task SetColorAsync(string color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return;
        }

        Collection.Color = color;
        await RaiseUpdatedAsync();
    }

    public async Task SetNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Collection.Name = name.Trim();
        await RaiseUpdatedAsync();
    }

    public async Task OpenSettingsAsync()
    {
        await _dialogService.ShowPortfolioSettingsAsync(Collection.Id);
    }

    public async Task RemoveLegAsync(LegModel leg)
    {
        if (!Collection.Legs.Contains(leg))
        {
            return;
        }

        Collection.Legs.Remove(leg);
        RemoveLegViewModel(leg);
        await RaiseUpdatedAsync();
        await RaiseLegRemovedAsync(leg);
    }




    private Task RaiseUpdatedAsync()
    {
        return Updated?.Invoke() ?? Task.CompletedTask;
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
        await RaiseUpdatedAsync();
        await RaiseLegAddedAsync(leg);
    }

    private void SyncQuickAddPrice()
    {
        QuickAdd.Price = CurrentPrice;
        QuickAdd.BaseAsset = Position?.Position.BaseAsset;
    }

    public void Dispose()
    {
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
                viewModel = _legViewModelFactory.Create(this, leg);
                _legViewModels[key] = viewModel;
                AttachLegViewModel(viewModel);
            }
            else
            {
                viewModel.UpdateLeg(leg);
            }

            viewModel.CurrentPrice = _currentPrice;
            viewModel.IsLive = _isLive;
            viewModel.ValuationDate = _valuationDate;
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

    private void HandleLegViewModelChanged()
    {
    }

    private void EnsureLegSymbol(LegModel leg)
    {
        if (leg is null || !string.IsNullOrWhiteSpace(leg.Symbol))
        {
            return;
        }

        leg.Symbol = _exchangeService.FormatSymbol(leg, Position?.Position.BaseAsset, Position?.Position.QuoteAsset);

    }
}












