namespace SteelPans.Shared.Music;

public static class Midi
{
    public static readonly string[] Extensions = [
        ".mid",
        ".midi",
        "audio/midi",
        "audio/x-midi"
    ];
}

public class MidiTrackSummary
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required int Index { get; init; }
    public string? Name { get; init; }
    public int NoteCount { get; init; }
    public PanType PanType { get; init; } = PanType.None;
    public int TempoBpm { get; init; } = 120;
    public int BeatsPerBar { get; init; } = 4;
    public int BeatUnit { get; init; } = 4;
    public double DurationSeconds { get; init; }

    public string DisplayInfo
    {
        get
        {
            var baseName = string.IsNullOrWhiteSpace(Name)
                ? $"Track {Index + 1}"
                : Name;

            var noteCount = $" ({NoteCount} notes)";
            return $"{baseName}{noteCount}";
        }
    }

    public string TrackLabel => Name ?? $"Track {Index}";
}

public sealed class MidiTrackInfo : MidiTrackSummary
{
    public List<MidiPanEvent> Events { get; init; } = [];
}

public sealed class MidiPanPlaybackAction
{
    public required Note Note { get; init; }
    public required TimeSpan Time { get; init; }
    public required bool IsNoteOn { get; init; }
}

public sealed class MidiPanScheduledAction
{
    public required string NoteKey { get; init; }
    public required double TimeSeconds { get; init; }
    public required bool IsNoteOn { get; init; }
}

public sealed class MidiPanEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Note Note { get; set; }
    public required TimeSpan Start { get; set; }
    public required TimeSpan Duration { get; set; }

    public TimeSpan End => Start + Duration;
}

public sealed class MidiTempoChange
{
    public required TimeSpan Time { get; init; }
    public required int Bpm { get; init; }
}

public sealed class MidiTimeSignatureChange
{
    public required TimeSpan Time { get; init; }
    public required int Numerator { get; init; }
    public required int Denominator { get; init; }
}

public sealed class MidiPlaybackInfo
{
    public required int InitialBpm { get; init; }
    public required int InitialBeatsPerBar { get; init; }
    public required int InitialBeatUnit { get; init; }

    public List<MidiTempoChange> TempoChanges { get; init; } = [];
    public List<MidiTimeSignatureChange> TimeSignatureChanges { get; init; } = [];
}

public sealed class MetronomeAction
{
    public required double TimeSeconds { get; init; }
    public required bool IsAccent { get; init; }
}
