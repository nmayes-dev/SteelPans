namespace SteelPans.WebApp.Services;

using Microsoft.AspNetCore.Components;

public abstract class OverlayComponentBase : ComponentBase, IDisposable
{
    [Inject]
    protected OverlayManagerService Registry { get; set; } = default!;

    private bool open_ = false;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Registry.Register(this);
    }

    public virtual void Dispose()
    {
        Registry.Unregister(this);
    }

    public async Task NotifyOpenedAsync()
    {
        if (open_)
            return;

        open_ = true;
        await Registry.OnOpenComponentAsync(this);
    }

    public Task RequestCloseAsync()
    {
        if (!open_)
            return Task.CompletedTask;

        open_ = false;
        return InvokeAsync(OnCloseAsync);
    }

    protected abstract Task OnCloseAsync();
}

public class OverlayManagerService
{
    private readonly HashSet<OverlayComponentBase> components_ = [];

    public IReadOnlyCollection<OverlayComponentBase> Components => components_;

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

    public async Task OnOpenComponentAsync(OverlayComponentBase component)
    {
        foreach (var other in components_.ToArray())
        {
            if (component != other)
                await other.RequestCloseAsync();
        }
    }
}