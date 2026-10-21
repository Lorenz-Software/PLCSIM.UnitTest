using System.IO;
using System.IO.Compression;
using System.Security;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PLCSIM.UnitTest.App.Wpf.Dialogs;
using PLCSIM.UnitTest.App.Wpf.Services;
using PLCSIM.UnitTest.App.Wpf.ViewModels;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Artifacts;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Diagnostics;
using PLCSIM.UnitTest.Core.Output;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.Exporters.JUnit;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Wpf;

public sealed partial class MainWindowViewModelStepperSmokeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plc_footer_uses_selected_identity_without_implying_connectivity(bool managed)
    {
        using var viewModel = CreateViewModel(new AlwaysConfirmDialogService());
        viewModel.BuildAndDownloadProject = managed;
        Assert.Equal("No PLC", viewModel.PlcStatus);

        viewModel.SelectedRuntimeTarget = RuntimeTargetSelectionViewModel.CreateOwned();
        Assert.Equal("No PLC", viewModel.PlcStatus);

        viewModel.SelectedRuntimeTarget = RuntimeTargetSelectionViewModel.FromTarget(
            new RuntimeTargetRef(new RuntimeTargetId("runtime-a"), "Runtime A"));
        Assert.Equal("Runtime PLC", viewModel.PlcStatus);
        Assert.Equal("Runtime A", viewModel.RuntimeStatus);
        Assert.Same(Brushes.Gray, viewModel.PlcStatusBrush);

        viewModel.SelectedProjectTarget = ProjectTargetSelectionViewModel.FromTarget(
            new ProjectTargetRef(new ProjectTargetId("plc-a"), "PLC A"));
        Assert.Equal("PLC A", viewModel.PlcStatus);
        viewModel.SelectedProjectTarget = ProjectTargetSelectionViewModel.FromTarget(
            new ProjectTargetRef(new ProjectTargetId("plc-b"), "PLC B"));
        Assert.Equal("PLC B", viewModel.PlcStatus);
        viewModel.SelectedProjectTarget = null;
        Assert.Equal("Runtime PLC", viewModel.PlcStatus);

        viewModel.SelectedRuntimeTarget = RuntimeTargetSelectionViewModel.FromTarget(
            new RuntimeTargetRef(new RuntimeTargetId("runtime-b"), "Runtime B"));
        viewModel.BuildAndDownloadProject = !managed;
        Assert.Equal("Runtime PLC", viewModel.PlcStatus);
        Assert.Equal("Runtime B", viewModel.RuntimeStatus);
        Assert.Same(Brushes.Gray, viewModel.PlcStatusBrush);
        viewModel.SelectedRuntimeTarget = null;
        Assert.Equal("No PLC", viewModel.PlcStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plc_footer_preserves_identity_across_lifecycle_and_clears_on_session_end(bool managed)
    {
        var timeProvider = FakeTime.Create();
        using var viewModel = CreateViewModel(new AlwaysConfirmDialogService(),
            FakeAccessProfileFactory.CreatePlugin(timeProvider), timeProvider);
        viewModel.BuildAndDownloadProject = managed;
        await LoadFakeProfileStep1Async(viewModel);
        Assert.Equal("No PLC", viewModel.PlcStatus);
        if (managed)
        {
            Assert.NotEmpty(viewModel.ProjectTargets);
            viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        }
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets.First(target => target.Target is not null);
        var expected = managed ? viewModel.SelectedProjectTarget!.DisplayName : "Runtime PLC";
        Assert.Equal(expected, viewModel.PlcStatus);
        Assert.Same(Brushes.Gray, viewModel.PlcStatusBrush);

        viewModel.ConfigureStep2Command.Execute(null);
        await ContinueThroughProfileConfigurationAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);
        Assert.Equal(expected, viewModel.PlcStatus);
        Assert.Same(Brushes.Goldenrod, viewModel.PlcStatusBrush);

        viewModel.StartPlcCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.Equal(expected, viewModel.PlcStatus);
        Assert.Same(Brushes.ForestGreen, viewModel.PlcStatusBrush);

        viewModel.StopPlcCommand.Execute(null);
        await WaitUntilAsync(() => HasPlcProcessStatus(viewModel, ProcessStatusKind.Stopped)
            && HasCommunicationStatus(viewModel, ProcessStatusKind.Disconnected));
        Assert.Equal(expected, viewModel.PlcStatus);
        Assert.Same(Brushes.Goldenrod, viewModel.PlcStatusBrush);

        viewModel.ReturnToStep3Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step3PanelVisibility == Visibility.Visible);
        Assert.Equal(expected, viewModel.PlcStatus);
        Assert.Same(Brushes.Gray, viewModel.PlcStatusBrush);

        viewModel.EndSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step1PanelVisibility == Visibility.Visible
            && viewModel.ActiveOperation == "Idle");
        Assert.Null(viewModel.SelectedProjectTarget);
        Assert.Null(viewModel.SelectedRuntimeTarget);
        Assert.Equal("No PLC", viewModel.PlcStatus);
        Assert.Same(Brushes.Gray, viewModel.PlcStatusBrush);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settings_save_failure_preserves_active_session_and_allows_retry(bool readOnly)
    {
        var directory = CreateTempDirectory("PLCSIM.UnitTest.Wpf.SettingsSaveFailure");
        var path = Path.Combine(directory, "settings.json");
        FileStream? fileLock = null;
        try
        {
            var settings = WpfApplicationSettingsService.Create(path);
            settings.Save(WpfApplicationSettings.Default with { BuildAndDownloadProjectByDefault = true });
            var previous = settings.Current;
            var previousBytes = File.ReadAllBytes(path);
            var updated = previous with { TestResultFileName = "retry.xml" };
            var changedCount = 0;
            settings.SettingsChanged += (_, _) => changedCount++;
            var dialog = new TestSettingsDialogService(() =>
            {
                settings.Save(updated);
                return true;
            });
            var timeProvider = FakeTime.Create();
            using var viewModel = CreateViewModel(
                new AlwaysConfirmDialogService(),
                FakeAccessProfileFactory.CreatePlugin(timeProvider),
                timeProvider,
                applicationSettings: settings,
                settingsDialogService: dialog);
            await PrepareFakeProfileForUnitTestRunAsync(viewModel);
            var previousWorkflowState = viewModel.WorkflowState;
            var previousRuntime = viewModel.SelectedRuntimeTarget;
            Assert.True(viewModel.StartUnitTestCommand.CanExecute(null));

            if (readOnly)
            {
                File.SetAttributes(path, FileAttributes.ReadOnly);
            }
            else
            {
                fileLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            viewModel.OpenSettingsCommand.Execute(null);

            Assert.Same(previous, settings.Current);
            Assert.Equal(0, changedCount);
            Assert.Equal(previousBytes, File.ReadAllBytes(path));
            Assert.Contains("Wpf.Settings.SaveFailed", viewModel.TerminalText, StringComparison.Ordinal);
            Assert.Contains("previous settings remain active", viewModel.TerminalText, StringComparison.Ordinal);
            Assert.DoesNotContain("Application settings saved.", viewModel.TerminalText, StringComparison.Ordinal);
            Assert.Equal(previousWorkflowState, viewModel.WorkflowState);
            Assert.Same(previousRuntime, viewModel.SelectedRuntimeTarget);
            Assert.True(viewModel.StartUnitTestCommand.CanExecute(null));
            Assert.True(viewModel.OpenSettingsCommand.CanExecute(null));

            fileLock?.Dispose();
            fileLock = null;
            File.SetAttributes(path, FileAttributes.Normal);
            viewModel.OpenSettingsCommand.Execute(null);

            Assert.Equal(updated, settings.Current);
            Assert.Equal(1, changedCount);
            Assert.Equal(updated, WpfApplicationSettingsService.Create(path).Current);
            Assert.Contains("Application settings saved.", viewModel.TerminalText, StringComparison.Ordinal);
            Assert.Equal(previousWorkflowState, viewModel.WorkflowState);
            Assert.True(viewModel.StartUnitTestCommand.CanExecute(null));
        }
        finally
        {
            fileLock?.Dispose();
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestSettingsDialogService(Func<bool> show) : ISettingsDialogService
    {
        public bool ShowSettingsDialog() => show();
    }

    [Fact]
    public void Application_exit_from_step1_does_not_require_confirmation()
    {
        var confirmations = new RejectConfirmDialogService();
        using var viewModel = CreateViewModel(confirmations);

        Assert.False(viewModel.RequiresApplicationExitConfirmation);
        Assert.True(viewModel.ConfirmApplicationExit());
        Assert.Equal(0, confirmations.CallCount);
    }

    [Fact]
    public async Task Confirmed_application_exit_from_step2_releases_session_resources()
    {
        var confirmations = new AlwaysConfirmDialogService();
        using var viewModel = CreateViewModel(confirmations);
        viewModel.ProjectSource = "fake-project.ap";
        await LoadFakeProfileStep1Async(viewModel);

        Assert.True(viewModel.RequiresApplicationExitConfirmation);
        Assert.True(viewModel.ConfirmApplicationExit());
        Assert.Equal("End Session and Exit?", confirmations.LastTitle);
        Assert.Equal(
            "The current session, prepared runtime, UnitTest communication, project context, access profile, and configuration draft will be released before the application exits.",
            confirmations.LastMessage);

        await viewModel.CleanupForApplicationExitAsync(CancellationToken.None);

        Assert.Contains("Ending session before application exit. Releasing workflow resources...", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, viewModel.ProjectLockIconVisibility);
    }

    [Fact]
    public async Task End_session_still_unloads_profile_after_workflow_cleanup_timeout()
    {
        var timeProvider = FakeTime.Create();
        var eventStream = new FirstCleanupCancellationHoldingEventStreamService();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider) with
        {
            UnitTestEventStream = eventStream
        };
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);
        await LoadFakeProfileStep1Async(viewModel);

        viewModel.EndSessionCommand.Execute(null);
        await eventStream.WaitUntilFirstCleanupStartedAsync();
        timeProvider.Advance(WpfApplicationSettings.Default.OperationTimeouts.Cleanup);
        await WaitUntilAsync(() => viewModel.Step1PanelVisibility == Visibility.Visible);

        Assert.Contains("Session ended.", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, viewModel.ProjectLockIconVisibility);
        Assert.True(viewModel.LoadStep1Command.CanExecute(null));
    }

    [Fact]
    public async Task Rejected_application_exit_from_step2_keeps_session_context()
    {
        var confirmations = new RejectConfirmDialogService();
        using var viewModel = CreateViewModel(confirmations);
        viewModel.ProjectSource = "fake-project.ap";
        await LoadFakeProfileStep1Async(viewModel);
        var projectTargetCount = viewModel.ProjectTargets.Count;
        var runtimeTargetCount = viewModel.RuntimeTargets.Count;

        Assert.True(viewModel.RequiresApplicationExitConfirmation);
        Assert.False(viewModel.ConfirmApplicationExit());
        Assert.Equal(1, confirmations.CallCount);
        Assert.Equal(projectTargetCount, viewModel.ProjectTargets.Count);
        Assert.Equal(runtimeTargetCount, viewModel.RuntimeTargets.Count);
        Assert.True(viewModel.ReturnToStep1Command.CanExecute(null));
    }

    [Fact]
    public async Task Stepper_exposes_progress_and_gate_reasons_without_ui_owned_state()
    {
        using var viewModel = CreateViewModel(new AlwaysConfirmDialogService());

        Assert.Equal("#2F80ED", viewModel.Step1MarkerFill);
        Assert.Equal("#B8C0CC", viewModel.Step2MarkerFill);

        viewModel.ProjectSource = "fake-project.ap";
        await LoadFakeProfileStep1Async(viewModel);

        Assert.Equal("#5E6B7D", viewModel.Step1MarkerFill);
        Assert.Equal("#2F80ED", viewModel.Step2MarkerFill);
        Assert.Equal("#B8C0CC", viewModel.Step3MarkerFill);
        Assert.Contains("PLC target is not selected", viewModel.Step2GateStatus, StringComparison.Ordinal);

        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];
        Assert.StartsWith("Next: Available", viewModel.Step2GateStatus, StringComparison.Ordinal);
        viewModel.ConfigureStep2Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step3PanelVisibility == Visibility.Visible);

        viewModel.CommunicationPrepareTimeoutSeconds = "0";
        Assert.Contains("Next: Blocked", viewModel.Step3GateStatus, StringComparison.Ordinal);
        Assert.Contains("positive whole number", viewModel.Step3GateStatus, StringComparison.Ordinal);
        Assert.False(viewModel.IsCommunicationPrepareTimeoutValid);
        Assert.Contains(
            "positive whole number",
            viewModel.CommunicationPrepareTimeoutValidationMessage,
            StringComparison.Ordinal);

        viewModel.CommunicationPrepareTimeoutSeconds = "60";
        viewModel.CommunicationRevalidationTimeoutSeconds = "d";
        Assert.False(viewModel.IsCommunicationRevalidationTimeoutValid);
        Assert.Contains(
            "non-negative whole number",
            viewModel.CommunicationRevalidationTimeoutValidationMessage,
            StringComparison.Ordinal);
        viewModel.CommunicationRevalidationTimeoutSeconds = "0";
        Assert.True(viewModel.IsCommunicationPrepareTimeoutValid);
        Assert.True(viewModel.IsCommunicationRevalidationTimeoutValid);
        Assert.Contains("Next: Available", viewModel.Step3GateStatus, StringComparison.Ordinal);

        viewModel.ConfigureProfileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);

        Assert.Equal("#5E6B7D", viewModel.Step3MarkerFill);
        Assert.Equal("#2F80ED", viewModel.Step4MarkerFill);
        Assert.StartsWith("Start PLC:", viewModel.StartPlcGateStatus, StringComparison.Ordinal);
        Assert.StartsWith("Stop PLC:", viewModel.StopPlcGateStatus, StringComparison.Ordinal);
        Assert.StartsWith("Start UnitTest:", viewModel.StartUnitTestGateStatus, StringComparison.Ordinal);
        Assert.StartsWith("Abort UnitTest:", viewModel.AbortUnitTestGateStatus, StringComparison.Ordinal);
        Assert.StartsWith("Reset UnitTest:", viewModel.ResetUnitTestGateStatus, StringComparison.Ordinal);
        Assert.StartsWith("Reset Event Buffer:", viewModel.ResetEventBufferGateStatus, StringComparison.Ordinal);
        Assert.False(viewModel.ResetEventBufferCommand.CanExecute(null));
    }

    [Fact]
    public async Task Manual_discovery_presents_multiple_project_targets_for_explicit_selection()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        var secondTarget = new ProjectTargetRef(
            new ProjectTargetId("fake-project-target-2"),
            "Fake PLC Project Target 2",
            "FakeProjectTarget2");
        project.DiscoverProjectTargetsResult =
            OperationResult<ProjectTargetDiscoveryResult>.Success(
                new ProjectTargetDiscoveryResult(
                    [project.DefaultProjectTarget, secondTarget],
                    TargetDiscoveryState.CandidatesAvailable,
                    []));
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        viewModel.ProjectSource = "fake-project.ap";
        viewModel.LoadStep1Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step2InputsEnabled);

        Assert.Equal(2, viewModel.ProjectTargets.Count);
        Assert.Null(viewModel.SelectedProjectTarget);
        Assert.Contains(viewModel.ProjectTargets, target => target.Target.Id.Value == secondTarget.Id.Value);

        viewModel.SelectedProjectTarget = viewModel.ProjectTargets.Single(
            target => target.Target.Id.Value == secondTarget.Id.Value);
        viewModel.SelectedRuntimeTarget = Assert.Single(viewModel.RuntimeTargets, target => !target.CreatesOwnedRuntime);

        Assert.True(viewModel.ConfigureStep2Command.CanExecute(null));
    }

    [Fact]
    public async Task Manual_setup_uses_require_existing_with_selected_runtime_and_edited_configuration()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        viewModel.ProjectSource = "fake-project.ap";
        await LoadFakeProfileStep1Async(viewModel);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];

        viewModel.ConfigureStep2Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step3PanelVisibility == Visibility.Visible);
        viewModel.CommunicationPrepareTimeoutSeconds = "0";
        Assert.False(viewModel.ConfigureProfileCommand.CanExecute(null));
        viewModel.CommunicationPrepareTimeoutSeconds = "17";
        viewModel.CommunicationRevalidationTimeoutSeconds = "-1";
        Assert.False(viewModel.ConfigureProfileCommand.CanExecute(null));
        viewModel.CommunicationRevalidationTimeoutSeconds = "0";
        var iterations = Assert.Single(
            viewModel.ProfileConfiguration.Fields,
            field => field.Id == "fake.test.iterations");
        iterations.Value = "3";
        Assert.True(viewModel.ConfigureProfileCommand.CanExecute(null));
        viewModel.ConfigureProfileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);

        var request = Assert.IsType<FakePlcControlService>(services.PlcControl).LastPrepareRuntimeRequest;
        Assert.NotNull(request);
        Assert.Equal(RuntimeProvisioningMode.UseExisting, request!.ProvisioningMode);
        Assert.Equal("fake-runtime-target", request.RuntimeTarget?.Id.Value);
        Assert.Equal("fake-project.ap", request.Configuration.ProjectSource);
        Assert.Contains(
            "Opening project file: fake-project.ap",
            viewModel.TerminalText,
            StringComparison.Ordinal);
        using var configuration = System.Text.Json.JsonDocument.Parse(request.Configuration.Values.Json);
        Assert.Equal(
            3,
            configuration.RootElement.GetProperty("testSettings").GetProperty("iterations").GetInt32());
    }

    [Fact]
    public async Task Manual_setup_uses_create_owned_without_a_runtime_target()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        ConfigureNoDiscoveredRuntimeTargets(services);
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        viewModel.ProjectSource = "fake-project.ap";
        await LoadFakeProfileStep1Async(viewModel);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = Assert.Single(
            viewModel.RuntimeTargets,
            target => target.CreatesOwnedRuntime);

        viewModel.ConfigureStep2Command.Execute(null);
        await ContinueThroughProfileConfigurationAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);

        var request = Assert.IsType<FakePlcControlService>(services.PlcControl).LastPrepareRuntimeRequest;
        Assert.NotNull(request);
        Assert.Equal(RuntimeProvisioningMode.CreateOwned, request!.ProvisioningMode);
        Assert.Null(request.RuntimeTarget);
        Assert.Equal(RuntimeProvisioningMode.CreateOwned, viewModel.SelectedRuntimeTarget?.ProvisioningMode);
    }

    [Fact]
    public async Task Managed_setup_does_not_prompt_when_safety_authorization_is_not_required()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var passwordDialog = new QueueSafetyProgramPasswordDialogService("unused-test-value");
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider,
            safetyProgramPasswordDialogService: passwordDialog);

        await ConfigureFakeProfileToStep4Async(viewModel);

        Assert.Equal(0, passwordDialog.CallCount);
        Assert.Contains("Safety program authorization is not required.", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_safety_program_stops_setup_before_prompt_build_and_transfer()
    {
        const string unsupportedMessage = "The selected project binding does not support Safety-program handling.";
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        project.SafetyAuthorizationRequirementResult =
            OperationResult<SafetyProgramAuthorizationRequirementResult>.NotSupported(unsupportedMessage);
        var buildCallCount = 0;
        var transferCallCount = 0;
        project.BuildHandler = _ =>
        {
            buildCallCount++;
            return Task.FromResult(OperationResult<PlcBuildResult>.Failure("Unexpected build."));
        };
        project.TransferHandler = _ =>
        {
            transferCallCount++;
            return Task.FromResult(OperationResult<PlcTransferResult>.Failure("Unexpected transfer."));
        };
        var passwordDialog = new QueueSafetyProgramPasswordDialogService("unused-test-value");
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider,
            safetyProgramPasswordDialogService: passwordDialog);

        await LoadFakeProfileStep1Async(viewModel);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];
        viewModel.ConfigureStep2Command.Execute(null);
        await ContinueThroughProfileConfigurationAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Step2Status == unsupportedMessage);

        Assert.Equal(0, passwordDialog.CallCount);
        Assert.Equal(0, buildCallCount);
        Assert.Equal(0, transferCallCount);
        Assert.True(viewModel.ConfigureProfileCommand.CanExecute(null));
        Assert.Contains($"Project setup cannot continue: {unsupportedMessage}", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Managed_setup_authorizes_safety_program_before_build_without_exposing_password()
    {
        const string testPassword = "wpf-safety-test-value";
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        project.SafetyAuthorizationRequirementResult =
            OperationResult<SafetyProgramAuthorizationRequirementResult>.Success(
                new SafetyProgramAuthorizationRequirementResult(
                    SafetyProgramAuthorizationRequirement.PasswordRequired));
        var authorizationCallCount = 0;
        var buildCallCount = 0;
        project.SafetyAuthorizationHandler = (password, _) =>
        {
            authorizationCallCount++;
            Assert.True(password.IsReadOnly());
            Assert.True(password.Length > 0);
            Assert.Equal(0, buildCallCount);
            return Task.FromResult(OperationResult<SafetyProgramAuthorizationResult>.Success(
                new SafetyProgramAuthorizationResult(true)));
        };
        project.BuildHandler = _ =>
        {
            buildCallCount++;
            return Task.FromResult(OperationResult<PlcBuildResult>.Success(
                new PlcBuildResult(true, false, [])));
        };
        var passwordDialog = new QueueSafetyProgramPasswordDialogService(testPassword);
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider,
            safetyProgramPasswordDialogService: passwordDialog);

        await ConfigureFakeProfileToStep4Async(viewModel);

        Assert.Equal(1, passwordDialog.CallCount);
        Assert.Equal(1, authorizationCallCount);
        Assert.Equal(1, buildCallCount);
        Assert.NotNull(passwordDialog.LastIssuedPassword);
        Assert.Throws<ObjectDisposedException>(() => _ = passwordDialog.LastIssuedPassword!.Length);
        Assert.DoesNotContain(testPassword, viewModel.TerminalText, StringComparison.Ordinal);
        Assert.DoesNotContain(testPassword, viewModel.Step2Status, StringComparison.Ordinal);
        Assert.Contains("Safety program authorization succeeded.", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_safety_password_prompt_keeps_prepared_setup_retryable_without_building()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        project.SafetyAuthorizationRequirementResult =
            OperationResult<SafetyProgramAuthorizationRequirementResult>.Success(
                new SafetyProgramAuthorizationRequirementResult(
                    SafetyProgramAuthorizationRequirement.PasswordRequired));
        var authorizationCallCount = 0;
        var buildCallCount = 0;
        project.SafetyAuthorizationHandler = (_, _) =>
        {
            authorizationCallCount++;
            return Task.FromResult(OperationResult<SafetyProgramAuthorizationResult>.Success(
                new SafetyProgramAuthorizationResult(true)));
        };
        project.BuildHandler = _ =>
        {
            buildCallCount++;
            return Task.FromResult(OperationResult<PlcBuildResult>.Success(
                new PlcBuildResult(true, false, [])));
        };
        var passwordDialog = new QueueSafetyProgramPasswordDialogService([null]);
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider,
            safetyProgramPasswordDialogService: passwordDialog);

        await LoadFakeProfileStep1Async(viewModel);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];
        viewModel.ConfigureStep2Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step3PanelVisibility == Visibility.Visible);
        viewModel.ConfigureProfileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step2Status == "Safety program authorization canceled.");

        Assert.Equal(1, passwordDialog.CallCount);
        Assert.Equal(0, authorizationCallCount);
        Assert.Equal(0, buildCallCount);
        Assert.True(viewModel.ConfigureProfileCommand.CanExecute(null));
        Assert.Equal(RunWorkflowState.RuntimePrepared, viewModel.WorkflowState);
    }

    [Fact]
    public async Task Rejected_safety_authorization_can_prompt_again_in_the_same_engineering_session()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        project.SafetyAuthorizationRequirementResult =
            OperationResult<SafetyProgramAuthorizationRequirementResult>.Success(
                new SafetyProgramAuthorizationRequirementResult(
                    SafetyProgramAuthorizationRequirement.PasswordRequired));
        var authorizationAttempt = 0;
        var buildCallCount = 0;
        project.SafetyAuthorizationHandler = (_, _) =>
        {
            authorizationAttempt++;
            return Task.FromResult(
                authorizationAttempt == 1
                    ? OperationResult<SafetyProgramAuthorizationResult>.Failure(
                        "Safety-program authorization was rejected.")
                    : OperationResult<SafetyProgramAuthorizationResult>.Success(
                        new SafetyProgramAuthorizationResult(true)));
        };
        project.BuildHandler = _ =>
        {
            buildCallCount++;
            return Task.FromResult(OperationResult<PlcBuildResult>.Success(
                new PlcBuildResult(true, false, [])));
        };
        var passwordDialog = new QueueSafetyProgramPasswordDialogService(
            "rejected-test-value",
            "accepted-test-value");
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider,
            safetyProgramPasswordDialogService: passwordDialog);

        await LoadFakeProfileStep1Async(viewModel);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];
        viewModel.ConfigureStep2Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step3PanelVisibility == Visibility.Visible);
        viewModel.ConfigureProfileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step2Status == "Safety-program authorization was rejected.");

        Assert.Equal(0, buildCallCount);
        Assert.True(viewModel.ConfigureProfileCommand.CanExecute(null));

        viewModel.ConfigureProfileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);

        Assert.Equal(2, passwordDialog.CallCount);
        Assert.Equal(2, authorizationAttempt);
        Assert.Equal(1, buildCallCount);
        Assert.Equal(1, project.PrepareProjectCallCount);
    }

    [Fact]
    public async Task Unsupported_safety_authorization_stops_setup_before_build_and_transfer()
    {
        const string unsupportedMessage = "The selected project binding cannot authorize this Safety program.";
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        project.SafetyAuthorizationRequirementResult =
            OperationResult<SafetyProgramAuthorizationRequirementResult>.Success(
                new SafetyProgramAuthorizationRequirementResult(
                    SafetyProgramAuthorizationRequirement.PasswordRequired));
        var authorizationCallCount = 0;
        var buildCallCount = 0;
        var transferCallCount = 0;
        project.SafetyAuthorizationHandler = (_, _) =>
        {
            authorizationCallCount++;
            return Task.FromResult(
                OperationResult<SafetyProgramAuthorizationResult>.NotSupported(unsupportedMessage));
        };
        project.BuildHandler = _ =>
        {
            buildCallCount++;
            return Task.FromResult(OperationResult<PlcBuildResult>.Failure("Unexpected build."));
        };
        project.TransferHandler = _ =>
        {
            transferCallCount++;
            return Task.FromResult(OperationResult<PlcTransferResult>.Failure("Unexpected transfer."));
        };
        var passwordDialog = new QueueSafetyProgramPasswordDialogService("wpf-safety-test-value");
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider,
            safetyProgramPasswordDialogService: passwordDialog);

        await LoadFakeProfileStep1Async(viewModel);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];
        viewModel.ConfigureStep2Command.Execute(null);
        await ContinueThroughProfileConfigurationAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Step2Status == unsupportedMessage);

        Assert.Equal(1, passwordDialog.CallCount);
        Assert.Equal(1, authorizationCallCount);
        Assert.Equal(0, buildCallCount);
        Assert.Equal(0, transferCallCount);
        Assert.True(viewModel.ConfigureProfileCommand.CanExecute(null));
        Assert.Contains($"Project setup cannot continue: {unsupportedMessage}", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_create_owned_runtime_returns_to_runtime_selection()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        ConfigureNoDiscoveredRuntimeTargets(services);
        Assert.IsType<FakePlcControlService>(services.PlcControl).PrepareRuntimeResult =
            OperationResult<PreparedPlcRuntime>.Failure("Owned runtime creation is not supported.");
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        viewModel.ProjectSource = "fake-project.ap";
        await LoadFakeProfileStep1Async(viewModel);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = Assert.Single(
            viewModel.RuntimeTargets,
            target => target.CreatesOwnedRuntime);

        viewModel.ConfigureStep2Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step3PanelVisibility == Visibility.Visible);
        viewModel.ConfigureProfileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step2PanelVisibility == Visibility.Visible);

        Assert.True(viewModel.Step2InputsEnabled);
        Assert.Contains("not supported", viewModel.Step2Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(viewModel.RuntimeTargets, target => target.CreatesOwnedRuntime);
        Assert.True(viewModel.ConfigureStep2Command.CanExecute(null));
    }

    [Fact]
    public async Task Manual_setup_offers_creation_independently_of_existing_targets()
    {
        using var viewModel = CreateViewModel(new AlwaysConfirmDialogService());

        viewModel.ProjectSource = "fake-project.ap";
        await LoadFakeProfileStep1Async(viewModel);

        Assert.NotEmpty(viewModel.RuntimeTargets);
        Assert.Contains(viewModel.RuntimeTargets, target => target.CreatesOwnedRuntime);
    }

    [Fact]
    public async Task Failed_automatic_communication_preparation_disables_unittest_commands()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider) with
        {
            PrepareUnitTestCommunicationResult =
                OperationResult<CommunicationSession>.Failure("Fake communication unavailable.")
        };
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await ConfigureFakeProfileToStep4Async(viewModel);
        viewModel.StartPlcCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.WorkflowState == RunWorkflowState.Error);

        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.False(viewModel.AbortUnitTestCommand.CanExecute(null));
        Assert.False(viewModel.ResetUnitTestCommand.CanExecute(null));
        Assert.False(viewModel.ResetEventBufferCommand.CanExecute(null));
        AssertCommunicationStatus(viewModel, ProcessStatusKind.Disconnected);
        Assert.Contains(
            viewModel.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.Communication.Prepare.Failed");
    }

    [Fact]
    public async Task Communication_revalidation_updates_badges_output_and_gates_until_recovered()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await ConfigureFakeProfileToStep4Async(viewModel);
        viewModel.StartPlcCommand.Execute(null);
        await WaitUntilAsync(() => HasCommunicationStatus(viewModel, ProcessStatusKind.Connected)
            && HasMonitoringStatus(viewModel, ProcessStatusKind.Active));

        lifecycle.SimulateLifecycleCommunicationRevalidationRequired();
        await WaitUntilAsync(() => HasCommunicationStatus(viewModel, ProcessStatusKind.Revalidating));

        AssertCommunicationStatus(viewModel, ProcessStatusKind.Revalidating);
        AssertMonitoringStatus(viewModel, ProcessStatusKind.Revalidating);
        AssertRunControlStatus(viewModel, ProcessStatusKind.Revalidating);
        Assert.True(viewModel.ActivityIndicatorActive);
        Assert.Equal("Revalidating UnitTest communication...", viewModel.ActiveOperation);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.Contains("UnitTest communication is revalidating.", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            viewModel.Diagnostics,
            diagnostic => diagnostic.Code.Contains("RevalidationRequired", StringComparison.Ordinal));

        lifecycle.SimulateLifecycleCommunicationRecovered();
        await WaitUntilAsync(() => HasCommunicationStatus(viewModel, ProcessStatusKind.Connected));

        AssertMonitoringStatus(viewModel, ProcessStatusKind.Active);
        AssertRunControlStatus(viewModel, ProcessStatusKind.Ready);
        Assert.False(viewModel.ActivityIndicatorActive);
        Assert.True(viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.Contains("UnitTest communication recovered.", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stepper_runs_fake_profile_path_and_confirmed_back_navigation()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        using var viewModel = CreateViewModel(
            confirmations,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider);

        Assert.Equal(Visibility.Visible, viewModel.Step1PanelVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.Step2PanelVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.Step4PanelVisibility);
        var discoveredProfile = Assert.Single(viewModel.AvailableAccessProfiles);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileId, discoveredProfile.ProfileId);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileVersion, discoveredProfile.ProfileVersion);
        Assert.Same(discoveredProfile, viewModel.SelectedAccessProfile);
        Assert.False(viewModel.LoadStep1Command.CanExecute(null));
        Assert.Equal("Project source missing", viewModel.ProjectSourceStatus);
        viewModel.ProjectSource = "fake-project.ap";
        Assert.True(viewModel.LoadStep1Command.CanExecute(null));
        AssertPreparationStatus(viewModel, ProcessStatusKind.NotPrepared);
        Assert.Contains("Access profile registry loaded: 1 registered profile(s), 1 registry entry/entries.", viewModel.TerminalText, StringComparison.Ordinal);

        await LoadFakeProfileStep1Async(viewModel);

        Assert.NotEmpty(viewModel.ProjectTargets);
        Assert.NotEmpty(viewModel.RuntimeTargets);
        Assert.Null(viewModel.SelectedProjectTarget);
        Assert.Null(viewModel.SelectedRuntimeTarget);
        Assert.False(viewModel.ConfigureStep2Command.CanExecute(null));

        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];

        Assert.True(viewModel.ConfigureStep2Command.CanExecute(null));
        Assert.Equal(Visibility.Collapsed, viewModel.Step4PanelVisibility);
        Assert.False(viewModel.StartPlcCommand.CanExecute(null));

        viewModel.ConfigureStep2Command.Execute(null);
        await ContinueThroughProfileConfigurationAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);

        AssertPlcProcessStatus(viewModel, ProcessStatusKind.Stopped);
        AssertCommunicationStatus(viewModel, ProcessStatusKind.Disconnected);
        AssertPreparationStatus(viewModel, ProcessStatusKind.Transferred);
        Assert.Contains("transferred", viewModel.PreparationPhase, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Not requested", viewModel.TestResultArtifactStatus);
        Assert.Equal("No run captured yet.", viewModel.TestResultArtifactPreview);
        Assert.False(viewModel.OpenTestResultArtifactCommand.CanExecute(null));
        Assert.True(viewModel.OpenOutputFolderCommand.CanExecute(null));
        Assert.Contains("Preparing project", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.Contains("Runtime setup is ready", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.True(viewModel.StartPlcCommand.CanExecute(null));
        Assert.False(viewModel.StopPlcCommand.CanExecute(null));

        viewModel.StartPlcCommand.Execute(null);
        await WaitUntilAsync(() => HasPlcProcessStatus(viewModel, ProcessStatusKind.Running)
            && HasCommunicationStatus(viewModel, ProcessStatusKind.Connected));

        Assert.False(viewModel.StartPlcCommand.CanExecute(null));
        Assert.True(viewModel.StopPlcCommand.CanExecute(null));
        AssertCommunicationStatus(viewModel, ProcessStatusKind.Connected);
        AssertMonitoringStatus(viewModel, ProcessStatusKind.Active);
        AssertRunControlStatus(viewModel, ProcessStatusKind.Ready);
        Assert.Equal("idle", viewModel.RunStatus);
        Assert.True(viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.False(viewModel.AbortUnitTestCommand.CanExecute(null));
        Assert.True(viewModel.ResetUnitTestCommand.CanExecute(null));
        Assert.True(viewModel.ResetEventBufferCommand.CanExecute(null));
        Assert.Contains("PLC is running", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.Contains("UnitTest event-stream monitoring is active", viewModel.TerminalText, StringComparison.Ordinal);

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));
        await WaitUntilAsync(() => HasRunControlStatus(viewModel, ProcessStatusKind.Running));
        Assert.True(viewModel.RunControlStatusViewState.IsAnimated);

        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.True(viewModel.AbortUnitTestCommand.CanExecute(null));
        Assert.True(viewModel.ResetUnitTestCommand.CanExecute(null));
        Assert.Contains("Test run started.", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.DoesNotContain("UnitTest start command accepted", viewModel.TerminalText, StringComparison.Ordinal);

        viewModel.StopPlcCommand.Execute(null);
        await WaitUntilAsync(() => HasPlcProcessStatus(viewModel, ProcessStatusKind.Stopped)
            && HasCommunicationStatus(viewModel, ProcessStatusKind.Disconnected));

        Assert.True(viewModel.StartPlcCommand.CanExecute(null));
        Assert.False(viewModel.StopPlcCommand.CanExecute(null));
        AssertCommunicationStatus(viewModel, ProcessStatusKind.Disconnected);
        AssertMonitoringStatus(viewModel, ProcessStatusKind.Off);
        AssertRunControlStatus(viewModel, ProcessStatusKind.Disconnected);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.False(viewModel.AbortUnitTestCommand.CanExecute(null));
        Assert.False(viewModel.ResetUnitTestCommand.CanExecute(null));
        Assert.Contains("PLC is stopped", viewModel.TerminalText, StringComparison.Ordinal);

        var selectedProjectTargetId = viewModel.SelectedProjectTarget?.Target.Id.Value;
        var selectedRuntimeTargetId = viewModel.SelectedRuntimeTarget?.Target?.Id.Value;
        viewModel.ReturnToStep3Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step3PanelVisibility == Visibility.Visible);

        Assert.Equal(1, confirmations.CallCount);
        Assert.Equal(selectedProjectTargetId, viewModel.SelectedProjectTarget?.Target.Id.Value);
        Assert.Equal(selectedRuntimeTargetId, viewModel.SelectedRuntimeTarget?.Target?.Id.Value);
        Assert.True(viewModel.ConfigureProfileCommand.CanExecute(null));

        viewModel.ConfigureProfileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible
            || viewModel.Step2Status.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || viewModel.Step2Status.Contains("blocked", StringComparison.OrdinalIgnoreCase)
            || viewModel.TerminalText.Contains("Runtime preparation failed", StringComparison.Ordinal));

        Assert.True(
            viewModel.Step4PanelVisibility == Visibility.Visible,
            viewModel.TerminalText + Environment.NewLine + string.Join(
                Environment.NewLine,
                viewModel.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        AssertPreparationStatus(viewModel, ProcessStatusKind.Transferred);
        Assert.DoesNotContain(
            viewModel.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.ActionBlocked.PrepareRuntime.RuntimeTargetNotSelected");

        viewModel.ReturnToStep2Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step2PanelVisibility == Visibility.Visible);

        Assert.Equal(2, confirmations.CallCount);
        Assert.Equal(Visibility.Collapsed, viewModel.Step4PanelVisibility);
        Assert.True(viewModel.Step2InputsEnabled);
        Assert.Contains("Operation context released", viewModel.TerminalText, StringComparison.Ordinal);

        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];
        viewModel.ConfigureStep2Command.Execute(null);
        await ContinueThroughProfileConfigurationAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible
            || viewModel.TerminalText.Contains("Runtime preparation failed", StringComparison.Ordinal));

        Assert.True(
            viewModel.Step4PanelVisibility == Visibility.Visible,
            viewModel.TerminalText + Environment.NewLine + string.Join(
                Environment.NewLine,
                viewModel.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            viewModel.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.ActionBlocked.PrepareRuntime.RuntimeTargetNotSelected");

        viewModel.ReturnToStep2Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step2PanelVisibility == Visibility.Visible);

        viewModel.ReturnToStep1Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step1PanelVisibility == Visibility.Visible);
        await WaitUntilAsync(() => viewModel.ProjectTargets.Count == 0 && viewModel.RuntimeTargets.Count == 0);

        Assert.Equal(4, confirmations.CallCount);
        Assert.Empty(viewModel.ProjectTargets);
        Assert.Empty(viewModel.RuntimeTargets);
        Assert.Null(viewModel.SelectedProjectTarget);
        Assert.Null(viewModel.SelectedRuntimeTarget);
        AssertPlcProcessStatus(viewModel, ProcessStatusKind.NotReady);
        AssertCommunicationStatus(viewModel, ProcessStatusKind.Disconnected);
        AssertRunControlStatus(viewModel, ProcessStatusKind.NotReady);
    }

    [Fact]
    public async Task Stepper_uses_fake_profile_from_directory_package_without_builtin_registration()
    {
        var root = CreateTempDirectory("PLCSIM.UnitTest.Wpf.FakeProfilePackage");
        try
        {
            await VerifyFakeProfilePackageAsync(root);
        }
        finally
        {
            DeleteTemporaryPackageDirectory(root);
        }
    }

    private static async Task VerifyFakeProfilePackageAsync(string root)
    {
        WriteFakeProfilePackage(root);
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        using var viewModel = CreateViewModel(
            confirmations,
            [],
            timeProvider,
            discoveryOptions: new AccessProfileDiscoveryOptions(root, IncludeBuiltInProfiles: false));

        var discoveredProfile = Assert.Single(viewModel.AvailableAccessProfiles);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileId, discoveredProfile.ProfileId);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileVersion, discoveredProfile.ProfileVersion);
        Assert.Same(discoveredProfile, viewModel.SelectedAccessProfile);

        await LoadFakeProfileStep1Async(viewModel);

        Assert.NotEmpty(viewModel.ProjectTargets);
        Assert.NotEmpty(viewModel.RuntimeTargets);
        Assert.Equal("fake-project-target", viewModel.ProjectTargets[0].Target.Id.Value);
        Assert.Equal("fake-runtime-target", viewModel.RuntimeTargets[0].Target?.Id.Value);
        Assert.Contains("Access profile registry loaded:", viewModel.TerminalText, StringComparison.Ordinal);

        viewModel.EndSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step1PanelVisibility == Visibility.Visible
            && viewModel.ActiveOperation == "Idle");
    }

    [Fact]
    public void New_manual_session_uses_persisted_project_handling_default()
    {
        var timeProvider = FakeTime.Create();
        var applicationSettings = new TestWpfApplicationSettingsService(
            WpfApplicationSettings.Default with { BuildAndDownloadProjectByDefault = false });
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            applicationSettings: applicationSettings);

        Assert.False(viewModel.BuildAndDownloadProject);
        Assert.Equal(ProjectOwnershipMode.ExternallyManaged, viewModel.SelectedProjectOwnershipMode);
        Assert.Equal(Visibility.Collapsed, viewModel.ManagedProjectControlsVisibility);
    }

    [Fact]
    public async Task Stepper_runtime_preparation_failure_keeps_profile_configuration_recoverable()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        plcControl.PrepareRuntimeResult = OperationResult<PreparedPlcRuntime>.Failure(
            "Fake runtime preparation failed.");
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await LoadFakeProfileStep1Async(viewModel);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];

        Assert.True(viewModel.ConfigureStep2Command.CanExecute(null));

        viewModel.ConfigureStep2Command.Execute(null);
        await ContinueThroughProfileConfigurationAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Step2Status == "Fake runtime preparation failed.");

        Assert.Equal(Visibility.Collapsed, viewModel.Step1PanelVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.Step2PanelVisibility);
        Assert.Equal(Visibility.Visible, viewModel.Step3PanelVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.Step4PanelVisibility);
        Assert.True(viewModel.Step2InputsEnabled);
        Assert.True(viewModel.ConfigureProfileCommand.CanExecute(null));
        Assert.True(viewModel.ReturnToStep2Command.CanExecute(null));
        Assert.True(viewModel.EndSessionCommand.CanExecute(null));
        Assert.Contains("Runtime preparation failed: Fake runtime preparation failed.", viewModel.TerminalText, StringComparison.Ordinal);

        viewModel.ReturnToStep2Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step2PanelVisibility == Visibility.Visible);

        Assert.True(viewModel.ReturnToStep1Command.CanExecute(null));
        viewModel.ReturnToStep1Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step1PanelVisibility == Visibility.Visible);

        Assert.Equal(2, confirmations.CallCount);
        Assert.True(viewModel.BrowseProjectSourceCommand.CanExecute(null));
        Assert.True(viewModel.LoadStep1Command.CanExecute(null));
    }

    [Fact]
    public async Task Browse_project_source_updates_step1_project_source_and_filters_profiles()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var projectSourceDialog = new FakeProjectSourceFileDialogService(@"D:\Projects\selected-fake-project.json");
        using var viewModel = CreateViewModel(
            confirmations,
            projectSourceFileDialogService: projectSourceDialog);

        Assert.False(viewModel.LoadStep1Command.CanExecute(null));

        viewModel.BrowseProjectSourceCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.TerminalText.Contains(
            "Project source evaluation matched 1 registered access profile(s).",
            StringComparison.Ordinal));
        Assert.Equal(@"D:\Projects\selected-fake-project.json", viewModel.ProjectSource);
        Assert.Equal(@"D:\Projects\selected-fake-project.json", viewModel.ProjectSourceStatus);
        Assert.Single(viewModel.AvailableAccessProfiles);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileId, viewModel.SelectedAccessProfile?.ProfileId);
        Assert.True(viewModel.LoadStep1Command.CanExecute(null));
        Assert.Equal(1, projectSourceDialog.CallCount);
        Assert.Contains("*.json", projectSourceDialog.LastFilter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Browse_project_source_filters_out_profiles_that_do_not_support_the_source()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var projectSourceDialog = new FakeProjectSourceFileDialogService(@"D:\Projects\selected-fake-project.unsupported");
        using var viewModel = CreateViewModel(
            confirmations,
            projectSourceFileDialogService: projectSourceDialog);

        Assert.Single(viewModel.AvailableAccessProfiles);

        viewModel.BrowseProjectSourceCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.Step1Status == "No registered access profile supports the selected project source.");
        Assert.Empty(viewModel.AvailableAccessProfiles);
        Assert.Null(viewModel.SelectedAccessProfile);
        Assert.False(viewModel.LoadStep1Command.CanExecute(null));
        Assert.Equal(1, projectSourceDialog.CallCount);
    }

    [Fact]
    public void Browse_project_source_cancel_keeps_existing_project_source()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var projectSourceDialog = new FakeProjectSourceFileDialogService(null);
        using var viewModel = CreateViewModel(
            confirmations,
            projectSourceFileDialogService: projectSourceDialog);
        viewModel.ProjectSource = "existing-project.ap";

        viewModel.BrowseProjectSourceCommand.Execute(null);

        Assert.Equal("existing-project.ap", viewModel.ProjectSource);
        Assert.True(viewModel.LoadStep1Command.CanExecute(null));
        Assert.Equal(1, projectSourceDialog.CallCount);
    }

    [Fact]
    public async Task Reset_after_completed_fake_run_reenables_start()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider, autoCompleteRuns: true);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await LoadFakeProfileStep1Async(viewModel);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];
        viewModel.ConfigureStep2Command.Execute(null);
        await ContinueThroughProfileConfigurationAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);
        viewModel.StartPlcCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.StartUnitTestCommand.CanExecute(null));

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));
        await WaitUntilAsync(() => HasRunControlStatus(viewModel, ProcessStatusKind.Running));
        timeProvider.Advance(TimeSpan.FromMilliseconds(750));
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.Completed));
        await WaitUntilAsync(() => HasRunControlStatus(viewModel, ProcessStatusKind.Completed));
        await WaitUntilAsync(() => viewModel.RunResultTree.Count == 1
            && viewModel.RunResultTree[0].Children.Count == 1
            && viewModel.RunResultTree[0].Children[0].Children.Count == 1);
        AssertRunControlStatus(viewModel, ProcessStatusKind.Completed);
        Assert.Contains("Test run completed.", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.Equal("Completed / Passed / Complete", viewModel.RunResultStatus);

        var root = viewModel.RunResultTree[0];
        var suite = root.Children[0];
        var testCase = suite.Children[0];
        Assert.Equal("RunControl", root.DisplayName);
        Assert.Equal("Passed", root.Result);
        Assert.Equal("Completed", root.LifecycleState);
        Assert.Equal("SampleSuite", suite.DisplayName);
        Assert.Equal("Passed", suite.Result);
        Assert.Equal("25 ms", suite.Runtime);
        Assert.Equal("SampleCase", testCase.DisplayName);
        Assert.Equal("Passed", testCase.Result);
        Assert.Equal(["RunControl", "SampleSuite"], viewModel.RunResultRows.Select(row => row.DisplayName));
        Assert.Equal([0, 1], viewModel.RunResultRows.Select(row => row.Level));
        viewModel.SelectedRunResultNode = suite;
        Assert.Contains("Test cases: total 1", viewModel.RunResultSummary, StringComparison.Ordinal);
        Assert.Contains("passed 1", viewModel.RunResultSummary, StringComparison.Ordinal);
        Assert.Contains("runtime 25 ms", viewModel.RunResultSummary, StringComparison.Ordinal);
        Assert.True(viewModel.ClearRunResultCommand.CanExecute(null));

        viewModel.ResetUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunControlStatus(viewModel, ProcessStatusKind.Resetting));
        Assert.True(viewModel.RunControlStatusViewState.IsAnimated);
        Assert.Equal("idle", viewModel.RunStatus);
        Assert.Empty(viewModel.RunResultTree);
        Assert.Empty(viewModel.RunResultRows);
        Assert.Equal("No run captured yet", viewModel.RunResultStatus);
        Assert.DoesNotContain("UnitTest reset command accepted", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.False(viewModel.ClearRunResultCommand.CanExecute(null));
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));

        timeProvider.Advance(TimeSpan.FromMilliseconds(250));
        await WaitUntilAsync(() => HasRunControlStatus(viewModel, ProcessStatusKind.Ready));
        Assert.Equal("idle", viewModel.RunStatus);
        AssertRunControlStatus(viewModel, ProcessStatusKind.Ready);

        Assert.True(viewModel.StartUnitTestCommand.CanExecute(null));
    }

    [Fact]
    public async Task Abort_fake_run_publishes_aborted_status()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await LoadFakeProfileStep1Async(viewModel);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];
        viewModel.ConfigureStep2Command.Execute(null);
        await ContinueThroughProfileConfigurationAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);
        viewModel.StartPlcCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.StartUnitTestCommand.CanExecute(null));

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));
        await WaitUntilAsync(() => HasRunControlStatus(viewModel, ProcessStatusKind.Running));
        viewModel.AbortUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunControlStatus(viewModel, ProcessStatusKind.Aborting));
        Assert.True(viewModel.RunControlStatusViewState.IsAnimated);
        AssertRunMonitorState(viewModel, TestRunMonitoringState.RunActive);

        lifecycle.SimulateExternalAbortCompleted();
        eventStream.PublishRunControlAbortCompleted();
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.Aborted));
        await WaitUntilAsync(() => HasRunControlStatus(viewModel, ProcessStatusKind.Completed));
        await WaitUntilAsync(() => viewModel.TerminalText.Contains("Test run aborted.", StringComparison.Ordinal));
        AssertRunControlStatus(viewModel, ProcessStatusKind.Completed);
        Assert.Contains("Test run aborted.", viewModel.TerminalText, StringComparison.Ordinal);

        Assert.False(viewModel.AbortUnitTestCommand.CanExecute(null));
        Assert.True(viewModel.ResetUnitTestCommand.CanExecute(null));
    }

    [Fact]
    public async Task Event_buffer_reset_acceptance_waits_for_clean_observation_before_enabling_start()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);
        await PrepareFakeProfileForUnitTestRunAsync(viewModel);
        eventStream.PublishLostEventEvidence(lostEventCount: 3);
        await WaitUntilAsync(() => !viewModel.StartUnitTestCommand.CanExecute(null));

        viewModel.ResetEventBufferCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TerminalText.Contains(
            "EventBuffer reset command accepted.",
            StringComparison.Ordinal));

        Assert.Equal(1, eventStream.EventBufferResetCount);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.Equal("active", viewModel.MonitoringStatus);
        Assert.Equal(ProcessStatusKind.Warning, viewModel.MonitoringStatusViewState.Kind);
        Assert.Contains("3 events were lost", viewModel.MonitoringStatusToolTip, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, viewModel.RunResultLostEventsVisibility);

        eventStream.PublishEmptyBuffer();
        await WaitUntilAsync(() => viewModel.StartUnitTestCommand.CanExecute(null));

        Assert.Equal("active", viewModel.MonitoringStatus);
        Assert.Equal(ProcessStatusKind.Active, viewModel.MonitoringStatusViewState.Kind);
    }

    [Fact]
    public async Task Failed_event_buffer_reset_keeps_wpf_start_blocked()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        eventStream.EventBufferResetResult =
            OperationResult<EventBufferResetCommandResult>.Failure(
                new EventBufferResetCommandResult(Accepted: false),
                "Fake EventBuffer reset write failed.");
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);
        await PrepareFakeProfileForUnitTestRunAsync(viewModel);
        eventStream.PublishLostEventEvidence(lostEventCount: 2);
        await WaitUntilAsync(() => !viewModel.StartUnitTestCommand.CanExecute(null));

        viewModel.ResetEventBufferCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TerminalText.Contains(
            "EventBuffer reset failed: Fake EventBuffer reset write failed.",
            StringComparison.Ordinal));

        Assert.Equal(1, eventStream.EventBufferResetCount);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.True(viewModel.ResetEventBufferCommand.CanExecute(null));
    }

    [Fact]
    public async Task Event_buffer_reset_rejected_during_active_run_keeps_current_observation()
    {
        var confirmations = new RejectConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);
        lifecycle.SimulateExternalRunStarted();
        eventStream.PublishRunControlStarted();
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));

        Assert.True(viewModel.ResetEventBufferCommand.CanExecute(null));
        viewModel.ResetEventBufferCommand.Execute(null);
        await WaitUntilAsync(() => confirmations.CallCount == 1);

        Assert.DoesNotContain("Resetting UnitTest EventBuffer...", viewModel.TerminalText, StringComparison.Ordinal);
        AssertRunMonitorState(viewModel, TestRunMonitoringState.RunActive);
    }

    [Fact]
    public async Task Externally_observed_run_start_updates_wpf_status_and_gates_without_start_command()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        lifecycle.SimulateExternalRunStarted();
        eventStream.PublishRunControlStarted();

        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));
        await WaitUntilAsync(() => HasRunControlStatus(viewModel, ProcessStatusKind.Running));

        AssertRunControlStatus(viewModel, ProcessStatusKind.Running);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.True(viewModel.AbortUnitTestCommand.CanExecute(null));
        Assert.True(viewModel.ResetUnitTestCommand.CanExecute(null));
        Assert.Equal("Running test...", viewModel.ActiveOperation);
        Assert.True(viewModel.ActivityIndicatorActive);
        Assert.Single(viewModel.RunResultTree);
        Assert.Equal("RunControl", viewModel.RunResultTree[0].DisplayName);
        Assert.Equal("Running / NotEvaluated / Partial", viewModel.RunResultStatus);
        Assert.Equal(["RunControl"], viewModel.RunResultRows.Select(row => row.DisplayName));
        Assert.DoesNotContain("UnitTest start command accepted", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Externally_observed_run_completion_updates_wpf_result_without_start_command()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        lifecycle.SimulateExternalRunStarted();
        eventStream.PublishRunControlStarted();
        lifecycle.SimulateExternalRunCompleted(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompleted(UnitTestResultState.Passed);

        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.Completed));
        await WaitUntilAsync(() => HasRunControlStatus(viewModel, ProcessStatusKind.Completed));

        Assert.Equal("Completed / Passed / Complete", viewModel.RunResultStatus);
        Assert.Single(viewModel.RunResultTree);
        Assert.Equal("RunControl", viewModel.RunResultTree[0].DisplayName);
        Assert.Equal("Passed", viewModel.RunResultTree[0].Result);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.False(viewModel.AbortUnitTestCommand.CanExecute(null));
        Assert.True(viewModel.ResetUnitTestCommand.CanExecute(null));
        Assert.True(viewModel.ClearRunResultCommand.CanExecute(null));
        Assert.Contains("Test run completed.", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.DoesNotContain("UnitTest start command accepted", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completed_run_without_runcontrol_result_is_shown_as_incomplete_evidence()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        lifecycle.SimulateExternalRunStarted();
        eventStream.PublishRunControlStarted();
        lifecycle.SimulateExternalRunCompleted(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompletedWithoutResult();

        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.Completed));

        Assert.Equal("Completed / Error / Incomplete", viewModel.RunResultStatus);
        Assert.Single(viewModel.RunResultTree);
        Assert.Equal("Incomplete", viewModel.RunResultTree[0].EvidenceState);
        Assert.Equal("NotEvaluated", viewModel.RunResultTree[0].Result);
    }

    [Fact]
    public async Task Externally_observed_abort_updates_wpf_result_without_abort_command()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        lifecycle.SimulateExternalRunStarted();
        eventStream.PublishRunControlStarted();
        lifecycle.SimulateExternalAbortStarted();
        await WaitUntilAsync(() => HasRunControlStatus(viewModel, ProcessStatusKind.Aborting));
        lifecycle.SimulateExternalAbortCompleted();
        eventStream.PublishRunControlAbortCompleted();

        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.Aborted));

        AssertRunControlStatus(viewModel, ProcessStatusKind.Completed);
        Assert.Equal("Aborted / Abort / Complete", viewModel.RunResultStatus);
        Assert.False(viewModel.AbortUnitTestCommand.CanExecute(null));
        Assert.True(viewModel.ResetUnitTestCommand.CanExecute(null));
        Assert.True(viewModel.ClearRunResultCommand.CanExecute(null));
        Assert.Contains("Test run aborted.", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.DoesNotContain("UnitTest abort command accepted", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Second_externally_observed_run_replaces_wpf_run_result_while_monitoring_remains_active()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        lifecycle.SimulateExternalRunStarted();
        eventStream.PublishRunControlStarted();
        lifecycle.SimulateExternalResetStarted();
        eventStream.PublishRunControlResetObserved();
        lifecycle.SimulateExternalResetCompleted();

        await WaitUntilAsync(() => viewModel.RunStatus == "idle"
            && HasRunControlStatus(viewModel, ProcessStatusKind.Ready));

        Assert.Equal("Invalidated / Error / Incomplete", viewModel.RunResultStatus);
        Assert.True(viewModel.StartUnitTestCommand.CanExecute(null));

        var firstEpochRootId = viewModel.RunResultTree[0].Id;
        lifecycle.SimulateExternalRunStarted();
        eventStream.PublishRunControlStarted();

        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));

        Assert.Equal("Running / NotEvaluated / Partial", viewModel.RunResultStatus);
        Assert.Single(viewModel.RunResultTree);
        Assert.NotEqual(firstEpochRootId, viewModel.RunResultTree[0].Id);

        lifecycle.SimulateExternalRunCompleted(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompleted(UnitTestResultState.Passed);

        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.Completed));

        AssertMonitoringStatus(viewModel, ProcessStatusKind.Active);
        AssertRunControlStatus(viewModel, ProcessStatusKind.Completed);
        Assert.Equal("Completed / Passed / Complete", viewModel.RunResultStatus);
        Assert.DoesNotContain(
            viewModel.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.NotObserving");
    }

    [Fact]
    public async Task Fake_profile_auto_completion_can_publish_failed_assertion_result_view()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(
            timeProvider,
            autoCompleteRuns: true,
            FakeUnitTestEvidenceScenario.FailedAssertion);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await RunAutoCompletedFakeTestAsync(viewModel, timeProvider);
        await WaitUntilAsync(() => viewModel.RunResultTree[0].Children[0].Children[0].Findings.Count == 1);

        var root = viewModel.RunResultTree[0];
        var suite = root.Children[0];
        var testCase = suite.Children[0];
        viewModel.SelectedRunResultNode = testCase;

        Assert.Equal("Completed / Failed / Complete", viewModel.RunResultStatus);
        Assert.Equal("Failed", root.Result);
        Assert.Equal("Failed", suite.Result);
        Assert.Equal("27 ms", suite.Runtime);
        Assert.Equal("Failed", testCase.Result);
        var finding = Assert.Single(viewModel.SelectedRunResultFindings);
        Assert.Equal("Assertion", finding.Kind);
        Assert.Equal("Failed", finding.Result);
        Assert.Equal("SampleSuite.SampleCase.ExpectedValue", finding.Name);
        Assert.Equal("Expected 4 but got 5.", finding.Message);
    }

    [Fact]
    public async Task Fake_profile_auto_completion_can_publish_framework_error_result_view()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(
            timeProvider,
            autoCompleteRuns: true,
            FakeUnitTestEvidenceScenario.FrameworkError);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await RunAutoCompletedFakeTestAsync(viewModel, timeProvider);
        await WaitUntilAsync(() => viewModel.Diagnostics.Any(diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.FrameworkEvent"));

        var root = viewModel.RunResultTree[0];
        var suite = Assert.Single(root.Children);

        Assert.Equal("Completed / Error / Complete", viewModel.RunResultStatus);
        Assert.Equal("Error", root.Result);
        Assert.Equal("SampleSuite", suite.DisplayName);
        Assert.Equal("Error", suite.Result);
        Assert.Contains(
            viewModel.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.FrameworkEvent"
                && diagnostic.Message == "Event buffer overflow detected.");
    }

    [Fact]
    public async Task Fake_profile_auto_completion_can_publish_incomplete_result_evidence_view()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(
            timeProvider,
            autoCompleteRuns: true,
            FakeUnitTestEvidenceScenario.IncompleteEvidence);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await RunAutoCompletedFakeTestAsync(viewModel, timeProvider);
        await WaitUntilAsync(() => viewModel.Diagnostics.Any(diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.LostEvents"));

        var root = viewModel.RunResultTree[0];
        var suite = root.Children[0];
        var testCase = suite.Children[0];

        Assert.Equal("Completed / Error / Incomplete", viewModel.RunResultStatus);
        Assert.Equal(Visibility.Visible, viewModel.RunResultLostEventsVisibility);
        Assert.Equal("Lost events: 1", viewModel.RunResultLostEventsText);
        Assert.Contains("result evidence is incomplete", viewModel.RunResultLostEventsToolTip, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Passed", root.Result);
        Assert.Equal("Passed", suite.Result);
        Assert.Equal("Passed", testCase.Result);
        Assert.Equal("Error", root.PresentationResult);
        Assert.Equal("Passed", suite.PresentationResult);
        Assert.Equal("Passed", testCase.PresentationResult);
        Assert.Same(System.Windows.Media.Brushes.Firebrick, root.StatusBrush);
        Assert.Same(System.Windows.Media.Brushes.ForestGreen, suite.StatusBrush);
        Assert.Equal(Visibility.Collapsed, root.EvidenceWarningVisibility);
        Assert.Equal(Visibility.Collapsed, suite.EvidenceWarningVisibility);
        Assert.NotEqual("Written", viewModel.TestResultArtifactStatus);
        Assert.False(viewModel.OpenTestResultArtifactCommand.CanExecute(null));
        Assert.Contains(
            viewModel.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.LostEvents"
                && diagnostic.Message.Contains("lost event", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Activity_display_follows_active_unittest_command_publication()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        Assert.Equal("Idle", viewModel.ActiveOperation);
        Assert.False(viewModel.ActivityIndicatorActive);
        Assert.Equal(Visibility.Collapsed, viewModel.ActivityIndicatorVisibility);

        await LoadFakeProfileStep1Async(viewModel);

        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];
        viewModel.ConfigureStep2Command.Execute(null);
        await ContinueThroughProfileConfigurationAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);

        viewModel.StartPlcCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.StartUnitTestCommand.CanExecute(null));

        lifecycle.HoldNextCommand = true;
        viewModel.StartUnitTestCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ActiveOperation == "Starting test...");

        Assert.Equal("Starting test...", viewModel.ActiveOperation);
        Assert.True(viewModel.ActivityIndicatorActive);
        Assert.Equal(Visibility.Visible, viewModel.ActivityIndicatorVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.CancelCurrentOperationVisibility);
        Assert.False(viewModel.CancelCurrentOperationCommand.CanExecute(null));

        lifecycle.CompletePendingCommandAccepted();
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => viewModel.ActiveOperation == "Running test...");
        Assert.True(viewModel.ActivityIndicatorActive);
    }

    [Fact]
    public async Task Run_result_tree_follows_current_run_snapshot_publication()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        Assert.Empty(viewModel.RunResultTree);
        Assert.Equal("No run captured yet", viewModel.RunResultStatus);
        Assert.Equal("No run node selected", viewModel.SelectedRunResultName);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));

        eventStream.PublishBatch(new UnitTestEventBatch(
            eventStream.CurrentMonitoringState.SessionId,
            [
                new UnitTestEventEntry(
                    SequenceId: 2,
                    Timestamp: timeProvider.GetUtcNow(),
                    UnitTestOwnerScope.TestSuite,
                    UnitTestEventType.StateTransition,
                    LogLevel.Information,
                    "SampleSuite",
                    "Sample suite",
                    UnitTestLifecycleState.Running,
                    UnitTestResultState.NotEvaluated,
                    "Sample suite started."),
                new UnitTestEventEntry(
                    SequenceId: 3,
                    Timestamp: timeProvider.GetUtcNow().AddMilliseconds(1),
                    UnitTestOwnerScope.TestCase,
                    UnitTestEventType.Result,
                    LogLevel.Information,
                    "SampleSuite.SampleCase",
                    "Sample case",
                    UnitTestLifecycleState.Completed,
                    UnitTestResultState.Passed,
                    "Sample case passed.")
            ],
            ProducerSequenceId: 3,
            DeletedSequenceId: 0,
            LostEventCount: 0,
            BufferObservedEmpty: false,
            EventBufferReadState.Valid));

        await WaitUntilAsync(() => viewModel.RunResultTree.Count == 1
            && viewModel.RunResultTree[0].Children.Count == 1
            && viewModel.RunResultTree[0].Children[0].Children.Count == 1);

        var root = viewModel.RunResultTree[0];
        var suite = root.Children[0];
        var testCase = suite.Children[0];
        Assert.Equal("RunControl", root.DisplayName);
        Assert.Equal("SampleSuite", suite.DisplayName);
        Assert.Equal("SampleCase", testCase.DisplayName);
        Assert.Equal("Passed", testCase.Result);
        Assert.Equal("Running / Passed / Partial", viewModel.RunResultStatus);
        Assert.False(suite.IsExpanded);
        Assert.Equal("M 6,4 L 10,8 L 6,12 Z", suite.ExpanderIconData);
        Assert.Equal(["RunControl", "SampleSuite"], viewModel.RunResultRows.Select(row => row.DisplayName));
        Assert.Equal([0, 1], viewModel.RunResultRows.Select(row => row.Level));
        Assert.Equal("RunControl", viewModel.SelectedRunResultName);
        Assert.Equal("1 observed suite; 0 findings; aggregation evidence not available.", viewModel.RunResultSummary);

        viewModel.ToggleRunResultNodeExpansion(suite);

        Assert.True(suite.IsExpanded);
        Assert.Equal("M 4,6 L 8,10 L 12,6 Z", suite.ExpanderIconData);
        Assert.Equal(["RunControl", "SampleSuite", "SampleCase"], viewModel.RunResultRows.Select(row => row.DisplayName));
        Assert.Equal([0, 1, 2], viewModel.RunResultRows.Select(row => row.Level));

        viewModel.ToggleRunResultNodeExpansion(suite);

        Assert.False(suite.IsExpanded);
        Assert.Equal("M 6,4 L 10,8 L 6,12 Z", suite.ExpanderIconData);
        Assert.Equal(["RunControl", "SampleSuite"], viewModel.RunResultRows.Select(row => row.DisplayName));
        Assert.False(viewModel.ClearRunResultCommand.CanExecute(null));
    }

    [Fact]
    public async Task Run_result_detail_shows_assertion_finding_and_failed_status()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));

        eventStream.PublishBatch(new UnitTestEventBatch(
            eventStream.CurrentMonitoringState.SessionId,
            [
                new UnitTestEventEntry(
                    SequenceId: 2,
                    Timestamp: timeProvider.GetUtcNow(),
                    UnitTestOwnerScope.TestCase,
                    UnitTestEventType.Result,
                    LogLevel.Information,
                    "SampleSuite.SampleCase",
                    "SampleCase",
                    UnitTestLifecycleState.Completed,
                    UnitTestResultState.Passed,
                    "Sample case passed."),
                new UnitTestEventEntry(
                    SequenceId: 3,
                    Timestamp: timeProvider.GetUtcNow().AddMilliseconds(1),
                    UnitTestOwnerScope.Assertion,
                    UnitTestEventType.Result,
                    LogLevel.Error,
                    "SampleSuite.SampleCase.ExpectedValue",
                    "ExpectedValue",
                    UnitTestLifecycleState.Completed,
                    UnitTestResultState.Failed,
                    "Expected 4 but got 5.")
            ],
            ProducerSequenceId: 3,
            DeletedSequenceId: 0,
            LostEventCount: 0,
            BufferObservedEmpty: false,
            EventBufferReadState.Valid));

        await WaitUntilAsync(() => viewModel.RunResultTree.Count == 1
            && viewModel.RunResultTree[0].Children.Count == 1
            && viewModel.RunResultTree[0].Children[0].Children.Count == 1
            && viewModel.RunResultTree[0].Children[0].Children[0].Findings.Count == 1);

        var testCase = viewModel.RunResultTree[0].Children[0].Children[0];
        viewModel.SelectedRunResultNode = testCase;

        Assert.Equal("Failed", testCase.Result);
        Assert.Same(Brushes.Firebrick, testCase.StatusBrush);
        Assert.Equal("1 finding(s)", viewModel.SelectedRunResultFindingStatus);
        var finding = Assert.Single(viewModel.SelectedRunResultFindings);
        Assert.Equal("Assertion", finding.Kind);
        Assert.Equal("Failed", finding.Result);
        Assert.Equal("SampleSuite.SampleCase.ExpectedValue", finding.Name);
        Assert.Equal("Expected 4 but got 5.", finding.Message);
        Assert.Equal("event #3", finding.EvidenceReference);
        Assert.Same(Brushes.Firebrick, finding.StatusBrush);
        Assert.Equal("1 finding; aggregation evidence not available.", viewModel.RunResultSummary);
    }

    [Fact]
    public async Task Framework_event_is_reported_as_diagnostic()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));

        eventStream.PublishBatch(new UnitTestEventBatch(
            eventStream.CurrentMonitoringState.SessionId,
            [
                new UnitTestEventEntry(
                    SequenceId: 2,
                    Timestamp: timeProvider.GetUtcNow(),
                    UnitTestOwnerScope.TestSuite,
                    UnitTestEventType.StateTransition,
                    LogLevel.Information,
                    "SampleSuite",
                    "Sample suite",
                    UnitTestLifecycleState.Running,
                    UnitTestResultState.NotEvaluated,
                    "Sample suite started."),
                new UnitTestEventEntry(
                    SequenceId: 3,
                    Timestamp: timeProvider.GetUtcNow().AddMilliseconds(1),
                    UnitTestOwnerScope.Framework,
                    UnitTestEventType.Framework,
                    LogLevel.Error,
                    "Framework.BufferOverflow",
                    "EventBuffer",
                    UnitTestLifecycleState.Running,
                    UnitTestResultState.Error,
                    "Event buffer overflow detected.")
            ],
            ProducerSequenceId: 3,
            DeletedSequenceId: 0,
            LostEventCount: 0,
            BufferObservedEmpty: false,
            EventBufferReadState.Valid));

        await WaitUntilAsync(() => viewModel.Diagnostics.Count == 1);
        var suite = Assert.Single(viewModel.RunResultTree[0].Children);
        viewModel.SelectedRunResultNode = suite;

        var diagnostic = Assert.Single(viewModel.Diagnostics);
        Assert.Equal("Workflow.EventStreamProcessor.FrameworkEvent", diagnostic.Code);
        Assert.Equal("Workflow", diagnostic.Category);
        Assert.Equal("RunObservation", diagnostic.Source);
        Assert.Equal("ExecutionMonitoring", diagnostic.Phase);
        Assert.Equal("Event buffer overflow detected.", diagnostic.Message);
        Assert.Equal("unit-test-event: 3", diagnostic.EvidenceReference);
        Assert.Contains("Run epoch: ", diagnostic.Correlation, StringComparison.Ordinal);
        Assert.Contains("Framework event 'Framework.BufferOverflow'", diagnostic.TechnicalDetail, StringComparison.Ordinal);
        Assert.Equal("SampleSuite", viewModel.SelectedRunResultName);
    }

    [Fact]
    public async Task Access_profile_registration_refresh_failure_is_visible_in_wpf_status_output_and_diagnostics()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var missingDirectory = Path.Combine(Path.GetTempPath(), "plcsim-unittest-missing-profiles", Guid.NewGuid().ToString("N"));
        using var viewModel = CreateViewModel(
            confirmations,
            [],
            timeProvider,
            discoveryOptions: new AccessProfileDiscoveryOptions(missingDirectory, IncludeBuiltInProfiles: false));

        Assert.Empty(viewModel.AvailableAccessProfiles);
        Assert.Null(viewModel.SelectedAccessProfile);
        Assert.False(viewModel.LoadStep1Command.CanExecute(null));
        Assert.False(viewModel.BrowseProjectSourceCommand.CanExecute(null));
        Assert.Equal("No registered access profiles available.", viewModel.Step1Status);
        Assert.Contains("Access profile registry loaded: no registered profiles available.", viewModel.TerminalText, StringComparison.Ordinal);

        const string warningOutput = "Diagnostic Warning: Profile.Discovery.Directory.NotFound";
        var warningCountBeforeRefresh = CountOccurrences(viewModel.TerminalText, warningOutput);

        Assert.True(viewModel.RefreshProfileRegistrationsCommand.CanExecute(null));
        viewModel.RefreshProfileRegistrationsCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.RefreshProfileRegistrationsCommand.CanExecute(null)
            && viewModel.TerminalText.Contains(
                "Access profile registration refresh completed:",
                StringComparison.Ordinal));

        var diagnostic = Assert.Single(viewModel.Diagnostics);
        Assert.Equal("Profile.Discovery.Directory.NotFound", diagnostic.Code);
        Assert.Equal("Configuration", diagnostic.Category);
        Assert.Equal("AccessProfileLoader", diagnostic.Source);
        Assert.Equal("ProfileValidation", diagnostic.Phase);
        Assert.Contains(missingDirectory, diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(warningCountBeforeRefresh + 1, CountOccurrences(viewModel.TerminalText, warningOutput));
    }

    [Fact]
    public async Task Current_profile_registration_keeps_startup_notice_collapsed()
    {
        var confirmations = new AlwaysConfirmDialogService();
        using var viewModel = CreateViewModel(confirmations);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(Visibility.Collapsed, viewModel.ProfileRegistrationNoticeVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.ProfileRegistrationActionVisibility);
        Assert.DoesNotContain("Access profile registration check:", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public void View_model_constructor_does_not_access_profile_registry()
    {
        var timeProvider = FakeTime.Create();
        var registryStore = new ThrowingApplicationProfileRegistryStore();

        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            [FakeAccessProfileFactory.CreatePlugin(timeProvider)],
            timeProvider,
            registryStore: registryStore,
            initialize: false);

        Assert.Equal(0, registryStore.EnsureLoadedCallCount);
        Assert.Empty(viewModel.AvailableAccessProfiles);
    }

    [Fact]
    public async Task Failed_initial_registry_load_is_visible_and_actionable()
    {
        var timeProvider = FakeTime.Create();
        var registryStore = new ThrowingApplicationProfileRegistryStore();
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            [FakeAccessProfileFactory.CreatePlugin(timeProvider)],
            timeProvider,
            registryStore: registryStore,
            initialize: false);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(1, registryStore.EnsureLoadedCallCount);
        Assert.Equal("Access profile registry is unavailable.", viewModel.Step1Status);
        Assert.Equal(Visibility.Visible, viewModel.ProfileRegistrationNoticeVisibility);
        Assert.Equal(Visibility.Visible, viewModel.ProfileRegistrationActionVisibility);
        Assert.Contains(viewModel.Diagnostics, diagnostic =>
            diagnostic.Code == "Wpf.ProfileRegistry.Unavailable");
    }

    [Fact]
    public async Task Startup_registration_warning_is_cleared_after_explicit_refresh_and_recheck()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        SequencedRegistrationService? registrationService = null;
        using var viewModel = CreateViewModel(
            confirmations,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            registrationServiceDecorator: inner => registrationService = new SequencedRegistrationService(
                inner,
                ApplicationProfileRegistrationCheckStatus.RefreshRequired,
                ApplicationProfileRegistrationCheckStatus.Current));

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.NotNull(registrationService);
        Assert.Equal(Visibility.Visible, viewModel.ProfileRegistrationNoticeVisibility);
        Assert.Equal(Visibility.Visible, viewModel.ProfileRegistrationActionVisibility);
        Assert.Contains("no longer current", viewModel.ProfileRegistrationNotice, StringComparison.Ordinal);

        viewModel.RefreshProfileRegistrationsCommand.Execute(null);
        await WaitUntilAsync(() => registrationService.CheckCallCount == 2);

        Assert.Equal(1, registrationService.RefreshAllCallCount);
        Assert.Equal(Visibility.Collapsed, viewModel.ProfileRegistrationNoticeVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.ProfileRegistrationActionVisibility);
        Assert.Single(viewModel.AvailableAccessProfiles);
    }

    [Fact]
    public async Task Failed_startup_registration_check_remains_actionable_without_retrying()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        SequencedRegistrationService? registrationService = null;
        using var viewModel = CreateViewModel(
            confirmations,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            registrationServiceDecorator: inner => registrationService = new SequencedRegistrationService(
                inner,
                ApplicationProfileRegistrationCheckStatus.Failed));

        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.NotNull(registrationService);
        Assert.Equal(1, registrationService.CheckCallCount);
        Assert.Equal(0, registrationService.RefreshAllCallCount);
        Assert.Equal(Visibility.Visible, viewModel.ProfileRegistrationNoticeVisibility);
        Assert.Equal(Visibility.Visible, viewModel.ProfileRegistrationActionVisibility);
        Assert.Contains("could not be checked", viewModel.ProfileRegistrationNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_profile_registrations_command_is_available_only_before_profile_load()
    {
        var confirmations = new AlwaysConfirmDialogService();
        using var viewModel = CreateViewModel(confirmations);

        Assert.True(viewModel.RefreshProfileRegistrationsCommand.CanExecute(null));
        var selectedProfile = Assert.Single(viewModel.AvailableAccessProfiles);
        Assert.Same(selectedProfile, viewModel.SelectedAccessProfile);

        viewModel.RefreshProfileRegistrationsCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TerminalText.Contains("Access profile registration refresh completed:", StringComparison.Ordinal));

        Assert.True(viewModel.RefreshProfileRegistrationsCommand.CanExecute(null));
        Assert.Single(viewModel.AvailableAccessProfiles);
        Assert.Equal(selectedProfile.ProfileId, viewModel.SelectedAccessProfile?.ProfileId);
        Assert.Equal(selectedProfile.ProfileVersion, viewModel.SelectedAccessProfile?.ProfileVersion);

        await LoadFakeProfileStep1Async(viewModel);

        Assert.False(viewModel.RefreshProfileRegistrationsCommand.CanExecute(null));
    }

    [Fact]
    public void Unavailable_profile_registration_is_listed_but_does_not_enable_step1_load()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var plugin = new FakeAccessProfilePlugin(
            (_, creationContext) => FakeAccessProfileFactory.CreateServices(creationContext.TimeProvider),
            (_, _) => Task.FromResult(FakeAccessProfilePlugin.CreateUnavailableRegistrationResult()));
        using var viewModel = CreateViewModel(confirmations, plugin, timeProvider);

        var unavailableProfile = Assert.Single(viewModel.AvailableAccessProfiles);

        Assert.False(unavailableProfile.IsRegistered);
        Assert.Equal(AccessProfileRegistrationState.Unavailable, unavailableProfile.RegistrationState);
        Assert.Contains("Unavailable", unavailableProfile.DisplayName, StringComparison.Ordinal);
        Assert.Same(unavailableProfile, viewModel.SelectedAccessProfile);
        Assert.False(viewModel.BrowseProjectSourceCommand.CanExecute(null));
        Assert.Equal("No registered access profiles available.", viewModel.Step1Status);
        Assert.Contains("Access profile registry loaded: no registered profiles available.", viewModel.TerminalText, StringComparison.Ordinal);

        viewModel.ProjectSource = "fake-project.ap";

        Assert.False(viewModel.LoadStep1Command.CanExecute(null));
    }

    [Fact]
    public async Task Clear_run_result_clears_completed_result_but_is_disabled_for_active_run()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));
        await WaitUntilAsync(() => viewModel.RunResultTree.Count == 1);

        Assert.False(viewModel.ClearRunResultCommand.CanExecute(null));

        lifecycle.SimulateExternalRunCompleted(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompleted(UnitTestResultState.Passed);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.Completed));
        await WaitUntilAsync(() => viewModel.RunResultStatus == "Completed / Passed / Complete"
            && viewModel.ClearRunResultCommand.CanExecute(null));

        viewModel.ClearRunResultCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.RunResultTree.Count == 0
            && viewModel.RunResultStatus == "No run captured yet"
            && viewModel.SelectedRunResultName == "No run node selected"
            && viewModel.TerminalText.Contains("Run result cleared.", StringComparison.Ordinal));

        Assert.Empty(viewModel.RunResultTree);
        Assert.Empty(viewModel.RunResultRows);
        Assert.Equal("No run captured yet", viewModel.RunResultStatus);
        Assert.Equal("No run node selected", viewModel.SelectedRunResultName);
        Assert.False(viewModel.ClearRunResultCommand.CanExecute(null));
        Assert.Contains("Run result cleared.", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_result_is_retained_on_stop_plc_and_cleared_on_start_plc()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider, autoCompleteRuns: true);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await LoadFakeProfileStep1Async(viewModel);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];
        viewModel.ConfigureStep2Command.Execute(null);
        await ContinueThroughProfileConfigurationAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);
        viewModel.StartPlcCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.StartUnitTestCommand.CanExecute(null));

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));
        timeProvider.Advance(TimeSpan.FromMilliseconds(750));
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.Completed));
        await WaitUntilAsync(() => viewModel.RunResultTree.Count == 1);

        viewModel.StopPlcCommand.Execute(null);
        await WaitUntilAsync(() => HasPlcProcessStatus(viewModel, ProcessStatusKind.Stopped)
            && HasCommunicationStatus(viewModel, ProcessStatusKind.Disconnected));

        Assert.Single(viewModel.RunResultTree);
        Assert.Equal("Completed / Passed / Complete", viewModel.RunResultStatus);

        viewModel.StartPlcCommand.Execute(null);
        await WaitUntilAsync(() => HasPlcProcessStatus(viewModel, ProcessStatusKind.Running)
            && HasCommunicationStatus(viewModel, ProcessStatusKind.Connected));

        Assert.Empty(viewModel.RunResultTree);
        Assert.Empty(viewModel.RunResultRows);
        Assert.Equal("No run captured yet", viewModel.RunResultStatus);
    }

    [Fact]
    public async Task External_plc_running_state_updates_wpf_status_and_gates_without_local_start_command()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await LoadFakeProfileStep1Async(viewModel);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];
        viewModel.ConfigureStep2Command.Execute(null);
        await ContinueThroughProfileConfigurationAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);

        AssertPlcProcessStatus(viewModel, ProcessStatusKind.Stopped);
        AssertCommunicationStatus(viewModel, ProcessStatusKind.Disconnected);
        AssertMonitoringStatus(viewModel, ProcessStatusKind.Off);
        AssertRunControlStatus(viewModel, ProcessStatusKind.Disconnected);
        Assert.True(viewModel.StartPlcCommand.CanExecute(null));
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));

        plcControl.SimulateExternalPlcRunning();

        await WaitUntilAsync(() => HasPlcProcessStatus(viewModel, ProcessStatusKind.Running)
            && HasCommunicationStatus(viewModel, ProcessStatusKind.Connected)
            && HasMonitoringStatus(viewModel, ProcessStatusKind.Active));

        AssertPlcProcessStatus(viewModel, ProcessStatusKind.Running);
        AssertCommunicationStatus(viewModel, ProcessStatusKind.Connected);
        AssertMonitoringStatus(viewModel, ProcessStatusKind.Active);
        AssertRunControlStatus(viewModel, ProcessStatusKind.Ready);
        Assert.False(viewModel.StartPlcCommand.CanExecute(null));
        Assert.True(viewModel.StopPlcCommand.CanExecute(null));
        Assert.True(viewModel.StartUnitTestCommand.CanExecute(null));
        AssertPreparationStatus(viewModel, ProcessStatusKind.Transferred);
    }

    [Fact]
    public async Task External_plc_stopped_state_disconnects_wpf_commands_and_retains_completed_result()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider, autoCompleteRuns: true);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await RunAutoCompletedFakeTestAsync(viewModel, timeProvider);
        await WaitUntilAsync(() => viewModel.RunResultTree.Count == 1);

        plcControl.SimulateExternalPlcStopped();

        await WaitUntilAsync(() => HasPlcProcessStatus(viewModel, ProcessStatusKind.Stopped)
            && HasCommunicationStatus(viewModel, ProcessStatusKind.Disconnected));

        AssertMonitoringStatus(viewModel, ProcessStatusKind.Off);
        AssertRunControlStatus(viewModel, ProcessStatusKind.Disconnected);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.False(viewModel.AbortUnitTestCommand.CanExecute(null));
        Assert.False(viewModel.ResetUnitTestCommand.CanExecute(null));
        Assert.True(viewModel.StartPlcCommand.CanExecute(null));
        Assert.Single(viewModel.RunResultTree);
        Assert.Equal("Completed / Passed / Complete", viewModel.RunResultStatus);
        AssertPreparationStatus(viewModel, ProcessStatusKind.Transferred);
    }

    [Fact]
    public async Task External_runtime_context_lost_blocks_unittest_commands_without_marking_preparation_failed()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        AssertPlcProcessStatus(viewModel, ProcessStatusKind.Running);
        AssertCommunicationStatus(viewModel, ProcessStatusKind.Connected);
        AssertMonitoringStatus(viewModel, ProcessStatusKind.Active);
        Assert.True(viewModel.StartUnitTestCommand.CanExecute(null));

        plcControl.SimulateRuntimeContextLost();

        await WaitUntilAsync(() => HasPlcProcessStatus(viewModel, ProcessStatusKind.NotReady)
            && HasCommunicationStatus(viewModel, ProcessStatusKind.Disconnected));

        AssertMonitoringStatus(viewModel, ProcessStatusKind.Off);
        AssertRunControlStatus(viewModel, ProcessStatusKind.Disconnected);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.False(viewModel.AbortUnitTestCommand.CanExecute(null));
        Assert.False(viewModel.ResetUnitTestCommand.CanExecute(null));
        AssertPreparationStatus(viewModel, ProcessStatusKind.Configured);
    }

    [Fact]
    public async Task Pending_start_plc_failure_after_external_stop_leaves_wpf_recoverable()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        plcControl.HoldNextStart = true;
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await ConfigureFakeProfileToStep4Async(viewModel);

        viewModel.StartPlcCommand.Execute(null);
        await WaitUntilAsync(() => plcControl.IsStartPending
            && viewModel.ActiveOperation == "Starting PLC...");

        AssertPlcProcessStatus(viewModel, ProcessStatusKind.Stopped);
        Assert.False(viewModel.StartPlcCommand.CanExecute(null));
        Assert.False(viewModel.StopPlcCommand.CanExecute(null));
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));

        plcControl.SimulateExternalPlcStopped();
        Assert.True(plcControl.IsStartPending);

        plcControl.FailPendingStart("Fake PLC start interrupted.");
        await WaitUntilAsync(() => viewModel.ActiveOperation == "Idle");

        Assert.False(viewModel.ActivityIndicatorActive);
        AssertPlcProcessStatus(viewModel, ProcessStatusKind.Stopped);
        AssertCommunicationStatus(viewModel, ProcessStatusKind.Disconnected);
        AssertMonitoringStatus(viewModel, ProcessStatusKind.Off);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        AssertPreparationStatus(viewModel, ProcessStatusKind.Transferred);
        Assert.Contains("PLC start failed: Fake PLC start interrupted.", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pending_communication_prepare_interruption_does_not_leave_wpf_connecting()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        services.HoldNextPrepareUnitTestCommunication = true;
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await ConfigureFakeProfileToStep4Async(viewModel);

        viewModel.StartPlcCommand.Execute(null);
        await WaitUntilAsync(() => services.IsPrepareUnitTestCommunicationPending);

        AssertCommunicationStatus(viewModel, ProcessStatusKind.Disconnected);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));

        plcControl.SimulateExternalPlcStopped();
        services.FailPendingPrepareUnitTestCommunication("Fake communication prepare interrupted.");
        await WaitUntilAsync(() => viewModel.ActiveOperation == "Idle");

        AssertPlcProcessStatus(viewModel, ProcessStatusKind.Stopped);
        AssertCommunicationStatus(viewModel, ProcessStatusKind.Disconnected);
        AssertMonitoringStatus(viewModel, ProcessStatusKind.Off);
        AssertRunControlStatus(viewModel, ProcessStatusKind.Disconnected);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        AssertPreparationStatus(viewModel, ProcessStatusKind.Transferred);
        await WaitUntilAsync(() => viewModel.Diagnostics.Any(
            diagnostic => diagnostic.Code == "Workflow.Communication.Prepare.Failed"));
        Assert.Contains(
            viewModel.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.Communication.Prepare.Failed"
                && diagnostic.Message == "Fake communication prepare interrupted.");
    }

    [Fact]
    public async Task Pending_start_monitoring_interruption_does_not_leave_wpf_active()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        eventStream.HoldNextStartMonitoring = true;
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await ConfigureFakeProfileToStep4Async(viewModel);

        viewModel.StartPlcCommand.Execute(null);
        await WaitUntilAsync(() => eventStream.IsStartMonitoringPending
            && viewModel.ActiveOperation == "Starting monitor...");

        AssertMonitoringStatus(viewModel, ProcessStatusKind.Starting);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));

        plcControl.SimulateRuntimeContextLost();
        eventStream.FailPendingStartMonitoring("Fake monitoring interrupted.");
        await WaitUntilAsync(() => viewModel.ActiveOperation == "Idle");

        AssertPlcProcessStatus(viewModel, ProcessStatusKind.NotReady);
        AssertCommunicationStatus(viewModel, ProcessStatusKind.Disconnected);
        AssertMonitoringStatus(viewModel, ProcessStatusKind.Off);
        AssertRunControlStatus(viewModel, ProcessStatusKind.Disconnected);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        AssertPreparationStatus(viewModel, ProcessStatusKind.Configured);
        Assert.Contains(
            viewModel.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.EventStream.MonitoringStart.Failed"
                && diagnostic.Message == "Fake monitoring interrupted.");
    }

    [Fact]
    public async Task Pending_stop_plc_after_external_stop_clears_communication_and_keeps_completed_result()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider, autoCompleteRuns: true);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        plcControl.HoldNextStop = true;
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await RunAutoCompletedFakeTestAsync(viewModel, timeProvider);
        await WaitUntilAsync(() => viewModel.RunResultTree.Count == 1);

        viewModel.StopPlcCommand.Execute(null);
        await WaitUntilAsync(() => plcControl.IsStopPending
            && viewModel.ActiveOperation == "Stopping PLC...");

        AssertPlcProcessStatus(viewModel, ProcessStatusKind.Running);
        plcControl.SimulateExternalPlcStopped();
        plcControl.CompletePendingStopAsStopped();
        await WaitUntilAsync(() => viewModel.ActiveOperation == "Idle"
            && HasPlcProcessStatus(viewModel, ProcessStatusKind.Stopped));

        AssertCommunicationStatus(viewModel, ProcessStatusKind.Disconnected);
        AssertMonitoringStatus(viewModel, ProcessStatusKind.Off);
        AssertRunControlStatus(viewModel, ProcessStatusKind.Disconnected);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.Single(viewModel.RunResultTree);
        Assert.Equal("Completed / Passed / Complete", viewModel.RunResultStatus);
    }

    [Fact]
    public async Task Pending_end_session_cleanup_recovers_to_initial_wpf_state()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        eventStream.HoldNextReleaseCommunication = true;
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        viewModel.EndSessionCommand.Execute(null);
        await WaitUntilAsync(() => eventStream.IsReleaseCommunicationPending
            && viewModel.ActiveOperation == "Ending session...");

        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        eventStream.CompletePendingReleaseCommunication();
        await WaitUntilAsync(() => viewModel.Step1PanelVisibility == Visibility.Visible
            && viewModel.ActiveOperation == "Idle");

        Assert.Equal(1, confirmations.CallCount);
        AssertPlcProcessStatus(viewModel, ProcessStatusKind.NotReady);
        AssertCommunicationStatus(viewModel, ProcessStatusKind.Disconnected);
        AssertMonitoringStatus(viewModel, ProcessStatusKind.Off);
        AssertRunControlStatus(viewModel, ProcessStatusKind.NotReady);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
    }

    [Fact]
    public async Task End_session_restores_persisted_project_handling_default()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var applicationSettings = new TestWpfApplicationSettingsService(
            WpfApplicationSettings.Default with { BuildAndDownloadProjectByDefault = true });
        using var viewModel = CreateViewModel(
            confirmations,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            applicationSettings: applicationSettings);

        await LoadFakeProfileStep1Async(viewModel);
        viewModel.BuildAndDownloadProject = false;

        viewModel.EndSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step1PanelVisibility == Visibility.Visible
            && viewModel.ActiveOperation == "Idle");

        Assert.True(viewModel.BuildAndDownloadProject);
        Assert.Equal(ProjectOwnershipMode.ApplicationManaged, viewModel.SelectedProjectOwnershipMode);
    }

    [Fact]
    public async Task Pending_artifact_export_survives_external_plc_stop_without_rewriting_run_result()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider, autoCompleteRuns: true);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var exporter = new HoldingArtifactExporter();
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider,
            artifactExportService: new ArtifactExportService(
                TestCoreServiceContext.Create(timeProvider: timeProvider),
                [exporter]));

        await RunAutoCompletedFakeTestAsync(viewModel, timeProvider);
        await WaitUntilAsync(() => exporter.IsPending
            && viewModel.TestResultArtifactStatus == "Pending");

        Assert.Equal("Completed / Passed / Complete", viewModel.RunResultStatus);
        plcControl.SimulateExternalPlcStopped();
        await WaitUntilAsync(() => HasPlcProcessStatus(viewModel, ProcessStatusKind.Stopped)
            && HasCommunicationStatus(viewModel, ProcessStatusKind.Disconnected));

        Assert.Equal("Pending", viewModel.TestResultArtifactStatus);
        Assert.Equal("Completed / Passed / Complete", viewModel.RunResultStatus);

        exporter.CompleteWritten();
        await WaitUntilAsync(() => viewModel.TestResultArtifactStatus == "Written");

        Assert.Equal("Completed / Passed / Complete", viewModel.RunResultStatus);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
    }

    [Fact]
    public async Task Pending_artifact_export_does_not_restore_preview_after_run_result_is_cleared()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider, autoCompleteRuns: true);
        var exporter = new HoldingArtifactExporter();
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider,
            artifactExportService: new ArtifactExportService(
                TestCoreServiceContext.Create(timeProvider: timeProvider),
                [exporter]));

        await RunAutoCompletedFakeTestAsync(viewModel, timeProvider);
        await WaitUntilAsync(() => exporter.IsPending
            && viewModel.TestResultArtifactStatus == "Pending");

        viewModel.ClearRunResultCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.RunResultTree.Count == 0
            && viewModel.TestResultArtifactPreview == "No run captured yet.");

        exporter.CompleteWritten();
        await Task.Delay(100);

        Assert.Equal("No run captured yet", viewModel.RunResultStatus);
        Assert.Equal("No run captured yet.", viewModel.TestResultArtifactPreview);
        Assert.False(viewModel.OpenTestResultArtifactCommand.CanExecute(null));
    }

    [Fact]
    public async Task Diagnostics_view_preserves_selection_and_exposes_detail_fields()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await LoadFakeProfileStep1Async(viewModel);

        var first = CreateDiagnostic("diag-1", "First diagnostic", timeProvider.GetUtcNow());
        plcControl.ReportDiagnostic(first);
        await WaitUntilAsync(() => viewModel.Diagnostics.Count == 1);

        Assert.Equal(DiagnosticStatusKind.Error, viewModel.DiagnosticStatusKind);
        Assert.Equal("1", viewModel.DiagnosticBadgeText);
        Assert.Equal("First diagnostic", viewModel.Diagnostics[^1].Message);
        Assert.Contains("Diagnostic Error: Workflow.Test.First - First diagnostic", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.NotNull(viewModel.SelectedDiagnostic);
        Assert.Equal("Workflow.Test.First", viewModel.SelectedDiagnostic.Code);
        Assert.Equal("Workflow", viewModel.SelectedDiagnostic.Category);
        Assert.Equal("RuntimeStartup", viewModel.SelectedDiagnostic.Phase);
        Assert.Equal("PlcControlService", viewModel.SelectedDiagnostic.Source);
        Assert.Equal("1", viewModel.SelectedDiagnostic.Occurrences);
        Assert.Equal("runtime-log: fake-runtime.log", viewModel.SelectedDiagnostic.EvidenceReference);
        Assert.Contains("Operation: op-diag-1", viewModel.SelectedDiagnostic.Correlation, StringComparison.Ordinal);
        Assert.Contains("Runtime: runtime-diag-1", viewModel.SelectedDiagnostic.Correlation, StringComparison.Ordinal);
        Assert.Equal("Technical detail for diag-1.", viewModel.SelectedDiagnostic.TechnicalDetail);

        var selectedFirstKey = viewModel.SelectedDiagnostic.Key;
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        plcControl.ReportDiagnostic(CreateDiagnostic("diag-1", "First diagnostic repeated", timeProvider.GetUtcNow()));
        await WaitUntilAsync(() => viewModel.Diagnostics[0].Occurrences == "2");
        Assert.Single(viewModel.Diagnostics);
        Assert.Equal(selectedFirstKey, viewModel.SelectedDiagnostic?.Key);
        Assert.Equal("First diagnostic repeated", viewModel.Diagnostics[0].Message);
        Assert.Contains("Diagnostic Error: Workflow.Test.First - First diagnostic repeated", viewModel.TerminalText, StringComparison.Ordinal);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        plcControl.ReportDiagnostic(CreateDiagnostic("diag-2", "Second diagnostic", timeProvider.GetUtcNow()));
        await WaitUntilAsync(() => viewModel.Diagnostics.Count == 2);
        Assert.Equal(selectedFirstKey, viewModel.SelectedDiagnostic?.Key);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        plcControl.ReportDiagnostic(CreateDiagnostic("diag-1", "First diagnostic seen again", timeProvider.GetUtcNow()));
        await WaitUntilAsync(() => viewModel.Diagnostics.Count == 2
            && viewModel.Diagnostics[1].Message == "First diagnostic seen again");
        Assert.Equal("First diagnostic seen again", viewModel.Diagnostics[^1].Message);
        Assert.Equal("3", viewModel.Diagnostics[1].Occurrences);
        Assert.Equal(selectedFirstKey, viewModel.SelectedDiagnostic?.Key);

        viewModel.SelectedDiagnostic = viewModel.Diagnostics[0];
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        plcControl.ReportDiagnostic(CreateDiagnostic("diag-3", "Third diagnostic", timeProvider.GetUtcNow()));
        await WaitUntilAsync(() => viewModel.Diagnostics.Count == 3);
        Assert.Contains("Operation: op-diag-2", viewModel.SelectedDiagnostic?.Correlation, StringComparison.Ordinal);
        Assert.Equal("Third diagnostic", viewModel.Diagnostics[^1].Message);
    }

    [Fact]
    public async Task Open_application_log_reports_ui_diagnostic_through_shared_sink_when_file_is_unavailable()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var logFileOpenService = new FakeLogFileOpenService(resolvedPath: null);
        var diagnosticSink = new DiagnosticSink();
        using var viewModel = CreateViewModel(confirmations, logFileOpenService, diagnosticSink);

        Assert.True(viewModel.OpenApplicationLogCommand.CanExecute(null));

        viewModel.OpenApplicationLogCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Diagnostics.Count == 1);

        var sinkEntry = Assert.Single(diagnosticSink.Current.Entries);
        Assert.Equal("Wpf.ApplicationLog.Unavailable", sinkEntry.Current.Code);
        Assert.Equal(0, logFileOpenService.OpenCount);
        Assert.Equal("1", viewModel.DiagnosticBadgeText);
        Assert.NotNull(viewModel.SelectedDiagnostic);
        Assert.Equal("Wpf.ApplicationLog.Unavailable", viewModel.SelectedDiagnostic.Code);
        Assert.Equal("Output", viewModel.SelectedDiagnostic.Category);
        Assert.Equal("OutputCoordination", viewModel.SelectedDiagnostic.Phase);
        Assert.Equal("WpfApplication", viewModel.SelectedDiagnostic.Source);
        Assert.Contains("Application log file is not available", viewModel.SelectedDiagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("application-log:", viewModel.SelectedDiagnostic.EvidenceReference, StringComparison.Ordinal);
        Assert.Contains("Diagnostic Warning: Wpf.ApplicationLog.Unavailable", viewModel.TerminalText, StringComparison.Ordinal);

        viewModel.OpenApplicationLogCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Diagnostics[0].Occurrences == "2");

        sinkEntry = Assert.Single(diagnosticSink.Current.Entries);
        Assert.Equal(2, sinkEntry.OccurrenceCount);
        Assert.Single(viewModel.Diagnostics);
        Assert.Equal("2", viewModel.SelectedDiagnostic?.Occurrences);
    }

    [Fact]
    public async Task Open_application_log_uses_configured_log_file_when_available()
    {
        var confirmations = new AlwaysConfirmDialogService();
        const string resolvedLogPath = @"C:\Logs\wpf-20260622.log";
        var logFileOpenService = new FakeLogFileOpenService(resolvedPath: resolvedLogPath);
        using var viewModel = CreateViewModel(confirmations, logFileOpenService);

        viewModel.OpenApplicationLogCommand.Execute(null);
        await WaitUntilAsync(() => logFileOpenService.OpenCount == 1);

        Assert.Equal(resolvedLogPath, logFileOpenService.OpenedPath);
        Assert.Empty(viewModel.Diagnostics);
        Assert.Contains("Application log opened:", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clear_output_command_clears_visible_terminal_history()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var logFileOpenService = new FakeLogFileOpenService(resolvedPath: null);
        using var viewModel = CreateViewModel(confirmations, logFileOpenService);

        Assert.True(viewModel.ClearOutputCommand.CanExecute(null));
        Assert.Contains("Access profile registry loaded:", viewModel.TerminalText, StringComparison.Ordinal);

        viewModel.ClearOutputCommand.Execute(null);
        await WaitUntilAsync(() => string.IsNullOrEmpty(viewModel.TerminalText));

        Assert.False(viewModel.ClearOutputCommand.CanExecute(null));

        viewModel.OpenApplicationLogCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TerminalText.Contains("Diagnostic Warning: Wpf.ApplicationLog.Unavailable", StringComparison.Ordinal));

        Assert.True(viewModel.ClearOutputCommand.CanExecute(null));

        viewModel.ClearOutputCommand.Execute(null);
        await WaitUntilAsync(() => string.IsNullOrEmpty(viewModel.TerminalText));

        Assert.False(viewModel.ClearOutputCommand.CanExecute(null));

        viewModel.OpenApplicationLogCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TerminalText.Contains("Diagnostic Warning: Wpf.ApplicationLog.Unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clear_diagnostics_command_clears_shared_sink_for_wpf_and_workflow_diagnostics()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var diagnosticSink = new DiagnosticSink();
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider,
            new FakeLogFileOpenService(resolvedPath: null),
            diagnosticSink: diagnosticSink);

        Assert.False(viewModel.ClearDiagnosticsCommand.CanExecute(null));
        Assert.Empty(diagnosticSink.Current.Entries);

        viewModel.OpenApplicationLogCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Diagnostics.Count == 1);
        Assert.Contains(diagnosticSink.Current.Entries, diagnostic => diagnostic.Current.Code == "Wpf.ApplicationLog.Unavailable");

        await LoadFakeProfileStep1Async(viewModel);

        plcControl.ReportDiagnostic(CreateDiagnostic("diag-1", "First diagnostic", timeProvider.GetUtcNow()));
        await WaitUntilAsync(() => viewModel.Diagnostics.Count == 2);
        Assert.Contains(diagnosticSink.Current.Entries, diagnostic => diagnostic.Current.Code == "Workflow.Test.First");

        var terminalBeforeClear = viewModel.TerminalText;

        viewModel.ClearDiagnosticsCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Diagnostics.Count == 0);

        Assert.Empty(diagnosticSink.Current.Entries);
        Assert.Equal(DiagnosticStatusKind.Clear, viewModel.DiagnosticStatusKind);
        Assert.Equal("0", viewModel.DiagnosticBadgeText);
        Assert.Equal(terminalBeforeClear, viewModel.TerminalText);
        Assert.Null(viewModel.SelectedDiagnostic);
        Assert.False(viewModel.ClearDiagnosticsCommand.CanExecute(null));

        plcControl.ReportDiagnostic(CreateDiagnostic("diag-2", "Second diagnostic", timeProvider.GetUtcNow()));
        await WaitUntilAsync(() => viewModel.Diagnostics.Count == 1);

        Assert.Equal(DiagnosticStatusKind.Error, viewModel.DiagnosticStatusKind);
        var sinkEntry = Assert.Single(diagnosticSink.Current.Entries);
        Assert.Equal("Workflow.Test.First", sinkEntry.Current.Code);
        Assert.Equal("Second diagnostic", sinkEntry.Current.Message);
        Assert.Equal(1, sinkEntry.OccurrenceCount);
        Assert.Contains("Operation: op-diag-2", viewModel.Diagnostics[0].Correlation, StringComparison.Ordinal);
        Assert.Equal("Second diagnostic", viewModel.Diagnostics[^1].Message);
    }

    [Fact]
    public async Task Clear_actions_after_incomplete_evidence_do_not_change_workflow_state_or_result_authority()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(
            timeProvider,
            autoCompleteRuns: true,
            FakeUnitTestEvidenceScenario.IncompleteEvidence);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await RunAutoCompletedFakeTestAsync(viewModel, timeProvider);
        await WaitUntilAsync(() => viewModel.Diagnostics.Any(
            diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.LostEvents"));

        AssertRunMonitorState(viewModel, TestRunMonitoringState.Completed);
        Assert.Equal("Completed / Error / Incomplete", viewModel.RunResultStatus);
        Assert.Equal(Visibility.Visible, viewModel.RunResultLostEventsVisibility);
        Assert.True(viewModel.ClearOutputCommand.CanExecute(null));
        Assert.True(viewModel.ClearDiagnosticsCommand.CanExecute(null));
        Assert.True(viewModel.ClearRunResultCommand.CanExecute(null));
        var diagnosticCount = viewModel.Diagnostics.Count;

        viewModel.ClearOutputCommand.Execute(null);
        await WaitUntilAsync(() => string.IsNullOrEmpty(viewModel.TerminalText));

        Assert.Equal(diagnosticCount, viewModel.Diagnostics.Count);
        AssertRunMonitorState(viewModel, TestRunMonitoringState.Completed);
        Assert.Equal("Completed / Error / Incomplete", viewModel.RunResultStatus);
        Assert.True(viewModel.ClearRunResultCommand.CanExecute(null));

        viewModel.ClearRunResultCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.RunResultTree.Count == 0
            && viewModel.TerminalText.Contains("Run result cleared.", StringComparison.Ordinal));

        AssertRunMonitorState(viewModel, TestRunMonitoringState.Completed);
        Assert.Equal("No run captured yet", viewModel.RunResultStatus);
        Assert.Equal(Visibility.Collapsed, viewModel.RunResultLostEventsVisibility);
        Assert.False(viewModel.ClearRunResultCommand.CanExecute(null));
        Assert.Equal(diagnosticCount, viewModel.Diagnostics.Count);

        viewModel.ClearDiagnosticsCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Diagnostics.Count == 0);

        Assert.Equal(DiagnosticStatusKind.Clear, viewModel.DiagnosticStatusKind);
        Assert.Equal("0", viewModel.DiagnosticBadgeText);
        AssertRunMonitorState(viewModel, TestRunMonitoringState.Completed);
        Assert.Equal("No run captured yet", viewModel.RunResultStatus);
        Assert.Contains("Run result cleared.", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Return_to_step2_after_incomplete_evidence_releases_operation_context_and_clears_result_presentation()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(
            timeProvider,
            autoCompleteRuns: true,
            FakeUnitTestEvidenceScenario.IncompleteEvidence);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await RunAutoCompletedFakeTestAsync(viewModel, timeProvider);
        await WaitUntilAsync(() => viewModel.Diagnostics.Any(
            diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.LostEvents"));

        viewModel.ReturnToStep2Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step2PanelVisibility == Visibility.Visible
            && viewModel.Step2InputsEnabled);

        Assert.Equal(1, confirmations.CallCount);
        Assert.Equal(Visibility.Collapsed, viewModel.Step4PanelVisibility);
        Assert.Equal("No run captured yet", viewModel.RunResultStatus);
        Assert.Empty(viewModel.RunResultTree);
        AssertMonitoringStatus(viewModel, ProcessStatusKind.Off);
        AssertCommunicationStatus(viewModel, ProcessStatusKind.Disconnected);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.True(viewModel.ConfigureStep2Command.CanExecute(null));
        Assert.Contains(
            viewModel.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.LostEvents");
    }

    [Fact]
    public async Task Fake_profile_smoke_covers_finalizing_and_timeout_monitoring_paths()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));

        lifecycle.CompleteRun(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompleted(UnitTestResultState.Passed, bufferObservedEmpty: false);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.CompletionPending));

        Assert.Equal("Finalizing test...", viewModel.ActiveOperation);
        Assert.True(viewModel.ActivityIndicatorActive);
        Assert.Contains("Test run finalizing. Waiting for final event evidence.", viewModel.TerminalText, StringComparison.Ordinal);

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.TimedOut));

        Assert.Contains("Test run observation timed out.", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.Contains(viewModel.Diagnostics, diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.ObservationTimeout");
    }

    [Fact]
    public async Task Lifecycle_command_timeout_is_visible_as_technical_profile_diagnostic_not_monitor_timeout()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);
        lifecycle.NextCommandResult = OperationResult<TestRunCommandResult>.TimedOut(
            "Fake UnitTest lifecycle command 'Start' acknowledgement timed out.");

        viewModel.StartUnitTestCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Diagnostics.Any(
            diagnostic => diagnostic.Code == "Workflow.AccessProfile.UnitTestLifecycle.StartUnitTest.TechnicalTimeout"));

        Assert.Contains(
            viewModel.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.AccessProfile.UnitTestLifecycle.StartUnitTest.TechnicalTimeout"
                && diagnostic.Message.Contains("acknowledgement timed out", StringComparison.Ordinal));
        Assert.DoesNotContain(
            viewModel.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.UnitTestCommand.StartUnitTest.ServiceFailed");
        Assert.DoesNotContain(
            viewModel.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.ObservationTimeout");
        Assert.Contains(
            "Diagnostic Error: Workflow.AccessProfile.UnitTestLifecycle.StartUnitTest.TechnicalTimeout",
            viewModel.TerminalText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runcontrol_result_without_completion_anchor_keeps_epoch_active_until_run_observation_timeout()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));

        lifecycle.CompleteRun(UnitTestResultState.Passed);
        eventStream.PublishRunControlResultWithoutCompletionAnchor(UnitTestResultState.Passed);
        await WaitUntilAsync(() => viewModel.RunResultStatus == "Running / Passed / Partial");

        Assert.True(HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));
        Assert.Equal("Running test...", viewModel.ActiveOperation);
        Assert.Equal("Running / Passed / Partial", viewModel.RunResultStatus);

        timeProvider.Advance(TimeSpan.FromMinutes(10));
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.TimedOut));

        Assert.Equal("TimedOut / Error / Incomplete", viewModel.RunResultStatus);
        Assert.Contains(viewModel.Diagnostics, diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.ObservationTimeout");
    }

    [Fact]
    public async Task Timeout_monitoring_does_not_mark_completed_preparation_as_failed()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        AssertPreparationStatus(viewModel, ProcessStatusKind.Transferred);

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));

        lifecycle.CompleteRun(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompleted(UnitTestResultState.Passed, bufferObservedEmpty: false);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.CompletionPending));

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.TimedOut));

        AssertPreparationStatus(viewModel, ProcessStatusKind.Transferred);
        Assert.Same(System.Windows.Media.Brushes.ForestGreen, viewModel.RuntimeStatusBrush);
    }

    [Fact]
    public async Task End_session_after_invalidated_evidence_returns_to_initial_state_without_hiding_diagnostics()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));

        eventStream.PublishSessionInvalidated();
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.Invalidated));
        await WaitUntilAsync(() => viewModel.Diagnostics.Any(
            diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.Invalidated"));

        viewModel.EndSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step1PanelVisibility == Visibility.Visible
            && viewModel.ActiveOperation == "Idle");

        Assert.Equal(1, confirmations.CallCount);
        Assert.Equal(Visibility.Collapsed, viewModel.Step2PanelVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.Step4PanelVisibility);
        AssertPlcProcessStatus(viewModel, ProcessStatusKind.NotReady);
        AssertCommunicationStatus(viewModel, ProcessStatusKind.Disconnected);
        AssertMonitoringStatus(viewModel, ProcessStatusKind.Off);
        AssertRunControlStatus(viewModel, ProcessStatusKind.NotReady);
        Assert.Equal("idle", viewModel.RunStatus);
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        Assert.False(viewModel.AbortUnitTestCommand.CanExecute(null));
        Assert.False(viewModel.ResetUnitTestCommand.CanExecute(null));
        Assert.Equal("No run captured yet", viewModel.RunResultStatus);
        Assert.Empty(viewModel.RunResultTree);
        Assert.Contains(
            viewModel.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.Invalidated");
        Assert.Contains("Session ended.", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sequence_gap_is_reported_without_completing_the_run()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));

        eventStream.PublishRunControlStarted();
        eventStream.PublishSequenceGap();

        await WaitUntilAsync(() => viewModel.Diagnostics.Any(
            diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.SequenceGap"));

        AssertRunMonitorState(viewModel, TestRunMonitoringState.RunActive);
        Assert.Equal("Running / NotEvaluated / Partial", viewModel.RunResultStatus);
    }

    [Fact]
    public async Task Aggregation_evidence_updates_run_result_summary_and_invalid_payload_is_visible_as_finding()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));

        eventStream.PublishAggregation(total: 2, passed: 1, failed: 1, runtime: 42);
        await WaitUntilAsync(() => viewModel.RunResultTree.Count == 1
            && viewModel.RunResultTree[0].Runtime == "42 ms");

        var root = viewModel.RunResultTree[0];
        Assert.Equal("42 ms", root.Runtime);
        Assert.Contains("Suites: total 2", root.Summary, StringComparison.Ordinal);
        Assert.Contains("failed 1", root.Summary, StringComparison.Ordinal);
        Assert.Contains("runtime 42 ms", root.Summary, StringComparison.Ordinal);

        eventStream.PublishInvalidAggregation(UnitTestOwnerScope.TestSuite, "SampleSuite");
        await WaitUntilAsync(() => viewModel.RunResultTree[0].Children.Count == 1
            && viewModel.RunResultTree[0].Children[0].Findings.Count == 1);

        var suite = viewModel.RunResultTree[0].Children[0];
        viewModel.SelectedRunResultNode = suite;

        Assert.Equal("SampleSuite", suite.DisplayName);
        Assert.Equal("Conflicting", suite.EvidenceState);
        Assert.Equal("1 finding(s)", viewModel.SelectedRunResultFindingStatus);
        var finding = Assert.Single(viewModel.SelectedRunResultFindings);
        Assert.Equal("Consistency", finding.Kind);
        Assert.Equal("Aggregation payload could not be parsed.", finding.Message);
    }

    [Fact]
    public async Task Fake_profile_smoke_covers_invalidated_monitoring_path()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));

        eventStream.PublishBatch(new UnitTestEventBatch(
            eventStream.CurrentMonitoringState.SessionId,
            Array.Empty<UnitTestEventEntry>(),
            ProducerSequenceId: 0,
            DeletedSequenceId: 0,
            LostEventCount: 0,
            BufferObservedEmpty: false,
            EventBufferReadState.SessionInvalid));
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.Invalidated));

        Assert.Contains("Test run evidence invalidated.", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.Contains(viewModel.Diagnostics, diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.Invalidated");
        AssertPreparationStatus(viewModel, ProcessStatusKind.Transferred);
    }

    [Fact]
    public async Task Analysis_commands_present_run_lifecycle_and_session_reports_in_the_analysis_window()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider, autoCompleteRuns: true);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        Assert.False(viewModel.AnalyzeCurrentRunCommand.CanExecute(null));
        Assert.False(viewModel.AnalyzeLifecycleCommand.CanExecute(null));
        Assert.False(viewModel.AnalyzeCommunicationSessionCommand.CanExecute(null));

        var presentationCount = 0;
        viewModel.AnalysisPresented += (_, _) => presentationCount++;

        await RunAutoCompletedFakeTestAsync(viewModel, timeProvider);
        await WaitUntilAsync(() => viewModel.AnalyzeCurrentRunCommand.CanExecute(null));

        viewModel.AnalyzeCurrentRunCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.AnalysisStatus.StartsWith("Run /", StringComparison.Ordinal));

        Assert.Equal(1, presentationCount);
        Assert.True(viewModel.IsRunAnalysisDisplayed);
        Assert.Equal("RUN ANALYSIS", viewModel.AnalysisTitle);
        Assert.Contains("Analysis generated", viewModel.AnalysisRefreshStatus, StringComparison.Ordinal);
        Assert.Equal("Analysis is current", viewModel.AnalysisFreshnessStatus);
        Assert.StartsWith("# Run Evidence Analysis", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("## Assessment", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("**Healthy**", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("No UnitTest", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("failure, evidence gap", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("## Package Evidence", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.DoesNotContain("# Activity Timeline", viewModel.AnalysisReport, StringComparison.Ordinal);

        viewModel.AnalyzeLifecycleCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.AnalysisStatus.StartsWith("Lifecycle /", StringComparison.Ordinal));

        Assert.Equal(2, presentationCount);
        Assert.True(viewModel.IsLifecycleAnalysisDisplayed);
        Assert.Equal("PLC LIFECYCLE ANALYSIS", viewModel.AnalysisTitle);
        Assert.StartsWith("# Lifecycle Evidence Analysis", viewModel.AnalysisReport, StringComparison.Ordinal);

        viewModel.AnalyzeCommunicationSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.AnalysisStatus.StartsWith("Communication Health /", StringComparison.Ordinal));

        Assert.Equal(3, presentationCount);
        Assert.True(viewModel.IsCommunicationSessionAnalysisDisplayed);
        Assert.Equal("COMMUNICATION HEALTH ANALYSIS", viewModel.AnalysisTitle);
        Assert.Contains("**Scope:** Session", viewModel.AnalysisReport, StringComparison.Ordinal);

        eventStream.PublishSequenceGap();
        await WaitUntilAsync(() => viewModel.Diagnostics.Any(
            diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.SequenceGap"));
        viewModel.RefreshAnalysisFreshnessStatus();

        Assert.Equal("New evidence available", viewModel.AnalysisFreshnessStatus);
    }

    [Fact]
    public async Task Run_analysis_localizes_lost_and_incomplete_evidence_in_compact_preview()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(
            timeProvider,
            autoCompleteRuns: true,
            FakeUnitTestEvidenceScenario.IncompleteEvidence);
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await RunAutoCompletedFakeTestAsync(viewModel, timeProvider);
        await WaitUntilAsync(() => viewModel.AnalyzeCurrentRunCommand.CanExecute(null));

        viewModel.AnalyzeCurrentRunCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.AnalysisStatus.StartsWith("Run / Error", StringComparison.Ordinal));

        Assert.Contains("**Error**", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("The producer reported 1 lost event(s).", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("Incomplete run evidence", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("no retained owner can be factually identified", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.DoesNotContain("TestCase: SampleCase", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("## Technical Details", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("## Package Evidence", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("[Run hierarchy](hierarchy.json)", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("[Ordered activity evidence](events.ndjson)", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.DoesNotContain("# Activity Timeline", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.DoesNotContain("# Run Hierarchy Snapshot", viewModel.AnalysisReport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifecycle_analysis_localizes_reset_reaction_timeout_and_retains_command_handover()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider, autoCompleteRuns: true);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        lifecycle.AutoCompleteResetAfter = null;
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await RunAutoCompletedFakeTestAsync(viewModel, timeProvider);
        viewModel.ResetUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunControlStatus(viewModel, ProcessStatusKind.Resetting));

        timeProvider.Advance(TimeSpan.FromSeconds(11));
        await WaitUntilAsync(() => viewModel.Diagnostics.Any(diagnostic =>
            diagnostic.Code == "Workflow.TestRunMonitor.CommandReactionTimeout"));
        viewModel.SelectedDiagnostic = viewModel.Diagnostics.First(diagnostic =>
            diagnostic.Code == "Workflow.TestRunMonitor.CommandReactionTimeout");

        viewModel.AnalyzeSelectedDiagnosticCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.AnalysisStatus != "No analysis generated.");

        Assert.StartsWith("Lifecycle / Error", viewModel.AnalysisStatus, StringComparison.Ordinal);

        Assert.Contains("**Error**", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("Reset command handover", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("Lifecycle reaction failed", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("Workflow.TestRunMonitor.CommandReactionTimeout", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("expected lifecycle reaction was not observed", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("## Package Evidence", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("events.ndjson", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.DoesNotContain("# Supporting Evidence", viewModel.AnalysisReport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selected_diagnostic_navigation_opens_related_analysis()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await PrepareFakeProfileForUnitTestRunAsync(viewModel);
        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));
        eventStream.PublishRunControlStarted();
        eventStream.PublishSequenceGap();
        await WaitUntilAsync(() => viewModel.Diagnostics.Any(
            diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.SequenceGap"));
        viewModel.SelectedDiagnostic = viewModel.Diagnostics.First(
            diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.SequenceGap");

        Assert.True(viewModel.AnalyzeSelectedDiagnosticCommand.CanExecute(null));
        viewModel.AnalyzeSelectedDiagnosticCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.AnalysisStatus != "No analysis generated.");

        Assert.True(viewModel.IsCommunicationSessionAnalysisDisplayed);
        Assert.Equal("COMMUNICATION HEALTH ANALYSIS", viewModel.AnalysisTitle);
        Assert.StartsWith("# Communication Health Evidence Analysis", viewModel.AnalysisReport, StringComparison.Ordinal);
        Assert.Contains("## Assessment", viewModel.AnalysisReport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analysis_preview_exports_the_exact_retained_package_and_clear_retains_an_active_trace()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider, autoCompleteRuns: true);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var exportPath = Path.Combine(CreateTempDirectory("PLCSIM.UnitTest.Wpf.Analysis"), "analysis.zip");
        var fileDialog = new FakeAnalysisReportFileDialogService(exportPath);
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider,
            analysisReportFileDialogService: fileDialog);

        await RunAutoCompletedFakeTestAsync(viewModel, timeProvider);
        viewModel.AnalyzeCurrentRunCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.CanCopyAnalysisReport);
        var report = viewModel.AnalysisReport;

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        eventStream.PublishSequenceGap();
        await WaitUntilAsync(() => viewModel.Diagnostics.Any(
            diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.SequenceGap"));
        Assert.Equal(report, viewModel.AnalysisReport);

        viewModel.ExportAnalysisCommand.Execute(null);
        await WaitUntilAsync(() => File.Exists(exportPath));

        using (var archive = ZipFile.OpenRead(exportPath))
        {
            Assert.Equal(
                new[] { "analysis.md", "events.ndjson", "hierarchy.json" },
                archive.Entries.Select(static entry => entry.FullName).Order().ToArray());
            var analysisEntry = archive.GetEntry("analysis.md");
            Assert.NotNull(analysisEntry);
            using var reader = new StreamReader(analysisEntry.Open());
            Assert.Equal(report, await reader.ReadToEndAsync());
        }

        Assert.EndsWith(".zip", fileDialog.SuggestedFileName, StringComparison.Ordinal);
        Assert.Contains("Evidence analysis package exported:", viewModel.TerminalText, StringComparison.Ordinal);

        viewModel.ClearAnalysisCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.CanCopyAnalysisReport);

        Assert.Equal("No analysis generated yet.", viewModel.AnalysisReport);
        Assert.Equal("No analysis generated.", viewModel.AnalysisStatus);
        Assert.True(viewModel.AnalyzeCurrentRunCommand.CanExecute(null));
        Assert.Contains("Active activity trace retained.", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clear_analysis_removes_a_completed_session_trace()
    {
        var timeProvider = FakeTime.Create();
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider);

        await ConfigureFakeProfileToStep4Async(viewModel);
        Assert.True(viewModel.AnalyzeCommunicationSessionCommand.CanExecute(null));

        viewModel.EndSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step1PanelVisibility == Visibility.Visible
            && viewModel.ClearAnalysisCommand.CanExecute(null));
        viewModel.ClearAnalysisCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.AnalyzeCommunicationSessionCommand.CanExecute(null));

        Assert.False(viewModel.ClearAnalysisCommand.CanExecute(null));
        Assert.Contains("completed activity trace cleared", viewModel.TerminalText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Completed_fake_run_writes_junit_artifact_and_loads_preview_from_file()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider, autoCompleteRuns: true);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider,
            artifactExportService: new ArtifactExportService(
                TestCoreServiceContext.Create(timeProvider: timeProvider),
                [new JUnitArtifactExporter()]));

        await RunAutoCompletedFakeTestAsync(viewModel, timeProvider);
        await WaitUntilAsync(() => viewModel.TestResultArtifactStatus == "Written");

        Assert.Contains("<testsuites", viewModel.TestResultArtifactPreview, StringComparison.Ordinal);
        Assert.Contains("Artifact JUnitXml written:", viewModel.TerminalText, StringComparison.Ordinal);
        var artifactWrittenLine = Assert.Single(viewModel.TerminalText
            .Split([Environment.NewLine], StringSplitOptions.None)
            .Where(line => line.Contains("Artifact JUnitXml written:", StringComparison.Ordinal)));
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}  Artifact JUnitXml written:", artifactWrittenLine);
        Assert.DoesNotContain("<testsuites", viewModel.TerminalText, StringComparison.Ordinal);

        viewModel.ClearRunResultCommand.Execute(null);

        Assert.Empty(viewModel.RunResultTree);
        Assert.Equal("No run captured yet", viewModel.RunResultStatus);
        Assert.Equal("No run captured yet.", viewModel.TestResultArtifactPreview);
    }

    [Fact]
    public async Task Import_configuration_command_cancel_keeps_current_stepper_state()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var fileDialog = new FakeRunConfigurationFileDialogService(null);
        using var viewModel = CreateViewModel(
            confirmations,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            configurationFileDialogService: fileDialog);

        Assert.True(viewModel.ImportConfigurationFileCommand.CanExecute(null));
        Assert.True(viewModel.LoadUnitTestFileCommand.CanExecute(null));

        viewModel.ImportConfigurationFileCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.TerminalText.Contains("Import Configuration canceled.", StringComparison.Ordinal));
        Assert.Equal(RunConfigurationFileDialogPurpose.ImportConfiguration, fileDialog.LastPurpose);
        Assert.Equal(Visibility.Visible, viewModel.Step1PanelVisibility);
        Assert.Empty(viewModel.Diagnostics);
    }

    [Fact]
    public async Task Load_unittest_activity_can_be_cancelled_from_status_bar()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var fileDialog = new FakeRunConfigurationFileDialogService("held.config.json");
        var fileLoad = new CancellationAwareHoldingConfigurationFileLoadService();
        using var viewModel = CreateViewModel(
            confirmations,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            configurationFileLoadService: fileLoad,
            configurationFileDialogService: fileDialog);

        viewModel.LoadUnitTestFileCommand.Execute(null);
        await fileLoad.WaitUntilStartedAsync();

        Assert.Equal("Loading UnitTest...", viewModel.ActiveOperation);
        Assert.True(viewModel.ActivityIndicatorActive);
        Assert.Equal(Visibility.Visible, viewModel.CancelCurrentOperationVisibility);
        Assert.True(viewModel.CancelCurrentOperationCommand.CanExecute(null));

        viewModel.CancelCurrentOperationCommand.Execute(null);
        await fileLoad.WaitUntilCancellationRequestedAsync();

        Assert.Equal("Cancelling operation...", viewModel.ActiveOperation);
        Assert.Equal(Visibility.Visible, viewModel.CancelCurrentOperationVisibility);
        Assert.False(viewModel.CancelCurrentOperationCommand.CanExecute(null));

        fileLoad.CompleteCancellation();
        await WaitUntilAsync(() => viewModel.CancelCurrentOperationVisibility == Visibility.Collapsed);

        Assert.Equal(Visibility.Visible, viewModel.Step1PanelVisibility);
        Assert.Equal("Load UnitTest canceled.", viewModel.Step1Status);
        Assert.Equal("Idle", viewModel.ActiveOperation);
        Assert.False(viewModel.ActivityIndicatorActive);
        Assert.Contains("Loading UnitTest canceled.", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_session_cancels_active_load_before_cleanup()
    {
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var fileDialog = new FakeRunConfigurationFileDialogService("held.config.json");
        var fileLoad = new CancellationAwareHoldingConfigurationFileLoadService();
        using var viewModel = CreateViewModel(
            confirmations,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            configurationFileLoadService: fileLoad,
            configurationFileDialogService: fileDialog);

        viewModel.LoadUnitTestFileCommand.Execute(null);
        await fileLoad.WaitUntilStartedAsync();

        viewModel.EndSessionCommand.Execute(null);
        await fileLoad.WaitUntilCancellationRequestedAsync();
        Assert.Equal("Cancelling operation...", viewModel.ActiveOperation);

        fileLoad.CompleteCancellation();
        await WaitUntilAsync(() => viewModel.TerminalText.Contains("Session ended.", StringComparison.Ordinal));

        Assert.Equal(Visibility.Visible, viewModel.Step1PanelVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.CancelCurrentOperationVisibility);
        Assert.True(viewModel.EndSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task Application_exit_continues_after_active_operation_cancellation_wait_times_out()
    {
        var timeProvider = FakeTime.Create();
        var fileDialog = new FakeRunConfigurationFileDialogService("held.config.json");
        var fileLoad = new CancellationAwareHoldingConfigurationFileLoadService();
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            configurationFileLoadService: fileLoad,
            configurationFileDialogService: fileDialog);

        viewModel.LoadUnitTestFileCommand.Execute(null);
        await fileLoad.WaitUntilStartedAsync();

        var cleanup = viewModel.CleanupForApplicationExitAsync(CancellationToken.None);
        await fileLoad.WaitUntilCancellationRequestedAsync();

        Assert.False(viewModel.EndSessionCommand.CanExecute(null));
        Assert.Equal("Cancelling operation...", viewModel.ActiveOperation);

        timeProvider.Advance(WpfApplicationSettings.Default.OperationTimeouts.Cleanup);
        await cleanup;

        Assert.Contains(
            viewModel.Diagnostics,
            diagnostic => diagnostic.Code == "Wpf.Cleanup.ActiveOperation.TimedOut");
        Assert.Contains(
            "Ending session before application exit. Releasing workflow resources...",
            viewModel.TerminalText,
            StringComparison.Ordinal);

        fileLoad.CompleteCancellation();
        await WaitUntilAsync(() => viewModel.CancelCurrentOperationVisibility == Visibility.Collapsed);
    }

    [Fact]
    public async Task Cancelled_target_discovery_allows_back_cleanup_and_a_fresh_retry()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        var discoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveryCompletion = new TaskCompletionSource<OperationResult<ProjectTargetDiscoveryResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        project.DiscoverProjectTargetsHandler = async cancellationToken =>
        {
            discoveryStarted.TrySetResult();
            using var registration = cancellationToken.Register(() =>
                discoveryCompletion.TrySetResult(
                    OperationResult<ProjectTargetDiscoveryResult>.Cancelled("Discovery cancelled.")));
            return await discoveryCompletion.Task;
        };
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        viewModel.ProjectSource = "fake-project.ap";
        viewModel.LoadStep1Command.Execute(null);
        await discoveryStarted.Task;

        viewModel.CancelCurrentOperationCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.CancelCurrentOperationVisibility == Visibility.Collapsed);

        Assert.Equal(Visibility.Visible, viewModel.Step2PanelVisibility);
        Assert.False(viewModel.Step2InputsEnabled);
        Assert.True(viewModel.ReturnToStep1Command.CanExecute(null));
        Assert.True(viewModel.EndSessionCommand.CanExecute(null));
        Assert.Equal("Idle", viewModel.ActiveOperation);

        viewModel.ReturnToStep1Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step1PanelVisibility == Visibility.Visible);
        Assert.Null(project.CurrentProject);

        project.DiscoverProjectTargetsHandler = null;
        viewModel.LoadStep1Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step2InputsEnabled);

        Assert.Single(viewModel.ProjectTargets);
        Assert.NotEmpty(viewModel.RuntimeTargets);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];
        Assert.True(viewModel.ConfigureStep2Command.CanExecute(null));
    }

    [Fact]
    public async Task Cancelled_build_resumes_the_same_setup_and_end_session_releases_retained_resources()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        ConfigureNoDiscoveredRuntimeTargets(services);
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildCompletion = new TaskCompletionSource<OperationResult<PlcBuildResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        project.BuildHandler = async cancellationToken =>
        {
            buildStarted.TrySetResult();
            using var registration = cancellationToken.Register(() =>
                buildCompletion.TrySetResult(
                    OperationResult<PlcBuildResult>.Cancelled("Build cancelled.")));
            return await buildCompletion.Task;
        };
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await LoadFakeProfileStep1Async(viewModel);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = Assert.Single(
            viewModel.RuntimeTargets,
            target => target.CreatesOwnedRuntime);
        viewModel.ConfigureStep2Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step3PanelVisibility == Visibility.Visible);

        viewModel.ConfigureProfileCommand.Execute(null);
        await buildStarted.Task;
        viewModel.CancelCurrentOperationCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.CancelCurrentOperationVisibility == Visibility.Collapsed);

        Assert.NotNull(project.CurrentProject);
        Assert.Equal(RuntimeInstanceState.Available, plcControl.CurrentState.InstanceState);
        Assert.True(viewModel.ConfigureProfileCommand.CanExecute(null));
        Assert.Equal("Idle", viewModel.ActiveOperation);

        project.BuildHandler = null;
        viewModel.ConfigureProfileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);

        AssertPreparationStatus(viewModel, ProcessStatusKind.Transferred);
        Assert.Equal(1, project.PrepareProjectCallCount);

        viewModel.EndSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step1PanelVisibility == Visibility.Visible);

        Assert.Null(project.CurrentProject);
        Assert.Equal(RuntimeInstanceState.Unavailable, plcControl.CurrentState.InstanceState);
        Assert.True(viewModel.EndSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancelled_runtime_prepare_reuses_the_prepared_project_on_retry()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var prepareStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepareCompletion = new TaskCompletionSource<OperationResult<PreparedPlcRuntime>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        plcControl.PrepareRuntimeHandler = async cancellationToken =>
        {
            prepareStarted.TrySetResult();
            using var registration = cancellationToken.Register(() =>
                prepareCompletion.TrySetResult(
                    OperationResult<PreparedPlcRuntime>.Cancelled("Runtime preparation cancelled.")));
            return await prepareCompletion.Task;
        };
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);

        await LoadFakeProfileStep1Async(viewModel);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = Assert.Single(
            viewModel.RuntimeTargets,
            target => !target.CreatesOwnedRuntime);
        viewModel.ConfigureStep2Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step3PanelVisibility == Visibility.Visible);

        viewModel.ConfigureProfileCommand.Execute(null);
        await prepareStarted.Task;
        viewModel.CancelCurrentOperationCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.CancelCurrentOperationVisibility == Visibility.Collapsed);

        Assert.NotNull(project.CurrentProject);
        Assert.Equal(RuntimeInstanceState.Unavailable, plcControl.CurrentState.InstanceState);
        Assert.True(viewModel.ConfigureProfileCommand.CanExecute(null));

        plcControl.PrepareRuntimeHandler = null;
        viewModel.ConfigureProfileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);

        Assert.Equal(1, project.PrepareProjectCallCount);
        AssertPreparationStatus(viewModel, ProcessStatusKind.Transferred);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Load_unittest_command_applies_complete_fake_setup_fast_path(bool existingRuntime)
    {
        var path = WriteFakeConfigurationFile(
            projectSource: "fake-project.ap",
            profileId: FakeAccessProfilePlugin.DefaultProfileId,
            profileVersion: FakeAccessProfilePlugin.DefaultProfileVersion,
            runtimeRefId: "",
            provisioningMode: "CreateOwned");
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var fileDialog = new FakeRunConfigurationFileDialogService(path);
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        if (!existingRuntime) ConfigureNoDiscoveredRuntimeTargets(services);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider,
            configurationFileDialogService: fileDialog);

        viewModel.BuildAndDownloadProject = false;
        viewModel.LoadUnitTestFileCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);
        Assert.True(viewModel.BuildAndDownloadProject);
        Assert.Equal(ProjectOwnershipMode.ApplicationManaged, viewModel.SelectedProjectOwnershipMode);
        Assert.Equal(RunConfigurationFileDialogPurpose.LoadUnitTest, fileDialog.LastPurpose);
        Assert.Equal(Visibility.Collapsed, viewModel.Step1PanelVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.Step2PanelVisibility);
        Assert.Equal("fake-project.ap", viewModel.ProjectSource);
        Assert.Equal("fake-project-target", viewModel.SelectedProjectTarget?.Target.Id.Value);
        Assert.True(viewModel.SelectedRuntimeTarget?.CreatesOwnedRuntime);
        Assert.Equal("Runtime setup is ready.", viewModel.Step2Status);
        AssertPlcProcessStatus(viewModel, ProcessStatusKind.Stopped);
        AssertPreparationStatus(viewModel, ProcessStatusKind.Transferred);
        Assert.True(viewModel.StartPlcCommand.CanExecute(null));
        Assert.False(viewModel.StartUnitTestCommand.CanExecute(null));
        var runtimePrepare = Assert.IsType<FakePlcControlService>(services.PlcControl).LastPrepareRuntimeRequest;
        Assert.NotNull(runtimePrepare);
        Assert.Equal(RuntimeProvisioningMode.CreateOwned, runtimePrepare!.ProvisioningMode);
        Assert.Null(runtimePrepare.RuntimeTarget);
        Assert.Equal("fake-project.ap", runtimePrepare.Configuration.ProjectSource);
        Assert.Contains("\"runtimeRef\"", runtimePrepare.Configuration.Values.Json, StringComparison.Ordinal);
        Assert.Empty(viewModel.Diagnostics);
        Assert.Contains("Load UnitTest: setup is ready. Start the PLC when ready.", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_unittest_unsupported_creation_stops_before_build_and_transfer()
    {
        var path = WriteFakeConfigurationFile("fake-project.ap", FakeAccessProfilePlugin.DefaultProfileId,
            FakeAccessProfilePlugin.DefaultProfileVersion, runtimeRefId: "", provisioningMode: null);
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var control = Assert.IsType<FakePlcControlService>(services.PlcControl);
        control.PrepareRuntimeResult = OperationResult<PreparedPlcRuntime>.NotSupported("Creation not supported.");
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        var unexpectedCalls = 0;
        project.BuildHandler = _ => { unexpectedCalls++; throw new InvalidOperationException("Unexpected build"); };
        project.TransferHandler = _ => { unexpectedCalls++; throw new InvalidOperationException("Unexpected transfer"); };
        using var viewModel = CreateViewModel(new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services), timeProvider,
            configurationFileDialogService: new FakeRunConfigurationFileDialogService(path));

        viewModel.LoadUnitTestFileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TerminalText.Contains("UnitTest configuration file loaded and validated.", StringComparison.Ordinal));

        Assert.Equal(RuntimeProvisioningMode.CreateOwned, control.LastPrepareRuntimeRequest!.ProvisioningMode);
        Assert.Equal(Visibility.Visible, viewModel.Step2PanelVisibility);
        Assert.Contains("not supported", viewModel.Step2Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, unexpectedCalls);
    }

    [Theory]
    [InlineData("", 1, false, false)]
    [InlineData("", 2, false, false)]
    [InlineData("", 2, true, true)]
    [InlineData("second-runtime", 2, false, true)]
    [InlineData("fake-runtime-target", 2, true, true)]
    [InlineData("missing-runtime", 2, true, false)]
    [InlineData("fake-runtime-target", 3, true, false)]
    public async Task Load_unittest_discovery_matrix_preserves_intent_and_existing_resources(
        string targetId, int candidateCount, bool hasDefault, bool success)
    {
        var path = WriteFakeConfigurationFile("fake-project.ap", FakeAccessProfilePlugin.DefaultProfileId,
            FakeAccessProfilePlugin.DefaultProfileVersion, runtimeRefId: targetId);
        var original = File.ReadAllText(path);
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var control = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var first = control.DefaultRuntimeTarget;
        var second = new RuntimeTargetRef(new RuntimeTargetId("second-runtime"), "Second runtime", "Second");
        var candidates = candidateCount == 1 ? new[] { first }
            : candidateCount == 2 ? new[] { first, second } : new[] { first, second, first };
        control.DiscoverRuntimeTargetsResult = OperationResult<RuntimeTargetDiscoveryResult>.Success(
            new(candidates, TargetDiscoveryState.CandidatesAvailable, [], hasDefault ? second : null));
        using var viewModel = CreateViewModel(new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services), timeProvider,
            configurationFileDialogService: new FakeRunConfigurationFileDialogService(path));

        viewModel.LoadUnitTestFileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TerminalText.Contains("UnitTest configuration file loaded and validated.", StringComparison.Ordinal));

        Assert.Equal(original, File.ReadAllText(path));
        if (success)
        {
            Assert.Equal(Visibility.Visible, viewModel.Step4PanelVisibility);
            var expected = targetId == first.Id.Value ? first : second;
            Assert.Equal(expected, control.LastPrepareRuntimeRequest!.RuntimeTarget);
            Assert.Equal(RuntimeProvisioningMode.UseExisting, control.LastPrepareRuntimeRequest.ProvisioningMode);
            AssertPreparationStatus(viewModel, ProcessStatusKind.Transferred);
            viewModel.EndSessionCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.TerminalText.Contains("Session ended.", StringComparison.Ordinal));
            Assert.Equal(RuntimeResourceReleaseMode.KeepResource, control.LastReleaseRuntimeRequest!.ReleaseMode);
            Assert.Equal(expected, control.LastReleaseRuntimeRequest.RuntimeTarget);
        }
        else
        {
            Assert.Equal(Visibility.Visible, viewModel.Step2PanelVisibility);
            Assert.Null(viewModel.SelectedRuntimeTarget);
            Assert.Null(control.LastPrepareRuntimeRequest);
            Assert.Contains(viewModel.Diagnostics, item => item.Code == "Wpf.Configuration.RuntimeTarget.NotResolved");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("CreateOwned")]
    public async Task Load_unittest_creation_releases_owned_resource(string? mode)
    {
        var path = WriteFakeConfigurationFile("fake-project.ap", FakeAccessProfilePlugin.DefaultProfileId,
            FakeAccessProfilePlugin.DefaultProfileVersion, runtimeRefId: "", provisioningMode: mode);
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var control = Assert.IsType<FakePlcControlService>(services.PlcControl);
        using var viewModel = CreateViewModel(new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services), timeProvider,
            configurationFileDialogService: new FakeRunConfigurationFileDialogService(path));

        viewModel.LoadUnitTestFileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);
        Assert.Equal(RuntimeProvisioningMode.CreateOwned, control.LastPrepareRuntimeRequest!.ProvisioningMode);
        Assert.Null(control.LastPrepareRuntimeRequest.RuntimeTarget);
        viewModel.EndSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TerminalText.Contains("Session ended.", StringComparison.Ordinal));
        Assert.Equal(RuntimeResourceReleaseMode.DestroyResource, control.LastReleaseRuntimeRequest!.ReleaseMode);
    }

    [Fact]
    public async Task Invalid_import_can_be_corrected_manually_without_rewriting_the_file()
    {
        var path = WriteFakeConfigurationFile("fake-project.ap", FakeAccessProfilePlugin.DefaultProfileId,
            FakeAccessProfilePlugin.DefaultProfileVersion, runtimeRefId: "missing-runtime");
        var original = File.ReadAllText(path);
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var control = Assert.IsType<FakePlcControlService>(services.PlcControl);
        using var viewModel = CreateViewModel(new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services), timeProvider,
            configurationFileDialogService: new FakeRunConfigurationFileDialogService(path));

        viewModel.ImportConfigurationFileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step1Status.StartsWith("Configuration imported.", StringComparison.Ordinal));
        Assert.Null(control.LastPrepareRuntimeRequest);
        viewModel.LoadStep1Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step2InputsEnabled);
        Assert.Null(viewModel.SelectedRuntimeTarget);
        viewModel.SelectedRuntimeTarget = Assert.Single(viewModel.RuntimeTargets, target => !target.CreatesOwnedRuntime);
        viewModel.ConfigureStep2Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step3PanelVisibility == Visibility.Visible);
        viewModel.ConfigureProfileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);
        Assert.Equal(control.DefaultRuntimeTarget, control.LastPrepareRuntimeRequest!.RuntimeTarget);
        Assert.Equal(RuntimeProvisioningMode.UseExisting, control.LastPrepareRuntimeRequest.ProvisioningMode);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("fake-runtime-target")]
    [InlineData("")]
    public async Task Load_unittest_preserves_existing_target_or_profile_default(string targetId)
    {
        var path = WriteFakeConfigurationFile("fake-project.ap", FakeAccessProfilePlugin.DefaultProfileId,
            FakeAccessProfilePlugin.DefaultProfileVersion, runtimeRefId: targetId);
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var control = Assert.IsType<FakePlcControlService>(services.PlcControl);
        using var viewModel = CreateViewModel(new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services), timeProvider,
            configurationFileDialogService: new FakeRunConfigurationFileDialogService(path));

        viewModel.LoadUnitTestFileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);

        Assert.True(viewModel.BuildAndDownloadProject);
        Assert.Equal(RuntimeProvisioningMode.UseExisting, control.LastPrepareRuntimeRequest!.ProvisioningMode);
        Assert.Equal("fake-runtime-target", control.LastPrepareRuntimeRequest.RuntimeTarget!.Id.Value);
        Assert.Empty(viewModel.Diagnostics);
    }

    [Theory]
    [InlineData(null, "fake-runtime-target")]
    [InlineData("CreateOwned", "fake-runtime-target")]
    [InlineData("UseExisting", "missing-runtime")]
    public async Task Load_unittest_rejects_conflicting_or_missing_target_without_preparing(string? mode, string targetId)
    {
        var path = WriteFakeConfigurationFile("fake-project.ap", FakeAccessProfilePlugin.DefaultProfileId,
            FakeAccessProfilePlugin.DefaultProfileVersion, runtimeRefId: targetId, provisioningMode: mode);
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var control = Assert.IsType<FakePlcControlService>(services.PlcControl);
        using var viewModel = CreateViewModel(new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services), timeProvider,
            configurationFileDialogService: new FakeRunConfigurationFileDialogService(path));

        viewModel.LoadUnitTestFileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TerminalText.Contains("UnitTest configuration file loaded and validated.", StringComparison.Ordinal));

        Assert.Equal(Visibility.Visible, viewModel.Step2PanelVisibility);
        Assert.Null(viewModel.SelectedRuntimeTarget);
        Assert.Null(control.LastPrepareRuntimeRequest);
        Assert.Contains(viewModel.Diagnostics, item => item.Code == "Wpf.Configuration.RuntimeTarget.NotResolved");
    }

    [Fact]
    public async Task Load_unittest_uses_explicit_profile_even_when_manual_filter_hid_it()
    {
        var path = WriteFakeConfigurationFile(
            projectSource: "filtered-config-fake-project.ap",
            profileId: FakeAccessProfilePlugin.DefaultProfileId,
            profileVersion: FakeAccessProfilePlugin.DefaultProfileVersion,
            runtimeRefId: "",
            provisioningMode: "CreateOwned");
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var fileDialog = new FakeRunConfigurationFileDialogService(path);
        var projectSourceDialog = new FakeProjectSourceFileDialogService(@"D:\Projects\selected-fake-project.unsupported");
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        ConfigureNoDiscoveredRuntimeTargets(services);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider,
            configurationFileDialogService: fileDialog,
            projectSourceFileDialogService: projectSourceDialog);

        viewModel.BrowseProjectSourceCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.AvailableAccessProfiles.Count == 0);
        Assert.Null(viewModel.SelectedAccessProfile);
        Assert.False(viewModel.LoadStep1Command.CanExecute(null));

        viewModel.LoadUnitTestFileCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);
        Assert.Equal("filtered-config-fake-project.ap", viewModel.ProjectSource);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileId, viewModel.SelectedAccessProfile?.ProfileId);
        Assert.Equal("fake-project-target", viewModel.SelectedProjectTarget?.Target.Id.Value);
        Assert.True(viewModel.SelectedRuntimeTarget?.CreatesOwnedRuntime);
        Assert.DoesNotContain(viewModel.Diagnostics, diagnostic => diagnostic.Code == "Wpf.Configuration.AccessProfile.NotDiscovered");
        Assert.Contains("Load UnitTest: setup is ready. Start the PLC when ready.", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_configuration_command_reports_loader_diagnostics()
    {
        var path = WriteTemporaryConfigurationFile("{");
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var fileDialog = new FakeRunConfigurationFileDialogService(path);
        using var viewModel = CreateViewModel(
            confirmations,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            configurationFileDialogService: fileDialog);

        viewModel.ImportConfigurationFileCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.Diagnostics.Any(
            diagnostic => diagnostic.Code == "Wpf.Configuration.Json.Invalid"));
        Assert.Contains("Import Configuration failed: 1 diagnostic(s).", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.Contains("Diagnostic Error: Wpf.Configuration.Json.Invalid", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_configuration_command_uses_persisted_project_handling_default()
    {
        var path = WriteFakeConfigurationFile(
            projectSource: "imported-fake-project.ap",
            profileId: FakeAccessProfilePlugin.DefaultProfileId,
            profileVersion: FakeAccessProfilePlugin.DefaultProfileVersion);
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var fileDialog = new FakeRunConfigurationFileDialogService(path);
        using var viewModel = CreateViewModel(
            confirmations,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            configurationFileDialogService: fileDialog);

        viewModel.BuildAndDownloadProject = false;
        viewModel.ImportConfigurationFileCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.Step1Status.StartsWith("Configuration imported.", StringComparison.Ordinal));
        Assert.True(viewModel.BuildAndDownloadProject);
        Assert.Equal(ProjectOwnershipMode.ApplicationManaged, viewModel.SelectedProjectOwnershipMode);
        Assert.Equal("imported-fake-project.ap", viewModel.ProjectSource);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileId, viewModel.SelectedAccessProfile?.ProfileId);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileVersion, viewModel.SelectedAccessProfile?.ProfileVersion);
        Assert.Equal(Visibility.Visible, viewModel.Step1PanelVisibility);
        Assert.True(viewModel.LoadStep1Command.CanExecute(null));
    }

    [Fact]
    public async Task Imported_configuration_applies_communication_timeout_overrides()
    {
        var path = WriteFakeConfigurationFile(
            projectSource: "imported-timeouts-fake-project.ap",
            profileId: FakeAccessProfilePlugin.DefaultProfileId,
            profileVersion: FakeAccessProfilePlugin.DefaultProfileVersion,
            timeoutProperties: """
                  "projectPrepare": "00:00:41",
                  "communicationPrepare": "00:00:17",
                  "communicationRevalidation": "00:00:00"
                """);
        var timeProvider = FakeTime.Create();
        var fileDialog = new FakeRunConfigurationFileDialogService(path);
        using var viewModel = CreateViewModel(
            new AlwaysConfirmDialogService(),
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            configurationFileDialogService: fileDialog);

        viewModel.ImportConfigurationFileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step1Status.StartsWith("Configuration imported.", StringComparison.Ordinal));
        viewModel.LoadStep1Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step2PanelVisibility == Visibility.Visible);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];
        viewModel.ConfigureStep2Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step3PanelVisibility == Visibility.Visible);

        Assert.Equal("17", viewModel.CommunicationPrepareTimeoutSeconds);
        Assert.Equal("0", viewModel.CommunicationRevalidationTimeoutSeconds);
    }

    [Fact]
    public async Task Imported_configuration_payload_is_used_for_target_discovery_and_preselection()
    {
        var path = WriteFakeConfigurationFile(
            projectSource: "imported-fake-project.ap",
            profileId: FakeAccessProfilePlugin.DefaultProfileId,
            profileVersion: FakeAccessProfilePlugin.DefaultProfileVersion);
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var fileDialog = new FakeRunConfigurationFileDialogService(path);
        AccessProfileSessionRequest? capturedSessionRequest = null;
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plugin = new FakeAccessProfilePlugin((request, _) =>
        {
            capturedSessionRequest = request;
            return services;
        });
        using var viewModel = CreateViewModel(
            confirmations,
            plugin,
            timeProvider,
            configurationFileDialogService: fileDialog);

        viewModel.ImportConfigurationFileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Step1Status.StartsWith("Configuration imported.", StringComparison.Ordinal));
        viewModel.LoadStep1Command.Execute(null);

        await WaitUntilAsync(() => viewModel.Step2PanelVisibility == Visibility.Visible
            && viewModel.SelectedProjectTarget is not null
            && viewModel.SelectedRuntimeTarget is not null);
        Assert.NotNull(capturedSessionRequest);
        Assert.Equal("imported-fake-project.ap", capturedSessionRequest!.ProjectSource);
        Assert.Equal(ProjectOwnershipMode.ApplicationManaged, capturedSessionRequest.ProjectOwnershipMode);

        var projectDiscoveryRequest = Assert.IsType<FakePlcProjectService>(services.Project).LastDiscoverProjectTargetsRequest;
        var runtimeDiscoveryRequest = Assert.IsType<FakePlcControlService>(services.PlcControl).LastDiscoverRuntimeTargetsRequest;
        Assert.NotNull(projectDiscoveryRequest);
        Assert.NotNull(runtimeDiscoveryRequest);
        Assert.Contains("\"plcRef\"", projectDiscoveryRequest!.Configuration.Values.Json, StringComparison.Ordinal);
        Assert.Contains("\"runtimeRef\"", runtimeDiscoveryRequest!.Configuration.Values.Json, StringComparison.Ordinal);
        Assert.Equal("fake-project-target", viewModel.SelectedProjectTarget!.Target.Id.Value);
        Assert.Equal("fake-runtime-target", viewModel.SelectedRuntimeTarget!.Target?.Id.Value);
        Assert.True(viewModel.ConfigureStep2Command.CanExecute(null));
        Assert.Contains("Import Configuration: preselected available PLC/runtime targets from profile values.", viewModel.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_configuration_replacement_cancel_keeps_existing_import_draft()
    {
        var firstPath = WriteFakeConfigurationFile(
            projectSource: "first-fake-project.ap",
            profileId: FakeAccessProfilePlugin.DefaultProfileId,
            profileVersion: FakeAccessProfilePlugin.DefaultProfileVersion);
        var secondPath = WriteFakeConfigurationFile(
            projectSource: "second-fake-project.ap",
            profileId: FakeAccessProfilePlugin.DefaultProfileId,
            profileVersion: FakeAccessProfilePlugin.DefaultProfileVersion);
        var confirmations = new RejectConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var fileDialog = new QueueRunConfigurationFileDialogService(firstPath, secondPath);
        using var viewModel = CreateViewModel(
            confirmations,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            configurationFileDialogService: fileDialog);

        viewModel.ImportConfigurationFileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ProjectSource == "first-fake-project.ap");

        viewModel.ImportConfigurationFileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TerminalText.Contains(
            "Import Configuration canceled. Existing session or configuration draft kept.",
            StringComparison.Ordinal));

        Assert.Equal(1, confirmations.CallCount);
        Assert.Equal("first-fake-project.ap", viewModel.ProjectSource);
        Assert.Equal(Visibility.Visible, viewModel.Step1PanelVisibility);
        Assert.True(viewModel.LoadStep1Command.CanExecute(null));
    }

    [Fact]
    public async Task Load_unittest_replacement_cancel_keeps_existing_prepared_session()
    {
        var path = WriteFakeConfigurationFile(
            projectSource: "replacement-fake-project.ap",
            profileId: FakeAccessProfilePlugin.DefaultProfileId,
            profileVersion: FakeAccessProfilePlugin.DefaultProfileVersion);
        var confirmations = new RejectConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var fileDialog = new FakeRunConfigurationFileDialogService(path);
        using var viewModel = CreateViewModel(
            confirmations,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            configurationFileDialogService: fileDialog);
        await ConfigureFakeProfileToStep4Async(viewModel);

        viewModel.LoadUnitTestFileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TerminalText.Contains(
            "Load UnitTest canceled. Existing session or configuration draft kept.",
            StringComparison.Ordinal));

        Assert.Equal(1, confirmations.CallCount);
        Assert.Equal(Visibility.Visible, viewModel.Step4PanelVisibility);
        AssertPreparationStatus(viewModel, ProcessStatusKind.Transferred);
        Assert.Equal("fake-project.ap", viewModel.ProjectSource);
        Assert.NotNull(viewModel.SelectedProjectTarget);
        Assert.NotNull(viewModel.SelectedRuntimeTarget);
    }

    [Fact]
    public async Task Import_configuration_reports_when_requested_profile_is_not_discovered()
    {
        var path = WriteFakeConfigurationFile(
            projectSource: "missing-profile-project.ap",
            profileId: "missing-profile",
            profileVersion: null);
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var fileDialog = new FakeRunConfigurationFileDialogService(path);
        using var viewModel = CreateViewModel(
            confirmations,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            configurationFileDialogService: fileDialog);

        viewModel.ImportConfigurationFileCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.Diagnostics.Any(
            diagnostic => diagnostic.Code == "Wpf.Configuration.AccessProfile.NotDiscovered"));
        Assert.Equal("missing-profile-project.ap", viewModel.ProjectSource);
        Assert.Null(viewModel.SelectedAccessProfile);
        Assert.False(viewModel.LoadStep1Command.CanExecute(null));
        Assert.Contains("Configuration loaded, but the requested access profile is not available.", viewModel.Step1Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_unittest_reports_unresolved_targets_without_advancing_to_step3()
    {
        var path = WriteFakeConfigurationFile(
            projectSource: "unresolved-target-project.ap",
            profileId: FakeAccessProfilePlugin.DefaultProfileId,
            profileVersion: FakeAccessProfilePlugin.DefaultProfileVersion,
            plcRefId: "missing-project-target",
            runtimeRefId: "missing-runtime-target");
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var fileDialog = new FakeRunConfigurationFileDialogService(path);
        using var viewModel = CreateViewModel(
            confirmations,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            configurationFileDialogService: fileDialog);

        viewModel.LoadUnitTestFileCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.Diagnostics.Any(
            diagnostic => diagnostic.Code == "Wpf.Configuration.Targets.NotResolved"));
        Assert.Equal(Visibility.Collapsed, viewModel.Step4PanelVisibility);
        Assert.Equal(Visibility.Visible, viewModel.Step2PanelVisibility);
        Assert.Null(viewModel.SelectedProjectTarget);
        Assert.Null(viewModel.SelectedRuntimeTarget);
        Assert.Contains(viewModel.RuntimeTargets, target => target.CreatesOwnedRuntime);
        Assert.Equal("Loaded UnitTest configuration did not resolve all required setup values.", viewModel.Step2Status);
    }

    [Fact]
    public async Task Load_unittest_replacement_confirm_cleans_existing_session_and_applies_new_setup()
    {
        var path = WriteFakeConfigurationFile(
            projectSource: "replacement-fake-project.ap",
            profileId: FakeAccessProfilePlugin.DefaultProfileId,
            profileVersion: FakeAccessProfilePlugin.DefaultProfileVersion,
            runtimeRefId: "",
            provisioningMode: "CreateOwned");
        var confirmations = new AlwaysConfirmDialogService();
        var timeProvider = FakeTime.Create();
        var fileDialog = new FakeRunConfigurationFileDialogService(path);
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        using var viewModel = CreateViewModel(
            confirmations,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider,
            configurationFileDialogService: fileDialog);
        await ConfigureFakeProfileToStep4Async(viewModel);
        ConfigureNoDiscoveredRuntimeTargets(services);

        viewModel.LoadUnitTestFileCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible
            && viewModel.ProjectSource == "replacement-fake-project.ap");
        Assert.Equal(1, confirmations.CallCount);
        Assert.Equal("replacement-fake-project.ap", viewModel.ProjectSource);
        Assert.Equal("fake-project-target", viewModel.SelectedProjectTarget?.Target.Id.Value);
        Assert.True(viewModel.SelectedRuntimeTarget?.CreatesOwnedRuntime);
        Assert.Equal("Runtime setup is ready.", viewModel.Step2Status);
        Assert.Contains("Load UnitTest: releasing current workflow context before loading configuration...", viewModel.TerminalText, StringComparison.Ordinal);
        Assert.Contains("Load UnitTest: setup is ready. Start the PLC when ready.", viewModel.TerminalText, StringComparison.Ordinal);
    }

    private static void ConfigureNoDiscoveredRuntimeTargets(FakeAccessProfileServices services)
    {
        Assert.IsType<FakePlcControlService>(services.PlcControl).DiscoverRuntimeTargetsResult =
            OperationResult<RuntimeTargetDiscoveryResult>.Success(
                new RuntimeTargetDiscoveryResult(
                    [],
                    TargetDiscoveryState.NoCandidates,
                    []));
    }

    private static MainWindowViewModel CreateViewModel(
        IConfirmationDialogService confirmations,
        IProjectSourceFileDialogService? projectSourceFileDialogService = null)
    {
        var timeProvider = FakeTime.Create();
        return CreateViewModel(
            confirmations,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            projectSourceFileDialogService: projectSourceFileDialogService);
    }

    private static MainWindowViewModel CreateViewModel(
        IConfirmationDialogService confirmations,
        ILogFileOpenService logFileOpenService,
        DiagnosticSink? diagnosticSink = null)
    {
        var timeProvider = FakeTime.Create();
        return CreateViewModel(
            confirmations,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            logFileOpenService,
            diagnosticSink: diagnosticSink);
    }

    private static MainWindowViewModel CreateViewModel(
        IConfirmationDialogService confirmations,
        IAccessProfilePlugin plugin,
        TimeProvider timeProvider,
        ILogFileOpenService? logFileOpenService = null,
        ArtifactExportService? artifactExportService = null,
        AccessProfileDiscoveryOptions? discoveryOptions = null,
        IConfigurationFileLoadService? configurationFileLoadService = null,
        IRunConfigurationFileDialogService? configurationFileDialogService = null,
        IProjectSourceFileDialogService? projectSourceFileDialogService = null,
        DiagnosticSink? diagnosticSink = null,
        IWpfApplicationSettingsService? applicationSettings = null,
        IAnalysisReportFileDialogService? analysisReportFileDialogService = null,
        ISafetyProgramPasswordDialogService? safetyProgramPasswordDialogService = null,
        Func<IApplicationProfileRegistrationService, IApplicationProfileRegistrationService>? registrationServiceDecorator = null,
        IApplicationProfileRegistryStore? registryStore = null,
        bool initialize = true,
        ISettingsDialogService? settingsDialogService = null)
    {
        return CreateViewModel(
            confirmations,
            [plugin],
            timeProvider,
            logFileOpenService,
            artifactExportService,
            discoveryOptions,
            configurationFileLoadService,
            configurationFileDialogService,
            projectSourceFileDialogService,
            diagnosticSink,
            applicationSettings,
            analysisReportFileDialogService,
            safetyProgramPasswordDialogService,
            registrationServiceDecorator,
            registryStore,
            initialize,
            settingsDialogService);
    }

    private static MainWindowViewModel CreateViewModel(
        IConfirmationDialogService confirmations,
        IReadOnlyList<IAccessProfilePlugin> plugins,
        TimeProvider timeProvider,
        ILogFileOpenService? logFileOpenService = null,
        ArtifactExportService? artifactExportService = null,
        AccessProfileDiscoveryOptions? discoveryOptions = null,
        IConfigurationFileLoadService? configurationFileLoadService = null,
        IRunConfigurationFileDialogService? configurationFileDialogService = null,
        IProjectSourceFileDialogService? projectSourceFileDialogService = null,
        DiagnosticSink? diagnosticSink = null,
        IWpfApplicationSettingsService? applicationSettings = null,
        IAnalysisReportFileDialogService? analysisReportFileDialogService = null,
        ISafetyProgramPasswordDialogService? safetyProgramPasswordDialogService = null,
        Func<IApplicationProfileRegistrationService, IApplicationProfileRegistrationService>? registrationServiceDecorator = null,
        IApplicationProfileRegistryStore? registryStore = null,
        bool initialize = true,
        ISettingsDialogService? settingsDialogService = null)
    {
        diagnosticSink ??= new DiagnosticSink();
        applicationSettings ??= new TestWpfApplicationSettingsService(
            WpfApplicationSettings.Default with { BuildAndDownloadProjectByDefault = true });
        var effectiveDiscoveryOptions = discoveryOptions ?? new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true);
        var coreContext = TestCoreServiceContext.Create(timeProvider: timeProvider, diagnosticSink: diagnosticSink);
        var pluginLoader = new AccessProfilePluginLoader(coreContext, plugins);
        var setupValidator = new RunSetupValidator(timeProvider);
        var workflow = new RunWorkflowService(
            setupValidator,
            coreContext);
        var accessProfileSessions = new AccessProfileSessionService(
            pluginLoader,
            effectiveDiscoveryOptions,
            coreContext);
        registryStore ??= new FileApplicationProfileRegistryStore(
            coreContext,
            new ApplicationProfileRegistryOptions(Path.Combine(
                CreateTempDirectory("PLCSIM.UnitTest.Wpf.ProfileRegistry"),
                ApplicationProfileRegistryOptions.RegistryFileName)),
            new AccessProfileCapabilityValidator());
        var registrationService = new ApplicationProfileRegistrationService(
            pluginLoader,
            effectiveDiscoveryOptions,
            registryStore,
            new AccessProfileCapabilityValidator(),
            coreContext);
        if (initialize)
        {
            registrationService.RefreshAllProfileRegistrationsAsync(TimeSpan.FromMinutes(3), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        var effectiveRegistrationService = registrationServiceDecorator?.Invoke(registrationService)
            ?? registrationService;
        var selectionService = new ApplicationProfileSelectionService(
            pluginLoader,
            effectiveDiscoveryOptions,
            registryStore,
            coreContext);
        artifactExportService ??= new ArtifactExportService(
            coreContext,
            [new JUnitArtifactExporter()]);
        workflow.PublicationEventHandler += artifactExportService.OnWorkflowPublication;
        configurationFileLoadService ??= new ConfigurationFileLoadService(
            setupValidator,
            timeProvider,
            coreContext.Diagnostics.Writer,
            NullLogger<ConfigurationFileLoadService>.Instance);

        var viewModel = new MainWindowViewModel(
            workflow,
            new RunSessionReleaseService(workflow, accessProfileSessions, coreContext),
            accessProfileSessions,
            registryStore,
            effectiveRegistrationService,
            selectionService,
            artifactExportService,
            confirmations,
            NullLogger<MainWindowViewModel>.Instance,
            logFileOpenService,
            configurationFileLoadService,
            configurationFileDialogService,
            projectSourceFileDialogService,
            settingsDialogService: settingsDialogService,
            applicationSettings: applicationSettings,
            diagnostics: coreContext.Diagnostics,
            timeProvider: timeProvider,
            analysisReportFileDialogService: analysisReportFileDialogService,
            safetyProgramPasswordDialogService: safetyProgramPasswordDialogService);

        if (initialize)
        {
            Task.Run(() => viewModel.InitializeAsync(CancellationToken.None)).GetAwaiter().GetResult();
        }

        return viewModel;
    }

    private sealed class ThrowingApplicationProfileRegistryStore : IApplicationProfileRegistryStore
    {
        public int EnsureLoadedCallCount { get; private set; }

        public Task<ApplicationProfileRegistry> EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLoadedCallCount++;
            throw new UnauthorizedAccessException("Test registry path is unavailable.");
        }

        public Task<ApplicationProfileRegistry> LoadAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task SaveAsync(ApplicationProfileRegistry registry, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class SequencedRegistrationService : IApplicationProfileRegistrationService
    {
        private readonly IApplicationProfileRegistrationService inner;
        private readonly Queue<ApplicationProfileRegistrationCheckStatus> checkStatuses;
        private ApplicationProfileRegistrationCheckStatus lastCheckStatus;

        public SequencedRegistrationService(
            IApplicationProfileRegistrationService inner,
            params ApplicationProfileRegistrationCheckStatus[] checkStatuses)
        {
            this.inner = inner;
            this.checkStatuses = new Queue<ApplicationProfileRegistrationCheckStatus>(checkStatuses);
            lastCheckStatus = checkStatuses.Last();
        }

        public int CheckCallCount { get; private set; }

        public int RefreshAllCallCount { get; private set; }

        public Task<ApplicationProfileRegistrationCheckResult> CheckProfileRegistrationsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckCallCount++;
            if (checkStatuses.Count > 0)
            {
                lastCheckStatus = checkStatuses.Dequeue();
            }

            return Task.FromResult(new ApplicationProfileRegistrationCheckResult(
                lastCheckStatus,
                $"Registration check returned {lastCheckStatus}.",
                []));
        }

        public Task<ApplicationProfileRegistrationRefreshResult> RefreshProfileRegistrationAsync(
            AccessProfileCandidateKey profile,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return inner.RefreshProfileRegistrationAsync(profile, timeout, cancellationToken);
        }

        public Task<ApplicationProfileRegistrationRefreshAllResult> RefreshAllProfileRegistrationsAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            RefreshAllCallCount++;
            return inner.RefreshAllProfileRegistrationsAsync(timeout, cancellationToken);
        }
    }

    private static async Task PrepareFakeProfileForUnitTestRunAsync(MainWindowViewModel viewModel)
    {
        await ConfigureFakeProfileToStep4Async(viewModel);
        viewModel.StartPlcCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.StartUnitTestCommand.CanExecute(null));
    }

    private static async Task ConfigureFakeProfileToStep4Async(MainWindowViewModel viewModel)
    {
        await LoadFakeProfileStep1Async(viewModel);
        viewModel.SelectedProjectTarget = viewModel.ProjectTargets[0];
        viewModel.SelectedRuntimeTarget = viewModel.RuntimeTargets[0];
        viewModel.ConfigureStep2Command.Execute(null);
        await ContinueThroughProfileConfigurationAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Step4PanelVisibility == Visibility.Visible);
    }

    private static async Task ContinueThroughProfileConfigurationAsync(MainWindowViewModel viewModel)
    {
        await WaitUntilAsync(() => viewModel.Step3PanelVisibility == Visibility.Visible);
        Assert.True(viewModel.ConfigureProfileCommand.CanExecute(null));
        viewModel.ConfigureProfileCommand.Execute(null);
    }

    private static async Task LoadFakeProfileStep1Async(MainWindowViewModel viewModel)
    {
        viewModel.ProjectSource = "fake-project.ap";
        viewModel.LoadStep1Command.Execute(null);
        await WaitUntilAsync(() => viewModel.Step2PanelVisibility == Visibility.Visible);
    }

    private static async Task RunAutoCompletedFakeTestAsync(
        MainWindowViewModel viewModel,
        FakeTimeProvider timeProvider)
    {
        await PrepareFakeProfileForUnitTestRunAsync(viewModel);

        viewModel.StartUnitTestCommand.Execute(null);
        AdvanceFakeLifecycleObservation(timeProvider);
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.RunActive));
        timeProvider.Advance(TimeSpan.FromMilliseconds(750));
        await WaitUntilAsync(() => HasRunMonitorState(viewModel, TestRunMonitoringState.Completed));
        await WaitUntilAsync(() => viewModel.RunResultTree.Count == 1);
    }

    private static void AdvanceFakeLifecycleObservation(FakeTimeProvider timeProvider)
    {
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
    }

    private static void AssertPreparationStatus(
        MainWindowViewModel viewModel,
        ProcessStatusKind expected)
    {
        Assert.Equal(expected, viewModel.PreparationStatusViewState.Kind);
    }

    private static void AssertPlcProcessStatus(
        MainWindowViewModel viewModel,
        ProcessStatusKind expected)
    {
        Assert.Equal(expected, viewModel.PlcProcessStatusViewState.Kind);
    }

    private static void AssertCommunicationStatus(
        MainWindowViewModel viewModel,
        ProcessStatusKind expected)
    {
        Assert.Equal(expected, viewModel.CommunicationStatusViewState.Kind);
    }

    private static void AssertMonitoringStatus(
        MainWindowViewModel viewModel,
        ProcessStatusKind expected)
    {
        Assert.Equal(expected, viewModel.MonitoringStatusViewState.Kind);
    }

    private static void AssertRunControlStatus(
        MainWindowViewModel viewModel,
        ProcessStatusKind expected)
    {
        Assert.Equal(expected, viewModel.RunControlStatusViewState.Kind);
    }

    private static void AssertRunMonitorState(
        MainWindowViewModel viewModel,
        TestRunMonitoringState expected)
    {
        Assert.Equal(expected, viewModel.RunMonitorState);
    }

    private static bool HasPlcProcessStatus(
        MainWindowViewModel viewModel,
        ProcessStatusKind expected)
    {
        return viewModel.PlcProcessStatusViewState.Kind == expected;
    }

    private static bool HasCommunicationStatus(
        MainWindowViewModel viewModel,
        ProcessStatusKind expected)
    {
        return viewModel.CommunicationStatusViewState.Kind == expected;
    }

    private static bool HasMonitoringStatus(
        MainWindowViewModel viewModel,
        ProcessStatusKind expected)
    {
        return viewModel.MonitoringStatusViewState.Kind == expected;
    }

    private static bool HasRunControlStatus(
        MainWindowViewModel viewModel,
        ProcessStatusKind expected)
    {
        return viewModel.RunControlStatusViewState.Kind == expected;
    }

    private static bool HasRunMonitorState(
        MainWindowViewModel viewModel,
        TestRunMonitoringState expected)
    {
        return viewModel.RunMonitorState == expected;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, timeout.Token);
        }
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string WriteTemporaryConfigurationFile(string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest.Wpf.ConfigurationCommands");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.config.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static string CreateTempDirectory(string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryPackageDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                CollectUnloadablePackageContexts();
            }
            catch (IOException)
            {
                CollectUnloadablePackageContexts();
            }
        }
    }

    private static void CollectUnloadablePackageContexts()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void WriteFakeProfilePackage(string root)
    {
        var packageDirectory = Path.Combine(root, "fake");
        Directory.CreateDirectory(packageDirectory);
        var assemblyFileName = Path.GetFileName(typeof(FakeAccessProfilePlugin).Assembly.Location);
        File.Copy(typeof(FakeAccessProfilePlugin).Assembly.Location, Path.Combine(packageDirectory, assemblyFileName));
        File.WriteAllText(
            Path.Combine(packageDirectory, "manifest.json"),
            $$"""
            {
              "schemaVersion": 1,
              "profile": {
                "id": "{{FakeAccessProfilePlugin.DefaultProfileId}}",
                "version": "{{FakeAccessProfilePlugin.DefaultProfileVersion}}",
                "displayName": "{{FakeAccessProfilePlugin.DefaultDisplayName}}",
                "description": "{{FakeAccessProfilePlugin.DefaultDescription}}",
                "contractVersion": "2.0.0"
              },
              "entryPoint": {
                "assembly": "{{assemblyFileName}}",
                "type": "{{typeof(FakeAccessProfilePlugin).FullName}}"
              }
            }
            """);
    }

    private static string GetRepositoryPath(params string[] pathParts)
    {
        return Path.Combine(FindRepositoryRoot(), Path.Combine(pathParts));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "access-profiles"))
                && Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root could not be located.");
    }

    private static string WriteFakeConfigurationFile(
        string projectSource,
        string profileId,
        string? profileVersion,
        string plcRefId = "fake-project-target",
        string runtimeRefId = "fake-runtime-target",
        string? timeoutProperties = null,
        string? provisioningMode = "UseExisting")
    {
        var versionProperty = string.IsNullOrWhiteSpace(profileVersion)
            ? string.Empty
            : $"""
                "version": "{profileVersion}",
            """;
        var executionProperty = string.IsNullOrWhiteSpace(timeoutProperties)
            ? string.Empty
            : $$"""
            ,
              "execution": {
                "timeouts": {
            {{timeoutProperties}}
                }
              }
            """;
        return WriteTemporaryConfigurationFile(
            $$"""
            {
              "version": 2,
              "runtime": { {{(provisioningMode is null ? "" : $"\"provisioningMode\": \"{provisioningMode}\"")}} },
              "project": {
                "source": "{{projectSource}}"
              },
              "accessProfile": {
                "id": "{{profileId}}",
            {{versionProperty}}
                "values": {
                  "plcRef": {
                    "id": "{{plcRefId}}"
                  },
                  "runtimeRef": {
                    {{(string.IsNullOrEmpty(runtimeRefId) ? "" : $"\"id\": \"{runtimeRefId}\"")}}
                  }
                }
              }
            {{executionProperty}}
            }
            """);
    }

    private static Diagnostic CreateDiagnostic(string id, string message, DateTimeOffset timestamp)
    {
        return new Diagnostic(
            "Workflow.Test.First",
            DiagnosticCategory.Workflow,
            LogLevel.Error,
            DiagnosticSource.PlcControlService,
            DiagnosticPhase.RuntimeStartup,
            message,
            new DiagnosticCorrelation(
                OperationId: new OperationId($"op-{id}"),
                RuntimeTargetId: new RuntimeTargetId($"runtime-{id}")),
            timestamp,
            Id: id,
            TechnicalDetail: $"Technical detail for {id}.",
            EvidenceReference: new DiagnosticEvidenceReference("runtime-log", "fake-runtime.log"));
    }

    private sealed class TestWpfApplicationSettingsService : IWpfApplicationSettingsService
    {
        public TestWpfApplicationSettingsService(WpfApplicationSettings current)
        {
            Current = current;
        }

        public WpfApplicationSettings Current { get; private set; }

        public event EventHandler? SettingsChanged;

        public void Save(WpfApplicationSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class AlwaysConfirmDialogService : IConfirmationDialogService
    {
        public int CallCount { get; private set; }

        public string? LastTitle { get; private set; }

        public string? LastMessage { get; private set; }

        public bool Confirm(string title, string message)
        {
            CallCount++;
            LastTitle = title;
            LastMessage = message;
            return true;
        }
    }

    private sealed class RejectConfirmDialogService : IConfirmationDialogService
    {
        public int CallCount { get; private set; }

        public bool Confirm(string title, string message)
        {
            CallCount++;
            return false;
        }
    }

    private sealed class QueueSafetyProgramPasswordDialogService : ISafetyProgramPasswordDialogService
    {
        private readonly Queue<string?> passwords;

        public QueueSafetyProgramPasswordDialogService(params string?[] passwords)
        {
            this.passwords = new Queue<string?>(passwords);
        }

        public int CallCount { get; private set; }

        public SecureString? LastIssuedPassword { get; private set; }

        public SecureString? RequestPassword(string projectTargetDisplayName)
        {
            CallCount++;
            var value = passwords.Count > 0 ? passwords.Dequeue() : null;
            if (value is null)
            {
                return null;
            }

            var password = new SecureString();
            foreach (var character in value)
            {
                password.AppendChar(character);
            }

            password.MakeReadOnly();
            LastIssuedPassword = password;
            return password;
        }
    }

    private sealed class FakeLogFileOpenService : ILogFileOpenService
    {
        private readonly string? resolvedPath;

        public FakeLogFileOpenService(string? resolvedPath)
        {
            this.resolvedPath = resolvedPath;
        }

        public int OpenCount { get; private set; }

        public string? OpenedPath { get; private set; }

        public string? ResolveExistingLogFilePath(string configuredPath)
        {
            return resolvedPath;
        }

        public void Open(string path)
        {
            OpenCount++;
            OpenedPath = path;
        }
    }

    private sealed class FakeRunConfigurationFileDialogService : IRunConfigurationFileDialogService
    {
        private readonly string? selectedPath;

        public FakeRunConfigurationFileDialogService(string? selectedPath)
        {
            this.selectedPath = selectedPath;
        }

        public RunConfigurationFileDialogPurpose? LastPurpose { get; private set; }

        public string? SelectConfigurationFile(RunConfigurationFileDialogPurpose purpose)
        {
            LastPurpose = purpose;
            return selectedPath;
        }
    }

    private sealed class FakeProjectSourceFileDialogService : IProjectSourceFileDialogService
    {
        private readonly string? selectedPath;

        public FakeProjectSourceFileDialogService(string? selectedPath)
        {
            this.selectedPath = selectedPath;
        }

        public int CallCount { get; private set; }

        public string? LastFilter { get; private set; }

        public string? SelectProjectSourceFile(string filter)
        {
            CallCount++;
            LastFilter = filter;
            return selectedPath;
        }
    }

    private sealed class FakeAnalysisReportFileDialogService(string? selectedPath) : IAnalysisReportFileDialogService
    {
        public string? SuggestedFileName { get; private set; }

        public string? SelectPackageExportPath(string suggestedFileName)
        {
            SuggestedFileName = suggestedFileName;
            return selectedPath;
        }
    }

    private sealed class QueueRunConfigurationFileDialogService : IRunConfigurationFileDialogService
    {
        private readonly Queue<string?> selectedPaths;

        public QueueRunConfigurationFileDialogService(params string?[] selectedPaths)
        {
            this.selectedPaths = new Queue<string?>(selectedPaths);
        }

        public string? SelectConfigurationFile(RunConfigurationFileDialogPurpose purpose)
        {
            return selectedPaths.Count == 0
                ? null
                : selectedPaths.Dequeue();
        }
    }

    private sealed class HoldingArtifactExporter : IArtifactExporter
    {
        private TaskCompletionSource<ExportResult>? pendingExport;
        private ExportRequest? pendingRequest;

        public ArtifactFormat Format => ArtifactFormat.JUnitXml;

        public bool IsPending => pendingExport is not null;

        public event EventHandler<DiagnosticEventArgs> DiagnosticEventHandler
        {
            add { }
            remove { }
        }

        public Task<ExportResult> ExportAsync(
            ExportRequest request,
            UnitTestRunEpochSnapshot runEpoch,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pendingExport is not null)
            {
                return Task.FromResult(new ExportResult(request.Format, ArtifactState.Failed, request.OutputPath));
            }

            pendingRequest = request;
            pendingExport = new TaskCompletionSource<ExportResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return pendingExport.Task;
        }

        public void CompleteWritten()
        {
            var completion = pendingExport
                ?? throw new InvalidOperationException("No fake artifact export is pending.");
            var request = pendingRequest
                ?? throw new InvalidOperationException("No fake artifact export request is pending.");

            pendingExport = null;
            pendingRequest = null;
            completion.SetResult(new ExportResult(request.Format, ArtifactState.Written, request.OutputPath));
        }
    }

    private sealed class CancellationAwareHoldingConfigurationFileLoadService : IConfigurationFileLoadService
    {
        private readonly TaskCompletionSource started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource cancellationRequested = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ConfigurationFileLoadResult> LoadAsync(
            string path,
            CancellationToken cancellationToken)
        {
            started.TrySetResult();
            using var registration = cancellationToken.Register(
                () => cancellationRequested.TrySetResult());
            await release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The holding configuration load should be cancelled.");
        }

        public Task WaitUntilStartedAsync()
        {
            return started.Task;
        }

        public Task WaitUntilCancellationRequestedAsync()
        {
            return cancellationRequested.Task;
        }

        public void CompleteCancellation()
        {
            release.TrySetResult();
        }
    }

    private sealed class FirstCleanupCancellationHoldingEventStreamService : FakeUnitTestEventStreamService
    {
        private readonly TaskCompletionSource firstCleanupStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int cleanupCallCount;

        public override async Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref cleanupCallCount) == 1)
            {
                firstCleanupStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return OperationResult.Success();
        }

        public Task WaitUntilFirstCleanupStartedAsync()
        {
            return firstCleanupStarted.Task;
        }
    }

}
