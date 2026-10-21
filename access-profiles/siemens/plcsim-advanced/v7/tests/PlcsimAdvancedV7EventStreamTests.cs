using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;
using static PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V7.PlcsimAdvancedV7RealRuntimeTests;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V7;

public sealed class PlcsimAdvancedV7EventStreamTests
{
    [Fact]
    public async Task V7_worker_session_starts_and_stops_eventstream_monitoring_through_validation_worker()
    {
        var worker = await OpenValidationWorkerSessionAsync("communication-prepare");
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
                "prepare"),
            CancellationToken.None);

        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);
        var validation = await communication.ValidateEventBufferAsync(
            new PlcsimEventBufferValidationRequest(
                communication.Communication.SessionId,
                UnitTestContractVersions.EventStreamModel,
                "validate"),
            CancellationToken.None);
        var start = await communication.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(
                communication.Communication.SessionId,
                "start"),
            CancellationToken.None);
        var stop = await communication.StopEventStreamMonitoringAsync(
            new PlcsimEventStreamStopRequest(
                communication.Communication.SessionId,
                "stop"),
            CancellationToken.None);

        Assert.True(prepare.Succeeded);
        Assert.True(validation.Succeeded);
        Assert.True(validation.ContractValid);
        Assert.True(start.Succeeded);
        Assert.True(start.State.IsMonitoring);
        Assert.Equal(EventBufferReadState.NotObserved, start.State.LastReadState);
        Assert.False(start.State.BufferObservedEmpty);
        Assert.True(stop.Succeeded);
        Assert.False(stop.State.IsMonitoring);

        await communication.CloseAsync(CancellationToken.None);
        await worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V7_worker_session_supports_repeated_eventstream_monitoring_cycles()
    {
        var worker = await OpenValidationWorkerSessionAsync("communication-prepare");
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
                "prepare"),
            CancellationToken.None);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var start = await communication.StartEventStreamMonitoringAsync(
                new PlcsimEventStreamMonitoringRequest(
                    communication.Communication.SessionId,
                    "start-" + cycle),
                CancellationToken.None);
            var stop = await communication.StopEventStreamMonitoringAsync(
                new PlcsimEventStreamStopRequest(
                    communication.Communication.SessionId,
                    "stop-" + cycle),
                CancellationToken.None);

            Assert.True(start.Succeeded);
            Assert.True(start.State.IsMonitoring);
            Assert.True(stop.Succeeded);
            Assert.False(stop.State.IsMonitoring);
        }

        await communication.CloseAsync(CancellationToken.None);
        await worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V7_worker_session_rejects_only_non_positive_monitoring_intervals()
    {
        var worker = await OpenValidationWorkerSessionAsync("communication-prepare");
        var invalid = await worker.PrepareCommunicationAsync(
            new PlcsimCommunicationPrepareRequest(
                worker.SessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                UnitTestContractVersions.EventStreamModel,
                new PlcsimCommunicationWorkerOptions(
                    PlcsimCommunicationObservationStrategyKind.Polling,
                    TimeSpan.Zero),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                "invalid-poll-interval"),
            CancellationToken.None);

        Assert.Equal(PlcsimOperationStatus.Rejected, invalid.Status);
        Assert.Null(invalid.Session);
        Assert.Contains(
            invalid.Diagnostics,
            diagnostic => diagnostic.Code == "PlcsimAdvanced.V7.CommunicationMonitoringIntervalInvalid");

        var applicationSelectedInterval = TimeSpan.FromMilliseconds((double)int.MaxValue + 1)
            + TimeSpan.FromTicks(1);
        var valid = await worker.PrepareCommunicationAsync(
            new PlcsimCommunicationPrepareRequest(
                worker.SessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                UnitTestContractVersions.EventStreamModel,
                new PlcsimCommunicationWorkerOptions(
                    PlcsimCommunicationObservationStrategyKind.Polling,
                    applicationSelectedInterval),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                "application-selected-poll-interval"),
            CancellationToken.None);

        Assert.True(valid.Succeeded);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(valid.Session);
        await communication.CloseAsync(CancellationToken.None);
        await worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V7_worker_session_invalidates_eventstream_when_batch_consumer_blocks_pipe_progress()
    {
        var worker = await OpenValidationWorkerSessionAsync(
            "eventstream-snapshot-partial",
            commandResponseTimeout: TimeSpan.FromMilliseconds(100));
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
                "prepare"),
            CancellationToken.None);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        communication.EventBatchReceived += (_, _) =>
        {
            callbackEntered.Set();
            releaseCallback.Wait(TimeSpan.FromSeconds(5));
        };

        try
        {
            var monitoringTask = communication.StartEventStreamMonitoringAsync(
                new PlcsimEventStreamMonitoringRequest(
                    communication.Communication.SessionId,
                    "start"),
                CancellationToken.None);

            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(2)));
            var monitoring = await monitoringTask;

            Assert.Equal(PlcsimOperationStatus.TimedOut, monitoring.Status);
            Assert.Equal(PlcsimCommunicationHealthState.Faulted, communication.Health);
        }
        finally
        {
            releaseCallback.Set();
        }
    }

    [Fact]
    public async Task V7_worker_session_stops_eventstream_monitoring_when_runtime_is_already_stopped()
    {
        var session = await OpenValidationWorkerSessionAsync("eventstream-stop-runtime-stopped");
        var communication = CreateCommunicationInfo(session);

        var result = await session.StopEventStreamMonitoringAsync(
            new PlcsimEventStreamStopRequest(communication.SessionId, "stop"),
            communication,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.State.IsMonitoring);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "PlcsimAdvanced.V7.RunControlRuntimeNotRunning");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V7_worker_session_ignores_async_eventstream_fault_messages_as_command_responses()
    {
        var logDirectory = CreateTestLogDirectory();
        var worker = await OpenValidationWorkerSessionAsync(
            "eventstream-monitoring-faulted-event",
            logDirectory: logDirectory);
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
                "prepare"),
            CancellationToken.None);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);

        var monitoring = await communication.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(
                communication.Communication.SessionId,
                "start"),
            CancellationToken.None);

        Assert.True(monitoring.Succeeded);
        Assert.True(monitoring.State.IsMonitoring);

        await communication.CloseAsync(CancellationToken.None);
        await worker.CloseAsync(CancellationToken.None);

        var log = ReadWorkerLog(logDirectory, "siemens-test");
        Assert.Contains(
            "EventStream monitoring faulted. CommunicationSessionId="
            + communication.Communication.SessionId
            + " Code=PlcsimAdvanced.V7.Worker.ValidationEventStreamMonitoringFaulted",
            log);
    }

    [Theory]
    [InlineData("pre-fifo")]
    [InlineData("pre-status")]
    [InlineData("entry")]
    [InlineData("post-fifo")]
    [InlineData("post-status")]
    [InlineData("release")]
    public async Task V7_worker_discards_stale_eventstream_cycle_and_publishes_only_revalidated_batch(
        string phase)
    {
        var result = await StartValidationEventStreamMonitoringAsync(
            "worker-revalidation-eventstream-" + phase);

        Assert.True(result.Monitoring.Succeeded);
        Assert.Equal(EventBufferReadState.Valid, result.Monitoring.State.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Completed, result.Monitoring.State.BatchReleaseState);
        var batch = Assert.Single(result.Batches);
        var entry = Assert.Single(batch.Events);
        Assert.Equal((uint)1, entry.SequenceId);

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V7_worker_keeps_terminal_eventstream_read_failure_distinct_from_stale_revalidation()
    {
        var result = await StartValidationEventStreamMonitoringAsync(
            "worker-revalidation-eventstream-terminal");

        Assert.False(result.Monitoring.Succeeded);
        Assert.False(result.Monitoring.State.IsMonitoring);
        Assert.Contains(
            result.Monitoring.Diagnostics,
            diagnostic => diagnostic.Code == "PlcsimAdvanced.V7.Worker.EventBufferMetadataReadFailed");
        Assert.Empty(result.Batches);

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("eventstream-snapshot-empty", EventBufferReadState.Empty, PlcsimOperationStatus.Succeeded, true, 0)]
    [InlineData("eventstream-snapshot-partial", EventBufferReadState.Valid, PlcsimOperationStatus.Succeeded, false, 0)]
    [InlineData("eventstream-snapshot-wraparound", EventBufferReadState.Valid, PlcsimOperationStatus.Succeeded, false, 0)]
    [InlineData("eventstream-snapshot-full", EventBufferReadState.Valid, PlcsimOperationStatus.Succeeded, false, 0)]
    [InlineData("eventstream-snapshot-index-in-advanced", EventBufferReadState.Valid, PlcsimOperationStatus.Succeeded, false, 0)]
    [InlineData("eventstream-snapshot-index-out-changed", EventBufferReadState.RetryRequired, PlcsimOperationStatus.SucceededWithWarnings, false, 0)]
    [InlineData("eventstream-snapshot-sequence-zero", EventBufferReadState.RetryRequired, PlcsimOperationStatus.SucceededWithWarnings, false, 0)]
    [InlineData("eventstream-snapshot-sequence-regression", EventBufferReadState.Inconsistent, PlcsimOperationStatus.SucceededWithWarnings, false, 0)]
    [InlineData("eventstream-snapshot-lost-events", EventBufferReadState.Valid, PlcsimOperationStatus.Succeeded, false, 2)]
    [InlineData("eventstream-snapshot-config-error", EventBufferReadState.RetryRequired, PlcsimOperationStatus.SucceededWithWarnings, false, 0)]
    [InlineData("eventstream-snapshot-partial-entry-read", EventBufferReadState.RetryRequired, PlcsimOperationStatus.SucceededWithWarnings, false, 0)]
    public async Task V7_worker_session_classifies_eventstream_polling_snapshot(
        string validationBehavior,
        EventBufferReadState expectedReadState,
        PlcsimOperationStatus expectedStatus,
        bool expectedBufferObservedEmpty,
        ushort expectedLostEventCount)
    {
        var result = await StartValidationEventStreamMonitoringAsync(validationBehavior);

        Assert.Equal(expectedStatus, result.Monitoring.Status);
        Assert.True(result.Monitoring.Succeeded);
        Assert.True(result.Monitoring.State.IsMonitoring);
        Assert.Equal(expectedReadState, result.Monitoring.State.LastReadState);
        Assert.Equal(expectedBufferObservedEmpty, result.Monitoring.State.BufferObservedEmpty);
        Assert.Equal(expectedLostEventCount, result.Monitoring.State.LostEventCount);

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V7_worker_session_publishes_event_batch_for_valid_eventstream_snapshot()
    {
        var result = await StartValidationEventStreamMonitoringAsync("eventstream-snapshot-partial");

        var batch = Assert.Single(result.Batches);
        Assert.Equal(result.Communication.Communication.SessionId, batch.SessionId.Value);
        Assert.Equal(EventBufferReadState.Valid, batch.ReadState);
        Assert.Equal(EventStreamBatchReleaseState.Completed, result.Monitoring.State.BatchReleaseState);
        Assert.Equal(new uint[] { 1, 2 }, batch.Events.Select(entry => entry.SequenceId).ToArray());

        var first = batch.Events[0];
        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks(1700000000001000000L / 100), first.Timestamp);
        Assert.Equal(UnitTestOwnerScope.RunControl, first.OwnerScope);
        Assert.Equal(UnitTestEventType.StateTransition, first.EventType);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, first.Severity);
        Assert.Equal("ValidationRunControl", first.Name);
        Assert.Equal("ValidationResource", first.ResourceName);
        Assert.Equal(UnitTestLifecycleState.Starting, first.State);
        Assert.Equal(UnitTestResultState.NotEvaluated, first.Result);
        Assert.Equal("Validation run started.", first.Message);

        var last = batch.Events[1];
        Assert.Equal(UnitTestLifecycleState.Completed, last.State);
        Assert.Equal(UnitTestResultState.Passed, last.Result);
        Assert.Equal("Validation run completed.", last.Message);

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V7_worker_logs_bounded_eventstream_batch_summary_without_raw_payload_by_default()
    {
        var logDirectory = CreateTestLogDirectory();
        var result = await StartValidationEventStreamMonitoringAsync(
            "eventstream-snapshot-partial",
            logDirectory);
        var stop = await result.Communication.StopEventStreamMonitoringAsync(
            new PlcsimEventStreamStopRequest(
                result.Communication.Communication.SessionId,
                "stop"),
            CancellationToken.None);
        Assert.True(stop.Succeeded);

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);

        var log = ReadWorkerLog(logDirectory, "siemens-test");
        Assert.Contains(
            "EventStream batch captured. CommunicationSessionId=",
            log);
        Assert.Contains("EventCount=2 FirstSequenceId=1 LastSequenceId=2", log);
        Assert.Contains("Worker command received. CommandType=StartEventStreamMonitoring", log);
        Assert.Contains("Worker command received. CommandType=StopEventStreamMonitoring", log);
        Assert.DoesNotContain("Raw UnitTest event.", log);
        Assert.DoesNotContain("Validation run started.", log);
    }

    [Fact]
    public async Task V7_worker_logs_snapshot_phase_and_release_roundtrip_evidence()
    {
        var logDirectory = CreateTestLogDirectory();
        var result = await StartValidationEventStreamMonitoringAsync(
            "worker-revalidation-eventstream-entry",
            logDirectory);
        await Task.Delay(100);

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);

        var log = ReadWorkerLog(logDirectory, "siemens-test");
        Assert.Contains("RunControlReadDurationMs=", log);
        Assert.Contains("PreMetadataDurationMs=", log);
        Assert.Contains("EntryReadDurationMs=", log);
        Assert.Contains("PostMetadataDurationMs=", log);
        Assert.Contains("MetadataBatchReadCalls=2", log);
        Assert.Contains("EventStream release write completed. DeleteSequenceId=1", log);
        Assert.Contains(
            "EventStream release completed; capture resumed. CommunicationSessionId=",
            log);
        Assert.Contains("DeleteSequenceId=1 RoundtripDurationMs=", log);
        Assert.Contains("ConfirmationPolls=1", log);
    }

    [Fact]
    public async Task V7_worker_releases_and_publishes_only_the_configured_fifo_prefix()
    {
        var logDirectory = CreateTestLogDirectory();
        var options = CreateWorkerOptions() with { MaxEventsPerBatch = 2 };
        var result = await StartValidationEventStreamMonitoringAsync(
            "worker-revalidation-eventstream-bounded-full",
            logDirectory,
            workerOptions: options);

        var batch = Assert.Single(result.Batches);
        Assert.Equal(new uint[] { 1, 2 }, batch.Events.Select(entry => entry.SequenceId));
        Assert.Equal(EventStreamBatchReleaseState.Completed, result.Monitoring.State.BatchReleaseState);

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);

        var log = ReadWorkerLog(logDirectory, "siemens-test");
        Assert.Contains("EventCount=2 CapturedEventCount=2", log);
        Assert.Contains("ReadState=\"Valid\"", log);
        Assert.Contains("EffectiveMaxEventsPerBatch=2", log);
        Assert.Contains("KnownRemainingEvents=true", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EventStream release write completed. DeleteSequenceId=2", log);
    }

    [Fact]
    public async Task V7_worker_caps_the_configured_batch_size_at_the_plc_buffer_length()
    {
        var logDirectory = CreateTestLogDirectory();
        var options = CreateWorkerOptions() with { MaxEventsPerBatch = 10 };
        var result = await StartValidationEventStreamMonitoringAsync(
            "worker-revalidation-eventstream-bounded-full",
            logDirectory,
            workerOptions: options);

        var batch = Assert.Single(result.Batches);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, batch.Events.Select(entry => entry.SequenceId));

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);

        var log = ReadWorkerLog(logDirectory, "siemens-test");
        Assert.Contains("EffectiveMaxEventsPerBatch=4", log);
        Assert.Contains("KnownRemainingEvents=false", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EventStream release write completed. DeleteSequenceId=4", log);
    }

    [Fact]
    public async Task V7_worker_uses_drain_cadence_until_bounded_backlog_and_release_are_clear()
    {
        var logDirectory = CreateTestLogDirectory();
        var options = CreateWorkerOptions() with
        {
            MonitoringInterval = TimeSpan.FromMilliseconds(250),
            DrainInterval = TimeSpan.FromMilliseconds(5),
            MaxEventsPerBatch = 2
        };
        var result = await StartValidationEventStreamMonitoringAsync(
            "worker-revalidation-eventstream-adaptive-drain",
            logDirectory,
            workerOptions: options);

        Assert.True(
            SpinWait.SpinUntil(() => result.Batches.Count >= 2, TimeSpan.FromSeconds(2)),
            "The adaptive worker did not drain both bounded FIFO prefixes.");
        Assert.Equal(new uint[] { 1, 2 }, result.Batches[0].Events.Select(entry => entry.SequenceId));
        Assert.Equal(new uint[] { 3, 4 }, result.Batches[1].Events.Select(entry => entry.SequenceId));
        await Task.Delay(50);

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);

        var log = ReadWorkerLog(logDirectory, "siemens-test");
        const string monitoring = "EventStream observation cadence changed. State=Monitoring Strategy=Polling IntervalMs=250";
        const string drain = "EventStream observation cadence changed. State=Drain Strategy=Polling IntervalMs=5";
        var drainStarted = log.IndexOf(drain, StringComparison.Ordinal);
        var monitoringResumed = log.IndexOf(monitoring, StringComparison.Ordinal);
        Assert.True(drainStarted >= 0, "The worker did not enter the drain cadence for initial backlog.");
        Assert.True(monitoringResumed > drainStarted, "Regular monitoring did not resume after draining.");
    }

    [Fact]
    public async Task V7_worker_uses_default_drain_cadence_when_drain_interval_is_omitted()
    {
        var logDirectory = CreateTestLogDirectory();
        var options = CreateWorkerOptions() with
        {
            MonitoringInterval = TimeSpan.FromMilliseconds(37),
            DrainInterval = null,
            MaxEventsPerBatch = 2
        };
        var result = await StartValidationEventStreamMonitoringAsync(
            "worker-revalidation-eventstream-adaptive-drain",
            logDirectory,
            workerOptions: options);

        Assert.True(
            SpinWait.SpinUntil(() => result.Batches.Count >= 2, TimeSpan.FromSeconds(2)),
            "The worker did not drain both FIFO prefixes with the default cadence.");
        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);

        var log = ReadWorkerLog(logDirectory, "siemens-test");
        Assert.Contains(
            "EventStream observation cadence changed. State=Drain Strategy=Polling IntervalMs=1",
            log);
    }

    [Fact]
    public async Task V7_worker_keeps_observing_and_accepts_commands_while_release_remains_pending()
    {
        var options = CreateWorkerOptions() with
        {
            MonitoringInterval = TimeSpan.FromMilliseconds(250),
            DrainInterval = TimeSpan.FromMilliseconds(5),
            MaxEventsPerBatch = 2
        };
        var result = await StartValidationEventStreamMonitoringAsync(
            "worker-revalidation-eventstream-adaptive-pending-release",
            workerOptions: options);
        var runControlObservationCount = 0;
        result.Communication.RunControlObservationChanged += (_, _) =>
            Interlocked.Increment(ref runControlObservationCount);

        Assert.True(
            SpinWait.SpinUntil(
                () => Volatile.Read(ref runControlObservationCount) >= 2,
                TimeSpan.FromSeconds(1)),
            "RunControl observation did not continue while the PLC release remained pending.");
        Assert.Single(result.Batches);
        Assert.Equal(new uint[] { 1, 2 }, result.Batches[0].Events.Select(entry => entry.SequenceId));

        foreach (var command in new[]
                 {
                     UnitTestRunCommand.Start,
                     UnitTestRunCommand.Reset,
                     UnitTestRunCommand.Abort
                 })
        {
            var commandResult = await result.Communication.RequestRunControlCommandAsync(
                    new PlcsimRunControlCommandRequest(
                        result.Communication.Communication.SessionId,
                        command,
                        TimeSpan.FromSeconds(1),
                        "pending-release-" + command),
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(commandResult.Succeeded);
            Assert.True(commandResult.Accepted);
        }

        var reset = await result.Communication.RequestEventBufferResetAsync(
                new PlcsimEventBufferResetRequest(
                    result.Communication.Communication.SessionId,
                    TimeSpan.FromSeconds(1),
                    "pending-release-reset"),
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(reset.Succeeded);
        Assert.True(reset.Accepted);
        Assert.Single(result.Batches);

        var stop = await result.Communication.StopEventStreamMonitoringAsync(
                new PlcsimEventStreamStopRequest(
                    result.Communication.Communication.SessionId,
                    "pending-release-stop"),
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(stop.Succeeded);
        Assert.False(stop.State.IsMonitoring);

        var restart = await result.Communication.StartEventStreamMonitoringAsync(
                new PlcsimEventStreamMonitoringRequest(
                    result.Communication.Communication.SessionId,
                    "pending-release-restart"),
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(restart.Succeeded);
        Assert.True(restart.State.IsMonitoring);

        foreach (var state in result.MonitoringStates.Concat(new[] { stop.State, restart.State }))
        {
            Assert.Equal(4u, state.ProducerSequenceId);
            Assert.Equal(0u, state.DeletedSequenceId);
            Assert.False(state.BufferObservedEmpty);
            Assert.Equal(EventBufferReadState.Valid, state.LastReadState);
            Assert.Equal(EventStreamBatchReleaseState.Completed, state.BatchReleaseState);
        }
        Assert.Single(result.Batches);

        await result.Communication.CloseAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await result.Worker.CloseAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task V7_worker_logs_raw_events_only_when_debug_and_raw_event_logging_are_enabled()
    {
        var logDirectory = CreateTestLogDirectory();
        var result = await StartValidationEventStreamMonitoringAsync(
            "eventstream-snapshot-partial",
            logDirectory,
            rawEventLoggingEnabled: true);

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);

        var log = ReadWorkerLog(logDirectory, "siemens-test");
        Assert.Equal(
            2,
            log.Split(new[] { "Raw UnitTest event." }, StringSplitOptions.None).Length - 1);
        Assert.Contains("EventMessage=Validation run started.", log);
        Assert.Contains("EventMessage=Validation run completed.", log);
    }

    [Fact]
    public async Task V7_worker_ignores_raw_event_option_below_debug_level()
    {
        var logDirectory = CreateTestLogDirectory();
        var result = await StartValidationEventStreamMonitoringAsync(
            "eventstream-snapshot-partial",
            logDirectory,
            rawEventLoggingEnabled: true,
            logLevel: LogLevel.Information);

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);

        var log = ReadWorkerLog(logDirectory, "siemens-test");
        Assert.DoesNotContain("Raw UnitTest event.", log);
        Assert.DoesNotContain("Validation run started.", log);
    }

    [Fact]
    public async Task V7_worker_logs_eventstream_loss_as_bounded_warning()
    {
        var logDirectory = CreateTestLogDirectory();
        var result = await StartValidationEventStreamMonitoringAsync(
            "eventstream-snapshot-lost-events",
            logDirectory);

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);

        var log = ReadWorkerLog(logDirectory, "siemens-test");
        Assert.Contains(
            "EventStream batch may be incomplete because the PLC reported lost events.",
            log);
        Assert.Contains("LostEventCount=2", log);
    }

    [Fact]
    public async Task V7_worker_session_preserves_fifo_order_across_wraparound()
    {
        var result = await StartValidationEventStreamMonitoringAsync("eventstream-snapshot-wraparound");

        var batch = Assert.Single(result.Batches);
        Assert.Equal(new uint[] { 1, 2, 3 }, batch.Events.Select(entry => entry.SequenceId).ToArray());

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V7_worker_session_round_trips_escaped_event_payload()
    {
        var result = await StartValidationEventStreamMonitoringAsync("eventstream-snapshot-escaped-payload");

        var batch = Assert.Single(result.Batches);
        Assert.All(batch.Events, entry =>
        {
            Assert.Equal("Resource | A=B % complete", entry.ResourceName);
            Assert.Equal("Validation | payload=A=B % complete.", entry.Message);
        });

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V7_worker_session_maps_unknown_contract_values_without_losing_the_batch()
    {
        var result = await StartValidationEventStreamMonitoringAsync("eventstream-snapshot-unknown-values");

        var batch = Assert.Single(result.Batches);
        Assert.All(batch.Events, entry =>
        {
            Assert.Equal(UnitTestOwnerScope.Unknown, entry.OwnerScope);
            Assert.Equal(UnitTestEventType.Unknown, entry.EventType);
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.None, entry.Severity);
            Assert.Equal(UnitTestLifecycleState.Unknown, entry.State);
            Assert.Equal(UnitTestResultState.Unknown, entry.Result);
        });

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public void V7_worker_eventstream_warning_result_remains_continuable()
    {
        var snapshot = new PlcsimEventBufferSnapshot(
            new PlcsimEventBufferContractSnapshot(UnitTestContractVersions.EventStreamModel, "validation", ContractValid: true),
            new PlcsimEventBufferFifoControlSnapshot(1, 3, 4, BufferEmpty: false, BufferFull: false, ConfigError: false, IndexError: false),
            new PlcsimEventBufferStatusSnapshot(SequenceId: 4, DeletedSequenceId: 0, LostEventCount: 0),
            Array.Empty<PlcsimEventBufferEntrySnapshot>(),
            EventBufferReadState.RetryRequired,
            DateTimeOffset.UtcNow);
        var classification = new PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker.EventBufferPollingSnapshotClassification(
            snapshot,
            Array.Empty<ushort>(),
            Array.Empty<PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker.EventBufferCapturedSequence>());

        var result = PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker.EventStreamMonitoringResult.CreateBufferObservationResult(
            "communication-session",
            isMonitoring: true,
            classification);

        Assert.Equal("SucceededWithWarnings", result.Status);
        Assert.True(result.Succeeded);
        Assert.Null(result.CapturedBatch);
    }

    [Fact]
    public async Task V7_worker_session_neither_releases_nor_publishes_a_partially_read_entry()
    {
        var result = await StartValidationEventStreamMonitoringAsync("eventstream-snapshot-partial-entry-read");

        Assert.Equal(PlcsimOperationStatus.SucceededWithWarnings, result.Monitoring.Status);
        Assert.Equal(EventBufferReadState.RetryRequired, result.Monitoring.State.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Idle, result.Monitoring.State.BatchReleaseState);
        Assert.Empty(result.Batches);

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("eventstream-snapshot-release-failed", EventStreamBatchReleaseState.Idle)]
    [InlineData("eventstream-snapshot-release-pending", EventStreamBatchReleaseState.Idle)]
    public async Task V7_worker_session_does_not_publish_batch_before_automatic_release_succeeds(
        string validationBehavior,
        EventStreamBatchReleaseState expectedReleaseState)
    {
        var result = await StartValidationEventStreamMonitoringAsync(validationBehavior);

        Assert.True(result.Monitoring.Succeeded);
        Assert.Equal(EventBufferReadState.Valid, result.Monitoring.State.LastReadState);
        Assert.Equal(expectedReleaseState, result.Monitoring.State.BatchReleaseState);
        Assert.Empty(result.Batches);

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V7_worker_session_publishes_empty_observation_batch_without_release()
    {
        var result = await StartValidationEventStreamMonitoringAsync("eventstream-snapshot-empty");

        var batch = Assert.Single(result.Batches);
        Assert.Empty(batch.Events);
        Assert.True(batch.BufferObservedEmpty);
        Assert.Equal(EventBufferReadState.Empty, batch.ReadState);
        Assert.Equal(EventStreamBatchReleaseState.Idle, result.Monitoring.State.BatchReleaseState);

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V7_worker_session_publishes_event_batch_with_lost_event_count()
    {
        var result = await StartValidationEventStreamMonitoringAsync("eventstream-snapshot-lost-events");

        var batch = Assert.Single(result.Batches);
        Assert.Equal(EventBufferReadState.Valid, batch.ReadState);
        Assert.Equal((ushort)2, batch.LostEventCount);
        Assert.Equal(new uint[] { 1, 2 }, batch.Events.Select(entry => entry.SequenceId).ToArray());

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V7_worker_session_publishes_monitoring_state_without_event_batch()
    {
        var result = await StartValidationEventStreamMonitoringAsync("eventstream-monitoring-state-without-batch");

        var state = Assert.Single(result.MonitoringStates);
        Assert.True(state.IsMonitoring);
        Assert.Equal(EventBufferReadState.Valid, state.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Idle, state.BatchReleaseState);
        Assert.False(state.BufferObservedEmpty);
        Assert.Equal((ushort)2, state.LostEventCount);
        Assert.False(state.BufferFull);
        Assert.Equal((uint)4, state.ProducerSequenceId);
        Assert.Equal((uint)0, state.DeletedSequenceId);
        Assert.Empty(result.Batches);

        await result.Communication.CloseAsync(CancellationToken.None);
        await result.Worker.CloseAsync(CancellationToken.None);
    }
}
