using System.Collections.ObjectModel;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class OptionChainDialogViewModel : IDisposable
{
    private readonly OptionsChainService _optionsChainService;
    private PositionModel? _position;
    private List<OptionChainTicker> _chainTickers = new();
    private string? _baseAsset;
    private DateTime? _selectedExpiration;
    private double? _atmStrike;
    private double? _underlyingPrice;
    private readonly HashSet<Guid> _ivModeLegIds = new();

    public OptionChainDialogViewModel(OptionsChainService optionsChainService)
    {
        _optionsChainService = optionsChainService;
    }

    public ObservableCollection<OptionLegModel> Legs { get; } = new();

    public IReadOnlyList<DateTime> AvailableExpirations { get; private set; } = Array.Empty<DateTime>();

    public IReadOnlyList<double> AvailableStrikes { get; private set; } = Array.Empty<double>();

    public DateTime? SelectedExpiration
    {
        get => _selectedExpiration;
        private set => _selectedExpiration = value;
    }

    public bool IsRefreshing { get; private set; }

    public event Action? OnChange;

    public Task InitializeAsync(PositionModel? position, double? underlyingPrice)
    {
        _position = position;
        _baseAsset = position?.BaseAsset;
        _underlyingPrice = underlyingPrice;
        Legs.Clear();

        _optionsChainService.ChainUpdated += HandleChainUpdated;

        if (position is not null)
        {
            foreach (var leg in position.Legs)
            {
                Legs.Add(CloneLeg(leg));
            }
        }

        _chainTickers = _optionsChainService.GetSnapshot().ToList();
        UpdateExpirations();

        if (Legs.Count > 0)
        {
            SelectedExpiration = Legs.Max(leg => leg.ExpirationDate.Date);
        }
        else if (AvailableExpirations.Count > 0)
        {
            SelectedExpiration ??= AvailableExpirations.First();
        }

        UpdateStrikes();
        OnChange?.Invoke();

        _ = RefreshChainAsync();
        return Task.CompletedTask;
    }

    public void SetSelectedExpiration(DateTime? expiration)
    {
        SelectedExpiration = expiration;
        UpdateStrikes();
        OnChange?.Invoke();
    }

    public OptionChainTicker? GetTickerForLeg(OptionLegModel leg)
    {
        return _optionsChainService.FindTickerForLeg(leg, _baseAsset);
    }

    public bool IsLegIvMode(OptionLegModel leg) => _ivModeLegIds.Contains(leg.Id);

    public void ToggleLegInputMode(OptionLegModel leg)
    {
        if (!_ivModeLegIds.Add(leg.Id))
        {
            _ivModeLegIds.Remove(leg.Id);
        }

        OnChange?.Invoke();
    }

    public double GetLegInputValue(OptionLegModel leg)
    {
        return IsLegIvMode(leg) ? leg.ImpliedVolatility : leg.Price;
    }

    public void SetLegInputValue(OptionLegModel leg, double value)
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

    public void SetLegSize(OptionLegModel leg, double size)
    {
        leg.Size = size;
        OnChange?.Invoke();
    }

    public void AdjustLegSize(OptionLegModel leg, double delta)
    {
        leg.Size += delta;
        OnChange?.Invoke();
    }

    public string GetContractSymbol(OptionLegModel leg)
    {
        return leg.ChainSymbol ?? GetTickerForLeg(leg)?.Symbol ?? string.Empty;
    }

    public string GetContractDescription(OptionLegModel leg)
    {
        var typeLabel = leg.Type switch
        {
            OptionLegType.Call => "CALL",
            OptionLegType.Put => "PUT",
            _ => "FUT"
        };

        var dte = (leg.ExpirationDate.Date - DateTime.UtcNow.Date).TotalDays;
        return $"({typeLabel} {leg.Strike:0}, Exp: {leg.ExpirationDate:dd.MM}, DTE: {dte:0.0})";
    }

    public void AddLeg(double strike, OptionLegType type)
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

        var leg = new OptionLegModel
        {
            Type = type,
            Strike = strike,
            ExpirationDate = _selectedExpiration.Value.Date,
            Size = 1,
            Price = ticker?.MarkPrice ?? 0,
            ImpliedVolatility = ticker?.MarkIv ?? 0,
            ChainSymbol = ticker?.Symbol
        };

        Legs.Add(leg);
        OnChange?.Invoke();
    }

    public void RemoveLeg(OptionLegModel leg)
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

        var callIv = candidates.FirstOrDefault(ticker => ticker.Type == OptionLegType.Call)?.MarkIv;
        if (callIv > 0)
        {
            return callIv;
        }

        var putIv = candidates.FirstOrDefault(ticker => ticker.Type == OptionLegType.Put)?.MarkIv;
        return putIv > 0 ? putIv : null;
    }

    public double GetTotalQuantity()
    {
        return Legs.Sum(leg => leg.Size);
    }

    public double GetTotalPremium()
    {
        return Legs.Sum(leg => leg.Price * leg.Size);
    }

    public double GetAverageImpliedVolatility()
    {
        var weighted = Legs.Sum(leg => leg.ImpliedVolatility * leg.Size);
        var totalSize = Legs.Sum(leg => leg.Size);
        if (totalSize <= 0)
        {
            return 0;
        }

        return weighted / totalSize;
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

    public double GetLegGreekValue(OptionLegModel leg, Func<OptionChainTicker, double?> selector)
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
            .Where(ticker => ticker.Type == OptionLegType.Call && ticker.Delta.HasValue)
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

    private void HandleChainUpdated()
    {
        _chainTickers = _optionsChainService.GetSnapshot().ToList();
        UpdateExpirations();
        UpdateStrikes();
        OnChange?.Invoke();
    }

    private static OptionLegModel CloneLeg(OptionLegModel leg)
    {
        return new OptionLegModel
        {
            Id = leg.Id,
            IsIncluded = leg.IsIncluded,
            Type = leg.Type,
            Strike = leg.Strike,
            ExpirationDate = leg.ExpirationDate,
            Size = leg.Size,
            Price = leg.Price,
            ImpliedVolatility = leg.ImpliedVolatility,
            ChainSymbol = leg.ChainSymbol
        };
    }

    public void Dispose()
    {
        _optionsChainService.ChainUpdated -= HandleChainUpdated;
    }
}
