namespace SteelPans.WebApp.Model;

public sealed class ConfigurationPan
{
    public PanType Pan { get; set; }
    public int Track { get; set; }
}

public sealed class Configuration
{
    public required Version Version { get; set; }
    public List<ConfigurationPan> Layout { get; set; } = [];
}
