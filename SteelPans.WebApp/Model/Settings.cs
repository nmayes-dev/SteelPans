namespace SteelPans.WebApp.Model
{
    public sealed class StartupConfig
    {
        public string MidiFilePath { get; set; } = string.Empty;
        public List<ConfigurationPan> Layout { get; set; } = [];
    }

    public sealed class Settings
    {
        public required Version Version { get; set; }
        public required Version LayoutFileVersion { get; set; }
        public bool UseStartupConfig { get; set; } = false;
        public StartupConfig StartupConfig { get; set; } = new();

    }
}
