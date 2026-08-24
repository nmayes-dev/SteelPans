namespace SteelPans.WebApp.Services;

using SteelPans.Shared.Music;
using SteelPans.WebApp.Components.Pages;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class SteelPanLoaderService
{
    private readonly IWebHostEnvironment env_;
    private readonly JsonSerializerOptions options_;
    private readonly AudioPackService audioPacks_;

    public IReadOnlyList<SteelPan> Pans { get; private set; } = [];

    public SteelPanLoaderService(IWebHostEnvironment env, AudioPackService audioPacks)
    {
        env_ = env;
        audioPacks_ = audioPacks;

        options_ = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        options_.Converters.Add(new JsonStringEnumConverter());
    }
    public class SteelPanDto
    {
        public PanType PanType { get; set; }
        public string SamplePack { get; set; } = "default";
        public List<Note> Notes { get; set; } = new();
    }

    public async Task InitializeAsync(string path = "data/pans.json")
    {
        Console.WriteLine($"ContentRootPath: {env_.ContentRootPath}");
        if (string.IsNullOrWhiteSpace(env_.WebRootPath))
            throw new InvalidOperationException("WebRootPath is null. Ensure wwwroot exists in the published app.");

        var fullPath = Path.Combine(env_.WebRootPath!, path);
        Console.WriteLine($"Full path: {fullPath}");

        if (!File.Exists(fullPath))
            throw new FileNotFoundException(fullPath);

        var json = await File.ReadAllTextAsync(fullPath);

        var pansDto = JsonSerializer.Deserialize<List<SteelPanDto>>(json, options_)
               ?? throw new InvalidOperationException("Invalid JSON");

        Pans = pansDto
            .OrderBy(p => p.PanType)
            .Select(p => new SteelPan
            {
                Type = p.PanType,
                SamplePack = audioPacks_.GetOpaqueId(p.SamplePack),
                Notes = p.Notes.Select(n => new PanNote { Note = n }).ToList()
            }).ToList();

        if (Pans?.Any() == false)
            throw new ApplicationException("Failed to find any pans on startup.");
    }
}