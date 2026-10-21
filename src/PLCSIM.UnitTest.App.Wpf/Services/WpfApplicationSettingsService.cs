using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Core.Output;

namespace PLCSIM.UnitTest.App.Wpf.Services;

public interface IWpfApplicationSettingsService
{
    WpfApplicationSettings Current { get; }

    event EventHandler? SettingsChanged;

    void Save(WpfApplicationSettings settings);
}

public sealed record WpfApplicationSettings(
    RunOperationTimeoutPolicy RunTimeouts,
    ApplicationOperationTimeoutPolicy OperationTimeouts,
    string OutputDirectory,
    string TestResultFileName,
    string LogDirectory,
    int LogRetentionDays,
    bool BuildAndDownloadProjectByDefault,
    LogLevel ApplicationLogLevel,
    LogLevel AccessProfileLogLevel,
    bool RawEventLoggingEnabled)
{
    public static IReadOnlyList<LogLevel> SupportedLogLevels { get; } =
    [
        LogLevel.Debug,
        LogLevel.Information,
        LogLevel.Warning,
        LogLevel.Error,
        LogLevel.Critical
    ];

    public static WpfApplicationSettings Default { get; } = new(
        RunTimeouts: RunConfigurationDefaults.CreateDefaultTimeoutPolicy() with
        {
            CommunicationRevalidation = TimeSpan.Zero
        },
        OperationTimeouts: RunConfigurationDefaults.CreateDefaultApplicationOperationTimeoutPolicy(),
        OutputDirectory: WpfApplicationDefaults.DefaultArtifactOutputDirectory,
        TestResultFileName: "junit.xml",
        LogDirectory: WpfApplicationDefaults.DefaultLogDirectory,
        LogRetentionDays: 5,
        BuildAndDownloadProjectByDefault: false,
        ApplicationLogLevel: LogLevel.Information,
        AccessProfileLogLevel: LogLevel.Information,
        RawEventLoggingEnabled: false);

    public RunOperationTimeoutPolicy ToTimeoutPolicy() => RunTimeouts;

    public ArtifactExportOptions ToArtifactExportOptions()
    {
        return new ArtifactExportOptions(
            ResolvedOutputDirectory,
            TestResult: true,
            TestResultFileName);
    }

    public string ResolvedOutputDirectory => ResolvePath(OutputDirectory);

    public string ResolvedLogDirectory => ResolvePath(LogDirectory);

    public string LogFilePath => Path.Combine(ResolvedLogDirectory, "wpf-.log");

    public static string ResolvePath(string value)
    {
        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? ""
            : Path.GetFullPath(trimmed);
    }
}

public sealed class WpfApplicationSettingsService : IWpfApplicationSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string path;

    private WpfApplicationSettingsService(string path, WpfApplicationSettings current)
    {
        this.path = path;
        Current = current;
    }

    public WpfApplicationSettings Current { get; private set; }

    public event EventHandler? SettingsChanged;

    public static WpfApplicationSettingsService CreateDefault()
    {
        var path = WpfApplicationDefaults.SettingsFilePath;
        return new WpfApplicationSettingsService(path, Load(path));
    }

    internal static WpfApplicationSettingsService Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new WpfApplicationSettingsService(path, Load(path));
    }

    public void Save(WpfApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(normalized, SerializerOptions));
        Current = normalized;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static WpfApplicationSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return WpfApplicationSettings.Default;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<WpfApplicationSettings>(File.ReadAllText(path), SerializerOptions);
            return Normalize(MigrateLegacyDefaults(settings ?? WpfApplicationSettings.Default));
        }
        catch (JsonException)
        {
            return WpfApplicationSettings.Default;
        }
        catch (IOException)
        {
            return WpfApplicationSettings.Default;
        }
    }

    private static WpfApplicationSettings Normalize(WpfApplicationSettings settings)
    {
        var applicationLogLevel = NormalizeLogLevel(settings.ApplicationLogLevel);
        var accessProfileLogLevel = NormalizeLogLevel(settings.AccessProfileLogLevel);
        return settings with
        {
            RunTimeouts = NormalizeRunTimeouts(
                settings.RunTimeouts ?? WpfApplicationSettings.Default.RunTimeouts),
            OperationTimeouts = NormalizeOperationTimeouts(
                settings.OperationTimeouts ?? WpfApplicationSettings.Default.OperationTimeouts),
            OutputDirectory = NormalizeText(settings.OutputDirectory, WpfApplicationSettings.Default.OutputDirectory),
            TestResultFileName = NormalizeText(settings.TestResultFileName, WpfApplicationSettings.Default.TestResultFileName),
            LogDirectory = NormalizeText(settings.LogDirectory, WpfApplicationSettings.Default.LogDirectory),
            LogRetentionDays = NormalizePositive(settings.LogRetentionDays, WpfApplicationSettings.Default.LogRetentionDays),
            ApplicationLogLevel = applicationLogLevel,
            AccessProfileLogLevel = accessProfileLogLevel,
            RawEventLoggingEnabled = accessProfileLogLevel == LogLevel.Debug && settings.RawEventLoggingEnabled
        };
    }

    private static WpfApplicationSettings MigrateLegacyDefaults(WpfApplicationSettings settings)
    {
        return string.Equals(
            settings.OutputDirectory?.Trim(),
            RunConfigurationDefaults.DefaultOutputDirectory,
            StringComparison.OrdinalIgnoreCase)
            ? settings with { OutputDirectory = WpfApplicationDefaults.DefaultArtifactOutputDirectory }
            : settings;
    }

    private static RunOperationTimeoutPolicy NormalizeRunTimeouts(RunOperationTimeoutPolicy timeouts)
    {
        var defaults = WpfApplicationSettings.Default.RunTimeouts;
        return timeouts with
        {
            ProjectPrepare = NormalizePositive(timeouts.ProjectPrepare, defaults.ProjectPrepare),
            BuildTransfer = NormalizePositive(timeouts.BuildTransfer, defaults.BuildTransfer),
            RuntimePrepare = NormalizePositive(timeouts.RuntimePrepare, defaults.RuntimePrepare),
            PlcCommand = NormalizePositive(timeouts.PlcCommand, defaults.PlcCommand),
            PlcStartReaction = NormalizePositive(timeouts.PlcStartReaction, defaults.PlcStartReaction),
            PlcStopReaction = NormalizePositive(timeouts.PlcStopReaction, defaults.PlcStopReaction),
            CommunicationPrepare = NormalizePositive(timeouts.CommunicationPrepare, defaults.CommunicationPrepare),
            CommunicationRevalidation = NormalizeNonNegative(
                timeouts.CommunicationRevalidation,
                defaults.CommunicationRevalidation),
            UnitTestCommand = NormalizePositive(timeouts.UnitTestCommand, defaults.UnitTestCommand),
            UnitTestCommandReaction = NormalizePositive(
                timeouts.UnitTestCommandReaction,
                defaults.UnitTestCommandReaction),
            TestRun = NormalizePositive(timeouts.TestRun, defaults.TestRun),
            FinalEvent = NormalizePositive(timeouts.FinalEvent, defaults.FinalEvent)
        };
    }

    private static ApplicationOperationTimeoutPolicy NormalizeOperationTimeouts(
        ApplicationOperationTimeoutPolicy timeouts)
    {
        var defaults = WpfApplicationSettings.Default.OperationTimeouts;
        return timeouts with
        {
            RegistrationRefresh = NormalizePositive(timeouts.RegistrationRefresh, defaults.RegistrationRefresh),
            ProfileLoad = NormalizePositive(timeouts.ProfileLoad, defaults.ProfileLoad),
            ProfileUnload = NormalizePositive(timeouts.ProfileUnload, defaults.ProfileUnload),
            ProjectDiscovery = NormalizePositive(timeouts.ProjectDiscovery, defaults.ProjectDiscovery),
            RuntimeDiscovery = NormalizePositive(timeouts.RuntimeDiscovery, defaults.RuntimeDiscovery),
            ProjectRelease = NormalizePositive(timeouts.ProjectRelease, defaults.ProjectRelease),
            RuntimeRelease = NormalizePositive(timeouts.RuntimeRelease, defaults.RuntimeRelease),
            Monitoring = NormalizePositive(timeouts.Monitoring, defaults.Monitoring),
            Cleanup = NormalizePositive(timeouts.Cleanup, defaults.Cleanup)
        };
    }

    private static LogLevel NormalizeLogLevel(LogLevel level)
    {
        return WpfApplicationSettings.SupportedLogLevels.Contains(level)
            ? level
            : LogLevel.Information;
    }

    private static int NormalizePositive(int value, int fallback)
    {
        return value > 0 ? value : fallback;
    }

    private static TimeSpan NormalizePositive(TimeSpan value, TimeSpan fallback)
    {
        return value > TimeSpan.Zero ? value : fallback;
    }

    private static TimeSpan NormalizeNonNegative(TimeSpan value, TimeSpan fallback)
    {
        return value >= TimeSpan.Zero ? value : fallback;
    }

    private static string NormalizeText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
