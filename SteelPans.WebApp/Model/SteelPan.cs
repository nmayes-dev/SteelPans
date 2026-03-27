using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteelPans.WebApp.Model;

public enum PanType
{
    None,
    Lead,
    DoubleGuitar,
    DoubleSecond,
}

public class PanNote
{
    public required Note Note { get; init; }
    public bool Active { get; set; } = false;

    public override string ToString() => Note.ToString();
}

public class SteelPan
{
    public PanType PanType { get; set; }

    public List<PanNote> Notes { get; set; } = new();
}
