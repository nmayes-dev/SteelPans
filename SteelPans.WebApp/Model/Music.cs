using SteelPans.WebApp.Services;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteelPans.WebApp.Model;

public enum NoteLetter
{
    C,
    D,
    E,
    F,
    G,
    A,
    B,
}

public readonly record struct Accidental(int SemitoneOffset)
{
    public static readonly Accidental DoubleFlat = new(-2);
    public static readonly Accidental Flat = new(-1);
    public static readonly Accidental Natural = new(0);
    public static readonly Accidental Sharp = new(1);
    public static readonly Accidental DoubleSharp = new(2);

    public override string ToString()
    {
        return SemitoneOffset switch
        {
            < 0 => new string('b', -SemitoneOffset),
            > 0 => new string('#', SemitoneOffset),
            _ => "",
        };
    }
}

public readonly record struct NoteName(NoteLetter Letter, Accidental Accidental)
{
    public int PitchClass => Mod(GetNaturalPitchClass(Letter) + Accidental.SemitoneOffset, 12);

    public bool IsEnharmonicEquivalentTo(NoteName other)
    {
        return PitchClass == other.PitchClass;
    }

    public override string ToString()
    {
        return $"{Letter}{Accidental}";
    }

    public static int GetNaturalPitchClass(NoteLetter letter)
    {
        return letter switch
        {
            NoteLetter.C => 0,
            NoteLetter.D => 2,
            NoteLetter.E => 4,
            NoteLetter.F => 5,
            NoteLetter.G => 7,
            NoteLetter.A => 9,
            NoteLetter.B => 11,
            _ => throw new UnreachableException("Bad NoteLetter value"),
        };
    }

    private static int Mod(int value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}

public readonly record struct Interval(int Semitones)
{
    public static readonly Interval PerfectUnison = new(0);
    public static readonly Interval MinorSecond = new(1);
    public static readonly Interval MajorSecond = new(2);
    public static readonly Interval MinorThird = new(3);
    public static readonly Interval MajorThird = new(4);
    public static readonly Interval PerfectFourth = new(5);
    public static readonly Interval Tritone = new(6);
    public static readonly Interval PerfectFifth = new(7);
    public static readonly Interval MinorSixth = new(8);
    public static readonly Interval MajorSixth = new(9);
    public static readonly Interval MinorSeventh = new(10);
    public static readonly Interval MajorSeventh = new(11);
    public static readonly Interval Octave = new(12);

    public static Interval operator +(Interval left, Interval right)
    {
        return new Interval(left.Semitones + right.Semitones);
    }

    public static Interval operator -(Interval left, Interval right)
    {
        return new Interval(left.Semitones - right.Semitones);
    }

    public override string ToString()
    {
        return $"{Semitones}";
    }
}

public static class ChordFormulas
{
    public static readonly Interval[] MajorTriad =
    [
        Interval.PerfectUnison,
        Interval.MajorThird,
        Interval.PerfectFifth,
    ];

    public static readonly Interval[] MinorTriad =
    [
        Interval.PerfectUnison,
        Interval.MinorThird,
        Interval.PerfectFifth,
    ];

    public static readonly Interval[] DiminishedTriad =
    [
        Interval.PerfectUnison,
        Interval.MinorThird,
        Interval.Tritone,
    ];

    public static readonly Interval[] AugmentedTriad =
    [
        Interval.PerfectUnison,
        Interval.MajorThird,
        Interval.MinorSixth,
    ];

    public static readonly Interval[] DominantSeventh =
    [
        Interval.PerfectUnison,
        Interval.MajorThird,
        Interval.PerfectFifth,
        Interval.MinorSeventh,
    ];

    public static readonly Interval[] MajorSeventh =
    [
        Interval.PerfectUnison,
        Interval.MajorThird,
        Interval.PerfectFifth,
        Interval.MajorSeventh,
    ];

    public static readonly Interval[] MinorSeventh =
    [
        Interval.PerfectUnison,
        Interval.MinorThird,
        Interval.PerfectFifth,
        Interval.MinorSeventh,
    ];
}

[JsonConverter(typeof(NoteJsonConverter))]
public readonly record struct Note
{
    public NoteLetter Letter { get; }
    public Accidental Accidental { get; }
    public int Octave { get; }

    public int SemitoneNumber { get; }

    public int PitchClass => Mod(SemitoneNumber, 12);

    public NoteName Name => new(Letter, Accidental);

    public Note(NoteLetter letter, Accidental accidental, int octave)
    {
        Letter = letter;
        Accidental = accidental;
        Octave = octave;

        var name = new NoteName(letter, accidental);
        SemitoneNumber = octave * 12 + name.PitchClass;
    }

    public Note(NoteName name, int octave)
        : this(name.Letter, name.Accidental, octave)
    {
    }

    public bool IsEnharmonicEquivalentTo(Note other)
    {
        return SemitoneNumber == other.SemitoneNumber;
    }
    public bool IsSamePitchClass(Note other)
    {
        return PitchClass == other.PitchClass;
    }

    public Note Transpose(Interval interval)
    {
        return FromSemitoneNumber(SemitoneNumber + interval.Semitones);
    }

    public IReadOnlyList<Note> BuildChord(params Interval[] intervals)
    {
        var result = new Note[intervals.Length];

        for (int i = 0; i < intervals.Length; i++)
        {
            result[i] = Transpose(intervals[i]);
        }

        return result;
    }

    public int ToMidi()
    {
        return SemitoneNumber + 12;
    }

    public static Note FromMidi(int midiNumber)
    {
        return FromSemitoneNumber(midiNumber - 12);
    }

    public static Note FromSemitoneNumber(int semitoneNumber)
    {
        var octave = FloorDiv(semitoneNumber, 12);
        var pitchClass = Mod(semitoneNumber, 12);

        return pitchClass switch
        {
            0 => new Note(NoteLetter.C, Accidental.Natural, octave),
            1 => new Note(NoteLetter.C, Accidental.Sharp, octave),
            2 => new Note(NoteLetter.D, Accidental.Natural, octave),
            3 => new Note(NoteLetter.D, Accidental.Sharp, octave),
            4 => new Note(NoteLetter.E, Accidental.Natural, octave),
            5 => new Note(NoteLetter.F, Accidental.Natural, octave),
            6 => new Note(NoteLetter.F, Accidental.Sharp, octave),
            7 => new Note(NoteLetter.G, Accidental.Natural, octave),
            8 => new Note(NoteLetter.G, Accidental.Sharp, octave),
            9 => new Note(NoteLetter.A, Accidental.Natural, octave),
            10 => new Note(NoteLetter.A, Accidental.Sharp, octave),
            11 => new Note(NoteLetter.B, Accidental.Natural, octave),
            _ => throw new UnreachableException("Bad pitch class"),
        };
    }

    public static Note operator +(Note note, Interval interval)
    {
        return note.Transpose(interval);
    }

    public static Note operator -(Note note, Interval interval)
    {
        return note.Transpose(new Interval(-interval.Semitones));
    }

    public override string ToString()
    {
        return $"{Letter}{Accidental}{Octave}";
    }

    private static int Mod(int value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;

        if (remainder != 0 && ((remainder < 0) != (divisor < 0)))
            quotient--;

        return quotient;
    }
}
public sealed class NoteJsonConverter : JsonConverter<Note>
{
    private static readonly Regex s_noteRegex =
        new(@"^([A-G])([#b]*)(-?\d+)$", RegexOptions.Compiled);

    public override Note Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString();
        if (string.IsNullOrWhiteSpace(str))
            throw new JsonException("Note string is null or empty.");

        var match = s_noteRegex.Match(str);
        if (!match.Success)
            throw new JsonException($"Invalid note format '{str}'. Expected forms like C4, F#3, or Eb5.");

        var letter = ParseLetter(match.Groups[1].ValueSpan);
        var accidental = ParseAccidental(match.Groups[2].ValueSpan);

        if (!int.TryParse(match.Groups[3].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var octave))
            throw new JsonException($"Invalid octave in note '{str}'.");

        return new Note(letter, accidental, octave);
    }

    public override void Write(Utf8JsonWriter writer, Note value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }

    private static NoteLetter ParseLetter(ReadOnlySpan<char> span)
    {
        if (span.Length != 1)
            throw new JsonException("Invalid note letter.");

        return span[0] switch
        {
            'A' => NoteLetter.A,
            'B' => NoteLetter.B,
            'C' => NoteLetter.C,
            'D' => NoteLetter.D,
            'E' => NoteLetter.E,
            'F' => NoteLetter.F,
            'G' => NoteLetter.G,
            _ => throw new JsonException($"Invalid note letter '{new string(span)}'."),
        };
    }

    private static Accidental ParseAccidental(ReadOnlySpan<char> span)
    {
        var offset = 0;

        foreach (var c in span)
        {
            offset += c switch
            {
                '#' => 1,
                'b' => -1,
                _ => throw new JsonException($"Invalid accidental character '{c}'."),
            };
        }

        return new Accidental(offset);
    }
}