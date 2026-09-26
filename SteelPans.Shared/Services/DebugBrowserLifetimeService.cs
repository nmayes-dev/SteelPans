using Microsoft.Extensions.Hosting;
using System.Diagnostics;

namespace SteelPans.Shared.Services;

public sealed class DebugBrowserLifetimeService : IDisposable
{
    private readonly IHostApplicationLifetime lifetime_;
    private readonly TimeSpan checkRate_ =
        TimeSpan.FromMilliseconds(250);

    private readonly TimeSpan timeoutDuration_ =
        TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource cts_ = new();
    private readonly Lock lock_ = new();

    private long? lastBeat_;

    public DebugBrowserLifetimeService(
        IHostApplicationLifetime lifetime)
    {
        lifetime_ = lifetime;

        _ = WaitForTimeoutAsync();
    }

    public void Heartbeat()
    {
        lock (lock_)
        {
            lastBeat_ = Stopwatch.GetTimestamp();
        }
    }

    private async Task WaitForTimeoutAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(
                    checkRate_,
                    cts_.Token);

                lock (lock_)
                {
                    if (lastBeat_ is not null &&
                        Stopwatch.GetElapsedTime(lastBeat_.Value) >
                        timeoutDuration_)
                    {
                        lifetime_.StopApplication();
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
            when (cts_.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        cts_.Cancel();
        cts_.Dispose();
    }
}