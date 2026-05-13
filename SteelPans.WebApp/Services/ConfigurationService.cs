using SteelPans.WebApp.Model;

namespace SteelPans.WebApp.Services
{
    public sealed class StartupOptions
    {
        public string? MidiFile { get; set; }

        public List<StartupPan> Pans { get; set; } = [];
    }

    public sealed class StartupPan
    {
        public PanType Pan { get; set; }

        public int Track { get; set; }
    }

    public class ConfigurationService
    {
    }
}
