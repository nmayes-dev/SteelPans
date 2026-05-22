using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SteelPans.WebApp.Components.Layout.Toolbar;

public partial class Toolbar
{
    public enum ToolbarSide
    {
        Left,
        Right
    }
    private ElementReference toolbarElement_;
    private DotNetObjectReference<Toolbar>? dotNetReference_;

    private readonly List<ToolbarElement> elements_ = [];

    private bool menuOpen_;
    private bool animating_;

    private bool anyOpen_ => menuOpen_ || ActiveElement is not null;

    private ModalPopup? elementPopup_;

    internal bool IsMenuOpen => menuOpen_;
    internal bool IsPanelOpen => ActiveElement is not null;
    internal bool IsAnyOpen => IsMenuOpen || IsPanelOpen;
    internal bool IsAnimating => animating_;

    internal IReadOnlyList<ToolbarElement> Elements => elements_;

    internal ToolbarElement? ActiveElement { get; private set; }
    internal ToolbarElement? ModalElement { get; private set; }

    [Parameter] public RenderFragment? ChildContent { get; set; }

    [Parameter] public string MenuButtonLabel { get; set; } = "Open Menu";

    [Parameter] public string MenuLabel { get; set; } = "Toolbar Menu";

    [Parameter] public string ClosedIconClass { get; set; } = "bi-list";

    [Parameter] public string OpenIconClass { get; set; } = "bi-x-lg";

    [Parameter] public ToolbarSide Side { get; set; } = ToolbarSide.Left;

    private string ToolbarClass => string.Join(
    " ",
    new[]
    {
        "toolbar",
        Side == ToolbarSide.Left ? "toolbar--left" : "toolbar--right",
        IsAnyOpen ?  "toolbar__open" : null,
        IsMenuOpen ? "toolbar--menu-open" : null,
        IsPanelOpen ? "toolbar--panel-open" : null,
        IsAnimating ? "toolbar--animating" : null
    }.Where(x => !string.IsNullOrWhiteSpace(x)));


    internal void RegisterElement(ToolbarElement element)
    {
        if (!elements_.Contains(element))
        {
            elements_.Add(element);
            StateHasChanged();
        }
    }

    internal void UnregisterElement(ToolbarElement element)
    {
        elements_.Remove(element);

        if (ReferenceEquals(ActiveElement, element))
            ActiveElement = null;

        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnToolbarTransitionEnded()
    {
        animating_ = false;
        await InvokeAsync(StateHasChanged);
    }

    private async Task BeginToolbarAnimationAsync()
    {
        animating_ = true;

        dotNetReference_ ??= DotNetObjectReference.Create(this);

        await JS.InvokeVoidAsync(
            "toolbar.waitForSurfaceTransition",
            toolbarElement_,
            dotNetReference_);
    }

    internal async Task OpenElementAsync(ToolbarElement element)
    {
        if (element.Disabled || element.HasSubMenu)
            return;

        if (element.HasBody)
        {
            ActiveElement = element;
            menuOpen_ = false;
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (element.OnClick.HasDelegate)
            await element.OnClick.InvokeAsync();

        if (element.CloseOnAction)
            menuOpen_ = false;
    }

    private async Task ToggleMenuAsync()
    {
        await BeginToolbarAnimationAsync();

        if (ActiveElement is not null)
        {
            ActiveElement = null;
            menuOpen_ = true;
            return;
        }

        menuOpen_ = !menuOpen_;
        if (menuOpen_)
            await NotifyOpenedAsync();
    }

    private async Task OpenModalElement()
    {
        if (ActiveElement is null || elementPopup_ is null)
            return;

        await BeginToolbarAnimationAsync();
        ModalElement = ActiveElement;
        menuOpen_ = false;
        await elementPopup_.Open();
    }

    protected override async Task OnCloseAsync()
    {
        await BeginToolbarAnimationAsync();
        menuOpen_ = false;
        ActiveElement = null;
        await InvokeAsync(StateHasChanged);
    }

    public override void Dispose()
    {
        dotNetReference_?.Dispose();
        ActiveElement = null;
        elements_.Clear();

        base.Dispose();
    }
}
