using Microsoft.AspNetCore.Components;
using SteelPans.Components.Layout;
using SteelPans.Components.Services;


namespace SteelPans.Components.Toolbar;

public partial class Toolbar : OverlayComponentBase
{
    public enum ToolbarSide
    {
        Left,
        Right
    }

    private readonly List<ToolbarElement> contentElements_ = [];
    private readonly List<ToolbarElement> rootElements_ = [];

    private bool menuOpen_;

    private bool anyOpen_ => menuOpen_ || ActiveElement is not null;

    internal bool IsMenuOpen => menuOpen_;
    internal bool IsPanelOpen => ActiveElement is not null;
    internal bool IsAnyOpen => IsMenuOpen || IsPanelOpen;

    internal IReadOnlyList<ToolbarElement> Elements => rootElements_;

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
        IsAnyOpen ?  "toolbar--open" : null,
        IsMenuOpen ? "toolbar--menu-open" : null,
        IsPanelOpen ? "toolbar--panel-open" : null,
    }.Where(x => !string.IsNullOrWhiteSpace(x)));


    internal void RegisterElement(ToolbarElement element, bool root)
    {
        if (element.HasBody)
            contentElements_.Add(element);

        if (root)
            rootElements_.Add(element);

        StateHasChanged();
    }

    internal void UnregisterElement(ToolbarElement element, bool root)
    {
        if (element.HasBody)
            contentElements_.Remove(element);

        if (root)
            rootElements_.Remove(element);

        if (ReferenceEquals(ActiveElement, element))
            ActiveElement = null;

        StateHasChanged();
    }

    internal async Task OpenElementAsync(ToolbarElement element)
    {   
        Console.WriteLine($"Fired @onclick for \"{element.Text}\"");

        if (element.Disabled || element.HasSubMenu)
            return;

        if (element.HasBody)
        {
            ActiveElement = element;
            menuOpen_ = false;
            await NotifyOpenedAsync();
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (element.OnClick.HasDelegate)
            await element.OnClick.InvokeAsync();

        if (element.CloseOnAction)
            menuOpen_ = false;

        await InvokeAsync(StateHasChanged);
    }

    private async Task ToggleMenuAsync()
    {
        if (ActiveElement is not null)
        {
            ActiveElement = null;
            menuOpen_ = true;
            await NotifyOpenedAsync();
            await InvokeAsync(StateHasChanged);
            return;
        }

        menuOpen_ = !menuOpen_;
        if (menuOpen_)
            await NotifyOpenedAsync();
        else
            await RequestCloseAsync();
    }

    private async Task OpenModalElement()
    {
        if (ActiveElement is null)
            return;

        ModalElement = ActiveElement;
        ActiveElement = null;
        menuOpen_ = false;
        await Modals.OpenAsync("ToolbarContent");
    }

    protected override async Task OnCloseAsync()
    {
        menuOpen_ = false;
        ActiveElement = null;
    }

    public override void Dispose()
    {
        ActiveElement = null;
        rootElements_.Clear();
        contentElements_.Clear();

        base.Dispose();
    }
}
