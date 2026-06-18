using SteelPans.Shared;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Music;
using System.Security.Cryptography;

namespace SteelPans.Shared.Data;

public sealed class EnsembleGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string InviteCode { get; set; } = "";
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<EnsembleGroupMember> Members { get; set; } = [];
    public List<EnsembleGroupMidiFile> SharedMidiFiles { get; set; } = [];
    public List<EnsembleGroupInvite> Invites { get; set; } = [];

    public static string GenerateInviteCode()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed class EnsembleGroupMember
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public GroupRole Role { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? AdminSince { get; set; }

    public EnsembleGroup Group { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
}


public sealed class EnsembleGroupInvite
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public string Token { get; set; } = "";
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }

    public EnsembleGroup Group { get; set; } = null!;
}

public sealed class EnsembleGroupMidiFile
{
    public Guid GroupId { get; set; }
    public Guid MidiFileId { get; set; }
    public DateTimeOffset SharedAt { get; set; }

    public EnsembleGroup Group { get; set; } = null!;
    public EnsembleMidiFile MidiFile { get; set; } = null!;
}

public sealed class EnsembleMidiFile
{
    public Guid Id { get; set; }
    public Guid UploadedByUserId { get; set; }

    public string Title { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string ContentType { get; set; } = "audio/midi";
    public long SizeBytes { get; set; }
    public string StorageKey { get; set; } = "";

    public DateTimeOffset UploadedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public bool IsIncomplete { get; set; }

    public List<EnsembleGroupMidiFile> SharedGroups { get; set; } = [];
    public List<EnsembleMidiTrack> Tracks { get; set; } = [];
    public List<EnsembleMidiTrackAssignment> Assignments { get; set; } = [];
}

public sealed class EnsembleMidiTrack
{
    public Guid Id { get; set; }
    public Guid MidiFileId { get; set; }
    public int TrackIndex { get; set; }
    public string? TrackName { get; set; }
    public PanType? SuggestedPanType { get; set; }

    public EnsembleMidiFile MidiFile { get; set; } = null!;
}

public sealed class EnsembleMidiTrackAssignment
{
    public Guid Id { get; set; }
    public Guid MidiFileId { get; set; }
    public Guid TrackId { get; set; }
    public int TrackIndex { get; set; }
    public PanType PanType { get; set; }
    public string Label { get; set; } = "";

    public EnsembleMidiFile MidiFile { get; set; } = null!;
    public EnsembleMidiTrack Track { get; set; } = null!;
}