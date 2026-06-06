using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using SteelPans.Components.Services;

namespace SteelPans.Components.Layout;

public partial class ModalPopup<TPayload> : OverlayComponentBase, IModalPopup
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
    public EventCallback<TPayload?> OnOpen { get; set; }

    [Parameter]
    public Func<TPayload?, Task<bool>>? OnConfirm { get; set; }

    [Parameter]
    public EventCallback<TPayload?> OnClose { get; set; }

    [Parameter]
    public Func<Task<bool>> CanOpen { get; set; } = () => Task.FromResult(true);

    public object? Payload => payload_;

    private TPayload? payload_;
    private Func<Task>? onSuccess_;
    private double dragStartClientX_;
    private double dragStartClientY_;
    private double dragStartOffsetX_;
    private double dragStartOffsetY_;
    private double offsetX_;
    private double offsetY_;
    private ElementReference? popupElement_;

    protected bool isOpen_;
    protected bool focusOnRender_;
    protected bool isDragging_;

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
                _ => RequestCloseAsync()));

        keyCallbacks_.Add(
            Keyboard.Register(
                e => isOpen_ && e.Key == "Enter" && !e.IsEditableTarget,
                _ => ConfirmAsync()));

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

    public virtual Task OpenAsync()
    {
        return OpenAsync(null, null);
    }

    public virtual async Task OpenAsync(object? payload = null, ModalOptions? options = null)
    {
        if (!await CanOpen())
            return;

        var opts = options ?? new ModalOptions();

        payload_ = GetTypedPayload(payload);
        onSuccess_ = opts.OnSuccess;
        isOpen_ = true;
        focusOnRender_ = true;
        ResetDrag();

        await OnOpen.InvokeAsync(payload_);
        await NotifyOpenedAsync(opts.CloseOthers);
        await InvokeAsync(StateHasChanged);
    }

    public virtual async Task ConfirmAsync()
    {
        if (OnConfirm is not null && !await OnConfirm.Invoke(payload_))
            return;

        if (onSuccess_ is not null)
            await onSuccess_();

        await RequestCloseAsync();
    }

    protected override async Task OnCloseAsync()
    {
        var payload = payload_;

        isOpen_ = false;
        isDragging_ = false;
        payload_ = default;
        onSuccess_ = null;

        await OnClose.InvokeAsync(payload);
        await InvokeAsync(StateHasChanged);
    }

    private static TPayload? GetTypedPayload(object? payload)
    {
        if (payload is null)
            return default;

        if (payload is TPayload typedPayload)
            return typedPayload;

        throw new InvalidOperationException(
            $"Modal payload type mismatch. Expected '{typeof(TPayload).Name}', but received '{payload.GetType().Name}'.");
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

    protected void ResetDrag()
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