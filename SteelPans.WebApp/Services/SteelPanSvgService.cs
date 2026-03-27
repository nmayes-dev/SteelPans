using System.Text;
using System.Text.RegularExpressions;
using SteelPans.WebApp.Model;

namespace SteelPans.WebApp.Services;

public sealed class SteelPanSvgService
{
    private readonly IWebHostEnvironment env_;

    private readonly Dictionary<string, string> fileCache_ = new();
    private readonly Dictionary<string, string> masterSvgCache_ = new();
    private readonly Dictionary<string, string> masterSkeletonCache_ = new();

    // Prebuilt note fragments without component-specific onclick.
    // key = "{relativePath}|{noteKey}|on/off"
    private readonly Dictionary<string, string> noteFragmentCache_ = new();

    public SteelPanSvgService(IWebHostEnvironment env)
    {
        env_ = env;
    }

    public async Task PrebuildNoteFragmentsAsync(
        string relativePath,
        IEnumerable<Note> notes)
    {
        var masterSvg = await GetMasterSvgAsync(relativePath);
        if (string.IsNullOrWhiteSpace(masterSvg))
            return;

        _ = GetOrBuildMasterSkeleton(relativePath, masterSvg);

        var noteKeys = notes
            .Select(n => n.ToString())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var noteKey in noteKeys)
        {
            await GetNoteFragmentTemplateAsync(relativePath, noteKey, isActive: false);
            await GetNoteFragmentTemplateAsync(relativePath, noteKey, isActive: true);
        }
    }

    public async Task<string> BuildPanSvgAsync(
        string relativePath,
        string componentId,
        IEnumerable<Note> notes)
    {
        var masterSvg = await GetMasterSvgAsync(relativePath);
        if (string.IsNullOrWhiteSpace(masterSvg))
            return string.Empty;

        var skeleton = GetOrBuildMasterSkeleton(relativePath, masterSvg);

        var noteMarkup = new StringBuilder();
        var activeNotes = new HashSet<string>(
            notes.Where(n => n.Active).Select(n => n.ToString()),
            StringComparer.Ordinal);

        foreach (var note in notes)
        {
            var template = await GetNoteFragmentTemplateAsync(
                relativePath,
                note.ToString(),
                note.Active);

            if (string.IsNullOrWhiteSpace(template))
                continue;

            var fragment = BindNoteFragmentToComponent(template, componentId, note.ToString(), note.Active);
            noteMarkup.Append(fragment);
        }

        var rebuilt = skeleton.Replace("<!-- NOTE_SHAPES -->", noteMarkup.ToString());
        rebuilt = MoveLabelsToEnd(rebuilt, activeNotes);

        return rebuilt;
    }

    private async Task<string> GetMasterSvgAsync(string relativePath)
    {
        if (masterSvgCache_.TryGetValue(relativePath, out var cached))
            return cached;

        var svg = await LoadSvgFileAsync(relativePath);
        if (string.IsNullOrWhiteSpace(svg))
            return string.Empty;

        svg = RewriteRootSvg(svg, "steel-pan__svg");

        masterSvgCache_[relativePath] = svg;
        return svg;
    }

    private string GetOrBuildMasterSkeleton(string relativePath, string masterSvg)
    {
        if (masterSkeletonCache_.TryGetValue(relativePath, out var cached))
            return cached;

        var skeleton = StripNoteShapes(masterSvg);
        masterSkeletonCache_[relativePath] = skeleton;
        return skeleton;
    }

    private async Task<string> GetNoteFragmentTemplateAsync(
        string relativePath,
        string noteKey,
        bool isActive)
    {
        var cacheKey = $"{relativePath}|{noteKey}|{(isActive ? "on" : "off")}";

        if (noteFragmentCache_.TryGetValue(cacheKey, out var cached))
            return cached;

        var masterSvg = await GetMasterSvgAsync(relativePath);
        if (string.IsNullOrWhiteSpace(masterSvg))
            return string.Empty;

        var fragment = ExtractAndRewriteNoteElementTemplate(masterSvg, noteKey, isActive);
        noteFragmentCache_[cacheKey] = fragment;
        return fragment;
    }

    private async Task<string> LoadSvgFileAsync(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        if (fileCache_.TryGetValue(relativePath, out var cached))
            return cached;

        var fullPath = Path.Combine(
            env_.WebRootPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(fullPath))
            return string.Empty;

        var svg = await File.ReadAllTextAsync(fullPath);
        fileCache_[relativePath] = svg;
        return svg;
    }

    private static string RewriteRootSvg(string svg, string cssClass)
    {
        return Regex.Replace(
            svg,
            @"<svg\b([^>]*)>",
            match =>
            {
                var attrs = match.Groups[1].Value;

                attrs = Regex.Replace(
                    attrs,
                    @"\sclass\s*=\s*[""'][^""']*[""']",
                    string.Empty,
                    RegexOptions.IgnoreCase);

                attrs = Regex.Replace(
                    attrs,
                    @"\spreserveAspectRatio\s*=\s*[""'][^""']*[""']",
                    string.Empty,
                    RegexOptions.IgnoreCase);

                return $"""<svg{attrs} class="{cssClass}" preserveAspectRatio="xMidYMid meet">""";
            },
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
    }

    private static string StripNoteShapes(string svg)
    {
        var bodyMatch = Regex.Match(
            svg,
            @"^(.*?<svg\b[^>]*>)(.*?)(</svg>\s*)$",
            RegexOptions.Singleline | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        if (!bodyMatch.Success)
            return svg;

        var open = bodyMatch.Groups[1].Value;
        var body = bodyMatch.Groups[2].Value;
        var close = bodyMatch.Groups[3].Value;

        body = Regex.Replace(
            body,
            @"<(path|rect|ellipse|circle|polygon|polyline)\b[^>]*\bid\s*=\s*[""']note-[^""']+[""'][^>]*/?>",
            "",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        return open + body + "<!-- NOTE_SHAPES -->" + close;
    }

    private static string ExtractAndRewriteNoteElementTemplate(
        string svg,
        string noteKey,
        bool isActive)
    {
        var noteId = $"note-{noteKey}";
        var stateClass = isActive
            ? "steel-pan__note-shape--on"
            : "steel-pan__note-shape--off";

        var pattern =
            $@"<(path|rect|ellipse|circle|polygon|polyline)\b([^>]*\bid\s*=\s*[""']{Regex.Escape(noteId)}[""'][^>]*?)(\s*/?)>";

        var match = Regex.Match(
            svg,
            pattern,
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        if (!match.Success)
            return string.Empty;

        var tagName = match.Groups[1].Value;
        var attrs = match.Groups[2].Value;
        var closing = match.Groups[3].Value;

        attrs = Regex.Replace(
            attrs,
            @"\sclass\s*=\s*[""']([^""']*)[""']",
            m =>
            {
                var classes = m.Groups[1].Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(c =>
                        !string.Equals(c, "st0", StringComparison.Ordinal) &&
                        !c.StartsWith("steel-pan__note-shape--", StringComparison.Ordinal))
                    .ToList();

                if (!classes.Contains("steel-pan__note-shape"))
                    classes.Add("steel-pan__note-shape");

                classes.Add(stateClass);

                return $""" class="{string.Join(" ", classes.Distinct())}" """;
            },
            RegexOptions.IgnoreCase);

        if (!Regex.IsMatch(attrs, @"\sclass\s*=", RegexOptions.IgnoreCase))
        {
            attrs += $""" class="steel-pan__note-shape {stateClass}" """;
        }

        attrs = Regex.Replace(attrs, @"\sdata-note\s*=\s*[""'][^""']*[""']", "", RegexOptions.IgnoreCase);
        attrs = Regex.Replace(attrs, @"\sonclick\s*=\s*[""'][^""']*[""']", "", RegexOptions.IgnoreCase);
        attrs = Regex.Replace(attrs, @"\sfill\s*=\s*[""'][^""']*[""']", "", RegexOptions.IgnoreCase);
        attrs = Regex.Replace(attrs, @"\sstroke\s*=\s*[""'][^""']*[""']", "", RegexOptions.IgnoreCase);
        attrs = Regex.Replace(attrs, @"\sstroke-width\s*=\s*[""'][^""']*[""']", "", RegexOptions.IgnoreCase);
        attrs = Regex.Replace(attrs, @"\sstroke-linecap\s*=\s*[""'][^""']*[""']", "", RegexOptions.IgnoreCase);
        attrs = Regex.Replace(attrs, @"\sstroke-linejoin\s*=\s*[""'][^""']*[""']", "", RegexOptions.IgnoreCase);

        attrs = Regex.Replace(
            attrs,
            @"\sstyle\s*=\s*[""']([^""']*)[""']",
            m =>
            {
                var style = m.Groups[1].Value.Trim();

                if (style.Length > 0 && !style.EndsWith(";"))
                    style += ";";

                if (!style.Contains("cursor:", StringComparison.OrdinalIgnoreCase))
                    style += "cursor:pointer;";

                return $""" style="{style}" """;
            },
            RegexOptions.IgnoreCase);

        if (!Regex.IsMatch(attrs, @"\sstyle\s*=", RegexOptions.IgnoreCase))
        {
            attrs += """ style="cursor:pointer;" """;
        }

        var fill = isActive ? "#e2201c" : "#ffffff";
        attrs += $""" fill="{fill}" stroke="#000" stroke-width="6" stroke-linecap="round" stroke-linejoin="round" """;
        attrs += """ data-note="__NOTE_KEY__" onclick="__TOGGLE_CLICK__" onpointerdown="__HOLD_START__" """;

        return $"""<{tagName}{attrs}{closing}>""";
    }

    private static string BindNoteFragmentToComponent(
        string template,
        string componentId,
        string noteKey,
        bool isActive)
    {
        var toggleAction = isActive ? "deactivate" : "activate";

        var toggleClick =
            $"window.steelPanToggleClick('{EscapeJs(componentId)}','{EscapeJs(toggleAction)}','{EscapeJs(noteKey)}',event)";

        var holdStart =
            $"window.steelPanHoldStart('{EscapeJs(componentId)}','{EscapeJs(noteKey)}',event)";

        return template
            .Replace("__NOTE_KEY__", EscapeAttribute(noteKey), StringComparison.Ordinal)
            .Replace("__TOGGLE_CLICK__", EscapeAttribute(toggleClick), StringComparison.Ordinal)
            .Replace("__HOLD_START__", EscapeAttribute(holdStart), StringComparison.Ordinal);
    }

    private static string MoveLabelsToEnd(string svg, HashSet<string> activeNotes)
    {
        var bodyMatch = Regex.Match(
            svg,
            @"^(.*?<svg\b[^>]*>)(.*?)(</svg>\s*)$",
            RegexOptions.Singleline | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        if (!bodyMatch.Success)
            return svg;

        var open = bodyMatch.Groups[1].Value;
        var body = bodyMatch.Groups[2].Value;
        var close = bodyMatch.Groups[3].Value;

        var extracted = new List<string>();

        body = Regex.Replace(
            body,
            @"<g\b[^>]*\bid\s*=\s*[""']label-([^""']+)[""'][^>]*>.*?</g>",
            m =>
            {
                var noteKey = m.Groups[1].Value;
                var content = m.Value;

                if (activeNotes.Contains(noteKey))
                    content = ForceLabelWhite(content);

                extracted.Add(content);
                return string.Empty;
            },
            RegexOptions.Singleline | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        body = Regex.Replace(
            body,
            @"<(path|rect|ellipse|circle|polygon|polyline)\b[^>]*\bid\s*=\s*[""']label-([^""']+)[""'][^>]*/?>",
            m =>
            {
                var noteKey = m.Groups[2].Value;
                var content = m.Value;

                if (activeNotes.Contains(noteKey))
                    content = ForceLabelWhite(content);

                extracted.Add(content);
                return string.Empty;
            },
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        if (extracted.Count == 0)
            return svg;

        return open + body + string.Join("", extracted) + close;
    }

    private static string EscapeJs(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("'", "\\'");
    }

    private static string EscapeAttribute(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;");
    }

    private static string ForceLabelWhite(string svg)
    {
        // Remove existing fill
        svg = Regex.Replace(
            svg,
            @"\sfill\s*=\s*[""'][^""']*[""']",
            "",
            RegexOptions.IgnoreCase);

        // Inject fill="white" into all drawable elements
        return Regex.Replace(
            svg,
            @"<(path|text|tspan|circle|ellipse|polygon|polyline)\b",
            m => $"{m.Value} fill=\"#ffffff\"",
            RegexOptions.IgnoreCase);
    }
}