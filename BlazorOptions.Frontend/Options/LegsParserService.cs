using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlazorOptions.Diagnostics;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class LegsParserService : ILegsParserService
{
    private const int DefaultMinDaysOut = 7;
    private static readonly Regex QuickAddAtRegex = new(@"@\s*(?<value>\d+(?:\.\d+)?)\s*(?<percent>%?)", RegexOptions.Compiled);
    private static readonly Regex QuickAddDateWithYearRegex = new(@"(?i)\b\d{1,2}[A-Z]{3}\d{2,4}\b", RegexOptions.Compiled);
    private static readonly Regex QuickAddDateWithoutYearRegex = new(@"(?i)\b\d{1,2}[A-Z]{3}\b", RegexOptions.Compiled);
    private static readonly Regex QuickAddNumericDateRegex = new(@"\b\d{4}\b", RegexOptions.Compiled);
    private static readonly Regex QuickAddDayMonthRegex = new(@"\b(?<day>\d{1,2})[./-](?<month>\d{1,2})\b", RegexOptions.Compiled);
    private static readonly string[] PositionExpirationFormats = { "ddMMMyy", "ddMMMyyyy" };

    private readonly OptionsChainService _optionsChainService;
    private readonly IExchangeService _exchangeService;

    public LegsParserService(
        OptionsChainService optionsChainService,
        IExchangeService exchangeService)
    {
        _optionsChainService = optionsChainService;
        _exchangeService = exchangeService;
    }

    public IReadOnlyList<LegModel> ParseLegs(string input, decimal defaultSize, DateTime? defaultExpiration, string? baseAsset)
    {
        using var activity = ActivitySources.Telemetry.StartActivity("LegsParser.ParseLegs");
        var entries = ParseEntries(input);
        if (entries.Count == 0)
        {
            return Array.Empty<LegModel>();
        }

        var results = new List<LegModel>(entries.Count);
        foreach (var entry in entries)
        {
            var normalized = NormalizeEntry(entry, defaultSize, out var hasExpiration);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (TryParseSymbolEntry(normalized, defaultSize, out var symbolLeg))
            {
                if (!hasExpiration && symbolLeg.Type != LegType.Future && defaultExpiration.HasValue)
                {
                    symbolLeg.ExpirationDate = defaultExpiration.Value.Date;
                }

                results.Add(symbolLeg);
                continue;
            }

            var parsed = ParseLeg(normalized, baseAsset);
            if (!hasExpiration && parsed.Type != LegType.Future && defaultExpiration.HasValue)
            {
                parsed.ExpirationDate = defaultExpiration.Value.Date;
            }

            results.Add(parsed);
        }

        return results;
    }

    public async Task ApplyTickerDefaultsAsync(IReadOnlyList<LegModel> legs, string? baseAsset, decimal? underlyingPrice)
    {
        using var activity = ActivitySources.Telemetry.StartActivity("LegsParser.ApplyTickerDefaults");
        if (legs.Count == 0)
        {
            return;
        }

        var resolvedBaseAsset = ResolveBaseAsset(baseAsset, legs);
        if (string.IsNullOrWhiteSpace(resolvedBaseAsset))
        {
            return;
        }

        var tickers = _optionsChainService.GetTickersByBaseAsset(resolvedBaseAsset);
        if (tickers.Count == 0)
        {
            await _optionsChainService.RefreshAsync(resolvedBaseAsset);
            tickers = _optionsChainService.GetTickersByBaseAsset(resolvedBaseAsset);
        }

        var defaultExpiration = ResolveNextExpirationDate(tickers);

        foreach (var leg in legs)
        {
            if (leg.Type == LegType.Future)
            {
                if (!leg.Price.HasValue && underlyingPrice.HasValue)
                {
                    leg.Price = RoundPrice(underlyingPrice.Value);
                }

                continue;
            }

            if (!leg.ExpirationDate.HasValue && defaultExpiration.HasValue)
            {
                leg.ExpirationDate = defaultExpiration.Value.Date;
            }

            if (leg.ExpirationDate.HasValue && leg.Strike.HasValue == false)
            {
                var candidates = tickers
                    .Where(ticker => ticker.Type == leg.Type && ticker.ExpirationDate.Date == leg.ExpirationDate.Value.Date)
                    .ToList();

                if (candidates.Count > 0)
                {
                    leg.Strike = ResolveAtmStrike(candidates, leg.Type, underlyingPrice);
                }
            }

            var match = ResolveTickerMatch(tickers, leg, resolvedBaseAsset);
            if (match is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(leg.Symbol))
            {
                leg.Symbol = match.Symbol;
            }

            if (!leg.Price.HasValue)
            {
                var entryPrice = ResolveEntryPriceFromTicker(match, leg.Size);
                if (entryPrice.HasValue)
                {
                    leg.Price = RoundPrice(entryPrice.Value);
                }
            }

            if (!leg.ImpliedVolatility.HasValue || leg.ImpliedVolatility.Value <= 0)
            {
                leg.ImpliedVolatility = NormalizeIv(match.MarkIv) ?? 0;
            }
        }
    }

    public string BuildPreviewDescription(IEnumerable<LegModel> legs, decimal? underlyingPrice, string? baseAsset)
    {
        using var activity = ActivitySources.Telemetry.StartActivity("LegsParser.BuildPreviewDescription");
        var list = legs.ToList();
        if (list.Count == 0)
        {
            return string.Empty;
        }

        var resolvedBaseAsset = ResolveBaseAsset(baseAsset, list);
        var tickers = string.IsNullOrWhiteSpace(resolvedBaseAsset)
            ? new List<OptionChainTicker>()
            : _optionsChainService.GetTickersByBaseAsset(resolvedBaseAsset);

        var defaultExpiration = ResolveNextExpirationDate(tickers) ?? DateTime.UtcNow.Date.AddDays(DefaultMinDaysOut);
        var descriptions = new List<string>(list.Count);

        foreach (var leg in list)
        {
            var direction = leg.Size >= 0 ? "Buy" : "Sell";
            var absSize = Math.Abs(leg.Size);
            var typeLabel = leg.Type switch
            {
                LegType.Call => "CALL",
                LegType.Put => "PUT",
                _ => "FUTURE"
            };

            var pieces = new List<string> { $"{direction} {absSize:0.##} {typeLabel}" };

            if (leg.Type != LegType.Future)
            {
                var expiration = leg.ExpirationDate ?? defaultExpiration;
                var dte = (expiration.Date - DateTime.UtcNow.Date).Days;
                var strike = leg.Strike;
                if (!strike.HasValue)
                {
                    var candidates = tickers
                        .Where(ticker => ticker.Type == leg.Type && ticker.ExpirationDate.Date == expiration.Date)
                        .ToList();

                    if (candidates.Count > 0)
                    {
                        strike = ResolveAtmStrike(candidates, leg.Type, underlyingPrice);
                    }
                }

                pieces.Add($"{FormatStrike(strike)}, {expiration:dd.MM.yy} ({dte} DTE)");

                var price = leg.Price;
                if (!price.HasValue)
                {
                    var match = ResolveTickerMatch(tickers, leg, resolvedBaseAsset);
                    price = match is null ? null : ResolveEntryPriceFromTicker(match, leg.Size);
                }

                pieces.Add($"@ {FormatPrice(price)}");
            }
            else if (!string.IsNullOrWhiteSpace(leg.Symbol))
            {
                pieces.Add(leg.Symbol.ToUpperInvariant());
            }

            if (leg.Type == LegType.Future)
            {
                var displayPrice = leg.Price ?? underlyingPrice;
                pieces.Add($"@ {FormatPrice(displayPrice)}");
            }

            var description = string.Join(" ", pieces.Where(item => !string.IsNullOrWhiteSpace(item)));
            if (!string.IsNullOrWhiteSpace(description))
            {
                descriptions.Add(description);
            }
        }

        return string.Join(" | ", descriptions);
    }

    private LegModel ParseLeg(string input, string? baseAsset)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new LegsParseException("Enter a leg expression like '+1 C 3400' or '+1 P'.");
        }

        var trimmed = input.Trim();
        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            throw new LegsParseException("Enter a leg expression like '+1 C 3400' or '+1 P'.");
        }

        var size = 1.0m;
        var tokenIndex = 0;
        if (tokens.Length > 0 && TryParseDecimal(tokens[0], out var parsedSize))
        {
            size = parsedSize;
            tokenIndex = 1;
        }

        if (Math.Abs(size) < 0.0001m)
        {
            throw new LegsParseException("Leg size must be non-zero.");
        }

        var symbolHint = ExtractSymbolToken(tokens);
        var parsedBaseAsset = string.Empty;
        var parsedSymbolExpiration = default(DateTime);
        var parsedSymbolStrike = 0.0m;
        var parsedSymbolType = LegType.Call;
        var hasParsedSymbol = !string.IsNullOrWhiteSpace(symbolHint)
            && _exchangeService.TryParseSymbol(symbolHint, out parsedBaseAsset, out parsedSymbolExpiration, out parsedSymbolStrike, out parsedSymbolType);

        if (!TryFindLegType(tokens, tokenIndex, out var type, out var typeIndex))
        {
            if (hasParsedSymbol)
            {
                type = parsedSymbolType;
                typeIndex = -1;
            }
            else if (!string.IsNullOrWhiteSpace(symbolHint))
            {
                type = LegType.Future;
                typeIndex = -1;
            }
            else if (tokens.Length == 1 && TryParseDecimal(tokens[0], out _))
            {
                type = LegType.Future;
                typeIndex = -1;
            }
            else
            {
                throw new LegsParseException("Specify a leg type (CALL, PUT, or FUTURE).");
            }
        }

        var strike = typeIndex >= 0 ? TryParseStrike(tokens, typeIndex) : null;
        if (type == LegType.Future && !strike.HasValue)
        {
            strike = TryParseFirstNumber(tokens, tokenIndex);
        }

        var expirationParsed = TryParseExpirationDate(trimmed, out var parsedExpiration);
        var expiration = expirationParsed
            ? parsedExpiration
            : (DateTime?)null;
        if (hasParsedSymbol)
        {
            strike ??= parsedSymbolStrike;
            expiration ??= parsedSymbolExpiration;
        }

        var (priceOverride, ivOverride) = ParsePriceOverrides(trimmed);
        if (type != LegType.Future && !strike.HasValue && priceOverride.HasValue)
        {
            strike = priceOverride;
            priceOverride = null;
        }

        var leg = new LegModel
        {
            Type = type,
            Strike = strike,
            ExpirationDate = expiration?.Date,
            Size = size,
            Price = priceOverride,
            ImpliedVolatility = ivOverride
        };

        if (!string.IsNullOrWhiteSpace(symbolHint) && (hasParsedSymbol || type == LegType.Future))
        {
            leg.Symbol = symbolHint.Trim().ToUpperInvariant();
        }

        return leg;
    }

    private static IReadOnlyList<string> ParseEntries(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Array.Empty<string>();
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal) && TryParseJsonArray(trimmed, out var jsonItems))
        {
            return jsonItems;
        }

        if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..^1];
        }

        return SplitLegEntries(trimmed);
    }

    private static string NormalizeEntry(string entry, decimal defaultSize, out bool hasExpiration)
    {
        var trimmed = entry.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            hasExpiration = false;
            return string.Empty;
        }

        hasExpiration = HasExpirationToken(trimmed);
        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            hasExpiration = false;
            return string.Empty;
        }

        var action = tokens[0].Trim();
        var isBuy = action.Equals("buy", StringComparison.OrdinalIgnoreCase);
        var isSell = action.Equals("sell", StringComparison.OrdinalIgnoreCase);
        if (!isBuy && !isSell)
        {
            if (TryParseDecimal(tokens[0], out _))
            {
                return trimmed;
            }

            return $"{defaultSize.ToString(CultureInfo.InvariantCulture)} {trimmed}";
        }

        var sign = isSell ? -1m : 1m;
        var tokenIndex = 1;
        var size = defaultSize;

        if (tokens.Length > 1 && TryParseDecimal(tokens[1], out var parsed))
        {
            size = Math.Abs(parsed);
            tokenIndex = 2;
        }

        if (tokenIndex >= tokens.Length)
        {
            return string.Empty;
        }

        var remaining = string.Join(' ', tokens.Skip(tokenIndex));
        return $"{(sign * size).ToString(CultureInfo.InvariantCulture)} {remaining}";
    }

    private bool TryParseSymbolEntry(string input, decimal defaultSize, out LegModel leg)
    {
        leg = new LegModel();
        var tokens = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var tokenIndex = 0;
        var sign = 1.0m;
        var size = defaultSize;
        var first = tokens[tokenIndex].Trim();

        if (first.Equals("buy", StringComparison.OrdinalIgnoreCase) || first.Equals("sell", StringComparison.OrdinalIgnoreCase))
        {
            sign = first.Equals("sell", StringComparison.OrdinalIgnoreCase) ? -1m : 1m;
            tokenIndex++;
        }

        if (tokenIndex < tokens.Length && TryParseDecimal(tokens[tokenIndex], out var parsedSize))
        {
            size = Math.Abs(parsedSize);
            if (parsedSize < 0)
            {
                sign = -1m;
            }

            tokenIndex++;
        }

        if (tokenIndex >= tokens.Length)
        {
            return false;
        }

        var symbol = string.Join(' ', tokens.Skip(tokenIndex)).Trim();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        if (!_exchangeService.TryParseSymbol(symbol, out _, out var expiration, out var strike, out var type))
        {
            return false;
        }

        leg = new LegModel
        {
            Type = type,
            Strike = strike,
            ExpirationDate = expiration,
            Size = sign * size,
            Symbol = symbol.Trim().ToUpperInvariant()
        };

        return true;
    }

    private static bool HasExpirationToken(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        return TryParseExpirationDate(input, out _);
    }

    private static bool TryParseDecimal(string token, out decimal value)
    {
        var cleaned = token.Trim().Trim(',', ';');
        return decimal.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static decimal? TryParseFirstNumber(string[] tokens, int startIndex)
    {
        for (var i = startIndex; i < tokens.Length; i++)
        {
            if (TryParseDecimal(tokens[i], out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryParseExpirationDate(string input, out DateTime expirationDate)
    {
        var dayMonthMatch = QuickAddDayMonthRegex.Match(input);
        if (dayMonthMatch.Success)
        {
            var dayToken = dayMonthMatch.Groups["day"].Value;
            var monthToken = dayMonthMatch.Groups["month"].Value;
            if (int.TryParse(dayToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day)
                && int.TryParse(monthToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var month))
            {
                if (month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(DateTime.UtcNow.Year, month))
                {
                    expirationDate = BuildDateFromMonthDay(month, day);
                    return true;
                }
            }
        }

        var match = QuickAddDateWithYearRegex.Match(input);
        if (match.Success)
        {
            if (DateTime.TryParseExact(match.Value.ToUpperInvariant(), PositionExpirationFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                expirationDate = parsed.Date;
                return true;
            }
        }

        match = QuickAddDateWithoutYearRegex.Match(input);
        if (match.Success && DateTime.TryParseExact(match.Value.ToUpperInvariant(), "ddMMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedWithoutYear))
        {
            var candidate = BuildDateFromMonthDay(parsedWithoutYear.Month, parsedWithoutYear.Day);
            expirationDate = candidate;
            return true;
        }

        match = QuickAddNumericDateRegex.Match(input);
        if (match.Success && match.Value.Length == 4)
        {
            var day = int.Parse(match.Value[..2], CultureInfo.InvariantCulture);
            var month = int.Parse(match.Value.Substring(2, 2), CultureInfo.InvariantCulture);
            if (month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(DateTime.UtcNow.Year, month))
            {
                expirationDate = BuildDateFromMonthDay(month, day);
                return true;
            }
        }

        expirationDate = default;
        return false;
    }

    private static string? ExtractSymbolToken(string[] tokens)
    {
        for (var i = tokens.Length - 1; i >= 0; i--)
        {
            var token = tokens[i].Trim().Trim(',', ';');
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (token.StartsWith("@", StringComparison.Ordinal))
            {
                continue;
            }

            var upper = token.ToUpperInvariant();
            if (IsLegTypeToken(upper))
            {
                continue;
            }

            if (TryParseDecimal(token, out _))
            {
                continue;
            }

            if (IsDateToken(upper))
            {
                continue;
            }

            return token;
        }

        return null;
    }

    private static bool IsLegTypeToken(string token)
    {
        return token is "C" or "CALL" or "CALLS"
            or "P" or "PUT" or "PUTS"
            or "F" or "FUT" or "FUTURE" or "FUTURES";
    }

    private static bool IsDateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (QuickAddDayMonthRegex.IsMatch(token) || QuickAddDateWithYearRegex.IsMatch(token) || QuickAddDateWithoutYearRegex.IsMatch(token))
        {
            return true;
        }

        if (!QuickAddNumericDateRegex.IsMatch(token))
        {
            return false;
        }

        if (token.Length != 4)
        {
            return false;
        }

        if (!int.TryParse(token[..2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var day))
        {
            return false;
        }

        if (!int.TryParse(token.Substring(2, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var month))
        {
            return false;
        }

        return month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(DateTime.UtcNow.Year, month);
    }

    private static bool TryFindLegType(string[] tokens, int startIndex, out LegType type, out int typeIndex)
    {
        for (var i = startIndex; i < tokens.Length; i++)
        {
            var normalized = tokens[i].Trim().Trim(',', ';').ToUpperInvariant();
            switch (normalized)
            {
                case "F":
                case "FUT":
                case "FUTURE":
                case "FUTURES":
                    type = LegType.Future;
                    typeIndex = i;
                    return true;
                case "C":
                case "CALL":
                case "CALLS":
                    type = LegType.Call;
                    typeIndex = i;
                    return true;
                case "P":
                case "PUT":
                case "PUTS":
                    type = LegType.Put;
                    typeIndex = i;
                    return true;
            }
        }

        type = LegType.Call;
        typeIndex = -1;
        return false;
    }

    private static decimal? TryParseStrike(string[] tokens, int typeIndex)
    {
        for (var i = typeIndex + 1; i < tokens.Length; i++)
        {
            var token = tokens[i].Trim().Trim(',', ';');
            if (string.Equals(token, "@", StringComparison.Ordinal) || token.StartsWith("@", StringComparison.Ordinal))
            {
                break;
            }

            var upper = token.ToUpperInvariant();
            if (IsDateToken(upper))
            {
                continue;
            }

            if (TryParseDecimal(token, out var strike))
            {
                return strike;
            }
        }

        return null;
    }

    private static (decimal? price, decimal? iv) ParsePriceOverrides(string input)
    {
        var match = QuickAddAtRegex.Match(input);
        if (!match.Success)
        {
            return (null, null);
        }

        if (!decimal.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return (null, null);
        }

        return match.Groups["percent"].Value == "%"
            ? (null, value)
            : (value, null);
    }

    private static DateTime BuildDateFromMonthDay(int month, int day)
    {
        var today = DateTime.UtcNow.Date;
        var daysInMonth = DateTime.DaysInMonth(today.Year, month);
        var safeDay = Math.Min(day, daysInMonth);
        var candidate = new DateTime(today.Year, month, safeDay);
        if (candidate < today)
        {
            candidate = candidate.AddYears(1);
        }

        return candidate;
    }

    private static bool TryParseJsonArray(string input, out List<string> items)
    {
        items = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(input);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        items.Add(value.Trim());
                    }
                }
            }

            return items.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> SplitLegEntries(string input)
    {
        var results = new List<string>();
        var builder = new StringBuilder(input.Length);
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                builder.Append(ch);
                continue;
            }

            if (!inQuotes && (ch == ',' || ch == ';' || ch == '\n'))
            {
                AddEntry(results, builder);
                continue;
            }

            if (ch != '\r')
            {
                builder.Append(ch);
            }
        }

        AddEntry(results, builder);
        return results;
    }

    private static void AddEntry(ICollection<string> results, StringBuilder builder)
    {
        if (builder.Length == 0)
        {
            return;
        }

        var value = builder.ToString().Trim();
        builder.Clear();

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1].Trim();
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            results.Add(value);
        }
    }

    private string ResolveBaseAsset(string? baseAsset, IReadOnlyList<LegModel> legs)
    {
        if (!string.IsNullOrWhiteSpace(baseAsset))
        {
            return baseAsset.Trim().ToUpperInvariant();
        }

        foreach (var leg in legs)
        {
            if (string.IsNullOrWhiteSpace(leg.Symbol))
            {
                continue;
            }

            if (_exchangeService.TryParseSymbol(leg.Symbol, out var parsedBase, out _, out _, out _))
            {
                return parsedBase;
            }
        }

        return string.Empty;
    }

    private static DateTime? ResolveNextExpirationDate(IReadOnlyList<OptionChainTicker> tickers)
    {
        if (tickers.Count == 0)
        {
            return null;
        }

        var minDate = DateTime.UtcNow.Date.AddDays(DefaultMinDaysOut);
        var expirations = tickers
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

    private static decimal ResolveAtmStrike(IReadOnlyList<OptionChainTicker> candidates, LegType type, decimal? underlyingPrice)
    {
        var referencePrice = underlyingPrice;
        if (!referencePrice.HasValue)
        {
            referencePrice = candidates.Average(ticker => ticker.Strike);
        }

        var orderedStrikes = candidates
            .Select(ticker => ticker.Strike)
            .Distinct()
            .OrderBy(strike => strike)
            .ToList();

        if (orderedStrikes.Count == 0)
        {
            return referencePrice ?? 0m;
        }

        if (type == LegType.Call)
        {
            var above = orderedStrikes.FirstOrDefault(strike => strike >= referencePrice.Value);
            return above > 0 ? above : orderedStrikes.Last();
        }

        if (type == LegType.Put)
        {
            var below = orderedStrikes.LastOrDefault(strike => strike <= referencePrice.Value);
            return below > 0 ? below : orderedStrikes.First();
        }

        return orderedStrikes
            .OrderBy(strike => Math.Abs(strike - referencePrice.Value))
            .First();
    }

    private static OptionChainTicker? ResolveTickerMatch(IReadOnlyList<OptionChainTicker> tickers, LegModel leg, string baseAsset)
    {
        if (!string.IsNullOrWhiteSpace(leg.Symbol))
        {
            var symbolMatch = tickers.FirstOrDefault(ticker => string.Equals(ticker.Symbol, leg.Symbol, StringComparison.OrdinalIgnoreCase));
            if (symbolMatch is not null)
            {
                return symbolMatch;
            }
        }

        if (!leg.ExpirationDate.HasValue || !leg.Strike.HasValue)
        {
            return null;
        }

        return tickers.FirstOrDefault(ticker =>
            string.Equals(ticker.BaseAsset, baseAsset, StringComparison.OrdinalIgnoreCase)
            && ticker.Type == leg.Type
            && ticker.ExpirationDate.Date == leg.ExpirationDate.Value.Date
            && Math.Abs(ticker.Strike - leg.Strike.Value) < 0.01m);
    }

    private static decimal? ResolveEntryPriceFromTicker(OptionChainTicker ticker, decimal size)
    {
        if (size >= 0)
        {
            if (ticker.AskPrice > 0)
            {
                return ticker.AskPrice;
            }

            if (ticker.MarkPrice > 0)
            {
                return ticker.MarkPrice;
            }

            if (ticker.BidPrice > 0)
            {
                return ticker.BidPrice;
            }

            return null;
        }

        if (ticker.BidPrice > 0)
        {
            return ticker.BidPrice;
        }

        if (ticker.MarkPrice > 0)
        {
            return ticker.MarkPrice;
        }

        if (ticker.AskPrice > 0)
        {
            return ticker.AskPrice;
        }

        return null;
    }

    private static decimal? NormalizeIv(decimal? value)
    {
        if (!value.HasValue || value.Value <= 0)
        {
            return null;
        }

        return value.Value <= 3 ? value.Value * 100 : value.Value;
    }

    private static decimal RoundPrice(decimal value)
    {
        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    private static string FormatPrice(decimal? value)
    {
        return value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : "?";
    }

    private static string FormatStrike(decimal? value)
    {
        return value.HasValue ? Math.Round(value.Value).ToString("0", CultureInfo.InvariantCulture) : "?";
    }

    public sealed class LegsParseException : Exception
    {
        public LegsParseException(string message) : base(message)
        {
        }
    }
}
