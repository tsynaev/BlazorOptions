namespace BlazorOptions.Services;

public record ExchangeTickerSubscription(string Exchange, string Symbol, Uri WebSocketUrl);

public record ExchangePriceUpdate(string Exchange, string Symbol, decimal Price, DateTime Timestamp);

public interface IExchangeTickerClient
{
    string Exchange { get; }
    event EventHandler<ExchangePriceUpdate>? PriceUpdated;
    Task ConnectAsync(ExchangeTickerSubscription subscription, CancellationToken cancellationToken);
    Task DisconnectAsync();
}
