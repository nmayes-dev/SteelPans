using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteelPans.Shared.Music;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PanType
{
    None,
    LeadTenor,
    DoubleGuitar,
    DoubleSecond,
    TripleCello,
    Bass,
}

public class PanNote
{
    public required Note Note { get; init; }

    [JsonIgnore]
    public bool Selected { get; set; } = false;

    [JsonIgnore]
    public bool Playing { get; set; } = false;

    [JsonIgnore]
    public bool Active => Selected || Playing;

    public override string ToString() => Note.ToString();
}

public class SteelPan
{
    public PanType Type { get; set; }

    public List<PanNote> Notes { get; set; } = new();
}
