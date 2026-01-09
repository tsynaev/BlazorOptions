using System.Collections.ObjectModel;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class OptionChainDialogViewModel
{
    private readonly OptionsChainService _optionsChainService;
    private PositionModel? _position;
    private List<OptionChainTicker> _chainTickers = new();
    private string? _baseAsset;
    private DateTime? _selectedExpiration;

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

    public Task InitializeAsync(PositionModel? position)
    {
        _position = position;
        _baseAsset = position?.BaseAsset;
        Legs.Clear();

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
        else
        {
            SelectedExpiration ??= AvailableExpirations.FirstOrDefault();
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

        await _optionsChainService.RefreshAsync();
        _chainTickers = _optionsChainService.GetSnapshot().ToList();
        UpdateExpirations();
        UpdateStrikes();
        IsRefreshing = false;
        OnChange?.Invoke();
    }

    private void UpdateExpirations()
    {
        var expirations = _chainTickers
            .Where(ticker => string.IsNullOrWhiteSpace(_baseAsset) || string.Equals(ticker.BaseAsset, _baseAsset, StringComparison.OrdinalIgnoreCase))
            .Select(ticker => ticker.ExpirationDate.Date)
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        AvailableExpirations = expirations;

        if (_selectedExpiration.HasValue && !AvailableExpirations.Contains(_selectedExpiration.Value.Date))
        {
            SelectedExpiration = AvailableExpirations.FirstOrDefault();
        }
    }

    private void UpdateStrikes()
    {
        if (!_selectedExpiration.HasValue)
        {
            AvailableStrikes = Array.Empty<double>();
            return;
        }

        var strikes = _chainTickers
            .Where(ticker =>
                string.Equals(ticker.BaseAsset, _baseAsset, StringComparison.OrdinalIgnoreCase)
                && ticker.ExpirationDate.Date == _selectedExpiration.Value.Date)
            .Select(ticker => ticker.Strike)
            .Distinct()
            .OrderBy(strike => strike)
            .ToList();

        AvailableStrikes = strikes;
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
}
