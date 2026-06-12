@@ -0,0 + 1,70 @@
﻿using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Text;

namespace SteelPans.Shared.Services;

public sealed class SafeJSInteropService(IJSRuntime js) : IDisposable
{
    private bool isReady_ = false;

    public void MarkSafe()
    {
        isReady_ = true;
    }

    public void MarkUnsafe()
    {
        isReady_ = false;
    }

    public void Dispose()
    {
        isReady_ = false;
    }

    public async ValueTask<TValue?> InvokeAsync<TValue>(string identifier, params object?[]? args)
    {
        if (!isReady_)
        {
            Console.WriteLine("Cannot issue JS interop calls at this time!");
            return default;
        }

        return await js.InvokeAsync<TValue>(identifier, args);
    }

    public async ValueTask<TValue?> InvokeAsync<TValue>(string identifier, CancellationToken token, params object?[]? args)
    {
        if (!isReady_)
        {
            Console.WriteLine("Cannot issue JS interop calls at this time!");
            return default;
        }

        return await js.InvokeAsync<TValue>(identifier, token, args);
    }

    public ValueTask InvokeVoidAsync(string identifier, params object?[]? args)
    {
        if (!isReady_)
        {
            Console.WriteLine("Cannot issue JS interop calls at this time!");
            return ValueTask.CompletedTask;
        }

        return js.InvokeVoidAsync(identifier, args);
    }

    public ValueTask InvokeVoidAsync(string identifier, CancellationToken token, params object?[]? args)
    {
        if (!isReady_)
        {
            Console.WriteLine("Cannot issue JS interop calls at this time!");
            return ValueTask.CompletedTask;
        }

        return js.InvokeVoidAsync(identifier, token, args);
    }
}
