using System;
using System.Collections.Generic;
using System.Text;

namespace SteelPans.Shared.Extensions;

public static class EnumerableExtensions
{
    public static IEnumerable<(int, T)> Enumerate<T>(this IEnumerable<T> self)
    {
        return self.Select((x, index) => (index, x));
    }
}
