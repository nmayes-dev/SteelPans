using SteelPans.Shared;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Music;

namespace SteelPans.Shared.Data;

public sealed class EnsembleGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<EnsembleGroupMember> Members { get; set; } = [];
    public List<EnsembleMidiFile> MidiFiles { get; set; } = [];
}

public sealed class EnsembleGroupMember
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public GroupRole Role { get; set; }
    public DateTimeOffset JoinedAt { get; set; }

    public EnsembleGroup Group { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
}

public sealed class EnsembleMidiFile
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid UploadedByUserId { get; set; }

    public string Title { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string ContentType { get; set; } = "audio/midi";
    public long SizeBytes { get; set; }
    public string StorageKey { get; set; } = "";

    public DateTimeOffset UploadedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public EnsembleGroup Group { get; set; } = null!;
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
    public int TrackIndex { get; set; }
    public PanType PanType { get; set; }
    public string Label { get; set; } = "";

    public EnsembleMidiFile MidiFile { get; set; } = null!;
}