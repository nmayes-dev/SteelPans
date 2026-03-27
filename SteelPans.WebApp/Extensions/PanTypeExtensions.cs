using SteelPans.WebApp.Model;
using System.Text.RegularExpressions;

namespace SteelPans.WebApp.Extensions;

public static class EnumExtensions
{
    private static readonly Regex splitKebab_ =
        new(@"([a-z0-9])([A-Z])", RegexOptions.Compiled);

    private static readonly Regex splitPascal_ =
    new(@"(?<!^)([A-Z])", RegexOptions.Compiled);

    public static string ToKebabCase(this PanType value)
    {
        var name = value.ToString();
        return splitKebab_.Replace(name, "$1-$2").ToLowerInvariant();
    }

    public static string ToSpacedPascal(this Enum value)
    {
        return splitPascal_.Replace(value.ToString(), " $1");
    }
}