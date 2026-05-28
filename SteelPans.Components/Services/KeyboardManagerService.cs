using Microsoft.JSInterop;

namespace SteelPans.Components.Services;

public sealed class KeyboardManagerService : IAsyncDisposable
{
    private readonly IJSRuntime js_;
    private readonly List<KeyboardCallbackRegistration> callbacks_ = [];

    private DotNetObjectReference<KeyboardManagerService>? dotNetRef_;

    public KeyboardManagerService(IJSRuntime js)
    {
        js_ = js;
    }

    public async ValueTask InitializeAsync()
    {
        if (dotNetRef_ is not null)
            return;

        dotNetRef_ = DotNetObjectReference.Create(this);
        await js_.InvokeVoidAsync("keyboardManager.initialize", dotNetRef_);
    }

    public IDisposable Register(
        Func<KeyboardEventData, bool> predicate,
        Func<KeyboardEventData, Task> callback)
    {
        var registration = new KeyboardCallbackRegistration(
            predicate,
            callback,
            OneShot: false);

        callbacks_.Add(registration);

        return new KeyboardRegistration(this, registration);
    }

    public IDisposable RegisterOneShot(
        Func<KeyboardEventData, bool> predicate,
        Func<KeyboardEventData, Task> callback)
    {
        var registration = new KeyboardCallbackRegistration(
            predicate,
            callback,
            OneShot: true);

        callbacks_.Add(registration);

        return new KeyboardRegistration(this, registration);
    }

    private void Unregister(KeyboardCallbackRegistration registration)
    {
        callbacks_.Remove(registration);
    }

    [JSInvokable]
    public async Task OnKeyDownAsync(KeyboardEventData eventData)
    {
        foreach (var registration in callbacks_.ToArray())
        {
            if (!callbacks_.Contains(registration))
                continue;

            if (!registration.Predicate(eventData))
                continue;

            await registration.Callback(eventData);

            if (registration.OneShot)
                callbacks_.Remove(registration);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await js_.InvokeVoidAsync("keyboardManager.dispose");
        }
        catch (InvalidOperationException)
        {
        }
        catch (JSDisconnectedException)
        {
        }

        callbacks_.Clear();

        dotNetRef_?.Dispose();
        dotNetRef_ = null;
    }

    private sealed class KeyboardRegistration : IDisposable
    {
        private KeyboardManagerService? owner_;
        private readonly KeyboardCallbackRegistration registration_;

        public KeyboardRegistration(
            KeyboardManagerService owner,
            KeyboardCallbackRegistration registration)
        {
            owner_ = owner;
            registration_ = registration;
        }

        public void Dispose()
        {
            owner_?.Unregister(registration_);
            owner_ = null;
        }
    }

    private sealed record KeyboardCallbackRegistration(
        Func<KeyboardEventData, bool> Predicate,
        Func<KeyboardEventData, Task> Callback,
        bool OneShot);
}

public sealed class KeyboardEventData
{
    public string Key { get; set; } = "";
    public string Code { get; set; } = "";
    public bool CtrlKey { get; set; }
    public bool ShiftKey { get; set; }
    public bool AltKey { get; set; }
    public bool MetaKey { get; set; }
    public bool Repeat { get; set; }
    public bool IsEditableTarget { get; set; }
}