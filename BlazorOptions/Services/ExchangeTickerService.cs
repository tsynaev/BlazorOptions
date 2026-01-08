using System.Collections.Concurrent;

namespace BlazorOptions.Services;

public class ExchangeTickerService : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, IExchangeTickerClient> _clients;
    private readonly ConcurrentDictionary<IExchangeTickerClient, EventHandler<ExchangePriceUpdate>> _handlers = new();
    private IExchangeTickerClient? _activeClient;

    public ExchangeTickerService(IEnumerable<IExchangeTickerClient> clients)
    {
        _clients = clients.ToDictionary(client => client.Exchange, StringComparer.OrdinalIgnoreCase);

        foreach (var client in _clients.Values)
        {
            var handler = new EventHandler<ExchangePriceUpdate>((_, update) => PriceUpdated?.Invoke(this, update));
            _handlers[client] = handler;
            client.PriceUpdated += handler;
        }
    }

    public event EventHandler<ExchangePriceUpdate>? PriceUpdated;

    public async Task ConnectAsync(ExchangeTickerSubscription subscription, CancellationToken cancellationToken)
    {
        if (!_clients.TryGetValue(subscription.Exchange, out var client))
        {
            throw new InvalidOperationException($"Exchange '{subscription.Exchange}' is not registered.");
        }

        if (_activeClient != client)
        {
            await DisconnectAsync();
            _activeClient = client;
        }

        await client.ConnectAsync(subscription, cancellationToken);
    }

    public async Task DisconnectAsync()
    {
        if (_activeClient is null)
        {
            return;
        }

        await _activeClient.DisconnectAsync();
        _activeClient = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();

        foreach (var (client, handler) in _handlers)
        {
            client.PriceUpdated -= handler;
        }

        _handlers.Clear();
    }
}
