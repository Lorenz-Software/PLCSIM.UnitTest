using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.Common.Logging;

/// <summary>
/// Application-owned operational file-logging policy for profiles and external workers.
/// </summary>
public sealed record OperationalLoggingOptions
{
    public OperationalLoggingOptions(
        string logDirectory,
        LogLevel minimumLevel,
        long fileSizeLimitBytes,
        int retainedFileCount,
        bool rawEventLoggingEnabled = false)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new ArgumentException("A log directory is required.", nameof(logDirectory));
        }

        if (fileSizeLimitBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSizeLimitBytes));
        }

        if (retainedFileCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedFileCount));
        }

        LogDirectory = logDirectory;
        MinimumLevel = minimumLevel;
        FileSizeLimitBytes = fileSizeLimitBytes;
        RetainedFileCount = retainedFileCount;
        RawEventLoggingEnabled = minimumLevel == LogLevel.Debug && rawEventLoggingEnabled;
    }

    public string LogDirectory { get; }

    public LogLevel MinimumLevel { get; }

    public long FileSizeLimitBytes { get; }

    public int RetainedFileCount { get; }

    public bool RawEventLoggingEnabled { get; }
}
