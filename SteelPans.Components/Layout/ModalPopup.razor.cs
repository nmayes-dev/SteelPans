using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace SteelPans.Components.Layout;

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
    public bool Draggable { get; set; } = false;

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
    private bool isDragging_;
    private double dragStartClientX_;
    private double dragStartClientY_;
    private double dragStartOffsetX_;
    private double dragStartOffsetY_;
    private double offsetX_;
    private double offsetY_;
    private ElementReference? popupElement_;

    private string PopupClass
    {
        get
        {
            var classes = new List<string> { "modal-popup" };

            if (Draggable)
                classes.Add("modal-popup--draggable");

            if (isDragging_)
                classes.Add("modal-popup--dragging");

            return string.Join(' ', classes);
        }
    }

    private string? PopupStyle => Draggable
        ? $"--modal-popup-offset-x: {offsetX_}px; --modal-popup-offset-y: {offsetY_}px;"
        : null;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!focusOnRender_ || popupElement_ is null)
            return;

        focusOnRender_ = false;
        await popupElement_.Value.FocusAsync();
    }

    public async Task Open(bool closeOthers = true)
    {
        isOpen_ = true;
        focusOnRender_ = true;
        ResetDrag();

        await NotifyOpenedAsync(closeOthers);
        await InvokeAsync(StateHasChanged);
    }

    protected override async Task OnCloseAsync()
    {
        isOpen_ = false;
        isDragging_ = false;

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

    private Task OnHeaderPointerDownAsync(PointerEventArgs e)
    {
        if (!Draggable || e.Button != 0)
            return Task.CompletedTask;

        isDragging_ = true;
        dragStartClientX_ = e.ClientX;
        dragStartClientY_ = e.ClientY;
        dragStartOffsetX_ = offsetX_;
        dragStartOffsetY_ = offsetY_;

        return Task.CompletedTask;
    }

    private Task OnPointerMoveAsync(PointerEventArgs e)
    {
        if (!isDragging_)
            return Task.CompletedTask;

        offsetX_ = dragStartOffsetX_ + e.ClientX - dragStartClientX_;
        offsetY_ = dragStartOffsetY_ + e.ClientY - dragStartClientY_;

        return Task.CompletedTask;
    }

    private Task OnPointerUpAsync(PointerEventArgs e)
    {
        isDragging_ = false;
        return Task.CompletedTask;
    }

    private void ResetDrag()
    {
        isDragging_ = false;
        offsetX_ = 0;
        offsetY_ = 0;
        dragStartClientX_ = 0;
        dragStartClientY_ = 0;
        dragStartOffsetX_ = 0;
        dragStartOffsetY_ = 0;
    }
}
