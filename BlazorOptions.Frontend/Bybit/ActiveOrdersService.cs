using System.Text.Json;

namespace BlazorOptions.Services;

public sealed class ActiveOrdersService : IOrdersService, IAsyncDisposable
{

    private readonly BybitOrderService _bybitOrderService;
    private readonly IBybitPrivateStreamService _privateStreamService;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly object _subscriberLock = new();
    private readonly List<Func<IReadOnlyList<ExchangeOrder>, Task>> _subscribers = new();
    private readonly List<ExchangeOrder> _orders = new();
    private bool _snapshotInitialized;
    private bool _streamingInitialized;
    private Task _snapshotTask = Task.CompletedTask;
    private IDisposable? _topicSubscription;

    public ActiveOrdersService(
        BybitOrderService bybitOrderService,
        IBybitPrivateStreamService privateStreamService)
    {
        _bybitOrderService = bybitOrderService;
        _privateStreamService = privateStreamService;
    }

    public async Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();
        await _snapshotTask;

        await _sync.WaitAsync(cancellationToken);
        try
        {
            return _orders.ToArray();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async ValueTask<IDisposable> SubscribeAsync(
        Func<IReadOnlyList<ExchangeOrder>, Task> handler,
        CancellationToken cancellationToken = default)
    {
        if (handler is null)
        {
            return new SubscriptionRegistration(() => { });
        }

        lock (_subscriberLock)
        {
            _subscribers.Add(handler);
        }

        await EnsureInitializedAsync(requireStreaming: true);
        await _snapshotTask;

        ExchangeOrder[] snapshot;
        await _sync.WaitAsync(cancellationToken);
        try
        {
            snapshot = _orders.ToArray();
        }
        finally
        {
            _sync.Release();
        }

        if (snapshot.Length > 0 && !cancellationToken.IsCancellationRequested)
        {
            await handler.Invoke(snapshot);
        }

        return new SubscriptionRegistration(() => Unsubscribe(handler));
    }

    private async Task EnsureInitializedAsync(bool requireStreaming = false)
    {
        if (!_snapshotInitialized)
        {
            _snapshotInitialized = true;
            
            _snapshotTask = LoadSnapshotOnceAsync();
            
        }

        if (!requireStreaming || _streamingInitialized)
        {
            return;
        }

        _streamingInitialized = true;
        // Snapshot-only consumers should not keep a private stream alive.
        _topicSubscription = await _privateStreamService.SubscribeTopicAsync("order", HandleOrderTopicAsync);
    }

    private async Task HandleOrderTopicAsync(IReadOnlyList<JsonElement> entries)
    {
        foreach (var entry in entries)
        {
            var update = ReadOrder(entry);
            if (update is null)
            {
                continue;
            }

            await ApplyUpdateAsync(update);
        }
    }

    private async Task LoadSnapshotOnceAsync()
    {
        await _snapshotLock.WaitAsync();
        try
        {

            var orders = await _bybitOrderService.GetOpenOrdersAsync();
            await UpdateSnapshotAsync(orders);
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private async Task UpdateSnapshotAsync(IReadOnlyList<ExchangeOrder> orders)
    {
        await _sync.WaitAsync();
        try
        {
            _orders.Clear();
            _orders.AddRange(orders.Where(IsOpenOrderStatus));
        }
        finally
        {
            _sync.Release();
        }

        await NotifySubscribersAsync();
    }

 



   

    private static ExchangeOrder? ReadOrder(JsonElement entry)
    {
        if (!entry.TryReadString("symbol", out var symbol))
        {
            return null;
        }

        entry.TryReadString("orderId", out var orderId);
        entry.TryReadString("side", out var side);
        entry.TryReadString("category", out var category);
        entry.TryReadString("orderType", out var orderType);
        entry.TryReadString("stopOrderType", out var stopOrderType);
        entry.TryReadString("orderStatus", out var orderStatus);

        var qty = entry.ReadDecimal("qty");
        if (qty == 0)
        {
            qty = entry.ReadDecimal("leavesQty");
        }

        var price = ResolveOrderPrice(entry);

        return new ExchangeOrder(orderId, symbol, side, category, orderType, orderStatus, qty, price, stopOrderType);
    }

    private async Task ApplyUpdateAsync(ExchangeOrder update)
    {
        await _sync.WaitAsync();
        try
        {
            var index = _orders.FindIndex(order => string.Equals(order.OrderId, update.OrderId, StringComparison.OrdinalIgnoreCase));
            if (!IsOpenOrderStatus(update))
            {
                if (index >= 0)
                {
                    _orders.RemoveAt(index);
                }
            }
            else if (index >= 0)
            {
                _orders[index] = update;
            }
            else
            {
                _orders.Add(update);
            }
        }
        finally
        {
            _sync.Release();
        }

        await NotifySubscribersAsync();
    }



   


    private static bool IsOpenOrderStatus(ExchangeOrder order)
    {
        return IsOpenOrderStatus(order.OrderStatus);
    }

    private static bool IsOpenOrderStatus(string status)
    {
        return status.Equals("New", StringComparison.OrdinalIgnoreCase)
            || status.Equals("PartiallyFilled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Untriggered", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? ResolveOrderPrice(JsonElement element)
    {
        return FirstPositive(
            element.ReadNullableDecimal("price"),
            element.ReadNullableDecimal("avgPrice"),
            element.ReadNullableDecimal("triggerPrice"),
            element.ReadNullableDecimal("takeProfit"),
            element.ReadNullableDecimal("stopLoss"),
            element.ReadNullableDecimal("tpLimitPrice"),
            element.ReadNullableDecimal("slLimitPrice"));
    }

    private static decimal? FirstPositive(params decimal?[] values)
    {
        foreach (var value in values)
        {
            if (value.HasValue && value.Value > 0)
            {
                return value.Value;
            }
        }

        return null;
    }

    private void Unsubscribe(Func<IReadOnlyList<ExchangeOrder>, Task> handler)
    {
        lock (_subscriberLock)
        {
            _subscribers.Remove(handler);
        }
    }

    private async Task NotifySubscribersAsync()
    {
        Func<IReadOnlyList<ExchangeOrder>, Task>[] handlers;
        lock (_subscriberLock)
        {
            handlers = _subscribers.ToArray();
        }

        if (handlers.Length == 0)
        {
            return;
        }

        ExchangeOrder[] snapshot;
        await _sync.WaitAsync();
        try
        {
            snapshot = _orders.ToArray();
        }
        finally
        {
            _sync.Release();
        }

        foreach (var handler in handlers)
        {
            await handler.Invoke(snapshot);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _topicSubscription?.Dispose();
        _topicSubscription = null;
    }
}
