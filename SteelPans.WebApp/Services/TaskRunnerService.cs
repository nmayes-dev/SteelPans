using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace SteelPans.WebApp.Services;

public class TaskRunnerService : IDisposable
{
    public bool Busy { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public string Error { get; private set; } = string.Empty;

    private readonly IDisposable navEvent_;
    private bool disposed_;

    public TaskRunnerService(NavigationManager nav)
    {
        navEvent_ = nav.RegisterLocationChangingHandler(OnLocationChanged);
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
        navEvent_.Dispose();
    }

    private ValueTask OnLocationChanged(LocationChangingContext context)
    {
        if (!Busy)
            InitializeState(block: false, resetMessage: true);

        return ValueTask.CompletedTask;
    }

    private void InitializeState(bool block, bool resetMessage)
    {
        Busy = block;
        Message = resetMessage ? string.Empty : Message;
        Error = string.Empty;
    }

    private async Task<bool> Run(bool block, Func<Task<string>> job)
    {
        InitializeState(block, resetMessage: true);

        try
        {
            Message = await job();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task<bool> Run(bool block, Func<Task> job)
    {
        InitializeState(block, resetMessage: false);

        try
        {
            await job();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            Busy = false;
        }
    }
}
