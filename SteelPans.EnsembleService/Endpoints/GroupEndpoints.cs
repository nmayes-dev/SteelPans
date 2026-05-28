using Microsoft.EntityFrameworkCore;
using SteelPans.EnsembleService.Auth;
using SteelPans.EnsembleService.Data;
using SteelPans.Shared.Ensembles;

namespace SteelPans.EnsembleService.Endpoints;

public static class GroupEndpoints
{
    public static IEndpointRouteBuilder MapGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/groups");

        group.MapGet("/mine", GetMyGroups);
        group.MapPost("/", CreateGroup);
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
                x.Group.Slug,
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
        var user = await db.Users.FindAsync(
            [currentUser.UserId],
            cancellationToken);

        if (user is null)
        {
            user = new EnsembleUser
            {
                Id = currentUser.UserId,
                Email = currentUser.Email,
                DisplayName = currentUser.Email,
                CreatedAt = DateTimeOffset.UtcNow
            };

            db.Users.Add(user);
        }

        var ensembleGroup = new EnsembleGroup
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Slug = request.Slug.Trim().ToLowerInvariant(),
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
            ensembleGroup.Slug,
            GroupRole.Leader));
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