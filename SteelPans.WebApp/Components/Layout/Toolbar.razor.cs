using Microsoft.AspNetCore.Components;

namespace SteelPans.WebApp.Components.Layout;

public partial class Toolbar
{
    public enum ToolbarSide
    {
        Left,
        Right
    }

    private readonly List<ToolbarElement> elements_ = [];

    private bool menuOpen_;

    private bool anyOpen_ => menuOpen_ || ActiveElement is not null;

    internal IReadOnlyList<ToolbarElement> Elements => elements_;

    internal ToolbarElement? ActiveElement { get; private set; }
    internal ToolbarElement? ModalElement { get; private set; }

    private ModalPopup? elementPopup_ { get; set; }

    [Parameter] public RenderFragment? ChildContent { get; set; }

    [Parameter] public string MenuButtonLabel { get; set; } = "Open Menu";

    [Parameter] public string MenuLabel { get; set; } = "Toolbar Menu";

    [Parameter] public string ClosedIconClass { get; set; } = "bi-list";

    [Parameter] public string OpenIconClass { get; set; } = "bi-x-lg";

    [Parameter] public ToolbarSide Side { get; set; } = ToolbarSide.Left;

    private string SideClass => Side == ToolbarSide.Right
        ? "toolbar--right"
        : "toolbar--left";

    private string CssClass => $"toolbar " +
        $"{SideClass} " +
        $"{(anyOpen_ ? "toolbar__open" : "toolbar__closed")} " +
        $"{(menuOpen_ ? "toolbar--menu-open" : "toolbar--menu-closed")} " +
        $"{(ActiveElement is not null ? "toolbar--panel-open" : "toolbar--panel-closed")}";

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
        {
            ActiveElement = null;
        }

        StateHasChanged();
    }

    private async Task ToggleMenu()
    {
        if (ActiveElement is not null)
        {
            ActiveElement = null;
            menuOpen_ = true;
            return;
        }

        menuOpen_ = !menuOpen_;
        if (menuOpen_)
        {
            await NotifyOpenedAsync();
        }
    }

    private async Task OpenModalElement()
    {
        if (ActiveElement is null || elementPopup_ is null)
            return;

        ModalElement = ActiveElement;
        menuOpen_ = false;
        await elementPopup_.Open();
    }

    private async Task OpenElementAsync(ToolbarElement element)
    {
        if (element.Disabled)
            return;

        if (element.HasBody)
        {
            ActiveElement = element;
            menuOpen_ = false;
            return;
        }

        if (element.OnClick.HasDelegate)
            await element.OnClick.InvokeAsync();

        if (element.CloseOnAction)
            menuOpen_ = false;
    }

    protected override async Task OnCloseAsync()
    {
        menuOpen_ = false;
        ActiveElement = null;
        await InvokeAsync(StateHasChanged);
    }

    public override void Dispose()
    {
        ActiveElement = null;
        elements_.Clear();

        base.Dispose();
    }
}
