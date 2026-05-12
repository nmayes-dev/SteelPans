namespace SteelPans.WebApp.Model;

public sealed class MidiTrackAssignment
{
    public required PanType AssignedPanType { get; init; }
    public MidiTrackInfo? Track { get; init; }
    public bool IsSelected { get; set; }
}
