using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.App.Wpf;
using PLCSIM.UnitTest.App.Wpf.Services;
using PLCSIM.UnitTest.App.Wpf.ViewModels;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.AccessProfiles;
using System.IO;
using System.Windows;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Wpf;

public sealed class WpfPresentationHelperTests
{
    [Fact]
    public void Settings_dialog_view_model_creates_settings_from_valid_fields()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.Wpf.SettingsValidation",
            Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(temporaryDirectory, "custom-artifacts");
        var logDirectory = Path.Combine(temporaryDirectory, "custom-logs");
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(logDirectory);

        try
        {
            var viewModel = new SettingsDialogViewModel(WpfApplicationSettings.Default)
            {
                ProjectPrepareTimeoutSeconds = "11",
                BuildTransferTimeoutSeconds = "22",
                RuntimePrepareTimeoutSeconds = "33",
                PlcCommandTimeoutSeconds = "44",
                PlcStartReactionTimeoutSeconds = "55",
                PlcStopReactionTimeoutSeconds = "66",
                UnitTestCommandTimeoutSeconds = "77",
                UnitTestCommandReactionTimeoutSeconds = "88",
                TestRunTimeoutSeconds = "99",
                FinalEventTimeoutSeconds = "111",
                CommunicationPrepareTimeoutSeconds = "122",
                CommunicationRevalidationTimeoutSeconds = "0",
                OutputDirectory = outputDirectory,
                TestResultFileName = "custom.xml",
                LogDirectory = logDirectory,
                LogRetentionDays = "6",
                BuildAndDownloadProjectByDefault = true,
                ApplicationLogLevel = LogLevel.Warning,
                AccessProfileLogLevel = LogLevel.Debug,
                RawEventLoggingEnabled = true
            };

            var created = viewModel.TryCreateSettings(out var settings, out var error);

            Assert.True(created, error);
            Assert.Equal(TimeSpan.FromSeconds(11), settings.RunTimeouts.ProjectPrepare);
            Assert.Equal(TimeSpan.FromSeconds(22), settings.RunTimeouts.BuildTransfer);
            Assert.Equal(TimeSpan.FromSeconds(33), settings.RunTimeouts.RuntimePrepare);
            Assert.Equal(TimeSpan.FromSeconds(44), settings.RunTimeouts.PlcCommand);
            Assert.Equal(TimeSpan.FromSeconds(55), settings.RunTimeouts.PlcStartReaction);
            Assert.Equal(TimeSpan.FromSeconds(66), settings.RunTimeouts.PlcStopReaction);
            Assert.Equal(TimeSpan.FromSeconds(77), settings.RunTimeouts.UnitTestCommand);
            Assert.Equal(TimeSpan.FromSeconds(88), settings.RunTimeouts.UnitTestCommandReaction);
            Assert.Equal(TimeSpan.FromSeconds(99), settings.RunTimeouts.TestRun);
            Assert.Equal(TimeSpan.FromSeconds(111), settings.RunTimeouts.FinalEvent);
            Assert.Equal(TimeSpan.FromSeconds(122), settings.RunTimeouts.CommunicationPrepare);
            Assert.Equal(TimeSpan.Zero, settings.RunTimeouts.CommunicationRevalidation);
            Assert.Equal(
                WpfApplicationSettings.Default.OperationTimeouts,
                settings.OperationTimeouts);
            Assert.Equal(outputDirectory, settings.OutputDirectory);
            Assert.Equal("custom.xml", settings.TestResultFileName);
            Assert.Equal(logDirectory, settings.LogDirectory);
            Assert.Equal(6, settings.LogRetentionDays);
            Assert.True(settings.BuildAndDownloadProjectByDefault);
            Assert.Equal(LogLevel.Warning, settings.ApplicationLogLevel);
            Assert.Equal(LogLevel.Debug, settings.AccessProfileLogLevel);
            Assert.True(settings.RawEventLoggingEnabled);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Wpf_access_profile_logger_uses_profile_file_and_configured_minimum_level()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.Wpf.ProfileLogging",
            Guid.NewGuid().ToString("N"));

        try
        {
            var options = new OperationalLoggingOptions(
                directory,
                LogLevel.Information,
                1_000_000,
                2);
            using var factory = new WpfAccessProfileLoggerFactory(options);
            var logger = factory.GetLoggerFactory("Siemens/Profile").CreateLogger("Profile.Test");

            logger.LogDebug("Debug entry must be filtered.");
            logger.LogInformation("Profile information entry.");
            factory.Dispose();

            var logFile = Assert.Single(Directory.GetFiles(directory, "access-profile-siemens_profile-*.log"));
            var text = File.ReadAllText(logFile);
            Assert.Contains("Profile information entry.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Debug entry must be filtered.", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Application_settings_default_to_external_project_handling()
    {
        Assert.False(WpfApplicationSettings.Default.BuildAndDownloadProjectByDefault);
    }

    [Fact]
    public void Application_settings_default_artifacts_to_the_per_user_data_directory()
    {
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PLCSIM.UnitTest",
                "artifacts"),
            WpfApplicationSettings.Default.OutputDirectory);
        Assert.True(Path.IsPathRooted(WpfApplicationSettings.Default.OutputDirectory));
    }

    [Fact]
    public void Application_settings_default_to_information_logging_without_raw_events()
    {
        Assert.Equal(LogLevel.Information, WpfApplicationSettings.Default.ApplicationLogLevel);
        Assert.Equal(LogLevel.Information, WpfApplicationSettings.Default.AccessProfileLogLevel);
        Assert.False(WpfApplicationSettings.Default.RawEventLoggingEnabled);
    }

    [Fact]
    public void Application_settings_default_to_bounded_prepare_and_unbounded_revalidation()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), WpfApplicationSettings.Default.RunTimeouts.CommunicationPrepare);
        Assert.Equal(TimeSpan.Zero, WpfApplicationSettings.Default.RunTimeouts.CommunicationRevalidation);
        Assert.Equal(
            new CommunicationTimeoutPolicy(TimeSpan.FromSeconds(30), TimeSpan.Zero),
            WpfApplicationSettings.Default.RunTimeouts.ToCommunicationTimeoutPolicy());
    }

    [Fact]
    public void Application_settings_persist_project_handling_default()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.Wpf.SettingsPersistence",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");

        try
        {
            var service = WpfApplicationSettingsService.Create(path);
            service.Save(WpfApplicationSettings.Default with
            {
                BuildAndDownloadProjectByDefault = true,
                ApplicationLogLevel = LogLevel.Error,
                AccessProfileLogLevel = LogLevel.Debug,
                RawEventLoggingEnabled = true,
                RunTimeouts = WpfApplicationSettings.Default.RunTimeouts with
                {
                    CommunicationPrepare = TimeSpan.FromSeconds(45),
                    CommunicationRevalidation = TimeSpan.FromSeconds(12)
                }
            });

            var reloaded = WpfApplicationSettingsService.Create(path);

            Assert.True(reloaded.Current.BuildAndDownloadProjectByDefault);
            Assert.Equal(LogLevel.Error, reloaded.Current.ApplicationLogLevel);
            Assert.Equal(LogLevel.Debug, reloaded.Current.AccessProfileLogLevel);
            Assert.True(reloaded.Current.RawEventLoggingEnabled);
            Assert.Equal(TimeSpan.FromSeconds(45), reloaded.Current.RunTimeouts.CommunicationPrepare);
            Assert.Equal(TimeSpan.FromSeconds(12), reloaded.Current.RunTimeouts.CommunicationRevalidation);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Application_settings_service_disables_raw_event_logging_outside_debug_level()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.Wpf.RawEventSettings",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");

        try
        {
            var service = WpfApplicationSettingsService.Create(path);
            service.Save(WpfApplicationSettings.Default with
            {
                AccessProfileLogLevel = LogLevel.Information,
                RawEventLoggingEnabled = true
            });

            Assert.False(service.Current.RawEventLoggingEnabled);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Application_settings_service_migrates_the_legacy_artifact_default()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.Wpf.LegacyArtifactDefault",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ \"OutputDirectory\": \"artifacts\" }");

            var service = WpfApplicationSettingsService.Create(path);

            Assert.Equal(WpfApplicationDefaults.DefaultArtifactOutputDirectory, service.Current.OutputDirectory);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Application_settings_service_preserves_a_custom_relative_artifact_directory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.Wpf.CustomArtifactDirectory",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ \"OutputDirectory\": \"custom-artifacts\" }");

            var service = WpfApplicationSettingsService.Create(path);

            Assert.Equal("custom-artifacts", service.Current.OutputDirectory);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Settings_dialog_disables_raw_event_logging_outside_debug_level()
    {
        var viewModel = new SettingsDialogViewModel(WpfApplicationSettings.Default)
        {
            AccessProfileLogLevel = LogLevel.Debug,
            RawEventLoggingEnabled = true
        };

        Assert.True(viewModel.CanEnableRawEventLogging);
        Assert.True(viewModel.RawEventLoggingEnabled);

        viewModel.AccessProfileLogLevel = LogLevel.Information;

        Assert.False(viewModel.CanEnableRawEventLogging);
        Assert.False(viewModel.RawEventLoggingEnabled);
    }

    [Fact]
    public void Incomplete_application_settings_use_current_defaults()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.Wpf.LegacySettings",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ \"LogDirectory\": \"logs\", \"LogRetentionDays\": 5 }");

            var service = WpfApplicationSettingsService.Create(path);

            Assert.Equal(LogLevel.Information, service.Current.ApplicationLogLevel);
            Assert.Equal(LogLevel.Information, service.Current.AccessProfileLogLevel);
            Assert.False(service.Current.RawEventLoggingEnabled);
            Assert.Equal(WpfApplicationSettings.Default.RunTimeouts, service.Current.RunTimeouts);
            Assert.Equal(WpfApplicationSettings.Default.OperationTimeouts, service.Current.OperationTimeouts);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Settings_dialog_view_model_rejects_invalid_timeout()
    {
        var viewModel = new SettingsDialogViewModel(WpfApplicationSettings.Default)
        {
            PlcCommandTimeoutSeconds = "0"
        };

        var created = viewModel.TryCreateSettings(out _, out var error);

        Assert.False(created);
        Assert.Contains("PLC command timeout", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_dialog_view_model_rejects_negative_communication_revalidation_timeout()
    {
        var viewModel = new SettingsDialogViewModel(WpfApplicationSettings.Default)
        {
            CommunicationRevalidationTimeoutSeconds = "-1"
        };

        var created = viewModel.TryCreateSettings(out _, out var error);

        Assert.False(created);
        Assert.Contains("Communication revalidation timeout", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_dialog_view_model_rejects_missing_output_directory()
    {
        var missingDirectory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.Wpf.MissingSettingsDirectory",
            Guid.NewGuid().ToString("N"));
        var viewModel = new SettingsDialogViewModel(WpfApplicationSettings.Default)
        {
            OutputDirectory = missingDirectory
        };

        var created = viewModel.TryCreateSettings(out _, out var error);

        Assert.False(created);
        Assert.Equal("Output directory must exist.", error);
    }

    [Fact]
    public void Settings_dialog_view_model_resolves_relative_directories_against_current_directory()
    {
        var previousDirectory = Environment.CurrentDirectory;
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.Wpf.RelativeSettingsDirectory",
            Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(temporaryDirectory, "artifacts");
        var logDirectory = Path.Combine(temporaryDirectory, "logs");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(logDirectory);
            Environment.CurrentDirectory = temporaryDirectory;
            var viewModel = new SettingsDialogViewModel(WpfApplicationSettings.Default)
            {
                OutputDirectory = "artifacts",
                LogDirectory = "logs"
            };

            var created = viewModel.TryCreateSettings(out var settings, out var error);

            Assert.True(created, error);
            Assert.Equal(outputDirectory, viewModel.ResolvedOutputDirectory);
            Assert.Equal(logDirectory, viewModel.ResolvedLogDirectory);
            Assert.Equal(outputDirectory, settings.OutputDirectory);
            Assert.Equal(logDirectory, settings.LogDirectory);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Settings_dialog_view_model_displays_the_absolute_per_user_artifact_default()
    {
        var viewModel = new SettingsDialogViewModel(WpfApplicationSettings.Default);

        Assert.Equal(WpfApplicationDefaults.DefaultArtifactOutputDirectory, viewModel.OutputDirectory);
    }

    [Fact]
    public void Wpf_profile_discovery_options_do_not_create_the_configured_directory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.Wpf.ProfileDiscovery",
            Guid.NewGuid().ToString("N"));

        var options = WpfServiceRegistration.CreateProfileDiscoveryOptions(directory);

        Assert.Equal(Path.GetFullPath(directory), options.ProfileDiscoveryDirectory);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Diagnostic_presentation_uses_highest_retained_severity()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new DiagnosticSnapshot(
            [
                DiagnosticEntry.Create(CreateDiagnostic(
                    "Wpf.Test.Information",
                    "Information",
                    LogLevel.Information,
                    null)),
                DiagnosticEntry.Create(CreateDiagnostic(
                    "Wpf.Test.Warning",
                    "Warning",
                    LogLevel.Warning,
                    null)),
                DiagnosticEntry.Create(CreateDiagnostic(
                    "Wpf.Test.Error",
                    "Error",
                    LogLevel.Error,
                    null))
            ],
            now);

        var presentation = DiagnosticPresentationMapper.Map(snapshot);

        Assert.Equal(DiagnosticStatusKind.Error, presentation.StatusKind);
        Assert.Equal("3", presentation.BadgeText);
    }

    [Theory]
    [InlineData(LogLevel.Trace, DiagnosticStatusKind.Information)]
    [InlineData(LogLevel.Information, DiagnosticStatusKind.Information)]
    [InlineData(LogLevel.Warning, DiagnosticStatusKind.Warning)]
    [InlineData(LogLevel.Error, DiagnosticStatusKind.Error)]
    [InlineData(LogLevel.Critical, DiagnosticStatusKind.Error)]
    public void Diagnostic_presentation_maps_retained_severity(
        LogLevel level,
        DiagnosticStatusKind expected)
    {
        var diagnostic = CreateDiagnostic(
            $"Wpf.Test.{level}",
            level.ToString(),
            level,
            null);
        var snapshot = new DiagnosticSnapshot(
            [DiagnosticEntry.Create(diagnostic)],
            diagnostic.Timestamp);

        var presentation = DiagnosticPresentationMapper.Map(snapshot);

        Assert.Equal(expected, presentation.StatusKind);
        Assert.Equal("1", presentation.BadgeText);
    }

    [Fact]
    public void Diagnostic_presentation_maps_empty_snapshot_to_neutral_state()
    {
        var presentation = DiagnosticPresentationMapper.Map(
            DiagnosticSnapshot.Empty(DateTimeOffset.UtcNow));

        Assert.Equal(DiagnosticStatusKind.Clear, presentation.StatusKind);
        Assert.Equal("0", presentation.BadgeText);
    }

    [Fact]
    public void Diagnostic_clipboard_formatter_includes_operational_evidence()
    {
        var diagnostic = DiagnosticViewModel.FromDiagnostic(CreateDiagnostic(
            "Wpf.Test.Copy",
            "Copy message",
            LogLevel.Warning,
            "profile-a"));

        var text = DiagnosticClipboardFormatter.Format(diagnostic);

        Assert.Contains($"Timestamp: {diagnostic.LastSeenDetail}", text, StringComparison.Ordinal);
        Assert.Contains("Severity: Warning", text, StringComparison.Ordinal);
        Assert.Contains("Code: Wpf.Test.Copy", text, StringComparison.Ordinal);
        Assert.Contains("Message: Copy message", text, StringComparison.Ordinal);
        Assert.Contains("Technical detail: -", text, StringComparison.Ordinal);
        Assert.Contains("Evidence: -", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostic_clipboard_formatter_separates_multiple_entries()
    {
        var first = DiagnosticViewModel.FromDiagnostic(CreateDiagnostic(
            "Wpf.Test.First",
            "First",
            LogLevel.Information,
            null));
        var second = DiagnosticViewModel.FromDiagnostic(CreateDiagnostic(
            "Wpf.Test.Second",
            "Second",
            LogLevel.Error,
            null));

        var text = DiagnosticClipboardFormatter.FormatAll([first, second]);

        Assert.Contains("Code: Wpf.Test.First", text, StringComparison.Ordinal);
        Assert.Contains("Code: Wpf.Test.Second", text, StringComparison.Ordinal);
        Assert.Contains(
            $"{Environment.NewLine}{Environment.NewLine}",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Access_profile_log_path_uses_logger_file_naming()
    {
        var path = WpfOperationalLogPaths.CreateAccessProfileLogPath(
            @"C:\Logs",
            "Siemens/Profile");

        Assert.Equal(
            Path.Combine(@"C:\Logs", "access-profile-siemens_profile-.log"),
            path);
    }

    [Fact]
    public void Draft_mapper_selects_profile_without_interpreting_profile_owned_values()
    {
        var profile = new AccessProfileSelectionViewModel(
            "fake",
            "1.0.0",
            "Fake profile",
            "Test profile",
            AccessProfileSourceKind.BuiltIn.ToString(),
            "builtin",
            AccessProfileRegistrationState.Registered,
            null);
        var config = new AccessProfileConfig(
            "fake",
            "1.0.0",
            new AccessProfileConfigurationPayload(
                """
                {
                  "plcRef": { "id": "plc-a" },
                  "runtimeRef": { "id": "runtime-a" }
                }
                """),
            "project.json");

        var state = RunConfigurationDraftPresentationMapper.Map(
            config,
            "Import Configuration",
            "ready",
            [profile],
            CreateDiagnostic);

        Assert.Same(config, state.Draft);
        Assert.Same(profile, state.SelectedAccessProfile);
        Assert.Contains("\"plc-a\"", state.Draft.Values.Json, StringComparison.Ordinal);
        Assert.Contains("\"runtime-a\"", state.Draft.Values.Json, StringComparison.Ordinal);
        Assert.Equal("project.json", state.ProjectSource);
        Assert.Equal("ready", state.Step1Status);
        Assert.Null(state.Diagnostic);
        Assert.Equal("Import Configuration: selected access profile 'Fake profile (fake 1.0.0)'.", state.OutputLine);
    }

    [Fact]
    public void Draft_mapper_reports_missing_profile_without_selecting_profile()
    {
        var config = new AccessProfileConfig(
            "missing",
            null,
            AccessProfileConfigurationPayload.Empty,
            "project.json");

        var state = RunConfigurationDraftPresentationMapper.Map(
            config,
            "Import Configuration",
            "ready",
            [],
            CreateDiagnostic);

        Assert.Null(state.SelectedAccessProfile);
        Assert.Equal("Configuration loaded, but the requested access profile is not available.", state.Step1Status);
        Assert.NotNull(state.Diagnostic);
        Assert.Null(state.OutputLine);
    }

    [Fact]
    public void Terminal_output_presenter_combines_manual_workflow_and_artifact_output()
    {
        var presenter = new TerminalOutputPresenter();

        presenter.AppendLine("Manual line");
        presenter.ApplyWorkflowOutput("Workflow line");
        presenter.ApplyArtifactOutput("Artifact line");

        Assert.True(presenter.HasText);
        Assert.Contains("Manual line", presenter.Text);
        Assert.Contains("Workflow line", presenter.Text);
        Assert.Contains("Artifact line", presenter.Text);

        presenter.AppendLine("Reconnect line");

        Assert.True(
            presenter.Text.IndexOf("Artifact line", StringComparison.Ordinal)
            < presenter.Text.IndexOf("Reconnect line", StringComparison.Ordinal));

        presenter.ApplyArtifactOutput("Artifact line");

        Assert.True(
            presenter.Text.IndexOf("Artifact line", StringComparison.Ordinal)
            < presenter.Text.IndexOf("Reconnect line", StringComparison.Ordinal));

        presenter.ClearArtifactOutput();

        Assert.Contains("Manual line", presenter.Text);
        Assert.Contains("Workflow line", presenter.Text);
        Assert.DoesNotContain("Artifact line", presenter.Text);
    }

    [Fact]
    public void Run_result_tree_presenter_keeps_suites_collapsed_by_default()
    {
        var presenter = new RunResultTreePresenter();
        var snapshot = CreateSnapshot("epoch-1");

        var initial = presenter.Refresh(snapshot, null);

        Assert.Equal("epoch-1", initial.DisplayedEpochId);
        Assert.Equal(UnitTestRunState.Completed, presenter.DisplayedState);
        Assert.Contains(initial.Rows, row => row.Kind == UnitTestNodeKind.RunControl.ToString());
        Assert.Contains(initial.Rows, row => row.Kind == UnitTestNodeKind.TestSuite.ToString());
        Assert.DoesNotContain(initial.Rows, row => row.Kind == UnitTestNodeKind.TestCase.ToString());
        Assert.Equal("Completed / Passed / Complete", initial.Status);
    }

    [Fact]
    public void Incomplete_running_epoch_marks_run_control_as_presentation_error_immediately()
    {
        var presenter = new RunResultTreePresenter();
        var completedTestCase = CreateNode(
            "case-1",
            "Case",
            UnitTestNodeKind.TestCase,
            []);
        var suite = CreateNode(
            "suite-1",
            "Suite",
            UnitTestNodeKind.TestSuite,
            [completedTestCase]);
        var root = CreateNode(
            "run-control",
            "RunControl",
            UnitTestNodeKind.RunControl,
            [suite],
            lifecycleState: UnitTestLifecycleState.Running);
        var snapshot = new UnitTestRunEpochSnapshot(
            new RunEpochId("epoch-incomplete"),
            UnitTestRunState.Running,
            UnitTestResultState.Passed,
            EvidenceState.Incomplete,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [],
            [],
            root,
            lostEventCount: 19);

        var state = presenter.Refresh(snapshot, null);
        var rootView = Assert.Single(state.Tree);
        var suiteView = Assert.Single(rootView.Children);
        var testCaseView = Assert.Single(suiteView.Children);

        Assert.Equal("Running / Error / Incomplete", state.Status);
        Assert.Equal("Error", rootView.PresentationResult);
        Assert.Equal("Passed", suiteView.PresentationResult);
        Assert.Equal("Passed", testCaseView.PresentationResult);
    }

    [Fact]
    public void Run_result_tree_presenter_expands_suite_on_toggle()
    {
        var presenter = new RunResultTreePresenter();
        var initial = presenter.Refresh(CreateSnapshot("epoch-1"), null);
        var suite = initial.Rows.Single(row => row.Kind == UnitTestNodeKind.TestSuite.ToString());

        var expanded = presenter.ToggleExpansion(suite, initial.Tree, suite.Id);

        Assert.Contains(expanded.Rows, row => row.Kind == UnitTestNodeKind.TestCase.ToString());
        Assert.Same(suite, expanded.SelectedNode);
    }

    [Fact]
    public void Run_result_tree_presenter_suppresses_displayed_epoch()
    {
        var presenter = new RunResultTreePresenter();
        var initial = presenter.Refresh(CreateSnapshot("epoch-1"), null);

        var suppressed = presenter.Suppress("epoch-1", initial.Tree, initial.SelectedNode?.Id);

        Assert.Empty(suppressed.Tree);
        Assert.Empty(suppressed.Rows);
        Assert.Null(suppressed.SelectedNode);
        Assert.Null(presenter.DisplayedEpochId);
        Assert.Null(presenter.DisplayedState);
    }

    [Fact]
    public void Missing_child_evidence_marks_only_the_child_and_escalates_error_to_parents()
    {
        var testCase = CreateNode(
            "case-1",
            "Case",
            UnitTestNodeKind.TestCase,
            [],
            UnitTestResultState.NotEvaluated,
            EvidenceState.Incomplete,
            missingResultEvidence: true);
        var suite = CreateNode(
            "suite-1",
            "Suite",
            UnitTestNodeKind.TestSuite,
            [testCase],
            UnitTestResultState.Passed,
            EvidenceState.Incomplete);
        var root = CreateNode(
            "run-control",
            "RunControl",
            UnitTestNodeKind.RunControl,
            [suite],
            UnitTestResultState.Passed,
            EvidenceState.Incomplete);

        var rootView = RunResultNodeViewModel.FromNode(root, isTerminalRun: true);
        var suiteView = Assert.Single(rootView.Children);
        var testCaseView = Assert.Single(suiteView.Children);

        Assert.Equal("Passed", rootView.Result);
        Assert.Equal("Error", rootView.PresentationResult);
        Assert.Same(System.Windows.Media.Brushes.Firebrick, rootView.StatusBrush);
        Assert.Equal(Visibility.Collapsed, rootView.EvidenceWarningVisibility);
        Assert.Equal("Passed", suiteView.Result);
        Assert.Equal("Error", suiteView.PresentationResult);
        Assert.Same(System.Windows.Media.Brushes.Firebrick, suiteView.StatusBrush);
        Assert.Equal(Visibility.Collapsed, suiteView.EvidenceWarningVisibility);
        Assert.Equal("NotEvaluated", testCaseView.Result);
        Assert.Equal("Error", testCaseView.PresentationResult);
        Assert.Same(System.Windows.Media.Brushes.Firebrick, testCaseView.StatusBrush);
        Assert.Equal(Visibility.Visible, testCaseView.EvidenceWarningVisibility);
        Assert.Contains("evidence is incomplete", testCaseView.StatusToolTip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Terminal_epoch_escalates_missing_child_evidence_to_parent()
    {
        var testCase = CreateNode(
            "case-1",
            "ConfigBounds",
            UnitTestNodeKind.TestCase,
            [],
            UnitTestResultState.NotEvaluated,
            EvidenceState.Incomplete,
            missingResultEvidence: true,
            lifecycleState: UnitTestLifecycleState.Running,
            missingCompletionEvidence: true);
        var suite = CreateNode(
            "suite-1",
            "TestSuite_TestCaseOrchestrators",
            UnitTestNodeKind.TestSuite,
            [testCase]);

        var suiteView = RunResultNodeViewModel.FromNode(suite, isTerminalRun: true);
        var testCaseView = Assert.Single(suiteView.Children);

        Assert.Equal("Error", suiteView.PresentationResult);
        Assert.Equal(Visibility.Collapsed, suiteView.EvidenceWarningVisibility);
        Assert.Equal("Error", testCaseView.PresentationResult);
        Assert.Equal(Visibility.Visible, testCaseView.EvidenceWarningVisibility);
    }

    [Fact]
    public void Parent_result_does_not_finalize_missing_child_evidence_while_epoch_is_active()
    {
        var testCase = CreateNode(
            "case-1",
            "ConfigBounds",
            UnitTestNodeKind.TestCase,
            [],
            UnitTestResultState.NotEvaluated,
            EvidenceState.Incomplete,
            missingResultEvidence: true,
            lifecycleState: UnitTestLifecycleState.Running,
            missingCompletionEvidence: true);
        var suite = CreateNode(
            "suite-1",
            "TestSuite_TestCaseOrchestrators",
            UnitTestNodeKind.TestSuite,
            [testCase]);

        var suiteView = RunResultNodeViewModel.FromNode(suite);
        var testCaseView = Assert.Single(suiteView.Children);

        Assert.Equal("Passed", suiteView.PresentationResult);
        Assert.Equal("NotEvaluated", testCaseView.PresentationResult);
        Assert.Equal(Visibility.Collapsed, testCaseView.EvidenceWarningVisibility);
    }

    [Fact]
    public void Monitoring_timeout_finalizes_missing_child_evidence_and_escalates_it()
    {
        var testCase = CreateNode(
            "case-1",
            "ConfigBounds",
            UnitTestNodeKind.TestCase,
            [],
            UnitTestResultState.NotEvaluated,
            EvidenceState.Incomplete,
            missingResultEvidence: true,
            lifecycleState: UnitTestLifecycleState.Running,
            missingCompletionEvidence: true);
        var suite = CreateNode(
            "suite-1",
            "TestSuite_TestCaseOrchestrators",
            UnitTestNodeKind.TestSuite,
            [testCase],
            evidenceState: EvidenceState.Incomplete);
        var root = CreateNode(
            "run-control",
            "RunControl",
            UnitTestNodeKind.RunControl,
            [suite],
            evidenceState: EvidenceState.Incomplete,
            lifecycleState: UnitTestLifecycleState.Running,
            missingCompletionEvidence: true);
        var snapshot = new UnitTestRunEpochSnapshot(
            new RunEpochId("epoch-timeout"),
            UnitTestRunState.TimedOut,
            UnitTestResultState.Passed,
            EvidenceState.Incomplete,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [],
            [],
            root,
            lostEventCount: 19);

        var state = new RunResultTreePresenter().Refresh(snapshot, null);
        var rootView = Assert.Single(state.Tree);
        var suiteView = Assert.Single(rootView.Children);
        var testCaseView = Assert.Single(suiteView.Children);

        Assert.Equal("TimedOut / Error / Incomplete", state.Status);
        Assert.Equal("Error", rootView.PresentationResult);
        Assert.Equal(Visibility.Visible, rootView.EvidenceWarningVisibility);
        Assert.Equal("Error", suiteView.PresentationResult);
        Assert.Equal(Visibility.Collapsed, suiteView.EvidenceWarningVisibility);
        Assert.Equal("Error", testCaseView.PresentationResult);
        Assert.Equal(Visibility.Visible, testCaseView.EvidenceWarningVisibility);
    }

    [Fact]
    public void Complete_passed_nodes_keep_success_presentation()
    {
        var node = RunResultNodeViewModel.FromNode(CreateNode(
            "run-control",
            "RunControl",
            UnitTestNodeKind.RunControl,
            [],
            UnitTestResultState.Passed,
            EvidenceState.Complete), isTerminalRun: true);

        Assert.Equal("Passed", node.Result);
        Assert.Equal("Passed", node.PresentationResult);
        Assert.Same(System.Windows.Media.Brushes.ForestGreen, node.StatusBrush);
        Assert.Equal(Visibility.Collapsed, node.EvidenceWarningVisibility);
        Assert.Contains("Presentation state: Passed", node.StatusToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void Running_not_evaluated_node_does_not_escalate_presentation_error()
    {
        var node = RunResultNodeViewModel.FromNode(CreateNode(
            "case-1",
            "Case",
            UnitTestNodeKind.TestCase,
            [],
            UnitTestResultState.NotEvaluated,
            EvidenceState.Partial,
            lifecycleState: UnitTestLifecycleState.Running));

        Assert.Equal("NotEvaluated", node.PresentationResult);
        Assert.Equal(Visibility.Collapsed, node.EvidenceWarningVisibility);
    }

    private static Diagnostic CreateDiagnostic(
        string code,
        string message,
        LogLevel level,
        string? evidence)
    {
        return new Diagnostic(
            code,
            DiagnosticCategory.Configuration,
            level,
            DiagnosticSource.WpfApplication,
            DiagnosticPhase.Configuration,
            message,
            new DiagnosticCorrelation(AccessProfileId: evidence),
            DateTimeOffset.UtcNow);
    }

    private static UnitTestRunEpochSnapshot CreateSnapshot(string epochId)
    {
        var testCase = CreateNode(
            "case-1",
            "Case",
            UnitTestNodeKind.TestCase,
            []);
        var suite = CreateNode(
            "suite-1",
            "Suite",
            UnitTestNodeKind.TestSuite,
            [testCase]);
        var root = CreateNode(
            "run-control",
            "RunControl",
            UnitTestNodeKind.RunControl,
            [suite]);

        return new UnitTestRunEpochSnapshot(
            new RunEpochId(epochId),
            UnitTestRunState.Completed,
            UnitTestResultState.Passed,
            EvidenceState.Complete,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [],
            [],
            root);
    }

    [Theory]
    [InlineData(UnitTestResultState.Failed, "Abort")]
    [InlineData(UnitTestResultState.Error, "Error")]
    public void Aborted_parent_preserves_child_result_in_presentation(UnitTestResultState childResult, string expected)
    {
        var child = CreateNode("case", "Case", UnitTestNodeKind.TestCase, [], childResult);
        var suite = CreateNode("suite", "Suite", UnitTestNodeKind.TestSuite, [child], UnitTestResultState.Abort);
        var view = RunResultNodeViewModel.FromNode(suite, isTerminalRun: true);
        Assert.Equal("Abort", view.Result);
        Assert.Equal(expected, view.PresentationResult);
        Assert.Equal(childResult.ToString(), Assert.Single(view.Children).PresentationResult);
    }

    private static UnitTestRunNode CreateNode(
        string id,
        string displayName,
        UnitTestNodeKind kind,
        IReadOnlyList<UnitTestRunNode> children,
        UnitTestResultState result = UnitTestResultState.Passed,
        EvidenceState evidenceState = EvidenceState.Complete,
        bool missingResultEvidence = false,
        UnitTestLifecycleState lifecycleState = UnitTestLifecycleState.Completed,
        bool missingCompletionEvidence = false)
    {
        return new UnitTestRunNode(
            new UnitTestNodeId(id),
            displayName,
            displayName,
            kind,
            lifecycleState,
            result,
            evidenceState,
            null,
            new UnitTestEventRef(1),
            missingCompletionEvidence ? null : new UnitTestEventRef(2),
            missingResultEvidence ? null : new UnitTestEventRef(3),
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [],
            children);
    }
}
