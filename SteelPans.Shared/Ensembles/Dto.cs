using SteelPans.Shared.Music;

namespace SteelPans.Shared.Ensembles;

public sealed record GroupSummaryDto(
    Guid Id,
    string Name,
    string InviteCode,
    GroupRole Role);

public sealed record GroupInviteDto(
    string Token,
    DateTimeOffset ExpiresAt);

public sealed record CreateGroupRequest(
    string Name);

public sealed record GroupFileDto(
    Guid Id,
    IReadOnlyList<Guid> SharedGroupIds,
    string Title,
    string OriginalFileName,
    long SizeBytes,
    DateTimeOffset UploadedAt);

public sealed record GroupMemberSummaryDto(
    Guid UserId,
    string UserName,
    string Email,
    GroupRole Role,
    DateTimeOffset JoinedAt,
    DateTimeOffset? AdminSince);

public sealed record UpdateGroupMemberRoleRequest(
    Guid UserId,
    GroupRole Role);


public sealed record MidiTrackDto(
    int TrackIndex,
    string? TrackName,
    PanType? SuggestedPanType);

public sealed record MidiFileDetailsDto(
    Guid Id,
    string Title,
    string OriginalFileName,
    IReadOnlyList<MidiTrackDto> Tracks,
    IReadOnlyList<MidiTrackAssignmentDto> Assignments);

public sealed record MidiTrackAssignmentDto(
    int TrackIndex,
    PanType PanType,
    string Label);

public sealed record SaveMidiAssignmentsRequest(
    IReadOnlyList<MidiTrackAssignmentDto> Assignments);