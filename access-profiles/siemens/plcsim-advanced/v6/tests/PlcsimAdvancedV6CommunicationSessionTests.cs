using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;
using static PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V6.PlcsimAdvancedV6RealRuntimeTests;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V6;

public sealed class PlcsimAdvancedV6CommunicationSessionTests
{
    [Fact]
    public async Task V6_worker_session_rejects_communication_prepare_when_runtime_is_not_running()
    {
        var session = await OpenValidationWorkerSessionAsync("communication-runtime-stopped");

        var result = await session.PrepareCommunicationAsync(
            new PlcsimCommunicationPrepareRequest(
                session.SessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                UnitTestContractVersions.EventStreamModel,
                CreateWorkerOptions(),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                "correlation"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Unavailable, result.Status);
        Assert.Null(result.Session);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.Worker.RuntimeNotRunning");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_drains_cancelled_communication_operations_and_remains_reusable()
    {
        var worker = await OpenValidationWorkerSessionAsync(
            "delayed-communication-operations",
            TimeSpan.FromSeconds(10));
        var prepareRequest = CreateCommunicationPrepareRequest(worker.SessionId);
        using var prepareCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var cancelledPrepare = await worker.PrepareCommunicationAsync(
            prepareRequest,
            prepareCancellation.Token);

        Assert.Equal(PlcsimOperationStatus.Cancelled, cancelledPrepare.Status);
        Assert.Null(cancelledPrepare.Session);

        var prepare = await worker.PrepareCommunicationAsync(
            prepareRequest with { CorrelationId = "prepare-retry" },
            CancellationToken.None);
        Assert.True(prepare.Succeeded);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);

        using var commandCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var command = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(5),
                "cancelled-command"),
            commandCancellation.Token);
        Assert.Equal(PlcsimOperationStatus.Cancelled, command.Status);
        Assert.True(command.Accepted);

        var commandRetry = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Reset,
                TimeSpan.FromSeconds(5),
                "command-retry"),
            CancellationToken.None);
        Assert.True(commandRetry.Succeeded);
        Assert.True(commandRetry.Accepted);

        using var monitoringCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var monitoring = await communication.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(
                communication.Communication.SessionId,
                "cancelled-monitoring-start"),
            monitoringCancellation.Token);
        Assert.Equal(PlcsimOperationStatus.Cancelled, monitoring.Status);
        Assert.True(monitoring.State.IsMonitoring);

        Assert.Equal(PlcsimCommunicationHealthState.Healthy, communication.Health);

        var stop = await communication.StopEventStreamMonitoringAsync(
            new PlcsimEventStreamStopRequest(
                communication.Communication.SessionId,
                "monitoring-stop"),
            CancellationToken.None);
        Assert.True(stop.Succeeded);
        Assert.False(stop.State.IsMonitoring);

        await communication.CloseAsync(CancellationToken.None);
        await worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_observes_runcontrol_status_projection()
    {
        var session = await OpenValidationWorkerSessionAsync("runcontrol-observe-completed");

        var result = await session.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(session.SessionId + "-communication", "correlation"),
            new PlcsimCommunicationSessionInfo(
                session.SessionId + "-communication",
                session.SessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(PlcsimOperationStatus.Succeeded, result.Status);
        var observation = Assert.IsType<PlcsimRunControlObservation>(result.Observation);
        Assert.True(observation.Completed);
        Assert.False(observation.Running);
        Assert.Equal(UnitTestExecutionStatus.Completed, observation.ExecutionStatus);
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "RunControlObservation");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_preserves_all_independent_runcontrol_signals()
    {
        var session = await OpenValidationWorkerSessionAsync("runcontrol-observe-all-signals");

        var result = await session.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(session.SessionId + "-communication", "correlation"),
            new PlcsimCommunicationSessionInfo(
                session.SessionId + "-communication",
                session.SessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        var observation = Assert.IsType<PlcsimRunControlObservation>(result.Observation);
        Assert.Equal(
            UnitTestExecutionSignals.Uninitialized
            | UnitTestExecutionSignals.ConfigError
            | UnitTestExecutionSignals.Ready
            | UnitTestExecutionSignals.Running
            | UnitTestExecutionSignals.Completed
            | UnitTestExecutionSignals.Aborted
            | UnitTestExecutionSignals.Inactive
            | UnitTestExecutionSignals.Error,
            observation.ExecutionSignals);
        Assert.Equal(UnitTestExecutionStatus.Starting, observation.ExecutionStatus);
        Assert.Equal(UnitTestLifecycleState.Preparing, observation.State);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_does_not_reconstruct_error_from_config_error_signal()
    {
        var session = await OpenValidationWorkerSessionAsync("runcontrol-observe-config-error");

        var result = await session.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(session.SessionId + "-communication", "correlation"),
            new PlcsimCommunicationSessionInfo(
                session.SessionId + "-communication",
                session.SessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        var observation = Assert.IsType<PlcsimRunControlObservation>(result.Observation);
        Assert.True(observation.ConfigError);
        Assert.False(observation.Error);
        Assert.Equal(UnitTestExecutionSignals.ConfigError, observation.ExecutionSignals);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_rejects_runcontrol_observation_when_runtime_is_not_running()
    {
        var session = await OpenValidationWorkerSessionAsync("runcontrol-runtime-stopped");

        var result = await session.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(session.SessionId + "-communication", "correlation"),
            new PlcsimCommunicationSessionInfo(
                session.SessionId + "-communication",
                session.SessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Unavailable, result.Status);
        Assert.Null(result.Observation);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.RunControlRuntimeNotRunning");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_requests_runcontrol_start_command()
    {
        var session = await OpenValidationWorkerSessionAsync("runcontrol-command-start");
        var communication = CreateCommunicationInfo(session);

        var result = await session.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(1),
                "correlation"),
            communication,
            CancellationToken.None);

        Assert.Equal(PlcsimOperationStatus.Succeeded, result.Status);
        Assert.True(result.Accepted);
        Assert.Equal(UnitTestRunCommand.Start, result.Command);
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "RunControlCommand");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_rejects_runcontrol_command_when_runtime_is_not_running()
    {
        var session = await OpenValidationWorkerSessionAsync("runcontrol-runtime-stopped");
        var communication = CreateCommunicationInfo(session);

        var result = await session.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(1),
                "correlation"),
            communication,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.Accepted);
        Assert.Equal(PlcsimOperationStatus.Unavailable, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.RunControlRuntimeNotRunning");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_maps_runcontrol_command_rejection()
    {
        var session = await OpenValidationWorkerSessionAsync("runcontrol-command-rejected");
        var communication = CreateCommunicationInfo(session);

        var result = await session.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(1),
                "correlation"),
            communication,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.Accepted);
        Assert.Equal(PlcsimOperationStatus.Rejected, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.Worker.ValidationRunControlCommandRejected");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_closes_prepared_communication_session()
    {
        var session = await OpenValidationWorkerSessionAsync("communication-close");
        var communication = CreateCommunicationInfo(session);

        var result = await session.CloseCommunicationAsync(communication, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Succeeded, result.Status);
        Assert.Equal(communication.SessionId, result.CommunicationSessionId);
        Assert.Empty(result.Diagnostics);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_logs_communication_session_boundaries_without_protocol_payloads()
    {
        var logDirectory = CreateTestLogDirectory();
        var worker = await OpenValidationWorkerSessionAsync("communication-prepare", logDirectory: logDirectory);
        var prepare = await worker.PrepareCommunicationAsync(
            new PlcsimCommunicationPrepareRequest(
                worker.SessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                UnitTestContractVersions.EventStreamModel,
                CreateWorkerOptions(),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                "correlation"),
            CancellationToken.None);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);

        await communication.CloseAsync(CancellationToken.None);
        await worker.CloseAsync(CancellationToken.None);

        var log = ReadWorkerLog(logDirectory, "siemens-test");
        Assert.Contains("Worker command received. CommandType=PrepareCommunication", log);
        Assert.Contains("Worker command handling ended. CommandType=PrepareCommunication DurationMs=", log);
        Assert.Contains("Worker command received. CommandType=CloseCommunication", log);
        Assert.Contains("Worker command handling ended. CommandType=CloseCommunication DurationMs=", log);
        Assert.DoesNotContain("SIEMENS-WORKER/1|", log);
    }

    [Fact]
    public async Task V6_worker_logs_runcontrol_command_and_observation_at_bounded_levels()
    {
        var commandLogDirectory = CreateTestLogDirectory();
        var commandWorker = await OpenValidationWorkerSessionAsync(
            "runcontrol-command-start",
            logDirectory: commandLogDirectory);
        var communication = CreateCommunicationInfo(commandWorker);

        var command = await commandWorker.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(1),
                "correlation"),
            communication,
            CancellationToken.None);
        Assert.True(command.Succeeded);
        await commandWorker.CloseAsync(CancellationToken.None);

        var commandLog = ReadWorkerLog(commandLogDirectory, "siemens-test");
        Assert.Contains("Worker command received. CommandType=RequestRunControlCommand", commandLog);
        Assert.Contains("Worker command handling ended. CommandType=RequestRunControlCommand DurationMs=", commandLog);

        var observationLogDirectory = CreateTestLogDirectory();
        var observationWorker = await OpenValidationWorkerSessionAsync(
            "runcontrol-observe-completed",
            logDirectory: observationLogDirectory);
        var observationCommunication = CreateCommunicationInfo(observationWorker);

        var observation = await observationWorker.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(observationCommunication.SessionId, "correlation"),
            observationCommunication,
            CancellationToken.None);
        Assert.True(observation.Succeeded);
        await observationWorker.CloseAsync(CancellationToken.None);

        var observationLog = ReadWorkerLog(observationLogDirectory, "siemens-test");
        Assert.Contains("[DBG]", observationLog);
        Assert.Contains("Worker command received. CommandType=ObserveRunControl", observationLog);
        Assert.DoesNotContain("SIEMENS-WORKER/1|", commandLog);
        Assert.DoesNotContain("SIEMENS-WORKER/1|", observationLog);
    }

    [Fact]
    public async Task V6_worker_session_accepts_event_buffer_reset_write()
    {
        var worker = await OpenValidationWorkerSessionAsync("event-buffer-reset-accepted");
        var communication = CreateCommunicationInfo(worker);

        var result = await worker.RequestEventBufferResetAsync(
            new PlcsimEventBufferResetRequest(
                communication.SessionId,
                TimeSpan.FromSeconds(1),
                "correlation"),
            communication,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Accepted);
        await worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_maps_rejected_event_buffer_reset_write()
    {
        var worker = await OpenValidationWorkerSessionAsync("event-buffer-reset-rejected");
        var communication = CreateCommunicationInfo(worker);

        var result = await worker.RequestEventBufferResetAsync(
            new PlcsimEventBufferResetRequest(
                communication.SessionId,
                TimeSpan.FromSeconds(1),
                "correlation"),
            communication,
            CancellationToken.None);

        Assert.Equal(PlcsimOperationStatus.Rejected, result.Status);
        Assert.False(result.Accepted);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.Worker.ValidationEventBufferResetRejected");
        await worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_maps_communication_close_error_response()
    {
        var session = await OpenValidationWorkerSessionAsync("communication-close-error");
        var communication = CreateCommunicationInfo(session);

        var result = await session.CloseCommunicationAsync(communication, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.Worker.ValidationCommunicationCloseFailed");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_communication_session_closes_worker_session_and_rejects_later_runcontrol_access()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("worker-session");
        var communication = new PlcsimAdvancedV6CommunicationSession(
            worker,
            CreateCommunicationInfo(worker),
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            UnitTestContractVersions.PlcsimAdvancedCommunication);

        await communication.CloseAsync(CancellationToken.None);
        await communication.CloseAsync(CancellationToken.None);
        var validation = await communication.ValidateRunControlAsync(
            new PlcsimRunControlValidationRequest(
                communication.Communication.SessionId,
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                "correlation"),
            CancellationToken.None);
        var observation = await communication.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(
                communication.Communication.SessionId,
                "correlation"),
            CancellationToken.None);
        var command = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(1),
                "correlation"),
            CancellationToken.None);
        var eventBufferValidation = await communication.ValidateEventBufferAsync(
            new PlcsimEventBufferValidationRequest(
                communication.Communication.SessionId,
                UnitTestContractVersions.EventStreamModel,
                "correlation"),
            CancellationToken.None);
        var monitoring = await communication.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(
                communication.Communication.SessionId,
                "correlation"),
            CancellationToken.None);
        Assert.Equal(1, worker.CloseCommunicationCount);
        Assert.Equal(PlcsimCommunicationHealthState.Disposed, communication.Health);
        Assert.Equal(PlcsimOperationStatus.Unavailable, validation.Status);
        Assert.False(validation.ContractValid);
        Assert.Equal(PlcsimOperationStatus.Unavailable, observation.Status);
        Assert.Equal(PlcsimOperationStatus.Unavailable, command.Status);
        Assert.Equal(PlcsimOperationStatus.Unavailable, eventBufferValidation.Status);
        Assert.Equal(PlcsimOperationStatus.Unavailable, monitoring.Status);
        Assert.False(command.Accepted);
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.CommunicationSessionClosed");
        Assert.Contains(observation.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.CommunicationSessionClosed");
        Assert.Contains(command.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.CommunicationSessionClosed");
        Assert.Contains(eventBufferValidation.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.CommunicationSessionClosed");
        Assert.Contains(monitoring.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.CommunicationSessionClosed");
    }

    [Fact]
    public async Task V6_communication_session_returns_rejected_results_for_mismatched_session_ids()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("worker-session");
        var communication = new PlcsimAdvancedV6CommunicationSession(
            worker,
            CreateCommunicationInfo(worker),
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            UnitTestContractVersions.PlcsimAdvancedCommunication);
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
        var eventBufferReset = await communication.RequestEventBufferResetAsync(
            new PlcsimEventBufferResetRequest(
                mismatchedSessionId,
                TimeSpan.FromSeconds(1),
                "event-buffer-reset"),
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
                eventBufferReset.Status,
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
                eventBufferReset.Diagnostics,
                observation.Diagnostics,
                eventBufferValidation.Diagnostics,
                monitoringStart.Diagnostics,
                monitoringStop.Diagnostics
            },
            diagnostics => Assert.Contains(
                diagnostics,
                diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.CommunicationSessionMismatch"));
        Assert.False(command.Accepted);
        Assert.Null(observation.Observation);
        Assert.False(eventBufferValidation.ContractValid);
        Assert.False(monitoringStart.State.IsMonitoring);
        Assert.False(monitoringStop.State.IsMonitoring);

        await communication.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_communication_session_publishes_safe_runcontrol_defaults_when_close_fails()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("worker-session")
        {
            CommunicationCloseResult = new PlcsimCommunicationCloseResult(
                PlcsimOperationStatus.Failed,
                "worker-session-communication",
                new[]
                {
                    new PlcsimDiagnostic(
                        "Stub.CloseFailed",
                        PlcsimDiagnosticSeverity.Error,
                        "Communication close failed.",
                        "CloseCommunication",
                        "stub")
                },
                Array.Empty<PlcsimEvidence>())
        };
        var unregisterCount = 0;
        var runtimeSession = new PlcsimAdvancedV6RuntimeSession(
            worker,
            new PlcsimRuntimeReference("runtime-id", "PlcUnderTest"),
            DateTimeOffset.UtcNow,
            () => unregisterCount++);
        var communication = new PlcsimAdvancedV6CommunicationSession(
            worker,
            CreateCommunicationInfo(worker),
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            UnitTestContractVersions.PlcsimAdvancedCommunication);
        PlcsimRunControlObservation? observed = null;
        var observationCount = 0;
        communication.RunControlObservationChanged += (_, args) =>
        {
            observed = args.Observation;
            observationCount++;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => communication.CloseAsync(CancellationToken.None));
        worker.RaiseRunControlObservationChanged(new PlcsimRunControlObservation(
            UnitTestExecutionStatus.Idle,
            UnitTestExecutionSignals.Ready,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated));
        var command = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(1),
                "correlation"),
            CancellationToken.None);

        Assert.Equal(PlcsimCommunicationHealthState.Invalidated, communication.Health);
        Assert.Equal(PlcsimRuntimeHealthState.Invalidated, runtimeSession.Health);
        Assert.Equal(1, unregisterCount);
        Assert.Equal(1, worker.TerminateCount);
        Assert.Equal(1, observationCount);
        Assert.Equal(PlcsimOperationStatus.Unavailable, command.Status);
        Assert.NotNull(observed);
        Assert.Equal(UnitTestExecutionStatus.Unknown, observed!.ExecutionStatus);
        Assert.Equal(UnitTestExecutionSignals.None, observed.ExecutionSignals);
        Assert.Equal(UnitTestLifecycleState.Unknown, observed.State);
        Assert.Equal(UnitTestResultState.NotEvaluated, observed.Result);
    }

    [Fact]
    public async Task V6_communication_session_finalizes_locally_when_close_is_cancelled()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("worker-session");
        var communication = new PlcsimAdvancedV6CommunicationSession(
            worker,
            CreateCommunicationInfo(worker),
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            UnitTestContractVersions.PlcsimAdvancedCommunication);
        var observationCount = 0;
        communication.RunControlObservationChanged += (_, _) => observationCount++;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => communication.CloseAsync(cancellation.Token));
        worker.RaiseRunControlObservationChanged(new PlcsimRunControlObservation(
            UnitTestExecutionStatus.Idle,
            UnitTestExecutionSignals.Ready,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated));

        Assert.Equal(PlcsimCommunicationHealthState.Invalidated, communication.Health);
        Assert.Equal(1, worker.TerminateCount);
        Assert.Equal(1, observationCount);
    }

    [Fact]
    public async Task V6_communication_session_forwards_worker_runcontrol_observations()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("worker-session");
        var communication = new PlcsimAdvancedV6CommunicationSession(
            worker,
            CreateCommunicationInfo(worker),
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            UnitTestContractVersions.PlcsimAdvancedCommunication);
        PlcsimRunControlObservation? observed = null;
        communication.RunControlObservationChanged += (_, args) => observed = args.Observation;

        worker.RaiseRunControlObservationChanged(new PlcsimRunControlObservation(
            UnitTestExecutionStatus.Idle,
            UnitTestExecutionSignals.Ready,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated));

        Assert.NotNull(observed);
        Assert.True(observed!.Ready);
        Assert.Equal(UnitTestExecutionStatus.Idle, observed.ExecutionStatus);

        await communication.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_communication_session_does_not_publish_command_result_as_lifecycle_observation()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("worker-session");
        var communication = new PlcsimAdvancedV6CommunicationSession(
            worker,
            CreateCommunicationInfo(worker),
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            UnitTestContractVersions.PlcsimAdvancedCommunication);
        var observationCount = 0;
        communication.RunControlObservationChanged += (_, _) => observationCount++;

        var result = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(1),
                "correlation"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Accepted);
        Assert.Equal(0, observationCount);

        await communication.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_communication_session_ignores_stale_event_batches()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("worker-session");
        var communication = new PlcsimAdvancedV6CommunicationSession(
            worker,
            CreateCommunicationInfo(worker),
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            UnitTestContractVersions.PlcsimAdvancedCommunication);
        var received = new List<UnitTestEventBatch>();
        communication.EventBatchReceived += (_, args) => received.Add(args.Batch);

        worker.RaiseEventBatchReceived(CreateEventBatch("other-session", 1));
        worker.RaiseEventBatchReceived(CreateEventBatch(communication.Communication.SessionId, 2));
        await communication.CloseAsync(CancellationToken.None);
        worker.RaiseEventBatchReceived(CreateEventBatch(communication.Communication.SessionId, 3));

        var batch = Assert.Single(received);
        Assert.Equal((uint)2, Assert.Single(batch.Events).SequenceId);
    }

    [Fact]
    public async Task V6_communication_session_forwards_matching_eventstream_state_and_ignores_stale_state()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("worker-session");
        var communication = new PlcsimAdvancedV6CommunicationSession(
            worker,
            CreateCommunicationInfo(worker),
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            UnitTestContractVersions.PlcsimAdvancedCommunication);
        var received = new List<PlcsimEventStreamMonitoringState>();
        communication.EventStreamMonitoringStateChanged += (_, args) => received.Add(args.State);

        worker.RaiseEventStreamMonitoringStateChanged(new PlcsimEventStreamMonitoringState(
            "other-session", true, EventBufferReadState.Inconsistent, EventStreamBatchReleaseState.Pending,
            BufferObservedEmpty: false, LostEventCount: 3, BufferFull: true,
            ProducerSequenceId: 41, DeletedSequenceId: 40));
        worker.RaiseEventStreamMonitoringStateChanged(new PlcsimEventStreamMonitoringState(
            communication.Communication.SessionId, true, EventBufferReadState.Valid, EventStreamBatchReleaseState.Completed,
            BufferObservedEmpty: false, LostEventCount: 2, BufferFull: true,
            ProducerSequenceId: 24, DeletedSequenceId: 20));
        await communication.CloseAsync(CancellationToken.None);
        worker.RaiseEventStreamMonitoringStateChanged(new PlcsimEventStreamMonitoringState(
            communication.Communication.SessionId, false, EventBufferReadState.Empty, EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty: true, LostEventCount: 0));

        var state = Assert.Single(received);
        Assert.True(state.IsMonitoring);
        Assert.Equal((uint)2, state.LostEventCount);
        Assert.True(state.BufferFull);
        Assert.Equal((uint)24, state.ProducerSequenceId);
        Assert.Equal((uint)20, state.DeletedSequenceId);
    }

    [Fact]
    public async Task V6_communication_session_invalidates_health_when_worker_eventstream_monitoring_faults()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("worker-session");
        var communication = new PlcsimAdvancedV6CommunicationSession(
            worker,
            CreateCommunicationInfo(worker),
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            UnitTestContractVersions.PlcsimAdvancedCommunication);
        var updates = new List<PlcsimCommunicationHealthChangedEventArgs>();
        PlcsimRunControlObservation? observed = null;
        communication.HealthChanged += (_, args) => updates.Add(args);
        communication.RunControlObservationChanged += (_, args) => observed = args.Observation;

        worker.RaiseEventStreamMonitoringFaulted(
            communication.Communication.SessionId,
            PlcsimOperationStatus.Failed,
            "Event stream worker faulted.");
        worker.RaiseEventStreamMonitoringFaulted(
            communication.Communication.SessionId,
            PlcsimOperationStatus.Failed,
            "Repeated event stream worker fault.");
        worker.RaiseRunControlObservationChanged(new PlcsimRunControlObservation(
            UnitTestExecutionStatus.Idle,
            UnitTestExecutionSignals.Ready,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated));
        var command = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(1),
                "correlation"),
            CancellationToken.None);

        Assert.Equal(PlcsimCommunicationHealthState.Faulted, communication.Health);
        Assert.Equal(PlcsimOperationStatus.Unavailable, command.Status);
        var update = Assert.Single(updates);
        Assert.Equal(PlcsimCommunicationHealthState.Healthy, update.Previous);
        Assert.Equal(PlcsimCommunicationHealthState.Faulted, update.Current);
        Assert.Equal("Stub.EventStreamMonitoringFaulted: Event stream worker faulted.", update.Message);
        Assert.NotNull(observed);
        Assert.Equal(UnitTestExecutionStatus.Unknown, observed!.ExecutionStatus);
        Assert.Equal(UnitTestExecutionSignals.None, observed.ExecutionSignals);
        Assert.Equal(UnitTestLifecycleState.Unknown, observed.State);
        Assert.Equal(UnitTestResultState.NotEvaluated, observed.Result);
    }

    [Fact]
    public void V6_communication_session_projects_worker_revalidation_and_recovery_health()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("worker-session");
        var communication = new PlcsimAdvancedV6CommunicationSession(
            worker,
            CreateCommunicationInfo(worker),
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            UnitTestContractVersions.PlcsimAdvancedCommunication);
        var updates = new List<PlcsimCommunicationHealthChangedEventArgs>();
        communication.HealthChanged += (_, args) => updates.Add(args);

        worker.RaiseCommunicationHealthChanged(
            communication.Communication.SessionId,
            PlcsimCommunicationHealthState.RevalidationRequired,
            "Communication is revalidating.");
        worker.RaiseCommunicationHealthChanged(
            communication.Communication.SessionId,
            PlcsimCommunicationHealthState.Healthy,
            "Communication recovered.");

        Assert.Equal(PlcsimCommunicationHealthState.Healthy, communication.Health);
        Assert.Equal(
            new[]
            {
                PlcsimCommunicationHealthState.RevalidationRequired,
                PlcsimCommunicationHealthState.Healthy
            },
            updates.Select(update => update.Current));
    }

    [Fact]
    public void V6_communication_session_accepts_terminal_worker_fault_during_revalidation()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("worker-session");
        var communication = new PlcsimAdvancedV6CommunicationSession(
            worker,
            CreateCommunicationInfo(worker),
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            UnitTestContractVersions.PlcsimAdvancedCommunication);

        worker.RaiseCommunicationHealthChanged(
            communication.Communication.SessionId,
            PlcsimCommunicationHealthState.RevalidationRequired,
            "Communication is revalidating.");
        worker.RaiseEventStreamMonitoringFaulted(
            communication.Communication.SessionId,
            PlcsimOperationStatus.Failed,
            "Communication revalidation timed out.");

        Assert.Equal(PlcsimCommunicationHealthState.Faulted, communication.Health);
    }

    [Fact]
    public async Task V6_communication_session_close_after_terminal_fault_is_idempotent_without_degraded_health()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("worker-session");
        var communication = new PlcsimAdvancedV6CommunicationSession(
            worker,
            CreateCommunicationInfo(worker),
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            UnitTestContractVersions.PlcsimAdvancedCommunication);
        var updates = new List<PlcsimCommunicationHealthChangedEventArgs>();
        communication.HealthChanged += (_, args) => updates.Add(args);

        worker.RaiseEventStreamMonitoringFaulted(
            communication.Communication.SessionId,
            PlcsimOperationStatus.Unavailable,
            "Communication became terminal.");
        await communication.CloseAsync(CancellationToken.None);
        await communication.CloseAsync(CancellationToken.None);

        Assert.Equal(PlcsimCommunicationHealthState.Disposed, communication.Health);
        Assert.Equal(1, worker.CloseCommunicationCount);
        Assert.DoesNotContain(updates, update => update.Current == PlcsimCommunicationHealthState.Degraded);
        Assert.Equal(
            new[]
            {
                PlcsimCommunicationHealthState.Invalidated,
                PlcsimCommunicationHealthState.Disposed
            },
            updates.Select(update => update.Current));
    }

    [Fact]
    public async Task V6_communication_session_ignores_worker_eventstream_fault_after_close()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("worker-session");
        var communication = new PlcsimAdvancedV6CommunicationSession(
            worker,
            CreateCommunicationInfo(worker),
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            UnitTestContractVersions.PlcsimAdvancedCommunication);
        await communication.CloseAsync(CancellationToken.None);

        worker.RaiseEventStreamMonitoringFaulted(
            communication.Communication.SessionId,
            PlcsimOperationStatus.Failed,
            "Late event stream worker fault.");

        Assert.Equal(PlcsimCommunicationHealthState.Disposed, communication.Health);
    }

    [Fact]
    public async Task V6_worker_session_supports_repeated_communication_close_and_reopen_cycles()
    {
        var worker = await OpenValidationWorkerSessionAsync("communication-prepare");
        IPlcsimUnitTestCommunicationSession? previous = null;

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var prepare = await worker.PrepareCommunicationAsync(
                new PlcsimCommunicationPrepareRequest(
                    worker.SessionId,
                    "DB_UnitTestRunControl",
                    "DB_UnitTestEventBuffer",
                    UnitTestContractVersions.PlcsimAdvancedCommunication,
                    UnitTestContractVersions.EventStreamModel,
                    CreateWorkerOptions(),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    "prepare-" + cycle),
                CancellationToken.None);
            var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);

            Assert.True(prepare.Succeeded);
            Assert.Equal(PlcsimCommunicationHealthState.Healthy, communication.Health);
            Assert.NotSame(previous, communication);

            await communication.CloseAsync(CancellationToken.None);

            Assert.Equal(PlcsimCommunicationHealthState.Disposed, communication.Health);
            previous = communication;
        }

        await worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_closed_sessions_release_binding_registration_and_worker_subscriptions()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("runtime-session");
        var binding = CreateBindingWithWorker(worker);
        var opened = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);
        var runtime = Assert.IsAssignableFrom<IPlcsimRuntimeSession>(opened.Session);

        Assert.Equal(1, worker.RuntimeStateChangedSubscriberCount);

        var communication = CreateFacadeCommunicationSession(worker);

        Assert.Equal(1, worker.EventBatchReceivedSubscriberCount);
        Assert.Equal(1, worker.EventStreamMonitoringStateChangedSubscriberCount);
        Assert.Equal(1, worker.RunControlObservationChangedSubscriberCount);
        Assert.Equal(1, worker.CommunicationHealthChangedSubscriberCount);
        Assert.Equal(1, worker.EventStreamMonitoringFaultedSubscriberCount);

        await communication.CloseAsync(CancellationToken.None);

        Assert.Equal(0, worker.EventBatchReceivedSubscriberCount);
        Assert.Equal(0, worker.EventStreamMonitoringStateChangedSubscriberCount);
        Assert.Equal(0, worker.RunControlObservationChangedSubscriberCount);
        Assert.Equal(0, worker.CommunicationHealthChangedSubscriberCount);
        Assert.Equal(0, worker.EventStreamMonitoringFaultedSubscriberCount);
        Assert.Equal(1, worker.RuntimeStateChangedSubscriberCount);

        await runtime.CloseAsync(CancellationToken.None);

        Assert.Equal(0, worker.RuntimeStateChangedSubscriberCount);
        var unavailable = await binding.PrepareCommunicationAsync(
            CreateCommunicationPrepareRequest(runtime.Runtime.SessionId),
            CancellationToken.None);
        Assert.Equal(PlcsimOperationStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Session);
    }

    [Fact]
    public async Task V6_closed_runtime_and_communication_sessions_are_collectable()
    {
        var references = await CreateClosedSessionWeakReferencesAsync();

        ForceFullCollection();

        Assert.False(references.Runtime.IsAlive);
        Assert.False(references.Communication.IsAlive);
    }

    [Fact]
    public async Task V6_worker_session_rejects_plccycle_observation_when_sync_point_api_is_unavailable()
    {
        var worker = await OpenValidationWorkerSessionAsync("communication-plccycle-unavailable");
        var prepare = await worker.PrepareCommunicationAsync(
            new PlcsimCommunicationPrepareRequest(
                worker.SessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                UnitTestContractVersions.EventStreamModel,
                CreatePlcCycleWorkerOptions(),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                "prepare"),
            CancellationToken.None);

        Assert.False(prepare.Succeeded);
        Assert.Null(prepare.Session);
        Assert.Equal(PlcsimOperationStatus.Unavailable, prepare.Status);
        Assert.Contains(prepare.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.Worker.PlcCycleObservationUnavailable");

        await worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_recovers_stale_contract_read_during_communication_preparation()
    {
        var worker = await OpenValidationWorkerSessionAsync("worker-revalidation-prepare-recover");

        var prepare = await worker.PrepareCommunicationAsync(
            CreateCommunicationPrepareRequest(worker.SessionId),
            CancellationToken.None);

        Assert.True(prepare.Succeeded);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);
        await communication.CloseAsync(CancellationToken.None);
        await worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_accepts_unbounded_active_communication_revalidation()
    {
        var worker = await OpenValidationWorkerSessionAsync("worker-revalidation-prepare-recover");
        var request = CreateCommunicationPrepareRequest(worker.SessionId) with
        {
            RevalidationTimeout = TimeSpan.Zero
        };

        var prepare = await worker.PrepareCommunicationAsync(request, CancellationToken.None);

        Assert.True(prepare.Succeeded);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);
        await communication.CloseAsync(CancellationToken.None);
        await worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_publishes_active_revalidation_and_recovery_health()
    {
        var worker = await OpenValidationWorkerSessionAsync("worker-revalidation-runcontrol-state");
        var prepare = await worker.PrepareCommunicationAsync(
            CreateCommunicationPrepareRequest(worker.SessionId),
            CancellationToken.None);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);
        var updates = new List<PlcsimCommunicationHealthState>();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        communication.HealthChanged += (_, args) =>
        {
            updates.Add(args.Current);
            if (args.Previous == PlcsimCommunicationHealthState.RevalidationRequired
                && args.Current == PlcsimCommunicationHealthState.Healthy)
            {
                recovered.TrySetResult();
            }
        };

        var monitoring = await communication.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(
                communication.Communication.SessionId,
                "start"),
            CancellationToken.None);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(monitoring.Succeeded);
        Assert.Equal(
            new[]
            {
                PlcsimCommunicationHealthState.RevalidationRequired,
                PlcsimCommunicationHealthState.Healthy
            },
            updates);

        await communication.CloseAsync(CancellationToken.None);
        await worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_bounds_stale_contract_revalidation_during_communication_preparation()
    {
        var worker = await OpenValidationWorkerSessionAsync("worker-revalidation-prepare-timeout");
        var request = CreateCommunicationPrepareRequest(worker.SessionId) with
        {
            PrepareTimeout = TimeSpan.FromMilliseconds(1)
        };

        var prepare = await worker.PrepareCommunicationAsync(request, CancellationToken.None);

        Assert.False(prepare.Succeeded);
        Assert.Null(prepare.Session);
        Assert.Equal(PlcsimOperationStatus.TimedOut, prepare.Status);
        Assert.Contains(
            prepare.Diagnostics,
            diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.Worker.CommunicationContractRevalidationTimedOut");
        await worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_keeps_configuration_and_ip_events_as_diagnostic_evidence()
    {
        var logDirectory = CreateTestLogDirectory();
        var worker = await OpenValidationWorkerSessionAsync(
            "worker-event-evidence-diagnostic-only",
            logDirectory: logDirectory);
        var runtimeChanges = new List<PlcsimRuntimeStateChange>();
        worker.RuntimeStateChanged += (_, args) => runtimeChanges.Add(args.Change);

        var prepare = await worker.PrepareCommunicationAsync(
            CreateCommunicationPrepareRequest(worker.SessionId),
            CancellationToken.None);

        Assert.True(prepare.Succeeded);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);
        Assert.Equal(PlcsimCommunicationHealthState.Healthy, communication.Health);
        Assert.Empty(runtimeChanges);

        await communication.CloseAsync(CancellationToken.None);
        await worker.CloseAsync(CancellationToken.None);

        var log = ReadWorkerLog(logDirectory, "siemens-test");
        Assert.Contains(
            "PLCSIM Advanced Runtime Manager configuration changed.",
            log);
        Assert.Contains("PLC software configuration changed.", log);
        Assert.Contains("PLC hardware configuration changed.", log);
        Assert.Contains("PLC IP address changed.", log);
        Assert.Equal(
            4,
            log.Split(new[] { "Reaction=DiagnosticOnly" }, StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("execution-status")]
    [InlineData("result")]
    [InlineData("uninitialized")]
    [InlineData("config-error")]
    [InlineData("ready")]
    [InlineData("running")]
    [InlineData("completed")]
    [InlineData("aborted")]
    [InlineData("inactive")]
    [InlineData("error")]
    public async Task V6_worker_recovers_each_stale_runcontrol_read_position(string field)
    {
        var worker = await OpenValidationWorkerSessionAsync("worker-revalidation-runcontrol-" + field);
        var prepare = await worker.PrepareCommunicationAsync(
            CreateCommunicationPrepareRequest(worker.SessionId),
            CancellationToken.None);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);

        var observation = await communication.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(
                communication.Communication.SessionId,
                "stale-" + field),
            CancellationToken.None);

        Assert.True(observation.Succeeded);
        Assert.NotNull(observation.Observation);
        Assert.Equal(UnitTestLifecycleState.Idle, observation.Observation.State);
        Assert.True(observation.Observation.Ready);
        await communication.CloseAsync(CancellationToken.None);
        await worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_does_not_retry_terminal_runcontrol_read_failure()
    {
        var worker = await OpenValidationWorkerSessionAsync("worker-revalidation-runcontrol-terminal");
        var prepare = await worker.PrepareCommunicationAsync(
            CreateCommunicationPrepareRequest(worker.SessionId),
            CancellationToken.None);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);

        var observation = await communication.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(
                communication.Communication.SessionId,
                "terminal"),
            CancellationToken.None);

        Assert.False(observation.Succeeded);
        Assert.Contains(
            observation.Diagnostics,
            diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.Worker.RunControlStatusReadFailed");
        await communication.CloseAsync(CancellationToken.None);
        await worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_binding_prepares_communication_through_registered_runtime_worker_session()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("worker-session");
        var binding = new PlcsimAdvancedV6Binding(
            new StubPlcsimAdvancedV6EnvironmentProbe(ReadyProbeWithRuntimeApiDirectory("C:\\PLCSIMADV\\API\\6.0")),
            new StubPlcsimAdvancedV6WorkerSessionHost(PlcsimAdvancedV6WorkerSessionHostOpenResult.SucceededResult(
                worker,
                Array.Empty<PlcsimEvidence>())));
        binding.Initialize(CreateBindingContext());

        var open = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);
        var communication = await binding.PrepareCommunicationAsync(
            new PlcsimCommunicationPrepareRequest(
                open.Session!.Runtime.SessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                UnitTestContractVersions.EventStreamModel,
                CreateWorkerOptions(),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                "prepare"),
            CancellationToken.None);

        Assert.True(communication.Succeeded);
        Assert.NotNull(communication.Session);
        Assert.Equal("worker-session-communication", communication.Session!.Communication.SessionId);
        Assert.Equal("worker-session", communication.Session.Communication.RuntimeSessionId);
    }

    [Fact]
    public async Task V6_binding_maps_unavailable_plc_cycle_observation_to_actionable_diagnostic()
    {
        var worker = new StubPlcsimAdvancedV6WorkerSessionHandle("worker-session")
        {
            CommunicationPrepareResult = new PlcsimCommunicationPrepareResult(
                PlcsimOperationStatus.Unavailable,
                null,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.Worker.PlcCycleObservationUnavailable",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V6 sync-point observation could not be established.",
                        "PrepareCommunication",
                        "plcsim-advanced-v6-communication-prepare")
                },
                Array.Empty<PlcsimEvidence>())
        };
        var binding = CreateBindingWithWorker(worker);
        var open = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);
        var request = CreateCommunicationPrepareRequest(open.Session!.Runtime.SessionId) with
        {
            WorkerOptions = CreatePlcCycleWorkerOptions()
        };

        var communication = await binding.PrepareCommunicationAsync(request, CancellationToken.None);

        Assert.Equal(PlcsimOperationStatus.Unavailable, communication.Status);
        var diagnostic = Assert.Single(communication.Diagnostics);
        Assert.Equal("PlcsimAdvanced.V6.PlcCycleObservationNotSupported", diagnostic.Code);
        Assert.Equal(
            "PLC-cycle observation is not supported by the current PLCSIM Advanced V6 binding. Use polling instead.",
            diagnostic.Message);
    }

    [Fact]
    public async Task V6_binding_does_not_simulate_communication_success()
    {
        var binding = new PlcsimAdvancedV6Binding();

        Assert.IsAssignableFrom<IPlcsimAdvancedRuntimeBinding>(binding);
        Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationBinding>(binding);

        var communication = await binding.PrepareCommunicationAsync(
            new PlcsimCommunicationPrepareRequest(
                "runtime-session",
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                "1.0.0",
                "1.0.0",
                CreateWorkerOptions(),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                null),
            CancellationToken.None);

        Assert.False(communication.Succeeded);
        Assert.Null(communication.Session);
        Assert.Equal(PlcsimOperationStatus.Unavailable, communication.Status);
        Assert.Contains(communication.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.RuntimeSessionNotFound");
    }

}
