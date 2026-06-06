using Microsoft.AspNetCore.SignalR;
using SteelPans.Shared.Services;
using SteelPans.WebApp.Hubs;

namespace SteelPans.WebApp.Services;

public sealed class SignalRRealtimeUpdateDispatcher(IHubContext<AppUpdatesHub> hub) : IRealtimeUpdateDispatcher
{
    public Task NotifyUserStateChangedAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return hub.Clients
            .Group(AppUpdatesHub.UserGroup(userId))
            .SendAsync(AppUpdateMessages.UserStateChanged, userId, cancellationToken);
    }

    public Task NotifyGroupChangedAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        return hub.Clients
            .Group(AppUpdatesHub.GroupGroup(groupId))
            .SendAsync(AppUpdateMessages.GroupChanged, groupId, cancellationToken);
    }

    public async Task NotifyGroupsChangedAsync(IEnumerable<Guid> groupIds, CancellationToken cancellationToken = default)
    {
        foreach (var groupId in groupIds.Distinct())
        {
            await NotifyGroupChangedAsync(groupId, cancellationToken);
        }
    }
}

public static class AppUpdateMessages
{
    public const string UserStateChanged = nameof(UserStateChanged);
    public const string GroupChanged = nameof(GroupChanged);
}
