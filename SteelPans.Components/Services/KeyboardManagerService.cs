using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SteelPans.Components.Services;

public interface IKeyboardListener
{
    Task HandleKeyboardEventAsync(KeyboardEventData eventData);
}

public abstract class KeyboardComponentBase : ComponentBase, IKeyboardListener, IDisposable
{
    [Inject]
    protected KeyboardManagerService Keyboard { get; set; } = null!;

    private readonly List<KeyboardCallbackRegistration> callbacks_ = [];

    protected override void OnInitialized()
    {
        Keyboard.Register(this);
    }

    protected void RegisterKeyboardCallback(
        Func<KeyboardEventData, bool> predicate,
        Func<KeyboardEventData, Task> callback)
    {
        Keyboard.Register(this);
        callbacks_.Add(new KeyboardCallbackRegistration(
            predicate,
            callback,
            OneShot: false));
    }

    protected void RegisterOneShotKeyboardCallback(
        Func<KeyboardEventData, bool> predicate,
        Func<KeyboardEventData, Task> callback)
    {
        Keyboard.Register(this);
        callbacks_.Add(new KeyboardCallbackRegistration(
            predicate,
            callback,
            OneShot: true));
    }

    public async Task HandleKeyboardEventAsync(KeyboardEventData eventData)
    {
        foreach (var registration in callbacks_.ToArray())
        {
            if (!registration.Predicate(eventData))
                continue;

            await registration.Callback(eventData);

            if (registration.OneShot)
                callbacks_.Remove(registration);
        }
    }

    public virtual void Dispose()
    {
        callbacks_.Clear();
        Keyboard.Unregister(this);
    }

    private sealed record KeyboardCallbackRegistration(
        Func<KeyboardEventData, bool> Predicate,
        Func<KeyboardEventData, Task> Callback,
        bool OneShot);
}

public sealed class KeyboardManagerService
{
    private readonly IJSRuntime js_;
    private DotNetObjectReference<KeyboardManagerService>? dotNetRef_;
    private readonly List<IKeyboardListener> listeners_ = [];

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

    public void Register(IKeyboardListener listener)
    {
        if (!listeners_.Contains(listener))
            listeners_.Add(listener);
    }

    public void Unregister(IKeyboardListener listener)
    {
        listeners_.Remove(listener);
    }

    [JSInvokable]
    public async Task OnKeyDownAsync(KeyboardEventData eventData)
    {
        foreach (var listener in listeners_.ToArray())
        {
            await listener.HandleKeyboardEventAsync(eventData);
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

        dotNetRef_?.Dispose();
    }
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