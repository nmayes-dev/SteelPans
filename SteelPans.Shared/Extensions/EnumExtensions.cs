using SteelPans.Shared.Music;
using System.Text.RegularExpressions;

namespace SteelPans.Shared.Extensions;

public static class EnumExtensions
{
    private static readonly Regex splitKebab_ =
        new(@"([a-z0-9])([A-Z])", RegexOptions.Compiled);

    private static readonly Regex splitPascal_ =
    new(@"(?<!^)([A-Z])", RegexOptions.Compiled);

    public static string ToSpacedPascal(this Enum value) => splitPascal_.Replace(value.ToString(), " $1");

    public static string ToKebabCase(this Enum value)
    {
        var name = value.ToString();
        return splitKebab_.Replace(name, "$1-$2").ToLowerInvariant();
    }

    public static string ToPath(this PanType value) => $"images/pans/{value.ToKebabCase()}.svg";
}

public static class EnumFlagExtensions
{
    public static bool HasAll<TEnum>(this TEnum value, TEnum flags)
    where TEnum : struct, Enum
    {
        FlagEnumGuard<TEnum>.ThrowIfNotFlags();

        var valueBits = Convert.ToUInt64(value);
        var flagBits = Convert.ToUInt64(flags);

        return (valueBits & flagBits) == flagBits;
    }

    public static bool HasAny<TEnum>(this TEnum value, TEnum flags)
        where TEnum : struct, Enum
    {
        FlagEnumGuard<TEnum>.ThrowIfNotFlags();

        var valueBits = Convert.ToUInt64(value);
        var flagBits = Convert.ToUInt64(flags);

        return (valueBits & flagBits) != 0;
    }

    private static class FlagEnumGuard<TEnum>
    where TEnum : struct, Enum
    {
        private static readonly bool IsFlags =
            typeof(TEnum).IsDefined(typeof(FlagsAttribute), inherit: false);

        public static void ThrowIfNotFlags()
        {
            if (!IsFlags)
                throw new InvalidOperationException($"{typeof(TEnum).Name} must be a [Flags] enum.");
        }
    }
}