using SteelPans.WebApp.Services;
using System.Security.Cryptography;

namespace SteelPans.WebApp.Endpoints;

public static class AudioEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/audio-packs/{packId}", GetAudioPackAsync);
    }

    private static async Task<IResult> GetAudioPackAsync(string packId,
        AudioPackService packs,
        HttpResponse response)
    {
        var pack = packs.GetPackByOpaqueId(packId);

        if (pack is null)
            return Results.NotFound();

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
        catch (CryptographicException)
        {
            return Results.Problem(
                "Audio pack could not be decrypted.",
                statusCode:
                    StatusCodes.Status500InternalServerError);
        }
        catch (InvalidDataException)
        {
            return Results.Problem(
                "Audio pack is invalid.",
                statusCode:
                    StatusCodes.Status500InternalServerError);
        }
    }
}
