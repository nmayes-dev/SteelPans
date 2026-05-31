using SteelPans.Components.Layout;

namespace SteelPans.Components.Services;

public sealed class ModalPopupService
{
    private readonly Dictionary<string, ModalPopup> modals_ = new(StringComparer.Ordinal);

    public void Register(string id, ModalPopup modal)
    {
        if (id is null)
            return;

        if (modals_.TryGetValue(id, out var existing) && !ReferenceEquals(existing, modal))
            throw new InvalidOperationException($"A modal popup with id '{id}' has already been registered.");

        modals_[id] = modal;
    }

    public void Unregister(string id, ModalPopup modal)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        if (modals_.TryGetValue(id, out var existing) && ReferenceEquals(existing, modal))
            modals_.Remove(id);
    }

    public async Task<bool> OpenAsync(string id, bool closeOthers = true)
    {
        if (!modals_.TryGetValue(id, out var modal))
            return false;

        await modal.OpenAsync(closeOthers);
        return true;
    }

    public async Task<bool> CloseAsync(string id)
    {
        if (!modals_.TryGetValue(id, out var modal))
            return false;

        await modal.RequestCloseAsync();
        return true;
    }
}
