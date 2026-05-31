namespace SteelPans.Components.Services;

using Microsoft.AspNetCore.Components;


public abstract class OverlayComponentBase : ComponentBase, IDisposable
{
    [Inject]
    protected OverlayManagerService Registry { get; set; } = default!;
    public bool IsOpen { get; private set; } = false;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Registry.Register(this);
    }

    public virtual void Dispose()
    {
        Registry.Unregister(this);
    }

    public async Task NotifyOpenedAsync(bool closeOthers = true)
    {
        if (IsOpen)
            return;

        IsOpen = true;
        await Registry.OnOpenComponentAsync(this, closeOthers);
        await InvokeAsync(StateHasChanged);
    }

    public async Task RequestCloseAsync()
    {
        if (!IsOpen)
            return;

        IsOpen = false;
        await OnCloseAsync();
        await InvokeAsync(StateHasChanged);
    }

    protected abstract Task OnCloseAsync();
}

public class OverlayManagerService
{
    private readonly HashSet<OverlayComponentBase> components_ = [];

    public IReadOnlyCollection<OverlayComponentBase> Components => components_;

    public bool AnyOpen => components_.Any(x => x.IsOpen);

    public void Register(OverlayComponentBase component)
    {
        components_.Add(component);
    }

    public void Unregister(OverlayComponentBase component)
    {
        components_.Remove(component);
    }

    public async Task RequestCloseAllComponentsAsync()
    {
        foreach (var component in components_)
        {
            await component.RequestCloseAsync();
        }
    }

    public async Task OnOpenComponentAsync(OverlayComponentBase component, bool closeOthers)
    {
        if (!closeOthers)
            return;

        foreach (var other in components_.ToArray())
        {
            if (component != other)
                await other.RequestCloseAsync();
        }
    }
}