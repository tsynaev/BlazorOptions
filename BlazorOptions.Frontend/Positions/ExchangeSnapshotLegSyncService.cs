using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class ExchangeSnapshotLegSyncService
{
    private static readonly string[] PositionExpirationFormats = { "ddMMMyy", "ddMMMyyyy" };
    private readonly IExchangeService _exchangeService;

    public ExchangeSnapshotLegSyncService(IExchangeService exchangeService)
    {
        _exchangeService = exchangeService;
    }

    public LegModel? CreateLegFromExchangePosition(ExchangePosition position, string baseAsset, string? category, bool include = true)
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

        return new LegModel
        {
            ReferenceId = BuildPositionReferenceId(position.Symbol, size),
            IsReadOnly = true,
            IsIncluded = include,
            Status = LegStatus.Active,
            Type = type,
            Strike = strike,
            ExpirationDate = expiration,
            Size = size,
            Price = position.AvgPrice,
            ImpliedVolatility = null,
            Symbol = position.Symbol
        };
    }

    public LegModel? CreateLegFromExchangeOrder(ExchangeOrder order, string baseAsset, bool include = false)
    {
        if (string.IsNullOrWhiteSpace(order.Symbol))
        {
            return null;
        }

        DateTime? expiration = null;
        decimal? strike = null;
        var type = LegType.Future;

        if (string.Equals(order.Category, "option", StringComparison.OrdinalIgnoreCase))
        {
            if (!_exchangeService.TryParseSymbol(order.Symbol, out var parsedBase, out var parsedExpiration, out var parsedStrike, out var parsedType))
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
            if (!order.Symbol.StartsWith(baseAsset, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (TryParseFutureExpiration(order.Symbol, out var parsedFutureExpiration))
            {
                expiration = parsedFutureExpiration;
            }
        }

        var size = DetermineSignedSize(order);
        if (Math.Abs(size) < 0.0001m)
        {
            return null;
        }

        return new LegModel
        {
            Id = BuildOrderCandidateId(order),
            ReferenceId = BuildOrderReferenceId(order),
            IsReadOnly = true,
            IsIncluded = include,
            Status = LegStatus.Order,
            Type = type,
            Strike = strike,
            ExpirationDate = expiration,
            Size = size,
            Price = order.Price,
            ImpliedVolatility = null,
            Symbol = order.Symbol
        };
    }

    public bool SyncOrderLegs(
        ObservableCollection<LegModel> legs,
        IReadOnlyList<LegModel> orderLegs,
        IReadOnlyList<LegModel> activeLegs,
        Action<LegModel>? onRemoved = null)
    {
        var changed = false;
        var openOrderLegs = orderLegs
            .Where(leg => !string.IsNullOrWhiteSpace(leg.ReferenceId))
            .ToDictionary(leg => leg.ReferenceId!, StringComparer.OrdinalIgnoreCase);
        var existingOrderLegs = legs.Where(leg => leg.Status == LegStatus.Order).ToList();

        foreach (var existing in existingOrderLegs)
        {
            var referenceId = existing.ReferenceId
                ?? ExtractReferenceIdFromCandidateId(existing.Id)
                ?? BuildFallbackOrderReference(existing.Symbol, existing.Size, existing.Price);
            existing.ReferenceId = referenceId;

            if (!openOrderLegs.TryGetValue(referenceId, out var snapshotLeg))
            {
                if (TryConvertExecutedOrderToActive(legs, existing, activeLegs, onRemoved))
                {
                    changed = true;
                    continue;
                }

                legs.Remove(existing);
                onRemoved?.Invoke(existing);
                changed = true;
                continue;
            }

            changed |= ApplyOrderLegSnapshot(existing, snapshotLeg);
        }

        return changed;
    }

    public void SyncReadOnlyLegs(ObservableCollection<LegModel> legs, IReadOnlyList<LegModel> activeLegs)
    {
        var lookup = activeLegs
            .Where(leg => !string.IsNullOrWhiteSpace(leg.Symbol))
            .GroupBy(leg => leg.Symbol!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var leg in legs)
        {
            if (leg.Status == LegStatus.Order)
            {
                continue;
            }

            if (!leg.IsReadOnly)
            {
                leg.Status = LegStatus.New;
                continue;
            }

            var symbol = leg.Symbol?.Trim();
            if (string.IsNullOrWhiteSpace(symbol) || !lookup.TryGetValue(symbol, out var matches))
            {
                leg.Status = LegStatus.Missing;
                leg.IsIncluded = false;
                continue;
            }

            var match = matches.FirstOrDefault(item =>
                Math.Sign(item.Size) == Math.Sign(leg.Size)
                && item.Type == leg.Type
                && item.ExpirationDate?.Date == leg.ExpirationDate?.Date
                && item.Strike == leg.Strike);

            if (match is null)
            {
                leg.Status = LegStatus.Missing;
                leg.IsIncluded = false;
                continue;
            }

            ApplyActiveLegSnapshot(leg, match, forceInclude: false);
        }
    }

    public static bool ApplyOrderLegSnapshot(LegModel target, LegModel snapshot)
    {
        var changed = false;
        if (target.Type != snapshot.Type)
        {
            target.Type = snapshot.Type;
            changed = true;
        }

        if (target.Strike != snapshot.Strike)
        {
            target.Strike = snapshot.Strike;
            changed = true;
        }

        if (target.ExpirationDate != snapshot.ExpirationDate)
        {
            target.ExpirationDate = snapshot.ExpirationDate;
            changed = true;
        }

        if (target.Size != snapshot.Size)
        {
            target.Size = snapshot.Size;
            changed = true;
        }

        if (target.Price != snapshot.Price)
        {
            target.Price = snapshot.Price;
            changed = true;
        }

        if (!string.Equals(target.Symbol, snapshot.Symbol, StringComparison.OrdinalIgnoreCase))
        {
            target.Symbol = snapshot.Symbol;
            changed = true;
        }

        if (!target.IsReadOnly)
        {
            target.IsReadOnly = true;
            changed = true;
        }

        if (target.IsIncluded)
        {
            target.IsIncluded = false;
            changed = true;
        }

        if (target.Status != LegStatus.Order)
        {
            target.Status = LegStatus.Order;
            changed = true;
        }

        if (!string.Equals(target.ReferenceId, snapshot.ReferenceId, StringComparison.OrdinalIgnoreCase))
        {
            target.ReferenceId = snapshot.ReferenceId;
            changed = true;
        }

        return changed;
    }

    public static void ApplyActiveLegSnapshot(LegModel target, LegModel snapshot, bool forceInclude)
    {
        target.Status = LegStatus.Active;
        target.IsReadOnly = true;
        if (forceInclude)
        {
            target.IsIncluded = true;
        }

        target.Type = snapshot.Type;
        target.Strike = snapshot.Strike;
        target.ExpirationDate = snapshot.ExpirationDate;
        target.Size = snapshot.Size;
        target.Price = snapshot.Price;
        target.Symbol = snapshot.Symbol;
        target.ReferenceId = snapshot.ReferenceId;

        if (!string.IsNullOrWhiteSpace(target.Id)
            && target.Id.StartsWith("order:", StringComparison.OrdinalIgnoreCase))
        {
            target.Id = Guid.NewGuid().ToString("N");
        }
    }

    public static string BuildOrderCandidateId(ExchangeOrder order)
    {
        return $"order:{BuildOrderReferenceId(order)}";
    }

    public static string BuildOrderReferenceId(ExchangeOrder order)
    {
        if (!string.IsNullOrWhiteSpace(order.OrderId))
        {
            return order.OrderId.Trim();
        }

        return BuildFallbackOrderReference(order.Symbol, DetermineSignedSize(order), order.Price);
    }

    public static string BuildPositionReferenceId(string? symbol, decimal size)
    {
        var normalizedSymbol = string.IsNullOrWhiteSpace(symbol) ? string.Empty : symbol.Trim().ToUpperInvariant();
        return $"position:{normalizedSymbol}:{Math.Sign(size)}";
    }

    public static string BuildFallbackOrderReference(string? symbol, decimal size, decimal? price)
    {
        var normalizedSymbol = string.IsNullOrWhiteSpace(symbol) ? string.Empty : symbol.Trim().ToUpperInvariant();
        var side = Math.Sign(size).ToString(CultureInfo.InvariantCulture);
        var qty = Math.Abs(size).ToString("0.########", CultureInfo.InvariantCulture);
        var normalizedPrice = price.HasValue ? price.Value.ToString("0.########", CultureInfo.InvariantCulture) : "mkt";
        return $"{normalizedSymbol}:{side}:{qty}:{normalizedPrice}";
    }

    public static string? ExtractReferenceIdFromCandidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var value = id.Trim();
        return value.StartsWith("order:", StringComparison.OrdinalIgnoreCase)
            ? value["order:".Length..]
            : value;
    }

    public static decimal DetermineSignedSize(ExchangePosition position)
    {
        var magnitude = Math.Abs(position.Size);
        if (magnitude < 0.0001m)
        {
            return 0m;
        }

        if (!string.IsNullOrWhiteSpace(position.Side))
        {
            var normalized = position.Side.Trim();
            if (string.Equals(normalized, "Sell", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Short", StringComparison.OrdinalIgnoreCase))
            {
                return -magnitude;
            }
        }

        return magnitude;
    }

    public static decimal DetermineSignedSize(ExchangeOrder order)
    {
        var magnitude = Math.Abs(order.Qty);
        if (magnitude < 0.0001m)
        {
            return 0m;
        }

        if (!string.IsNullOrWhiteSpace(order.Side)
            && string.Equals(order.Side.Trim(), "Sell", StringComparison.OrdinalIgnoreCase))
        {
            return -magnitude;
        }

        return magnitude;
    }

    private static bool TryConvertExecutedOrderToActive(
        ObservableCollection<LegModel> legs,
        LegModel orderLeg,
        IReadOnlyList<LegModel> openPositionLegs,
        Action<LegModel>? onRemoved)
    {
        var matchedPositionLeg = openPositionLegs.FirstOrDefault(positionLeg =>
            string.Equals(positionLeg.Symbol, orderLeg.Symbol, StringComparison.OrdinalIgnoreCase)
            && Math.Sign(positionLeg.Size) == Math.Sign(orderLeg.Size)
            && positionLeg.Type == orderLeg.Type
            && positionLeg.ExpirationDate?.Date == orderLeg.ExpirationDate?.Date
            && positionLeg.Strike == orderLeg.Strike);

        if (matchedPositionLeg is null)
        {
            return false;
        }

        if (legs.Any(existing =>
                !ReferenceEquals(existing, orderLeg)
                && existing.IsReadOnly
                && existing.Status != LegStatus.Order
                && string.Equals(existing.Symbol, matchedPositionLeg.Symbol, StringComparison.OrdinalIgnoreCase)
                && Math.Sign(existing.Size) == Math.Sign(matchedPositionLeg.Size)))
        {
            legs.Remove(orderLeg);
            onRemoved?.Invoke(orderLeg);
            return true;
        }

        ApplyActiveLegSnapshot(orderLeg, matchedPositionLeg, forceInclude: true);
        return true;
    }

    private static bool TryParseFutureExpiration(string symbol, out DateTime expiration)
    {
        expiration = default;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var tokens = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length >= 2
            && DateTime.TryParseExact(tokens[1], PositionExpirationFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
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
}
