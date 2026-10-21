using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.App.Wpf.Services;
using PLCSIM.UnitTest.Common.Configuration;
using System.Globalization;
using System.IO;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

public sealed class SettingsDialogViewModel : ObservableObject
{
    private readonly ApplicationOperationTimeoutPolicy operationTimeouts;
    private string projectPrepareTimeoutSeconds = "";
    private string buildTransferTimeoutSeconds = "";
    private string runtimePrepareTimeoutSeconds = "";
    private string plcCommandTimeoutSeconds = "";
    private string plcStartReactionTimeoutSeconds = "";
    private string plcStopReactionTimeoutSeconds = "";
    private string unitTestCommandTimeoutSeconds = "";
    private string unitTestCommandReactionTimeoutSeconds = "";
    private string testRunTimeoutSeconds = "";
    private string finalEventTimeoutSeconds = "";
    private string communicationPrepareTimeoutSeconds = "";
    private string communicationRevalidationTimeoutSeconds = "";
    private string outputDirectory = "";
    private string testResultFileName = "";
    private string logDirectory = "";
    private string logRetentionDays = "";
    private bool buildAndDownloadProjectByDefault;
    private LogLevel applicationLogLevel;
    private LogLevel accessProfileLogLevel;
    private bool rawEventLoggingEnabled;

    public SettingsDialogViewModel(WpfApplicationSettings settings)
    {
        operationTimeouts = settings.OperationTimeouts;
        ProjectPrepareTimeoutSeconds = FormatSeconds(settings.RunTimeouts.ProjectPrepare);
        BuildTransferTimeoutSeconds = FormatSeconds(settings.RunTimeouts.BuildTransfer);
        RuntimePrepareTimeoutSeconds = FormatSeconds(settings.RunTimeouts.RuntimePrepare);
        PlcCommandTimeoutSeconds = FormatSeconds(settings.RunTimeouts.PlcCommand);
        PlcStartReactionTimeoutSeconds = FormatSeconds(settings.RunTimeouts.PlcStartReaction);
        PlcStopReactionTimeoutSeconds = FormatSeconds(settings.RunTimeouts.PlcStopReaction);
        UnitTestCommandTimeoutSeconds = FormatSeconds(settings.RunTimeouts.UnitTestCommand);
        UnitTestCommandReactionTimeoutSeconds = FormatSeconds(settings.RunTimeouts.UnitTestCommandReaction);
        TestRunTimeoutSeconds = FormatSeconds(settings.RunTimeouts.TestRun);
        FinalEventTimeoutSeconds = FormatSeconds(settings.RunTimeouts.FinalEvent);
        CommunicationPrepareTimeoutSeconds = FormatSeconds(settings.RunTimeouts.CommunicationPrepare);
        CommunicationRevalidationTimeoutSeconds = FormatSeconds(settings.RunTimeouts.CommunicationRevalidation);
        OutputDirectory = NormalizeDirectoryForDisplay(settings.OutputDirectory);
        TestResultFileName = settings.TestResultFileName;
        LogDirectory = NormalizeDirectoryForDisplay(settings.LogDirectory);
        LogRetentionDays = settings.LogRetentionDays.ToString(WpfApplicationDefaults.Culture);
        BuildAndDownloadProjectByDefault = settings.BuildAndDownloadProjectByDefault;
        ApplicationLogLevel = settings.ApplicationLogLevel;
        AccessProfileLogLevel = settings.AccessProfileLogLevel;
        RawEventLoggingEnabled = settings.RawEventLoggingEnabled;
    }

    public string ProjectPrepareTimeoutSeconds
    {
        get => projectPrepareTimeoutSeconds;
        set => SetProperty(ref projectPrepareTimeoutSeconds, value);
    }

    public string BuildTransferTimeoutSeconds
    {
        get => buildTransferTimeoutSeconds;
        set => SetProperty(ref buildTransferTimeoutSeconds, value);
    }

    public string RuntimePrepareTimeoutSeconds
    {
        get => runtimePrepareTimeoutSeconds;
        set => SetProperty(ref runtimePrepareTimeoutSeconds, value);
    }

    public string PlcCommandTimeoutSeconds
    {
        get => plcCommandTimeoutSeconds;
        set => SetProperty(ref plcCommandTimeoutSeconds, value);
    }

    public string PlcStartReactionTimeoutSeconds
    {
        get => plcStartReactionTimeoutSeconds;
        set => SetProperty(ref plcStartReactionTimeoutSeconds, value);
    }

    public string PlcStopReactionTimeoutSeconds
    {
        get => plcStopReactionTimeoutSeconds;
        set => SetProperty(ref plcStopReactionTimeoutSeconds, value);
    }

    public string UnitTestCommandTimeoutSeconds
    {
        get => unitTestCommandTimeoutSeconds;
        set => SetProperty(ref unitTestCommandTimeoutSeconds, value);
    }

    public string UnitTestCommandReactionTimeoutSeconds
    {
        get => unitTestCommandReactionTimeoutSeconds;
        set => SetProperty(ref unitTestCommandReactionTimeoutSeconds, value);
    }

    public string TestRunTimeoutSeconds
    {
        get => testRunTimeoutSeconds;
        set => SetProperty(ref testRunTimeoutSeconds, value);
    }

    public string FinalEventTimeoutSeconds
    {
        get => finalEventTimeoutSeconds;
        set => SetProperty(ref finalEventTimeoutSeconds, value);
    }

    public string CommunicationPrepareTimeoutSeconds
    {
        get => communicationPrepareTimeoutSeconds;
        set => SetProperty(ref communicationPrepareTimeoutSeconds, value);
    }

    public string CommunicationRevalidationTimeoutSeconds
    {
        get => communicationRevalidationTimeoutSeconds;
        set => SetProperty(ref communicationRevalidationTimeoutSeconds, value);
    }

    public string OutputDirectory
    {
        get => outputDirectory;
        set
        {
            if (SetProperty(ref outputDirectory, value))
            {
                OnPropertyChanged(nameof(ResolvedOutputDirectory));
            }
        }
    }

    public string ResolvedOutputDirectory => WpfApplicationSettings.ResolvePath(OutputDirectory);

    public string TestResultFileName
    {
        get => testResultFileName;
        set => SetProperty(ref testResultFileName, value);
    }

    public string LogDirectory
    {
        get => logDirectory;
        set
        {
            if (SetProperty(ref logDirectory, value))
            {
                OnPropertyChanged(nameof(ResolvedLogDirectory));
            }
        }
    }

    public string ResolvedLogDirectory => WpfApplicationSettings.ResolvePath(LogDirectory);

    public string LogRetentionDays
    {
        get => logRetentionDays;
        set => SetProperty(ref logRetentionDays, value);
    }

    public bool BuildAndDownloadProjectByDefault
    {
        get => buildAndDownloadProjectByDefault;
        set => SetProperty(ref buildAndDownloadProjectByDefault, value);
    }

    public IReadOnlyList<LogLevel> AvailableLogLevels => WpfApplicationSettings.SupportedLogLevels;

    public LogLevel ApplicationLogLevel
    {
        get => applicationLogLevel;
        set => SetProperty(ref applicationLogLevel, value);
    }

    public LogLevel AccessProfileLogLevel
    {
        get => accessProfileLogLevel;
        set
        {
            if (!SetProperty(ref accessProfileLogLevel, value))
            {
                return;
            }

            if (!CanEnableRawEventLogging)
            {
                RawEventLoggingEnabled = false;
            }

            OnPropertyChanged(nameof(CanEnableRawEventLogging));
        }
    }

    public bool CanEnableRawEventLogging => AccessProfileLogLevel == LogLevel.Debug;

    public bool RawEventLoggingEnabled
    {
        get => rawEventLoggingEnabled;
        set => SetProperty(ref rawEventLoggingEnabled, CanEnableRawEventLogging && value);
    }

    public bool TryCreateSettings(out WpfApplicationSettings settings, out string error)
    {
        settings = WpfApplicationSettings.Default;
        error = "";

        if (!TryParsePositive(ProjectPrepareTimeoutSeconds, "Project prepare timeout", out var projectPrepareTimeout, out error)
            || !TryParsePositive(BuildTransferTimeoutSeconds, "Build and transfer timeout", out var buildTransferTimeout, out error)
            || !TryParsePositive(RuntimePrepareTimeoutSeconds, "Runtime prepare timeout", out var runtimePrepareTimeout, out error)
            || !TryParsePositive(PlcCommandTimeoutSeconds, "PLC command timeout", out var plcCommandTimeout, out error)
            || !TryParsePositive(PlcStartReactionTimeoutSeconds, "PLC start reaction timeout", out var plcStartReactionTimeout, out error)
            || !TryParsePositive(PlcStopReactionTimeoutSeconds, "PLC stop reaction timeout", out var plcStopReactionTimeout, out error)
            || !TryParsePositive(UnitTestCommandTimeoutSeconds, "UnitTest command timeout", out var unitTestCommandTimeout, out error)
            || !TryParsePositive(UnitTestCommandReactionTimeoutSeconds, "UnitTest command reaction timeout", out var unitTestCommandReactionTimeout, out error)
            || !TryParsePositive(TestRunTimeoutSeconds, "Test run timeout", out var testRunTimeout, out error)
            || !TryParsePositive(FinalEventTimeoutSeconds, "Final event timeout", out var finalEventTimeout, out error)
            || !TryParsePositive(CommunicationPrepareTimeoutSeconds, "Communication preparation timeout", out var communicationPrepareTimeout, out error)
            || !TryParseNonNegative(CommunicationRevalidationTimeoutSeconds, "Communication revalidation timeout", out var communicationRevalidationTimeout, out error)
            || !TryParsePositive(LogRetentionDays, "Retention days", out var logRetentionDays, out error))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            error = "Output directory must not be empty.";
            return false;
        }

        var resolvedOutputDirectory = WpfApplicationSettings.ResolvePath(OutputDirectory);
        if (!Directory.Exists(resolvedOutputDirectory))
        {
            error = "Output directory must exist.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(TestResultFileName))
        {
            error = "Test result file name must not be empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(LogDirectory))
        {
            error = "Log directory must not be empty.";
            return false;
        }

        var resolvedLogDirectory = WpfApplicationSettings.ResolvePath(LogDirectory);
        if (!Directory.Exists(resolvedLogDirectory))
        {
            error = "Log directory must exist.";
            return false;
        }

        settings = new WpfApplicationSettings(
            new RunOperationTimeoutPolicy(
                TimeSpan.FromSeconds(projectPrepareTimeout),
                TimeSpan.FromSeconds(buildTransferTimeout),
                TimeSpan.FromSeconds(runtimePrepareTimeout),
                TimeSpan.FromSeconds(plcCommandTimeout),
                TimeSpan.FromSeconds(plcStartReactionTimeout),
                TimeSpan.FromSeconds(plcStopReactionTimeout),
                TimeSpan.FromSeconds(communicationPrepareTimeout),
                TimeSpan.FromSeconds(communicationRevalidationTimeout),
                TimeSpan.FromSeconds(unitTestCommandTimeout),
                TimeSpan.FromSeconds(unitTestCommandReactionTimeout),
                TimeSpan.FromSeconds(testRunTimeout),
                TimeSpan.FromSeconds(finalEventTimeout)),
            operationTimeouts,
            resolvedOutputDirectory,
            TestResultFileName.Trim(),
            resolvedLogDirectory,
            logRetentionDays,
            BuildAndDownloadProjectByDefault,
            ApplicationLogLevel,
            AccessProfileLogLevel,
            RawEventLoggingEnabled);
        return true;
    }

    private static string FormatSeconds(TimeSpan value)
    {
        return Convert.ToInt32(value.TotalSeconds).ToString(WpfApplicationDefaults.Culture);
    }

    private static bool TryParsePositive(string value, string fieldName, out int result, out string error)
    {
        if (!int.TryParse(value, NumberStyles.Integer, WpfApplicationDefaults.Culture, out result) || result <= 0)
        {
            error = $"{fieldName} must be a positive whole number of seconds.";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryParseNonNegative(string value, string fieldName, out int result, out string error)
    {
        if (!int.TryParse(value, NumberStyles.Integer, WpfApplicationDefaults.Culture, out result) || result < 0)
        {
            error = $"{fieldName} must be a non-negative whole number of seconds.";
            return false;
        }

        error = "";
        return true;
    }

    private static string NormalizeDirectoryForDisplay(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || Path.IsPathRooted(trimmed) || trimmed.StartsWith(@".\", StringComparison.Ordinal))
        {
            return trimmed;
        }

        return @".\" + trimmed;
    }
}
