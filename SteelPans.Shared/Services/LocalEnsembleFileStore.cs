using Microsoft.Extensions.Configuration;

namespace SteelPans.Shared.Services;

public sealed class LocalEnsembleFileStore(IConfiguration configuration) : IEnsembleFileStore
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

        await using var output = File.Create(fullPath);
        await content.CopyToAsync(output, cancellationToken);

        return relativePath.Replace('\\', '/');
    }

    public Task<Stream> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(rootPath, storageKey);
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
        }

        return Task.CompletedTask;
    }
}