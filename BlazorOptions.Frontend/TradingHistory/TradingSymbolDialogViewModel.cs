using System.Text.Json;
using System.Text;
using System.Globalization;
using BlazorOptions.API.TradingHistory;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class TradingSymbolDialogViewModel : Bindable
{
    private const string UnauthorizedMessage = "Sign in to view trading history.";
    private readonly ITradingHistoryPort _tradingHistoryPort;
    private IReadOnlyList<TradingHistoryEntry> _trades = Array.Empty<TradingHistoryEntry>();
    private string _rawJson = string.Empty;
    private bool _isLoading;
    private string? _errorMessage;
    private string _symbol = string.Empty;
    private string _category = string.Empty;
    private DateTime? _sinceDate;

    public TradingSymbolDialogViewModel(ITradingHistoryPort tradingHistoryPort)
    {
        _tradingHistoryPort = tradingHistoryPort;
    }

    public string Symbol
    {
        get => _symbol;
        private set => SetField(ref _symbol, value);
    }

    public string Category
    {
        get => _category;
        private set => SetField(ref _category, value);
    }

    public DateTime? SinceDate
    {
        get => _sinceDate;
        private set => SetField(ref _sinceDate, value);
    }

    public IReadOnlyList<TradingHistoryEntry> Trades
    {
        get => _trades;
        private set => SetField(ref _trades, value);
    }

    public string RawJson
    {
        get => _rawJson;
        private set => SetField(ref _rawJson, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    public async Task LoadAsync(string symbol, string? category, DateTime? sinceDate)
    {
        Symbol = symbol?.Trim() ?? string.Empty;
        Category = category?.Trim() ?? string.Empty;
        SinceDate = sinceDate;
        ErrorMessage = null;
        Trades = Array.Empty<TradingHistoryEntry>();
        RawJson = string.Empty;

        if (string.IsNullOrWhiteSpace(Symbol))
        {
            return;
        }

        IsLoading = true;
        try
        {
            // Load server-calculated trades for this symbol and build the raw JSON view from the payloads we receive.
            var sinceTimestamp = GetSinceTimestamp(sinceDate);
            var categoryValue = string.IsNullOrWhiteSpace(Category) ? null : Category;
            var entries = await _tradingHistoryPort.LoadBySymbolAsync(Symbol, categoryValue, sinceTimestamp);
            var ordered = BuildSymbolView(entries);
            Trades = ordered;
            RawJson = BuildRawJson(ordered);
        }
        catch (Exception ex)
        {
            ErrorMessage = ResolveErrorMessage(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public string BuildTradesMarkdownTable()
    {
        if (Trades.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("| Date | Trade | Price | Value | Fee | Size after | Avg price after | Realized PnL | Cumulative PnL |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var trade in Trades)
        {
            var dateText = EscapeMarkdown(FormatTimestamp(trade.Timestamp));
            var tradeText = EscapeMarkdown($"{trade.Side} {trade.Size} {trade.Symbol}".Trim());
            var priceText = FormatNumber(trade.Price);
            var currencyText = string.IsNullOrWhiteSpace(trade.Currency) ? string.Empty : $" {trade.Currency}";
            var valueText = $"{FormatNumber(trade.Size * trade.Price)}{currencyText}";
            var feeText = $"{FormatNumber(trade.Fee)}{currencyText}";
            var sizeAfterText = FormatNumber(trade.Calculated?.SizeAfter ?? 0m);
            var avgPriceAfterText = FormatNumber(trade.Calculated?.AvgPriceAfter ?? 0m);
            var realizedPnlText = FormatNumber(trade.Calculated?.RealizedPnl ?? 0m);
            var cumulativePnlText = FormatNumber(trade.Calculated?.CumulativePnl ?? 0m);

            builder.Append("| ")
                .Append(dateText).Append(" | ")
                .Append(tradeText).Append(" | ")
                .Append(priceText).Append(" | ")
                .Append(valueText).Append(" | ")
                .Append(feeText).Append(" | ")
                .Append(sizeAfterText).Append(" | ")
                .Append(avgPriceAfterText).Append(" | ")
                .Append(realizedPnlText).Append(" | ")
                .Append(cumulativePnlText).AppendLine(" |");
        }

        return builder.ToString();
    }

    private static long? GetSinceTimestamp(DateTime? sinceDate)
    {
        if (!sinceDate.HasValue)
        {
            return null;
        }

        var local = DateTime.SpecifyKind(sinceDate.Value, DateTimeKind.Local);
        return new DateTimeOffset(local).ToUnixTimeMilliseconds();
    }

    private static string BuildRawJson(IReadOnlyList<TradingHistoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var payload = new List<JsonElement>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.RawJson))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(entry.RawJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    payload.Add(item.Clone());
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                payload.Add(root.Clone());
            }
        }

        if (payload.Count == 0)
        {
            return string.Empty;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(payload, options);
    }

    private static string ResolveErrorMessage(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            return UnauthorizedMessage;
        }

        if (ex is ProblemDetailsException problem)
        {
            return problem.Details.Detail ?? problem.Details.Title ?? ex.Message;
        }

        return ex.Message;
    }

    private static IReadOnlyList<TradingHistoryEntry> BuildSymbolView(
        IReadOnlyList<TradingHistoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return Array.Empty<TradingHistoryEntry>();
        }

        var orderedAsc = entries
            .OrderBy(entry => entry.Timestamp ?? 0)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToList();

        var cumulativeByCoin = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in orderedAsc)
        {
            var settleCoin = string.IsNullOrWhiteSpace(entry.Currency) ? "_unknown" : entry.Currency;
            cumulativeByCoin.TryGetValue(settleCoin, out var current);

            var realized = entry.Calculated?.RealizedPnl ?? 0m;
            var next = current + realized - entry.Fee;

            entry.Calculated ??= new TradingTransactionCalculated();
            entry.Calculated.CumulativePnl = next;

            cumulativeByCoin[settleCoin] = next;
        }

        return orderedAsc;
    }

    private static string EscapeMarkdown(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static string FormatNumber(decimal value)
    {
        return value.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    private static string FormatTimestamp(long? timestamp)
    {
        if (!timestamp.HasValue || timestamp.Value <= 0)
        {
            return "N/A";
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value)
                .ToLocalTime()
                .ToString("g", CultureInfo.CurrentCulture);
        }
        catch
        {
            return "N/A";
        }
    }
}
