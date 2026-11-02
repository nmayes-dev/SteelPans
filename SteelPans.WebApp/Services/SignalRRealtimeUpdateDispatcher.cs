using Microsoft.AspNetCore.SignalR;
using SteelPans.Shared.Services;
using SteelPans.WebApp.Hubs;

namespace SteelPans.WebApp.Services;

public sealed class SignalRRealtimeUpdateDispatcher(
    IHubContext<AppUpdatesHub> hub,
    ILogger<SignalRRealtimeUpdateDispatcher> logger) : IRealtimeUpdateDispatcher
{
    public Task NotifyUserStateChangedAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Dispatching user-state update to {UserId}", userId);
        return hub.Clients
            .Group(AppUpdatesHub.UserGroup(userId))
            .SendAsync(AppUpdateMessages.UserStateChanged, userId, cancellationToken);
    }

    public Task NotifyGroupChangedAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Dispatching group update to {GroupId}", groupId);
        return hub.Clients
            .Group(AppUpdatesHub.GroupGroup(groupId))
            .SendAsync(AppUpdateMessages.GroupChanged, groupId, cancellationToken);
    }

    public async Task NotifyGroupsChangedAsync(IEnumerable<Guid> groupIds, CancellationToken cancellationToken = default)
    {
        var distinctGroupIds = groupIds.Distinct().ToList();
        logger.LogDebug("Dispatching updates to {GroupCount} groups", distinctGroupIds.Count);

        foreach (var groupId in distinctGroupIds)
        {
            await NotifyGroupChangedAsync(groupId, cancellationToken);
        }
    }

    public async Task NotifyMidiAssignmentsChangedAsync(
        Guid fileId,
        Guid ownerId,
        IEnumerable<Guid> groupIds,
        CancellationToken cancellationToken = default)
    {
        var distinctGroupIds = groupIds.Distinct().ToList();
        logger.LogDebug(
            "Dispatching MIDI assignment update for {FileId} to owner {OwnerId} and {GroupCount} groups",
            fileId,
            ownerId,
            distinctGroupIds.Count);

        await hub.Clients
            .Group(AppUpdatesHub.UserGroup(ownerId))
            .SendAsync(AppUpdateMessages.MidiAssignmentsChanged, fileId, cancellationToken);

        foreach (var groupId in distinctGroupIds)
        {
            await hub.Clients
                .Group(AppUpdatesHub.GroupGroup(groupId))
                .SendAsync(AppUpdateMessages.MidiAssignmentsChanged, fileId, cancellationToken);
        }
    }
}

public static class AppUpdateMessages
{
    public const string UserStateChanged = nameof(UserStateChanged);
    public const string GroupChanged = nameof(GroupChanged);
    public const string MidiAssignmentsChanged = nameof(MidiAssignmentsChanged);
}
