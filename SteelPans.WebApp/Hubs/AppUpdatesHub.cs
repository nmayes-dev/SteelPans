using Microsoft.AspNetCore.SignalR;

namespace SteelPans.WebApp.Hubs;

public sealed class AppUpdatesHub : Hub
{
    public Task JoinUser(Guid userId)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
    }

    public Task LeaveUser(Guid userId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, UserGroup(userId));
    }

    public Task JoinGroup(Guid groupId)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, GroupGroup(groupId));
    }

    public Task LeaveGroup(Guid groupId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupGroup(groupId));
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
