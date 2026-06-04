using Microsoft.EntityFrameworkCore;
using SteelPans.Shared.Auth;
using SteelPans.Shared.Data;
using SteelPans.Shared.Ensembles;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SteelPans.Shared.Services;

public sealed class DbService
{
    public readonly GroupService Groups;
    public readonly MidiFileService MidiFiles;
    public Guid User { get; init; }

    public DbService(EnsembleDbContext db,
        ICurrentUserAccessor currentUser,
        IEnsembleFileStore fileStore,
        MidiInspectionService midiInspection)
    {
        User = currentUser.UserId;
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

        public async Task<GroupSummaryDto?> GetGroupAsync(
            Guid groupId,
            CancellationToken cancellationToken = default)
        {
            return await db.GroupMembers
                .AsNoTracking()
                .Where(x => x.GroupId == groupId && x.UserId == currentUser.UserId)
                .Select(x => new GroupSummaryDto(
                    x.Group.Id,
                    x.Group.Name,
                    x.Group.InviteCode,
                    x.Role))
                .FirstOrDefaultAsync(cancellationToken);
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

            while (await db.Groups.AnyAsync(x => x.InviteCode == inviteCode, cancellationToken))
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

            return new GroupSummaryDto(group.Id, group.Name, group.InviteCode, GroupRole.Leader);
        }

        public async Task<GroupInviteDto> CreateInviteAsync(Guid groupId, CancellationToken cancellationToken = default)
        {
            if (!await IsLeaderOrAdminAsync(groupId, cancellationToken))
            {
                throw new UnauthorizedAccessException();
            }

            var now = DateTimeOffset.UtcNow;
            var token = EnsembleGroup.GenerateInviteCode();

            while (await db.GroupInvites.AnyAsync(x => x.Token == token, cancellationToken))
            {
                token = EnsembleGroup.GenerateInviteCode();
            }

            var invite = new EnsembleGroupInvite
            {
                Id = Guid.NewGuid(),
                GroupId = groupId,
                Token = token,
                CreatedByUserId = currentUser.UserId,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(30)
            };

            db.GroupInvites.Add(invite);
            await db.SaveChangesAsync(cancellationToken);

            return new GroupInviteDto(invite.Token, invite.ExpiresAt);
        }

        public async Task<GroupSummaryDto?> JoinGroupAsync(string token, CancellationToken cancellationToken = default)
        {
            token = NormalizeCode(token);
            var now = DateTimeOffset.UtcNow;

            var invite = await db.GroupInvites
                .Include(x => x.Group)
                .ThenInclude(x => x.Members)
                .FirstOrDefaultAsync(x => x.Token == token, cancellationToken);

            if (invite is null || invite.ExpiresAt <= now)
            {
                return null;
            }

            var existingMember = invite.Group.Members.FirstOrDefault(x => x.UserId == currentUser.UserId);
            var role = existingMember?.Role ?? GroupRole.Member;

            if (existingMember is null)
            {
                invite.Group.Members.Add(new EnsembleGroupMember
                {
                    GroupId = invite.GroupId,
                    UserId = currentUser.UserId,
                    Role = role,
                    JoinedAt = now
                });
            }

            await db.SaveChangesAsync(cancellationToken);

            return new GroupSummaryDto(invite.Group.Id, invite.Group.Name, invite.Group.InviteCode, role);
        }

        public async Task<IReadOnlyList<GroupFileDto>> GetGroupFilesAsync(Guid groupId, CancellationToken cancellationToken = default)
        {
            if (!await IsMemberAsync(groupId, cancellationToken))
            {
                throw new UnauthorizedAccessException();
            }

            return await db.GroupMidiFiles
                .AsNoTracking()
                .Where(x => x.GroupId == groupId && x.MidiFile.ArchivedAt == null)
                .OrderByDescending(x => x.MidiFile.UploadedAt)
                .Select(x => new GroupFileDto(
                    x.MidiFile.Id,
                    x.MidiFile.SharedGroups.Select(shared => shared.GroupId).ToList(),
                    x.MidiFile.Title,
                    x.MidiFile.OriginalFileName,
                    x.MidiFile.SizeBytes,
                    x.MidiFile.UploadedAt))
                .ToListAsync(cancellationToken);
        }

        public async Task<Dictionary<GroupSummaryDto, IReadOnlyList<GroupFileDto>>> GetAllGroupFilesAsync(CancellationToken cancellationToken = default)
        {
            var files = new Dictionary<GroupSummaryDto, IReadOnlyList<GroupFileDto>>();
            var groups = await GetMyGroupsAsync(cancellationToken);
            foreach (var group in groups)
            {
                files[group] = await GetGroupFilesAsync(group.Id, cancellationToken);
            }
            return files;
        }

        public async Task DeleteGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
        {
            var group = await db.Groups.Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == groupId, cancellationToken);
            if (group is null) return;

            var currentMember = group.Members.FirstOrDefault(x => x.UserId == currentUser.UserId);
            if (currentMember?.Role != GroupRole.Leader)
            {
                throw new UnauthorizedAccessException();
            }

            db.Groups.Remove(group);
            await db.SaveChangesAsync(cancellationToken);
        }

        public async Task<List<GroupMemberSummaryDto>> GetGroupMembersAsync(Guid groupId, CancellationToken cancellationToken = default)
        {
            if (!await IsMemberAsync(groupId, cancellationToken))
            {
                throw new UnauthorizedAccessException();
            }

            return await db.GroupMembers
                .AsNoTracking()
                .Where(x => x.GroupId == groupId)
                .Include(x => x.User)
                .OrderBy(GroupRoleExtensions.RoleSortOrder)
                .ThenBy(x => x.JoinedAt)
                .ThenBy(x => x.User.UserName)
                .Select(x => new GroupMemberSummaryDto(
                    x.UserId,
                    x.User.UserName ?? string.Empty,
                    x.User.Email ?? string.Empty,
                    x.Role,
                    x.JoinedAt,
                    x.AdminSince))
                .ToListAsync(cancellationToken);
        }

        public async Task SetMemberRoleAsync(
            Guid groupId,
            UpdateGroupMemberRoleRequest request,
            CancellationToken cancellationToken = default)
        {
            var currentMember = await GetCurrentMemberAsync(groupId, cancellationToken);
            if (currentMember?.Role != GroupRole.Leader)
            {
                throw new UnauthorizedAccessException();
            }

            if (request.Role == GroupRole.Leader)
            {
                throw new InvalidOperationException("Use transfer leadership instead.");
            }

            var member = await db.GroupMembers
                .FirstOrDefaultAsync(x => x.GroupId == groupId && x.UserId == request.UserId, cancellationToken)
                ?? throw new InvalidOperationException("Member was not found.");

            if (member.Role == GroupRole.Leader)
            {
                throw new InvalidOperationException("The leader role cannot be edited here.");
            }

            member.Role = request.Role;
            member.AdminSince = request.Role == GroupRole.Admin
                ? member.AdminSince ?? DateTimeOffset.UtcNow
                : null;

            await db.SaveChangesAsync(cancellationToken);
        }

        public async Task TransferLeadershipAsync(Guid groupId, Guid newLeaderUserId, CancellationToken cancellationToken = default)
        {
            var currentMember = await GetCurrentMemberAsync(groupId, cancellationToken);
            if (currentMember?.Role != GroupRole.Leader)
            {
                throw new UnauthorizedAccessException();
            }

            var newLeader = await db.GroupMembers.FirstOrDefaultAsync(x => x.GroupId == groupId && x.UserId == newLeaderUserId, cancellationToken)
                ?? throw new InvalidOperationException("Member was not found.");

            if (newLeader.Role != GroupRole.Admin)
            {
                throw new InvalidOperationException("Leadership can only be transferred to an admin.");
            }

            currentMember.Role = GroupRole.Admin;
            currentMember.AdminSince ??= DateTimeOffset.UtcNow;
            newLeader.Role = GroupRole.Leader;
            newLeader.AdminSince = null;

            await db.SaveChangesAsync(cancellationToken);
        }

        public async Task RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default)
        {
            var currentMember = await GetCurrentMemberAsync(groupId, cancellationToken);
            if (currentMember is null)
            {
                throw new UnauthorizedAccessException();
            }

            var member = await db.GroupMembers
                .FirstOrDefaultAsync(x => x.GroupId == groupId && x.UserId == userId, cancellationToken)
                ?? throw new InvalidOperationException("Member was not found.");

            var removingSelf = member.UserId == currentUser.UserId;

            var canRemove = removingSelf ||
                currentMember.Role == GroupRole.Leader ||
                (currentMember.Role == GroupRole.Admin && member.Role == GroupRole.Member);

            if (!canRemove)
            {
                throw new UnauthorizedAccessException();
            }

            if (!removingSelf && member.Role == GroupRole.Leader)
            {
                throw new InvalidOperationException("The leader cannot be removed by another member.");
            }

            db.GroupMembers.Remove(member);

            await PromoteReplacementLeaderOrDeleteAsync(
                groupId,
                member.Role == GroupRole.Leader,
                cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
        }

        public Task LeaveGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
            => RemoveMemberAsync(groupId, currentUser.UserId, cancellationToken);

        public async Task<bool> IsMemberAsync(Guid groupId, CancellationToken cancellationToken = default)
        {
            return await db.GroupMembers.AnyAsync(x => x.GroupId == groupId && x.UserId == currentUser.UserId, cancellationToken);
        }

        public async Task<bool> IsLeaderAsync(Guid groupId, CancellationToken cancellationToken = default)
        {
            return await db.GroupMembers.AnyAsync(x => x.GroupId == groupId && x.UserId == currentUser.UserId && x.Role == GroupRole.Leader, cancellationToken);
        }

        public async Task<bool> IsLeaderOrAdminAsync(Guid groupId, CancellationToken cancellationToken = default)
        {
            return await db.GroupMembers.AnyAsync(x => x.GroupId == groupId && x.UserId == currentUser.UserId && (x.Role == GroupRole.Leader || x.Role == GroupRole.Admin), cancellationToken);
        }

        private async Task<EnsembleGroupMember?> GetCurrentMemberAsync(Guid groupId, CancellationToken cancellationToken)
        {
            return await db.GroupMembers.FirstOrDefaultAsync(x => x.GroupId == groupId && x.UserId == currentUser.UserId, cancellationToken);
        }

        private async Task PromoteReplacementLeaderOrDeleteAsync(Guid groupId, bool leaderWasRemoved, CancellationToken cancellationToken)
        {
            var remaining = await db.GroupMembers.Where(x => x.GroupId == groupId).ToListAsync(cancellationToken);
            if (remaining.Count == 0)
            {
                var group = await db.Groups.FirstOrDefaultAsync(x => x.Id == groupId, cancellationToken);
                if (group is not null) db.Groups.Remove(group);
                return;
            }

            if (!leaderWasRemoved)
            {
                return;
            }

            var replacement = remaining
                .Where(x => x.Role == GroupRole.Admin)
                .OrderBy(x => x.AdminSince ?? x.JoinedAt)
                .ThenBy(x => x.JoinedAt)
                .FirstOrDefault()
                ?? remaining.OrderBy(x => x.JoinedAt).First();

            replacement.Role = GroupRole.Leader;
            replacement.AdminSince = null;
        }

        private static string NormalizeCode(string value) => value.Trim().ToLowerInvariant();
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
                !await groups.IsLeaderOrAdminAsync(groupId.Value, cancellationToken))
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

            if (groupId is not null)
            {
                midiFile.SharedGroups.Add(new EnsembleGroupMidiFile
                {
                    GroupId = groupId.Value,
                    MidiFileId = fileId,
                    SharedAt = DateTimeOffset.UtcNow
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
                midiFile.SharedGroups
                    .Select(x => x.GroupId)
                    .ToList(),
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
                    x.SharedGroups
                        .Select(shared => shared.GroupId)
                        .ToList(),
                    x.Title,
                    x.OriginalFileName,
                    x.SizeBytes,
                    x.UploadedAt))
                .ToListAsync(cancellationToken);
        }

        public async Task UnshareMidiFileWithGroupAsync(
            Guid fileId,
            Guid groupId,
            CancellationToken cancellationToken = default)
        {
            if (!await groups.IsLeaderOrAdminAsync(groupId, cancellationToken))
            {
                throw new UnauthorizedAccessException();
            }

            var share = await db.GroupMidiFiles
                .FirstOrDefaultAsync(
                    x => x.MidiFileId == fileId &&
                         x.GroupId == groupId,
                    cancellationToken);

            if (share is null)
            {
                return;
            }

            db.GroupMidiFiles.Remove(share);

            await db.SaveChangesAsync(cancellationToken);
        }

        public async Task ShareMidiFileWithGroupAsync(
            Guid fileId,
            Guid groupId,
            CancellationToken cancellationToken = default)
        {
            if (!await groups.IsLeaderOrAdminAsync(groupId, cancellationToken))
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

            var alreadyShared = await db.GroupMidiFiles.AnyAsync(
                x => x.GroupId == groupId && x.MidiFileId == fileId,
                cancellationToken);

            if (!alreadyShared)
            {
                db.GroupMidiFiles.Add(new EnsembleGroupMidiFile
                {
                    GroupId = groupId,
                    MidiFileId = fileId,
                    SharedAt = DateTimeOffset.UtcNow
                });

                await db.SaveChangesAsync(cancellationToken);
            }
        }

        private async Task<bool> CanAccessFileAsync(
            EnsembleMidiFile file,
            CancellationToken cancellationToken)
        {
            if (file.UploadedByUserId == currentUser.UserId)
            {
                return true;
            }

            return await db.GroupMidiFiles.AnyAsync(
                x => x.MidiFileId == file.Id &&
                     x.MidiFile.ArchivedAt == null &&
                     x.Group.Members.Any(member => member.UserId == currentUser.UserId),
                cancellationToken);
        }

        private async Task<bool> CanEditFileAsync(
            EnsembleMidiFile file,
            CancellationToken cancellationToken)
        {
            if (file.UploadedByUserId == currentUser.UserId)
            {
                return true;
            }

            return await db.GroupMidiFiles.AnyAsync(
                x => x.MidiFileId == file.Id &&
                     x.MidiFile.ArchivedAt == null &&
                     x.Group.Members.Any(member =>
                         member.UserId == currentUser.UserId &&
                         (member.Role == GroupRole.Leader || member.Role == GroupRole.Admin)),
                cancellationToken);
        }
    }

}


