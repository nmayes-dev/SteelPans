﻿using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Text;

namespace SteelPans.Shared.Services;

public sealed class SafeJSInteropService(IJSRuntime js) : IDisposable
{
    private bool isReady_ = false;
    private CancellationTokenSource? cts_;

    public void MarkSafe()
    {
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
            Console.WriteLine($"Safe JS Interop was cancelled: {ex.Message}");
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

    public async ValueTask<TValue?> InvokeAsync<TValue>(string identifier, params object?[]? args)
    {
        if (!isReady_ || cts_ is null)
        {
            Console.WriteLine("Cannot issue JS interop calls at this time!");
            return default;
        }

        return await js.InvokeAsync<TValue>(identifier, cts_.Token, args);
    }

    public async ValueTask<TValue?> InvokeAsync<TValue>(string identifier, CancellationToken token, params object?[]? args)
    {
        if (!isReady_ || cts_ is null)
        {
            Console.WriteLine("Cannot issue JS interop calls at this time!");
            return default;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            token,
            cts_.Token);
        return await js.InvokeAsync<TValue>(identifier, linkedCts.Token, args);
    }

    public ValueTask InvokeVoidAsync(string identifier, params object?[]? args)
    {
        if (!isReady_ || cts_ is null)
        {
            Console.WriteLine("Cannot issue JS interop calls at this time!");
            return ValueTask.CompletedTask;
        }

        return js.InvokeVoidAsync(identifier, cts_.Token, args);
    }

    public ValueTask InvokeVoidAsync(string identifier, CancellationToken token, params object?[]? args)
    {
        if (!isReady_ || cts_ is null)
        {
            Console.WriteLine("Cannot issue JS interop calls at this time!");
            return ValueTask.CompletedTask;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            token,
            cts_.Token);
        return js.InvokeVoidAsync(identifier, linkedCts.Token, args);
    }
}
