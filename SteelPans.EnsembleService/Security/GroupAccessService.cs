using Microsoft.EntityFrameworkCore;
using SteelPans.Shared.Data;
using SteelPans.Shared.Ensembles;

namespace SteelPans.EnsembleService.Security;

public sealed class GroupAccessService(EnsembleDbContext db)
{
    public async Task<bool> IsMemberAsync(
        Guid groupId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        return await db.GroupMembers.AnyAsync(
            x => x.GroupId == groupId && x.UserId == userId,
            cancellationToken);
    }

    public async Task<bool> IsLeaderAsync(
        Guid groupId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        return await db.GroupMembers.AnyAsync(
            x => x.GroupId == groupId &&
                 x.UserId == userId &&
                 x.Role == GroupRole.Leader,
            cancellationToken);
    }

    public async Task<bool> CanAccessFileAsync(
        EnsembleMidiFile file,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (file.UploadedByUserId == userId)
        {
            return true;
        }

        if (file.GroupId is null)
        {
            return false;
        }

        return await IsMemberAsync(file.GroupId.Value, userId, cancellationToken);
    }

    public async Task<bool> CanEditFileAsync(
        EnsembleMidiFile file,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (file.UploadedByUserId == userId)
        {
            return true;
        }

        if (file.GroupId is null)
        {
            return false;
        }

        return await IsLeaderAsync(file.GroupId.Value, userId, cancellationToken);
    }
}