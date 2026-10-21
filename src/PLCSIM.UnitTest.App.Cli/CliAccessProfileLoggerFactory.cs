using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Logging;
using Serilog;

namespace PLCSIM.UnitTest.App.Cli;

internal sealed class CliAccessProfileLoggerFactory : IDisposable
{
    private readonly OperationalLoggingOptions options;
    private readonly ConcurrentDictionary<string, ILoggerFactory> loggerFactories =
        new(StringComparer.OrdinalIgnoreCase);
    private bool isDisposed;

    public CliAccessProfileLoggerFactory(OperationalLoggingOptions options)
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
            .MinimumLevel.Is(CliLogging.MapLevel(options.MinimumLevel))
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("AccessProfileId", accessProfileId)
            .WriteTo.File(
                CreateAccessProfileLogPath(options.LogDirectory, accessProfileId),
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

    private static string CreateAccessProfileLogPath(string logDirectory, string accessProfileId)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var fileNamePart = new string(accessProfileId
            .Select(character => invalidCharacters.Contains(character) ? '_' : char.ToLowerInvariant(character))
            .ToArray());
        return Path.Combine(logDirectory, $"access-profile-{fileNamePart}-.log");
    }
}
