using SteelPans.Shared.Extensions;
using SteelPans.Shared.Music;
using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;

namespace SteelPans.WebApp.Services;

public sealed class SteelPanSvgService
{
    private static readonly HashSet<string> ShapeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "path",
        "rect",
        "ellipse",
        "circle",
        "polygon",
        "polyline"
    };

    private static readonly HashSet<string> AllowedSvgClasses = new(StringComparer.Ordinal)
    {
        "sp-app-svg",
        "sp-app-svg-circle",
        "sp-app-svg-label",
        "sp-app-svg-label--on",
        "sp-app-svg-note",
        "sp-app-svg-note--on"
    };

    private static readonly Dictionary<string, string[]> EnharmonicSpellings = new(StringComparer.Ordinal)
    {
        ["A#"] = ["A#", "Bb"],
        ["Bb"] = ["Bb", "A#"],

        ["C#"] = ["C#", "Db"],
        ["Db"] = ["Db", "C#"],

        ["D#"] = ["D#", "Eb"],
        ["Eb"] = ["Eb", "D#"],

        ["F#"] = ["F#", "Gb"],
        ["Gb"] = ["Gb", "F#"],

        ["G#"] = ["G#", "Ab"],
        ["Ab"] = ["Ab", "G#"]
    };

    private readonly IWebHostEnvironment env_;
    private readonly SteelPanLoaderService panLoader_;

    private IReadOnlyDictionary<string, string> fileCache_ =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, XDocument> masterDocCache_ =
        new Dictionary<string, XDocument>(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, string> skeletonCache_ =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // key = "{relativePath}|{noteKey}"
    // value = (noteFragmentTemplate, labelFragmentTemplate)
    private IReadOnlyDictionary<string, (string NoteFragment, string LabelFragment)> noteFragmentCache_ =
        new Dictionary<string, (string NoteFragment, string LabelFragment)>(StringComparer.Ordinal);

    public SteelPanSvgService(IWebHostEnvironment env, SteelPanLoaderService panLoader)
    {
        env_ = env;
        panLoader_ = panLoader;
    }

    public async Task InitializeAsync()
    {
        var panBuildInfos = BuildPanBuildInfos();

        fileCache_ = await BuildFileCacheAsync(panBuildInfos);
        masterDocCache_ = BuildMasterDocumentCache(fileCache_);
        skeletonCache_ = BuildSkeletonCache(masterDocCache_);
        noteFragmentCache_ = BuildNoteFragmentCache(panBuildInfos, masterDocCache_);
    }

    public Task<string> LoadPanAsync(SteelPan pan, string componentId)
    {
        var relativePath = pan.Type.ToPath();

        var skeleton = GetSkeleton(relativePath);
        if (string.IsNullOrWhiteSpace(skeleton))
            return Task.FromResult(string.Empty);

        var noteMarkup = new StringBuilder();
        var labelMarkup = new StringBuilder();

        foreach (var note in pan.Notes)
        {
            var noteKey = note.ToString();

            var (noteTemplate, labelTemplate) = GetNoteFragmentTemplate(relativePath, noteKey);

            if (string.IsNullOrWhiteSpace(noteTemplate))
                continue;

            var labelElementId = BuildLabelElementId(componentId, noteKey);

            noteMarkup.Append(
                BindNoteFragmentToComponent(
                    noteTemplate,
                    componentId,
                    noteKey,
                    labelElementId));

            if (!string.IsNullOrWhiteSpace(labelTemplate))
            {
                labelMarkup.Append(
                    BindLabelFragmentToComponent(
                        labelTemplate,
                        componentId,
                        noteKey,
                        labelElementId));
            }
        }

        var rebuilt = skeleton
            .Replace("__COMPONENT_ID__", EscapeAttribute(componentId), StringComparison.Ordinal)
            .Replace("<!-- NOTE_SHAPES -->", noteMarkup.ToString(), StringComparison.Ordinal);

        if (labelMarkup.Length > 0)
            rebuilt = rebuilt.Replace("</svg>", labelMarkup + "</svg>", StringComparison.Ordinal);

        return Task.FromResult(rebuilt);
    }

    private List<PanSvgBuildInfo> BuildPanBuildInfos()
    {
        return panLoader_.Pans
            .Select(pan => new PanSvgBuildInfo(
                pan.Type.ToPath(),
                pan.Notes
                    .Select(n => n.ToString())
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()))
            .GroupBy(x => x.RelativePath, StringComparer.Ordinal)
            .Select(g => new PanSvgBuildInfo(
                g.Key,
                g.SelectMany(x => x.NoteKeys)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()))
            .ToList();
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildFileCacheAsync(
        IReadOnlyList<PanSvgBuildInfo> panBuildInfos)
    {
        var cache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        var tasks = panBuildInfos.Select(async buildInfo =>
        {
            var svg = await LoadSvgFileUncachedAsync(buildInfo.RelativePath);

            if (!string.IsNullOrWhiteSpace(svg))
                cache[buildInfo.RelativePath] = svg;
        });

        await Task.WhenAll(tasks);

        return cache.ToDictionary(
            x => x.Key,
            x => x.Value,
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, XDocument> BuildMasterDocumentCache(
        IReadOnlyDictionary<string, string> fileCache)
    {
        var cache = new ConcurrentDictionary<string, XDocument>(StringComparer.Ordinal);

        Parallel.ForEach(fileCache, pair =>
        {
            try
            {
                var doc = XDocument.Parse(pair.Value, LoadOptions.PreserveWhitespace);
                RewriteRootSvg(doc, "sp-app-svg");

                cache[pair.Key] = doc;
            }
            catch
            {
                // Ignore invalid SVG documents.
            }
        });

        return cache.ToDictionary(
            x => x.Key,
            x => x.Value,
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> BuildSkeletonCache(
        IReadOnlyDictionary<string, XDocument> masterDocCache)
    {
        var cache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        Parallel.ForEach(masterDocCache, pair =>
        {
            var skeleton = BuildSkeleton(pair.Value);

            if (!string.IsNullOrWhiteSpace(skeleton))
                cache[pair.Key] = skeleton;
        });

        return cache.ToDictionary(
            x => x.Key,
            x => x.Value,
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, (string NoteFragment, string LabelFragment)> BuildNoteFragmentCache(
        IReadOnlyList<PanSvgBuildInfo> panBuildInfos,
        IReadOnlyDictionary<string, XDocument> masterDocCache)
    {
        var cache = new ConcurrentDictionary<string, (string NoteFragment, string LabelFragment)>(
            StringComparer.Ordinal);

        Parallel.ForEach(panBuildInfos, buildInfo =>
        {
            if (!masterDocCache.TryGetValue(buildInfo.RelativePath, out var masterDoc) ||
                masterDoc.Root is null)
            {
                return;
            }

            foreach (var noteKey in buildInfo.NoteKeys)
            {
                var noteFragment = ExtractAndRewriteNoteElementTemplate(masterDoc.Root, noteKey);
                var labelFragment = ExtractAndRewriteLabelElementTemplate(masterDoc.Root, noteKey);

                cache[BuildNoteFragmentCacheKey(buildInfo.RelativePath, noteKey)] =
                    (noteFragment, labelFragment);
            }
        });

        return cache.ToDictionary(
            x => x.Key,
            x => x.Value,
            StringComparer.Ordinal);
    }

    private string GetSkeleton(string relativePath)
    {
        return skeletonCache_.TryGetValue(relativePath, out var skeleton)
            ? skeleton
            : string.Empty;
    }

    private (string NoteFragment, string LabelFragment) GetNoteFragmentTemplate(
        string relativePath,
        string noteKey)
    {
        return noteFragmentCache_.TryGetValue(BuildNoteFragmentCacheKey(relativePath, noteKey), out var fragments)
            ? fragments
            : (string.Empty, string.Empty);
    }

    private static string BuildNoteFragmentCacheKey(string relativePath, string noteKey)
    {
        return $"{relativePath}|{noteKey}";
    }

    private async Task<string> LoadSvgFileUncachedAsync(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        var fullPath = Path.Combine(
            env_.WebRootPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(fullPath))
            return string.Empty;

        var svg = await File.ReadAllTextAsync(fullPath);
        return DecodeIllustratorIds(svg);
    }

    private static string DecodeIllustratorIds(string svg)
    {
        return svg.Replace("_x23_", "#", StringComparison.Ordinal);
    }

    private static string BuildSkeleton(XDocument masterDoc)
    {
        var skeletonDoc = new XDocument(masterDoc);
        var root = skeletonDoc.Root;
        if (root is null)
            return string.Empty;

        root.SetAttributeValue("data-steelpan-id", "__COMPONENT_ID__");

        var noteElements = FindNoteElements(root).ToList();
        foreach (var noteElement in noteElements)
        {
            noteElement.Remove();
        }

        var labelElements = FindLabelElements(root).ToList();
        foreach (var labelElement in labelElements)
        {
            labelElement.Remove();
        }

        root.Add(new XComment(" NOTE_SHAPES "));

        return SerializeDocument(skeletonDoc);
    }

    private static void RewriteRootSvg(XDocument doc, string cssClass)
    {
        var root = doc.Root;
        if (root is null)
            return;

        SetOnlyClasses(root, cssClass);
        root.SetAttributeValue("preserveAspectRatio", "xMidYMid meet");

        foreach (var circle in root.Descendants()
                     .Where(e => string.Equals(e.Name.LocalName, "circle", StringComparison.OrdinalIgnoreCase))
                     .Where(e => !IsInsideNoteOrLabelGroup(e)))
        {
            SetOnlyClasses(circle, "sp-app-svg-circle");
        }
    }

    private static bool IsInsideNoteOrLabelGroup(XElement element)
    {
        return element.Ancestors()
            .Any(a =>
            {
                if (!string.Equals(a.Name.LocalName, "g", StringComparison.OrdinalIgnoreCase))
                    return false;

                var id = (string?)a.Attribute("id");
                return id is not null &&
                       (id.StartsWith("note-", StringComparison.Ordinal) ||
                        id.StartsWith("label-", StringComparison.Ordinal));
            });
    }

    private static IEnumerable<XElement> FindNoteElements(XElement root)
    {
        return root
            .Descendants()
            .Where(e =>
            {
                var id = (string?)e.Attribute("id");
                return !string.IsNullOrWhiteSpace(id) &&
                       id.StartsWith("note-", StringComparison.Ordinal);
            })
            .Where(e =>
            {
                if (IsShapeElement(e))
                    return true;

                return string.Equals(e.Name.LocalName, "g", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static IEnumerable<XElement> FindLabelElements(XElement root)
    {
        return root
            .Descendants()
            .Where(e =>
            {
                var id = (string?)e.Attribute("id");
                return !string.IsNullOrWhiteSpace(id) &&
                       id.StartsWith("label-", StringComparison.Ordinal);
            })
            .Where(e =>
                IsShapeElement(e) ||
                IsTextElement(e) ||
                string.Equals(e.Name.LocalName, "g", StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractAndRewriteNoteElementTemplate(
        XElement root,
        string noteKey)
    {
        var source = FindNoteSource(root, noteKey);
        if (source is null)
            return string.Empty;

        var clone = new XElement(source);
        RewriteNoteElement(clone);

        return SerializeElement(clone);
    }

    private static string ExtractAndRewriteLabelElementTemplate(
        XElement root,
        string noteKey)
    {
        var source = FindLabelSource(root, noteKey);
        if (source is null)
            return string.Empty;

        var clone = new XElement(source);
        RewriteLabelMarkup(clone);

        clone.SetAttributeValue("id", "__LABEL_ELEMENT_ID__");
        clone.SetAttributeValue("data-pan-component", "__COMPONENT_ID__");
        clone.SetAttributeValue("data-pan-label", "__NOTE_KEY__");

        return SerializeElement(clone);
    }

    private static XElement? FindNoteSource(XElement root, string noteKey)
    {
        foreach (var candidate in GetEquivalentNoteKeys(noteKey))
        {
            var noteId = $"note-{candidate}";

            var source = root
                .Descendants()
                .FirstOrDefault(e =>
                    string.Equals((string?)e.Attribute("id"), noteId, StringComparison.Ordinal) &&
                    (IsShapeElement(e) ||
                     string.Equals(e.Name.LocalName, "g", StringComparison.OrdinalIgnoreCase)));

            if (source is not null)
                return source;
        }

        return null;
    }

    private static XElement? FindLabelSource(XElement root, string noteKey)
    {
        foreach (var candidate in GetEquivalentNoteKeys(noteKey))
        {
            var labelId = $"label-{candidate}";

            var source = root
                .Descendants()
                .FirstOrDefault(e =>
                    string.Equals((string?)e.Attribute("id"), labelId, StringComparison.Ordinal) &&
                    (IsShapeElement(e) ||
                     IsTextElement(e) ||
                     string.Equals(e.Name.LocalName, "g", StringComparison.OrdinalIgnoreCase)));

            if (source is not null)
                return source;
        }

        return null;
    }

    private static IEnumerable<string> GetEquivalentNoteKeys(string noteKey)
    {
        if (string.IsNullOrWhiteSpace(noteKey))
            yield break;

        yield return noteKey;

        if (!TrySplitNoteKey(noteKey, out var pitch, out var octave))
            yield break;

        if (!EnharmonicSpellings.TryGetValue(pitch, out var spellings))
            yield break;

        foreach (var spelling in spellings)
        {
            var candidate = spelling + octave;

            if (!string.Equals(candidate, noteKey, StringComparison.Ordinal))
                yield return candidate;
        }
    }

    private static bool TrySplitNoteKey(
        string noteKey,
        out string pitch,
        out string octave)
    {
        pitch = string.Empty;
        octave = string.Empty;

        if (string.IsNullOrWhiteSpace(noteKey))
            return false;

        var splitIndex = -1;
        for (var i = 0; i < noteKey.Length; i++)
        {
            if (char.IsDigit(noteKey[i]) || noteKey[i] == '-')
            {
                splitIndex = i;
                break;
            }
        }

        if (splitIndex <= 0 || splitIndex >= noteKey.Length)
            return false;

        pitch = noteKey[..splitIndex];
        octave = noteKey[splitIndex..];
        return true;
    }

    private static void RewriteNoteElement(XElement element)
    {
        SetOnlyClasses(element, "sp-app-svg-note");

        element.SetAttributeValue("data-pan-component", "__COMPONENT_ID__");
        element.SetAttributeValue("data-pan-note", "__NOTE_KEY__");
        element.SetAttributeValue("onpointerdown", "__NOTE_CLICK__");

        if (IsShapeElement(element))
        {
            RewriteClickableShape(element);
            return;
        }

        foreach (var shape in element.Descendants().Where(IsShapeElement))
        {
            RewriteShapeForGroup(shape);
        }
    }

    private static void RewriteClickableShape(XElement shape)
    {
        RemovePresentationAttributes(shape);
        RemoveLegacyClasses(shape);

        shape.SetAttributeValue("stroke", "#000");
        shape.SetAttributeValue("stroke-width", "6");
        shape.SetAttributeValue("stroke-linecap", "round");
        shape.SetAttributeValue("stroke-linejoin", "round");
    }

    private static void RewriteShapeForGroup(XElement shape)
    {
        RemovePresentationAttributes(shape);
        RemoveLegacyClasses(shape);

        shape.SetAttributeValue("stroke", "#000");
        shape.SetAttributeValue("stroke-width", "6");
        shape.SetAttributeValue("stroke-linecap", "round");
        shape.SetAttributeValue("stroke-linejoin", "round");
    }

    private static void RemovePresentationAttributes(XElement element)
    {
        element.Attribute("fill")?.Remove();
        element.Attribute("stroke")?.Remove();
        element.Attribute("stroke-width")?.Remove();
        element.Attribute("stroke-linecap")?.Remove();
        element.Attribute("stroke-linejoin")?.Remove();
        element.Attribute("stroke-miterlimit")?.Remove();
        element.Attribute("style")?.Remove();
    }

    private static void RewriteLabelMarkup(XElement element)
    {
        SetOnlyClasses(element, "sp-app-svg-label");

        foreach (var node in element.DescendantsAndSelf()
                     .Where(e => IsShapeElement(e) || IsTextElement(e)))
        {
            node.Attribute("fill")?.Remove();
            node.Attribute("style")?.Remove();

            if (!ReferenceEquals(node, element))
                SetOnlyClasses(node);
        }
    }

    private static bool IsShapeElement(XElement element)
    {
        return ShapeTags.Contains(element.Name.LocalName);
    }

    private static bool IsTextElement(XElement element)
    {
        return string.Equals(element.Name.LocalName, "text", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(element.Name.LocalName, "tspan", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetOnlyClasses(XElement element, params string[] classNames)
    {
        var classes = classNames
            .Where(c => AllowedSvgClasses.Contains(c))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (classes.Count == 0)
        {
            element.Attribute("class")?.Remove();
            return;
        }

        element.SetAttributeValue("class", string.Join(" ", classes));
    }

    private static void RemoveLegacyClasses(XElement element)
    {
        var existingAllowed = ((string?)element.Attribute("class") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(AllowedSvgClasses.Contains)
            .ToArray();

        SetOnlyClasses(element, existingAllowed);
    }

    private static string BindNoteFragmentToComponent(
        string template,
        string componentId,
        string noteKey,
        string labelElementId)
    {
        var noteClick =
            $"steelPan.notePointerDown(this,document.getElementById('{EscapeJs(labelElementId)}'),'{EscapeJs(componentId)}','{EscapeJs(noteKey)}',event)";

        return template
            .Replace("__COMPONENT_ID__", EscapeAttribute(componentId), StringComparison.Ordinal)
            .Replace("__NOTE_KEY__", EscapeAttribute(noteKey), StringComparison.Ordinal)
            .Replace("__NOTE_CLICK__", EscapeAttribute(noteClick), StringComparison.Ordinal);
    }

    private static string BindLabelFragmentToComponent(
        string template,
        string componentId,
        string noteKey,
        string labelElementId)
    {
        return template
            .Replace("__COMPONENT_ID__", EscapeAttribute(componentId), StringComparison.Ordinal)
            .Replace("__NOTE_KEY__", EscapeAttribute(noteKey), StringComparison.Ordinal)
            .Replace("__LABEL_ELEMENT_ID__", EscapeAttribute(labelElementId), StringComparison.Ordinal);
    }

    private static string BuildLabelElementId(string componentId, string noteKey)
    {
        return $"sp-app-svg-label-{SanitizeIdPart(componentId)}-{SanitizeIdPart(noteKey)}";
    }

    private static string SanitizeIdPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "x";

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }

        return sb.ToString();
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

    private static string SerializeDocument(XDocument doc)
    {
        return doc.Declaration is null
            ? doc.ToString(SaveOptions.DisableFormatting)
            : doc.Declaration + doc.ToString(SaveOptions.DisableFormatting);
    }

    private static string SerializeElement(XElement element)
    {
        return element.ToString(SaveOptions.DisableFormatting);
    }

    private sealed record PanSvgBuildInfo(
        string RelativePath,
        IReadOnlyList<string> NoteKeys);
}