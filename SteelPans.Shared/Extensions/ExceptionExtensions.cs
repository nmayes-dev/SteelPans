using System;
using System.Collections.Generic;
using System.Text;

namespace SteelPans.Shared.Extensions;

public static class ExceptionExtensions
{

    public static void Log(this Exception ex, string? initialMessage = null)
    {
        if (!string.IsNullOrWhiteSpace(initialMessage))
            Console.Error.WriteLine(initialMessage);

        Console.Error.WriteLine(ex.Message);

        var inner = ex.InnerException;
        var innerCount = 1;

        while (inner is not null)
        {
            Console.Error.WriteLine($"{new string('\t', innerCount)}Inner Exception:");
            Console.Error.WriteLine(inner.Message);

            inner = inner.InnerException;
            innerCount++;
        }
    }
}
