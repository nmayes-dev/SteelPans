using System.Text;

namespace SteelPans.WebApp.Endpoints;

public static class FileEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/download", DownloadAsync);
    }

    private static async Task<IResult> DownloadAsync(HttpRequest request)
    {
        var form = await request.ReadFormAsync();

        var fileName =
            Path.GetFileName(form["fileName"].ToString());

        var content =
            form["content"].ToString();

        var contentType =
            form["contentType"].ToString();

        if (string.IsNullOrWhiteSpace(contentType))
        {
            contentType = "application/octet-stream";
        }

        var bytes = Encoding.UTF8.GetBytes(content);

        return Results.File(
            bytes,
            contentType,
            fileName,
            enableRangeProcessing: false);
    }
}
