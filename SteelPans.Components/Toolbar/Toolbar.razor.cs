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
    private enum ToolbarState
    {
        Closed,
        Menu,
        Content,
    }

    private readonly List<ToolbarElement> contentElements_ = [];
    private readonly List<ToolbarElement> rootElements_ = [];
    private bool singleElement_ => rootElements_.Count == 1;

    internal IReadOnlyList<ToolbarElement> Elements => rootElements_.OrderBy(x => x.Order).ToArray();

    internal ToolbarElement? ActiveElement { get; private set; }
    internal ToolbarElement? ModalElement { get; private set; }

    [Parameter] public RenderFragment? ChildContent { get; set; }

    [Parameter] public string MenuButtonLabel { get; set; } = "Open Menu";

    [Parameter] public string MenuLabel { get; set; } = "Toolbar Menu";

    [Parameter] public string ClosedIconClass { get; set; } = "bi-list";

    [Parameter] public string OpenIconClass { get; set; } = "bi-x-lg";

    [Parameter] public ToolbarSide Side { get; set; } = ToolbarSide.Left;

    private ToolbarState state_ = ToolbarState.Closed;


    private string SideClass => Side switch
    {
        ToolbarSide.Left => " toolbar--left",
        ToolbarSide.Right => " toolbar--right",
        _ => string.Empty,
    };

    private string StateClass => state_ switch
    {
        ToolbarState.Menu => " toolbar--open toolbar--menu-open",
        ToolbarState.Content => " toolbar--open toolbar--panel-open",
        _ => string.Empty,
    };

    private string ButtonClass => "bi " + state_ switch
    {
        ToolbarState.Closed => ClosedIconClass,
        _ => OpenIconClass,
    };

    private string ToolbarClass => $"toolbar{SideClass}{StateClass}";


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

    private async Task OpenMenuAsync()
    {
        state_ = ToolbarState.Menu;
        await NotifyOpenedAsync();
    }

    internal async Task OpenElementAsync(ToolbarElement element)
    {   
        if (element.Disabled || element.HasSubMenu)
            return;

        if (element.HasBody)
        {
            ActiveElement = element;
            state_ = ToolbarState.Content;
            await NotifyOpenedAsync();
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (element.OnClick.HasDelegate)
            await element.OnClick.InvokeAsync();

        if (element.CloseOnAction)
            state_ = ToolbarState.Closed;

        await InvokeAsync(StateHasChanged);
    }

    private async Task OpenMenuOrContentAsync()
    {
        if (!singleElement_)
        {
            await OpenMenuAsync();
            return;
        }

        var onlyElement = rootElements_.First();
        if (!onlyElement.Disabled && onlyElement.HasBody && !onlyElement.HasSubMenu)
        {
            await OpenElementAsync(onlyElement);
            return;
        }

        await OpenMenuAsync();
    }

    private async Task CloseCurrentElementAsync()
    {
        ActiveElement = null;
        state_ = singleElement_ ? ToolbarState.Closed : ToolbarState.Menu;

        await InvokeAsync(StateHasChanged);
    }


    private async Task ChangeStateAsync()
    {
        switch (state_)
        {
            case ToolbarState.Closed:
                await OpenMenuOrContentAsync();
                return;
            case ToolbarState.Menu:
                await RequestCloseAsync();
                return;
            case ToolbarState.Content:
                await CloseCurrentElementAsync();
                return;
        }
    }

    private async Task OpenModalElement()
    {
        if (ActiveElement is null)
            return;

        ModalElement = ActiveElement;
        ActiveElement = null;
        state_ = ToolbarState.Closed;
        await Modals.OpenAsync("ToolbarContent");
    }

    protected override async Task OnCloseAsync()
    {
        state_ = ToolbarState.Closed;
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
