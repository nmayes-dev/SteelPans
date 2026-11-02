using Microsoft.AspNetCore.Builder;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SteelPans.Shared;

public static class StartupPipeline
{
    public static StartupPipeline<string[], TResult> AddStep<TResult>(Func<string[], Task<TResult>> buildOperation)
    {
        return new StartupPipeline<string[], TResult>(buildOperation);
    }
    public static StartupPipeline<string[], TResult> AddStep<TResult>(Func<string[], TResult> buildOperation)
    {
        return new StartupPipeline<string[], TResult>(args => Task.FromResult(buildOperation.Invoke(args)));
    }

    public static StartupPipeline<WebApplicationBuilder, TResult> AddStep<TResult>(Func<WebApplicationBuilder, Task<TResult>> launchOperation)
    {
        return new StartupPipeline<WebApplicationBuilder, TResult>(launchOperation);
    }
    public static StartupPipeline<WebApplicationBuilder, TResult> AddStep<TResult>(Func<WebApplicationBuilder, TResult> buildOperation)
    {
        return new StartupPipeline<WebApplicationBuilder, TResult>(args => Task.FromResult(buildOperation.Invoke(args)));
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
    public static Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> operation)
        => operation();

    public static Task ExecuteAsync(Func<Task> operation)
        => operation();
}

public readonly struct Unit
{
    public static readonly Unit Value = new();
}