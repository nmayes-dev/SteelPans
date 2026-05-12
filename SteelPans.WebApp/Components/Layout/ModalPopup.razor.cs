using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace SteelPans.WebApp.Components.Layout;

public partial class ModalPopup
{

    [Parameter]
    public string Title { get; set; } = string.Empty;

    [Parameter]
    public string? Subtitle { get; set; }

    [Parameter]
    public string TitleId { get; set; } = $"modal-popup-title-{Guid.NewGuid():N}";

    [Parameter]
    public bool CloseOnBackdropClick { get; set; } = true;

    [Parameter]
    public bool ShowCloseButton { get; set; } = true;

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    [Parameter]
    public RenderFragment? Actions { get; set; }

    [Parameter]
    public EventCallback OnEnter { get; set; }

    [Parameter]
    public EventCallback OnClose { get; set; }
        
    private bool isOpen_;
    private bool focusOnRender_;
    private ElementReference? popupElement_;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!focusOnRender_ || popupElement_ is null)
            return;

        focusOnRender_ = false;
        await popupElement_.Value.FocusAsync();
    }

    public async Task Open()
    {
        isOpen_ = true;
        focusOnRender_ = true;

        await NotifyOpenedAsync();
        await InvokeAsync(StateHasChanged);
    }

    protected override async Task OnCloseAsync()
    {
        isOpen_ = false;
        await OnClose.InvokeAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnBackdropClickedAsync()
    {
        if (!CloseOnBackdropClick)
            return;

        await RequestCloseAsync();
    }

    private async Task OnKeyDownAsync(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "Escape":
                await RequestCloseAsync();
                break;
            case "Enter":
                await OnEnter.InvokeAsync();
                break;
        }
    }
}