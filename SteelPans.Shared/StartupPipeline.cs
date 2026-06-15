using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SteelPans.Shared;

public static class StartupPipeline
{
    public static StartupPipeline<string[], TResult> AddStep<TResult>(Func<string[], Task<TResult>> startOperation)
    {
        return new StartupPipeline<string[], TResult>(startOperation);
    }
    public static StartupPipeline<string[], TResult> AddStep<TResult>(Func<string[], TResult> startOperation)
    {
        return new StartupPipeline<string[], TResult>(args => Task.FromResult(startOperation.Invoke(args)));
    }
}

public sealed class StartupPipeline<TInput, TOutput>
{
    private readonly Func<TInput, Task<TOutput>> operation_;

    internal StartupPipeline(Func<TInput, Task<TOutput>> operation)
    {
        operation_ = operation;
    }

    public StartupPipeline<TInput, TNext> AddStep<TNext>(
        Func<TOutput, Task<TNext>> step)
    {
        return new StartupPipeline<TInput, TNext>(async input =>
        {
            var output = await operation_(input);

            return await StartupStepExecutor.ExecuteAsync(
                () => step(output));
        });
    }

    public StartupPipeline<TInput, Unit> AddStep(
        Func<TOutput, Task> step)
    {
        return new StartupPipeline<TInput, Unit>(async input =>
        {
            var output = await operation_(input);

            await StartupStepExecutor.ExecuteAsync(
                () => step(output));

            return Unit.Value;
        });
    }

    public StartupPipeline<TInput, TNext> AddStep<TNext>(
        Func<TOutput, TNext> step)
    {
        return new StartupPipeline<TInput, TNext>(async input =>
        {
            var output = await operation_(input);

            return await StartupStepExecutor.ExecuteAsync(
                () => Task.FromResult(step(output)));
        });
    }

    public StartupPipeline<TInput, Unit> AddStep(Action<TOutput> step)
    {
        return new StartupPipeline<TInput, Unit>(async input =>
        {
            var output = await operation_(input);

            await StartupStepExecutor.ExecuteAsync(() =>
            {
                step(output);
                return Task.CompletedTask;
            });

            return Unit.Value;
        });
    }

    public Task<TOutput> RunAsync(TInput input)
    {
        return operation_(input);
    }
}

internal static class StartupStepExecutor
{
    public static async Task<TResult> ExecuteAsync<TResult>(
        Func<Task<TResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex)
        {
            WriteStartupException(ex);
            throw;
        }
    }

    public static async Task ExecuteAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            WriteStartupException(ex);
            throw;
        }
    }

    private static void WriteStartupException(Exception ex)
    {
        Console.Error.WriteLine("Exception occurred during app startup:");
        Console.Error.WriteLine(ex.Message);

        var inner = ex.InnerException;
        var innerCount = 1;

        while (inner is not null)
        {
            Console.Error.WriteLine($"{new string('\t', innerCount)}Inner Exception:");
            Console.Error.WriteLine(inner.Message);

            inner = inner.InnerException;
            innerCount++;
        }
    }
}

public readonly struct Unit
{
    public static readonly Unit Value = new();
}