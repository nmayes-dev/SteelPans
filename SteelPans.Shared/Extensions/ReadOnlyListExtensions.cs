using SteelPans.Shared.Music;

namespace SteelPans.Shared.Extensions;

public static class ReadOnlyListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> self, T test)
    {
        return self
            .Select((value, i) => new { value, i })
            .FirstOrDefault(x => EqualityComparer<T>.Default.Equals(x.value, test))
            ?.i ?? -1;
    }
}