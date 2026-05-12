namespace SteelPans.WebApp.Services;

using SteelPans.WebApp.Model;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class SteelPanLoader
{
    private readonly IWebHostEnvironment _env;
    private readonly JsonSerializerOptions _options;

    public SteelPanLoader(IWebHostEnvironment env)
    {
        _env = env;

        _options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        _options.Converters.Add(new JsonStringEnumConverter());
    }
    public class SteelPanDto
    {
        public PanType PanType { get; set; }
        public List<Note> Notes { get; set; } = new();
    }

    public async Task<List<SteelPan>> LoadAsync(string path = "data/pans.json")
    {
        Console.WriteLine($"ContentRootPath: {_env.ContentRootPath}");
        Console.WriteLine($"WebRootPath: {_env.WebRootPath ?? "<null>"}");

        if (string.IsNullOrWhiteSpace(_env.WebRootPath))
            throw new InvalidOperationException("WebRootPath is null. Ensure wwwroot exists in the published app.");

        var fullPath = Path.Combine(_env.WebRootPath!, path);
        Console.WriteLine($"Full path: {fullPath}");

        if (!File.Exists(fullPath))
            throw new FileNotFoundException(fullPath);

        var json = await File.ReadAllTextAsync(fullPath);

        var pansDto = JsonSerializer.Deserialize<List<SteelPanDto>>(json, _options)
               ?? throw new InvalidOperationException("Invalid JSON");

        return pansDto
            .Select(p => new SteelPan
            {
                Type = p.PanType,
                Notes = p.Notes.Select(n => new PanNote { Note = n }).ToList()
            })
            .ToList();
    }
}