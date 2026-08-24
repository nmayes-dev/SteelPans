using System.Security.Cryptography;
using System.Text;

namespace SteelPans.WebApp.Services;

public sealed class AudioPackService
{
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("SteelPans.AudioPack.v1");
    private static readonly byte[] IdPrefix = Encoding.UTF8.GetBytes("SteelPans.AudioPack.Id.v1:");

    private readonly string packDirectory_;
    private readonly byte[] key_;

    public AudioPackService(IWebHostEnvironment env, IConfiguration configuration)
    {
        var packDir = configuration["AudioPacks:RootPath"];
        ArgumentException.ThrowIfNullOrEmpty(packDir);

        packDirectory_ = packDir;
        key_ = LoadKey(configuration);
    }

    public string GetOpaqueId(string packId)
    {
        var packBytes = Encoding.UTF8.GetBytes(packId);
        var input = new byte[IdPrefix.Length + packBytes.Length];

        IdPrefix.CopyTo(input, 0);
        packBytes.CopyTo(input, IdPrefix.Length);

        var hash = HMACSHA256.HashData(key_, input);

        return Convert
            .ToHexString(hash.AsSpan(0, 16))
            .ToLowerInvariant();
    }

    public AudioPackFile? GetPackByOpaqueId(string opaqueId)
    {
        if (!IsValidOpaqueId(opaqueId))
            return null;

        if (!Directory.Exists(packDirectory_))
            return null;

        foreach (var path in Directory.EnumerateFiles(
                     packDirectory_,
                     "*.spp",
                     SearchOption.TopDirectoryOnly))
        {
            var packId = Path.GetFileNameWithoutExtension(path);

            if (string.Equals(
                    GetOpaqueId(packId),
                    opaqueId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new AudioPackFile(path);
            }
        }

        return null;
    }

    public byte[] Decrypt(AudioPackFile pack)
    {
        var encrypted = File.ReadAllBytes(pack.Path);

        if (encrypted.Length < 4 + 12 + 16 ||
            !encrypted.AsSpan(0, 4).SequenceEqual("SPE1"u8))
        {
            throw new InvalidDataException(
                "Invalid encrypted audio pack header.");
        }

        var nonce = encrypted.AsSpan(4, 12);
        var ciphertextAndTag = encrypted.AsSpan(16);

        var ciphertext = ciphertextAndTag[..^16];
        var tag = ciphertextAndTag[^16..];

        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key_, 16);
        aes.Decrypt(
            nonce,
            ciphertext,
            tag,
            plaintext,
            Aad);

        return plaintext;
    }

    private static byte[] LoadKey(IConfiguration configuration)
    {
        var value = configuration["AudioPacks:Key"];

        if (string.IsNullOrWhiteSpace(value))
            value = Environment.GetEnvironmentVariable("STEELPANS_AUDIO_PACK_KEY");

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "Audio pack encryption key is not configured.");
        }

        try
        {
            var bytes = Convert.FromBase64String(value);

            if (bytes.Length != 32)
            {
                throw new InvalidOperationException(
                    "Audio pack encryption key must decode to exactly 32 bytes.");
            }

            return bytes;
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Audio pack encryption key must be valid base64.",
                ex);
        }
    }

    private static bool IsValidOpaqueId(string opaqueId)
    {
        return opaqueId.Length == 32 &&
               opaqueId.All(char.IsAsciiHexDigit);
    }
}

public sealed record AudioPackFile(string Path);