using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using SteelPans.Components.Services;

namespace SteelPans.WebApp.Services;

public class TaskRunnerService : IDisposable
{
    public bool Busy { get; private set; }

    private readonly ILogger<TaskRunnerService> logger_;
    private readonly MessagePopupService messager_;
    private bool disposed_;

    public TaskRunnerService(
        MessagePopupService messager,
        ILogger<TaskRunnerService> logger)
    {
        messager_ = messager;
        logger_ = logger;
    }

    public Task<bool> RunSafe(Func<Task<string>> job)
    {
        return Run(true, job);
    }

    public Task<bool> RunSafe(Func<Task> job)
    {
        return Run(true, job);
    }

    public Task<bool> RunUnsafe(Func<Task<string>> job)
    {
        return Run(false, job);
    }

    public Task<bool> RunUnsafe(Func<Task> job)
    {
        return Run(false, job);
    }

    public void Dispose()
    {
        if (disposed_)
            return;

        disposed_ = true;
    }


    private async Task<bool> Run(bool block, Func<Task<string>> job)
    {
        Busy = block;

        try
        {
            var message = await job();
            await messager_.DisplayAsync(message);
            return true;
        }
        catch (OperationCanceledException ex)
        {
            logger_.LogDebug(ex, "UI task was cancelled");
            await messager_.DisplayAsync(ex.Message, MessageKind.Error);
            return false;
        }
        catch (Exception ex)
        {
            logger_.LogError(ex, "UI task failed");
            await messager_.DisplayAsync(ex.Message, MessageKind.Error);
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task<bool> Run(bool block, Func<Task> job)
    {
        Busy = block;

        try
        {
            await job();
            return true;
        }
        catch (OperationCanceledException ex)
        {
            logger_.LogDebug(ex, "UI task was cancelled");
            await messager_.DisplayAsync(ex.Message, MessageKind.Error);
            return false;
        }
        catch (Exception ex)
        {
            logger_.LogError(ex, "UI task failed");
            await messager_.DisplayAsync(ex.Message, MessageKind.Error);
            return false;
        }
        finally
        {
            Busy = false;
        }
    }
}
