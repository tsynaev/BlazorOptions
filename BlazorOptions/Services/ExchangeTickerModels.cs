namespace BlazorOptions.Services;

public record ExchangeTickerSubscription(string Exchange, string Symbol, Uri WebSocketUrl);

public record ExchangePriceUpdate(string Exchange, string Symbol, decimal Price, DateTime Timestamp);

public interface IExchangeTickerClient
{
    string Exchange { get; }
    event EventHandler<ExchangePriceUpdate>? PriceUpdated;
    Task EnsureConnectedAsync(Uri webSocketUrl, CancellationToken cancellationToken);
    Task SubscribeAsync(string symbol, CancellationToken cancellationToken);
    Task UnsubscribeAsync(string symbol, CancellationToken cancellationToken);
    Task DisconnectAsync();
}
