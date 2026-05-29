using Microsoft.EntityFrameworkCore;
using SteelPans.Shared.Auth;
using SteelPans.Shared.Data;
using SteelPans.Shared.Ensembles;

namespace SteelPans.EnsembleService.Endpoints;

public static class GroupEndpoints
{
    public static IEndpointRouteBuilder MapGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/groups")
            .RequireAuthorization();

        group.MapGet("/mine", GetMyGroups);
        group.MapPost("/", CreateGroup);
        group.MapPost("/join/{slug}", JoinGroup);
        group.MapGet("/{groupId:guid}/files", GetGroupFiles);

        return app;
    }

    private static async Task<IResult> GetMyGroups(
        EnsembleDbContext db,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var groups = await db.GroupMembers
            .Where(x => x.UserId == currentUser.UserId)
            .Select(x => new GroupSummaryDto(
                x.Group.Id,
                x.Group.Name,
                x.Group.InviteCode,
                x.Role))
            .ToListAsync(cancellationToken);

        return Results.Ok(groups);
    }

    private static async Task<IResult> CreateGroup(
        CreateGroupRequest request,
        EnsembleDbContext db,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest("Group name is required.");
        }

        var inviteCode = EnsembleGroup.GenerateInviteCode();

        while (await db.Groups.AnyAsync(
            x => x.InviteCode == inviteCode,
            cancellationToken))
        {
            inviteCode = EnsembleGroup.GenerateInviteCode();
        }

        var slugExists = await db.Groups.AnyAsync(
            x => x.InviteCode == inviteCode,
            cancellationToken);

        if (slugExists)
        {
            return Results.Conflict("A group with this slug already exists.");
        }

        var ensembleGroup = new EnsembleGroup
        {
            Id = Guid.NewGuid(),
            Name = name,
            InviteCode = inviteCode,
            CreatedByUserId = currentUser.UserId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        ensembleGroup.Members.Add(new EnsembleGroupMember
        {
            GroupId = ensembleGroup.Id,
            UserId = currentUser.UserId,
            Role = GroupRole.Leader,
            JoinedAt = DateTimeOffset.UtcNow
        });

        db.Groups.Add(ensembleGroup);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new GroupSummaryDto(
            ensembleGroup.Id,
            ensembleGroup.Name,
            ensembleGroup.InviteCode,
            GroupRole.Leader));
    }


    private static async Task<IResult> JoinGroup(
        string inviteCode,
        EnsembleDbContext db,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        inviteCode = inviteCode.Trim().ToLowerInvariant();

        var ensembleGroup = await db.Groups
            .Include(x => x.Members)
            .FirstOrDefaultAsync(x => x.InviteCode == inviteCode, cancellationToken);

        if (ensembleGroup is null)
        {
            return Results.NotFound("Group not found.");
        }

        var existingMember = ensembleGroup.Members
            .FirstOrDefault(x => x.UserId == currentUser.UserId);

        if (existingMember is null)
        {
            ensembleGroup.Members.Add(new EnsembleGroupMember
            {
                GroupId = ensembleGroup.Id,
                UserId = currentUser.UserId,
                Role = GroupRole.Member,
                JoinedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);
        }

        var role = existingMember?.Role ?? GroupRole.Member;

        return Results.Ok(new GroupSummaryDto(
            ensembleGroup.Id,
            ensembleGroup.Name,
            ensembleGroup.InviteCode,
            role));
    }

    private static async Task<IResult> GetGroupFiles(
        Guid groupId,
        EnsembleDbContext db,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var isMember = await db.GroupMembers.AnyAsync(
            x => x.GroupId == groupId && x.UserId == currentUser.UserId,
            cancellationToken);

        if (!isMember)
        {
            return Results.Forbid();
        }

        var files = await db.MidiFiles
            .Where(x => x.GroupId == groupId && x.ArchivedAt == null)
            .OrderByDescending(x => x.UploadedAt)
            .Select(x => new GroupFileDto(
                x.Id,
                x.GroupId,
                x.Title,
                x.OriginalFileName,
                x.SizeBytes,
                x.UploadedAt))
            .ToListAsync(cancellationToken);

        return Results.Ok(files);
    }
}