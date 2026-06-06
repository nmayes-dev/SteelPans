namespace SteelPans.Shared.Services;

public interface IRealtimeUpdateDispatcher
{
    Task NotifyUserStateChangedAsync(Guid userId, CancellationToken cancellationToken = default);
    Task NotifyGroupChangedAsync(Guid groupId, CancellationToken cancellationToken = default);
    Task NotifyGroupsChangedAsync(IEnumerable<Guid> groupIds, CancellationToken cancellationToken = default);
    Task NotifyMidiAssignmentsChangedAsync(Guid fileId, Guid ownerId, IEnumerable<Guid> groupIds, CancellationToken cancellationToken = default);
}

public sealed class NullRealtimeUpdateDispatcher : IRealtimeUpdateDispatcher
{
    public Task NotifyUserStateChangedAsync(Guid userId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task NotifyGroupChangedAsync(Guid groupId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task NotifyGroupsChangedAsync(IEnumerable<Guid> groupIds, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task NotifyMidiAssignmentsChangedAsync(Guid fileId, Guid ownerId, IEnumerable<Guid> groupIds, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
