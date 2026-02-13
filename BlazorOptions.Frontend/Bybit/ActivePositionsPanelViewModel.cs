using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class ActivePositionsPanelViewModel : IDisposable
{
    private static readonly string[] _positionExpirationFormats = { "ddMMMyy", "ddMMMyyyy" };
    private readonly IExchangeService _exchangeService;
    private readonly HashSet<ExchangePosition> _selectedPositions;
    private readonly BybitPositionComparer _comparer = new();
    private readonly List<ExchangePosition> _filteredPositions = new();
    private IReadOnlyList<LegModel> _existingLegs = [];
    private IDisposable? _positionsSubscription;

    public ActivePositionsPanelViewModel(IExchangeService exchangeService)
    {
        _exchangeService = exchangeService;
        _selectedPositions = new HashSet<ExchangePosition>(_comparer);
    }

    public string BaseAsset { get; private set; } = "ETH";

    public string QuoteAsset { get; private set; } = "USDT";

    public bool IsLoading { get; private set; }

    public string? LoadError { get; private set; }

    public int ExcludedCount { get; private set; }

    public IReadOnlyList<ExchangePosition> Positions => _filteredPositions;

    public IReadOnlyCollection<ExchangePosition> SelectedPositions => _selectedPositions;

    public string SelectedCountLabel => _selectedPositions.Count == 0
        ? "No positions selected"
        : $"Selected: {_selectedPositions.Count}";

    public event Action? OnChange;

    public async Task InitializeAsync(string? initialBaseAsset, string? initialQuoteAsset, IReadOnlyList<LegModel>? existingLegs)
    {
        BaseAsset = string.IsNullOrWhiteSpace(initialBaseAsset) ? "ETH" : initialBaseAsset.Trim();
        QuoteAsset = string.IsNullOrWhiteSpace(initialQuoteAsset) ? "USDT" : initialQuoteAsset.Trim();
        _existingLegs = existingLegs ?? Array.Empty<LegModel>();

        if (_positionsSubscription is null)
        {
            _positionsSubscription = await _exchangeService.Positions.SubscribeAsync(HandlePositionsUpdated);
        }

        await ApplyFilter();
    }

    public async Task SetBaseAsset(string value)
    {
        BaseAsset = value;
        LoadError = null;
        await ApplyFilter();
    }

    public async Task SetQuoteAsset(string value)
    {
        QuoteAsset = value;
        LoadError = null;
        await ApplyFilter();
    }


    public bool GetSelection(ExchangePosition position)
    {
        return _selectedPositions.Contains(position);
    }

    public void SetSelection(ExchangePosition position, bool isSelected)
    {
        if (isSelected)
        {
            _selectedPositions.Add(position);
        }
        else
        {
            _selectedPositions.Remove(position);
        }

        OnChange?.Invoke();
    }

    public void SelectAll()
    {
        foreach (var position in _filteredPositions)
        {
            _selectedPositions.Add(position);
        }

        OnChange?.Invoke();
    }

    public void ClearSelection()
    {
        _selectedPositions.Clear();
        OnChange?.Invoke();
    }

    public static string FormatSignedSize(ExchangePosition position)
    {
        var magnitude = Math.Abs(position.Size);
        if (magnitude < 0.0001m)
        {
            return "0";
        }

        var sign = string.Equals(position.Side, "Sell", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(position.Side, "Short", StringComparison.OrdinalIgnoreCase)
            ? "-"
            : "+";

        return $"{sign}{magnitude:0.####}";
    }

    private async Task HandlePositionsUpdated(IReadOnlyList<ExchangePosition> positions)
    {
        await ApplyFilter();
    }

    private async Task ApplyFilter()
    {
        _filteredPositions.Clear();
        ExcludedCount = 0;

        var baseAsset = string.IsNullOrWhiteSpace(BaseAsset) ? null : BaseAsset.Trim();
        var existingLegs = _existingLegs;

        foreach (var position in await _exchangeService.Positions.GetPositionsAsync())
        {
            if (!string.IsNullOrWhiteSpace(baseAsset) &&
                !position.Symbol.StartsWith(baseAsset, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(baseAsset) && existingLegs.Count > 0 &&
                TryBuildLegFromBybitPosition(position, baseAsset, position.Category, out var candidate) &&
                existingLegs.Any(existing => IsLegMatch(existing, candidate)))
            {
                _selectedPositions.Add(position);
            }

            _filteredPositions.Add(position);
        }

        SyncSelection();
        if (_filteredPositions.Count > 0)
        {
            LoadError = null;
        }
        else if (LoadError is null)
        {
            LoadError = "No active positions found for the selected asset.";
        }

        OnChange?.Invoke();
    }

    private void SyncSelection()
    {
        if (_selectedPositions.Count == 0)
        {
            return;
        }

        _selectedPositions.RemoveWhere(position => !_filteredPositions.Contains(position, _comparer));
    }

    private bool TryBuildLegFromBybitPosition(ExchangePosition position, string baseAsset, string category, out LegModel leg)
    {
        leg = new LegModel();
        if (string.IsNullOrWhiteSpace(position.Symbol))
        {
            return false;
        }

        DateTime? expiration = null;
        decimal? strike = null;
        var type = LegType.Future;
        if (string.Equals(category, "option", StringComparison.OrdinalIgnoreCase))
        {
            if (!_exchangeService.TryParseSymbol(position.Symbol, out var parsedBase, out var parsedExpiration, out var parsedStrike, out var parsedType))
            {
                return false;
            }

            if (!string.Equals(parsedBase, baseAsset, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            expiration = parsedExpiration;
            strike = parsedStrike;
            type = parsedType;
        }
        else
        {
            if (!position.Symbol.StartsWith(baseAsset, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (TryParseFutureExpiration(position.Symbol, out var parsedFutureExpiration))
            {
                expiration = parsedFutureExpiration;
            }
        }

        leg = new LegModel
        {
            Type = type,
            Strike = strike,
            ExpirationDate = expiration
        };

        return true;
    }

    private static bool IsLegMatch(LegModel existing, LegModel candidate)
    {
        return existing.Type == candidate.Type
               && IsDateMatch(existing.ExpirationDate, candidate.ExpirationDate)
               && IsStrikeMatch(existing.Strike, candidate.Strike);
    }

    private static bool IsStrikeMatch(decimal? left, decimal? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return true;
        }

        if (!left.HasValue || !right.HasValue)
        {
            return false;
        }

        return Math.Abs(left.Value - right.Value) < 0.01m;
    }

    private static bool IsDateMatch(DateTime? left, DateTime? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return true;
        }

        if (!left.HasValue || !right.HasValue)
        {
            return false;
        }

        return left.Value.Date == right.Value.Date;
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
            DateTime.TryParseExact(tokens[1], _positionExpirationFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
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

    public void Dispose()
    {
        _positionsSubscription?.Dispose();
        _positionsSubscription = null;
    }

    private sealed class BybitPositionComparer : IEqualityComparer<ExchangePosition>
    {
        public bool Equals(ExchangePosition? x, ExchangePosition? y)
        {
            if (x is null || y is null)
            {
                return false;
            }

            return string.Equals(x.Symbol, y.Symbol, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(x.Category, y.Category, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(x.Side, y.Side, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(ExchangePosition obj)
        {
            return HashCode.Combine(
                obj.Symbol?.ToUpperInvariant(),
                obj.Category?.ToUpperInvariant(),
                obj.Side?.ToUpperInvariant());
        }
    }
}
