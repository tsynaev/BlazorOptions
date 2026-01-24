using System.Globalization;
using System.Text.RegularExpressions;
using BlazorOptions.Services;
using Microsoft.AspNetCore.Components.Web;

namespace BlazorOptions.ViewModels;

public sealed class QuickAddViewModel
{
    private readonly INotifyUserService _context;
    private readonly OptionsChainService _optionsChainService;
    private static readonly Regex QuickAddAtRegex = new(@"@\s*(?<value>\d+(?:\.\d+)?)\s*(?<percent>%?)", RegexOptions.Compiled);
    private static readonly Regex QuickAddDateWithYearRegex = new(@"(?i)\b\d{1,2}[A-Z]{3}\d{2,4}\b", RegexOptions.Compiled);
    private static readonly Regex QuickAddDateWithoutYearRegex = new(@"(?i)\b\d{1,2}[A-Z]{3}\b", RegexOptions.Compiled);
    private static readonly Regex QuickAddNumericDateRegex = new(@"\b\d{4}\b", RegexOptions.Compiled);
    private static readonly Regex QuickAddDayMonthRegex = new(@"\b(?<day>\d{1,2})[./-](?<month>\d{1,2})\b", RegexOptions.Compiled);
    private static readonly string[] PositionExpirationFormats = { "ddMMMyy", "ddMMMyyyy" };

    public string QuickLegInput { get; set; } = string.Empty;

    public string AddActionDescription => BuildActionDescription(QuickLegInput);

    public double? Price { get; set; }

    public string? BaseAsset { get; set; }
    public LegsCollectionModel? Collection { get; set; }

    public event Func<LegModel,Task>? LegCreated;

    public QuickAddViewModel(INotifyUserService context, OptionsChainService optionsChainService)
    {
        _context = context;
        _optionsChainService = optionsChainService;
    }

   

 

    public async Task OnQuickLegKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            await AddQuickLegAsync();
        }
    }

    public async Task AddQuickLegAsync()
    {
        var leg = await AddLegFromTextWithResultAsync(QuickLegInput);
        if (leg is not null)
        {
            QuickLegInput = string.Empty;
        }
    }

    public Task<IEnumerable<string>> SearchQuickLegSymbolsAsync(string value, CancellationToken cancellationToken)
    {
        var suggestions = GetQuickAddSymbolSuggestions(value);
        return Task.FromResult<IEnumerable<string>>(suggestions);
    }

    public async Task<LegModel?> AddLegFromTextWithResultAsync(string? input)
    {
        var collection = Collection;
        if (collection is null)
        {
            _context.NotifyUser("Select a position before adding a leg.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            _context.NotifyUser("Enter a leg expression like '+1 C 3400' or '+1 P'.");
            return null;
        }

        var trimmed = input.Trim();
        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var size = 1.0;
        var tokenIndex = 0;

        if (tokens.Length > 0 && TryParseNumber(tokens[0], out var parsedSize))
        {
            size = parsedSize;
            tokenIndex = 1;
        }

        if (Math.Abs(size) < 0.0001)
        {
            _context.NotifyUser("Leg size must be non-zero.");
            return null;
        }

        var symbolHint = ExtractSymbolToken(tokens);
        var parsedBaseAsset = string.Empty;
        var parsedSymbolExpiration = default(DateTime);
        var parsedSymbolStrike = 0.0;
        var parsedSymbolType = LegType.Call;
        var hasParsedSymbol = !string.IsNullOrWhiteSpace(symbolHint)
            && TryParsePositionSymbol(symbolHint, out parsedBaseAsset, out parsedSymbolExpiration, out parsedSymbolStrike, out parsedSymbolType);

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
            else if (tokens.Length == 1 && TryParseNumber(tokens[0], out _))
            {
                type = LegType.Future;
                typeIndex = -1;
            }
            else
            {
                _context.NotifyUser("Specify a leg type (CALL, PUT, or FUTURE).");
                return null;
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

        if (type == LegType.Future)
        {
            var entryPrice = priceOverride ?? Price;

            var futureLeg = new LegModel
            {
                Type = LegType.Future,
                Strike = null,
                ExpirationDate = expiration?.Date,
                Size = size,
                Price = entryPrice.HasValue ? RoundPrice(entryPrice.Value) : null,
                ImpliedVolatility = null,
                Symbol = string.IsNullOrWhiteSpace(symbolHint) ? null : symbolHint
            };

            collection.Legs.Add(futureLeg);
            await (LegCreated?.Invoke(futureLeg) ?? Task.CompletedTask);
            return futureLeg;
        }

        var baseAsset = hasParsedSymbol ? parsedBaseAsset : BaseAsset;
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            _context.NotifyUser("The position base asset is missing.");
            return null;
        }

        var chainTickers = await LoadChainTickersAsync(baseAsset);
        if (chainTickers.Count == 0)
        {
            _context.NotifyUser("Option chain is unavailable. Please refresh and try again.");
            return null;
        }

        var expirationDate = expiration.HasValue
            ? ResolveNextExpirationDate(chainTickers, type, expiration.Value.Date)
            : ResolveDefaultExpirationDate();
        if (!string.IsNullOrWhiteSpace(symbolHint))
        {
            var symbolMatch = chainTickers.FirstOrDefault(ticker => string.Equals(ticker.Symbol, symbolHint, StringComparison.OrdinalIgnoreCase));
            if (symbolMatch is not null)
            {
                var resolvedPrice = priceOverride ?? ResolveEntryPriceFromTicker(symbolMatch, size);
                var symbolLeg = new LegModel
                {
                    Type = symbolMatch.Type,
                    Strike = symbolMatch.Strike,
                    ExpirationDate = symbolMatch.ExpirationDate.Date,
                    Size = size,
                    Price = resolvedPrice.HasValue ? RoundPrice(resolvedPrice.Value) : null,
                    ImpliedVolatility = ivOverride ?? 0,
                    Symbol = symbolMatch.Symbol
                };

                collection.Legs.Add(symbolLeg);
                await (LegCreated?.Invoke(symbolLeg) ?? Task.CompletedTask);
                return symbolLeg;
            }
        }

        var candidates = chainTickers
            .Where(ticker => ticker.Type == type && ticker.ExpirationDate.Date == expirationDate.Date)
            .ToList();

        if (candidates.Count == 0)
        {
            _context.NotifyUser($"No contracts found for {type} expiring on {expirationDate:ddMMMyy}.");
            return null;
        }

        var resolvedStrike = strike ?? ResolveAtmStrike(candidates, type, expirationParsed);
        var matchingTickers = candidates
            .Where(ticker => Math.Abs(ticker.Strike - resolvedStrike) < 0.01)
            .ToList();

        if (matchingTickers.Count != 1)
        {
            _context.NotifyUser("Ambiguous contract selection. Please refine the input.");
            return null;
        }

        var tickerMatch = matchingTickers[0];
        var defaultPrice = priceOverride ?? ResolveEntryPriceFromTicker(tickerMatch, size);
        var leg = new LegModel
        {
            Type = type,
            Strike = tickerMatch.Strike,
            ExpirationDate = expirationDate.Date,
            Size = size,
            Price = defaultPrice.HasValue ? RoundPrice(defaultPrice.Value) : null,
            ImpliedVolatility = ivOverride ?? 0
        };

        collection.Legs.Add(leg);
        await (LegCreated?.Invoke(leg) ?? Task.CompletedTask);
        return leg;
    }

    public IReadOnlyList<string> GetQuickAddSymbolSuggestions(string? input)
    {
        var query = ExtractSymbolQuery(input);
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<string>();
        }

        var snapshot = _optionsChainService.GetSnapshot();
        var symbols = snapshot
            .Select(ticker => ticker.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(symbol => symbol.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var ordered = symbols
            .OrderBy(symbol => symbol.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(symbol => symbol, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        return ordered;
    }

    private async Task<IReadOnlyList<OptionChainTicker>> LoadChainTickersAsync(string baseAsset)
    {
        var snapshot = _optionsChainService.GetSnapshot()
            .Where(ticker => string.Equals(ticker.BaseAsset, baseAsset, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (snapshot.Count > 0)
        {
            return snapshot;
        }

        await _optionsChainService.RefreshAsync(baseAsset);
        return _optionsChainService.GetSnapshot()
            .Where(ticker => string.Equals(ticker.BaseAsset, baseAsset, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private double ResolveAtmStrike(IReadOnlyList<OptionChainTicker> candidates, LegType type, bool preferAtm)
    {
        var referencePrice = Price;
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
            return referencePrice.Value;
        }

        if (!preferAtm && type == LegType.Call)
        {
            var above = orderedStrikes.FirstOrDefault(strike => strike >= referencePrice.Value);
            return above > 0 ? above : orderedStrikes.Last();
        }

        if (!preferAtm && type == LegType.Put)
        {
            var below = orderedStrikes.LastOrDefault(strike => strike <= referencePrice.Value);
            return below > 0 ? below : orderedStrikes.First();
        }

        return orderedStrikes
            .OrderBy(strike => Math.Abs(strike - referencePrice.Value))
            .First();
    }

    private DateTime ResolveDefaultExpirationDate()
    {
        var collection = Collection;
        if (collection?.Legs.Count > 0)
        {
            var lastWithExpiration = collection.Legs.LastOrDefault(leg => leg.ExpirationDate.HasValue);
            if (lastWithExpiration?.ExpirationDate.HasValue ?? false)
            {
                return lastWithExpiration.ExpirationDate.Value.Date;
            }
        }

        return GetNextMonthEndDate(DateTime.UtcNow.Date, 7);
    }

    private static DateTime GetNextMonthEndDate(DateTime reference, int minDaysOut)
    {
        var target = reference;
        while (true)
        {
            var candidate = new DateTime(target.Year, target.Month, DateTime.DaysInMonth(target.Year, target.Month));
            if ((candidate - reference.Date).TotalDays > minDaysOut)
            {
                return candidate;
            }

            target = target.AddMonths(1);
        }
    }

    private static bool TryParseNumber(string token, out double value)
    {
        var cleaned = token.Trim().Trim(',', ';');
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static double? TryParseFirstNumber(string[] tokens, int startIndex)
    {
        for (var i = startIndex; i < tokens.Length; i++)
        {
            if (TryParseNumber(tokens[i], out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsLegTypeToken(string token)
    {
        return token is "C" or "CALL" or "CALLS"
            or "P" or "PUT" or "PUTS"
            or "F" or "FUT" or "FUTURE" or "FUTURES";
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

            if (TryParseNumber(token, out _))
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

    private static string? ExtractSymbolQuery(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var tokens = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ExtractSymbolToken(tokens);
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

    private static double? TryParseStrike(string[] tokens, int typeIndex)
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

            if (TryParseNumber(token, out var strike))
            {
                return strike;
            }
        }

        return null;
    }

    private static (double? price, double? iv) ParsePriceOverrides(string input)
    {
        var match = QuickAddAtRegex.Match(input);
        if (!match.Success)
        {
            return (null, null);
        }

        if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return (null, null);
        }

        return match.Groups["percent"].Value == "%"
            ? (null, value)
            : (value, null);
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
            if (DateTime.TryParseExact(match.Value.ToUpperInvariant(), new[] { "ddMMMyy", "ddMMMyyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
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

    private static double RoundPrice(double value)
    {
        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    private static bool TryParsePositionSymbol(string symbol, out string baseAsset, out DateTime expiration, out double strike, out LegType type)
    {
        baseAsset = string.Empty;
        expiration = default;
        strike = 0;
        type = LegType.Call;

        var parts = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
        {
            return false;
        }

        baseAsset = parts[0];
        if (!DateTime.TryParseExact(parts[1], PositionExpirationFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedExpiration))
        {
            return false;
        }

        expiration = parsedExpiration.Date;

        if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out strike))
        {
            return false;
        }

        var typeToken = parts[3].Trim();
        type = typeToken.Equals("P", StringComparison.OrdinalIgnoreCase) ? LegType.Put : LegType.Call;
        return true;
    }

    private string BuildActionDescription(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var collection = Collection;
        if (collection is null)
        {
            return "Select a position before adding a leg.";
        }

        var trimmed = input.Trim();
        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var size = 1.0;
        var tokenIndex = 0;

        if (tokens.Length > 0 && TryParseNumber(tokens[0], out var parsedSize))
        {
            size = parsedSize;
            tokenIndex = 1;
        }

        if (Math.Abs(size) < 0.0001)
        {
            return "Leg size must be non-zero.";
        }

        var symbolHint = ExtractSymbolToken(tokens);
        var parsedBaseAsset = string.Empty;
        var parsedSymbolExpiration = default(DateTime);
        var parsedSymbolStrike = 0.0;
        var parsedSymbolType = LegType.Call;
        var hasParsedSymbol = !string.IsNullOrWhiteSpace(symbolHint)
            && TryParsePositionSymbol(symbolHint, out parsedBaseAsset, out parsedSymbolExpiration, out parsedSymbolStrike, out parsedSymbolType);

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
            else if (tokens.Length == 1 && TryParseNumber(tokens[0], out _))
            {
                type = LegType.Future;
                typeIndex = -1;
            }
            else
            {
                return "Specify a leg type (CALL, PUT, or FUTURE).";
            }
        }

        var strike = typeIndex >= 0 ? TryParseStrike(tokens, typeIndex) : null;
        var expirationParsed = TryParseExpirationDate(trimmed, out var parsedExpiration);
        var expiration = expirationParsed
            ? parsedExpiration
            : (DateTime?)null;

        if (hasParsedSymbol)
        {
            if (!strike.HasValue)
            {
                strike = parsedSymbolStrike;
            }

            if (!expiration.HasValue)
            {
                expiration = parsedSymbolExpiration;
            }
        }

        var (priceOverride, _) = ParsePriceOverrides(trimmed);
        var direction = size > 0 ? "Buy" : "Sell";
        var absSize = Math.Abs(size);
        var typeLabel = type switch
        {
            LegType.Call => "CALL",
            LegType.Put => "PUT",
            _ => "FUTURE"
        };

        var pieces = new List<string> { $"{direction} {absSize:0.##} {typeLabel}" };

        if (type != LegType.Future)
        {
            var resolvedExpiration = expiration.HasValue
                ? ResolveNextExpirationDateFromSnapshot(type, expiration.Value.Date)
                : ResolveDefaultExpirationDate();
            var dte = (resolvedExpiration.Date - DateTime.UtcNow.Date).Days;
            var resolvedStrike = strike ?? ResolveAtmStrikeFromSnapshot(type, resolvedExpiration.Date, expirationParsed);
            var resolvedPrice = ResolveOptionPriceFromSnapshot(type, resolvedExpiration.Date, resolvedStrike, size);
            pieces.Add($"{FormatStrike(resolvedStrike)}, {resolvedExpiration:dd.MM.yy} ({dte} DTE)");

            var displayValue = priceOverride ?? resolvedPrice;
            pieces.Add($"@ {FormatPrice(displayValue)}");
        }
        else if (!string.IsNullOrWhiteSpace(symbolHint))
        {
            pieces.Add(symbolHint.ToUpperInvariant());
        }

        if (type == LegType.Future)
        {
            var displayValue = priceOverride ?? Price;
            pieces.Add($"@ {FormatPrice(displayValue)}");
        }

        return string.Join(" ", pieces.Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private double? ResolveAtmStrikeFromSnapshot(LegType type, DateTime expirationDate, bool preferAtm)
    {
        var baseAsset = BaseAsset;
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return null;
        }

        var candidates = _optionsChainService.GetSnapshot()
            .Where(ticker => string.Equals(ticker.BaseAsset, baseAsset, StringComparison.OrdinalIgnoreCase)
                             && ticker.Type == type
                             && ticker.ExpirationDate.Date == expirationDate.Date)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        return ResolveAtmStrike(candidates, type, preferAtm);
    }

    private static DateTime ResolveNextExpirationDate(IReadOnlyList<OptionChainTicker> chainTickers, LegType type, DateTime requestedDate)
    {
        var expirations = chainTickers
            .Where(ticker => ticker.Type == type)
            .Select(ticker => ticker.ExpirationDate.Date)
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        if (expirations.Count == 0)
        {
            return requestedDate;
        }

        var next = expirations.FirstOrDefault(date => date >= requestedDate);
        return next != default ? next : expirations.Last();
    }

    private DateTime ResolveNextExpirationDateFromSnapshot(LegType type, DateTime requestedDate)
    {
        var baseAsset = BaseAsset;
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return requestedDate;
        }

        var expirations = _optionsChainService.GetSnapshot()
            .Where(ticker => string.Equals(ticker.BaseAsset, baseAsset, StringComparison.OrdinalIgnoreCase)
                             && ticker.Type == type)
            .Select(ticker => ticker.ExpirationDate.Date)
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        if (expirations.Count == 0)
        {
            return requestedDate;
        }

        var next = expirations.FirstOrDefault(date => date >= requestedDate);
        return next != default ? next : expirations.Last();
    }

    private double? ResolveOptionPriceFromSnapshot(LegType type, DateTime expirationDate, double? strike, double size)
    {
        if (!strike.HasValue)
        {
            return null;
        }

        var baseAsset = BaseAsset;
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return null;
        }

        var match = _optionsChainService.GetSnapshot()
            .FirstOrDefault(ticker => string.Equals(ticker.BaseAsset, baseAsset, StringComparison.OrdinalIgnoreCase)
                                      && ticker.Type == type
                                      && ticker.ExpirationDate.Date == expirationDate.Date
                                      && Math.Abs(ticker.Strike - strike.Value) < 0.01);

        if (match is null)
        {
            return null;
        }

        return ResolveEntryPriceFromTicker(match, size);
    }

    private static double? ResolveEntryPriceFromTicker(OptionChainTicker ticker, double size)
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

    private static string FormatPrice(double? value)
    {
        return value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : "?";
    }

    private static string FormatStrike(double? value)
    {
        return value.HasValue ? Math.Round(value.Value).ToString("0", CultureInfo.InvariantCulture) : "?";
    }
}
