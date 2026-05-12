namespace SteelPans.WebApp.Model;

public sealed class MidiAssignedPan
{
    public required Guid InstanceId { get; init; }
    public required int Index { get; init; }
    public required string Label { get; init; }
    public required PanType PanType { get; init; }
    public required SteelPan Pan { get; init; }
    public List<MidiPanEvent> Events { get; set; } = [];
    public double Volume { get; set; } = 1.0;
    public bool Muted { get; set; } = false;
}
