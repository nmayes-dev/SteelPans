namespace SteelPans.Shared.Music;

public sealed class MidiTrackAssignment
{
    public required PanType AssignedPanType { get; init; }
    public Guid? TrackId { get; init; }
    public bool IsSelected { get; set; }
}
