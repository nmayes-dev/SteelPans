namespace SteelPans.WebApp.Model
{
    public sealed class StartupSettings
    {
        public string? MidiFile { get; set; }

        public List<StartupPan> Tracks { get; set; } = [];
    }

    public sealed class StartupPan
    {
        public PanType Pan { get; set; }
        public int Track { get; set; }
    }
}
