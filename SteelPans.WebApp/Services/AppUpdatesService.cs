using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace SteelPans.WebApp.Services;

public sealed class AppUpdatesService(NavigationManager nav) : IAsyncDisposable
{
    public event Func<Task>? UserStateChanged;
    public event Func<Guid, Task>? GroupChanged;

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
            return;
        }

        if (connection_ is null)
        {
            connection_ = new HubConnectionBuilder()
                .WithUrl(nav.ToAbsoluteUri("/hubs/app-updates"))
                .WithAutomaticReconnect()
                .Build();

            connection_.On<Guid>(AppUpdateMessages.UserStateChanged, async _ =>
            {
                if (UserStateChanged is not null)
                {
                    await UserStateChanged.Invoke();
                }
            });

            connection_.On<Guid>(AppUpdateMessages.GroupChanged, async groupId =>
            {
                if (GroupChanged is not null)
                {
                    await GroupChanged.Invoke(groupId);
                }
            });

            await connection_.StartAsync(cancellationToken);
        }
        else if (connection_.State == HubConnectionState.Disconnected)
        {
            await connection_.StartAsync(cancellationToken);
        }

        if (joinedUser_ != userId)
        {
            if (joinedUser_ is not null)
            {
                await connection_.InvokeAsync("LeaveUser", joinedUser_.Value, cancellationToken);
            }

            await connection_.InvokeAsync("JoinUser", userId, cancellationToken);
            joinedUser_ = userId;
        }

        var desiredGroups = groupIds.ToHashSet();
        foreach (var groupId in joinedGroups_.Except(desiredGroups).ToList())
        {
            await connection_.InvokeAsync("LeaveGroup", groupId, cancellationToken);
            joinedGroups_.Remove(groupId);
        }

        foreach (var groupId in desiredGroups.Except(joinedGroups_).ToList())
        {
            await connection_.InvokeAsync("JoinGroup", groupId, cancellationToken);
            joinedGroups_.Add(groupId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (connection_ is not null)
        {
            await connection_.DisposeAsync();
        }
    }
}
