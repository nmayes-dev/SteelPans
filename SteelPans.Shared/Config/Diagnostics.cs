namespace SteelPans.Shared.Config;

public sealed class ConnectionDetails
{
    public string? Url { get; set; }
    public string? UserAgent { get; set; }
    public string? Platform { get; set; }
    public string? Language { get; set; }

    public ScreenDetails? Screen { get; set; }
    public ViewportDetails? Viewport { get; set; }

    public bool Mobile { get; set; }
    public bool Touch { get; set; }
    public bool Online { get; set; }

    public NetworkConnectionDetails? Connection { get; set; }
}

public sealed class ScreenDetails
{
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class ViewportDetails
{
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class NetworkConnectionDetails
{
    public string? EffectiveType { get; set; }
    public string? Type { get; set; }

    public double? Downlink { get; set; }
    public int? Rtt { get; set; }

    public bool SaveData { get; set; }
}