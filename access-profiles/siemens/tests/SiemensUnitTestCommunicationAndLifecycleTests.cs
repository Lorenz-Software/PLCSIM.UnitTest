using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.Fake;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.Fake;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens;

public sealed partial class SiemensAccessProfilePluginTests
{
    [Fact]
    public async Task Siemens_session_prepares_unit_test_communication_and_binds_lifecycle_service()
    {
        var communicationSession = new RecordingPlcsimCommunicationSession();
        var communicationBinding = new RecordingPlcsimCommunicationBinding(communicationSession);
        var session = new SiemensAccessProfileSession(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensSelectedBindings(
                new TestTiaBinding(),
                new RecordingPlcsimBinding(new RecordingPlcsimRuntimeSession(CreateRuntimeCandidate())),
                communicationBinding),
            ProjectOwnershipMode.ApplicationManaged,
            CreateResourceScope(),
            TimeProvider.System,
            new SiemensTestLoggerFactory().CreateLogger("test"));

        var prepare = await session.PrepareUnitTestCommunicationAsync(
            new CommunicationPrepareRequest(
                CreateCommunicationConfig(),
                CreateProjectExecutionContext(),
                new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
                new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1")),
            CancellationToken.None);
        var start = await session.UnitTestLifecycle.StartAsync(
            new UnitTestContext(
                prepare.Value!,
                CreateProjectExecutionContext(),
                new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
                new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1")),
            CancellationToken.None);

        Assert.True(prepare.IsSuccessful);
        Assert.Equal("communication-session-1", prepare.Value!.SessionId.Value);
        Assert.Equal("runtime-session-1", communicationBinding.LastPrepareRequest?.RuntimeSessionId);
        Assert.Equal("DB_RunControl", communicationBinding.LastPrepareRequest?.RunControlRoot);
        Assert.Equal("DB_EventBuffer", communicationBinding.LastPrepareRequest?.EventBufferRoot);
        Assert.Equal(UnitTestContractVersions.PlcsimAdvancedCommunication, communicationBinding.LastPrepareRequest?.ExpectedRunControlContractVersion);
        Assert.Equal(UnitTestContractVersions.EventStreamModel, communicationBinding.LastPrepareRequest?.ExpectedEventStreamContractVersion);
        Assert.Equal(PlcsimCommunicationObservationStrategyKind.Polling, communicationBinding.LastPrepareRequest?.WorkerOptions.ObservationStrategy);
        Assert.Equal(SiemensProfileConfigurationParser.DefaultUnitTestMonitoringInterval, communicationBinding.LastPrepareRequest?.WorkerOptions.MonitoringInterval);
        Assert.Equal(SiemensProfileConfigurationParser.DefaultUnitTestDrainInterval, communicationBinding.LastPrepareRequest?.WorkerOptions.DrainInterval);
        Assert.Equal(SiemensProfileConfigurationParser.DefaultUnitTestMaxEventsPerBatch, communicationBinding.LastPrepareRequest?.WorkerOptions.MaxEventsPerBatch);
        Assert.Equal(1, communicationSession.ValidateRunControlCount);
        Assert.Equal(1, communicationSession.ObserveRunControlCount);
        Assert.True(start.IsSuccessful);
        Assert.True(start.Value!.Accepted);
        Assert.Equal(UnitTestLifecycleState.Running, session.UnitTestLifecycle.CurrentObservation.State);
    }

    [Fact]
    public async Task Siemens_session_reconnect_releases_communication_and_revalidates_runtime_session_in_place()
    {
        var runtime = CreateRuntimeCandidate();
        var originalRuntimeSession = new RecordingPlcsimRuntimeSession(runtime);
        var runtimeBinding = new RecordingPlcsimBinding(originalRuntimeSession);
        var communicationSession = new RecordingPlcsimCommunicationSession();
        var profileSession = new SiemensAccessProfileSession(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensSelectedBindings(
                new TestTiaBinding(),
                runtimeBinding,
                new RecordingPlcsimCommunicationBinding(communicationSession)),
            ProjectOwnershipMode.ApplicationManaged,
            CreateResourceScope(),
            TimeProvider.System,
            new SiemensTestLoggerFactory().CreateLogger("test"));
        var configuration = CreateCommunicationConfig();
        var runtimeTarget = new RuntimeTargetRef(
            new RuntimeTargetId(runtime.Reference.Id!),
            runtime.Reference.InstanceName!,
            runtime.Reference.InstanceName);
        var prepared = await profileSession.PlcControl.PrepareRuntimeAsync(
            new PlcRuntimePrepareRequest(
                configuration,
                RuntimeProvisioningMode.UseExisting,
                runtimeTarget),
            CancellationToken.None);
        var communication = await profileSession.PrepareUnitTestCommunicationAsync(
            new CommunicationPrepareRequest(
                configuration,
                CreateProjectExecutionContext(),
                prepared.Value!.Runtime,
                prepared.Value.RuntimeTarget),
            CancellationToken.None);
        var plcControl = profileSession.PlcControl;
        var lifecycle = profileSession.UnitTestLifecycle;
        var eventStream = profileSession.UnitTestEventStream;

        var reconnect = await profileSession.ReconnectRuntimeAsync(
            new PlcRuntimeReconnectRequest(
                configuration,
                RuntimeProvisioningMode.UseExisting,
                prepared.Value.Runtime,
                prepared.Value.RuntimeTarget),
            CancellationToken.None);

        Assert.True(communication.IsSuccessful);
        Assert.True(reconnect.IsSuccessful);
        Assert.Equal(prepared.Value.Runtime.Id, reconnect.Value!.Runtime.Id);
        Assert.Same(plcControl, profileSession.PlcControl);
        Assert.Same(lifecycle, profileSession.UnitTestLifecycle);
        Assert.Same(eventStream, profileSession.UnitTestEventStream);
        Assert.Equal(1, communicationSession.CloseCount);
        Assert.Equal(1, originalRuntimeSession.RevalidateCount);
        Assert.Equal(0, originalRuntimeSession.CloseCount);
        Assert.Equal(0, originalRuntimeSession.ReleaseCount);
        Assert.Equal(AccessHealthState.NotEstablished, lifecycle.Health.State);
        Assert.Equal(AccessHealthState.NotEstablished, eventStream.Health.State);
    }

    [Fact]
    public async Task Siemens_session_forwards_explicit_communication_configuration_to_plcsim_binding()
    {
        var communicationSession = new RecordingPlcsimCommunicationSession();
        var communicationBinding = new RecordingPlcsimCommunicationBinding(communicationSession);
        var loggerFactory = new SiemensTestLoggerFactory();
        var session = new SiemensAccessProfileSession(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensSelectedBindings(
                new TestTiaBinding(),
                new RecordingPlcsimBinding(new RecordingPlcsimRuntimeSession(CreateRuntimeCandidate())),
                communicationBinding),
            ProjectOwnershipMode.ApplicationManaged,
            CreateResourceScope(),
            TimeProvider.System,
            loggerFactory.CreateLogger("test"),
            loggerFactory);
        var configuration = CreateConfig(
            SiemensAccessProfilePlugin.DefaultProfileId,
            SiemensAccessProfilePlugin.DefaultProfileVersion,
            """
            {
              "unitTest": {
                "runControl": "DB_ExplicitRunControl",
                "eventBuffer": "DB_ExplicitEventBuffer",
                "observationStrategy": "polling",
                "monitoringIntervalMs": 25,
                "drainIntervalMs": 2,
                "maxEventsPerBatch": 48
              }
            }
            """,
            @"D:\Projects\sample.apf");

        var result = await session.PrepareUnitTestCommunicationAsync(
            new CommunicationPrepareRequest(
                configuration,
                CreateProjectExecutionContext(),
                new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
                new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"),
                new CommunicationTimeoutPolicy(
                    TimeSpan.FromSeconds(17),
                    TimeSpan.Zero)),
            CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal("DB_ExplicitRunControl", communicationBinding.LastPrepareRequest?.RunControlRoot);
        Assert.Equal("DB_ExplicitEventBuffer", communicationBinding.LastPrepareRequest?.EventBufferRoot);
        Assert.Equal(
            PlcsimCommunicationObservationStrategyKind.Polling,
            communicationBinding.LastPrepareRequest?.WorkerOptions.ObservationStrategy);
        Assert.Equal(TimeSpan.FromMilliseconds(25), communicationBinding.LastPrepareRequest?.WorkerOptions.MonitoringInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(2), communicationBinding.LastPrepareRequest?.WorkerOptions.DrainInterval);
        Assert.Equal(48, communicationBinding.LastPrepareRequest?.WorkerOptions.MaxEventsPerBatch);
        Assert.Equal(TimeSpan.FromSeconds(17), communicationBinding.LastPrepareRequest?.PrepareTimeout);
        Assert.Equal(TimeSpan.Zero, communicationBinding.LastPrepareRequest?.RevalidationTimeout);
        Assert.Contains(
            loggerFactory.Entries,
            entry => entry.Level == LogLevel.Information
                && entry.Message.Contains("Preparing Siemens UnitTest communication", StringComparison.Ordinal)
                && entry.Message.Contains("ConfigurationFingerprint=sha256:", StringComparison.Ordinal)
                && entry.Message.Contains("RunControlRoot=DB_ExplicitRunControl", StringComparison.Ordinal)
                && entry.Message.Contains("EventBufferRoot=DB_ExplicitEventBuffer", StringComparison.Ordinal)
                && entry.Message.Contains("ObservationStrategy=Polling", StringComparison.Ordinal)
                && entry.Message.Contains("MonitoringIntervalMs=25", StringComparison.Ordinal)
                && entry.Message.Contains("DrainIntervalMs=2", StringComparison.Ordinal)
                && entry.Message.Contains("MaxEventsPerBatch=48", StringComparison.Ordinal));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Siemens_session_preserves_omitted_plc_cycle_monitoring_interval_for_binding()
    {
        var communicationBinding = new RecordingPlcsimCommunicationBinding(
            new RecordingPlcsimCommunicationSession());
        var session = new SiemensAccessProfileSession(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensSelectedBindings(
                new TestTiaBinding(),
                new RecordingPlcsimBinding(new RecordingPlcsimRuntimeSession(CreateRuntimeCandidate())),
                communicationBinding),
            ProjectOwnershipMode.ApplicationManaged,
            CreateResourceScope(),
            TimeProvider.System,
            new SiemensTestLoggerFactory().CreateLogger("test"));
        var configuration = CreateConfig(
            SiemensAccessProfilePlugin.DefaultProfileId,
            SiemensAccessProfilePlugin.DefaultProfileVersion,
            """{ "unitTest": { "observationStrategy": "plcCycle" } }""",
            @"D:\Projects\sample.apf");

        var result = await session.PrepareUnitTestCommunicationAsync(
            new CommunicationPrepareRequest(
                configuration,
                CreateProjectExecutionContext(),
                new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
                new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1")),
            CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal(
            PlcsimCommunicationObservationStrategyKind.PlcCycle,
            communicationBinding.LastPrepareRequest?.WorkerOptions.ObservationStrategy);
        Assert.Null(communicationBinding.LastPrepareRequest?.WorkerOptions.MonitoringInterval);
        Assert.Null(communicationBinding.LastPrepareRequest?.WorkerOptions.DrainInterval);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Siemens_plcsim_lifecycle_service_does_not_apply_command_result_as_observation()
    {
        var service = new SiemensPlcsimUnitTestLifecycleService(TimeProvider.System);
        var communicationSession = new RecordingPlcsimCommunicationSession(
            commandResult: new PlcsimRunControlCommandResult(
                PlcsimOperationStatus.Succeeded,
                UnitTestRunCommand.Start,
                Accepted: true,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        service.BindCommunicationSession(communicationSession);

        var observe = await service.ValidateAndObserveAsync(CancellationToken.None);
        var start = await service.StartAsync(
            new UnitTestContext(
                new CommunicationSession(new CommunicationSessionId("communication-session-1"), DateTimeOffset.UtcNow),
                CreateProjectExecutionContext(),
                new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
                new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1")),
            CancellationToken.None);

        Assert.True(observe.IsSuccessful);
        Assert.True(start.IsSuccessful);
        Assert.True(start.Value!.Accepted);
        Assert.Equal(UnitTestLifecycleState.Idle, service.CurrentObservation.State);
    }

    [Fact]
    public async Task Siemens_session_releases_prepared_communication_when_runcontrol_validation_fails()
    {
        var communicationSession = new RecordingPlcsimCommunicationSession(
            validationResult: new PlcsimRunControlValidationResult(
                PlcsimOperationStatus.Unavailable,
                ContractValid: false,
                ObservedContractVersion: null,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V8.Worker.RuntimeNotRunning",
                        PlcsimDiagnosticSeverity.Error,
                        "Runtime is not running.",
                        "RunControlValidation",
                        "validation-failed")
                },
                Array.Empty<PlcsimEvidence>()));
        var communicationBinding = new RecordingPlcsimCommunicationBinding(communicationSession);
        var session = new SiemensAccessProfileSession(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensSelectedBindings(
                new TestTiaBinding(),
                new RecordingPlcsimBinding(new RecordingPlcsimRuntimeSession(CreateRuntimeCandidate())),
                communicationBinding),
            ProjectOwnershipMode.ApplicationManaged,
            CreateResourceScope(),
            TimeProvider.System,
            new SiemensTestLoggerFactory().CreateLogger("test"));

        var prepare = await session.PrepareUnitTestCommunicationAsync(
            new CommunicationPrepareRequest(
                CreateCommunicationConfig(),
                CreateProjectExecutionContext(),
                new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
                new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1")),
            CancellationToken.None);
        var start = await session.UnitTestLifecycle.StartAsync(
            new UnitTestContext(
                new CommunicationSession(new CommunicationSessionId("communication-session-1"), DateTimeOffset.UtcNow),
                CreateProjectExecutionContext(),
                new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
                new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1")),
            CancellationToken.None);
        var monitoring = await session.UnitTestEventStream.StartMonitoringAsync(
            new UnitTestContext(
                new CommunicationSession(new CommunicationSessionId("communication-session-1"), DateTimeOffset.UtcNow),
                CreateProjectExecutionContext(),
                new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
                new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1")),
            CancellationToken.None);

        Assert.False(prepare.IsSuccessful);
        Assert.Equal(1, communicationSession.ValidateRunControlCount);
        Assert.Equal(1, communicationSession.CloseCount);
        Assert.False(start.IsSuccessful);
        Assert.False(monitoring.IsSuccessful);
    }

    [Fact]
    public async Task Siemens_session_dispose_releases_communication_after_cancelled_initial_observation()
    {
        var observationStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observationDelay = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var communicationSession = new RecordingPlcsimCommunicationSession(
            observationStarted: observationStarted,
            observationDelay: observationDelay.Task);
        var session = CreateSessionWithCommunication(communicationSession);
        using var cancellation = new CancellationTokenSource();

        var prepare = session.PrepareUnitTestCommunicationAsync(
            new CommunicationPrepareRequest(
                CreateCommunicationConfig(),
                CreateProjectExecutionContext(),
                new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
                new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1")),
            cancellation.Token);
        await observationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);
        Assert.Equal(0, communicationSession.CloseCount);

        await session.DisposeAsync();

        Assert.Equal(1, communicationSession.CloseCount);
        Assert.Equal(AccessHealthState.Disposed, session.Health.State);
    }

    [Fact]
    public async Task Siemens_plcsim_lifecycle_service_maps_runcontrol_commands_and_diagnostics()
    {
        var service = new SiemensPlcsimUnitTestLifecycleService(TimeProvider.System);
        var communicationSession = new RecordingPlcsimCommunicationSession(
            commandDiagnostics: new[]
            {
                new PlcsimDiagnostic(
                    "FakePlcsim.RunControl.CommandWarning",
                    PlcsimDiagnosticSeverity.Warning,
                    "Command warning.",
                    "RunControlCommand",
                    "evidence-1")
            });
        var observations = new List<UnitTestRunControlObservation>();
        var diagnostics = new List<Diagnostic>();
        service.RunControlObservationChanged += (_, args) => observations.Add(args.Observation);
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        service.BindCommunicationSession(communicationSession);
        var context = new UnitTestContext(
            new CommunicationSession(new CommunicationSessionId("communication-session-1"), DateTimeOffset.UtcNow),
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));

        var start = await service.StartAsync(context, CancellationToken.None);
        var abort = await service.AbortAsync(context, CancellationToken.None);
        var reset = await service.ResetAsync(context, CancellationToken.None);

        Assert.True(start.IsSuccessful);
        Assert.Equal(UnitTestRunCommand.Start, start.Value!.Command);
        Assert.True(start.Value.Accepted);
        Assert.True(abort.IsSuccessful);
        Assert.True(abort.Value!.Accepted);
        Assert.True(reset.IsSuccessful);
        Assert.True(reset.Value!.Accepted);
        Assert.Equal(new[] { UnitTestRunCommand.Start, UnitTestRunCommand.Abort, UnitTestRunCommand.Reset }, communicationSession.Commands);
        Assert.Equal(
            new[] { UnitTestLifecycleState.Running, UnitTestLifecycleState.Completed, UnitTestLifecycleState.Idle },
            observations.Select(observation => observation.State));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "FakePlcsim.RunControl.CommandWarning"
            && diagnostic.Phase == DiagnosticPhase.RunStart
            && diagnostic.Correlation.ServiceId == "Siemens.PlcsimAdvanced");
    }

    [Fact]
    public async Task Siemens_plcsim_lifecycle_service_maps_plc_execution_status_without_casting_to_internal_lifecycle_values()
    {
        var service = new SiemensPlcsimUnitTestLifecycleService(TimeProvider.System);
        var communicationSession = new RecordingPlcsimCommunicationSession(
            initialObservation: new PlcsimRunControlObservation(
                UnitTestExecutionStatus.Completed,
                UnitTestExecutionSignals.Completed,
                UnitTestLifecycleState.Completed,
                UnitTestResultState.NotEvaluated));
        service.BindCommunicationSession(communicationSession);

        var result = await service.ValidateAndObserveAsync(CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal(UnitTestLifecycleState.Completed, service.CurrentObservation.State);
        Assert.False(service.CurrentObservation.Running);
        Assert.True(service.CurrentObservation.Completed);
    }

    [Fact]
    public async Task Siemens_plcsim_lifecycle_service_keeps_ready_separate_from_lifecycle_state()
    {
        var service = new SiemensPlcsimUnitTestLifecycleService(TimeProvider.System);
        var communicationSession = new RecordingPlcsimCommunicationSession(
            initialObservation: new PlcsimRunControlObservation(
                UnitTestExecutionStatus.Running,
                UnitTestExecutionSignals.Ready,
                UnitTestLifecycleState.Running,
                UnitTestResultState.NotEvaluated));
        service.BindCommunicationSession(communicationSession);

        var result = await service.ValidateAndObserveAsync(CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal(UnitTestLifecycleState.Running, service.CurrentObservation.State);
        Assert.True(service.CurrentObservation.Ready);
    }

    [Fact]
    public async Task Siemens_plcsim_lifecycle_service_preserves_independent_observation_axes()
    {
        const UnitTestExecutionSignals signals = UnitTestExecutionSignals.Uninitialized
            | UnitTestExecutionSignals.ConfigError
            | UnitTestExecutionSignals.Ready
            | UnitTestExecutionSignals.Running
            | UnitTestExecutionSignals.Completed
            | UnitTestExecutionSignals.Aborted
            | UnitTestExecutionSignals.Inactive;
        var source = new PlcsimRunControlObservation(
            UnitTestExecutionStatus.Completed,
            signals,
            UnitTestLifecycleState.Reset,
            UnitTestResultState.Failed);
        var service = new SiemensPlcsimUnitTestLifecycleService(TimeProvider.System);
        service.BindCommunicationSession(new RecordingPlcsimCommunicationSession(initialObservation: source));

        var result = await service.ValidateAndObserveAsync(CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal(source.ExecutionStatus, service.CurrentObservation.ExecutionStatus);
        Assert.Equal(source.ExecutionSignals, service.CurrentObservation.ExecutionSignals);
        Assert.Equal(source.State, service.CurrentObservation.State);
        Assert.Equal(source.Result, service.CurrentObservation.Result);
        Assert.True(service.CurrentObservation.ConfigError);
        Assert.False(service.CurrentObservation.Error);
    }

    [Fact]
    public async Task Siemens_plcsim_lifecycle_service_does_not_replace_observation_when_observation_fails()
    {
        var service = new SiemensPlcsimUnitTestLifecycleService(TimeProvider.System);
        var completedObservation = new PlcsimRunControlObservation(
            UnitTestExecutionStatus.Completed,
            UnitTestExecutionSignals.Completed,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.NotEvaluated);
        var diagnostics = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        var communicationSession = new RecordingPlcsimCommunicationSession(initialObservation: completedObservation);
        service.BindCommunicationSession(communicationSession);
        var first = await service.ValidateAndObserveAsync(CancellationToken.None);
        communicationSession.SetObservationResult(new PlcsimRunControlObservationResult(
            PlcsimOperationStatus.Failed,
            null,
            new[]
            {
                new PlcsimDiagnostic(
                    "FakePlcsim.RunControl.ObservationFailed",
                    PlcsimDiagnosticSeverity.Error,
                    "Observation failed.",
                    "RunControlObservation",
                    "evidence-observation")
            },
            Array.Empty<PlcsimEvidence>()));

        var second = await service.ValidateAndObserveAsync(CancellationToken.None);

        Assert.True(first.IsSuccessful);
        Assert.False(second.IsSuccessful);
        Assert.Equal(UnitTestLifecycleState.Completed, service.CurrentObservation.State);
        Assert.True(service.CurrentObservation.Completed);
        Assert.Equal(AccessHealthState.RevalidationRequired, service.Health.State);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "FakePlcsim.RunControl.ObservationFailed");
    }

    [Fact]
    public async Task Siemens_plcsim_lifecycle_service_invalidates_health_when_runcontrol_runtime_is_unavailable()
    {
        var service = new SiemensPlcsimUnitTestLifecycleService(TimeProvider.System);
        var diagnostics = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        service.BindCommunicationSession(new RecordingPlcsimCommunicationSession(
            observationResult: new PlcsimRunControlObservationResult(
                PlcsimOperationStatus.Unavailable,
                null,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V8.RunControlRuntimeNotRunning",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V8 runtime is Stop; RunControl communication requires a running PLC runtime.",
                        "ObserveRunControl",
                        "plcsim-advanced-v8-runcontrol-runtime")
                },
                Array.Empty<PlcsimEvidence>())));

        var result = await service.ValidateAndObserveAsync(CancellationToken.None);

        Assert.False(result.IsSuccessful);
        Assert.Equal(AccessHealthState.Invalidated, service.Health.State);
        Assert.Equal(AccessHealthProblem.ContextLost, service.Health.Problem);
        Assert.Equal("Siemens.PlcsimAdvanced.RunControl.ObservationFailed", service.Health.ReasonCode);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V8.RunControlRuntimeNotRunning");
    }

    [Fact]
    public async Task Siemens_plcsim_lifecycle_service_invalidates_health_when_command_fails()
    {
        var service = new SiemensPlcsimUnitTestLifecycleService(TimeProvider.System);
        var communicationSession = new RecordingPlcsimCommunicationSession(
            commandResult: new PlcsimRunControlCommandResult(
                PlcsimOperationStatus.TimedOut,
                UnitTestRunCommand.Start,
                Accepted: false,
                new[]
                {
                    new PlcsimDiagnostic(
                        "FakePlcsim.RunControl.CommandTimedOut",
                        PlcsimDiagnosticSeverity.Error,
                        "Command timed out.",
                        "RunControlCommand",
                        "evidence-command")
                },
                Array.Empty<PlcsimEvidence>()));
        var diagnostics = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        service.BindCommunicationSession(communicationSession);
        var context = new UnitTestContext(
            new CommunicationSession(new CommunicationSessionId("communication-session-1"), DateTimeOffset.UtcNow),
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));

        var start = await service.StartAsync(context, CancellationToken.None);

        Assert.False(start.IsSuccessful);
        Assert.Equal(new[] { UnitTestRunCommand.Start }, communicationSession.Commands);
        Assert.Equal(AccessHealthState.RevalidationRequired, service.Health.State);
        Assert.Equal(AccessHealthProblem.RevalidationRequired, service.Health.Problem);
        Assert.Equal("Siemens.PlcsimAdvanced.RunControl.CommandFailed", service.Health.ReasonCode);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "FakePlcsim.RunControl.CommandTimedOut");
    }

    [Fact]
    public async Task Siemens_plcsim_lifecycle_service_reports_cleanup_failures()
    {
        var service = new SiemensPlcsimUnitTestLifecycleService(TimeProvider.System);
        var communicationSession = new RecordingPlcsimCommunicationSession(
            closeException: new IOException("Communication close failed."));
        var diagnostics = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        service.BindCommunicationSession(communicationSession);

        var result = await service.ReleaseCommunicationAsync(CancellationToken.None);

        Assert.False(result.IsSuccessful);
        Assert.Equal(1, communicationSession.CloseCount);
        Assert.Equal(AccessHealthState.Invalidated, service.Health.State);
        Assert.Equal(AccessHealthProblem.ContextLost, service.Health.Problem);
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == "Siemens.PlcsimAdvanced.CommunicationCleanupFailed"
            && diagnostic.Category == DiagnosticCategory.Cleanup
            && diagnostic.Phase == DiagnosticPhase.Cleanup);
    }

    [Fact]
    public async Task Siemens_plcsim_lifecycle_service_release_unwires_communication_session_events()
    {
        var service = new SiemensPlcsimUnitTestLifecycleService(TimeProvider.System);
        var communicationSession = new RecordingPlcsimCommunicationSession();
        service.BindCommunicationSession(communicationSession);

        var result = await service.ReleaseCommunicationAsync(CancellationToken.None);
        communicationSession.RaiseHealth(
            PlcsimCommunicationHealthState.Invalidated,
            "Late communication invalidation.");

        Assert.True(result.IsSuccessful);
        Assert.Equal(1, communicationSession.CloseCount);
        Assert.NotEqual(AccessHealthState.Invalidated, service.Health.State);
        Assert.Equal(UnitTestExecutionStatus.Unknown, service.CurrentObservation.ExecutionStatus);
        Assert.Equal(UnitTestExecutionSignals.None, service.CurrentObservation.ExecutionSignals);
        Assert.Equal(UnitTestLifecycleState.Unknown, service.CurrentObservation.State);
        Assert.Equal(UnitTestResultState.NotEvaluated, service.CurrentObservation.Result);
        Assert.False(service.CurrentObservation.Ready);
    }

    [Fact]
    public async Task Siemens_plcsim_lifecycle_service_release_waits_for_running_command()
    {
        var service = new SiemensPlcsimUnitTestLifecycleService(TimeProvider.System);
        var commandStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueCommand = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var communicationSession = new RecordingPlcsimCommunicationSession(
            commandStarted: commandStarted,
            commandDelay: continueCommand.Task);
        service.BindCommunicationSession(communicationSession);
        var context = new UnitTestContext(
            new CommunicationSession(new CommunicationSessionId("communication-session-1"), DateTimeOffset.UtcNow),
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));

        var startTask = service.StartAsync(context, CancellationToken.None);
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var releaseTask = service.ReleaseCommunicationAsync(CancellationToken.None);

        Assert.False(releaseTask.IsCompleted);
        Assert.Equal(0, communicationSession.CloseCount);

        continueCommand.SetResult(null);
        var start = await startTask;
        var release = await releaseTask;

        Assert.True(start.IsSuccessful);
        Assert.True(release.IsSuccessful);
        Assert.Equal(1, communicationSession.CloseCount);
        Assert.Equal(new[] { UnitTestRunCommand.Start }, communicationSession.Commands);
    }

    [Fact]
    public async Task Siemens_plcsim_lifecycle_service_cleanup_waits_for_running_observation()
    {
        var service = new SiemensPlcsimUnitTestLifecycleService(TimeProvider.System);
        var observationStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueObservation = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var communicationSession = new RecordingPlcsimCommunicationSession(
            observationStarted: observationStarted,
            observationDelay: continueObservation.Task);
        service.BindCommunicationSession(communicationSession);

        var observeTask = service.ValidateAndObserveAsync(CancellationToken.None);
        await observationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cleanupTask = service.CleanupAsync(CancellationToken.None);

        Assert.False(cleanupTask.IsCompleted);
        Assert.Equal(0, communicationSession.CloseCount);

        continueObservation.SetResult(null);
        var observe = await observeTask;
        var cleanup = await cleanupTask;

        Assert.True(observe.IsSuccessful);
        Assert.True(cleanup.IsSuccessful);
        Assert.Equal(1, communicationSession.CloseCount);
        Assert.Equal(AccessHealthState.Disposed, service.Health.State);
    }

}
