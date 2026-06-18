using SteelPans.Shared.Music;

namespace SteelPans.WebApp.Services;

public sealed class MidiEditingService
{
    private readonly UserStateService state_;

    public MidiEditingService(UserStateService state)
    {
        state_ = state;
    }

    public List<MidiTrackInfo> Tracks { get; } = [];

    public string Title { get; private set; } = string.Empty;
    public bool HasOpenFile { get; private set; }
    public bool HasUnsavedChanges { get; private set; }

    public void BeginCreateFile(string? title = null, bool resetExisting = false)
    {
        if (resetExisting || !HasOpenFile)
        {
            Tracks.Clear();
            Title = string.IsNullOrWhiteSpace(title) ? string.Empty : title.Trim();
            HasUnsavedChanges = false;
        }

        HasOpenFile = true;
        SyncActiveState();
    }

    public void SetTitle(string title)
    {
        Title = title;
        HasOpenFile = true;
        HasUnsavedChanges = true;
        SyncActiveState();
    }

    public MidiTrackInfo CreateEditableTrack(Guid? trackId = null)
    {
        var track = trackId is Guid id ? GetTrack(id) : null;
        return track is null
            ? new MidiTrackInfo
            {
                Id = trackId ?? Guid.NewGuid(),
                Index = GetNextTrackIndex(),
                Name = $"Track {GetNextTrackIndex() + 1}",
                TempoBpm = 120,
                BeatsPerBar = 4,
                BeatUnit = 4,
                DurationSeconds = 0,
                Events = []
            }
            : CloneTrack(track, track.Id, track.Index);
    }

    public MidiTrackInfo CreateRecordingScratch(MidiTrackInfo source)
    {
        return CloneTrack(source, source.Id == Guid.Empty ? Guid.NewGuid() : source.Id, source.Index);
    }

    public MidiTrackInfo CommitEditableTrack(MidiTrackInfo track)
    {
        HasOpenFile = true;
        HasUnsavedChanges = true;
        return CreateOrUpdateTrack(track);
    }

    public void MarkSaved()
    {
        HasUnsavedChanges = false;
    }

    public MidiTrackInfo? GetTrack(Guid id)
    {
        return Tracks.FirstOrDefault(x => x.Id == id)
            ?? state_.ActiveMidiTracks.FirstOrDefault(x => x.Id == id);
    }

    public MidiTrackInfo CreateTrack(MidiTrackInfo track)
    {
        var copy = CloneTrack(track, track.Id == Guid.Empty ? Guid.NewGuid() : track.Id, GetNextTrackIndex());
        Tracks.Add(copy);
        HasOpenFile = true;
        HasUnsavedChanges = true;
        state_.UpsertActiveMidiTrack(copy);
        return copy;
    }

    public bool UpdateTrack(Guid id, MidiTrackInfo track)
    {
        var index = Tracks.FindIndex(x => x.Id == id);
        var existing = GetTrack(id);
        if (index < 0 && existing is null)
            return false;

        var copy = CloneTrack(track, id, existing?.Index ?? track.Index);

        if (index >= 0)
            Tracks[index] = copy;
        else
            Tracks.Add(copy);

        HasOpenFile = true;
        HasUnsavedChanges = true;
        state_.UpsertActiveMidiTrack(copy);
        return true;
    }

    public MidiTrackInfo CreateOrUpdateTrack(MidiTrackInfo track)
    {
        if (track.Id != Guid.Empty && UpdateTrack(track.Id, track))
            return GetTrack(track.Id)!;

        return CreateTrack(track);
    }

    public void AddOrReplaceTrack(MidiTrackInfo track)
    {
        var id = track.Id == Guid.Empty ? Guid.NewGuid() : track.Id;
        var index = Tracks.FindIndex(x => x.Id == id);
        var copy = CloneTrack(track, id, track.Index);

        if (index >= 0)
            Tracks[index] = copy;
        else
            Tracks.Add(copy);

        HasOpenFile = true;
        HasUnsavedChanges = true;
        state_.UpsertActiveMidiTrack(copy);
    }

    public void RemoveTrack(Guid id)
    {
        if (Tracks.RemoveAll(x => x.Id == id) > 0)
        {
            HasOpenFile = true;
            HasUnsavedChanges = true;
            SyncActiveState();
        }
    }

    public void Clear()
    {
        Tracks.Clear();
        Title = string.Empty;
        HasOpenFile = false;
        HasUnsavedChanges = false;
        state_.ClearActiveMidiFile();
    }

    private void SyncActiveState()
    {
        state_.SetActiveMidiFile(Title, Tracks, Tracks.FirstOrDefault() is { } first
            ? new MidiPlaybackInfo
            {
                InitialBpm = first.TempoBpm,
                InitialBeatsPerBar = first.BeatsPerBar,
                InitialBeatUnit = first.BeatUnit
            }
            : null);
    }

    private int GetNextTrackIndex()
    {
        return Tracks.Count == 0 ? 0 : Tracks.Max(x => x.Index) + 1;
    }

    private static MidiTrackInfo CloneTrack(MidiTrackInfo track, Guid id, int index)
    {
        var events = CloneEvents(track.Events);
        return new MidiTrackInfo
        {
            Id = id,
            Index = index,
            Name = string.IsNullOrWhiteSpace(track.Name) ? $"Track {index + 1}" : track.Name.Trim(),
            NoteCount = events.Count,
            PanType = track.PanType,
            TempoBpm = track.TempoBpm,
            BeatsPerBar = track.BeatsPerBar,
            BeatUnit = track.BeatUnit,
            DurationSeconds = track.DurationSeconds,
            Events = events
        };
    }

    private static List<MidiPanEvent> CloneEvents(IEnumerable<MidiPanEvent> events)
    {
        return events
            .OrderBy(x => x.Start)
            .ThenBy(x => x.Note.SemitoneNumber)
            .Select(x => new MidiPanEvent
            {
                Id = x.Id == Guid.Empty ? Guid.NewGuid() : x.Id,
                Note = x.Note,
                Start = x.Start,
                Duration = x.Duration
            })
            .ToList();
    }
}
