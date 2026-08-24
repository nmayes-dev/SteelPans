using Microsoft.JSInterop;
using SteelPans.Shared.Extensions;

namespace SteelPans.Shared.Services;

public sealed class SafeJSInteropService(IJSRuntime js) : IDisposable
{
    private bool isReady_ = false;
    private CancellationTokenSource? cts_;

    public void MarkSafe()
    {
        cts_?.Dispose();
        cts_ = new();
        isReady_ = true;
    }

    public void MarkUnsafe()
    {
        try
        {
            isReady_ = false;
            cts_?.Cancel();
        }
        catch (Exception ex)
        {
            ex.Log("Safe JS Interop was cancelled:");
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
        if (!isReady_ || cts_ is null)
        {
            Console.WriteLine("Cannot issue JS interop calls at this time!");
            return default;
        }

        try
        {
            return await js.InvokeAsync<TValue>(
                identifier,
                cts_.Token,
                args);
        }
        catch (JSDisconnectedException ex)
        {
            HandleDisconnected(ex);
            return default;
        }
    }

    public async ValueTask<TValue?> InvokeAsync<TValue>(
        string identifier,
        CancellationToken token,
        params object?[]? args)
    {
        if (!isReady_ || cts_ is null)
        {
            Console.WriteLine("Cannot issue JS interop calls at this time!");
            return default;
        }

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                token,
                cts_.Token);

            return await js.InvokeAsync<TValue>(
                identifier,
                linkedCts.Token,
                args);
        }
        catch (JSDisconnectedException ex)
        {
            HandleDisconnected(ex);
            return default;
        }
    }

    public async ValueTask InvokeVoidAsync(
        string identifier,
        params object?[]? args)
    {
        if (!isReady_ || cts_ is null)
        {
            Console.WriteLine("Cannot issue JS interop calls at this time!");
            return;
        }

        try
        {
            await js.InvokeVoidAsync(
                identifier,
                cts_.Token,
                args);
        }
        catch (JSDisconnectedException ex)
        {
            HandleDisconnected(ex);
        }
    }

    public async ValueTask InvokeVoidAsync(
        string identifier,
        CancellationToken token,
        params object?[]? args)
    {
        if (!isReady_ || cts_ is null)
        {
            Console.WriteLine("Cannot issue JS interop calls at this time!");
            return;
        }

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                token,
                cts_.Token);

            await js.InvokeVoidAsync(
                identifier,
                linkedCts.Token,
                args);
        }
        catch (JSDisconnectedException ex)
        {
            HandleDisconnected(ex);
        }
    }

    private void HandleDisconnected(JSDisconnectedException ex)
    {
        isReady_ = false;
        ex.Log("JS interop was disconnected before this task was completed.");
    }
}