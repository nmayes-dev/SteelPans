using Microsoft.AspNetCore.Components;

namespace SteelPans.WebApp.Components.Layout;

public partial class Toolbar
{
    public enum ToolbarSide
    {
        Left,
        Right
    }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    [Parameter]
    public int InitialActiveIndex { get; set; } = -1;

    [Parameter]
    public string MenuButtonLabel { get; set; } = "Open menu";

    [Parameter]
    public string MenuAriaLabel { get; set; } = "Menu";

    [Parameter]
    public string ClosedIconClass { get; set; } = "bi-list";

    [Parameter]
    public string OpenIconClass { get; set; } = "bi-x-lg";

    [Parameter]
    public bool CloseOnAction { get; set; } = true;

    [Parameter]
    public ToolbarSide Side { get; set; } = ToolbarSide.Left;

    [Parameter]
    public EventCallback OnOpen { get; set; }

    private readonly List<ToolbarElement> elements_ = [];
    private int activeIndex_ = -1;
    private bool menuOpen_;

    private string SideClass => Side == ToolbarSide.Right
        ? "toolbar--right"
        : "toolbar--left";

    public void Close()
    {
        menuOpen_ = false;
        activeIndex_ = -1;
    }

    internal void RegisterElement(ToolbarElement element)
    {
        elements_.Add(element);

        if (activeIndex_ < 0 && InitialActiveIndex >= 0 && InitialActiveIndex < elements_.Count)
            activeIndex_ = InitialActiveIndex;

        StateHasChanged();
    }

    internal void UnregisterElement(ToolbarElement element)
    {
        var index = elements_.IndexOf(element);
        if (index < 0)
            return;

        elements_.RemoveAt(index);

        if (activeIndex_ == index)
            activeIndex_ = -1;
        else if (activeIndex_ > index)
            activeIndex_--;

        StateHasChanged();
    }

    private ToolbarElement? ActiveElement =>
        activeIndex_ >= 0 && activeIndex_ < elements_.Count
            ? elements_[activeIndex_]
            : null;

    private async Task ToggleMenu()
    {
        menuOpen_ = !menuOpen_;
        if (menuOpen_ && OnOpen.HasDelegate)
        {
            await OnOpen.InvokeAsync();
        }
    }

    private async Task ActivateElementAsync(int index)
    {
        if (index < 0 || index >= elements_.Count)
            return;

        var element = elements_[index];

        if (element.HasBody)
        {
            activeIndex_ = index;
            menuOpen_ = false;
            return;
        }

        if (element.OnClick.HasDelegate)
            await element.OnClick.InvokeAsync();

        if (CloseOnAction)
            menuOpen_ = false;
    }

    private void CloseActive()
    {
        activeIndex_ = -1;
    }

    public void Dispose()
    {
        elements_.Clear();
    }
}
