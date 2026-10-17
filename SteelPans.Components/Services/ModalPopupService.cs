using Microsoft.Extensions.Logging;
using SteelPans.Components.Layout;

namespace SteelPans.Components.Services;

public sealed class ModalOptions
{
    public bool CloseOthers { get; set; } = true;
    public Func<Task>? OnComplete { get; set; }
    public Func<Task>? OnSuccess { get; set; }
    public Func<Task>? OnFailure { get; set; }
}

public interface IModalPopup
{
    Task OpenAsync(object? payload = null, ModalOptions? options = null);

    Task RequestCloseAsync();
}

public sealed class ModalPopupService
{
    private readonly ILogger<ModalPopupService> logger_;
    private readonly Dictionary<string, IModalPopup> modals_ = new(StringComparer.Ordinal);

    public ModalPopupService(ILogger<ModalPopupService> logger)
    {
        logger_ = logger;
    }

    public void Register(string id, IModalPopup modal)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        if (modals_.TryGetValue(id, out var existing) && !ReferenceEquals(existing, modal))
            throw new InvalidOperationException($"A modal popup with id '{id}' has already been registered.");

        modals_[id] = modal;
        logger_.LogDebug("Registered modal {ModalId}", id);
    }

    public void Unregister(string id, IModalPopup modal)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        if (modals_.TryGetValue(id, out var existing) && ReferenceEquals(existing, modal))
        {
            modals_.Remove(id);
            logger_.LogDebug("Unregistered modal {ModalId}", id);
        }
    }

    public Task<bool> OpenAsync(string id, ModalOptions? options = null)
    {
        return OpenAsync<object?>(id, null, options);
    }

    public async Task<bool> OpenAsync<TPayload>(string id, TPayload payload, ModalOptions? options = null)
    {
        if (!modals_.TryGetValue(id, out var modal))
        {
            logger_.LogWarning("Cannot open unregistered modal {ModalId}", id);
            return false;
        }

        logger_.LogDebug("Opening modal {ModalId}", id);
        await modal.OpenAsync(payload, options);
        return true;
    }

    public async Task<bool> CloseAsync(string id)
    {
        if (!modals_.TryGetValue(id, out var modal))
        {
            logger_.LogWarning("Cannot close unregistered modal {ModalId}", id);
            return false;
        }

        logger_.LogDebug("Closing modal {ModalId}", id);
        await modal.RequestCloseAsync();
        return true;
    }
}