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
    public async Task Siemens_session_prepares_unit_test_communication_and_binds_event_stream_service()
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
        var communication = await session.PrepareUnitTestCommunicationAsync(
            new CommunicationPrepareRequest(
                CreateCommunicationConfig(),
                CreateProjectExecutionContext(),
                new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
                new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1")),
            CancellationToken.None);
        var context = new UnitTestContext(
            communication.Value!,
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));

        var monitoring = await session.UnitTestEventStream.StartMonitoringAsync(context, CancellationToken.None);

        Assert.True(monitoring.IsSuccessful);
        Assert.True(monitoring.Value!.IsMonitoring);
        Assert.Equal("communication-session-1", monitoring.Value.SessionId.Value);
        Assert.Equal(1, communicationSession.ValidateEventBufferCount);
        Assert.Equal(1, communicationSession.StartMonitoringCount);
    }

    [Fact]
    public async Task Siemens_session_closes_shared_communication_once_after_both_adapters_release()
    {
        var communicationSession = new RecordingPlcsimCommunicationSession();
        var session = CreateSessionWithCommunication(communicationSession);
        var lifecycleObservations = new List<UnitTestRunControlObservation>();
        var eventBatches = new List<UnitTestEventBatch>();
        session.UnitTestLifecycle.RunControlObservationChanged += (_, args) => lifecycleObservations.Add(args.Observation);
        session.UnitTestEventStream.EventBatchReceivedEventHandler += (_, args) => eventBatches.Add(args.Batch);
        var prepared = await PrepareCommunicationAsync(session);
        Assert.True(prepared.IsSuccessful);
        lifecycleObservations.Clear();

        var eventStreamRelease = await session.UnitTestEventStream.ReleaseCommunicationAsync(CancellationToken.None);
        Assert.True(eventStreamRelease.IsSuccessful);
        Assert.Equal(0, communicationSession.CloseCount);

        var lifecycleRelease = await session.UnitTestLifecycle.ReleaseCommunicationAsync(CancellationToken.None);
        communicationSession.RaiseObservation(new PlcsimRunControlObservation(
            UnitTestExecutionStatus.Running,
            UnitTestExecutionSignals.Running,
            UnitTestLifecycleState.Running,
            UnitTestResultState.NotEvaluated));
        communicationSession.RaiseBatch(CreateUnitTestEventBatch());
        communicationSession.RaiseHealth(
            PlcsimCommunicationHealthState.Invalidated,
            "Late communication invalidation.");
        await session.UnitTestEventStream.ReleaseCommunicationAsync(CancellationToken.None);
        await session.UnitTestLifecycle.ReleaseCommunicationAsync(CancellationToken.None);

        Assert.True(lifecycleRelease.IsSuccessful);
        Assert.Equal(1, communicationSession.CloseCount);
        Assert.Empty(lifecycleObservations);
        Assert.Empty(eventBatches);
        Assert.Equal(AccessHealthState.NotEstablished, session.UnitTestLifecycle.Health.State);
        Assert.Equal(AccessHealthState.NotEstablished, session.UnitTestEventStream.Health.State);

        await session.DisposeAsync();
        Assert.Equal(1, communicationSession.CloseCount);
    }

    [Fact]
    public async Task Siemens_session_closes_shared_communication_after_monitoring_stop_failure()
    {
        var failedStopState = new PlcsimEventStreamMonitoringState(
            "communication-session-1",
            IsMonitoring: true,
            EventBufferReadState.Valid,
            EventStreamBatchReleaseState.Pending,
            BufferObservedEmpty: false,
            LostEventCount: 0);
        var communicationSession = new RecordingPlcsimCommunicationSession(
            stopMonitoringResult: new PlcsimEventStreamMonitoringResult(
                PlcsimOperationStatus.Failed,
                failedStopState,
                [
                    new PlcsimDiagnostic(
                        "FakePlcsim.EventStream.StopFailed",
                        PlcsimDiagnosticSeverity.Error,
                        "Monitoring stop failed.",
                        "StopEventStreamMonitoring",
                        "stop-failure")
                ],
                Array.Empty<PlcsimEvidence>()));
        var session = CreateSessionWithCommunication(communicationSession);
        var prepared = await PrepareCommunicationAsync(session);
        Assert.True(prepared.IsSuccessful);
        var context = new UnitTestContext(
            prepared.Value!,
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));
        var monitoring = await session.UnitTestEventStream.StartMonitoringAsync(context, CancellationToken.None);
        Assert.True(monitoring.IsSuccessful);

        var eventStreamRelease = await session.UnitTestEventStream.ReleaseCommunicationAsync(CancellationToken.None);
        var lifecycleRelease = await session.UnitTestLifecycle.ReleaseCommunicationAsync(CancellationToken.None);

        Assert.False(eventStreamRelease.IsSuccessful);
        Assert.True(lifecycleRelease.IsSuccessful);
        Assert.Equal(1, communicationSession.StopMonitoringCount);
        Assert.Equal(1, communicationSession.CloseCount);
        Assert.False(session.UnitTestEventStream.CurrentMonitoringState.IsMonitoring);
        Assert.Equal(new CommunicationSessionId("not-started"), session.UnitTestEventStream.CurrentMonitoringState.SessionId);
    }

    [Fact]
    public async Task Siemens_session_dispose_releases_remaining_communication_after_reconnect_release_failure()
    {
        var failedStopState = new PlcsimEventStreamMonitoringState(
            "communication-session-1",
            IsMonitoring: true,
            EventBufferReadState.Valid,
            EventStreamBatchReleaseState.Pending,
            BufferObservedEmpty: false,
            LostEventCount: 0);
        var communicationSession = new RecordingPlcsimCommunicationSession(
            stopMonitoringResult: new PlcsimEventStreamMonitoringResult(
                PlcsimOperationStatus.Failed,
                failedStopState,
                [
                    new PlcsimDiagnostic(
                        "FakePlcsim.EventStream.StopFailed",
                        PlcsimDiagnosticSeverity.Error,
                        "Monitoring stop failed.",
                        "StopEventStreamMonitoring",
                        "stop-failure")
                ],
                Array.Empty<PlcsimEvidence>()));
        var session = CreateSessionWithCommunication(communicationSession);
        var prepared = await PrepareCommunicationAsync(session);
        Assert.True(prepared.IsSuccessful);
        var context = new UnitTestContext(
            prepared.Value!,
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));
        var monitoring = await session.UnitTestEventStream.StartMonitoringAsync(
            context,
            CancellationToken.None);
        Assert.True(monitoring.IsSuccessful);

        var reconnect = await session.ReconnectRuntimeAsync(
            new PlcRuntimeReconnectRequest(
                CreateCommunicationConfig(),
                RuntimeProvisioningMode.UseExisting,
                context.Runtime,
                context.RuntimeTarget),
            CancellationToken.None);

        Assert.True(reconnect.IsUnsuccessful);
        Assert.Equal(0, communicationSession.CloseCount);
        Assert.Equal(AccessHealthState.NotEstablished, session.UnitTestEventStream.Health.State);
        Assert.Equal(AccessHealthState.Healthy, session.UnitTestLifecycle.Health.State);

        await session.DisposeAsync();

        Assert.Equal(1, communicationSession.CloseCount);
        Assert.Equal(AccessHealthState.Disposed, session.Health.State);
    }

    [Fact]
    public async Task Siemens_session_shared_communication_close_is_independent_of_adapter_release_order()
    {
        var communicationSession = new RecordingPlcsimCommunicationSession();
        var session = CreateSessionWithCommunication(communicationSession);
        var prepared = await PrepareCommunicationAsync(session);
        Assert.True(prepared.IsSuccessful);

        var lifecycleRelease = await session.UnitTestLifecycle.ReleaseCommunicationAsync(CancellationToken.None);
        Assert.True(lifecycleRelease.IsSuccessful);
        Assert.Equal(0, communicationSession.CloseCount);

        var eventStreamRelease = await session.UnitTestEventStream.ReleaseCommunicationAsync(CancellationToken.None);

        Assert.True(eventStreamRelease.IsSuccessful);
        Assert.Equal(1, communicationSession.CloseCount);
    }

    [Fact]
    public async Task Siemens_session_dispose_reports_service_cleanup_failures()
    {
        var communicationSession = new RecordingPlcsimCommunicationSession(
            closeException: new InvalidOperationException("Communication close failed."));
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
            loggerFactory.CreateLogger("test"));
        var diagnostics = new List<Diagnostic>();
        session.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        var communication = await session.PrepareUnitTestCommunicationAsync(
            new CommunicationPrepareRequest(
                CreateCommunicationConfig(),
                CreateProjectExecutionContext(),
                new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
                new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1")),
            CancellationToken.None);
        Assert.True(communication.IsSuccessful);

        await session.DisposeAsync();

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.ServiceCleanupFailed");
        Assert.Contains(
            loggerFactory.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("service cleanup returned failure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            loggerFactory.Entries,
            entry => entry.Level == LogLevel.Information
                && entry.Message.Contains("session dispose completed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Siemens_session_dispose_reports_plc_control_cleanup_failures()
    {
        var runtimeSession = new RecordingPlcsimRuntimeSession(
            CreateRuntimeCandidate(),
            closeException: new IOException("Runtime close failed."));
        var loggerFactory = new SiemensTestLoggerFactory();
        var session = new SiemensAccessProfileSession(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensSelectedBindings(
                new TestTiaBinding(),
                new RecordingPlcsimBinding(runtimeSession),
                new RecordingPlcsimCommunicationBinding(new RecordingPlcsimCommunicationSession())),
            ProjectOwnershipMode.ApplicationManaged,
            CreateResourceScope(),
            TimeProvider.System,
            loggerFactory.CreateLogger("test"));
        var sessionDiagnostics = new List<Diagnostic>();
        var plcControlDiagnostics = new List<Diagnostic>();
        session.DiagnosticEventHandler += (_, args) => sessionDiagnostics.Add(args.Diagnostic);
        session.PlcControl.DiagnosticEventHandler += (_, args) => plcControlDiagnostics.Add(args.Diagnostic);

        var prepared = await session.PlcControl.PrepareRuntimeAsync(
            CreateRuntimePrepareRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty),
            CancellationToken.None);
        await session.DisposeAsync();

        Assert.True(prepared.IsSuccessful);
        Assert.Equal(1, runtimeSession.CloseCount);
        Assert.Contains(plcControlDiagnostics, diagnostic => diagnostic.Code == "Siemens.PlcsimAdvanced.RuntimeCleanupFailed");
        Assert.DoesNotContain(sessionDiagnostics, diagnostic => diagnostic.Code == "Siemens.PlcsimAdvanced.RuntimeCleanupFailed");
        Assert.Contains(sessionDiagnostics, diagnostic => diagnostic.Code == "SiemensProfile.ServiceCleanupFailed");
        Assert.Contains(
            loggerFactory.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("service cleanup returned failure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Siemens_session_dispose_continues_after_unexpected_service_cleanup_failure()
    {
        var runtimeSession = new RecordingPlcsimRuntimeSession(
            CreateRuntimeCandidate(),
            closeException: new ApplicationException("Unexpected runtime close failure."));
        var cleanupOrder = new List<string>();
        var resourceScope = CreateResourceScope();
        resourceScope.OwnOptionalDisposable(
            "tail resource",
            new RecordingAsyncDisposable("tail", cleanupOrder));
        var session = new SiemensAccessProfileSession(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensSelectedBindings(
                new TestTiaBinding(),
                new RecordingPlcsimBinding(runtimeSession),
                new RecordingPlcsimCommunicationBinding(new RecordingPlcsimCommunicationSession())),
            ProjectOwnershipMode.ApplicationManaged,
            resourceScope,
            TimeProvider.System,
            new SiemensTestLoggerFactory().CreateLogger("test"));
        var diagnostics = new List<Diagnostic>();
        session.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        var prepared = await session.PlcControl.PrepareRuntimeAsync(
            CreateRuntimePrepareRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty),
            CancellationToken.None);
        Assert.True(prepared.IsSuccessful);

        await session.DisposeAsync();

        Assert.Equal(new[] { "tail" }, cleanupOrder);
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code == "SiemensProfile.ServiceCleanupFailed"
                && diagnostic.Message.Contains("PLC control service", StringComparison.Ordinal));
        Assert.Equal(AccessHealthState.Disposed, session.Health.State);
    }

    [Fact]
    public async Task Siemens_session_forwards_communication_health_invalidation_for_lifecycle_and_event_stream()
    {
        var communicationSession = new RecordingPlcsimCommunicationSession();
        var session = new SiemensAccessProfileSession(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensSelectedBindings(
                new TestTiaBinding(),
                new RecordingPlcsimBinding(new RecordingPlcsimRuntimeSession(CreateRuntimeCandidate())),
                new RecordingPlcsimCommunicationBinding(communicationSession)),
            ProjectOwnershipMode.ApplicationManaged,
            CreateResourceScope(),
            TimeProvider.System,
            new SiemensTestLoggerFactory().CreateLogger("test"));
        var updates = new List<AccessServiceHealthUpdate>();
        session.HealthChangedEventHandler += (_, args) => updates.AddRange(args.Updates);
        var communication = await session.PrepareUnitTestCommunicationAsync(
            new CommunicationPrepareRequest(
                CreateCommunicationConfig(),
                CreateProjectExecutionContext(),
                new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
                new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1")),
            CancellationToken.None);
        Assert.True(communication.IsSuccessful);
        updates.Clear();

        communicationSession.RaiseHealth(
            PlcsimCommunicationHealthState.Invalidated,
            "Communication invalidated.");

        Assert.Contains(updates, update => update.ServiceKind == AccessServiceKind.UnitTestLifecycle
            && update.Health.State == AccessHealthState.Invalidated);
        Assert.Contains(updates, update => update.ServiceKind == AccessServiceKind.UnitTestEventStream
            && update.Health.State == AccessHealthState.Invalidated);
    }

    [Fact]
    public async Task Siemens_session_projects_terminal_communication_to_safe_states_and_ignores_late_callbacks()
    {
        var communicationSession = new RecordingPlcsimCommunicationSession();
        var session = CreateSessionWithCommunication(communicationSession);
        var prepared = await PrepareCommunicationAsync(session);
        Assert.True(prepared.IsSuccessful);
        var context = new UnitTestContext(
            prepared.Value!,
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));
        var monitoring = await session.UnitTestEventStream.StartMonitoringAsync(context, CancellationToken.None);
        Assert.True(monitoring.IsSuccessful);
        var observations = new List<UnitTestRunControlObservation>();
        var batches = new List<UnitTestEventBatch>();
        var stateBeforeFault = session.UnitTestEventStream.CurrentMonitoringState;
        session.UnitTestLifecycle.RunControlObservationChanged += (_, args) => observations.Add(args.Observation);
        session.UnitTestEventStream.EventBatchReceivedEventHandler += (_, args) => batches.Add(args.Batch);

        communicationSession.RaiseHealth(
            PlcsimCommunicationHealthState.Invalidated,
            "Communication session became terminal.");
        communicationSession.RaiseHealth(
            PlcsimCommunicationHealthState.Healthy,
            "Late healthy callback.");
        communicationSession.RaiseObservation(new PlcsimRunControlObservation(
            UnitTestExecutionStatus.Running,
            UnitTestExecutionSignals.Ready | UnitTestExecutionSignals.Running,
            UnitTestLifecycleState.Running,
            UnitTestResultState.NotEvaluated));
        communicationSession.RaiseBatch(CreateUnitTestEventBatch());
        var command = await session.UnitTestLifecycle.StartAsync(context, CancellationToken.None);
        var restartMonitoring = await session.UnitTestEventStream.StartMonitoringAsync(context, CancellationToken.None);

        Assert.Equal(AccessHealthState.Invalidated, session.UnitTestLifecycle.Health.State);
        Assert.Equal(AccessHealthState.Invalidated, session.UnitTestEventStream.Health.State);
        Assert.Equal(UnitTestExecutionStatus.Unknown, session.UnitTestLifecycle.CurrentObservation.ExecutionStatus);
        Assert.Equal(UnitTestExecutionSignals.None, session.UnitTestLifecycle.CurrentObservation.ExecutionSignals);
        Assert.Equal(UnitTestLifecycleState.Unknown, session.UnitTestLifecycle.CurrentObservation.State);
        Assert.False(session.UnitTestLifecycle.CurrentObservation.Ready);
        Assert.False(session.UnitTestEventStream.CurrentMonitoringState.IsMonitoring);
        Assert.Equal(EventBufferReadState.SessionInvalid, session.UnitTestEventStream.CurrentMonitoringState.LastReadState);
        Assert.Equal(stateBeforeFault.BatchReleaseState, session.UnitTestEventStream.CurrentMonitoringState.BatchReleaseState);
        Assert.Equal(stateBeforeFault.BufferObservedEmpty, session.UnitTestEventStream.CurrentMonitoringState.BufferObservedEmpty);
        Assert.Equal(stateBeforeFault.ProducerSequenceId, session.UnitTestEventStream.CurrentMonitoringState.ProducerSequenceId);
        Assert.Equal(stateBeforeFault.DeletedSequenceId, session.UnitTestEventStream.CurrentMonitoringState.DeletedSequenceId);
        Assert.Single(observations);
        Assert.Equal(UnitTestLifecycleState.Unknown, observations[0].State);
        Assert.Empty(batches);
        Assert.False(command.IsSuccessful);
        Assert.False(restartMonitoring.IsSuccessful);
        Assert.Empty(communicationSession.Commands);
        Assert.Equal(1, communicationSession.StartMonitoringCount);

        var eventStreamRelease = await session.UnitTestEventStream.ReleaseCommunicationAsync(CancellationToken.None);
        var lifecycleRelease = await session.UnitTestLifecycle.ReleaseCommunicationAsync(CancellationToken.None);

        Assert.True(eventStreamRelease.IsSuccessful);
        Assert.True(lifecycleRelease.IsSuccessful);
        Assert.Equal(0, communicationSession.StopMonitoringCount);
        Assert.Equal(1, communicationSession.CloseCount);
    }

    [Fact]
    public async Task Siemens_lifecycle_rejects_stale_command_completion_after_terminal_health()
    {
        var commandStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueCommand = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var communicationSession = new RecordingPlcsimCommunicationSession(
            commandStarted: commandStarted,
            commandDelay: continueCommand.Task);
        var session = CreateSessionWithCommunication(communicationSession);
        var prepared = await PrepareCommunicationAsync(session);
        Assert.True(prepared.IsSuccessful);
        var context = new UnitTestContext(
            prepared.Value!,
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));

        var commandTask = session.UnitTestLifecycle.StartAsync(context, CancellationToken.None);
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        communicationSession.RaiseHealth(
            PlcsimCommunicationHealthState.Faulted,
            "Communication faulted while command was in flight.");
        continueCommand.SetResult(null);
        var command = await commandTask;

        Assert.False(command.IsSuccessful);
        Assert.Equal(AccessHealthState.Invalidated, session.UnitTestLifecycle.Health.State);
        Assert.Equal(UnitTestLifecycleState.Unknown, session.UnitTestLifecycle.CurrentObservation.State);
        Assert.False(session.UnitTestLifecycle.CurrentObservation.Ready);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Siemens_session_keeps_revalidating_communication_usable()
    {
        var communicationSession = new RecordingPlcsimCommunicationSession();
        var session = CreateSessionWithCommunication(communicationSession);
        var prepared = await PrepareCommunicationAsync(session);
        Assert.True(prepared.IsSuccessful);
        var observations = new List<UnitTestRunControlObservation>();
        session.UnitTestLifecycle.RunControlObservationChanged += (_, args) => observations.Add(args.Observation);

        communicationSession.RaiseHealth(
            PlcsimCommunicationHealthState.RevalidationRequired,
            "Communication context is being revalidated.");
        communicationSession.RaiseObservation(new PlcsimRunControlObservation(
            UnitTestExecutionStatus.Completed,
            UnitTestExecutionSignals.Completed,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Passed));
        communicationSession.RaiseHealth(
            PlcsimCommunicationHealthState.Healthy,
            "Communication context recovered.");

        Assert.Equal(AccessHealthState.Healthy, session.UnitTestLifecycle.Health.State);
        Assert.Equal(AccessHealthState.Healthy, session.UnitTestEventStream.Health.State);
        Assert.Equal(UnitTestLifecycleState.Completed, session.UnitTestLifecycle.CurrentObservation.State);
        Assert.Single(observations);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Siemens_session_keeps_profile_health_independent_from_isolated_service_failure()
    {
        var runtimeSession = new RecordingPlcsimRuntimeSession(CreateRuntimeCandidate());
        var session = new SiemensAccessProfileSession(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensSelectedBindings(
                new TestTiaBinding(),
                new RecordingPlcsimBinding(runtimeSession),
                new RecordingPlcsimCommunicationBinding(new RecordingPlcsimCommunicationSession())),
            ProjectOwnershipMode.ApplicationManaged,
            CreateResourceScope(),
            TimeProvider.System,
            new SiemensTestLoggerFactory().CreateLogger("test"));
        var updates = new List<AccessServiceHealthUpdate>();
        session.HealthChangedEventHandler += (_, args) => updates.AddRange(args.Updates);
        var runtime = await session.PlcControl.PrepareRuntimeAsync(
            CreateRuntimePrepareRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty),
            CancellationToken.None);
        Assert.True(runtime.IsSuccessful);
        updates.Clear();

        runtimeSession.RaiseHealth(
            PlcsimRuntimeHealthState.Invalidated,
            "Runtime invalidated.");

        var update = Assert.Single(updates);
        Assert.Equal(AccessServiceKind.PlcControl, update.ServiceKind);
        Assert.Equal(AccessHealthState.Invalidated, update.Health.State);
        Assert.Equal(AccessHealthState.Healthy, session.Health.State);
        Assert.NotEqual(AccessHealthState.Invalidated, session.UnitTestLifecycle.Health.State);
        Assert.NotEqual(AccessHealthState.Invalidated, session.UnitTestEventStream.Health.State);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Siemens_session_dispose_is_idempotent_and_stops_forwarding_service_health()
    {
        var runtimeSession = new RecordingPlcsimRuntimeSession(CreateRuntimeCandidate());
        var communicationSession = new RecordingPlcsimCommunicationSession();
        var disposedCallbackCount = 0;
        var session = new SiemensAccessProfileSession(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensSelectedBindings(
                new TestTiaBinding(),
                new RecordingPlcsimBinding(runtimeSession),
                new RecordingPlcsimCommunicationBinding(communicationSession)),
            ProjectOwnershipMode.ApplicationManaged,
            CreateResourceScope(),
            TimeProvider.System,
            new SiemensTestLoggerFactory().CreateLogger("test"),
            disposedCallback: () => disposedCallbackCount++);
        var updates = new List<AccessServiceHealthUpdate>();
        session.HealthChangedEventHandler += (_, args) => updates.AddRange(args.Updates);
        var runtime = await session.PlcControl.PrepareRuntimeAsync(
            CreateRuntimePrepareRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty),
            CancellationToken.None);
        var communication = await PrepareCommunicationAsync(session);
        Assert.True(runtime.IsSuccessful);
        Assert.True(communication.IsSuccessful);

        var firstDispose = session.DisposeAsync().AsTask();
        var secondDispose = session.DisposeAsync().AsTask();
        await Task.WhenAll(firstDispose, secondDispose);
        updates.Clear();
        runtimeSession.RaiseHealth(PlcsimRuntimeHealthState.Invalidated, "Late runtime invalidation.");
        runtimeSession.RaiseRuntimeState(
            PlcsimRuntimeStateChangeKind.ConnectionLost,
            PlcsimRuntimeControlState.Invalid,
            "Late runtime state.");
        communicationSession.RaiseHealth(
            PlcsimCommunicationHealthState.Invalidated,
            "Late communication invalidation.");

        Assert.Equal(AccessHealthState.Disposed, session.Health.State);
        Assert.Empty(updates);
        Assert.Equal(1, runtimeSession.CloseCount);
        Assert.Equal(1, communicationSession.CloseCount);
        Assert.Equal(1, disposedCallbackCount);
    }

    [Fact]
    public async Task Siemens_plcsim_event_stream_service_maps_monitoring_batches_and_diagnostics()
    {
        var service = new SiemensPlcsimUnitTestEventStreamService(TimeProvider.System);
        var diagnostic = new PlcsimDiagnostic(
            "FakePlcsim.EventStream.MonitoringWarning",
            PlcsimDiagnosticSeverity.Warning,
            "Monitoring warning.",
            "StartEventStreamMonitoring",
            "evidence-1");
        var communicationSession = new RecordingPlcsimCommunicationSession(monitoringDiagnostics: new[] { diagnostic });
        var received = new List<UnitTestEventBatch>();
        var diagnostics = new List<Diagnostic>();
        service.EventBatchReceivedEventHandler += (_, args) => received.Add(args.Batch);
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        service.BindCommunicationSession(communicationSession);
        var context = new UnitTestContext(
            new CommunicationSession(new CommunicationSessionId("communication-session-1"), DateTimeOffset.UtcNow),
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));

        var start = await service.StartMonitoringAsync(context, CancellationToken.None);
        communicationSession.RaiseBatch(CreateUnitTestEventBatch());
        var batchState = service.CurrentMonitoringState;
        var stop = await service.StopMonitoringAsync(CancellationToken.None);

        Assert.True(start.IsSuccessful);
        Assert.True(start.Value!.IsMonitoring);
        Assert.Equal(EventBufferReadState.Empty, start.Value.LastReadState);
        Assert.Single(received);
        Assert.Equal(EventBufferReadState.Valid, batchState.LastReadState);
        Assert.False(batchState.BufferObservedEmpty);
        Assert.True(stop.IsSuccessful);
        Assert.False(service.CurrentMonitoringState.IsMonitoring);
        Assert.Equal(1, communicationSession.ValidateEventBufferCount);
        Assert.Equal(1, communicationSession.StartMonitoringCount);
        Assert.Equal(1, communicationSession.StopMonitoringCount);
        Assert.Contains(diagnostics, entry => entry.Code == "FakePlcsim.EventStream.MonitoringWarning"
            && entry.Phase == DiagnosticPhase.ExecutionMonitoring
            && entry.Correlation.ServiceId == "Siemens.PlcsimAdvanced");
    }

    [Fact]
    public async Task Siemens_plcsim_event_stream_service_maps_accepted_event_buffer_reset()
    {
        var service = new SiemensPlcsimUnitTestEventStreamService(TimeProvider.System);
        var communicationSession = new RecordingPlcsimCommunicationSession();
        service.BindCommunicationSession(communicationSession);
        var context = new UnitTestContext(
            new CommunicationSession(new CommunicationSessionId("communication-session-1"), DateTimeOffset.UtcNow),
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));

        var result = await service.ResetEventBufferAsync(context, CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.True(result.Value!.Accepted);
        Assert.Equal(1, communicationSession.EventBufferResetCount);
        Assert.Equal(0, communicationSession.CloseCount);
        Assert.Equal(AccessHealthState.Healthy, service.Health.State);
    }

    [Fact]
    public async Task Siemens_plcsim_event_stream_service_maps_rejected_event_buffer_reset_without_releasing_session()
    {
        var diagnostic = new PlcsimDiagnostic(
            "FakePlcsim.EventBufferReset.Rejected",
            PlcsimDiagnosticSeverity.Warning,
            "EventBuffer reset write was rejected.",
            "ResetEventBuffer",
            "reset-rejected");
        var communicationSession = new RecordingPlcsimCommunicationSession(
            eventBufferResetResult: new PlcsimEventBufferResetResult(
                PlcsimOperationStatus.Rejected,
                Accepted: false,
                new[] { diagnostic },
                Array.Empty<PlcsimEvidence>()));
        var service = new SiemensPlcsimUnitTestEventStreamService(TimeProvider.System);
        var diagnostics = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        service.BindCommunicationSession(communicationSession);
        var context = new UnitTestContext(
            new CommunicationSession(new CommunicationSessionId("communication-session-1"), DateTimeOffset.UtcNow),
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));

        var result = await service.ResetEventBufferAsync(context, CancellationToken.None);

        Assert.True(result.IsUnsuccessful);
        Assert.Equal(1, communicationSession.EventBufferResetCount);
        Assert.Equal(0, communicationSession.CloseCount);
        Assert.Equal(AccessHealthState.Healthy, service.Health.State);
        Assert.Contains(diagnostics, entry => entry.Code == diagnostic.Code
            && entry.Phase == DiagnosticPhase.ExecutionMonitoring);
    }

    [Fact]
    public async Task Siemens_plcsim_event_stream_service_maps_unsupported_event_buffer_reset_without_releasing_session()
    {
        var diagnostic = new PlcsimDiagnostic(
            "FakePlcsim.EventBufferReset.NotSupported",
            PlcsimDiagnosticSeverity.Warning,
            "EventBuffer reset is not supported.",
            "ResetEventBuffer",
            "reset-not-supported");
        var communicationSession = new RecordingPlcsimCommunicationSession(
            eventBufferResetResult: new PlcsimEventBufferResetResult(
                PlcsimOperationStatus.Unavailable,
                Accepted: false,
                new[] { diagnostic },
                Array.Empty<PlcsimEvidence>()));
        var service = new SiemensPlcsimUnitTestEventStreamService(TimeProvider.System);
        var diagnostics = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        service.BindCommunicationSession(communicationSession);
        var context = new UnitTestContext(
            new CommunicationSession(new CommunicationSessionId("communication-session-1"), DateTimeOffset.UtcNow),
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));

        var result = await service.ResetEventBufferAsync(context, CancellationToken.None);

        Assert.Equal(OperationResultState.NotSupported, result.State);
        Assert.False(result.Value!.Accepted);
        Assert.Equal(1, communicationSession.EventBufferResetCount);
        Assert.Equal(0, communicationSession.CloseCount);
        Assert.Equal(AccessHealthState.Healthy, service.Health.State);
        Assert.Contains(diagnostics, entry => entry.Code == diagnostic.Code
            && entry.Phase == DiagnosticPhase.ExecutionMonitoring);
    }

    [Fact]
    public async Task Siemens_plcsim_event_stream_service_keeps_session_on_failed_event_buffer_reset()
    {
        var diagnostic = new PlcsimDiagnostic(
            "FakePlcsim.EventBufferReset.WriteFailed",
            PlcsimDiagnosticSeverity.Error,
            "EventBuffer reset write failed.",
            "ResetEventBuffer",
            "reset-failed");
        var communicationSession = new RecordingPlcsimCommunicationSession(
            eventBufferResetResult: new PlcsimEventBufferResetResult(
                PlcsimOperationStatus.Failed,
                Accepted: false,
                new[] { diagnostic },
                Array.Empty<PlcsimEvidence>()));
        var service = new SiemensPlcsimUnitTestEventStreamService(TimeProvider.System);
        service.BindCommunicationSession(communicationSession);
        var context = new UnitTestContext(
            new CommunicationSession(new CommunicationSessionId("communication-session-1"), DateTimeOffset.UtcNow),
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));

        var result = await service.ResetEventBufferAsync(context, CancellationToken.None);

        Assert.True(result.IsUnsuccessful);
        Assert.Equal(1, communicationSession.EventBufferResetCount);
        Assert.Equal(0, communicationSession.CloseCount);
        Assert.Equal(AccessHealthState.RevalidationRequired, service.Health.State);
        Assert.Equal("Siemens.PlcsimAdvanced.EventStream.ResetFailed", service.Health.ReasonCode);
    }

    [Fact]
    public async Task Siemens_plcsim_event_stream_service_projects_overflow_loss_and_recovery_without_invalidating_health()
    {
        var service = new SiemensPlcsimUnitTestEventStreamService(TimeProvider.System);
        var communicationSession = new RecordingPlcsimCommunicationSession();
        var diagnostics = new List<Diagnostic>();
        var states = new List<EventStreamState>();
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        service.MonitoringStateChangedEventHandler += (_, args) => states.Add(args.CurrentState);
        service.BindCommunicationSession(communicationSession);
        var context = new UnitTestContext(
            new CommunicationSession(new CommunicationSessionId("communication-session-1"), DateTimeOffset.UtcNow),
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));
        var start = await service.StartMonitoringAsync(context, CancellationToken.None);
        Assert.True(start.IsSuccessful);
        states.Clear();

        communicationSession.RaiseEventStreamMonitoringState(new PlcsimEventStreamMonitoringState(
            "communication-session-1",
            IsMonitoring: true,
            EventBufferReadState.RetryRequired,
            EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty: false,
            LostEventCount: 7,
            BufferFull: true,
            ProducerSequenceId: 595,
            DeletedSequenceId: 262));
        communicationSession.RaiseEventStreamMonitoringState(new PlcsimEventStreamMonitoringState(
            "communication-session-1",
            IsMonitoring: true,
            EventBufferReadState.Valid,
            EventStreamBatchReleaseState.Completed,
            BufferObservedEmpty: false,
            LostEventCount: 7,
            BufferFull: true,
            ProducerSequenceId: 595,
            DeletedSequenceId: 262));
        communicationSession.RaiseEventStreamMonitoringState(new PlcsimEventStreamMonitoringState(
            "communication-session-1",
            IsMonitoring: true,
            EventBufferReadState.Empty,
            EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty: true,
            LostEventCount: 0,
            BufferFull: false,
            ProducerSequenceId: 595,
            DeletedSequenceId: 595));

        Assert.Equal(3, states.Count);
        Assert.True(states[0].BufferFull);
        Assert.Equal((uint)595, states[0].ProducerSequenceId);
        Assert.Equal((uint)262, states[0].DeletedSequenceId);
        Assert.False(service.CurrentMonitoringState.BufferFull);
        Assert.Equal((ushort)0, service.CurrentMonitoringState.LostEventCount);
        Assert.Equal(AccessHealthState.Healthy, service.Health.State);
        Assert.Collection(
            diagnostics.Where(diagnostic => diagnostic.Code.StartsWith("Siemens.Plcsim.EventStream.", StringComparison.Ordinal)),
            diagnostic => Assert.Equal("Siemens.Plcsim.EventStream.BufferFull", diagnostic.Code),
            diagnostic => Assert.Equal("Siemens.Plcsim.EventStream.EventsLost", diagnostic.Code),
            diagnostic => Assert.Equal("Siemens.Plcsim.EventStream.RecoveryStarted", diagnostic.Code),
            diagnostic => Assert.Equal("Siemens.Plcsim.EventStream.Recovered", diagnostic.Code));
    }

    [Fact]
    public async Task Siemens_plcsim_event_stream_service_ignores_duplicate_foreign_and_late_monitoring_states()
    {
        var service = new SiemensPlcsimUnitTestEventStreamService(TimeProvider.System);
        var communicationSession = new RecordingPlcsimCommunicationSession();
        var diagnostics = new List<Diagnostic>();
        var states = new List<EventStreamState>();
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        service.MonitoringStateChangedEventHandler += (_, args) => states.Add(args.CurrentState);
        service.BindCommunicationSession(communicationSession);
        var context = new UnitTestContext(
            new CommunicationSession(new CommunicationSessionId("communication-session-1"), DateTimeOffset.UtcNow),
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));
        var start = await service.StartMonitoringAsync(context, CancellationToken.None);
        Assert.True(start.IsSuccessful);
        states.Clear();

        var overflow = new PlcsimEventStreamMonitoringState(
            "communication-session-1",
            IsMonitoring: true,
            EventBufferReadState.RetryRequired,
            EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty: false,
            LostEventCount: 1,
            BufferFull: true,
            ProducerSequenceId: 20,
            DeletedSequenceId: 10);
        communicationSession.RaiseEventStreamMonitoringState(overflow with { CommunicationSessionId = "foreign-session" });
        communicationSession.RaiseEventStreamMonitoringState(overflow);
        communicationSession.RaiseEventStreamMonitoringState(overflow);
        Assert.Single(states);
        var release = await service.ReleaseCommunicationAsync(CancellationToken.None);
        var stateCountAfterRelease = states.Count;
        communicationSession.RaiseEventStreamMonitoringState(overflow with { LostEventCount = 2 });

        Assert.True(release.IsSuccessful);
        Assert.Equal(stateCountAfterRelease, states.Count);
        Assert.Single(diagnostics, diagnostic => diagnostic.Code == "Siemens.Plcsim.EventStream.BufferFull");
        Assert.Single(diagnostics, diagnostic => diagnostic.Code == "Siemens.Plcsim.EventStream.EventsLost");
        Assert.Equal(new CommunicationSessionId("not-started"), service.CurrentMonitoringState.SessionId);
    }

    [Fact]
    public async Task Siemens_plcsim_event_stream_service_invalidates_health_when_monitoring_start_is_unavailable()
    {
        var service = new SiemensPlcsimUnitTestEventStreamService(TimeProvider.System);
        var diagnostics = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        service.BindCommunicationSession(new RecordingPlcsimCommunicationSession(
            startMonitoringResult: new PlcsimEventStreamMonitoringResult(
                PlcsimOperationStatus.Unavailable,
                new PlcsimEventStreamMonitoringState(
                    "communication-session-1",
                    IsMonitoring: false,
                    EventBufferReadState.SessionInvalid,
                    EventStreamBatchReleaseState.Invalidated,
                    BufferObservedEmpty: true,
                    LostEventCount: 0),
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V8.EventStreamRuntimeUnavailable",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced runtime is unavailable for event-stream monitoring.",
                        "StartEventStreamMonitoring",
                        "evidence-eventstream-runtime")
                },
                Array.Empty<PlcsimEvidence>())));
        var context = new UnitTestContext(
            new CommunicationSession(new CommunicationSessionId("communication-session-1"), DateTimeOffset.UtcNow),
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));

        var result = await service.StartMonitoringAsync(context, CancellationToken.None);

        Assert.False(result.IsSuccessful);
        Assert.Equal(AccessHealthState.Invalidated, service.Health.State);
        Assert.Equal(AccessHealthProblem.ContextLost, service.Health.Problem);
        Assert.Equal("Siemens.PlcsimAdvanced.EventStream.MonitoringStartFailed", service.Health.ReasonCode);
        Assert.False(service.CurrentMonitoringState.IsMonitoring);
        Assert.Equal(EventBufferReadState.SessionInvalid, service.CurrentMonitoringState.LastReadState);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V8.EventStreamRuntimeUnavailable");
    }

    [Fact]
    public void Siemens_plcsim_event_stream_service_reports_diagnostic_when_session_health_is_invalidated()
    {
        var service = new SiemensPlcsimUnitTestEventStreamService(TimeProvider.System);
        var communicationSession = new RecordingPlcsimCommunicationSession();
        var diagnostics = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        service.BindCommunicationSession(communicationSession);

        communicationSession.RaiseHealth(
            PlcsimCommunicationHealthState.Invalidated,
            "PlcsimAdvanced.V8.Worker.EventStreamObservationLoopFailed: Event-stream monitoring faulted.");

        Assert.Equal(AccessHealthState.Invalidated, service.Health.State);
        Assert.Equal(AccessHealthProblem.ContextLost, service.Health.Problem);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "Siemens.Plcsim.EventStream.Invalidated"
            && diagnostic.Phase == DiagnosticPhase.ExecutionMonitoring
            && diagnostic.Correlation.ServiceId == "Siemens.PlcsimAdvanced"
            && diagnostic.Message == "PlcsimAdvanced.V8.Worker.EventStreamObservationLoopFailed: Event-stream monitoring faulted.");
    }

    [Fact]
    public async Task Siemens_plcsim_event_stream_service_release_unwires_communication_session_events()
    {
        var service = new SiemensPlcsimUnitTestEventStreamService(TimeProvider.System);
        var communicationSession = new RecordingPlcsimCommunicationSession();
        var received = new List<UnitTestEventBatch>();
        service.EventBatchReceivedEventHandler += (_, args) => received.Add(args.Batch);
        service.BindCommunicationSession(communicationSession);
        var context = new UnitTestContext(
            new CommunicationSession(new CommunicationSessionId("communication-session-1"), DateTimeOffset.UtcNow),
            CreateProjectExecutionContext(),
            new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1"));

        var start = await service.StartMonitoringAsync(context, CancellationToken.None);
        var release = await service.ReleaseCommunicationAsync(CancellationToken.None);
        communicationSession.RaiseBatch(CreateUnitTestEventBatch());
        communicationSession.RaiseHealth(
            PlcsimCommunicationHealthState.Invalidated,
            "Late communication invalidation.");

        Assert.True(start.IsSuccessful);
        Assert.True(release.IsSuccessful);
        Assert.Equal(1, communicationSession.StopMonitoringCount);
        Assert.Equal(1, communicationSession.CloseCount);
        Assert.Empty(received);
        Assert.False(service.CurrentMonitoringState.IsMonitoring);
        Assert.Equal(new CommunicationSessionId("not-started"), service.CurrentMonitoringState.SessionId);
        Assert.NotEqual(AccessHealthState.Invalidated, service.Health.State);
    }

}
