using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace SteelPans.WebApp.Services;

public sealed class AppUpdatesService(
    NavigationManager nav,
    IConfiguration config,
    ILogger<AppUpdatesService> logger) : IAsyncDisposable
{
    public event Func<Task>? UserStateChanged;
    public event Func<Guid, Task>? GroupChanged;
    public event Func<Guid, Task>? MidiAssignmentsChanged;

    private readonly HashSet<Guid> joinedGroups_ = [];
    private HubConnection? connection_;
    private Guid? joinedUser_;

    public async Task StartAsync(
        Guid userId,
        IEnumerable<Guid> groupIds,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            logger.LogDebug("Skipping realtime update connection because no authenticated user is available");
            return;
        }

        if (connection_ is null)
        {
            var hubUrl = config["AppUpdates:HubUrl"];
            var resolvedHubUrl = string.IsNullOrWhiteSpace(hubUrl)
                ? nav.ToAbsoluteUri("/hubs/app-updates")
                : new Uri(hubUrl);

            logger.LogInformation("Creating realtime update connection to {HubUrl}", resolvedHubUrl);

            connection_ = new HubConnectionBuilder()
                .WithUrl(resolvedHubUrl)
                .WithAutomaticReconnect()
                .Build();

            connection_.Reconnecting += exception =>
            {
                logger.LogWarning(exception, "Realtime update connection lost; reconnecting");
                return Task.CompletedTask;
            };

            connection_.Reconnected += connectionId =>
            {
                logger.LogInformation("Realtime update connection restored with connection id {ConnectionId}", connectionId);
                return Task.CompletedTask;
            };

            connection_.Closed += exception =>
            {
                if (exception is null)
                    logger.LogInformation("Realtime update connection closed");
                else
                    logger.LogWarning(exception, "Realtime update connection closed unexpectedly");

                return Task.CompletedTask;
            };

            connection_.On<Guid>(AppUpdateMessages.UserStateChanged, async _ =>
            {
                logger.LogDebug("Received realtime user-state change notification");
                if (UserStateChanged is not null)
                {
                    await UserStateChanged.Invoke();
                }
            });

            connection_.On<Guid>(AppUpdateMessages.GroupChanged, async groupId =>
            {
                logger.LogDebug("Received realtime group change notification for {GroupId}", groupId);
                if (GroupChanged is not null)
                {
                    await GroupChanged.Invoke(groupId);
                }
            });

            connection_.On<Guid>(AppUpdateMessages.MidiAssignmentsChanged, async fileId =>
            {
                logger.LogDebug("Received realtime MIDI assignment change notification for {FileId}", fileId);
                if (MidiAssignmentsChanged is not null)
                {
                    await MidiAssignmentsChanged.Invoke(fileId);
                }
            });

            await connection_.StartAsync(cancellationToken);
            logger.LogInformation("Realtime update connection started");
        }
        else if (connection_.State == HubConnectionState.Disconnected)
        {
            logger.LogInformation("Restarting disconnected realtime update connection");
            await connection_.StartAsync(cancellationToken);
        }

        if (joinedUser_ != userId)
        {
            if (joinedUser_ is not null)
            {
                logger.LogDebug("Leaving realtime user channel for {UserId}", joinedUser_.Value);
                await connection_.InvokeAsync("LeaveUser", joinedUser_.Value, cancellationToken);
            }

            await connection_.InvokeAsync("JoinUser", userId, cancellationToken);
            joinedUser_ = userId;
            logger.LogDebug("Joined realtime user channel for {UserId}", userId);
        }

        var desiredGroups = groupIds.ToHashSet();
        foreach (var groupId in joinedGroups_.Except(desiredGroups).ToList())
        {
            await connection_.InvokeAsync("LeaveGroup", groupId, cancellationToken);
            joinedGroups_.Remove(groupId);
            logger.LogDebug("Left realtime group channel {GroupId}", groupId);
        }

        foreach (var groupId in desiredGroups.Except(joinedGroups_).ToList())
        {
            await connection_.InvokeAsync("JoinGroup", groupId, cancellationToken);
            joinedGroups_.Add(groupId);
            logger.LogDebug("Joined realtime group channel {GroupId}", groupId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (connection_ is not null)
        {
            logger.LogDebug("Disposing realtime update connection");
            await connection_.DisposeAsync();
        }
    }
}
