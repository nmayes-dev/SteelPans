using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteelPans.WebApp.Model;

public enum PanType
{
    None,
    Lead,
    DoubleGuitar
}

public static class EnumExtensions
{
    private static readonly Regex _split =
        new(@"([a-z0-9])([A-Z])", RegexOptions.Compiled);

    public static string ToKebabCase(this PanType value)
    {
        var name = value.ToString();
        return _split.Replace(name, "$1-$2").ToLowerInvariant();
    }
}

public enum Pitch
{
    A,
    B,
    C,
    D,
    E,
    F,
    G,
}

public enum Accidental
{
    Flat,
    Natural,
    Sharp,
}

public class Note : IEquatable<Note>
{
    public Pitch Pitch { get; }
    public Accidental Accidental { get; }
    public int Octave { get; }
    public bool Active { get; set; }

    [JsonConstructor]
    public Note(Pitch pitch, Accidental accidental, int octave)
    {
        Pitch = pitch;
        Accidental = accidental;
        Octave = octave;
        Active = false;
    }

    public bool Equals(Note? other)
    {
        return Pitch.Equals(other?.Pitch) && Accidental.Equals(other?.Accidental) && Octave == other?.Octave;
    }

    public override bool Equals(object? obj)
    {
        return obj is Note other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Pitch, Accidental, Octave);
    }

    public override string ToString()
    {
        return $"{Pitch}{AccidentalToString()}{Octave}";
    }

    private string AccidentalToString()
    {
        return Accidental switch
        {
            Accidental.Flat => "b",
            Accidental.Natural => "",
            Accidental.Sharp => "#",
            _ => throw new UnreachableException("Bad Accidental value"),
        };
    }
}

public class SteelPan
{
    public PanType PanType { get; set; }

    public List<Note> Notes { get; set; } = new();
}
