using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MicrosoftLogger = Microsoft.Extensions.Logging.ILogger;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19.Worker
{
    internal sealed class WorkerLogging : IDisposable
    {
        private const string WorkerKind = "tia-openness";
        private const string BindingVersion = "v19";
        private readonly ILoggerFactory? loggerFactory;

        private WorkerLogging(MicrosoftLogger logger, ILoggerFactory? loggerFactory)
        {
            Logger = logger;
            this.loggerFactory = loggerFactory;
        }

        public MicrosoftLogger Logger { get; }

        public static WorkerLogging Create(string[] args)
        {
            try
            {
                var options = WorkerLoggingOptions.Parse(args);
                if (options.MinimumLevel == LogLevel.None)
                {
                    return new WorkerLogging(NullLogger.Instance, null);
                }

                Directory.CreateDirectory(options.LogDirectory);
                var logFilePath = Path.Combine(
                    options.LogDirectory,
                    "access-profile-"
                    + SanitizeFileName(options.AccessProfileId)
                    + "-tia-openness-v19-worker-.log");
                var serilogLogger = new LoggerConfiguration()
                    .MinimumLevel.Is(MapLevel(options.MinimumLevel))
                    .Enrich.WithProperty("AccessProfileId", options.AccessProfileId)
                    .Enrich.WithProperty("WorkerKind", WorkerKind)
                    .Enrich.WithProperty("BindingVersion", BindingVersion)
                    .Enrich.WithProperty("WorkerInstanceId", options.WorkerInstanceId)
                    .Enrich.WithProperty("ProcessId", Process.GetCurrentProcess().Id)
                    .WriteTo.Async(sink => sink.File(
                        logFilePath,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] ({AccessProfileId}/{WorkerKind}/{BindingVersion}/{WorkerInstanceId}/PID:{ProcessId}) {SourceContext}: {Message:lj}{NewLine}{Exception}",
                        rollingInterval: RollingInterval.Day,
                        fileSizeLimitBytes: options.FileSizeLimitBytes,
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: options.RetainedFileCount,
                        shared: true))
                    .CreateLogger();
                var loggerFactory = new SerilogLoggerFactory(serilogLogger, dispose: true);
                return new WorkerLogging(
                    loggerFactory.CreateLogger("TiaOpenness.V19.Worker"),
                    loggerFactory);
            }
            catch (Exception ex)
            {
                WriteFallback("Operational logger initialization failed: " + ex.GetBaseException().Message);
                return new WorkerLogging(NullLogger.Instance, null);
            }
        }

        public static void WriteFallback(string message)
        {
            try
            {
                Console.Error.WriteLine(message ?? string.Empty);
            }
            catch
            {
                // stderr is best-effort crash evidence.
            }
        }

        public void Dispose()
        {
            try
            {
                loggerFactory?.Dispose();
            }
            catch (Exception ex)
            {
                WriteFallback("Operational logger shutdown failed: " + ex.GetBaseException().Message);
            }
        }

        private static LogEventLevel MapLevel(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Trace:
                    return LogEventLevel.Verbose;
                case LogLevel.Debug:
                    return LogEventLevel.Debug;
                case LogLevel.Information:
                    return LogEventLevel.Information;
                case LogLevel.Warning:
                    return LogEventLevel.Warning;
                case LogLevel.Error:
                    return LogEventLevel.Error;
                case LogLevel.Critical:
                    return LogEventLevel.Fatal;
                default:
                    return LogEventLevel.Information;
            }
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(value
                .Select(character => invalid.Contains(character) ? '_' : character)
                .ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
        }
    }

    internal sealed class WorkerLoggingOptions
    {
        private WorkerLoggingOptions(
            string accessProfileId,
            string workerInstanceId,
            string logDirectory,
            LogLevel minimumLevel,
            long fileSizeLimitBytes,
            int retainedFileCount)
        {
            AccessProfileId = accessProfileId;
            WorkerInstanceId = workerInstanceId;
            LogDirectory = logDirectory;
            MinimumLevel = minimumLevel;
            FileSizeLimitBytes = fileSizeLimitBytes;
            RetainedFileCount = retainedFileCount;
        }

        public string AccessProfileId { get; }

        public string WorkerInstanceId { get; }

        public string LogDirectory { get; }

        public LogLevel MinimumLevel { get; }

        public long FileSizeLimitBytes { get; }

        public int RetainedFileCount { get; }

        public static WorkerLoggingOptions Parse(string[] args)
        {
            var accessProfileId = ReadRequiredArgument(args, "--access-profile-id");
            var workerInstanceId = ReadRequiredArgument(args, "--worker-instance-id");
            var logDirectory = Path.GetFullPath(ReadRequiredArgument(args, "--log-directory"));
            var levelValue = ReadRequiredArgument(args, "--log-level");
            if (!Enum.TryParse(levelValue, true, out LogLevel minimumLevel))
            {
                throw new ArgumentException("Worker log level is invalid.");
            }

            var fileSizeValue = ReadRequiredArgument(args, "--log-file-size-limit-bytes");
            if (!long.TryParse(fileSizeValue, NumberStyles.None, CultureInfo.InvariantCulture, out var fileSizeLimitBytes)
                || fileSizeLimitBytes <= 0)
            {
                throw new ArgumentException("Worker log file size limit is invalid.");
            }

            var retainedValue = ReadRequiredArgument(args, "--log-retained-file-count");
            if (!int.TryParse(retainedValue, NumberStyles.None, CultureInfo.InvariantCulture, out var retainedFileCount)
                || retainedFileCount <= 0)
            {
                throw new ArgumentException("Worker retained log file count is invalid.");
            }

            return new WorkerLoggingOptions(
                accessProfileId,
                workerInstanceId,
                logDirectory,
                minimumLevel,
                fileSizeLimitBytes,
                retainedFileCount);
        }

        private static string ReadRequiredArgument(string[] args, string name)
        {
            var index = Array.FindIndex(
                args,
                item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
            var value = index >= 0 && index + 1 < args.Length ? args[index + 1] : string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(name + " is required.");
            }

            return value;
        }
    }
}
