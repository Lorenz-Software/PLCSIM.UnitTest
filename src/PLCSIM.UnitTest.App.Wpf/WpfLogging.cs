using System.IO;
using PLCSIM.UnitTest.App.Wpf.Services;
using Serilog;
using Serilog.Events;

namespace PLCSIM.UnitTest.App.Wpf;

internal static class WpfLogging
{
    public static ILogger CreateLogger(WpfApplicationSettings settings)
    {
        Directory.CreateDirectory(settings.ResolvedLogDirectory);

        return new LoggerConfiguration()
            .MinimumLevel.Is(MapLevel(settings.ApplicationLogLevel))
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                settings.LogFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: settings.LogRetentionDays,
                shared: true)
            .CreateLogger();
    }

    internal static LogEventLevel MapLevel(Microsoft.Extensions.Logging.LogLevel level)
    {
        return level switch
        {
            Microsoft.Extensions.Logging.LogLevel.Debug => LogEventLevel.Debug,
            Microsoft.Extensions.Logging.LogLevel.Warning => LogEventLevel.Warning,
            Microsoft.Extensions.Logging.LogLevel.Error => LogEventLevel.Error,
            Microsoft.Extensions.Logging.LogLevel.Critical => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
    }
}
