using System.Collections.ObjectModel;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class OptionChainDialogViewModel : IDisposable
{
    private const string StrikeWindowKey = "optionChainDialog:strikeWindow";
    private readonly IExchangeService _exchangeService;
    private readonly ILocalStorageService _localStorageService;
    private PositionModel? _position;
    private List<OptionChainTicker> _chainTickers = new();
    private List<OptionChainTicker> _filteredTickers = new();
    private readonly Dictionary<(decimal Strike, LegType Type), OptionChainTicker> _strikeLookup = new();
    private string? _baseAsset;
    private DateTime? _selectedExpiration;
    private decimal? _atmStrike;
    private decimal? _underlyingPrice;
    private readonly HashSet<string> _ivModeLegIds = new();
    private readonly Dictionary<string, IDisposable> _tickerSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private int _strikeWindowSize = 10;

    public OptionChainDialogViewModel(IExchangeService exchangeService, ILocalStorageService localStorageService)
    {
        _exchangeService = exchangeService;
        _localStorageService = localStorageService;
    }

    public ObservableCollection<LegModel> Legs { get; } = new();

    public IReadOnlyList<DateTime> AvailableExpirations { get; private set; } = [];

    public IReadOnlyList<decimal> AvailableStrikes { get; private set; } = [];

    public IReadOnlyList<decimal> DisplayStrikes { get; private set; } = [];

    public int StrikeWindowSize => _strikeWindowSize;

    public DateTime? SelectedExpiration
    {
        get => _selectedExpiration;
        private set => _selectedExpiration = value;
    }

    public bool IsRefreshing { get; private set; }

    public event Action? OnChange;

    public async Task InitializeAsync(PositionModel? position, LegsCollectionModel? collection, decimal? underlyingPrice)
    {
        _position = position;
        _baseAsset = position?.BaseAsset;
        _underlyingPrice = underlyingPrice;
        Legs.Clear();

        await LoadStrikeWindowAsync();

        var sourceLegs = collection?.Legs
            ?? position?.Collections.FirstOrDefault()?.Legs
            ?? Enumerable.Empty<LegModel>();

        foreach (var leg in sourceLegs)
        {
            Legs.Add(CloneLeg(leg));
        }

        _chainTickers = GetBaseAssetTickers().ToList();
        UpdateFilteredTickers();
        UpdateExpirations();

        if (Legs.Count > 0)
        {
            var legExpirations = Legs
                .Select(leg => leg.ExpirationDate?.Date)
                .Where(date => date.HasValue)
                .Select(date => date!.Value)
                .ToList();

            if (legExpirations.Count > 0)
            {
                SelectedExpiration = legExpirations.Max();
            }
            else if (AvailableExpirations.Count > 0)
            {
                SelectedExpiration ??= AvailableExpirations.First();
            }
            else
            {
                SelectedExpiration = null;
            }
        }
        else if (AvailableExpirations.Count > 0)
        {
            SelectedExpiration ??= AvailableExpirations.First();
        }

        UpdateStrikes();
        OnChange?.Invoke();

        _ = RefreshChainAsync();
    }

    public void SetSelectedExpiration(DateTime? expiration)
    {
        SelectedExpiration = expiration;
        UpdateStrikes();
        OnChange?.Invoke();
    }

    public async Task SetStrikeWindowSizeAsync(int value)
    {
        var newValue = value;
        if (newValue < 1)
        {
            newValue = 1;
        }

        if (newValue == _strikeWindowSize)
        {
            return;
        }

        _strikeWindowSize = newValue;
        await _localStorageService.SetItemAsync(StrikeWindowKey, newValue.ToString());
        UpdateStrikes();
        OnChange?.Invoke();
    }

    public OptionChainTicker? GetTickerForLeg(LegModel leg)
    {
        return _exchangeService.OptionsChain.FindTickerForLeg(leg, _baseAsset);
    }

    public bool IsLegIvMode(LegModel leg) => _ivModeLegIds.Contains(leg.Id);

    public void ToggleLegInputMode(LegModel leg)
    {
        if (!_ivModeLegIds.Add(leg.Id))
        {
            _ivModeLegIds.Remove(leg.Id);
        }

        OnChange?.Invoke();
    }

    public decimal GetLegInputValue(LegModel leg)
    {
        return IsLegIvMode(leg) ? (leg.ImpliedVolatility ?? 0m) : (leg.Price ?? 0m);
    }

    public void SetLegInputValue(LegModel leg, decimal value)
    {
        if (IsLegIvMode(leg))
        {
            leg.ImpliedVolatility = value;
        }
        else
        {
            leg.Price = value;
        }

        OnChange?.Invoke();
    }

    public void SetLegSize(LegModel leg, decimal size)
    {
        leg.Size = size;
        OnChange?.Invoke();
    }

    public void AdjustLegSize(LegModel leg, decimal delta)
    {
        leg.Size += delta;
        OnChange?.Invoke();
    }

    public string GetContractSymbol(LegModel leg)
    {
        return GetTickerForLeg(leg)?.Symbol ?? string.Empty;
    }

    public string GetContractDescription(LegModel leg)
    {
        var typeLabel = leg.Type switch
        {
            LegType.Call => "CALL",
            LegType.Put => "PUT",
            _ => "FUT"
        };

        var strikeLabel = leg.Strike.HasValue ? leg.Strike.Value.ToString("0") : "-";
        var expirationText = leg.ExpirationDate.HasValue ? leg.ExpirationDate.Value.ToString("dd.MM") : "-";
        var dte = leg.ExpirationDate.HasValue
            ? (leg.ExpirationDate.Value.Date - DateTime.UtcNow.Date).TotalDays
            : (double?)null;

        var dteLabel = dte.HasValue ? dte.Value.ToString("0.0") : "-";
        return $"({typeLabel} {strikeLabel}, Exp: {expirationText}, DTE: {dteLabel})";
    }

    public void AddLeg(decimal strike, LegType type)
    {
        if (!_selectedExpiration.HasValue)
        {
            return;
        }

        var ticker = _chainTickers.FirstOrDefault(item =>
            string.Equals(item.BaseAsset, _baseAsset, StringComparison.OrdinalIgnoreCase)
            && item.ExpirationDate.Date == _selectedExpiration.Value.Date
            && item.Type == type
            && Math.Abs(item.Strike - strike) < 0.01m);

        var leg = new LegModel
        {
            Type = type,
            Strike = strike,
            ExpirationDate = _selectedExpiration.Value.Date,
            Size = 1,
            Price = ticker?.MarkPrice ?? 0,
            ImpliedVolatility = NormalizeIv(ticker?.MarkIv) ?? 0
        };

        Legs.Add(leg);
        OnChange?.Invoke();
    }

    public void AddLegFromQuote(decimal strike, LegType type, decimal price, decimal iv)
    {
        if (!_selectedExpiration.HasValue)
        {
            return;
        }

        var ticker = _chainTickers.FirstOrDefault(item =>
            string.Equals(item.BaseAsset, _baseAsset, StringComparison.OrdinalIgnoreCase)
            && item.ExpirationDate.Date == _selectedExpiration.Value.Date
            && item.Type == type
            && Math.Abs(item.Strike - strike) < 0.01m);

        var leg = new LegModel
        {
            Type = type,
            Strike = strike,
            ExpirationDate = _selectedExpiration.Value.Date,
            Size = 1,
            Price = price > 0 ? price : ticker?.MarkPrice ?? 0,
            ImpliedVolatility = iv > 0 ? iv : NormalizeIv(ticker?.MarkIv) ?? 0
        };

        Legs.Add(leg);
        OnChange?.Invoke();
    }

    public void RemoveLeg(LegModel leg)
    {
        if (Legs.Contains(leg))
        {
            Legs.Remove(leg);
            OnChange?.Invoke();
        }
    }

    private async Task RefreshChainAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        OnChange?.Invoke();

        await _exchangeService.OptionsChain.RefreshAsync(_baseAsset);
        _chainTickers = GetBaseAssetTickers();
        UpdateFilteredTickers();
        UpdateExpirations();
        UpdateStrikes();
        IsRefreshing = false;
        OnChange?.Invoke();
    }

    private void UpdateExpirations()
    {
        var expirations = _filteredTickers
            .Select(ticker => ticker.ExpirationDate.Date)
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        AvailableExpirations = expirations;

        if (AvailableExpirations.Count == 0)
        {
            SelectedExpiration = null;
        }
        else if (_selectedExpiration.HasValue && !AvailableExpirations.Contains(_selectedExpiration.Value.Date))
        {
            SelectedExpiration = AvailableExpirations.FirstOrDefault();
        }
        else if (!_selectedExpiration.HasValue && AvailableExpirations.Count > 0)
        {
            SelectedExpiration = AvailableExpirations.First();
        }
    }

    private void UpdateStrikes()
    {
        if (!_selectedExpiration.HasValue)
        {
            AvailableStrikes = Array.Empty<decimal>();
            _atmStrike = null;
            return;
        }

        var relevantTickers = _filteredTickers
            .Where(ticker => ticker.ExpirationDate.Date == _selectedExpiration.Value.Date)
            .ToList();

        var strikes = relevantTickers
            .Select(ticker => ticker.Strike)
            .Distinct()
            .OrderBy(strike => strike)
            .ToList();

        AvailableStrikes = strikes;
        _atmStrike = DetermineAtmStrike(relevantTickers, strikes, _underlyingPrice);
        DisplayStrikes = BuildStrikeWindow(strikes, _atmStrike, _strikeWindowSize);
        UpdateStrikeLookup();
        _ = UpdateTickerSubscriptionsAsync();
    }

    private void UpdateFilteredTickers()
    {
        if (string.IsNullOrWhiteSpace(_baseAsset))
        {
            _filteredTickers = _chainTickers;
            UpdateStrikeLookup();
            return;
        }

        var filtered = _chainTickers
            .Where(ticker => string.Equals(ticker.BaseAsset, _baseAsset, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _filteredTickers = filtered.Count > 0 ? filtered : _chainTickers;
        UpdateStrikeLookup();
    }

    public bool IsAtmStrike(decimal strike)
    {
        if (!_atmStrike.HasValue)
        {
            return false;
        }

        return Math.Abs(strike - _atmStrike.Value) < 0.01m;
    }

    public decimal? GetStrikeImpliedVolatility(decimal strike)
    {
        if (!_selectedExpiration.HasValue)
        {
            return null;
        }

        var expiration = _selectedExpiration.Value.Date;
        decimal? callIv = null;
        decimal? putIv = null;
        foreach (var ticker in _filteredTickers)
        {
            if (ticker.ExpirationDate.Date != expiration)
            {
                continue;
            }

            if (Math.Abs(ticker.Strike - strike) >= 0.01m)
            {
                continue;
            }

            if (ticker.Type == LegType.Call)
            {
                callIv ??= ticker.MarkIv;
            }
            else if (ticker.Type == LegType.Put)
            {
                putIv ??= ticker.MarkIv;
            }

            if (callIv.HasValue && putIv.HasValue)
            {
                break;
            }
        }

        if (!callIv.HasValue && !putIv.HasValue)
        {
            return null;
        }

        var normalizedCall = NormalizeIv(callIv);
        if (normalizedCall.HasValue && normalizedCall.Value > 0)
        {
            return normalizedCall.Value;
        }

        var normalizedPut = NormalizeIv(putIv);
        return normalizedPut.HasValue && normalizedPut.Value > 0 ? normalizedPut.Value : null;
    }

    public OptionChainTicker? GetStrikeTicker(decimal strike, LegType type)
    {
        if (!_selectedExpiration.HasValue)
        {
            return null;
        }

        var key = (NormalizeStrike(strike), type);
        return _strikeLookup.TryGetValue(key, out var ticker) ? ticker : null;
    }

    public decimal GetTotalPremium()
    {
        return Legs.Sum(leg => (leg.Price ?? 0) * leg.Size);
    }


    public decimal GetTotalGreek(Func<OptionChainTicker, decimal?> selector)
    {
        return Legs.Sum(leg =>
        {
            var ticker = GetTickerForLeg(leg);
            var greek = ticker is null ? 0 : selector(ticker) ?? 0;
            return greek * leg.Size;
        });
    }

    public decimal GetLegGreekValue(LegModel leg, Func<OptionChainTicker, decimal?> selector)
    {
        var ticker = GetTickerForLeg(leg);
        return ticker is null ? 0 : selector(ticker) ?? 0;
    }

    private static decimal? DetermineAtmStrike(List<OptionChainTicker> tickers, List<decimal> strikes, decimal? underlyingPrice)
    {
        if (underlyingPrice.HasValue && strikes.Count > 0)
        {
            return strikes
                .OrderBy(strike => Math.Abs(strike - underlyingPrice.Value))
                .First();
        }

        if (tickers.Count == 0)
        {
            return null;
        }

        var callByDelta = tickers
            .Where(ticker => ticker.Type == LegType.Call && ticker.Delta.HasValue)
            .OrderBy(ticker => Math.Abs(ticker.Delta!.Value - 0.5m))
            .FirstOrDefault();

        if (callByDelta is not null)
        {
            return callByDelta.Strike;
        }

        if (strikes.Count == 0)
        {
            return null;
        }

        return strikes[strikes.Count / 2];
    }

    private static LegModel CloneLeg(LegModel leg)
    {
        return new LegModel
        {
            Id = leg.Id,
            IsIncluded = leg.IsIncluded,
            IsReadOnly = leg.IsReadOnly,
            Type = leg.Type,
            Strike = leg.Strike,
            ExpirationDate = leg.ExpirationDate,
            Size = leg.Size,
            Price = leg.Price,
            ImpliedVolatility = leg.ImpliedVolatility
        };
    }

    private List<OptionChainTicker> GetBaseAssetTickers()
    {
        if (string.IsNullOrWhiteSpace(_baseAsset))
        {
            return new List<OptionChainTicker>();
        }

        return _exchangeService.OptionsChain.GetTickersByBaseAsset(_baseAsset);
    }

    public void Dispose()
    {
        foreach (var subscription in _tickerSubscriptions.Values)
        {
            subscription.Dispose();
        }

        _tickerSubscriptions.Clear();
    }

    private async Task LoadStrikeWindowAsync()
    {
        var stored = await _localStorageService.GetItemAsync(StrikeWindowKey);
        if (int.TryParse(stored, out var parsed) && parsed > 0)
        {
            _strikeWindowSize = parsed;
        }
    }

    private static IReadOnlyList<decimal> BuildStrikeWindow(IReadOnlyList<decimal> strikes, decimal? atmStrike, int windowSize)
    {
        if (strikes.Count == 0 || windowSize <= 0 || !atmStrike.HasValue)
        {
            return strikes;
        }

        var atmIndex = strikes
            .Select((strike, index) => (strike, index))
            .OrderBy(item => Math.Abs(item.strike - atmStrike.Value))
            .First()
            .index;

        var start = Math.Max(0, atmIndex - windowSize);
        var end = Math.Min(strikes.Count - 1, atmIndex + windowSize);

        var window = new List<decimal>();
        for (var i = start; i <= end; i++)
        {
            window.Add(strikes[i]);
        }

        return window;
    }

    private void UpdateStrikeLookup()
    {
        // Cache strike lookups to avoid repeated scans during Razor rendering.
        _strikeLookup.Clear();
        if (!_selectedExpiration.HasValue || _filteredTickers.Count == 0)
        {
            return;
        }

        var expiration = _selectedExpiration.Value.Date;
        foreach (var ticker in _filteredTickers)
        {
            if (ticker.ExpirationDate.Date != expiration)
            {
                continue;
            }

            var key = (NormalizeStrike(ticker.Strike), ticker.Type);
            if (!_strikeLookup.ContainsKey(key))
            {
                _strikeLookup.Add(key, ticker);
            }
        }
    }

    private static decimal? NormalizeIv(decimal? value)
    {
        if (!value.HasValue || value.Value <= 0)
        {
            return null;
        }

        return value.Value <= 3 ? value.Value * 100 : value.Value;
    }

    private static decimal NormalizeStrike(decimal strike)
    {
        return Math.Round(strike, 2, MidpointRounding.AwayFromZero);
    }

    private async Task UpdateTickerSubscriptionsAsync()
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var leg in Legs)
        {
            var symbol = GetTickerForLeg(leg)?.Symbol;
            if (!string.IsNullOrWhiteSpace(symbol))
            {
                symbols.Add(symbol);
            }
        }

        if (_selectedExpiration.HasValue && DisplayStrikes.Count > 0)
        {
            foreach (var strike in DisplayStrikes)
            {
                var call = GetStrikeTicker(strike, LegType.Call);
                if (call is not null && !string.IsNullOrWhiteSpace(call.Symbol))
                {
                    symbols.Add(call.Symbol);
                }

                var put = GetStrikeTicker(strike, LegType.Put);
                if (put is not null && !string.IsNullOrWhiteSpace(put.Symbol))
                {
                    symbols.Add(put.Symbol);
                }
            }
        }

        var toRemove = _tickerSubscriptions.Keys.Where(symbol => !symbols.Contains(symbol)).ToList();
        foreach (var symbol in toRemove)
        {
            _tickerSubscriptions[symbol].Dispose();
            _tickerSubscriptions.Remove(symbol);
        }

        foreach (var symbol in symbols)
        {
            if (_tickerSubscriptions.ContainsKey(symbol))
            {
                continue;
            }
            var registration = await _exchangeService.OptionsChain.SubscribeAsync(symbol, HandleTickerUpdated);
            _tickerSubscriptions[symbol] = registration;
        }
    }

    private async Task HandleTickerUpdated(OptionChainTicker ticker)
    {
        _chainTickers = GetBaseAssetTickers();
        UpdateFilteredTickers();
        OnChange?.Invoke();
    }
}




