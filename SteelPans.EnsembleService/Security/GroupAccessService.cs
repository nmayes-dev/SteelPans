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
}