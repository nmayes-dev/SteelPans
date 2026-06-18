using SteelPans.Shared.Music;

namespace SteelPans.WebApp.Services;

public sealed class RecordDraftService
{
    public List<RecordTrackDraft> Tracks { get; } = [];

    public RecordTrackDraft? GetTrack(Guid id)
    {
        return Tracks.FirstOrDefault(x => x.Id == id);
    }

    public RecordTrackDraft CreateTrack(RecordTrackDraft track)
    {
        var copy = track with
        {
            Id = track.Id == Guid.Empty ? Guid.NewGuid() : track.Id,
            Events = CloneEvents(track.Events)
        };

        Tracks.Add(copy);
        return copy;
    }

    public bool UpdateTrack(Guid id, RecordTrackDraft track)
    {
        var index = Tracks.FindIndex(x => x.Id == id);
        if (index < 0)
            return false;

        Tracks[index] = track with
        {
            Id = id,
            Events = CloneEvents(track.Events)
        };

        return true;
    }

    public RecordTrackDraft CreateOrUpdateTrack(RecordTrackDraft track)
    {
        if (track.Id != Guid.Empty && UpdateTrack(track.Id, track))
            return GetTrack(track.Id)!;

        return CreateTrack(track);
    }

    public void AddOrReplaceTrack(RecordTrackDraft track)
    {
        CreateOrUpdateTrack(track);
    }

    public void RemoveTrack(Guid id)
    {
        Tracks.RemoveAll(x => x.Id == id);
    }

    public void Clear()
    {
        Tracks.Clear();
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

public sealed record RecordTrackDraft
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Recorded track";
    public PanType PanType { get; init; } = PanType.LeadTenor;
    public int TempoBpm { get; init; } = 120;
    public int BeatsPerBar { get; init; } = 4;
    public int BeatUnit { get; init; } = 4;
    public double DurationSeconds { get; init; } = 60;
    public List<MidiPanEvent> Events { get; init; } = [];
}
