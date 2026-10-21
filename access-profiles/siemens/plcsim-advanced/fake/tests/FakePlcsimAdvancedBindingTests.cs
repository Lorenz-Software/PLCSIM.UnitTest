using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.Fake;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.UnitTests;
using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.Fake;

public sealed class FakePlcsimAdvancedBindingTests
{
    [Fact]
    public void Fake_binding_manifest_matches_binding_metadata_and_entry_point()
    {
        var binding = new FakePlcsimAdvancedBinding();
        using var document = JsonDocument.Parse(File.ReadAllText(GetFakeBindingManifestPath()));

        var root = document.RootElement;
        Assert.Equal(SiemensBindingPackageDescriptorContract.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.IsAssignableFrom<IPlcsimAdvancedRuntimeBinding>(binding);
        Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationBinding>(binding);

        var info = root.GetProperty("info");
        Assert.Equal(binding.Info.BindingKind, info.GetProperty("bindingKind").GetString());
        Assert.Equal(binding.Info.BindingVersion, info.GetProperty("bindingVersion").GetString());
        Assert.Equal(binding.Info.DisplayName, info.GetProperty("displayName").GetString());

        var entryPoint = root.GetProperty("entryPoint");
        Assert.Equal(Path.GetFileName(typeof(FakePlcsimAdvancedBinding).Assembly.Location), entryPoint.GetProperty("assembly").GetString());
        Assert.Equal(typeof(FakePlcsimAdvancedBinding).FullName, entryPoint.GetProperty("typeName").GetString());
        Assert.Same(
            typeof(FakePlcsimAdvancedBinding),
            typeof(FakePlcsimAdvancedBinding).Assembly.GetType(entryPoint.GetProperty("typeName").GetString()!));

        Assert.Empty(root
            .GetProperty("declaration")
            .GetProperty("projectSources")
            .EnumerateArray());
        var declaration = root.GetProperty("declaration");
        var accessApi = Assert.Single(declaration.GetProperty("accessApis").EnumerateArray());
        Assert.Equal(SiemensBindingKinds.PlcSimAdvanced, accessApi.GetProperty("kind").GetString());
        Assert.Equal("fake-8", accessApi.GetProperty("productVersion").GetString());
        Assert.Equal("fake-8", accessApi.GetProperty("accessApiVersion").GetString());

        var runtime = Assert.Single(declaration.GetProperty("runtimes").EnumerateArray());
        Assert.Equal(SiemensBindingKinds.PlcSimAdvanced, runtime.GetProperty("kind").GetString());
        Assert.Equal("fake", runtime.GetProperty("version").GetString());
        Assert.Equal(SiemensRuntimeLocality.Local.ToString(), runtime.GetProperty("locality").GetString());
        Assert.Equal(
            new[] { SiemensProjectSourceKinds.SiemensFakeProject },
            runtime
                .GetProperty("compatibleProjectSourceKinds")
                .EnumerateArray()
                .Select(kind => kind.GetString())
                .ToArray());

        Assert.Equal(
            new[] { UnitTestContractVersions.PlcsimAdvancedCommunication },
            declaration
                .GetProperty("unitTestCommunicationContractVersions")
                .EnumerateArray()
                .Select(version => version.GetString())
                .ToArray());
        Assert.Equal(
            new[] { UnitTestContractVersions.EventStreamModel },
            declaration
                .GetProperty("unitTestEventStreamContractVersions")
                .EnumerateArray()
                .Select(version => version.GetString())
                .ToArray());
    }

    [Fact]
    public async Task Fake_binding_executes_baseline_runtime_and_communication_flow()
    {
        var binding = new FakePlcsimAdvancedBinding();

        var runtimeOpen = await binding.OpenRuntimeSessionAsync(
            new PlcsimRuntimeSessionOpenRequest(
                new PlcsimRuntimeApiExpectation("fake-8", "fake-8", SiemensRuntimeLocality.Local),
                null,
                new PlcsimRuntimeStoragePreference(null),
                PlcsimRuntimeCleanupPreference.Keep,
                "test"),
            CancellationToken.None);

        Assert.True(runtimeOpen.Succeeded);
        var runtime = Assert.IsAssignableFrom<IPlcsimRuntimeSession>(runtimeOpen.Session);
        Assert.Equal(PlcsimRuntimeHealthState.Healthy, runtime.Health);

        var preparedRuntime = await runtime.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                null,
                new PlcsimRuntimeStoragePreference(null),
                PlcsimRuntimeProvisioningMode.UseExisting,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(30),
                "prepare-runtime"),
            CancellationToken.None);
        Assert.True(preparedRuntime.Succeeded);
        Assert.NotNull(preparedRuntime.Runtime);

        var started = await runtime.StartRuntimeAsync(
            new PlcsimRuntimeControlRequest(runtime.Runtime.SessionId, TimeSpan.FromSeconds(1), "start"),
            CancellationToken.None);
        Assert.True(started.Succeeded);
        Assert.Equal(PlcsimRuntimeControlState.Run, started.State);

        var prepared = await binding.PrepareCommunicationAsync(
            new PlcsimCommunicationPrepareRequest(
                runtime.Runtime.SessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                UnitTestContractVersions.EventStreamModel,
                CreateWorkerOptions(),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(30),
                "prepare"),
            CancellationToken.None);

        Assert.True(prepared.Succeeded);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepared.Session);
        Assert.Equal(PlcsimCommunicationHealthState.Healthy, communication.Health);
        var observations = new ConcurrentQueue<PlcsimRunControlObservation>();
        communication.RunControlObservationChanged += (_, args) => observations.Enqueue(args.Observation);

        var runControlValidation = await communication.ValidateRunControlAsync(
            new PlcsimRunControlValidationRequest(
                communication.Communication.SessionId,
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                "validate-run-control"),
            CancellationToken.None);
        Assert.True(runControlValidation.Succeeded);
        Assert.True(runControlValidation.ContractValid);

        var startCommand = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(1),
                "start-command"),
            CancellationToken.None);
        Assert.True(startCommand.Succeeded);
        Assert.True(startCommand.Accepted);
        await WaitUntilAsync(() => observations.Any(observation => observation.Running));
        var runningObservation = observations.First(observation => observation.Running);
        Assert.True(runningObservation.Running);
        Assert.Equal(UnitTestLifecycleState.Running, runningObservation.State);
        Assert.Equal(UnitTestExecutionStatus.Running, runningObservation.ExecutionStatus);

        var eventBufferValidation = await communication.ValidateEventBufferAsync(
            new PlcsimEventBufferValidationRequest(
                communication.Communication.SessionId,
                UnitTestContractVersions.EventStreamModel,
                "validate-event-buffer"),
            CancellationToken.None);
        Assert.True(eventBufferValidation.Succeeded);
        Assert.True(eventBufferValidation.ContractValid);
        Assert.Equal(EventBufferReadState.Empty, eventBufferValidation.Observation.ReadState);

        var monitoring = await communication.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(communication.Communication.SessionId, "monitor"),
            CancellationToken.None);
        Assert.True(monitoring.Succeeded);
        Assert.True(monitoring.State.IsMonitoring);
        await WaitUntilAsync(() => observations.Any(observation => observation.Completed));
        var completedObservation = observations.Last(observation => observation.Completed);
        Assert.Equal(UnitTestResultState.Passed, completedObservation.Result);
        Assert.Equal(UnitTestLifecycleState.Completed, completedObservation.State);
        Assert.Equal(UnitTestExecutionStatus.Completed, completedObservation.ExecutionStatus);

        await communication.CloseAsync(CancellationToken.None);
        Assert.Equal(PlcsimCommunicationHealthState.Disposed, communication.Health);

        var runtimeRelease = await runtime.ReleaseRuntimeAsync(
            new PlcsimRuntimeReleaseRequest(
                runtime.Runtime.SessionId,
                PlcsimRuntimeCleanupPreference.Keep,
                TimeSpan.FromSeconds(1),
                "release-runtime"),
            CancellationToken.None);
        Assert.True(runtimeRelease.Succeeded);
        Assert.True(runtimeRelease.Released);
        Assert.False(runtimeRelease.StorageCleaned);
        Assert.Equal(PlcsimRuntimeHealthState.Disposed, runtime.Health);
    }

    [Fact]
    public async Task Fake_communication_session_returns_rejected_results_for_mismatched_session_ids()
    {
        var binding = new FakePlcsimAdvancedBinding();
        var runtime = await OpenRunningRuntimeAsync(binding);
        var prepared = await PrepareCommunicationAsync(binding, runtime.Runtime.SessionId);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepared.Session);
        const string mismatchedSessionId = "different-communication-session";

        var validation = await communication.ValidateRunControlAsync(
            new PlcsimRunControlValidationRequest(
                mismatchedSessionId,
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                "validation"),
            CancellationToken.None);
        var command = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                mismatchedSessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(1),
                "command"),
            CancellationToken.None);
        var observation = await communication.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(mismatchedSessionId, "observation"),
            CancellationToken.None);
        var eventBufferValidation = await communication.ValidateEventBufferAsync(
            new PlcsimEventBufferValidationRequest(
                mismatchedSessionId,
                UnitTestContractVersions.EventStreamModel,
                "event-buffer-validation"),
            CancellationToken.None);
        var monitoringStart = await communication.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(mismatchedSessionId, "monitoring-start"),
            CancellationToken.None);
        var monitoringStop = await communication.StopEventStreamMonitoringAsync(
            new PlcsimEventStreamStopRequest(mismatchedSessionId, "monitoring-stop"),
            CancellationToken.None);
        Assert.All(
            new[]
            {
                validation.Status,
                command.Status,
                observation.Status,
                eventBufferValidation.Status,
                monitoringStart.Status,
                monitoringStop.Status
            },
            status => Assert.Equal(PlcsimOperationStatus.Rejected, status));
        Assert.All(
            new[]
            {
                validation.Diagnostics,
                command.Diagnostics,
                observation.Diagnostics,
                eventBufferValidation.Diagnostics,
                monitoringStart.Diagnostics,
                monitoringStop.Diagnostics
            },
            diagnostics => Assert.Contains(
                diagnostics,
                diagnostic => diagnostic.Code == "FakePlcsim.CommunicationSessionMismatch"));
        Assert.False(command.Accepted);
        Assert.Null(observation.Observation);
        Assert.False(eventBufferValidation.ContractValid);
        Assert.False(monitoringStart.State.IsMonitoring);
        Assert.False(monitoringStop.State.IsMonitoring);
        Assert.Equal(PlcsimCommunicationHealthState.Healthy, communication.Health);

        await communication.CloseAsync(CancellationToken.None);
        await runtime.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Fake_binding_rejects_unsupported_contract_versions()
    {
        var binding = new FakePlcsimAdvancedBinding();
        var runtime = await OpenRunningRuntimeAsync(binding);

        var prepared = await binding.PrepareCommunicationAsync(
            new PlcsimCommunicationPrepareRequest(
                runtime.Runtime.SessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                "unsupported",
                UnitTestContractVersions.EventStreamModel,
                CreateWorkerOptions(),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(30),
                null),
            CancellationToken.None);

        Assert.False(prepared.Succeeded);
        Assert.Null(prepared.Session);
        Assert.Equal(PlcsimOperationStatus.Rejected, prepared.Status);
        Assert.Contains(prepared.Diagnostics, diagnostic => diagnostic.Code == "FakePlcsim.RunControl.ContractMismatch");
    }

    [Fact]
    public async Task Fake_binding_rejects_communication_prepare_for_unknown_runtime_session()
    {
        var binding = new FakePlcsimAdvancedBinding();

        var prepared = await PrepareCommunicationAsync(binding, "missing-runtime");

        Assert.False(prepared.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Unavailable, prepared.Status);
        Assert.Null(prepared.Session);
        Assert.Contains(prepared.Diagnostics, diagnostic => diagnostic.Code == "FakePlcsim.Runtime.SessionNotFound");
    }

    [Fact]
    public async Task Fake_binding_rejects_communication_prepare_while_runtime_is_stopped()
    {
        var binding = new FakePlcsimAdvancedBinding();
        var runtime = await OpenPreparedRuntimeAsync(binding);

        var prepared = await PrepareCommunicationAsync(binding, runtime.Runtime.SessionId);

        Assert.False(prepared.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Unavailable, prepared.Status);
        Assert.Null(prepared.Session);
        Assert.Contains(prepared.Diagnostics, diagnostic => diagnostic.Code == "FakePlcsim.Runtime.NotRunning");
    }

    [Fact]
    public async Task Fake_runtime_stop_invalidates_active_communication_session()
    {
        var binding = new FakePlcsimAdvancedBinding();
        var runtime = await OpenRunningRuntimeAsync(binding);
        var prepared = await PrepareCommunicationAsync(binding, runtime.Runtime.SessionId);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepared.Session);
        PlcsimCommunicationHealthChangedEventArgs? healthChange = null;
        communication.HealthChanged += (_, args) => healthChange = args;

        var stopped = await runtime.StopRuntimeAsync(
            new PlcsimRuntimeControlRequest(runtime.Runtime.SessionId, TimeSpan.FromSeconds(1), "stop"),
            CancellationToken.None);

        Assert.True(stopped.Succeeded);
        Assert.Equal(PlcsimCommunicationHealthState.Invalidated, communication.Health);
        Assert.NotNull(healthChange);
        Assert.Equal(PlcsimCommunicationHealthState.Healthy, healthChange.Previous);
        Assert.Equal(PlcsimCommunicationHealthState.Invalidated, healthChange.Current);
    }

    [Fact]
    public async Task Fake_external_runtime_stop_invalidates_active_communication_session()
    {
        var binding = new FakePlcsimAdvancedBinding();
        var runtime = await OpenRunningRuntimeAsync(binding);
        var prepared = await PrepareCommunicationAsync(binding, runtime.Runtime.SessionId);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepared.Session);

        var changed = binding.SimulateExternalRuntimeStateChange(
            runtime.Runtime.SessionId,
            PlcsimRuntimeControlState.Stop);

        Assert.True(changed);
        Assert.Equal(PlcsimCommunicationHealthState.Invalidated, communication.Health);
        var observed = await runtime.ObserveRuntimeAsync(
            new PlcsimRuntimeObservationRequest("observe-external-stop"),
            CancellationToken.None);
        Assert.Equal(PlcsimRuntimeControlState.Stop, observed.Runtime?.State);
    }

    [Fact]
    public async Task Fake_external_runtime_invalidation_invalidates_active_communication_session()
    {
        var binding = new FakePlcsimAdvancedBinding();
        var runtime = await OpenRunningRuntimeAsync(binding);
        var prepared = await PrepareCommunicationAsync(binding, runtime.Runtime.SessionId);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepared.Session);

        var invalidated = binding.SimulateExternalRuntimeInvalidation(runtime.Runtime.SessionId);

        Assert.True(invalidated);
        Assert.Equal(PlcsimRuntimeHealthState.Invalidated, runtime.Health);
        Assert.Equal(PlcsimCommunicationHealthState.Invalidated, communication.Health);
    }

    [Fact]
    public async Task Fake_runtime_release_invalidates_communication_and_unregisters_runtime_session()
    {
        var binding = new FakePlcsimAdvancedBinding();
        var runtime = await OpenRunningRuntimeAsync(binding);
        var prepared = await PrepareCommunicationAsync(binding, runtime.Runtime.SessionId);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepared.Session);

        var released = await runtime.ReleaseRuntimeAsync(
            new PlcsimRuntimeReleaseRequest(
                runtime.Runtime.SessionId,
                PlcsimRuntimeCleanupPreference.Keep,
                TimeSpan.FromSeconds(1),
                "release-runtime"),
            CancellationToken.None);
        var secondPrepare = await PrepareCommunicationAsync(binding, runtime.Runtime.SessionId);

        Assert.True(released.Succeeded);
        Assert.Equal(PlcsimCommunicationHealthState.Invalidated, communication.Health);
        Assert.False(secondPrepare.Succeeded);
        Assert.Contains(secondPrepare.Diagnostics, diagnostic => diagnostic.Code == "FakePlcsim.Runtime.SessionNotFound");
    }

    [Fact]
    public async Task Fake_binding_applies_configured_runtime_open_failure()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            OpenRuntimeSession = new FakePlcsimOperationScenario(
                PlcsimOperationStatus.Unavailable,
                0,
                "FakePlcsim.Test.RuntimeUnavailable",
                PlcsimDiagnosticSeverity.Error,
                "Configured runtime unavailable.")
        });

        var open = await binding.OpenRuntimeSessionAsync(
            new PlcsimRuntimeSessionOpenRequest(
                new PlcsimRuntimeApiExpectation("fake-8", "fake-8", SiemensRuntimeLocality.Local),
                null,
                new PlcsimRuntimeStoragePreference(null),
                PlcsimRuntimeCleanupPreference.Keep,
                "test"),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Null(open.Session);
        Assert.Equal(PlcsimOperationStatus.Unavailable, open.Status);
        Assert.Contains(open.Diagnostics, diagnostic => diagnostic.Code == "FakePlcsim.Test.RuntimeUnavailable");
    }

    [Fact]
    public async Task Fake_binding_applies_configured_command_failure_without_changing_observation()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            StartRunControlCommand = new FakePlcsimOperationScenario(
                PlcsimOperationStatus.TimedOut,
                0,
                "FakePlcsim.Test.CommandTimedOut",
                PlcsimDiagnosticSeverity.Error,
                "Configured command timeout.")
        });
        var communication = await OpenCommunicationAsync(binding);

        var before = await communication.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(communication.Communication.SessionId, "before-command"),
            CancellationToken.None);
        var command = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(1),
                "start"),
            CancellationToken.None);
        var after = await communication.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(communication.Communication.SessionId, "after-command"),
            CancellationToken.None);

        Assert.False(command.Succeeded);
        Assert.False(command.Accepted);
        Assert.Equal(PlcsimOperationStatus.TimedOut, command.Status);
        Assert.Equal(before.Observation, after.Observation);
        Assert.Contains(command.Diagnostics, diagnostic => diagnostic.Code == "FakePlcsim.Test.CommandTimedOut");
    }

    [Theory]
    [InlineData(PlcsimOperationStatus.Succeeded, true)]
    [InlineData(PlcsimOperationStatus.Rejected, false)]
    [InlineData(PlcsimOperationStatus.Failed, false)]
    public async Task Fake_event_buffer_reset_applies_configured_write_result(
        PlcsimOperationStatus status,
        bool accepted)
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            ResetEventBuffer = new FakePlcsimOperationScenario(
                status,
                0,
                "FakePlcsim.Test.EventBufferReset",
                accepted ? PlcsimDiagnosticSeverity.Information : PlcsimDiagnosticSeverity.Error,
                $"Configured EventBuffer reset returned {status}.")
        });
        var communication = await OpenCommunicationAsync(binding);

        var result = await communication.RequestEventBufferResetAsync(
            new PlcsimEventBufferResetRequest(
                communication.Communication.SessionId,
                TimeSpan.FromSeconds(1),
                "event-buffer-reset"),
            CancellationToken.None);

        Assert.Equal(status, result.Status);
        Assert.Equal(accepted, result.Accepted);
        Assert.Equal(accepted, result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "FakePlcsim.Test.EventBufferReset");
        Assert.Equal(accepted, result.Evidence.Count > 0);
        Assert.Equal(PlcsimCommunicationHealthState.Healthy, communication.Health);
    }

    [Fact]
    public async Task Fake_event_buffer_reset_rejects_mismatched_communication_session()
    {
        var binding = new FakePlcsimAdvancedBinding();
        var communication = await OpenCommunicationAsync(binding);

        var result = await communication.RequestEventBufferResetAsync(
            new PlcsimEventBufferResetRequest(
                "different-communication-session",
                TimeSpan.FromSeconds(1),
                "event-buffer-reset"),
            CancellationToken.None);

        Assert.Equal(PlcsimOperationStatus.Rejected, result.Status);
        Assert.False(result.Accepted);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "FakePlcsim.CommunicationSessionMismatch");
    }

    [Fact]
    public async Task Fake_lifecycle_observation_uses_configured_initial_projection()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            InitialRunControlObservation = new PlcsimRunControlObservation(
                UnitTestExecutionStatus.Running,
                UnitTestExecutionSignals.Running,
                UnitTestLifecycleState.Running,
                UnitTestResultState.NotEvaluated)
        });
        var communication = await OpenCommunicationAsync(binding);

        var observed = await communication.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(communication.Communication.SessionId, "observe"),
            CancellationToken.None);

        Assert.True(observed.Succeeded);
        var observation = Assert.IsType<PlcsimRunControlObservation>(observed.Observation);
        Assert.True(observation.Running);
        Assert.False(observation.Ready);
        Assert.Equal(UnitTestLifecycleState.Running, observation.State);
    }

    [Fact]
    public async Task Fake_lifecycle_observation_preserves_all_execution_signals()
    {
        const UnitTestExecutionSignals signals = UnitTestExecutionSignals.Uninitialized
            | UnitTestExecutionSignals.ConfigError
            | UnitTestExecutionSignals.Ready
            | UnitTestExecutionSignals.Running
            | UnitTestExecutionSignals.Completed
            | UnitTestExecutionSignals.Aborted
            | UnitTestExecutionSignals.Inactive
            | UnitTestExecutionSignals.Error;
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            InitialRunControlObservation = new PlcsimRunControlObservation(
                UnitTestExecutionStatus.Error,
                signals,
                UnitTestLifecycleState.Error,
                UnitTestResultState.Error)
        });
        var communication = await OpenCommunicationAsync(binding);

        var result = await communication.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(communication.Communication.SessionId, "all-signals"),
            CancellationToken.None);

        var observation = Assert.IsType<PlcsimRunControlObservation>(result.Observation);
        Assert.Equal(signals, observation.ExecutionSignals);
        Assert.True(observation.Uninitialized);
        Assert.True(observation.ConfigError);
        Assert.True(observation.Ready);
        Assert.True(observation.Running);
        Assert.True(observation.Completed);
        Assert.True(observation.Aborted);
        Assert.True(observation.Inactive);
        Assert.True(observation.Error);
    }

    [Fact]
    public async Task Fake_config_error_signal_does_not_imply_error_signal()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            InitialRunControlObservation = new PlcsimRunControlObservation(
                UnitTestExecutionStatus.NotReady,
                UnitTestExecutionSignals.ConfigError,
                UnitTestLifecycleState.Idle,
                UnitTestResultState.NotEvaluated)
        });
        var communication = await OpenCommunicationAsync(binding);

        var result = await communication.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(communication.Communication.SessionId, "config-error"),
            CancellationToken.None);

        var observation = Assert.IsType<PlcsimRunControlObservation>(result.Observation);
        Assert.True(observation.ConfigError);
        Assert.False(observation.Error);
    }

    [Fact]
    public async Task Fake_failed_lifecycle_observation_does_not_return_a_snapshot()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            ObserveRunControl = new FakePlcsimOperationScenario(
                PlcsimOperationStatus.Failed,
                0,
                "FakePlcsim.Test.ObservationFailed",
                PlcsimDiagnosticSeverity.Error,
                "Configured observation failure.")
        });
        var communication = await OpenCommunicationAsync(binding);

        var result = await communication.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(communication.Communication.SessionId, "failed-observation"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task Fake_start_command_write_is_accepted_without_reaction_when_not_ready()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            InitialRunControlObservation = new PlcsimRunControlObservation(
                UnitTestExecutionStatus.Reset,
                UnitTestExecutionSignals.None,
                UnitTestLifecycleState.Reset,
                UnitTestResultState.NotEvaluated)
        });
        var communication = await OpenCommunicationAsync(binding);

        var command = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(1),
                "start"),
            CancellationToken.None);

        await Task.Delay(25);
        var observed = await communication.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(communication.Communication.SessionId, "observe"),
            CancellationToken.None);

        Assert.True(command.Succeeded);
        Assert.True(command.Accepted);
        Assert.Equal(UnitTestExecutionStatus.Reset, observed.Observation?.ExecutionStatus);
    }

    [Fact]
    public async Task Fake_abort_command_is_accepted_for_running_projection()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            InitialRunControlObservation = new PlcsimRunControlObservation(
                UnitTestExecutionStatus.Running,
                UnitTestExecutionSignals.Running,
                UnitTestLifecycleState.Running,
                UnitTestResultState.NotEvaluated)
        });
        var communication = await OpenCommunicationAsync(binding);
        PlcsimRunControlObservation? observed = null;
        communication.RunControlObservationChanged += (_, args) => observed = args.Observation;

        var command = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Abort,
                TimeSpan.FromSeconds(1),
                "abort"),
            CancellationToken.None);

        Assert.True(command.Succeeded);
        Assert.True(command.Accepted);
        await WaitUntilAsync(() => observed?.Completed == true);
        Assert.NotNull(observed);
        Assert.True(observed.Completed);
        Assert.True(observed.Aborted);
        Assert.Equal(UnitTestLifecycleState.Completed, observed.State);
        Assert.Equal(UnitTestExecutionStatus.Completed, observed.ExecutionStatus);
    }

    [Fact]
    public async Task Fake_abort_command_write_is_accepted_without_reaction_for_idle_projection()
    {
        var binding = new FakePlcsimAdvancedBinding();
        var communication = await OpenCommunicationAsync(binding);

        var command = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Abort,
                TimeSpan.FromSeconds(1),
                "abort"),
            CancellationToken.None);

        await Task.Delay(25);
        var observed = await communication.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(communication.Communication.SessionId, "observe"),
            CancellationToken.None);

        Assert.True(command.Succeeded);
        Assert.True(command.Accepted);
        Assert.Equal(UnitTestExecutionStatus.Idle, observed.Observation?.ExecutionStatus);
        Assert.True(observed.Observation?.Ready);
    }

    [Fact]
    public async Task Fake_reset_command_returns_ready_idle_projection()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            InitialRunControlObservation = new PlcsimRunControlObservation(
                UnitTestExecutionStatus.Running,
                UnitTestExecutionSignals.Running,
                UnitTestLifecycleState.Running,
                UnitTestResultState.NotEvaluated)
        });
        var communication = await OpenCommunicationAsync(binding);
        PlcsimRunControlObservation? observed = null;
        communication.RunControlObservationChanged += (_, args) => observed = args.Observation;

        var command = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Reset,
                TimeSpan.FromSeconds(1),
                "reset"),
            CancellationToken.None);

        Assert.True(command.Succeeded);
        Assert.True(command.Accepted);
        await WaitUntilAsync(() => observed?.Ready == true);
        Assert.NotNull(observed);
        Assert.True(observed.Ready);
        Assert.False(observed.Running);
        Assert.Equal(UnitTestLifecycleState.Idle, observed.State);
        Assert.Equal(UnitTestExecutionStatus.Idle, observed.ExecutionStatus);
    }

    [Fact]
    public async Task Fake_command_acceptance_does_not_require_a_lifecycle_reaction()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            RunControlReactionDelay = null
        });
        var communication = await OpenCommunicationAsync(binding);
        var before = await communication.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(communication.Communication.SessionId, "before"),
            CancellationToken.None);

        var command = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(1),
                "start"),
            CancellationToken.None);
        await Task.Delay(25);
        var after = await communication.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(communication.Communication.SessionId, "after"),
            CancellationToken.None);

        Assert.True(command.Succeeded);
        Assert.True(command.Accepted);
        Assert.Equal(before.Observation, after.Observation);
    }

    [Fact]
    public async Task Fake_communication_close_cancels_a_pending_lifecycle_reaction()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            RunControlReactionDelay = TimeSpan.FromMilliseconds(50)
        });
        var communication = await OpenCommunicationAsync(binding);
        var observations = new List<PlcsimRunControlObservation>();
        communication.RunControlObservationChanged += (_, args) => observations.Add(args.Observation);

        var command = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(1),
                "start"),
            CancellationToken.None);
        await communication.CloseAsync(CancellationToken.None);
        await Task.Delay(75);

        Assert.True(command.Accepted);
        Assert.Empty(observations);
        Assert.Equal(PlcsimCommunicationHealthState.Disposed, communication.Health);
    }

    [Fact]
    public async Task Fake_event_stream_monitoring_publishes_configured_batches_and_state()
    {
        var configuredBatch = CreateEventBatch(
            sequenceId: 7,
            producerSequenceId: 7,
            deletedSequenceId: 6,
            lostEventCount: 2,
            bufferObservedEmpty: false,
            EventBufferReadState.Valid);
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            EventStream = new FakePlcsimEventStreamBehavior(
                new[] { configuredBatch },
                EventBufferReadState.Valid,
                EventStreamBatchReleaseState.Idle,
                BufferObservedEmpty: false,
                LostEventCount: 2,
                Invalidated: false)
        });
        var communication = await OpenCommunicationAsync(binding);
        var receivedBatch = new TaskCompletionSource<UnitTestEventBatch>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        communication.EventBatchReceived += (_, args) => receivedBatch.TrySetResult(args.Batch);

        var monitoring = await communication.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(communication.Communication.SessionId, "monitor"),
            CancellationToken.None);
        var received = await receivedBatch.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(monitoring.Succeeded);
        Assert.True(monitoring.State.IsMonitoring);
        Assert.Equal(communication.Communication.SessionId, received.SessionId.Value);
        Assert.Equal(7u, received.ProducerSequenceId);
        Assert.Equal(6u, received.DeletedSequenceId);
    }

    [Fact]
    public async Task Fake_event_stream_publishes_independent_overflow_loss_and_recovery_states_without_batches()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            EventStream = FakePlcsimEventStreamBehavior.Default with
            {
                MonitoringStates = new[]
                {
                    new PlcsimEventStreamMonitoringState(
                        string.Empty,
                        IsMonitoring: true,
                        EventBufferReadState.Valid,
                        EventStreamBatchReleaseState.Idle,
                        BufferObservedEmpty: false,
                        LostEventCount: 0,
                        BufferFull: true,
                        ProducerSequenceId: 288,
                        DeletedSequenceId: 255),
                    new PlcsimEventStreamMonitoringState(
                        string.Empty,
                        IsMonitoring: true,
                        EventBufferReadState.Valid,
                        EventStreamBatchReleaseState.Idle,
                        BufferObservedEmpty: false,
                        LostEventCount: 19,
                        BufferFull: true,
                        ProducerSequenceId: 412,
                        DeletedSequenceId: 255),
                    new PlcsimEventStreamMonitoringState(
                        string.Empty,
                        IsMonitoring: true,
                        EventBufferReadState.Empty,
                        EventStreamBatchReleaseState.Completed,
                        BufferObservedEmpty: true,
                        LostEventCount: 0,
                        BufferFull: false,
                        ProducerSequenceId: 0,
                        DeletedSequenceId: 0)
                }
            }
        });
        var communication = await OpenCommunicationAsync(binding);
        var states = new List<PlcsimEventStreamMonitoringState>();
        var batchCount = 0;
        communication.EventStreamMonitoringStateChanged += (_, args) => states.Add(args.State);
        communication.EventBatchReceived += (_, _) => batchCount++;

        var monitoring = await communication.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(communication.Communication.SessionId, "monitor"),
            CancellationToken.None);

        Assert.True(monitoring.Succeeded);
        Assert.Equal(3, states.Count);
        Assert.All(states, state => Assert.Equal(communication.Communication.SessionId, state.CommunicationSessionId));
        Assert.True(states[0].BufferFull);
        Assert.Equal((ushort)19, states[1].LostEventCount);
        Assert.Equal(EventBufferReadState.Empty, states[2].LastReadState);
        Assert.False(states[2].BufferFull);
        Assert.Equal(PlcsimCommunicationHealthState.Healthy, communication.Health);
        Assert.Equal(0, batchCount);
    }

    [Fact]
    public async Task Fake_event_stream_does_not_publish_batch_when_release_fails()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            EventStream = FakePlcsimEventStreamBehavior.Default with
            {
                Batches = new[]
                {
                    CreateEventBatch(
                        sequenceId: 7,
                        producerSequenceId: 7,
                        deletedSequenceId: 6,
                        lostEventCount: 0,
                        bufferObservedEmpty: false,
                        EventBufferReadState.Valid)
                },
                ReadState = EventBufferReadState.Valid,
                BufferObservedEmpty = false
            },
            ReleaseProcessedEvents = new FakePlcsimOperationScenario(
                PlcsimOperationStatus.Failed,
                0,
                "FakePlcsim.Test.ReleaseFailed",
                PlcsimDiagnosticSeverity.Error,
                "Configured event-stream release failure.")
        });
        var communication = await OpenCommunicationAsync(binding);
        var published = false;
        communication.EventBatchReceived += (_, _) => published = true;

        var monitoring = await communication.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(communication.Communication.SessionId, "monitor"),
            CancellationToken.None);

        Assert.True(monitoring.Succeeded);
        Assert.False(published);
    }

    [Fact]
    public async Task Fake_event_stream_close_cancels_pending_release_without_late_publication()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            EventStream = FakePlcsimEventStreamBehavior.Default with
            {
                Batches = new[]
                {
                    CreateEventBatch(
                        sequenceId: 7,
                        producerSequenceId: 7,
                        deletedSequenceId: 6,
                        lostEventCount: 0,
                        bufferObservedEmpty: false,
                        EventBufferReadState.Valid)
                },
                ReadState = EventBufferReadState.Valid,
                BufferObservedEmpty = false
            },
            ReleaseProcessedEvents = FakePlcsimOperationScenario.SucceededScenario with
            {
                DelayMs = 10_000
            }
        });
        var communication = await OpenCommunicationAsync(binding);
        var published = false;
        communication.EventBatchReceived += (_, _) => published = true;

        await communication.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(communication.Communication.SessionId, "monitor"),
            CancellationToken.None);
        await communication.CloseAsync(CancellationToken.None);

        Assert.False(published);
        Assert.Equal(PlcsimCommunicationHealthState.Disposed, communication.Health);
    }

    [Fact]
    public async Task Fake_event_stream_runtime_invalidation_cancels_pending_release_without_late_publication()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            EventStream = FakePlcsimEventStreamBehavior.Default with
            {
                Batches = new[]
                {
                    CreateEventBatch(
                        sequenceId: 7,
                        producerSequenceId: 7,
                        deletedSequenceId: 6,
                        lostEventCount: 0,
                        bufferObservedEmpty: false,
                        EventBufferReadState.Valid)
                },
                ReadState = EventBufferReadState.Valid,
                BufferObservedEmpty = false
            },
            ReleaseProcessedEvents = FakePlcsimOperationScenario.SucceededScenario with
            {
                DelayMs = 10_000
            }
        });
        var runtime = await OpenRunningRuntimeAsync(binding);
        var prepared = await PrepareCommunicationAsync(binding, runtime.Runtime.SessionId);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepared.Session);
        var published = false;
        communication.EventBatchReceived += (_, _) => published = true;

        await communication.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(communication.Communication.SessionId, "monitor"),
            CancellationToken.None);
        await Task.Yield();
        var invalidated = binding.SimulateExternalRuntimeInvalidation(runtime.Runtime.SessionId);
        await Task.Delay(25);

        Assert.True(invalidated);
        Assert.False(published);
        Assert.Equal(PlcsimCommunicationHealthState.Invalidated, communication.Health);
    }

    [Fact]
    public async Task Fake_event_stream_monitoring_cancellation_returns_cancelled_without_state_change()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            StartEventStreamMonitoring = new FakePlcsimOperationScenario(
                PlcsimOperationStatus.Succeeded,
                10_000,
                null,
                PlcsimDiagnosticSeverity.Information,
                null)
        });
        var communication = await OpenCommunicationAsync(binding);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));

        var monitoring = await communication.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(communication.Communication.SessionId, "monitor"),
            cancellation.Token);

        Assert.False(monitoring.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Cancelled, monitoring.Status);
        Assert.False(monitoring.State.IsMonitoring);
    }

    [Fact]
    public async Task Fake_event_stream_invalidation_marks_session_invalid_and_health_invalidated()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            EventStream = new FakePlcsimEventStreamBehavior(
                Array.Empty<UnitTestEventBatch>(),
                EventBufferReadState.Valid,
                EventStreamBatchReleaseState.Idle,
                BufferObservedEmpty: false,
                LostEventCount: 0,
                Invalidated: true)
        });
        var communication = await OpenCommunicationAsync(binding);

        var monitoring = await communication.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(communication.Communication.SessionId, "monitor"),
            CancellationToken.None);

        Assert.True(monitoring.Succeeded);
        Assert.Equal(EventBufferReadState.SessionInvalid, monitoring.State.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Invalidated, monitoring.State.BatchReleaseState);
        Assert.Equal(PlcsimCommunicationHealthState.Invalidated, communication.Health);
    }

    [Fact]
    public async Task Fake_runtime_release_cleans_storage_only_for_owned_runtime_with_release_policy()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            RuntimeCandidates = Array.Empty<PlcsimRuntimeTargetCandidate>()
        });
        var runtime = await OpenRuntimeAsync(binding);
        var prepared = await runtime.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                null,
                new PlcsimRuntimeStoragePreference("fake-storage"),
                PlcsimRuntimeProvisioningMode.CreateOwned,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(30),
                "prepare"),
            CancellationToken.None);
        Assert.True(prepared.Succeeded);
        Assert.True(prepared.Runtime?.IsOwned);
        PlcsimRuntimeHealthChangedEventArgs? healthChanged = null;
        runtime.HealthChanged += (_, args) => healthChanged = args;

        var released = await runtime.ReleaseRuntimeAsync(
            new PlcsimRuntimeReleaseRequest(
                runtime.Runtime.SessionId,
                PlcsimRuntimeCleanupPreference.Release,
                TimeSpan.FromSeconds(1),
                "release"),
            CancellationToken.None);

        Assert.True(released.Succeeded);
        Assert.True(released.Released);
        Assert.True(released.StorageCleaned);
        Assert.Equal(PlcsimRuntimeCleanupPreference.Release, released.CleanupPreference);
        Assert.Equal(PlcsimRuntimeHealthState.Disposed, runtime.Health);
        Assert.NotNull(healthChanged);
        Assert.Equal(PlcsimRuntimeHealthState.Healthy, healthChanged!.Previous);
        Assert.Equal(PlcsimRuntimeHealthState.Disposed, healthChanged.Current);
    }

    [Fact]
    public async Task Fake_runtime_release_does_not_clean_attached_runtime_storage()
    {
        var binding = new FakePlcsimAdvancedBinding();
        var runtime = await OpenRuntimeAsync(binding);
        var prepared = await runtime.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                null,
                new PlcsimRuntimeStoragePreference("fake-storage"),
                PlcsimRuntimeProvisioningMode.UseExisting,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(30),
                "prepare"),
            CancellationToken.None);
        Assert.True(prepared.Succeeded);
        Assert.False(prepared.Runtime?.IsOwned);

        var released = await runtime.ReleaseRuntimeAsync(
            new PlcsimRuntimeReleaseRequest(
                runtime.Runtime.SessionId,
                PlcsimRuntimeCleanupPreference.Release,
                TimeSpan.FromSeconds(1),
                "release"),
            CancellationToken.None);

        Assert.True(released.Succeeded);
        Assert.True(released.Released);
        Assert.False(released.StorageCleaned);
        Assert.Equal(PlcsimRuntimeHealthState.Disposed, runtime.Health);
    }

    [Fact]
    public async Task Fake_runtime_close_disposes_without_policy_cleanup()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            RuntimeCandidates = Array.Empty<PlcsimRuntimeTargetCandidate>()
        });
        var runtime = await OpenRuntimeAsync(binding);
        var prepared = await runtime.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                null,
                new PlcsimRuntimeStoragePreference("fake-storage"),
                PlcsimRuntimeProvisioningMode.CreateOwned,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(30),
                "prepare"),
            CancellationToken.None);
        Assert.True(prepared.Succeeded);
        PlcsimRuntimeHealthChangedEventArgs? healthChanged = null;
        runtime.HealthChanged += (_, args) => healthChanged = args;

        await runtime.CloseAsync(CancellationToken.None);

        Assert.Equal(PlcsimRuntimeHealthState.Disposed, runtime.Health);
        Assert.NotNull(healthChanged);
        Assert.Equal(PlcsimRuntimeHealthState.Healthy, healthChanged!.Previous);
        Assert.Equal(PlcsimRuntimeHealthState.Disposed, healthChanged.Current);
    }

    [Fact]
    public async Task Fake_runtime_discovery_returns_configured_candidates()
    {
        var candidate = CreateRuntimeCandidate("runtime-a", "Runtime A");
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            RuntimeCandidates = new[] { candidate }
        });
        var runtime = await OpenRuntimeAsync(binding);

        var discovery = await runtime.DiscoverRuntimeTargetsAsync(
            new PlcsimRuntimeDiscoveryRequest("discover"),
            CancellationToken.None);

        Assert.True(discovery.Succeeded);
        var discovered = Assert.Single(discovery.Candidates);
        Assert.Equal("runtime-a", discovered.Reference.Id);
        Assert.True(discovered.IsUsable);
    }

    [Fact]
    public async Task Fake_runtime_prepare_rejects_ambiguous_implicit_selection()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            RuntimeCandidates = new[]
            {
                CreateRuntimeCandidate("runtime-a", "Runtime A"),
                CreateRuntimeCandidate("runtime-b", "Runtime B")
            }
        });
        var runtime = await OpenRuntimeAsync(binding);

        var prepared = await runtime.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                null,
                new PlcsimRuntimeStoragePreference(null),
                PlcsimRuntimeProvisioningMode.UseExisting,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(30),
                "prepare"),
            CancellationToken.None);

        Assert.False(prepared.Succeeded);
        Assert.Null(prepared.Runtime);
        Assert.Equal(PlcsimOperationStatus.Rejected, prepared.Status);
        Assert.Contains(prepared.Diagnostics, diagnostic => diagnostic.Code == "FakePlcsim.Runtime.TargetAmbiguous");
    }

    [Fact]
    public async Task Fake_runtime_prepare_requires_existing_runtime_without_creating()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            RuntimeCandidates = Array.Empty<PlcsimRuntimeTargetCandidate>()
        });
        var runtime = await OpenRuntimeAsync(binding);

        var prepared = await runtime.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                null,
                new PlcsimRuntimeStoragePreference("fake-storage"),
                PlcsimRuntimeProvisioningMode.UseExisting,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(30),
                "prepare"),
            CancellationToken.None);

        Assert.False(prepared.Succeeded);
        Assert.Null(prepared.Runtime);
        Assert.Contains(prepared.Diagnostics, diagnostic => diagnostic.Code == "FakePlcsim.Runtime.TargetUnavailable");
    }

    [Fact]
    public async Task Fake_runtime_prepare_can_create_owned_runtime_when_missing()
    {
        var binding = new FakePlcsimAdvancedBinding(FakePlcsimBehavior.Default with
        {
            RuntimeCandidates = Array.Empty<PlcsimRuntimeTargetCandidate>()
        });
        var runtime = await OpenRuntimeAsync(binding);

        var prepared = await runtime.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                null,
                new PlcsimRuntimeStoragePreference("fake-storage"),
                PlcsimRuntimeProvisioningMode.CreateOwned,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(30),
                "prepare"),
            CancellationToken.None);

        Assert.True(prepared.Succeeded);
        Assert.NotNull(prepared.Runtime);
        var created = prepared.Runtime!;
        Assert.True(created.IsOwned);
        Assert.Equal("fake-runtime-created", created.Reference.Id);
    }

    [Fact]
    public async Task Fake_runtime_prepare_creates_owned_runtime_next_to_unrelated_existing_runtime()
    {
        var binding = new FakePlcsimAdvancedBinding();
        var runtime = await OpenRuntimeAsync(binding);

        var prepared = await runtime.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                new PlcsimRuntimeReference(null, "OwnedByTest"),
                new PlcsimRuntimeStoragePreference("fake-storage"),
                PlcsimRuntimeProvisioningMode.CreateOwned,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(30),
                "prepare"),
            CancellationToken.None);

        Assert.True(prepared.Succeeded);
        Assert.NotNull(prepared.Runtime);
        Assert.True(prepared.Runtime!.IsOwned);
        Assert.Equal("fake-runtime-created", prepared.Runtime.Reference.Id);
        Assert.Equal("OwnedByTest", prepared.Runtime.Reference.InstanceName);
    }

    [Fact]
    public async Task Fake_runtime_prepare_rejects_create_owned_name_collision()
    {
        var binding = new FakePlcsimAdvancedBinding();
        var runtime = await OpenRuntimeAsync(binding);

        var prepared = await runtime.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                new PlcsimRuntimeReference(null, "Fake PLCSIM Runtime"),
                new PlcsimRuntimeStoragePreference("fake-storage"),
                PlcsimRuntimeProvisioningMode.CreateOwned,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(30),
                "prepare"),
            CancellationToken.None);

        Assert.False(prepared.Succeeded);
        Assert.Null(prepared.Runtime);
        Assert.Contains(
            prepared.Diagnostics,
            diagnostic => diagnostic.Code == "FakePlcsim.Runtime.InstanceAlreadyExists");
    }

    [Fact]
    public async Task Fake_runtime_observation_reflects_control_state_changes()
    {
        var binding = new FakePlcsimAdvancedBinding();
        var runtime = await OpenRuntimeAsync(binding);
        var prepared = await runtime.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                null,
                new PlcsimRuntimeStoragePreference(null),
                PlcsimRuntimeProvisioningMode.UseExisting,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(30),
                "prepare"),
            CancellationToken.None);
        Assert.True(prepared.Succeeded);

        await runtime.StartRuntimeAsync(
            new PlcsimRuntimeControlRequest(runtime.Runtime.SessionId, TimeSpan.FromSeconds(1), "start"),
            CancellationToken.None);
        var running = await runtime.ObserveRuntimeAsync(
            new PlcsimRuntimeObservationRequest("observe-running"),
            CancellationToken.None);

        Assert.True(running.Succeeded);
        Assert.NotNull(running.Runtime);
        Assert.Equal(PlcsimRuntimeControlState.Run, running.Runtime!.State);

        await runtime.StopRuntimeAsync(
            new PlcsimRuntimeControlRequest(runtime.Runtime.SessionId, TimeSpan.FromSeconds(1), "stop"),
            CancellationToken.None);
        var stopped = await runtime.ObserveRuntimeAsync(
            new PlcsimRuntimeObservationRequest("observe-stopped"),
            CancellationToken.None);

        Assert.True(stopped.Succeeded);
        Assert.NotNull(stopped.Runtime);
        Assert.Equal(PlcsimRuntimeControlState.Stop, stopped.Runtime!.State);
    }

    private static async Task<IPlcsimUnitTestCommunicationSession> OpenCommunicationAsync(FakePlcsimAdvancedBinding binding)
    {
        var runtime = await OpenRunningRuntimeAsync(binding);

        var prepared = await PrepareCommunicationAsync(binding, runtime.Runtime.SessionId);

        return Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepared.Session);
    }

    private static Task<PlcsimCommunicationPrepareResult> PrepareCommunicationAsync(
        FakePlcsimAdvancedBinding binding,
        string runtimeSessionId)
    {
        return binding.PrepareCommunicationAsync(
            new PlcsimCommunicationPrepareRequest(
                runtimeSessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                UnitTestContractVersions.EventStreamModel,
                CreateWorkerOptions(),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(30),
                "prepare"),
            CancellationToken.None);
    }

    private static async Task<IPlcsimRuntimeSession> OpenRuntimeAsync(FakePlcsimAdvancedBinding binding)
    {
        var runtimeOpen = await binding.OpenRuntimeSessionAsync(
            new PlcsimRuntimeSessionOpenRequest(
                new PlcsimRuntimeApiExpectation("fake-8", "fake-8", SiemensRuntimeLocality.Local),
                null,
                new PlcsimRuntimeStoragePreference(null),
                PlcsimRuntimeCleanupPreference.Keep,
                "test"),
            CancellationToken.None);
        return Assert.IsAssignableFrom<IPlcsimRuntimeSession>(runtimeOpen.Session);
    }

    private static async Task<IPlcsimRuntimeSession> OpenPreparedRuntimeAsync(FakePlcsimAdvancedBinding binding)
    {
        var runtime = await OpenRuntimeAsync(binding);
        var prepared = await runtime.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                null,
                new PlcsimRuntimeStoragePreference(null),
                PlcsimRuntimeProvisioningMode.UseExisting,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(1),
                "prepare-runtime"),
            CancellationToken.None);
        Assert.True(prepared.Succeeded);
        return runtime;
    }

    private static async Task<IPlcsimRuntimeSession> OpenRunningRuntimeAsync(FakePlcsimAdvancedBinding binding)
    {
        var runtime = await OpenPreparedRuntimeAsync(binding);
        var started = await runtime.StartRuntimeAsync(
            new PlcsimRuntimeControlRequest(runtime.Runtime.SessionId, TimeSpan.FromSeconds(1), "start-runtime"),
            CancellationToken.None);
        Assert.True(started.Succeeded);
        return runtime;
    }

    private static PlcsimCommunicationWorkerOptions CreateWorkerOptions()
    {
        return new PlcsimCommunicationWorkerOptions(
            PlcsimCommunicationObservationStrategyKind.Polling,
            TimeSpan.FromMilliseconds(100));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }

    private static PlcsimRuntimeTargetCandidate CreateRuntimeCandidate(string id, string name)
    {
        return new PlcsimRuntimeTargetCandidate(
            new PlcsimRuntimeReference(id, name),
            "fake-8",
            "fake-8",
            SiemensRuntimeLocality.Local,
            PlcsimRuntimeControlState.Stop,
            true,
            false);
    }

    private static UnitTestEventBatch CreateEventBatch(
        uint sequenceId,
        uint producerSequenceId,
        uint deletedSequenceId,
        ushort lostEventCount,
        bool bufferObservedEmpty,
        EventBufferReadState readState)
    {
        return new UnitTestEventBatch(
            new CommunicationSessionId("configured-session"),
            new[]
            {
                new UnitTestEventEntry(
                    sequenceId,
                    DateTimeOffset.UnixEpoch,
                    UnitTestOwnerScope.RunControl,
                    UnitTestEventType.Info,
                    LogLevel.Information,
                    "FakeRunControl",
                    string.Empty,
                    UnitTestLifecycleState.Running,
                    UnitTestResultState.NotEvaluated,
                    "Configured fake event.")
            },
            producerSequenceId,
            deletedSequenceId,
            lostEventCount,
            bufferObservedEmpty,
            readState);
    }

    private static string GetFakeBindingManifestPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "access-profiles",
                "siemens",
                "plcsim-advanced",
                "fake",
                "src",
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.Fake",
                SiemensBindingPackageDescriptorContract.FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            SiemensBindingPackageDescriptorContract.FileName);
    }
}
