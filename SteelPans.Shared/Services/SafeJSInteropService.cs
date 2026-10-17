using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace SteelPans.Shared.Services;

public sealed class SafeJSInteropService(
    IJSRuntime js,
    ILogger<SafeJSInteropService> logger) : IDisposable
{
    private bool isReady_ = false;
    private CancellationTokenSource? cts_;

    public void MarkSafe()
    {
        cts_?.Dispose();
        cts_ = new();
        isReady_ = true;
        logger.LogDebug("JS interop marked safe");
    }

    public void MarkUnsafe()
    {
        try
        {
            isReady_ = false;
            cts_?.Cancel();
            logger.LogDebug("JS interop marked unsafe");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed while cancelling safe JS interop operations");
        }
        finally
        {
            cts_?.Dispose();
            cts_ = null;
        }
    }

    public void Dispose()
    {
        MarkUnsafe();
    }

    public async ValueTask<TValue?> InvokeAsync<TValue>(
        string identifier,
        params object?[]? args)
    {
        if (!CanInvoke(identifier))
            return default;

        try
        {
            return await js.InvokeAsync<TValue>(
                identifier,
                cts_!.Token,
                args);
        }
        catch (JSDisconnectedException ex)
        {
            HandleDisconnected(identifier, ex);
            return default;
        }
    }

    public async ValueTask<TValue?> InvokeAsync<TValue>(
        string identifier,
        CancellationToken token,
        params object?[]? args)
    {
        if (!CanInvoke(identifier))
            return default;

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                token,
                cts_!.Token);

            return await js.InvokeAsync<TValue>(
                identifier,
                linkedCts.Token,
                args);
        }
        catch (JSDisconnectedException ex)
        {
            HandleDisconnected(identifier, ex);
            return default;
        }
    }

    public async ValueTask InvokeVoidAsync(
        string identifier,
        params object?[]? args)
    {
        if (!CanInvoke(identifier))
            return;

        try
        {
            await js.InvokeVoidAsync(
                identifier,
                cts_!.Token,
                args);
        }
        catch (JSDisconnectedException ex)
        {
            HandleDisconnected(identifier, ex);
        }
    }

    public async ValueTask InvokeVoidAsync(
        string identifier,
        CancellationToken token,
        params object?[]? args)
    {
        if (!CanInvoke(identifier))
            return;

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                token,
                cts_!.Token);

            await js.InvokeVoidAsync(
                identifier,
                linkedCts.Token,
                args);
        }
        catch (JSDisconnectedException ex)
        {
            HandleDisconnected(identifier, ex);
        }
    }

    private bool CanInvoke(string identifier)
    {
        if (isReady_ && cts_ is not null)
            return true;

        logger.LogDebug(
            "Skipped JS interop call {Identifier} because JS interop is not currently safe",
            identifier);
        return false;
    }

    private void HandleDisconnected(string identifier, JSDisconnectedException ex)
    {
        isReady_ = false;
        logger.LogWarning(
            ex,
            "JS interop disconnected while invoking {Identifier}; future calls will be skipped until marked safe again",
            identifier);
    }
}
