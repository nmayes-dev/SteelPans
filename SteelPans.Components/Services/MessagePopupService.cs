using Microsoft.Extensions.Logging;
using SteelPans.Components.Layout;
using static SteelPans.Components.Layout.MessagePopup;

namespace SteelPans.Components.Services;


public enum MessageKind
{
    Message,
    Warning,
    Error,
}

internal sealed record MessageNotification(Guid Id, string Message, MessageKind Kind, TimeSpan Duration);

public sealed class MessagePopupService
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(3);
    private readonly ILogger<ModalPopupService> logger_;
    internal event Func<MessageNotification, Task>? Requested;
    internal event Func<Guid?, Task>? ClearRequested;


    public MessagePopupService(ILogger<ModalPopupService> logger)
    {
        logger_ = logger;
    }


    public async Task<Guid> DisplayAsync(string message, MessageKind kind = MessageKind.Message, TimeSpan? duration = null)
    {
        var handlers = Requested;
        if (handlers is null)
            return Guid.Empty;

        if (string.IsNullOrWhiteSpace(message))
            return Guid.Empty;

        var displayDuration = duration ?? DefaultDuration;

        if (displayDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        var id = Guid.NewGuid();
        var notification = new MessageNotification(
            id,
            message,
            kind,
            displayDuration
        );

        var tasks = handlers.GetInvocationList()
            .Cast<Func<MessageNotification, Task>>()
            .Select(handler => handler(notification));

        await Task.WhenAll(tasks);
        return id;
    }

    public async Task ClearAsync(Guid? id = null)
    {
        if (id == Guid.Empty)
            return;

        var handlers = ClearRequested;
        if (handlers is null)
            return;

        var tasks = handlers.GetInvocationList()
            .Cast<Func<Guid?, Task>>()
            .Select(handler => handler(id));

        await Task.WhenAll(tasks);
    }
}