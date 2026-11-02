
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using System.Text;

namespace SteelPans.WebApp.Endpoints;

public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/download", Endpoints.DownloadAsync);

        return app;
    }
    private sealed class Endpoints
    {
        public static async Task<IResult> DownloadAsync(
            HttpRequest request,
            ILogger<Endpoints> logger,
            CancellationToken cancellationToken)
        {
            var form = await request.ReadFormAsync(cancellationToken);

            var fileName = Path.GetFileName(
                form["fileName"].ToString());

            var content = form["content"].ToString();

            var contentType = form["contentType"].ToString();

            if (string.IsNullOrWhiteSpace(contentType))
            {
                contentType = "application/octet-stream";
            }

            var bytes = Encoding.UTF8.GetBytes(content);

            logger.LogInformation(
                "Generated download {FileName} with content type {ContentType} and size {SizeBytes} bytes",
                fileName,
                contentType,
                bytes.Length);

            return Results.File(
                bytes,
                contentType,
                fileName,
                enableRangeProcessing: false);
        }
    }

}
