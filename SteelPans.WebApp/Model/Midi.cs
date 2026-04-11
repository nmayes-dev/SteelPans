namespace SteelPans.WebApp.Model;

public sealed class MidiTrackInfo
{
    public required int Index { get; init; }
    public string? Name { get; init; }
    public int NoteCount { get; init; }
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
    public required Note Note { get; init; }
    public required TimeSpan Start { get; init; }
    public required TimeSpan Duration { get; init; }

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