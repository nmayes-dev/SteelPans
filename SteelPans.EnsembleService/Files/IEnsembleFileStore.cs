namespace SteelPans.EnsembleService.Files;

public interface IEnsembleFileStore
{
    Task<string> SaveAsync(
        Guid groupId,
        Guid fileId,
        string originalFileName,
        Stream content,
        CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        string storageKey,
        CancellationToken cancellationToken);
}