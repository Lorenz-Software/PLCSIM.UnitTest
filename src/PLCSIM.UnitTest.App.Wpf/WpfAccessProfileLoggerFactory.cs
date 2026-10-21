using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Logging;
using Serilog;

namespace PLCSIM.UnitTest.App.Wpf;

internal sealed class WpfAccessProfileLoggerFactory : IDisposable
{
    private readonly OperationalLoggingOptions options;
    private readonly ConcurrentDictionary<string, ILoggerFactory> loggerFactories =
        new(StringComparer.OrdinalIgnoreCase);
    private bool isDisposed;

    public WpfAccessProfileLoggerFactory(OperationalLoggingOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ILoggerFactory GetLoggerFactory(string accessProfileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessProfileId);
        ObjectDisposedException.ThrowIf(isDisposed, this);
        return loggerFactories.GetOrAdd(accessProfileId, CreateLoggerFactory);
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        foreach (var loggerFactory in loggerFactories.Values)
        {
            loggerFactory.Dispose();
        }

        loggerFactories.Clear();
    }

    private ILoggerFactory CreateLoggerFactory(string accessProfileId)
    {
        Directory.CreateDirectory(options.LogDirectory);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(WpfLogging.MapLevel(options.MinimumLevel))
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("AccessProfileId", accessProfileId)
            .WriteTo.File(
                WpfOperationalLogPaths.CreateAccessProfileLogPath(options.LogDirectory, accessProfileId),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: options.RetainedFileCount,
                shared: true)
            .CreateLogger();

        return LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(logger, dispose: true);
        });
    }
}
