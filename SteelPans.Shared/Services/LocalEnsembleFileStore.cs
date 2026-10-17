using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SteelPans.Shared.Services;

public sealed class LocalEnsembleFileStore(
    IConfiguration configuration,
    ILogger<LocalEnsembleFileStore> logger) : IEnsembleFileStore
{
    private readonly string rootPath =
        configuration["FileStore:RootPath"] ?? throw new ArgumentNullException(nameof(rootPath));

    public async Task<string> SaveAsync(
        Guid groupId,
        Guid fileId,
        string originalFileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".mid";
        }

        var relativePath = Path.Combine(
            groupId.ToString("N"),
            $"{fileId:N}{extension}");

        var fullPath = Path.Combine(rootPath, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        logger.LogDebug(
            "Saving file {FileId} to {StorageKey}",
            fileId,
            relativePath);

        await using var output = File.Create(fullPath);
        await content.CopyToAsync(output, cancellationToken);

        logger.LogInformation(
            "Saved file {FileId} to local file store at {StorageKey} ({SizeBytes} bytes)",
            fileId,
            relativePath,
            output.Length);

        return relativePath.Replace('\\', '/');
    }

    public Task<Stream> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(rootPath, storageKey);
        logger.LogDebug("Opening local file store object {StorageKey}", storageKey);
        Stream stream = File.OpenRead(fullPath);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(
        string storageKey,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(rootPath, storageKey);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            logger.LogInformation("Deleted local file store object {StorageKey}", storageKey);
        }
        else
        {
            logger.LogDebug("Local file store object {StorageKey} did not exist when deletion was requested", storageKey);
        }

        return Task.CompletedTask;
    }
}
