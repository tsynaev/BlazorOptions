using BlazorOptions.Sync;
using Microsoft.AspNetCore.SignalR;

namespace BlazorOptions.Server.Services;

public class SyncHub : Hub
{
    private readonly UserRegistryService _registry;
    private readonly UserDataStore _dataStore;

    public SyncHub(UserRegistryService registry, UserDataStore dataStore)
    {
        _registry = registry;
        _dataStore = dataStore;
    }

    public override async Task OnConnectedAsync()
    {
        var token = Context.GetHttpContext()?.Request.Query["token"].FirstOrDefault();
        var user = await _registry.GetUserByTokenAsync(token);
        if (user is null)
        {
            Context.Abort();
            return;
        }

        Context.Items["userId"] = user.Id;
        await Groups.AddToGroupAsync(Context.ConnectionId, user.Id);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue("userId", out var value) && value is string userId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, userId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task<IReadOnlyList<Guid>> SendEvents(List<EventEnvelope> events)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Array.Empty<Guid>();
        }

        var accepted = await _dataStore.AppendEventsAsync(userId, events);
        if (accepted.Count > 0)
        {
            await Clients.GroupExcept(userId, new[] { Context.ConnectionId })
                .SendAsync("ReceiveEvents", accepted);
        }

        return accepted.Select(envelope => envelope.EventId).ToList();
    }

    private string? GetUserId()
    {
        return Context.Items.TryGetValue("userId", out var value) ? value as string : null;
    }
}
