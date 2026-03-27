namespace SteelPans.WebApp.Services;

using System.Text.Json;
using System.Text.Json.Serialization;
using SteelPans.WebApp.Model;

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

    public async Task<List<SteelPan>> LoadAsync(string path = "data/pans.json")
    {
        var fullPath = Path.Combine(_env.WebRootPath, path);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException(fullPath);

        var json = await File.ReadAllTextAsync(fullPath);
        return JsonSerializer.Deserialize<List<SteelPan>>(json, _options)
               ?? throw new InvalidOperationException("Invalid JSON");
    }
}