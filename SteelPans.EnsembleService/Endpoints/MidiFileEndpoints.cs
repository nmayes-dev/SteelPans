using Microsoft.EntityFrameworkCore;
using SteelPans.EnsembleService.Auth;
using SteelPans.EnsembleService.Data;
using SteelPans.EnsembleService.Files;
using SteelPans.EnsembleService.Security;
using SteelPans.Shared.Ensembles;

namespace SteelPans.EnsembleService.Endpoints;

public static class MidiFileEndpoints
{
    public static IEndpointRouteBuilder MapMidiFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files");

        group.MapPost("/groups/{groupId:guid}", UploadMidiFile);
        group.MapGet("/{fileId:guid}", GetMidiFileDetails);
        group.MapGet("/{fileId:guid}/download", DownloadMidiFile);
        group.MapPost("/{fileId:guid}/assignments", SaveAssignments);

        return app;
    }

    private static async Task<IResult> UploadMidiFile(
        Guid groupId,
        IFormFile file,
        EnsembleDbContext db,
        ICurrentUserAccessor currentUser,
        GroupAccessService access,
        IEnsembleFileStore fileStore,
        CancellationToken cancellationToken)
    {
        if (!await access.IsLeaderAsync(groupId, currentUser.UserId, cancellationToken))
        {
            return Results.Forbid();
        }

        if (file.Length <= 0)
        {
            return Results.BadRequest("File is empty.");
        }

        var extension = Path.GetExtension(file.FileName);

        if (!string.Equals(extension, ".mid", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".midi", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("Only .mid and .midi files are supported.");
        }

        var fileId = Guid.NewGuid();

        await using var input = file.OpenReadStream();

        var storageKey = await fileStore.SaveAsync(
            groupId,
            fileId,
            file.FileName,
            input,
            cancellationToken);

        var midiFile = new EnsembleMidiFile
        {
            Id = fileId,
            GroupId = groupId,
            UploadedByUserId = currentUser.UserId,
            Title = Path.GetFileNameWithoutExtension(file.FileName),
            OriginalFileName = file.FileName,
            ContentType = file.ContentType,
            SizeBytes = file.Length,
            StorageKey = storageKey,
            UploadedAt = DateTimeOffset.UtcNow
        };

        // Later: parse real tracks using your MidiService/DryWetMIDI logic.
        midiFile.Tracks.Add(new EnsembleMidiTrack
        {
            Id = Guid.NewGuid(),
            MidiFileId = fileId,
            TrackIndex = 0,
            TrackName = "Track 1"
        });

        db.MidiFiles.Add(midiFile);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new GroupFileDto(
            midiFile.Id,
            midiFile.GroupId,
            midiFile.Title,
            midiFile.OriginalFileName,
            midiFile.SizeBytes,
            midiFile.UploadedAt));
    }

    private static async Task<IResult> GetMidiFileDetails(
        Guid fileId,
        EnsembleDbContext db,
        ICurrentUserAccessor currentUser,
        GroupAccessService access,
        CancellationToken cancellationToken)
    {
        var file = await db.MidiFiles
            .Include(x => x.Tracks)
            .Include(x => x.Assignments)
            .FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);

        if (file is null)
        {
            return Results.NotFound();
        }

        if (!await access.IsMemberAsync(file.GroupId, currentUser.UserId, cancellationToken))
        {
            return Results.Forbid();
        }

        return Results.Ok(new MidiFileDetailsDto(
            file.Id,
            file.Title,
            file.OriginalFileName,
            file.Tracks
                .OrderBy(x => x.TrackIndex)
                .Select(x => new MidiTrackDto(
                    x.TrackIndex,
                    x.TrackName,
                    x.SuggestedPanType))
                .ToList(),
            file.Assignments
                .OrderBy(x => x.TrackIndex)
                .Select(x => new MidiTrackAssignmentDto(
                    x.TrackIndex,
                    x.PanType,
                    x.Label))
                .ToList()));
    }

    private static async Task<IResult> DownloadMidiFile(
        Guid fileId,
        EnsembleDbContext db,
        ICurrentUserAccessor currentUser,
        GroupAccessService access,
        IEnsembleFileStore fileStore,
        CancellationToken cancellationToken)
    {
        var file = await db.MidiFiles
            .FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);

        if (file is null)
        {
            return Results.NotFound();
        }

        if (!await access.IsMemberAsync(file.GroupId, currentUser.UserId, cancellationToken))
        {
            return Results.Forbid();
        }

        var stream = await fileStore.OpenReadAsync(file.StorageKey, cancellationToken);

        return Results.File(
            stream,
            file.ContentType,
            file.OriginalFileName);
    }

    private static async Task<IResult> SaveAssignments(
        Guid fileId,
        SaveMidiAssignmentsRequest request,
        EnsembleDbContext db,
        ICurrentUserAccessor currentUser,
        GroupAccessService access,
        CancellationToken cancellationToken)
    {
        var file = await db.MidiFiles
            .Include(x => x.Assignments)
            .FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);

        if (file is null)
        {
            return Results.NotFound();
        }

        if (!await access.IsLeaderAsync(file.GroupId, currentUser.UserId, cancellationToken))
        {
            return Results.Forbid();
        }

        db.MidiTrackAssignments.RemoveRange(file.Assignments);

        foreach (var assignment in request.Assignments)
        {
            db.MidiTrackAssignments.Add(new EnsembleMidiTrackAssignment
            {
                Id = Guid.NewGuid(),
                MidiFileId = fileId,
                TrackIndex = assignment.TrackIndex,
                PanType = assignment.PanType,
                Label = assignment.Label
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }
}