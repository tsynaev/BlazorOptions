using BlazorOptions.API.Positions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlazorOptions.Services;

public sealed class AmberdataPositionsService
{
    private readonly AmberdataDerivativesGraphQl _amberdata;
    private readonly IExchangeService _exchangeService;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, IReadOnlyList<StrategyPosition>> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PositionTradeSnapshot> _detailsCache = new(StringComparer.Ordinal);

    public AmberdataPositionsService(AmberdataDerivativesGraphQl amberdata, IExchangeService exchangeService)
    {
        _amberdata = amberdata;
        _exchangeService = exchangeService;
    }

    public async Task<IReadOnlyList<StrategyPosition>> GetDeribitEthPositionsAsync(DateTime dateStartUtc, DateTime dateEndUtc)
    {
        var normalizedStart = dateStartUtc.ToUniversalTime();
        var normalizedEnd = dateEndUtc.ToUniversalTime();
        if (normalizedEnd <= normalizedStart)
        {
            normalizedEnd = normalizedStart.AddHours(1);
        }

        var key = BuildRangeKey(normalizedStart, normalizedEnd);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var trades = await _amberdata.GetDeribitEthBlockTradesAsync(normalizedStart, normalizedEnd);
        var mapped = trades
            .Select(MapBlockTrade)
            .Where(item => item is not null)
            .Cast<StrategyPosition>()
            .ToArray();

        lock (_cacheLock)
        {
            _cache[key] = mapped;
        }

        return mapped;
    }
    
    public void Invalidate(DateTime dateStartUtc, DateTime dateEndUtc)
    {
        var key = BuildRangeKey(dateStartUtc.ToUniversalTime(), dateEndUtc.ToUniversalTime());
        lock (_cacheLock)
        {
            _cache.Remove(key);
            var prefix = $"{key}|";
            foreach (var detailKey in _detailsCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _detailsCache.Remove(detailKey);
            }
        }
    }

    public async Task<IReadOnlyList<LegModel>> GetPositionLegsAsync(StrategyPosition position, DateTime rangeStartUtc, DateTime rangeEndUtc)
    {
        var snapshot = await GetPositionSnapshotAsync(position, rangeStartUtc, rangeEndUtc);
        return snapshot.Legs.Select(item => item.Clone()).ToArray();
    }

    public async Task<IReadOnlyList<PositionTradeDetailRow>> GetPositionDetailsAsync(StrategyPosition position, DateTime rangeStartUtc, DateTime rangeEndUtc)
    {
        var snapshot = await GetPositionSnapshotAsync(position, rangeStartUtc, rangeEndUtc);
        return snapshot.Details.ToArray();
    }

    public async Task<PositionTradeSnapshot> GetPositionSnapshotAsync(StrategyPosition position, DateTime rangeStartUtc, DateTime rangeEndUtc)
    {
        if (string.IsNullOrWhiteSpace(position.UniqueTrade))
        {
            return new PositionTradeSnapshot(
                new[] { position.Leg.Clone() },
                Array.Empty<PositionTradeDetailRow>());
        }

        var normalizedStart = rangeStartUtc.ToUniversalTime();
        var normalizedEnd = rangeEndUtc.ToUniversalTime();
        if (normalizedEnd <= normalizedStart)
        {
            normalizedEnd = normalizedStart.AddHours(1);
        }

        var rangeKey = BuildRangeKey(normalizedStart, normalizedEnd);
        var cacheKey = $"{rangeKey}|{position.UniqueTrade}";
        lock (_cacheLock)
        {
            if (_detailsCache.TryGetValue(cacheKey, out var cached))
            {
                return new PositionTradeSnapshot(
                    cached.Legs.Select(item => item.Clone()).ToArray(),
                    cached.Details.ToArray());
            }
        }

        IReadOnlyList<LegModel> legsResult;
        IReadOnlyList<PositionTradeDetailRow> detailRows;
        try
        {
            var details = await _amberdata.GetDeribitEthTopTradesByUniqueTradeAsync(normalizedStart, normalizedEnd, position.UniqueTrade, blockTradeId: true);
            var selectedRows = SelectRowsForPosition(details, position);
            var legs = selectedRows
                .Select(MapDetailToLeg)
                .Where(item => item is not null)
                .Cast<LegModel>()
                .ToArray();
            detailRows = selectedRows
                .Select(MapDetailRow)
                .OrderByDescending(item => item.TimestampUtc ?? DateTime.MinValue)
                .ThenBy(item => item.Instrument, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            legsResult = legs.Length > 0 ? legs : new[] { position.Leg.Clone() };
        }
        catch
        {
            // Amberdata detail query is unstable for some uniqueTrade values; keep chart usable with base leg.
            legsResult = new[] { position.Leg.Clone() };
            detailRows = Array.Empty<PositionTradeDetailRow>();
        }

        var snapshotResult = new PositionTradeSnapshot(legsResult, detailRows);
        lock (_cacheLock)
        {
            _detailsCache[cacheKey] = new PositionTradeSnapshot(
                legsResult.Select(item => item.Clone()).ToArray(),
                detailRows);
        }

        return snapshotResult;
    }

    private static string BuildRangeKey(DateTime startUtc, DateTime endUtc)
        => $"{startUtc:O}|{endUtc:O}";

    private StrategyPosition? MapBlockTrade(BlockTradesItem trade)
    {
        if (string.IsNullOrWhiteSpace(trade.UniqueTrade) || !trade.TradeAmount.HasValue)
        {
            return null;
        }

        var unique = trade.UniqueTrade.Trim();
        var side = ResolveSideFromUniqueTrade(unique);
        var instrument = TryExtractInstrument(unique) ?? unique;
        var signedQty = trade.TradeAmount.Value;
        if (!_exchangeService.TryCreateLeg(instrument, signedQty, out var leg))
        {
            // Keep row visible even when uniqueTrade cannot be parsed into option symbol.
            leg = new LegModel
            {
                Type = LegType.Future,
                Size = signedQty,
                Symbol = "ETHUSDT",
                Price = trade.IndexPrice
            };
        }

        leg.Status = LegStatus.New;
        leg.IsIncluded = true;
        leg.Price ??= trade.NetPremium ?? trade.IndexPrice;
        leg.Symbol ??= instrument;

        var timestamp = DateTime.UtcNow;
        var id = BuildStableId(unique);
        var price = trade.NetPremium ?? trade.IndexPrice ?? 0m;
        var sizeUsd = Math.Abs(trade.TradeAmount.Value * (trade.IndexPrice ?? 0m));

        return new StrategyPosition(
            id,
            instrument,
            unique,
            side,
            trade.TradeAmount.Value,
            price,
            price,
            sizeUsd,
            timestamp,
            leg,
            trade.IndexPrice,
            trade.NetPremium,
            trade.NumTrades);
    }

    private static IReadOnlyList<TopTradesItem> SelectRowsForPosition(IReadOnlyList<TopTradesItem> details, StrategyPosition position)
    {
        if (details.Count == 0)
        {
            return Array.Empty<TopTradesItem>();
        }

        var grouped = details
            .GroupBy(item => string.IsNullOrWhiteSpace(item.BlockTradeId) ? $"ts:{item.Date}" : $"id:{item.BlockTradeId}", StringComparer.Ordinal)
            .Select(group => new
            {
                Items = group.ToArray(),
                Time = group
                    .Select(item => TryParseTradeDateUtc(item.Date) ?? DateTime.MinValue)
                    .Max()
            })
            .OrderByDescending(group => group.Time)
            .ToList();

        var targetTrades = position.NumTrades.GetValueOrDefault();
        if (targetTrades <= 0 || targetTrades >= grouped.Count)
        {
            return grouped
                .SelectMany(group => group.Items)
                .ToArray();
        }

        return grouped
            .Take(targetTrades)
            .SelectMany(group => group.Items)
            .ToArray();
    }

    private LegModel? MapDetailToLeg(TopTradesItem trade)
    {
        if (string.IsNullOrWhiteSpace(trade.Instrument)
            || !trade.TradeAmount.HasValue
            || !trade.Price.HasValue)
        {
            return null;
        }

        var sideRaw = trade.AmberdataDirection ?? trade.ExchangeDirection;
        var isSell = string.Equals(sideRaw, "sell", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(sideRaw, "short", StringComparison.OrdinalIgnoreCase);
        var signedQty = isSell ? -Math.Abs(trade.TradeAmount.Value) : Math.Abs(trade.TradeAmount.Value);

        if (!_exchangeService.TryCreateLeg(trade.Instrument, signedQty, out var leg))
        {
            return null;
        }

        leg.Status = LegStatus.New;
        leg.IsIncluded = true;
        leg.Symbol = trade.Instrument;
        // OptionsService payoff expects entry in quote currency (USD-like), while Deribit "price" is in underlying units.
        var mappedPrice = trade.PriceUsd ?? (trade.Price * trade.IndexPrice) ?? trade.Price;
        leg.Price = mappedPrice;
        if (trade.TradeIv.HasValue && trade.TradeIv.Value > 0m)
        {
            leg.ImpliedVolatility = trade.TradeIv.Value <= 3m ? trade.TradeIv.Value * 100m : trade.TradeIv.Value;
        }

        return leg;
    }

    private static PositionTradeDetailRow MapDetailRow(TopTradesItem trade)
    {
        var sideRaw = trade.AmberdataDirection ?? trade.ExchangeDirection;
        var side = string.IsNullOrWhiteSpace(sideRaw) ? "N/A" : sideRaw.Trim().ToLowerInvariant() switch
        {
            "buy" => "Buy",
            "sell" => "Sell",
            "short" => "Sell",
            "long" => "Buy",
            _ => sideRaw.Trim()
        };

        return new PositionTradeDetailRow(
            TryParseTradeDateUtc(trade.Date),
            trade.Instrument ?? string.Empty,
            side,
            trade.TradeAmount ?? 0m,
            trade.TradeIv,
            trade.Price,
            trade.PriceUsd,
            trade.SizeUsd,
            trade.IndexPrice,
            trade.BlockTradeId);
    }

    private static string? TryExtractInstrument(string? uniqueTrade)
    {
        if (string.IsNullOrWhiteSpace(uniqueTrade))
        {
            return null;
        }

        foreach (var token in ExtractUniqueTradeTokens(uniqueTrade))
        {
            var candidate = token.Trim();
            if (candidate.StartsWith("buy ", StringComparison.OrdinalIgnoreCase) || candidate.StartsWith("sell ", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate[4..].Trim();
            }

            if (candidate.Contains('-') && candidate.Contains("ETH", StringComparison.OrdinalIgnoreCase))
            {
                return candidate.ToUpperInvariant();
            }

            if (candidate.StartsWith("ETH-", StringComparison.OrdinalIgnoreCase))
            {
                return candidate.ToUpperInvariant();
            }
        }

        return uniqueTrade.Length <= 120 ? uniqueTrade : uniqueTrade[..120];
    }

    private static IReadOnlyList<string> ExtractUniqueTradeTokens(string uniqueTrade)
    {
        try
        {
            if (uniqueTrade.StartsWith("[", StringComparison.Ordinal))
            {
                var parsed = JsonSerializer.Deserialize<string[]>(uniqueTrade);
                if (parsed is { Length: > 0 })
                {
                    return parsed;
                }
            }
        }
        catch
        {
            // Fallback to split parser.
        }

        return uniqueTrade.Split([';', '|', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    private static string ResolveSideFromUniqueTrade(string uniqueTrade)
    {
        var first = ExtractUniqueTradeTokens(uniqueTrade).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return "N/A";
        }

        var trimmed = first.Trim();
        if (trimmed.StartsWith("buy ", StringComparison.OrdinalIgnoreCase))
        {
            return "Buy";
        }

        if (trimmed.StartsWith("sell ", StringComparison.OrdinalIgnoreCase))
        {
            return "Sell";
        }

        return "N/A";
    }

    private static string BuildStableId(string uniqueTrade)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(uniqueTrade));
        return Convert.ToHexString(bytes[..8]);
    }

    private static DateTime? TryParseTradeDateUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (long.TryParse(value, out var epoch))
        {
            return epoch < 10_000_000_000
                ? DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime
                : DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime;
        }

        if (DateTimeOffset.TryParse(value, out var parsed))
        {
            return parsed.UtcDateTime;
        }

        return null;
    }
}
