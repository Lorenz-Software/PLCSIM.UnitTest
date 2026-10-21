using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using PLCSIM.UnitTest.Common.Configuration;

namespace PLCSIM.UnitTest.App.Cli;

public sealed class CliServiceLifetime
{
    public static TimeSpan DefaultCleanupTimeout { get; } =
        RunConfigurationDefaults.DefaultCleanupTimeout;

    private readonly ICliConsole console;
    private readonly Func<CliApplicationOptions, ServiceProvider> createServices;
    private readonly TimeSpan cleanupTimeout;
    private readonly TimeProvider timeProvider;

    public CliServiceLifetime(
        ICliConsole console,
        Func<CliApplicationOptions, ServiceProvider> createServices,
        TimeSpan? cleanupTimeout = null,
        TimeProvider? timeProvider = null)
    {
        this.console = console ?? throw new ArgumentNullException(nameof(console));
        this.createServices = createServices
            ?? throw new ArgumentNullException(nameof(createServices));
        this.cleanupTimeout = cleanupTimeout ?? DefaultCleanupTimeout;
        this.timeProvider = timeProvider ?? TimeProvider.System;

        if (this.cleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cleanupTimeout),
                this.cleanupTimeout,
                "Cleanup timeout must be greater than zero.");
        }
    }

    public async Task<int> ExecuteAsync(
        CliApplicationOptions options,
        Func<IServiceProvider, CancellationToken, Task<int>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(operation);

        var services = createServices(options);
        ExceptionDispatchInfo? operationFailure = null;
        var result = (int)CliExitCode.UnexpectedError;

        try
        {
            result = await operation(services, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            operationFailure = ExceptionDispatchInfo.Capture(ex);
        }

        var cleanupResult = await DisposeAsync(services).ConfigureAwait(false);
        if (cleanupResult != CliExitCode.Success)
        {
            return (int)cleanupResult;
        }

        operationFailure?.Throw();
        return result;
    }

    private async Task<CliExitCode> DisposeAsync(ServiceProvider services)
    {
        var disposal = services.DisposeAsync().AsTask();

        try
        {
            await disposal
                .WaitAsync(cleanupTimeout, timeProvider, CancellationToken.None)
                .ConfigureAwait(false);
            return CliExitCode.Success;
        }
        catch (TimeoutException)
        {
            ObserveLateFailure(disposal);
            console.WriteError(
                $"Service cleanup did not complete within {cleanupTimeout}.");
            return CliExitCode.CleanupFailed;
        }
        catch (Exception ex)
        {
            console.WriteError($"Service cleanup failed: {ex.Message}");
            return CliExitCode.CleanupFailed;
        }
    }

    private static void ObserveLateFailure(Task disposal)
    {
        _ = disposal.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
