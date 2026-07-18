namespace SteelPans.Shared.Extensions;

public static class ByteSizeExtensions
{
    public static string ToFileSize(this long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:0.#} KB";

        return $"{bytes / 1024d / 1024d:0.#} MB";
    }
}
