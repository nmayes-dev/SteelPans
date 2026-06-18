using Melanchall.DryWetMidi.Core;
using Microsoft.EntityFrameworkCore;
using SteelPans.Shared.Auth;
using SteelPans.Shared.Data;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Extensions;
using SteelPans.Shared.Music;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SteelPans.Shared.Services;

public sealed class DbService
{
    public readonly GroupDbService Groups;
    public readonly MidiDbService MidiFiles;

    private ICurrentUserAccessor currentUser_;

    public DbService(IDbContextFactory<EnsembleDbContext> dbFactory,
        ICurrentUserAccessor currentUser,
        IEnsembleFileStore fileStore,
        MidiFileService midiInspection,
        IRealtimeUpdateDispatcher updates)
    {
        currentUser_ = currentUser;
        Groups = new(dbFactory, currentUser, updates);
        MidiFiles = new(dbFactory, currentUser, Groups, fileStore, midiInspection, updates);
    }

    public async Task<Guid> GetUserIdAsync() => await currentUser_.GetUserIdAsync();

    public sealed class GroupDbService(IDbContextFactory<EnsembleDbContext> dbFactory, ICurrentUserAccessor currentUser, IRealtimeUpdateDispatcher updates)
    {
        public async Task<IReadOnlyList<GroupSummaryDto>> GetMyGroupsAsync(
            CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var userId = await currentUser.GetUserIdAsync();
            return await db.GroupMembers
                .AsNoTracking()
                .Where(x => x.UserId == userId)
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
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var userId = await currentUser.GetUserIdAsync();
            return await db.GroupMembers
                .AsNoTracking()
                .Where(x => x.GroupId == groupId && x.UserId == userId)
                .Select(x => new GroupSummaryDto(
                    x.Group.Id,
                    x.Group.Name,
                    x.Group.InviteCode,
                    x.Role))
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<GroupSummaryDto?> GetGroupAsync(
            string token,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await db.GroupInvites
                .Where(x => x.Token == token)
                .Where(x => x.ExpiresAt > now)
                .Where(x => x.Group != null)
                .Select(x => new GroupSummaryDto(
                    x.Group.Id,
                    x.Group.Name,
                    x.Group.InviteCode,
                    GroupRole.Member))
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

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            while (await db.Groups.AnyAsync(x => x.InviteCode == inviteCode, cancellationToken))
            {
                inviteCode = EnsembleGroup.GenerateInviteCode();
            }

            var group = new EnsembleGroup
            {
                Id = Guid.NewGuid(),
                Name = name,
                InviteCode = inviteCode,
                CreatedByUserId = await currentUser.GetUserIdAsync(),
                CreatedAt = now
            };

            group.Members.Add(new EnsembleGroupMember
            {
                GroupId = group.Id,
                UserId = await currentUser.GetUserIdAsync(),
                Role = GroupRole.Leader,
                JoinedAt = now
            });

            db.Groups.Add(group);
            await db.SaveChangesAsync(cancellationToken);
            await updates.NotifyUserStateChangedAsync(await currentUser.GetUserIdAsync(), cancellationToken);

            return new GroupSummaryDto(group.Id, group.Name, group.InviteCode, GroupRole.Leader);
        }

        public async Task<GroupInviteDto> CreateInviteAsync(Guid groupId, CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            if (!await IsLeaderOrAdminAsync(db, groupId, cancellationToken))
                throw new UnauthorizedAccessException("You don't have permission to invite people to this group.");

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
                CreatedByUserId = await currentUser.GetUserIdAsync(),
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

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var invite = await db.GroupInvites
                .Include(x => x.Group)
                .ThenInclude(x => x.Members)
                .FirstOrDefaultAsync(x => x.Token == token, cancellationToken);

            if (invite is null || invite.ExpiresAt <= now)
            {
                return null;
            }

            var userId = await currentUser.GetUserIdAsync();
            var existingMember = invite.Group.Members.FirstOrDefault(x => x.UserId == userId);
            var role = existingMember?.Role ?? GroupRole.Member;

            if (existingMember is null)
            {
                invite.Group.Members.Add(new EnsembleGroupMember
                {
                    GroupId = invite.GroupId,
                    UserId = await currentUser.GetUserIdAsync(),
                    Role = role,
                    JoinedAt = now
                });
            }

            await db.SaveChangesAsync(cancellationToken);
            await updates.NotifyUserStateChangedAsync(await currentUser.GetUserIdAsync(), cancellationToken);
            await updates.NotifyGroupChangedAsync(invite.Group.Id, cancellationToken);

            return new GroupSummaryDto(invite.Group.Id, invite.Group.Name, invite.Group.InviteCode, role);
        }

        public async Task<IReadOnlyList<GroupFileDto>> GetGroupFilesAsync(Guid groupId, CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            if (!await IsMemberAsync(db, groupId, cancellationToken))
                throw new UnauthorizedAccessException("You are not a member of this group.");

            return await db.GroupMidiFiles
                .AsNoTracking()
                .Where(x => x.GroupId == groupId && x.MidiFile.ArchivedAt == null && !x.MidiFile.IsIncomplete)
                .OrderByDescending(x => x.MidiFile.UploadedAt)
                .Select(x => new GroupFileDto(
                    x.MidiFile.Id,
                    x.MidiFile.UploadedByUserId,
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
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var group = await db.Groups.Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == groupId, cancellationToken);
            if (group is null) return;

            var userId = await currentUser.GetUserIdAsync();
            var currentMember = group.Members.FirstOrDefault(x => x.UserId == userId);
            if (currentMember?.Role != GroupRole.Leader)
            {
                throw new UnauthorizedAccessException();
            }

            var memberUserIds = group.Members.Select(x => x.UserId).ToList();

            db.Groups.Remove(group);
            await db.SaveChangesAsync(cancellationToken);

            foreach (var id in memberUserIds)
            {
                await updates.NotifyUserStateChangedAsync(id, cancellationToken);
            }
        }

        public async Task<List<GroupMemberSummaryDto>> GetGroupMembersAsync(Guid groupId, CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            if (!await IsMemberAsync(db, groupId, cancellationToken))
                throw new UnauthorizedAccessException("You are not a member of this group.");

            return await db.GroupMembers
                .AsNoTracking()
                .Where(x => x.GroupId == groupId)
                .Include(x => x.User)
                .OrderBy(EntityExtensions.RoleSortOrder)
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
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var currentMember = await GetCurrentMemberAsync(db, groupId, cancellationToken);
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
            await updates.NotifyGroupChangedAsync(groupId, cancellationToken);
        }

        public async Task TransferLeadershipAsync(Guid groupId, Guid newLeaderUserId, CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var currentMember = await GetCurrentMemberAsync(db, groupId, cancellationToken);
            if (currentMember?.Role != GroupRole.Leader)
            {
                throw new UnauthorizedAccessException();
            }

            var newLeader = await db.GroupMembers.FirstOrDefaultAsync(x => x.GroupId == groupId && x.UserId == newLeaderUserId, cancellationToken)
                ?? throw new InvalidOperationException("Member was not found.");

            currentMember.Role = GroupRole.Admin;
            currentMember.AdminSince ??= DateTimeOffset.UtcNow;
            newLeader.Role = GroupRole.Leader;
            newLeader.AdminSince = null;

            await db.SaveChangesAsync(cancellationToken);
            await updates.NotifyGroupChangedAsync(groupId, cancellationToken);
        }

        public async Task RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var currentMember = await GetCurrentMemberAsync(db, groupId, cancellationToken);
            if (currentMember is null)
            {
                throw new UnauthorizedAccessException();
            }

            var member = await db.GroupMembers
                .FirstOrDefaultAsync(x => x.GroupId == groupId && x.UserId == userId, cancellationToken)
                ?? throw new InvalidOperationException("Member was not found.");

            var removingSelf = member.UserId == await currentUser.GetUserIdAsync();

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
            await updates.NotifyUserStateChangedAsync(userId, cancellationToken);
            await updates.NotifyGroupChangedAsync(groupId, cancellationToken);
        }

        public async Task LeaveGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
            => await RemoveMemberAsync(groupId, await currentUser.GetUserIdAsync(), cancellationToken);

        public async Task<bool> IsMemberAsync(EnsembleDbContext db, Guid groupId, CancellationToken cancellationToken = default)
        {
            var userId = await currentUser.GetUserIdAsync();
            return await db.GroupMembers.AnyAsync(x => x.GroupId == groupId && x.UserId == userId, cancellationToken);
        }

        public async Task<bool> IsLeaderAsync(EnsembleDbContext db, Guid groupId, CancellationToken cancellationToken = default)
        {
            var userId = await currentUser.GetUserIdAsync();
            return await db.GroupMembers.AnyAsync(x => x.GroupId == groupId && x.UserId == userId && x.Role == GroupRole.Leader, cancellationToken);
        }

        public async Task<bool> IsLeaderOrAdminAsync(EnsembleDbContext db, Guid groupId, CancellationToken cancellationToken = default)
        {
            var userId = await currentUser.GetUserIdAsync();
            return await db.GroupMembers.AnyAsync(x => x.GroupId == groupId && x.UserId == userId && (x.Role == GroupRole.Leader || x.Role == GroupRole.Admin), cancellationToken);
        }

        private async Task<EnsembleGroupMember?> GetCurrentMemberAsync(EnsembleDbContext db, Guid groupId, CancellationToken cancellationToken)
        {
            var userId = await currentUser.GetUserIdAsync();
            return await db.GroupMembers.FirstOrDefaultAsync(x => x.GroupId == groupId && x.UserId == userId, cancellationToken);
        }

        private async Task PromoteReplacementLeaderOrDeleteAsync(Guid groupId, bool leaderWasRemoved, CancellationToken cancellationToken)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
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

    public sealed class MidiDbService(
        IDbContextFactory<EnsembleDbContext> dbFactory,
        ICurrentUserAccessor currentUser,
        GroupDbService groups,
        IEnsembleFileStore fileStore,
        MidiFileService midiInspection,
        IRealtimeUpdateDispatcher updates)
    {
        public async Task<GroupFileDto> UploadMidiFileAsync(
            string originalFileName,
            string? contentType,
            long sizeBytes,
            Stream content,
            CancellationToken cancellationToken = default)
        {
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

            var parsedMidiFile = await midiInspection.OpenMidiFileAsync(buffer, cancellationToken);
            var tracks = midiInspection.GetTrackInfos(parsedMidiFile);

            var fileId = Guid.NewGuid();

            buffer.Position = 0;
            var storageKey = await fileStore.SaveAsync(
                await currentUser.GetUserIdAsync(),
                fileId,
                originalFileName,
                buffer,
                cancellationToken);

            var midiFile = new EnsembleMidiFile
            {
                Id = fileId,
                UploadedByUserId = await currentUser.GetUserIdAsync(),
                Title = Path.GetFileNameWithoutExtension(originalFileName),
                OriginalFileName = Path.GetFileName(originalFileName),
                ContentType = string.IsNullOrWhiteSpace(contentType)
                    ? "audio/midi"
                    : contentType,
                SizeBytes = sizeBytes,
                StorageKey = storageKey,
                UploadedAt = DateTimeOffset.UtcNow
            };

            foreach (var (index, track) in tracks.Enumerate())
            {
                var persistedTrack = new EnsembleMidiTrack
                {
                    Id = track.Id == Guid.Empty ? Guid.NewGuid() : track.Id,
                    MidiFileId = fileId,
                    TrackIndex = index + 1,
                    TrackName = track.Name
                };

                midiFile.Tracks.Add(persistedTrack);

                midiFile.Assignments.Add(new EnsembleMidiTrackAssignment
                {
                    Id = Guid.NewGuid(),
                    MidiFileId = fileId,
                    TrackId = persistedTrack.Id,
                    TrackIndex = persistedTrack.TrackIndex,
                    PanType = PanType.None,
                    Label = track.Name ?? $"Track {persistedTrack.TrackIndex}",
                });
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            db.MidiFiles.Add(midiFile);

            await db.SaveChangesAsync(cancellationToken);

            await SaveMidiAssignmentsAsync(
                fileId,
                new SaveMidiAssignmentsRequest(midiFile.Assignments
                    .OrderBy(x => x.TrackIndex)
                    .Select(x => new MidiTrackAssignmentDto(
                            x.TrackId,
                            x.TrackIndex,
                            x.PanType,
                            x.Label))
                    .ToList()),
                cancellationToken);

            await updates.NotifyUserStateChangedAsync(await currentUser.GetUserIdAsync(), cancellationToken);

            return new GroupFileDto(
                midiFile.Id,
                midiFile.UploadedByUserId,
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
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var file = await db.MidiFiles
                .AsNoTracking()
                .AsSplitQuery()
                .Include(x => x.Tracks)
                .Include(x => x.Assignments)
                .Include(x => x.SharedGroups)
                .FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);

            if (file is null)
            {
                return null;
            }

            if (!await CanAccessFileAsync(db, file, cancellationToken))
                throw new UnauthorizedAccessException("You don't have access to this file.");

            return new MidiFileDetailsDto(
                file.Id,
                file.UploadedByUserId,
                file.Title,
                file.OriginalFileName,
                file.Tracks
                    .OrderBy(x => x.TrackIndex)
                    .Select(x => new MidiTrackDto(
                        x.Id == Guid.Empty ? Guid.NewGuid() : x.Id,
                        x.TrackIndex,
                        x.TrackName,
                        x.SuggestedPanType))
                    .ToList(),
                file.Assignments
                    .OrderBy(x => x.TrackIndex)
                    .Select(x => new MidiTrackAssignmentDto(
                        x.TrackId,
                        x.TrackIndex,
                        x.PanType,
                        x.Label))
                    .ToList());
        }

        public async Task<MidiFileDownload?> OpenMidiFileAsync(
            Guid fileId,
            CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var file = await db.MidiFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);

            if (file is null)
            {
                return null;
            }

            if (!await CanAccessFileAsync(db, file, cancellationToken))
                throw new UnauthorizedAccessException("You don't have access to this file.");

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
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var file = await db.MidiFiles
                .Include(x => x.Tracks)
                .Include(x => x.Assignments)
                .Include(x => x.SharedGroups)
                .FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);

            if (file is null)
            {
                throw new InvalidOperationException("MIDI file not found.");
            }

            if (!await CanEditFileAsync(db, file, cancellationToken))
                throw new UnauthorizedAccessException("You don't have permissions to edit this file.");

            db.MidiTrackAssignments.RemoveRange(file.Assignments);

            var tracksById = file.Tracks.ToDictionary(x => x.Id);

            foreach (var assignment in request.Assignments)
            {
                if (!tracksById.TryGetValue(assignment.TrackId, out var track))
                    continue;

                db.MidiTrackAssignments.Add(new EnsembleMidiTrackAssignment
                {
                    Id = Guid.NewGuid(),
                    MidiFileId = fileId,
                    TrackId = track.Id,
                    TrackIndex = track.TrackIndex,
                    PanType = assignment.PanType,
                    Label = assignment.Label.Trim()
                });
            }

            var sharedGroupIds = file.SharedGroups
                .Select(x => x.GroupId)
                .ToList();

            await db.SaveChangesAsync(cancellationToken);
            await updates.NotifyUserStateChangedAsync(file.UploadedByUserId, cancellationToken);
            await updates.NotifyGroupsChangedAsync(sharedGroupIds, cancellationToken);
            await updates.NotifyMidiAssignmentsChangedAsync(
                file.Id,
                file.UploadedByUserId,
                sharedGroupIds,
                cancellationToken);
        }

        public sealed record CreateRecordedMidiFileRequest(
            string Title,
            int TempoBpm,
            int BeatsPerBar,
            int BeatUnit,
            IReadOnlyList<CreateRecordedMidiTrackRequest> Tracks,
            bool IsIncomplete = false);

        public sealed record CreateRecordedMidiTrackRequest(
            string Name,
            PanType PanType,
            double DurationSeconds,
            IReadOnlyList<CreateRecordedMidiNoteRequest> Notes);

        public sealed record CreateRecordedMidiNoteRequest(
            Note Note,
            double StartSeconds,
            double DurationSeconds);

        public async Task<GroupFileDto> CreateRecordedMidiFileAsync(
            CreateRecordedMidiFileRequest request,
            CancellationToken cancellationToken = default)
        {
            var title = string.IsNullOrWhiteSpace(request.Title)
                ? "Recorded MIDI"
                : request.Title.Trim();

            if (request.Tracks.Count == 0)
                throw new InvalidOperationException("Add at least one recorded track before saving the file.");

            var fileId = Guid.NewGuid();
            var originalFileName = $"{SanitizeFileName(title)}.mid";
            await using var midiContent = new MemoryStream();
            WriteRecordedMidiFile(midiContent, request);
            midiContent.Position = 0;

            var storageKey = await fileStore.SaveAsync(
                await currentUser.GetUserIdAsync(),
                fileId,
                originalFileName,
                midiContent,
                cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var midiFile = new EnsembleMidiFile
            {
                Id = fileId,
                UploadedByUserId = await currentUser.GetUserIdAsync(),
                Title = title,
                OriginalFileName = originalFileName,
                ContentType = "audio/midi",
                SizeBytes = midiContent.Length,
                StorageKey = storageKey,
                UploadedAt = now,
                IsIncomplete = request.IsIncomplete
            };

            foreach (var (index, track) in request.Tracks.Enumerate())
            {
                var trackIndex = index + 1;
                var trackName = string.IsNullOrWhiteSpace(track.Name)
                    ? $"Track {trackIndex}"
                    : track.Name.Trim();

                var persistedTrack = new EnsembleMidiTrack
                {
                    Id = Guid.NewGuid(),
                    MidiFileId = fileId,
                    TrackIndex = trackIndex,
                    TrackName = trackName,
                    SuggestedPanType = track.PanType
                };

                midiFile.Tracks.Add(persistedTrack);

                midiFile.Assignments.Add(new EnsembleMidiTrackAssignment
                {
                    Id = Guid.NewGuid(),
                    MidiFileId = fileId,
                    TrackId = persistedTrack.Id,
                    TrackIndex = persistedTrack.TrackIndex,
                    PanType = track.PanType,
                    Label = trackName
                });
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            db.MidiFiles.Add(midiFile);
            await db.SaveChangesAsync(cancellationToken);
            await updates.NotifyUserStateChangedAsync(await currentUser.GetUserIdAsync(), cancellationToken);

            return new GroupFileDto(
                midiFile.Id,
                midiFile.UploadedByUserId,
                [],
                midiFile.Title,
                midiFile.OriginalFileName,
                midiFile.SizeBytes,
                midiFile.UploadedAt);
        }

        private static void WriteRecordedMidiFile(Stream output, CreateRecordedMidiFileRequest request)
        {
            const short ticksPerQuarter = 480;
            using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
            WriteAscii(writer, "MThd");
            WriteInt32BE(writer, 6);
            WriteInt16BE(writer, 1);
            WriteInt16BE(writer, (short)(request.Tracks.Count + 1));
            WriteInt16BE(writer, ticksPerQuarter);

            using (var meta = new MemoryStream())
            using (var metaWriter = new BinaryWriter(meta, Encoding.ASCII, leaveOpen: true))
            {
                WriteVarLength(metaWriter, 0);
                metaWriter.Write((byte)0xFF);
                metaWriter.Write((byte)0x51);
                metaWriter.Write((byte)0x03);
                var microsecondsPerQuarter = 60_000_000 / Math.Clamp(request.TempoBpm, 1, 999);
                metaWriter.Write((byte)((microsecondsPerQuarter >> 16) & 0xFF));
                metaWriter.Write((byte)((microsecondsPerQuarter >> 8) & 0xFF));
                metaWriter.Write((byte)(microsecondsPerQuarter & 0xFF));

                WriteVarLength(metaWriter, 0);
                metaWriter.Write((byte)0xFF);
                metaWriter.Write((byte)0x58);
                metaWriter.Write((byte)0x04);
                metaWriter.Write((byte)Math.Clamp(request.BeatsPerBar, 1, 32));
                metaWriter.Write((byte)GetBeatUnitPower(Math.Clamp(request.BeatUnit, 1, 64)));
                metaWriter.Write((byte)24);
                metaWriter.Write((byte)8);

                WriteEndOfTrack(metaWriter);
                WriteTrackChunk(writer, meta.ToArray());
            }

            for (var i = 0; i < request.Tracks.Count; i++)
            {
                var channel = i % 16;
                var track = request.Tracks[i];
                var events = new List<(long Tick, int Order, byte[] Data)>();
                var nameBytes = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(track.Name) ? $"Track {i + 1}" : track.Name.Trim());
                events.Add((0, 0, [(byte)0xFF, (byte)0x03, (byte)nameBytes.Length, .. nameBytes]));
                events.Add((0, 1, [(byte)(0xC0 | channel), (byte)12]));

                foreach (var note in track.Notes)
                {
                    var midi = Math.Clamp(note.Note.ToMidi(), 0, 127);
                    var startTick = SecondsToTicks(note.StartSeconds, request.TempoBpm, ticksPerQuarter);
                    var endTick = SecondsToTicks(note.StartSeconds + Math.Max(0.05, note.DurationSeconds), request.TempoBpm, ticksPerQuarter);
                    events.Add((startTick, 2, [(byte)(0x90 | channel), (byte)midi, (byte)96]));
                    events.Add((Math.Max(startTick + 1, endTick), 1, [(byte)(0x80 | channel), (byte)midi, (byte)0]));
                }

                events.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));

                using var trackStream = new MemoryStream();
                using var trackWriter = new BinaryWriter(trackStream, Encoding.ASCII, leaveOpen: true);
                var previousTick = 0L;
                foreach (var midiEvent in events)
                {
                    WriteVarLength(trackWriter, midiEvent.Tick - previousTick);
                    trackWriter.Write(midiEvent.Data);
                    previousTick = midiEvent.Tick;
                }

                WriteEndOfTrack(trackWriter);
                WriteTrackChunk(writer, trackStream.ToArray());
            }
        }

        private static long SecondsToTicks(double seconds, int tempoBpm, short ticksPerQuarter)
            => (long)Math.Round(Math.Max(0, seconds) * Math.Clamp(tempoBpm, 1, 999) / 60.0 * ticksPerQuarter);

        private static int GetBeatUnitPower(int beatUnit)
        {
            var power = 0;
            var value = 1;
            while (value < beatUnit)
            {
                value *= 2;
                power++;
            }
            return power;
        }

        private static void WriteTrackChunk(BinaryWriter writer, byte[] data)
        {
            WriteAscii(writer, "MTrk");
            WriteInt32BE(writer, data.Length);
            writer.Write(data);
        }

        private static void WriteEndOfTrack(BinaryWriter writer)
        {
            WriteVarLength(writer, 0);
            writer.Write((byte)0xFF);
            writer.Write((byte)0x2F);
            writer.Write((byte)0x00);
        }

        private static void WriteAscii(BinaryWriter writer, string value)
            => writer.Write(Encoding.ASCII.GetBytes(value));

        private static void WriteInt16BE(BinaryWriter writer, short value)
        {
            writer.Write((byte)((value >> 8) & 0xFF));
            writer.Write((byte)(value & 0xFF));
        }

        private static void WriteInt32BE(BinaryWriter writer, int value)
        {
            writer.Write((byte)((value >> 24) & 0xFF));
            writer.Write((byte)((value >> 16) & 0xFF));
            writer.Write((byte)((value >> 8) & 0xFF));
            writer.Write((byte)(value & 0xFF));
        }

        private static void WriteVarLength(BinaryWriter writer, long value)
        {
            var buffer = value & 0x7F;
            while ((value >>= 7) > 0)
            {
                buffer <<= 8;
                buffer |= ((value & 0x7F) | 0x80);
            }

            while (true)
            {
                writer.Write((byte)(buffer & 0xFF));
                if ((buffer & 0x80) == 0)
                    break;
                buffer >>= 8;
            }
        }

        private static string SanitizeFileName(string title)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(title.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(sanitized) ? "recorded-midi" : sanitized;
        }

        public async Task DeleteMidiFileAsync(Guid fileId, CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var userId = await currentUser.GetUserIdAsync();
            var file = await db.MidiFiles
                .Include(x => x.SharedGroups)
                .FirstOrDefaultAsync(x => x.Id == fileId && x.UploadedByUserId == userId, cancellationToken);

            if (file is null)
            {
                throw new InvalidOperationException("MIDI file was not found.");
            }

            var sharedGroupIds = file.SharedGroups.Select(x => x.GroupId).ToList();

            db.MidiFiles.Remove(file);
            await db.SaveChangesAsync(cancellationToken);
            await updates.NotifyUserStateChangedAsync(await currentUser.GetUserIdAsync(), cancellationToken);
            await updates.NotifyGroupsChangedAsync(sharedGroupIds, cancellationToken);
        }
        public async Task<IReadOnlyList<GroupFileDto>> GetMyMidiFilesAsync(
            CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var userId = await currentUser.GetUserIdAsync();
            return await db.MidiFiles
                .AsNoTracking()
                .Where(x => x.UploadedByUserId == userId &&
                            x.ArchivedAt == null &&
                            !x.IsIncomplete)
                .OrderByDescending(x => x.UploadedAt)
                .Select(x => new GroupFileDto(
                    x.Id,
                    x.UploadedByUserId,
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
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            if (!await groups.IsLeaderOrAdminAsync(db, groupId, cancellationToken))
                throw new UnauthorizedAccessException("You don't have permission to unshare files in this group.");

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
            await updates.NotifyUserStateChangedAsync(await currentUser.GetUserIdAsync(), cancellationToken);
            await updates.NotifyGroupChangedAsync(groupId, cancellationToken);
        }

        public async Task ShareMidiFileWithGroupAsync(
            Guid fileId,
            Guid groupId,
            CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            if (!await groups.IsLeaderOrAdminAsync(db, groupId, cancellationToken))
                throw new UnauthorizedAccessException("You don't have permission to share files in this group.");

            var userId = await currentUser.GetUserIdAsync();
            var file = await db.MidiFiles
                .FirstOrDefaultAsync(x =>
                    x.Id == fileId &&
                    x.UploadedByUserId == userId &&
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
                await updates.NotifyUserStateChangedAsync(await currentUser.GetUserIdAsync(), cancellationToken);
                await updates.NotifyGroupChangedAsync(groupId, cancellationToken);
            }
        }

        private async Task<bool> CanAccessFileAsync(
            EnsembleDbContext db,
            EnsembleMidiFile file,
            CancellationToken cancellationToken)
        {
            if (file.UploadedByUserId == await currentUser.GetUserIdAsync())
            {
                return true;
            }

            var userId = await currentUser.GetUserIdAsync();
            return await db.GroupMidiFiles.AnyAsync(
                x => x.MidiFileId == file.Id &&
                     x.MidiFile.ArchivedAt == null &&
                     x.Group.Members.Any(member => member.UserId == userId),
                cancellationToken);
        }

        private async Task<bool> CanEditFileAsync(
            EnsembleDbContext db,
            EnsembleMidiFile file,
            CancellationToken cancellationToken)
        {
            if (file.UploadedByUserId == await currentUser.GetUserIdAsync())
            {
                return true;
            }

            var userId = await currentUser.GetUserIdAsync();
            return await db.GroupMidiFiles.AnyAsync(
                x => x.MidiFileId == file.Id &&
                     x.MidiFile.ArchivedAt == null &&
                     x.Group.Members.Any(member =>
                         member.UserId == userId &&
                         (member.Role == GroupRole.Leader || member.Role == GroupRole.Admin)),
                cancellationToken);
        }
    }

}


