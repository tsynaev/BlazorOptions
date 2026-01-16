using System.Text.Json;

namespace BlazorOptions.ViewModels;

public record TradingTransactionRecord
{
    public string UniqueKey { get; init; } = Guid.NewGuid().ToString("N");
    public List<JsonElement> Data { get; init; } = new();
    public TradingTransactionCalculated Calculated { get; init; } = new();
}
