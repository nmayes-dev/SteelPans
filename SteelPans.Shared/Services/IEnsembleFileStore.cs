namespace SteelPans.Shared.Services;

public interface IEnsembleFileStore
{
    Task<string> SaveAsync(
        Guid ownerId,
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
