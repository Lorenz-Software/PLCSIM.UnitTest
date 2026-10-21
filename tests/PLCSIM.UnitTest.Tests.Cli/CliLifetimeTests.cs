using Microsoft.Extensions.DependencyInjection;
using PLCSIM.UnitTest.App.Cli;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Cli;

public sealed class CliLifetimeTests
{
    [Fact]
    public void Cancellation_controller_uses_first_request_for_cooperative_cancellation()
    {
        using var controller = new CliCancellationController();

        var cancelFirstKeyPress = controller.RequestCancellation();
        var cancelSecondKeyPress = controller.RequestCancellation();

        Assert.True(cancelFirstKeyPress);
        Assert.True(controller.Token.IsCancellationRequested);
        Assert.False(cancelSecondKeyPress);
    }

    [Fact]
    public async Task Service_lifetime_cleans_up_after_cancelled_operation_before_rethrowing()
    {
        var console = new RecordingConsole();
        var disposable = new RecordingAsyncDisposable();
        var lifetime = new CliServiceLifetime(
            console,
            _ => CreateServices(disposable));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lifetime.ExecuteAsync(
                CliApplicationOptions.CreateDefault(),
                (services, cancellationToken) =>
                {
                    _ = services.GetRequiredService<IAsyncDisposable>();
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult((int)CliExitCode.Success);
                },
                new CancellationToken(canceled: true)));

        Assert.True(disposable.WasDisposed);
        Assert.Empty(console.Errors);
    }

    [Fact]
    public async Task Service_lifetime_maps_cleanup_failure_to_cleanup_exit_code()
    {
        var console = new RecordingConsole();
        var lifetime = new CliServiceLifetime(
            console,
            _ => CreateServices(new FailingAsyncDisposable()));

        var exitCode = await lifetime.ExecuteAsync(
            CliApplicationOptions.CreateDefault(),
            (services, cancellationToken) =>
            {
                _ = services.GetRequiredService<IAsyncDisposable>();
                return Task.FromResult((int)CliExitCode.Success);
            },
            CancellationToken.None);

        Assert.Equal((int)CliExitCode.CleanupFailed, exitCode);
        Assert.Contains(console.Errors, line =>
            line.StartsWith("Service cleanup failed:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Service_lifetime_bounds_cleanup_independently_from_operation_token()
    {
        var console = new RecordingConsole();
        var lifetime = new CliServiceLifetime(
            console,
            _ => CreateServices(new BlockingAsyncDisposable()),
            cleanupTimeout: TimeSpan.FromMilliseconds(25));

        var exitCode = await lifetime.ExecuteAsync(
            CliApplicationOptions.CreateDefault(),
            (services, cancellationToken) =>
            {
                _ = services.GetRequiredService<IAsyncDisposable>();
                return Task.FromResult((int)CliExitCode.Success);
            },
            new CancellationToken(canceled: true));

        Assert.Equal((int)CliExitCode.CleanupFailed, exitCode);
        Assert.Contains(console.Errors, line =>
            line.Contains("did not complete", StringComparison.Ordinal));
    }

    private static ServiceProvider CreateServices(IAsyncDisposable disposable)
    {
        return new ServiceCollection()
            .AddSingleton<IAsyncDisposable>(_ => disposable)
            .BuildServiceProvider();
    }

    private sealed class RecordingConsole : ICliConsole
    {
        public List<string> Errors { get; } = [];

        public void WriteOutput(string message)
        {
        }

        public void WriteError(string message)
        {
            Errors.Add(message);
        }
    }

    private sealed class RecordingAsyncDisposable : IAsyncDisposable
    {
        public bool WasDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            return ValueTask.FromException(new InvalidOperationException("cleanup failure"));
        }
    }

    private sealed class BlockingAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan));
        }
    }
}
