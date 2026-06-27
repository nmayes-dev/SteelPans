using Microsoft.AspNetCore.Components;

namespace SteelPans.WebApp.Components.Layout;

public partial class ExpandingOverlay
{
    public enum Direction
    {
        Left,
        Right,
    }

    [Parameter] public required Direction ExpandDirection { get; set; }

    [Parameter] public string Text { get; set; } = string.Empty;
    [Parameter] public string Title { get; set; } = string.Empty;

    [Parameter] public RenderFragment? ChildContent { get; set; }

    [Parameter] public string MenuButtonLabel { get; set; } = "Open Menu";

    [Parameter] public string MenuLabel { get; set; } = "Toolbar Menu";

    [Parameter] public string ClosedIconClass { get; set; } = "list";

    [Parameter] public string OpenIconClass { get; set; } = "x-lg";


    private bool HasButtonText => !string.IsNullOrWhiteSpace(Text);
    private bool ShowButtonText => HasButtonText && !IsOpen;
    private string ClosedFullIconClass => "bi bi-" + ClosedIconClass;
    private string OpenFullIconClass => "bi bi-" + OpenIconClass;

    private string IconClass => (IsOpen ? OpenFullIconClass : ClosedFullIconClass) + (ShowButtonText ? " hide" : string.Empty);
    private string TextClass => "overlay__header-button-text " + (ShowButtonText ? "overlay__header-button-text--show" : "overlay__header-button-text--hide");

    private string DirectionClass => ExpandDirection switch
    {
        Direction.Left => " overlay--left",
        Direction.Right => " overlay--right",
        _ => string.Empty,
    };

    private string StateClass => IsOpen ? " overlay--open" : " overlay--closed";

    private string OverlayClass => $"overlay{StateClass}{DirectionClass}";


    private async Task ToggleStateAsync()
    {
        if (IsOpen)
        {
            await RequestCloseAsync();
        }
        else
        {
            await NotifyOpenedAsync();
        }
    }

}
