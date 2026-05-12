namespace SteelPans.WebApp.Model;

public sealed class MidiAssignedPan
{
    public required Guid InstanceId { get; init; }
    public required MidiTrackAssignment Assignment { get; init; }
    public required SteelPan Pan { get; init; }
    public List<MidiPanEvent> Events { get; set; } = [];
    public double Volume { get; set; } = 1.0;
    public bool Muted { get; set; } = false;
    public bool Soloing { get; set; } = false;
}
