using SteelPans.Shared.Music;

namespace SteelPans.WebApp.Services;

public sealed class RecordDraftService
{
    public List<RecordTrackDraft> Tracks { get; } = [];

    public void AddOrReplaceTrack(RecordTrackDraft track)
    {
        var index = Tracks.FindIndex(x => x.Id == track.Id);
        if (index >= 0)
            Tracks[index] = track;
        else
            Tracks.Add(track);
    }

    public void RemoveTrack(Guid id)
    {
        Tracks.RemoveAll(x => x.Id == id);
    }

    public void Clear()
    {
        Tracks.Clear();
    }
}

public sealed class RecordTrackDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Recorded track";
    public PanType PanType { get; set; } = PanType.LeadTenor;
    public int TempoBpm { get; set; } = 120;
    public int BeatsPerBar { get; set; } = 4;
    public int BeatUnit { get; set; } = 4;
    public double DurationSeconds { get; set; } = 60;
    public List<RecordNoteDraft> Notes { get; set; } = [];
}

public sealed class RecordNoteDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Note Note { get; set; }
    public double StartSeconds { get; set; }
    public double DurationSeconds { get; set; } = 0.5;
}
