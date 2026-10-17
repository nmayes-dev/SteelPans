
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using SteelPans.WebApp.Services;
using System.Security.Cryptography;

namespace SteelPans.WebApp.Endpoints;

public static class AudioEndpoints
{
    public static IEndpointRouteBuilder MapAudioApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audio-packs/{packId}", Endpoints.GetAudioPack);

        return app;
    }

    private sealed class Endpoints
    {
        public static IResult GetAudioPack(
            string packId,
            AudioPackService packs,
            HttpResponse response,
            ILogger<Endpoints> logger)
        {
            var pack = packs.GetPackByOpaqueId(packId);

            if (pack is null)
            {
                logger.LogWarning(
                    "Audio pack {OpaquePackId} was not found",
                    packId);

                return Results.NotFound();
            }

            try
            {
                var bytes = packs.Decrypt(pack);

                response.Headers.CacheControl = "no-store";
                response.Headers.Pragma = "no-cache";

                return Results.File(
                    bytes,
                    "application/octet-stream",
                    enableRangeProcessing: false);
            }
            catch (CryptographicException ex)
            {
                logger.LogError(
                    ex,
                    "Audio pack {OpaquePackId} could not be decrypted",
                    packId);

                return Results.Problem(
                    "Audio pack could not be decrypted.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
            catch (InvalidDataException ex)
            {
                logger.LogError(
                    ex,
                    "Audio pack {OpaquePackId} is invalid",
                    packId);

                return Results.Problem(
                    "Audio pack is invalid.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }
    }
}
