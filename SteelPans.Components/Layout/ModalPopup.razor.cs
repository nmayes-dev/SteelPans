using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using SteelPans.Components.Services;

namespace SteelPans.Components.Layout;

public partial class ModalPopup : OverlayComponentBase
{
    [Inject]
    private ModalPopupService Modals { get; set; } = default!;

    [Parameter]
    public required string Id { get; set; }

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
    public RenderFragment? Buttons { get; set; }

    [Parameter]
    public string? CloseButton { get; set; }

    [Parameter]
    public string? CloseButtonClass { get; set; }

    [Parameter]
    public string? ConfirmButton { get; set; }

    [Parameter]
    public string? ConfirmButtonClass { get; set; }

    [Parameter]
    public EventCallback OnOpen { get; set; }

    [Parameter]
    public EventCallback OnConfirm { get; set; }

    [Parameter]
    public EventCallback OnClose { get; set; }

    [Parameter]
    public Func<Task<bool>> CanOpen { get; set; } = async () => true;

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

    private readonly List<IDisposable> keyCallbacks_ = [];

    protected override void OnInitialized()
    {
        if (Buttons is null && CloseButton is null && ConfirmButton is null && !ShowCloseButton && CloseOnBackdropClick)
        {
            throw new InvalidOperationException("This modal will be unable to close");
        }

        keyCallbacks_.Add(
            Keyboard.Register(
                e => isOpen_ && e.Key == "Escape" && !e.IsEditableTarget,
                async _ => await OnCloseAsync()));

        keyCallbacks_.Add(
                Keyboard.Register(
                    e => isOpen_ && e.Key == "Enter" && !e.IsEditableTarget,
                    async _ => await ConfirmAsync()));

        Modals.Register(Id, this);

        base.OnInitialized();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!focusOnRender_ || popupElement_ is null)
            return;

        focusOnRender_ = false;
        await popupElement_.Value.FocusAsync();
    }

    public async Task OpenAsync(bool closeOthers = true)
    {
        isOpen_ = true;
        focusOnRender_ = true;
        ResetDrag();

        await OnOpen.InvokeAsync();
        await NotifyOpenedAsync(closeOthers);
        await InvokeAsync(StateHasChanged);
    }

    public async Task ConfirmAsync()
    {
        await OnConfirm.InvokeAsync();
        await OnCloseAsync();
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

    public override void Dispose()
    {
        Modals.Unregister(Id, this);

        foreach (var registration in keyCallbacks_)
            registration.Dispose();

        keyCallbacks_.Clear();

        base.Dispose();
    }
}
