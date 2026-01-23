using System.Collections.ObjectModel;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class OptionChainDialogViewModel : IDisposable
{
    private const string StrikeWindowKey = "optionChainDialog:strikeWindow";
    private readonly OptionsChainService _optionsChainService;
    private readonly LocalStorageService _localStorageService;
    private PositionModel? _position;
    private List<OptionChainTicker> _chainTickers = new();
    private string? _baseAsset;
    private DateTime? _selectedExpiration;
    private double? _atmStrike;
    private double? _underlyingPrice;
    private readonly HashSet<string> _ivModeLegIds = new();
    private readonly Dictionary<string, IDisposable> _tickerSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private int _strikeWindowSize = 10;

    public OptionChainDialogViewModel(OptionsChainService optionsChainService, LocalStorageService localStorageService)
    {
        _optionsChainService = optionsChainService;
        _localStorageService = localStorageService;
    }

    public ObservableCollection<LegModel> Legs { get; } = new();

    public IReadOnlyList<DateTime> AvailableExpirations { get; private set; } = [];

    public IReadOnlyList<double> AvailableStrikes { get; private set; } = [];

    public IReadOnlyList<double> DisplayStrikes { get; private set; } = [];

    public int StrikeWindowSize => _strikeWindowSize;

    public DateTime? SelectedExpiration
    {
        get => _selectedExpiration;
        private set => _selectedExpiration = value;
    }

    public bool IsRefreshing { get; private set; }

    public event Action? OnChange;

    public async Task InitializeAsync(PositionModel? position, LegsCollectionModel? collection, double? underlyingPrice)
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

        _chainTickers = _optionsChainService.GetSnapshot().ToList();
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
        return _optionsChainService.FindTickerForLeg(leg, _baseAsset);
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

    public double GetLegInputValue(LegModel leg)
    {
        return IsLegIvMode(leg) ? (leg.ImpliedVolatility ?? 0) : (leg.Price ?? 0);
    }

    public void SetLegInputValue(LegModel leg, double value)
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

    public void SetLegSize(LegModel leg, double size)
    {
        leg.Size = size;
        OnChange?.Invoke();
    }

    public void AdjustLegSize(LegModel leg, double delta)
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

    public void AddLeg(double strike, LegType type)
    {
        if (!_selectedExpiration.HasValue)
        {
            return;
        }

        var ticker = _chainTickers.FirstOrDefault(item =>
            string.Equals(item.BaseAsset, _baseAsset, StringComparison.OrdinalIgnoreCase)
            && item.ExpirationDate.Date == _selectedExpiration.Value.Date
            && item.Type == type
            && Math.Abs(item.Strike - strike) < 0.01);

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

    public void AddLegFromQuote(double strike, LegType type, double price, double iv)
    {
        if (!_selectedExpiration.HasValue)
        {
            return;
        }

        var ticker = _chainTickers.FirstOrDefault(item =>
            string.Equals(item.BaseAsset, _baseAsset, StringComparison.OrdinalIgnoreCase)
            && item.ExpirationDate.Date == _selectedExpiration.Value.Date
            && item.Type == type
            && Math.Abs(item.Strike - strike) < 0.01);

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

        await _optionsChainService.RefreshAsync(_baseAsset);
        _chainTickers = _optionsChainService.GetSnapshot().ToList();
        UpdateExpirations();
        UpdateStrikes();
        IsRefreshing = false;
        OnChange?.Invoke();
    }

    private void UpdateExpirations()
    {
        var expirations = GetFilteredTickers()
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
            AvailableStrikes = Array.Empty<double>();
            _atmStrike = null;
            return;
        }

        var relevantTickers = GetFilteredTickers()
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
        _ = UpdateTickerSubscriptionsAsync();
    }

    private IEnumerable<OptionChainTicker> GetFilteredTickers()
    {
        if (string.IsNullOrWhiteSpace(_baseAsset))
        {
            return _chainTickers;
        }

        var filtered = _chainTickers
            .Where(ticker => string.Equals(ticker.BaseAsset, _baseAsset, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return filtered.Count > 0 ? filtered : _chainTickers;
    }

    public bool IsAtmStrike(double strike)
    {
        if (!_atmStrike.HasValue)
        {
            return false;
        }

        return Math.Abs(strike - _atmStrike.Value) < 0.01;
    }

    public double? GetStrikeImpliedVolatility(double strike)
    {
        if (!_selectedExpiration.HasValue)
        {
            return null;
        }

        var candidates = GetFilteredTickers()
            .Where(ticker => ticker.ExpirationDate.Date == _selectedExpiration.Value.Date
                && Math.Abs(ticker.Strike - strike) < 0.01)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var callIv = candidates.FirstOrDefault(ticker => ticker.Type == LegType.Call)?.MarkIv;
        var normalizedCall = NormalizeIv(callIv);
        if (normalizedCall.HasValue && normalizedCall.Value > 0)
        {
            return normalizedCall.Value;
        }

        var putIv = candidates.FirstOrDefault(ticker => ticker.Type == LegType.Put)?.MarkIv;
        var normalizedPut = NormalizeIv(putIv);
        return normalizedPut.HasValue && normalizedPut.Value > 0 ? normalizedPut.Value : null;
    }

    public OptionChainTicker? GetStrikeTicker(double strike, LegType type)
    {
        if (!_selectedExpiration.HasValue)
        {
            return null;
        }

        return GetFilteredTickers()
            .FirstOrDefault(ticker => ticker.ExpirationDate.Date == _selectedExpiration.Value.Date
                && ticker.Type == type
                && Math.Abs(ticker.Strike - strike) < 0.01);
    }

    public double GetTotalPremium()
    {
        return Legs.Sum(leg => (leg.Price ?? 0) * leg.Size);
    }


    public double GetTotalGreek(Func<OptionChainTicker, double?> selector)
    {
        return Legs.Sum(leg =>
        {
            var ticker = GetTickerForLeg(leg);
            var greek = ticker is null ? 0 : selector(ticker) ?? 0;
            return greek * leg.Size;
        });
    }

    public double GetLegGreekValue(LegModel leg, Func<OptionChainTicker, double?> selector)
    {
        var ticker = GetTickerForLeg(leg);
        return ticker is null ? 0 : selector(ticker) ?? 0;
    }

    private static double? DetermineAtmStrike(List<OptionChainTicker> tickers, List<double> strikes, double? underlyingPrice)
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
            .OrderBy(ticker => Math.Abs(ticker.Delta!.Value - 0.5))
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

    private static IReadOnlyList<double> BuildStrikeWindow(IReadOnlyList<double> strikes, double? atmStrike, int windowSize)
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

        var window = new List<double>();
        for (var i = start; i <= end; i++)
        {
            window.Add(strikes[i]);
        }

        return window;
    }

    private static double? NormalizeIv(double? value)
    {
        if (!value.HasValue || value.Value <= 0)
        {
            return null;
        }

        return value.Value <= 3 ? value.Value * 100 : value.Value;
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

            var subscription = new OptionsChainService.OptionChainSubscription(symbol);
            var registration = await _optionsChainService.SubscribeAsync(subscription, HandleTickerUpdated);
            _tickerSubscriptions[symbol] = registration;
        }
    }

    private void HandleTickerUpdated(OptionChainTicker ticker)
    {
        _chainTickers = _optionsChainService.GetSnapshot().ToList();
        OnChange?.Invoke();
    }
}




