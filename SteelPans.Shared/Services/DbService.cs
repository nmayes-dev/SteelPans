using Microsoft.EntityFrameworkCore;
using SteelPans.Shared.Auth;
using SteelPans.Shared.Data;
using SteelPans.Shared.Ensembles;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace SteelPans.Shared.Services;

public sealed class DbService
{
    public readonly GroupService Groups;
    public readonly MidiFileService MidiFiles;

    public DbService(EnsembleDbContext db,
        ICurrentUserAccessor currentUser,
        IEnsembleFileStore fileStore,
        MidiInspectionService midiInspection)
    {
        Groups = new(db, currentUser);
        MidiFiles = new(db, currentUser, Groups, fileStore, midiInspection);
    }

    public sealed class GroupService(EnsembleDbContext db, ICurrentUserAccessor currentUser)
    {
        public async Task<IReadOnlyList<GroupSummaryDto>> GetMyGroupsAsync(
            CancellationToken cancellationToken = default)
        {
            return await db.GroupMembers
                .AsNoTracking()
                .Where(x => x.UserId == currentUser.UserId)
                .OrderBy(x => x.Group.Name)
                .Select(x => new GroupSummaryDto(
                    x.Group.Id,
                    x.Group.Name,
                    x.Group.InviteCode,
                    x.Role))
                .ToListAsync(cancellationToken);
        }

        public async Task<GroupSummaryDto> CreateGroupAsync(
            CreateGroupRequest request,
            CancellationToken cancellationToken = default)
        {
            var name = request.Name.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("Group name is required.");
            }

            var now = DateTimeOffset.UtcNow;

            var inviteCode = EnsembleGroup.GenerateInviteCode();

            while (await db.Groups.AnyAsync(
                x => x.InviteCode == inviteCode,
                cancellationToken))
            {
                inviteCode = EnsembleGroup.GenerateInviteCode();
            }

            var group = new EnsembleGroup
            {
                Id = Guid.NewGuid(),
                Name = name,
                InviteCode = inviteCode,
                CreatedByUserId = currentUser.UserId,
                CreatedAt = now
            };

            group.Members.Add(new EnsembleGroupMember
            {
                GroupId = group.Id,
                UserId = currentUser.UserId,
                Role = GroupRole.Leader,
                JoinedAt = now
            });

            db.Groups.Add(group);
            await db.SaveChangesAsync(cancellationToken);

            return new GroupSummaryDto(
                group.Id,
                group.Name,
                group.InviteCode,
                GroupRole.Leader);
        }

        public async Task<GroupSummaryDto?> JoinGroupAsync(
            string inviteCode,
            CancellationToken cancellationToken = default)
        {
            inviteCode = NormalizeCode(inviteCode);

            var group = await db.Groups
                .Include(x => x.Members)
                .FirstOrDefaultAsync(x => x.InviteCode == inviteCode, cancellationToken);

            if (group is null)
            {
                return null;
            }

            var existingMember = group.Members
                .FirstOrDefault(x => x.UserId == currentUser.UserId);

            var role = existingMember?.Role ?? GroupRole.Member;

            if (existingMember is null)
            {
                group.Members.Add(new EnsembleGroupMember
                {
                    GroupId = group.Id,
                    UserId = currentUser.UserId,
                    Role = role,
                    JoinedAt = DateTimeOffset.UtcNow
                });

                await db.SaveChangesAsync(cancellationToken);
            }

            return new GroupSummaryDto(
                group.Id,
                group.Name,
                group.InviteCode,
                role);
        }

        public async Task<IReadOnlyList<GroupFileDto>> GetGroupFilesAsync(
            Guid groupId,
            CancellationToken cancellationToken = default)
        {
            if (!await IsMemberAsync(groupId, cancellationToken))
            {
                throw new UnauthorizedAccessException();
            }

            return await db.MidiFiles
                .AsNoTracking()
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
        }

        public async Task DeleteGroupAsync(
            Guid groupId,
            CancellationToken cancellationToken = default)
        {
            var group = await db.Groups
                .Include(x => x.Members)
                .FirstOrDefaultAsync(x => x.Id == groupId, cancellationToken);

            if (group is null)
            {
                return;
            }

            var currentMember = group.Members
                .FirstOrDefault(x => x.UserId == currentUser.UserId);

            if (currentMember?.Role != GroupRole.Leader)
            {
                throw new UnauthorizedAccessException();
            }

            db.Groups.Remove(group);

            await db.SaveChangesAsync(cancellationToken);
        }

        public async Task<bool> IsMemberAsync(
            Guid groupId,
            CancellationToken cancellationToken = default)
        {
            return await db.GroupMembers.AnyAsync(
                x => x.GroupId == groupId &&
                     x.UserId == currentUser.UserId,
                cancellationToken);
        }

        public async Task<bool> IsLeaderAsync(
            Guid groupId,
            CancellationToken cancellationToken = default)
        {
            return await db.GroupMembers.AnyAsync(
                x => x.GroupId == groupId &&
                     x.UserId == currentUser.UserId &&
                     x.Role == GroupRole.Leader,
                cancellationToken);
        }

        private static string NormalizeCode(string value)
        {
            return value.Trim().ToLowerInvariant();
        }
    }


    public sealed record MidiFileDownload(
        Stream Stream,
        string ContentType,
        string FileName);
    public sealed class MidiFileService(
        EnsembleDbContext db,
        ICurrentUserAccessor currentUser,
        GroupService groups,
        IEnsembleFileStore fileStore,
        MidiInspectionService midiInspection)
    {
        public async Task<GroupFileDto> UploadMidiFileAsync(
            Guid? groupId,
            string originalFileName,
            string? contentType,
            long sizeBytes,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            if (groupId is not null &&
                !await groups.IsLeaderAsync(groupId.Value, cancellationToken))
            {
                throw new UnauthorizedAccessException();
            }

            if (sizeBytes <= 0)
            {
                throw new InvalidOperationException("File is empty.");
            }

            var extension = Path.GetExtension(originalFileName);

            if (!string.Equals(extension, ".mid", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".midi", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only .mid and .midi files are supported.");
            }

            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;

            var parsedMidiFile = await midiInspection.ReadAsync(buffer, cancellationToken);
            var tracks = midiInspection.GetTrackInfos(parsedMidiFile);

            var fileId = Guid.NewGuid();

            buffer.Position = 0;
            var storageKey = await fileStore.SaveAsync(
                groupId ?? currentUser.UserId,
                fileId,
                originalFileName,
                buffer,
                cancellationToken);

            var midiFile = new EnsembleMidiFile
            {
                Id = fileId,
                GroupId = groupId,
                UploadedByUserId = currentUser.UserId,
                Title = Path.GetFileNameWithoutExtension(originalFileName),
                OriginalFileName = Path.GetFileName(originalFileName),
                ContentType = string.IsNullOrWhiteSpace(contentType)
                    ? "audio/midi"
                    : contentType,
                SizeBytes = sizeBytes,
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

                midiFile.Assignments.Add(new EnsembleMidiTrackAssignment
                {
                    Id = Guid.NewGuid(),
                    MidiFileId = fileId,
                    TrackIndex = track.Index,
                    PanType = Music.PanType.None,
                    Label = track.Name ?? $"Track {track.Index + 1}",
                });
            }

            db.MidiFiles.Add(midiFile);

            await db.SaveChangesAsync(cancellationToken);

            await SaveMidiAssignmentsAsync(
                fileId,
                new SaveMidiAssignmentsRequest(midiFile.Assignments
                    .OrderBy(x => x.TrackIndex)
                    .Select(x => new MidiTrackAssignmentDto(
                            x.TrackIndex,
                            x.PanType,
                            x.Label))
                    .ToList()));

            return new GroupFileDto(
                midiFile.Id,
                midiFile.GroupId,
                midiFile.Title,
                midiFile.OriginalFileName,
                midiFile.SizeBytes,
                midiFile.UploadedAt);
        }

        public async Task<MidiFileDetailsDto?> GetMidiFileDetailsAsync(
            Guid fileId,
            CancellationToken cancellationToken = default)
        {
            var file = await db.MidiFiles
                .AsNoTracking()
                .Include(x => x.Tracks)
                .Include(x => x.Assignments)
                .FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);

            if (file is null)
            {
                return null;
            }

            if (!await CanAccessFileAsync(file, cancellationToken))
            {
                throw new UnauthorizedAccessException();
            }

            return new MidiFileDetailsDto(
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
                    .ToList());
        }

        public async Task<MidiFileDownload?> OpenMidiFileAsync(
            Guid fileId,
            CancellationToken cancellationToken = default)
        {
            var file = await db.MidiFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);

            if (file is null)
            {
                return null;
            }

            if (!await CanAccessFileAsync(file, cancellationToken))
            {
                throw new UnauthorizedAccessException();
            }

            var stream = await fileStore.OpenReadAsync(
                file.StorageKey,
                cancellationToken);

            return new MidiFileDownload(
                stream,
                file.ContentType,
                file.OriginalFileName);
        }

        public async Task SaveMidiAssignmentsAsync(
            Guid fileId,
            SaveMidiAssignmentsRequest request,
            CancellationToken cancellationToken = default)
        {
            var file = await db.MidiFiles
                .Include(x => x.Assignments)
                .FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);

            if (file is null)
            {
                throw new InvalidOperationException("MIDI file not found.");
            }

            if (!await CanEditFileAsync(file, cancellationToken))
            {
                throw new UnauthorizedAccessException();
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
                    Label = assignment.Label.Trim()
                });
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        public async Task DeleteMidiFileAsync(Guid fileId)
        {
            var file = await db.MidiFiles
                .FirstOrDefaultAsync(x => x.Id == fileId && x.UploadedByUserId == currentUser.UserId);

            if (file is null)
            {
                throw new InvalidOperationException("MIDI file was not found.");
            }

            db.MidiFiles.Remove(file);
            await db.SaveChangesAsync();
        }
        public async Task<IReadOnlyList<GroupFileDto>> GetMyMidiFilesAsync(
            CancellationToken cancellationToken = default)
        {
            return await db.MidiFiles
                .AsNoTracking()
                .Where(x => x.UploadedByUserId == currentUser.UserId &&
                            x.ArchivedAt == null)
                .OrderByDescending(x => x.UploadedAt)
                .Select(x => new GroupFileDto(
                    x.Id,
                    x.GroupId,
                    x.Title,
                    x.OriginalFileName,
                    x.SizeBytes,
                    x.UploadedAt))
                .ToListAsync(cancellationToken);
        }

        public async Task ShareMidiFileWithGroupAsync(
            Guid fileId,
            Guid groupId,
            CancellationToken cancellationToken = default)
        {
            if (!await groups.IsLeaderAsync(groupId, cancellationToken))
            {
                throw new UnauthorizedAccessException();
            }

            var file = await db.MidiFiles
                .FirstOrDefaultAsync(x =>
                    x.Id == fileId &&
                    x.UploadedByUserId == currentUser.UserId &&
                    x.ArchivedAt == null,
                    cancellationToken);

            if (file is null)
            {
                throw new InvalidOperationException("MIDI file not found.");
            }

            file.GroupId = groupId;

            await db.SaveChangesAsync(cancellationToken);
        }

        private async Task<bool> CanAccessFileAsync(
            EnsembleMidiFile file,
            CancellationToken cancellationToken)
        {
            return file.UploadedByUserId == currentUser.UserId ||
                   file.GroupId is not null &&
                   await groups.IsMemberAsync(file.GroupId.Value, cancellationToken);
        }

        private async Task<bool> CanEditFileAsync(
            EnsembleMidiFile file,
            CancellationToken cancellationToken)
        {
            return file.UploadedByUserId == currentUser.UserId ||
                   file.GroupId is not null &&
                   await groups.IsLeaderAsync(file.GroupId.Value, cancellationToken);
        }
    }

}


