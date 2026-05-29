using Microsoft.EntityFrameworkCore;
using SteelPans.EnsembleService.Security;
using SteelPans.Shared.Auth;
using SteelPans.Shared.Data;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Services;

namespace SteelPans.EnsembleService.Endpoints;

public static class MidiFileEndpoints
{
    public static IEndpointRouteBuilder MapMidiFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files")
            .RequireAuthorization();

        group.MapPost("/groups/{groupId:guid}", UploadMidiFile)
            .RequireRateLimiting("Uploads");

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
        MidiInspectionService midiInspection,
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

        await using var parseInput = file.OpenReadStream();
        var parsedMidiFile = await midiInspection.ReadAsync(parseInput, cancellationToken);
        var tracks = midiInspection.GetTrackInfos(parsedMidiFile);

        await using var storageInput = file.OpenReadStream();

        var storageKey = await fileStore.SaveAsync(
            groupId,
            fileId,
            file.FileName,
            storageInput,
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

        foreach (var track in tracks)
        {
            midiFile.Tracks.Add(new EnsembleMidiTrack
            {
                Id = Guid.NewGuid(),
                MidiFileId = fileId,
                TrackIndex = track.Index,
                TrackName = track.Name
            });
        }

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

        if (!await access.CanAccessFileAsync(file, currentUser.UserId, cancellationToken))
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
        MidiInspectionService midiInspection,
        CancellationToken cancellationToken)
    {
        var file = await db.MidiFiles
            .FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);

        if (file is null)
        {
            return Results.NotFound();
        }

        if (!await access.CanAccessFileAsync(file, currentUser.UserId, cancellationToken))
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

        if (!await access.CanAccessFileAsync(file, currentUser.UserId, cancellationToken))
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