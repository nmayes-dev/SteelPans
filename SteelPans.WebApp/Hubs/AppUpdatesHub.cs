using Microsoft.AspNetCore.SignalR;

namespace SteelPans.WebApp.Hubs;

public sealed class AppUpdatesHub(ILogger<AppUpdatesHub> logger) : Hub
{
    public override Task OnConnectedAsync()
    {
        logger.LogDebug("Realtime client connected with connection id {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is null)
            logger.LogDebug("Realtime client disconnected with connection id {ConnectionId}", Context.ConnectionId);
        else
            logger.LogWarning(exception, "Realtime client {ConnectionId} disconnected unexpectedly", Context.ConnectionId);

        return base.OnDisconnectedAsync(exception);
    }

    public async Task JoinUser(Guid userId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        logger.LogDebug("Realtime client {ConnectionId} joined user channel {UserId}", Context.ConnectionId, userId);
    }

    public async Task LeaveUser(Guid userId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, UserGroup(userId));
        logger.LogDebug("Realtime client {ConnectionId} left user channel {UserId}", Context.ConnectionId, userId);
    }

    public async Task JoinGroup(Guid groupId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupGroup(groupId));
        logger.LogDebug("Realtime client {ConnectionId} joined group channel {GroupId}", Context.ConnectionId, groupId);
    }

    public async Task LeaveGroup(Guid groupId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupGroup(groupId));
        logger.LogDebug("Realtime client {ConnectionId} left group channel {GroupId}", Context.ConnectionId, groupId);
    }

    public static string UserGroup(Guid userId)
    {
        return $"user:{userId:N}";
    }

    public static string GroupGroup(Guid groupId)
    {
        return $"group:{groupId:N}";
    }
}
