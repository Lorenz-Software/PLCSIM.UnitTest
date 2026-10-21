using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.Tests.Core.TestLogging;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using System.Security;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Workflow;

public sealed class RunWorkflowFakeProfilePathTests
{
    [Fact]
    public async Task Delayed_batch_result_does_not_reactivate_a_timed_out_run()
    {
        var time = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(time);
        var workflow = CreateWorkflow(services, time);
        await PrepareMonitoringAsync(workflow, services, time);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        ObserveRunStarted(services);
        var processor = Assert.IsType<EventStreamProcessor>(typeof(RunWorkflowService)
            .GetField("eventStreamProcessor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(workflow));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var paused = 0;
        EventHandler<EventStreamProcessorUpdateEventArgs> pause = (_, args) =>
        {
            if (args.Update.Kind == EventStreamProcessorUpdateKind.BatchProcessed && Interlocked.Exchange(ref paused, 1) == 0)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Batch probe was not released.");
            }
        };
        processor.ProcessorUpdateEventHandler += pause;
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var batch = Task.Run(() => eventStream.PublishBatchWithoutCompletionAnchor());
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            time.Advance(RunConfigurationDefaults.DefaultTestRunTimeout);
            Assert.Equal(TestRunMonitoringState.TimedOut, workflow.Current.MonitorState);
            Assert.Equal(UnitTestRunState.TimedOut, processor.Current!.State);
        }
        finally
        {
            release.Set();
            await batch.WaitAsync(TimeSpan.FromSeconds(5));
            processor.ProcessorUpdateEventHandler -= pause;
        }

        try
        {
            Assert.Equal(TestRunMonitoringState.TimedOut, workflow.Current.MonitorState);
            Assert.Equal(UnitTestRunState.TimedOut, workflow.Current.CurrentRun!.State);
            Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
            Assert.True(eventStream.CurrentMonitoringState.IsMonitoring);
        }
        finally
        {
            await workflow.UnloadAccessProfileAsync(CreatePolicy(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Access_profile_session_bounds_the_retained_activity_trace()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var workflow = CreateWorkflow(services, timeProvider);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();

        await RunWorkflowProfileTestHelper.LoadAndBindAsync(workflow, config, services, timeProvider);

        var activeTrace = Assert.IsType<UnitTestActivityTraceSnapshot>(workflow.EvidenceAnalysis.CurrentTrace);
        Assert.True(activeTrace.IsCaptureActive);
        Assert.Equal(
            UnitTestSessionActivityKind.Started,
            Assert.IsType<SessionActivityPayload>(activeTrace.Entries[0].Payload).Activity);

        var unload = await workflow.UnloadAccessProfileAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(unload.Result.IsSuccessful);
        var completedTrace = Assert.IsType<UnitTestActivityTraceSnapshot>(workflow.EvidenceAnalysis.CurrentTrace);
        Assert.False(completedTrace.IsCaptureActive);
        Assert.NotNull(completedTrace.EndedAt);
        Assert.Equal(
            UnitTestSessionActivityKind.Ended,
            Assert.IsType<SessionActivityPayload>(completedTrace.Entries[^1].Payload).Activity);
        Assert.True(workflow.EvidenceAnalysis.ClearCompletedTrace());
        Assert.Null(workflow.EvidenceAnalysis.CurrentTrace);
    }

    [Fact]
    public async Task Workflow_capture_records_state_changes_once_and_keeps_command_transport_correlated()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var workflow = CreateWorkflow(services, timeProvider);
        await PrepareMonitoringAsync(workflow, services, timeProvider);

        plcControl.RepublishCurrentState();
        lifecycle.PublishObservation(lifecycle.CurrentObservation);
        var beforeDuplicateStates = Assert.IsType<UnitTestActivityTraceSnapshot>(workflow.EvidenceAnalysis.CurrentTrace);
        var plcStateCount = beforeDuplicateStates.Entries.Count(entry => entry.Payload is PlcStateActivityPayload);
        var runControlCount = beforeDuplicateStates.Entries.Count(entry => entry.Payload is RunControlActivityPayload);
        plcControl.RepublishCurrentState();
        plcControl.RepublishCurrentState();
        lifecycle.PublishObservation(lifecycle.CurrentObservation);
        lifecycle.PublishObservation(lifecycle.CurrentObservation);
        var afterDuplicateStates = Assert.IsType<UnitTestActivityTraceSnapshot>(workflow.EvidenceAnalysis.CurrentTrace);
        Assert.Equal(plcStateCount, afterDuplicateStates.Entries.Count(entry => entry.Payload is PlcStateActivityPayload));
        Assert.Equal(runControlCount, afterDuplicateStates.Entries.Count(entry => entry.Payload is RunControlActivityPayload));

        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);

        var trace = Assert.IsType<UnitTestActivityTraceSnapshot>(workflow.EvidenceAnalysis.CurrentTrace);
        var commands = trace.Entries
            .Where(entry => entry.Payload is CommandActivityPayload { Command: UnitTestActivityCommandKind.Start })
            .ToArray();
        Assert.Equal(2, commands.Length);
        Assert.Equal(commands[0].OperationId, commands[1].OperationId);
        Assert.Equal(commands[0].LifecycleObservationId, commands[1].LifecycleObservationId);
        Assert.NotNull(commands[0].OperationId);
        Assert.NotNull(commands[0].LifecycleObservationId);
        Assert.Equal(
            UnitTestActivityCommandPhase.Requested,
            Assert.IsType<CommandActivityPayload>(commands[0].Payload).Phase);
        var completed = Assert.IsType<CommandActivityPayload>(commands[1].Payload);
        Assert.Equal(UnitTestActivityCommandPhase.Completed, completed.Phase);
        Assert.True(completed.Accepted);
        Assert.Equal(OperationResultState.Succeeded, completed.ResultState);
    }

    [Fact]
    public async Task Workflow_capture_exposes_accepted_events_and_communication_failure_analysis()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services, timeProvider);
        await PrepareMonitoringAsync(workflow, services, timeProvider);

        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        ObserveRunStarted(services);
        var batch = eventStream.PublishBatchWithoutCompletionAnchor();
        lifecycle.SimulateLifecycleCommunicationInvalidated();

        var trace = Assert.IsType<UnitTestActivityTraceSnapshot>(workflow.EvidenceAnalysis.CurrentTrace);
        var capturedEvent = Assert.Single(trace.Entries.Where(entry =>
            entry.Payload is PlcEventActivityPayload payload
            && payload.Event.SequenceId == batch.Events.Single().SequenceId));
        Assert.Equal(
            batch.Events.Single().SequenceId,
            Assert.IsType<PlcEventActivityPayload>(capturedEvent.Payload).Event.SequenceId);
        Assert.Single(trace.Entries.Where(entry =>
            entry.Payload is AccessHealthActivityPayload
            {
                ServiceKind: AccessServiceKind.UnitTestLifecycle,
                Health.State: AccessHealthState.Invalidated
            }));
        Assert.Single(trace.Entries.Where(entry =>
            entry.Payload is DiagnosticActivityPayload diagnostic
            && diagnostic.Diagnostic.Code == "Workflow.AccessHealth.UnitTestLifecycle.Invalidated"));

        var analysis = workflow.EvidenceAnalysis.AnalyzeCommunicationSession();
        Assert.Contains(analysis.Incidents, incident =>
            incident.Classification == EvidenceIncidentClassification.CommunicationFailure);
    }

    [Fact]
    public async Task Fake_profile_prepare_plc_and_unittest_path_updates_gates()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);

        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);
        await workflow.TransferProjectAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(Gate(workflow, WorkflowAction.StartPlc).IsAvailable);
        Assert.False(Gate(workflow, WorkflowAction.StopPlc).IsAvailable);
        Assert.False(Gate(workflow, WorkflowAction.PrepareUnitTestCommunication).IsAvailable);
        Assert.Contains(
            Gate(workflow, WorkflowAction.PrepareUnitTestCommunication).Reasons,
            reason => reason.Code == ActionGateReasonCode.PlcNotRunning);
        Assert.False(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
        Assert.Contains(Gate(workflow, WorkflowAction.StartUnitTest).Reasons, reason => reason.Code == ActionGateReasonCode.PlcNotRunning);

        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);

        Assert.Equal(RunWorkflowState.Monitoring, workflow.Current.State);
        Assert.NotNull(workflow.Current.LifecycleObservation);
        Assert.Equal(UnitTestLifecycleState.Idle, workflow.Current.LifecycleObservation.State);
        Assert.True(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);

        Assert.NotNull(workflow.Current.EventStreamState);
        Assert.True(workflow.Current.EventStreamState.IsMonitoring);
        Assert.Equal(TestRunMonitoringState.WaitingForRun, workflow.Current.MonitorState);
        Assert.True(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
        Assert.False(Gate(workflow, WorkflowAction.AbortUnitTest).IsAvailable);
        Assert.Contains(Gate(workflow, WorkflowAction.AbortUnitTest).Reasons, reason => reason.Code == ActionGateReasonCode.NoRunAvailable);
        Assert.True(Gate(workflow, WorkflowAction.ResetUnitTest).IsAvailable);

        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        ObserveRunStarted(services);

        Assert.False(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
        Assert.Contains(Gate(workflow, WorkflowAction.StartUnitTest).Reasons, reason => reason.Code == ActionGateReasonCode.UnitTestNotReady);
        Assert.True(Gate(workflow, WorkflowAction.AbortUnitTest).IsAvailable);
    }

    [Fact]
    public async Task Event_buffer_reset_acceptance_keeps_start_blocked_until_clean_state_is_observed()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        eventStream.PublishLostEventEvidence(lostEventCount: 3);

        var blockedStart = await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        var reset = await workflow.ResetEventBufferAsync(CreatePolicy(), CancellationToken.None);

        Assert.False(blockedStart.Result.IsSuccessful);
        Assert.Contains("lost-event evidence", blockedStart.Result.Message, StringComparison.Ordinal);
        Assert.True(reset.Result.IsSuccessful);
        Assert.True(reset.Result.Value!.Accepted);
        Assert.Equal(1, eventStream.EventBufferResetCount);
        Assert.Equal(3, workflow.Current.EventStreamState!.LostEventCount);
        Assert.False(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);

        eventStream.PublishEmptyBuffer();

        Assert.Equal(0, workflow.Current.EventStreamState!.LostEventCount);
        Assert.True(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
    }

    [Fact]
    public async Task Event_buffer_reset_marks_active_run_evidence_incomplete_before_dispatch()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        ObserveRunStarted(services);
        Assert.Equal(TestRunMonitoringState.RunActive, workflow.Current.MonitorState);

        var reset = await workflow.ResetEventBufferAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(reset.Result.IsSuccessful);
        Assert.Equal(1, eventStream.EventBufferResetCount);
        Assert.Equal(TestRunMonitoringState.TimedOut, workflow.Current.MonitorState);
        Assert.Equal(UnitTestRunState.TimedOut, workflow.Current.CurrentRun!.State);
        Assert.Equal(EvidenceState.Incomplete, workflow.Current.CurrentRun.EvidenceState);
    }

    [Fact]
    public async Task Batchless_overflow_recovers_and_allows_a_subsequent_run_without_reconnect()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        ObserveRunStarted(services);
        var firstBatch = eventStream.PublishBatchWithoutCompletionAnchor();
        var firstEpochId = workflow.Current.CurrentRun!.EpochId;

        eventStream.PublishEventBufferFull(
            firstBatch.ProducerSequenceId,
            firstBatch.DeletedSequenceId);
        eventStream.PublishEventBufferLostEvents(
            lostEventCount: 19,
            producerSequenceId: firstBatch.ProducerSequenceId,
            deletedSequenceId: firstBatch.DeletedSequenceId);
        var continuedBatch = eventStream.PublishBatchWithoutCompletionAnchor(lostEventCount: 19);

        Assert.True(workflow.Current.EventStreamState!.IsMonitoring);
        Assert.Equal((ushort)19, workflow.Current.EventStreamState.LostEventCount);
        Assert.Equal(EvidenceState.Incomplete, workflow.Current.CurrentRun!.EvidenceState);
        Assert.Contains(
            firstBatch.Events.Single().SequenceId,
            workflow.Current.CurrentRun.EventStream.Select(item => item.SequenceId));
        Assert.Contains(
            continuedBatch.Events.Single().SequenceId,
            workflow.Current.CurrentRun.EventStream.Select(item => item.SequenceId));

        var bufferReset = await workflow.ResetEventBufferAsync(CreatePolicy(), CancellationToken.None);
        var affectedEpoch = workflow.Current.CurrentRun;
        var runReset = await workflow.ResetUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.CompleteReset();
        eventStream.PublishEventBufferRecovered();

        Assert.True(bufferReset.Result.IsSuccessful);
        Assert.True(runReset.Result.IsSuccessful);
        Assert.Equal(firstEpochId, affectedEpoch!.EpochId);
        Assert.Equal(EvidenceState.Incomplete, affectedEpoch.EvidenceState);
        Assert.Equal((ushort)19, affectedEpoch.LostEventCount);
        Assert.Equal((ushort)0, workflow.Current.EventStreamState!.LostEventCount);
        Assert.False(workflow.Current.EventStreamState.BufferFull);
        Assert.True(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);

        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();
        lifecycle.CompleteRun(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompleted(UnitTestResultState.Passed);

        Assert.NotEqual(firstEpochId, workflow.Current.CurrentRun!.EpochId);
        Assert.Equal(RunWorkflowState.RunCompleted, workflow.Current.State);
        Assert.Equal(EvidenceState.Complete, workflow.Current.CurrentRun.EvidenceState);
        Assert.Equal(UnitTestResultState.Passed, workflow.Current.CurrentRun.Result);
        Assert.Equal(0, lifecycle.ReleaseCommunicationCount);
        Assert.Equal(0, eventStream.ReleaseCommunicationCount);
    }

    [Fact]
    public async Task Failed_event_buffer_reset_keeps_dirty_start_gate_blocked()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        eventStream.EventBufferResetResult =
            OperationResult<EventBufferResetCommandResult>.Failure(
                new EventBufferResetCommandResult(Accepted: false),
                "Fake EventBuffer reset write failed.");
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        eventStream.PublishLostEventEvidence(lostEventCount: 2);

        var reset = await workflow.ResetEventBufferAsync(CreatePolicy(), CancellationToken.None);

        Assert.False(reset.Result.IsSuccessful);
        Assert.False(reset.Result.Value!.Accepted);
        Assert.Equal(1, eventStream.EventBufferResetCount);
        Assert.Equal(2, workflow.Current.EventStreamState!.LostEventCount);
        Assert.False(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
        Assert.Contains(
            Gate(workflow, WorkflowAction.StartUnitTest).Reasons,
            reason => reason.Code == ActionGateReasonCode.EventBufferHasLostEvents);
    }

    [Fact]
    public async Task Reconnect_runtime_rebuilds_fake_communication_without_reprovisioning_target()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var prepareRequest = Assert.IsType<PlcRuntimePrepareRequest>(plcControl.LastPrepareRuntimeRequest);

        var result = await workflow.ReconnectRuntimeAsync(CreatePolicy(), CancellationToken.None);
        await WaitUntilAsync(() => workflow.Current.State == RunWorkflowState.Monitoring);

        Assert.True(result.Result.IsSuccessful);
        Assert.Same(prepareRequest, plcControl.LastPrepareRuntimeRequest);
        Assert.Equal(
            1,
            Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle).ReleaseCommunicationCount);
        Assert.Equal(
            1,
            Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream).ReleaseCommunicationCount);
        Assert.True(workflow.Current.Status.RuntimePrepared);
        Assert.True(workflow.Current.Status.UnitTestCommunicationPrepared);
        Assert.True(workflow.Current.Status.MonitoringActive);
    }

    [Fact]
    public async Task Failed_runtime_revalidation_clears_communication_without_reprovisioning_runtime()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create()) with
        {
            ReconnectRuntimeResult = OperationResult<PreparedPlcRuntime>.Failure(
                "Fake runtime revalidation failed.")
        };
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var prepareRequest = plcControl.LastPrepareRuntimeRequest;

        var result = await workflow.ReconnectRuntimeAsync(CreatePolicy(), CancellationToken.None);

        Assert.False(result.Result.IsSuccessful);
        Assert.Same(prepareRequest, plcControl.LastPrepareRuntimeRequest);
        Assert.True(workflow.Current.Status.RuntimePrepared);
        Assert.False(workflow.Current.Status.UnitTestCommunicationPrepared);
        Assert.False(workflow.Current.Status.MonitoringActive);
        Assert.False(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
        Assert.True(Gate(workflow, WorkflowAction.ReconnectRuntime).IsAvailable);
    }

    [Fact]
    public async Task Reconnect_runtime_does_not_republish_completed_run_epoch()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        ObserveRunStarted(services);
        eventStream.PublishRunControlCompleted(UnitTestResultState.Passed);
        Assert.Equal(RunWorkflowState.RunCompleted, workflow.Current.State);
        var completedEpochId = workflow.Current.CurrentRun?.EpochId;

        var result = await workflow.ReconnectRuntimeAsync(CreatePolicy(), CancellationToken.None);
        await WaitUntilAsync(() => workflow.Current.State == RunWorkflowState.Monitoring);

        Assert.True(result.Result.IsSuccessful);
        Assert.Equal(completedEpochId, workflow.Current.CurrentRun?.EpochId);
        Assert.Equal(TestRunMonitoringState.WaitingForRun, workflow.Current.MonitorState);
    }

    [Fact]
    public async Task Reconnect_runtime_preserves_stopped_plc_state_without_starting_communication()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);
        await PrepareTransferredRuntimeAsync(workflow);

        var result = await workflow.ReconnectRuntimeAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(result.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.RuntimePrepared, workflow.Current.State);
        Assert.True(workflow.Current.Status.RuntimePrepared);
        Assert.False(workflow.Current.Status.UnitTestCommunicationPrepared);
        Assert.False(workflow.Current.Status.MonitoringActive);
    }

    [Fact]
    public async Task Reconnect_runtime_invalidates_active_run_before_rebuilding_communication()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        ObserveRunStarted(services);

        var result = await workflow.ReconnectRuntimeAsync(CreatePolicy(), CancellationToken.None);
        await WaitUntilAsync(() => workflow.Current.State == RunWorkflowState.Monitoring);

        Assert.True(result.Result.IsSuccessful);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Invalidated, workflow.Current.CurrentRun.State);
        Assert.Equal(EvidenceState.Incomplete, workflow.Current.CurrentRun.EvidenceState);
        Assert.Equal(TestRunMonitoringState.Invalidated, workflow.Current.MonitorState);
    }

    [Fact]
    public async Task Reconnect_runtime_invalidates_finalizing_run_before_rebuilding_communication()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        ObserveRunStarted(services);
        eventStream.PublishRunControlCompleted(UnitTestResultState.Passed, bufferObservedEmpty: false);
        Assert.Equal(TestRunMonitoringState.CompletionPending, workflow.Current.MonitorState);

        var result = await workflow.ReconnectRuntimeAsync(CreatePolicy(), CancellationToken.None);
        await WaitUntilAsync(() => workflow.Current.State == RunWorkflowState.Monitoring);

        Assert.True(result.Result.IsSuccessful);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Invalidated, workflow.Current.CurrentRun.State);
        Assert.Equal(EvidenceState.Incomplete, workflow.Current.CurrentRun.EvidenceState);
        Assert.Equal(TestRunMonitoringState.Invalidated, workflow.Current.MonitorState);
    }

    [Fact]
    public async Task Reconnect_runtime_cancelled_before_dispatch_preserves_existing_communication()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await workflow.ReconnectRuntimeAsync(CreatePolicy(), cancellation.Token);

        Assert.Equal(OperationResultState.Cancelled, result.Result.State);
        Assert.Equal(0, Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle).ReleaseCommunicationCount);
        Assert.Equal(0, Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream).ReleaseCommunicationCount);
        Assert.True(workflow.Current.Status.RuntimePrepared);
        Assert.True(workflow.Current.Status.UnitTestCommunicationPrepared);
        Assert.True(workflow.Current.Status.MonitoringActive);
    }

    [Fact]
    public async Task Current_run_configuration_snapshot_reaches_all_prepare_boundaries_after_discovery()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plugin = new FakeAccessProfilePlugin((_, _) => services);
        var discoveryConfig = FakeAccessProfileFactory.CreateDefaultConfig() with
        {
            Values = new AccessProfileConfigurationPayload("""{ "snapshot": "discovery" }""")
        };
        var runConfig = discoveryConfig with
        {
            Values = new AccessProfileConfigurationPayload("""{ "snapshot": "current-run" }""")
        };
        plugin.Initialize(new AccessProfileContext(
            NullLoggerFactory.Instance,
            timeProvider,
            CreateTempProfileRoot(),
            CreateTempProfileRoot(),
            TestOperationalLogging.Options));
        var sessionRequest = new AccessProfileSessionRequest(
            runConfig.ProfileId,
            runConfig.ProfileVersion,
            runConfig.ProjectSource,
            ProjectOwnershipMode.ApplicationManaged);
        var load = await plugin.LoadAsync(sessionRequest, CancellationToken.None);
        var profile = Assert.IsType<FakeAccessProfile>(load.Session);
        var workflow = CreateWorkflow(services, timeProvider);

        await workflow.BindAccessProfileSessionAsync(
            new AccessProfileSession(sessionRequest, plugin.Info, profile),
            CreatePolicy(),
            CancellationToken.None);
        await workflow.DiscoverProjectTargetsAsync(discoveryConfig, CreatePolicy(), CancellationToken.None);
        await workflow.DiscoverRuntimeTargetsAsync(discoveryConfig, CreatePolicy(), CancellationToken.None);
        var communicationTimeouts = new CommunicationTimeoutPolicy(
            TimeSpan.FromSeconds(17),
            TimeSpan.Zero);
        var timeoutPolicy = CreateRunSetup(runConfig).Execution.Timeouts with
        {
            CommunicationPrepare = communicationTimeouts.Prepare,
            CommunicationRevalidation = communicationTimeouts.Revalidation
        };
        var configure = workflow.Configure(CreateRunSetup(runConfig) with
        {
            Execution = new ExecutionPolicy(timeoutPolicy)
        });
        var prepareProject = await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        var prepareRuntime = await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);
        var transfer = await workflow.TransferProjectAsync(CreatePolicy(), CancellationToken.None);
        var start = await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(configure.IsSuccessful);
        Assert.True(prepareProject.Result.IsSuccessful);
        Assert.True(prepareRuntime.Result.IsSuccessful);
        Assert.True(transfer.Result.IsSuccessful);
        Assert.True(start.Result.IsSuccessful);
        Assert.Equal(
            runConfig,
            Assert.IsType<FakePlcProjectService>(services.Project)
                .LastPrepareProjectRequest?.Configuration);
        Assert.Equal(
            runConfig,
            Assert.IsType<FakePlcControlService>(services.PlcControl)
                .LastPrepareRuntimeRequest?.Configuration);
        Assert.Equal(runConfig, profile.LastPrepareUnitTestCommunicationRequest?.Configuration);
        Assert.Equal(
            communicationTimeouts,
            profile.LastPrepareUnitTestCommunicationRequest?.Timeouts);
    }

    [Fact]
    public async Task Start_stop_plc_updates_publication_state()
    {
        var workflow = CreateWorkflow(FakeAccessProfileFactory.CreatePlugin(FakeTime.Create()));
        var publicationSequences = new List<long>();
        workflow.PublicationEventHandler += (_, args) => publicationSequences.Add(args.Publication.Sequence);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig());

        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);
        await workflow.TransferProjectAsync(CreatePolicy(), CancellationToken.None);

        var start = await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);
        Assert.True(start.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.Monitoring, workflow.Current.State);

        var startupTrace = workflow.EvidenceAnalysis.CurrentTrace!;
        var request = Assert.Single(startupTrace.Entries.Where(entry => entry.Payload is CommandActivityPayload
            { Command: UnitTestActivityCommandKind.PlcStart, Phase: UnitTestActivityCommandPhase.Requested }));
        Assert.Contains(startupTrace.Entries, entry => entry.LifecycleObservationId == request.LifecycleObservationId
            && entry.Payload is PlcStateActivityPayload { State.OperatingState: PlcOperatingState.Running });

        var stop = await workflow.StopPlcAsync(CreatePolicy(), CancellationToken.None);
        Assert.True(stop.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.PlcStopped, workflow.Current.State);
        Assert.True(publicationSequences.Count > 0);
        for (var i = 1; i < publicationSequences.Count; i++)
        {
            Assert.True(publicationSequences[i] > publicationSequences[i - 1]);
        }
    }

    [Fact]
    public async Task External_plc_running_state_prepares_unittest_communication()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);
        await PrepareTransferredRuntimeAsync(workflow);

        plcControl.SimulateExternalPlcRunning();

        await WaitUntilAsync(() => workflow.Current.State == RunWorkflowState.Monitoring);
        Assert.NotNull(workflow.Current.LifecycleObservation);
        Assert.NotNull(workflow.Current.EventStreamState);
        Assert.True(workflow.Current.EventStreamState.IsMonitoring);
        Assert.True(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);

        plcControl.SimulateExternalPlcStopped();

        await WaitUntilAsync(() => workflow.Current.State == RunWorkflowState.PlcStopped
            && lifecycle.ReleaseCommunicationCount == 1
            && eventStream.ReleaseCommunicationCount == 1);
        Assert.Null(workflow.Current.LifecycleObservation);
        Assert.Null(workflow.Current.EventStreamState);
        Assert.Equal(1, eventStream.StopMonitoringCount);
        Assert.Contains(
            Gate(workflow, WorkflowAction.StartUnitTest).Reasons,
            reason => reason.Code == ActionGateReasonCode.PlcNotRunning);
        Assert.Equal(TestRunMonitoringState.NotMonitoring, workflow.Current.MonitorState);

        plcControl.SimulateExternalPlcRunning();

        await WaitUntilAsync(() => workflow.Current.State == RunWorkflowState.Monitoring
            && workflow.Current.EventStreamState?.IsMonitoring == true);
        Assert.Equal(TestRunMonitoringState.WaitingForRun, workflow.Current.MonitorState);
        Assert.DoesNotContain(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.EventStream.MonitoringStart.Failed");
    }

    [Fact]
    public async Task Automatic_monitoring_start_uses_configured_application_deadline()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services, timeProvider);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(
            workflow,
            config,
            services,
            timeProvider);
        var applicationTimeouts = RunConfigurationDefaults.CreateDefaultApplicationOperationTimeoutPolicy() with
        {
            Monitoring = TimeSpan.FromSeconds(5)
        };
        workflow.Configure(CreateRunSetup(config), applicationTimeouts);
        await PrepareTransferredRuntimeAsync(workflow);
        eventStream.HoldNextStartMonitoring = true;

        plcControl.SimulateExternalPlcRunning();
        await WaitUntilAsync(() => eventStream.IsStartMonitoringPending);
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => workflow.Current.Diagnostics.Any(
            diagnostic => diagnostic.Code == "Workflow.StartMonitoring.TimedOut"));

        Assert.False(eventStream.IsStartMonitoringPending);
        Assert.Null(workflow.Current.ActiveOperation);
    }

    [Fact]
    public async Task Automatic_communication_release_uses_configured_application_deadline()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services, timeProvider);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(
            workflow,
            config,
            services,
            timeProvider);
        var applicationTimeouts = RunConfigurationDefaults.CreateDefaultApplicationOperationTimeoutPolicy() with
        {
            Monitoring = TimeSpan.FromSeconds(5)
        };
        workflow.Configure(CreateRunSetup(config), applicationTimeouts);
        await PrepareTransferredRuntimeAsync(workflow);
        plcControl.SimulateExternalPlcRunning();
        await WaitUntilAsync(() => workflow.Current.State == RunWorkflowState.Monitoring);
        eventStream.HoldNextReleaseCommunication = true;

        plcControl.SimulateExternalPlcStopped();
        await WaitUntilAsync(() => eventStream.IsReleaseCommunicationPending);
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => workflow.Current.Diagnostics.Any(
            diagnostic => diagnostic.Code == "Workflow.PlcState.UnitTestCommunicationRelease.TimedOut"));

        Assert.False(eventStream.IsReleaseCommunicationPending);
        Assert.Equal(RunWorkflowState.PlcStopped, workflow.Current.State);
        Assert.Null(workflow.Current.EventStreamState);
        Assert.False(workflow.Current.Status.UnitTestCommunicationContextActive);
    }

    [Fact]
    public async Task Failed_automatic_communication_preparation_blocks_unittest_action_gates()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create()) with
        {
            PrepareUnitTestCommunicationResult =
                OperationResult<CommunicationSession>.Failure("Fake communication unavailable.")
        };
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);
        await PrepareTransferredRuntimeAsync(workflow);

        plcControl.SimulateExternalPlcRunning();

        await WaitUntilAsync(() => workflow.Current.State == RunWorkflowState.Error);

        Assert.False(workflow.Current.Status.UnitTestCommunicationPrepared);
        Assert.False(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
        Assert.False(Gate(workflow, WorkflowAction.AbortUnitTest).IsAvailable);
        Assert.False(Gate(workflow, WorkflowAction.ResetUnitTest).IsAvailable);
        Assert.Contains(
            Gate(workflow, WorkflowAction.StartUnitTest).Reasons,
            reason => reason.Code == ActionGateReasonCode.UnitTestCommunicationNotPrepared);
        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.Communication.Prepare.Failed");
    }

    [Fact]
    public async Task Running_state_observed_during_runtime_preparation_starts_communication_after_setup_operation()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        plcControl.RuntimeStateObservedDuringPreparation = new PlcState(
            RuntimeInstanceState.Available,
            PlcOperatingState.Running,
            [plcControl.DefaultRuntimeTarget],
            ReadyForTransfer: true,
            ReadyForExecution: true);
        var workflow = CreateWorkflow(services, timeProvider);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(
            workflow,
            config,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider,
            ProjectOwnershipMode.ExternallyManaged);
        var configure = workflow.Configure(CreateRunSetup(config) with { ProjectTarget = null });
        Assert.True(configure.IsSuccessful);

        var prepare = await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(prepare.Result.IsSuccessful);
        await WaitUntilAsync(() => workflow.Current.State == RunWorkflowState.Monitoring);
        Assert.True(workflow.Current.Status.PlcRunning);
        Assert.True(workflow.Current.Status.UnitTestCommunicationPrepared);
        Assert.True(workflow.Current.Status.UnitTestCommunicationContextActive);
        Assert.True(workflow.Current.Status.MonitoringActive);
        Assert.True(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
    }

    [Fact]
    public async Task External_runtime_context_lost_invalidates_unittest_communication_without_error_state()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);
        await PrepareTransferredRuntimeAsync(workflow);

        plcControl.SimulateExternalPlcRunning();
        await WaitUntilAsync(() => workflow.Current.State == RunWorkflowState.Monitoring);

        plcControl.SimulateRuntimeContextLost();

        await WaitUntilAsync(() => workflow.Current.State == RunWorkflowState.Configured
            && lifecycle.ReleaseCommunicationCount == 1
            && eventStream.ReleaseCommunicationCount == 1);
        Assert.Null(workflow.Current.LifecycleObservation);
        Assert.Null(workflow.Current.EventStreamState);
        Assert.Equal(1, eventStream.StopMonitoringCount);
        Assert.Contains(
            Gate(workflow, WorkflowAction.StartUnitTest).Reasons,
            reason => reason.Code == ActionGateReasonCode.UnitTestCommunicationNotPrepared);
    }

    [Fact]
    public async Task Runtime_health_invalidation_during_active_run_invalidates_observation()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        ObserveRunStarted(services);

        plcControl.SimulateRuntimeContextLost();

        Assert.Equal(TestRunMonitoringState.NotMonitoring, workflow.Current.MonitorState);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Invalidated, workflow.Current.CurrentRun.State);
        Assert.Equal(AccessHealthState.Invalidated, workflow.Current.Status.AccessHealth.PlcControl.State);
        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.AccessHealth.PlcControl.Invalidated");
    }

    [Fact]
    public async Task Workflow_publication_status_includes_latest_access_health_snapshot()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);

        project.SimulateProjectContextLost();

        Assert.Equal(AccessHealthState.Invalidated, workflow.Current.Status.AccessHealth.Project.State);
        Assert.Equal(AccessHealthProblem.ContextLost, workflow.Current.Status.AccessHealth.Project.Problem);
        Assert.Equal(AccessHealthState.NotEstablished, workflow.Current.Status.AccessHealth.PlcControl.State);
        Assert.Equal(AccessHealthState.NotEstablished, workflow.Current.Status.AccessHealth.UnitTestLifecycle.State);
        Assert.Equal(AccessHealthState.NotEstablished, workflow.Current.Status.AccessHealth.UnitTestEventStream.State);
    }

    [Fact]
    public async Task Lifecycle_health_invalidation_during_active_run_invalidates_observation_and_blocks_run_commands()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();

        lifecycle.SimulateLifecycleCommunicationInvalidated();

        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Equal(TestRunMonitoringState.Invalidated, workflow.Current.MonitorState);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Invalidated, workflow.Current.CurrentRun.State);
        Assert.Equal(AccessHealthState.Invalidated, workflow.Current.Status.AccessHealth.UnitTestLifecycle.State);
        Assert.Contains(
            Gate(workflow, WorkflowAction.StartUnitTest).Reasons,
            reason => reason.Code == ActionGateReasonCode.UnitTestLifecycleAccessUnhealthy);

        var start = await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);

        Assert.False(start.Result.IsSuccessful);
        Assert.Contains("UnitTest lifecycle access health is not usable.", start.Result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifecycle_revalidation_blocks_commands_without_diagnostic_and_recovers_in_place()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);

        lifecycle.SimulateLifecycleCommunicationRevalidationRequired();

        Assert.Equal(
            AccessHealthState.RevalidationRequired,
            workflow.Current.Status.AccessHealth.UnitTestLifecycle.State);
        Assert.Contains(
            Gate(workflow, WorkflowAction.StartUnitTest).Reasons,
            reason => reason.Code == ActionGateReasonCode.AccessRevalidationRequired);
        Assert.DoesNotContain(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code.Contains("RevalidationRequired", StringComparison.Ordinal));
        Assert.NotEqual(RunWorkflowState.Error, workflow.Current.State);

        lifecycle.SimulateLifecycleCommunicationRecovered();

        Assert.Equal(AccessHealthState.Healthy, workflow.Current.Status.AccessHealth.UnitTestLifecycle.State);
        Assert.True(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
    }

    [Fact]
    public async Task Event_stream_health_invalidation_during_active_run_invalidates_observation_and_blocks_monitoring()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();

        eventStream.SimulateEventStreamCommunicationInvalidated();

        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Equal(TestRunMonitoringState.Invalidated, workflow.Current.MonitorState);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Invalidated, workflow.Current.CurrentRun.State);
        Assert.Equal(AccessHealthState.Invalidated, workflow.Current.Status.AccessHealth.UnitTestEventStream.State);
        Assert.Contains(
            Gate(workflow, WorkflowAction.StopMonitoring).Reasons,
            reason => reason.Code == ActionGateReasonCode.UnitTestEventStreamAccessUnhealthy);
    }

    [Fact]
    public async Task Project_health_invalidation_during_active_run_blocks_project_paths_without_retroactive_run_invalidation()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        ObserveRunStarted(services);

        project.SimulateProjectContextLost();

        Assert.Equal(TestRunMonitoringState.RunActive, workflow.Current.MonitorState);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Running, workflow.Current.CurrentRun.State);
        Assert.Equal(AccessHealthState.Invalidated, workflow.Current.Status.AccessHealth.Project.State);
        Assert.Contains(
            Gate(workflow, WorkflowAction.TransferProject).Reasons,
            reason => reason.Code == ActionGateReasonCode.ProjectAccessUnhealthy);
    }

    [Fact]
    public async Task Profile_health_invalidation_during_active_run_invalidates_observation()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plugin = new FakeAccessProfilePlugin((_, _) => services);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        plugin.Initialize(new AccessProfileContext(
            NullLoggerFactory.Instance,
            timeProvider,
            CreateTempProfileRoot(),
            CreateTempProfileRoot(),
            TestOperationalLogging.Options));
        var request = new AccessProfileSessionRequest(
            config.ProfileId,
            config.ProfileVersion,
            config.ProjectSource,
            ProjectOwnershipMode.ApplicationManaged);
        var load = await plugin.LoadAsync(request, CancellationToken.None);
        var profile = Assert.IsType<FakeAccessProfile>(load.Session);
        var workflow = CreateWorkflow(services, timeProvider);
        await workflow.BindAccessProfileSessionAsync(
            new AccessProfileSession(request, plugin.Info, profile),
            CreatePolicy(),
            CancellationToken.None);
        workflow.Configure(CreateRunSetup(config));
        await PrepareTransferredRuntimeAsync(workflow);
        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        ObserveRunStarted(services);

        profile.SimulateProfileInvalidated();

        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Equal(TestRunMonitoringState.Invalidated, workflow.Current.MonitorState);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Invalidated, workflow.Current.CurrentRun.State);
        Assert.Equal(AccessHealthState.Invalidated, workflow.Current.Status.AccessHealth.Profile.State);
        Assert.Contains(
            Gate(workflow, WorkflowAction.StartUnitTest).Reasons,
            reason => reason.Code == ActionGateReasonCode.AccessProfileUnhealthy);
    }

    [Fact]
    public async Task Published_gate_block_also_blocks_workflow_operation_entry()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        project.DiscoverProjectTargetsResult = OperationResult<ProjectTargetDiscoveryResult>.Failure("Discovery service should not be called.");
        var plugin = new FakeAccessProfilePlugin((_, _) => services);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        plugin.Initialize(new AccessProfileContext(
            NullLoggerFactory.Instance,
            timeProvider,
            CreateTempProfileRoot(),
            CreateTempProfileRoot(),
            TestOperationalLogging.Options));
        var request = new AccessProfileSessionRequest(
            config.ProfileId,
            config.ProfileVersion,
            config.ProjectSource,
            ProjectOwnershipMode.ApplicationManaged);
        var load = await plugin.LoadAsync(request, CancellationToken.None);
        var profile = Assert.IsType<FakeAccessProfile>(load.Session);
        var workflow = CreateWorkflow(services, timeProvider);
        await workflow.BindAccessProfileSessionAsync(
            new AccessProfileSession(request, plugin.Info, profile),
            CreatePolicy(),
            CancellationToken.None);

        profile.SimulateProfileInvalidated();

        var publishedGate = Gate(workflow, WorkflowAction.DiscoverProjectTargets);
        var gateReason = Assert.Single(publishedGate.Reasons);
        var result = await workflow.DiscoverProjectTargetsAsync(config, CreatePolicy(), CancellationToken.None);

        Assert.False(publishedGate.IsAvailable);
        Assert.Equal(ActionGateReasonCode.AccessProfileUnhealthy, gateReason.Code);
        Assert.False(result.Result.IsSuccessful);
        Assert.Equal(gateReason.Message, result.Result.Message);
        Assert.Equal(0, project.DiscoverProjectTargetsCallCount);
        Assert.DoesNotContain("Discovery service should not be called", result.Result.Message, StringComparison.Ordinal);
        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.ActionBlocked.DiscoverProjectTargets.AccessProfileUnhealthy");
    }

    [Fact]
    public async Task Repeated_unchanged_publications_do_not_start_follow_up_workflow_operations()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);
        var prepareCallsBeforePublications = project.PrepareProjectCallCount;
        var discoveryCallsBeforePublications = project.DiscoverProjectTargetsCallCount;
        var gateBeforePublications = Gate(workflow, WorkflowAction.PrepareProject);

        workflow.ClearDiagnostics();
        workflow.ClearDiagnostics();

        var gateAfterPublications = Gate(workflow, WorkflowAction.PrepareProject);
        Assert.Equal(prepareCallsBeforePublications, project.PrepareProjectCallCount);
        Assert.Equal(discoveryCallsBeforePublications, project.DiscoverProjectTargetsCallCount);
        Assert.Equal(gateBeforePublications.Availability, gateAfterPublications.Availability);
        Assert.Equal(
            gateBeforePublications.Reasons.Select(reason => reason.Code),
            gateAfterPublications.Reasons.Select(reason => reason.Code));
    }

    [Fact]
    public async Task Stop_plc_invalidates_prepared_unittest_communication_publication()
    {
        var workflow = CreateWorkflow(FakeAccessProfileFactory.CreatePlugin(FakeTime.Create()));
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig());

        await PrepareTransferredRuntimeAsync(workflow);
        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);

        var stop = await workflow.StopPlcAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(stop.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.PlcStopped, workflow.Current.State);
        Assert.Null(workflow.Current.LifecycleObservation);
        Assert.Null(workflow.Current.EventStreamState);
        Assert.Contains(
            Gate(workflow, WorkflowAction.StartUnitTest).Reasons,
            reason => reason.Code == ActionGateReasonCode.PlcNotRunning);
    }

    [Fact]
    public async Task Fake_profile_can_simulate_project_prepare_failure()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        project.PrepareProjectResult = OperationResult<PlcProjectContext>.Failure("Fake project preparation failed.");
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);

        var result = await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);

        Assert.False(result.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Contains(workflow.Current.Diagnostics, diagnostic => diagnostic.Message.Contains("Fake project preparation failed.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fake_profile_can_simulate_runtime_prepare_failure()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        plcControl.PrepareRuntimeResult = OperationResult<PreparedPlcRuntime>.Failure("Fake runtime preparation failed.");
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);
        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);

        var result = await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);

        Assert.False(result.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Contains(workflow.Current.Diagnostics, diagnostic => diagnostic.Message.Contains("Fake runtime preparation failed.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fake_profile_can_simulate_project_transfer_failure()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        project.TransferResult = OperationResult<PlcTransferResult>.Failure("Fake project transfer failed.");
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);
        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);

        var result = await workflow.TransferProjectAsync(CreatePolicy(), CancellationToken.None);

        Assert.False(result.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Contains(workflow.Current.Diagnostics, diagnostic => diagnostic.Message.Contains("Fake project transfer failed.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Safety_authorization_requirement_is_discovered_for_the_prepared_project_target()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        project.SafetyAuthorizationRequirementResult =
            OperationResult<SafetyProgramAuthorizationRequirementResult>.Success(
                new SafetyProgramAuthorizationRequirementResult(
                    SafetyProgramAuthorizationRequirement.PasswordRequired));
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);
        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);

        var result = await workflow.GetSafetyProgramAuthorizationRequirementAsync(
            CreatePolicy(),
            CancellationToken.None);

        Assert.True(result.Result.IsSuccessful);
        Assert.Equal(
            SafetyProgramAuthorizationRequirement.PasswordRequired,
            result.Result.Value?.Requirement);
        Assert.Equal(RunWorkflowState.PreparingProject, workflow.Current.State);
    }

    [Fact]
    public async Task Safety_authorization_is_retryable_and_precedes_existing_build_and_transfer()
    {
        var calls = new List<string>();
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        project.SafetyAuthorizationRequirementHandler = _ =>
        {
            calls.Add("requirement");
            return Task.FromResult(OperationResult<SafetyProgramAuthorizationRequirementResult>.Success(
                new SafetyProgramAuthorizationRequirementResult(
                    SafetyProgramAuthorizationRequirement.PasswordRequired)));
        };
        var authorizationAttempt = 0;
        project.SafetyAuthorizationHandler = (_, _) =>
        {
            calls.Add("authorize");
            authorizationAttempt++;
            return Task.FromResult(
                authorizationAttempt == 1
                    ? OperationResult<SafetyProgramAuthorizationResult>.Success(
                        new SafetyProgramAuthorizationResult(false))
                    : OperationResult<SafetyProgramAuthorizationResult>.Success(
                        new SafetyProgramAuthorizationResult(true)));
        };
        project.BuildHandler = _ =>
        {
            calls.Add("build");
            return Task.FromResult(OperationResult<PlcBuildResult>.Success(
                new PlcBuildResult(true, false, Array.Empty<Diagnostic>())));
        };
        project.TransferHandler = _ =>
        {
            calls.Add("transfer");
            return Task.FromResult(OperationResult<PlcTransferResult>.Success(
                new PlcTransferResult(
                    project.CurrentProject!,
                    project.DefaultProjectTarget,
                    Assert.IsType<FakePlcControlService>(services.PlcControl).DefaultRuntimeTarget,
                    DateTimeOffset.UtcNow)));
        };
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);
        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);

        var requirement = await workflow.GetSafetyProgramAuthorizationRequirementAsync(
            CreatePolicy(),
            CancellationToken.None);
        using var password = CreateSecureString("safety-secret");
        var rejected = await workflow.AuthorizeSafetyProgramAsync(
            password,
            CreatePolicy(),
            CancellationToken.None);
        var stateAfterRejectedAuthorization = workflow.Current.State;
        var authorized = await workflow.AuthorizeSafetyProgramAsync(
            password,
            CreatePolicy(),
            CancellationToken.None);
        var transfer = await workflow.TransferProjectAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(requirement.Result.IsSuccessful);
        Assert.False(rejected.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.RuntimePrepared, stateAfterRejectedAuthorization);
        Assert.True(authorized.Result.IsSuccessful);
        Assert.True(authorized.Result.Value?.Authorized);
        Assert.True(transfer.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.ProjectTransferred, workflow.Current.State);
        Assert.Equal(["requirement", "authorize", "authorize", "build", "transfer"], calls);
    }

    [Fact]
    public async Task Fake_profile_can_simulate_plc_start_failure()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        plcControl.StartResult = OperationResult<PlcState>.Failure("Fake PLC start failed.");
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);
        await PrepareTransferredRuntimeAsync(workflow);

        var result = await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);

        Assert.False(result.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Contains(workflow.Current.Diagnostics, diagnostic => diagnostic.Message.Contains("Fake PLC start failed.", StringComparison.Ordinal));
        var completion = Assert.Single(workflow.EvidenceAnalysis.CurrentTrace!.Entries.Where(entry =>
            entry.Payload is CommandActivityPayload { Command: UnitTestActivityCommandKind.PlcStart,
                Phase: UnitTestActivityCommandPhase.Completed }));
        Assert.False(((CommandActivityPayload)completion.Payload).Accepted);
        var analysis = workflow.EvidenceAnalysis.AnalyzeLifecycle(completion.LifecycleObservationId!.Value);
        Assert.Contains(analysis.Incidents, incident => incident.Classification == EvidenceIncidentClassification.CommandTransportFailure);
    }

    [Fact]
    public async Task Plc_start_command_result_does_not_masquerade_as_observed_running_state()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        plcControl.StartResult = OperationResult<PlcState>.Success(new PlcState(
            RuntimeInstanceState.Available,
            PlcOperatingState.Running,
            [plcControl.DefaultRuntimeTarget],
            ReadyForTransfer: true,
            ReadyForExecution: true));
        var workflow = CreateWorkflow(services, timeProvider);
        await LoadAndConfigureAsync(
            workflow,
            FakeAccessProfileFactory.CreateDefaultConfig(),
            services,
            timeProvider);
        await PrepareTransferredRuntimeAsync(workflow);

        var result = await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(result.Result.IsSuccessful);
        Assert.False(workflow.Current.Status.PlcRunning);
        Assert.NotEqual(PlcOperatingState.Running, workflow.Current.Status.PlcState.OperatingState);

        timeProvider.Advance(TimeSpan.FromDays(1));

        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.PlcCommand.StateObservationTimeout");
        Assert.False(workflow.Current.Status.UnitTestCommunicationPrepared);
    }

    [Fact]
    public async Task Fake_profile_can_simulate_plc_stop_failure()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        plcControl.StopResult = OperationResult<PlcState>.Failure("Fake PLC stop failed.");
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);
        await PrepareTransferredRuntimeAsync(workflow);
        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);

        var result = await workflow.StopPlcAsync(CreatePolicy(), CancellationToken.None);

        Assert.False(result.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Contains(workflow.Current.Diagnostics, diagnostic => diagnostic.Message.Contains("Fake PLC stop failed.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plc_stop_command_result_does_not_masquerade_as_observed_stopped_state()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        var workflow = CreateWorkflow(services, timeProvider);
        await LoadAndConfigureAsync(
            workflow,
            FakeAccessProfileFactory.CreateDefaultConfig(),
            services,
            timeProvider);
        await PrepareTransferredRuntimeAsync(workflow);
        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);
        plcControl.StopResult = OperationResult<PlcState>.Success(new PlcState(
            RuntimeInstanceState.Available,
            PlcOperatingState.Stopped,
            [plcControl.DefaultRuntimeTarget],
            ReadyForTransfer: true,
            ReadyForExecution: false));

        var result = await workflow.StopPlcAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(result.Result.IsSuccessful);
        Assert.True(workflow.Current.Status.PlcRunning);
        Assert.False(workflow.Current.Status.UnitTestCommunicationPrepared);

        timeProvider.Advance(TimeSpan.FromDays(1));

        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.PlcCommand.StateObservationTimeout");
    }

    [Fact]
    public async Task UnitTest_start_abort_reset_go_through_workflow_service()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);

        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);
        await workflow.TransferProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);

        var start = await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();
        Assert.True(start.Result.IsSuccessful);
        Assert.True(start.Result.Value?.Accepted);
        Assert.Equal(RunWorkflowState.RunActive, workflow.Current.State);
        Assert.Equal(TestRunMonitoringState.RunActive, workflow.Current.MonitorState);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Running, workflow.Current.CurrentRun.State);

        var abort = await workflow.AbortUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalAbortStarted();
        Assert.True(abort.Result.IsSuccessful);
        Assert.True(abort.Result.Value?.Accepted);
        Assert.Equal(RunWorkflowState.RunActive, workflow.Current.State);

        var reset = await workflow.ResetUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.CompleteReset();
        Assert.True(reset.Result.IsSuccessful);
        Assert.True(reset.Result.Value?.Accepted);
        Assert.Equal(RunWorkflowState.Monitoring, workflow.Current.State);
    }

    [Fact]
    public async Task Lifecycle_completion_and_empty_event_buffer_publish_run_completed()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();

        lifecycle.CompleteRun(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompletionAnchor();

        Assert.Equal(TestRunMonitoringState.Completed, workflow.Current.MonitorState);
        Assert.Equal(RunWorkflowState.RunCompleted, workflow.Current.State);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Completed, workflow.Current.CurrentRun.State);
        Assert.Equal(EvidenceState.Incomplete, workflow.Current.CurrentRun.EvidenceState);
        Assert.Equal(new uint[] { 1, 2 }, workflow.Current.CurrentRun.EventStream.Select(item => item.SequenceId));
    }

    [Theory]
    [InlineData(UnitTestResultState.Failed)]
    [InlineData(UnitTestResultState.Error)]
    public async Task Failed_and_error_sample_evidence_publish_terminal_current_run_result(UnitTestResultState result)
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();

        lifecycle.CompleteRun(result);
        PublishCompletionBatchWithResult(eventStream, result);

        Assert.Equal(TestRunMonitoringState.Completed, workflow.Current.MonitorState);
        Assert.Equal(RunWorkflowState.RunCompleted, workflow.Current.State);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Completed, workflow.Current.CurrentRun.State);
        Assert.Equal(result, workflow.Current.CurrentRun.Result);
        Assert.Equal(EvidenceState.Complete, workflow.Current.CurrentRun.EvidenceState);
    }

    [Fact]
    public async Task Abort_sample_evidence_publishes_aborted_current_run_without_run_completed_state()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();
        await workflow.AbortUnitTestAsync(CreatePolicy(), CancellationToken.None);

        lifecycle.CompleteAbort();
        eventStream.PublishRunControlAbortCompleted();

        Assert.Equal(TestRunMonitoringState.Aborted, workflow.Current.MonitorState);
        Assert.Equal(RunWorkflowState.Monitoring, workflow.Current.State);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Aborted, workflow.Current.CurrentRun.State);
        Assert.Equal(UnitTestResultState.Abort, workflow.Current.CurrentRun.Result);
        Assert.Equal(EvidenceState.Complete, workflow.Current.CurrentRun.EvidenceState);
    }

    [Fact]
    public async Task Externally_observed_run_start_opens_epoch_without_start_command()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);

        lifecycle.SimulateExternalRunStarted();

        Assert.Equal(TestRunMonitoringState.RunActive, workflow.Current.MonitorState);
        Assert.Equal(RunWorkflowState.RunActive, workflow.Current.State);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Running, workflow.Current.CurrentRun.State);
        Assert.Equal(new uint[] { 1 }, workflow.Current.CurrentRun.EventStream.Select(item => item.SequenceId));
        Assert.False(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
        Assert.True(Gate(workflow, WorkflowAction.AbortUnitTest).IsAvailable);
    }

    [Fact]
    public async Task Externally_observed_run_completion_publishes_completed_epoch_without_start_command()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);

        lifecycle.SimulateExternalRunStarted();
        lifecycle.SimulateExternalRunCompleted(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompleted(UnitTestResultState.Passed);

        Assert.Equal(TestRunMonitoringState.Completed, workflow.Current.MonitorState);
        Assert.Equal(RunWorkflowState.RunCompleted, workflow.Current.State);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Completed, workflow.Current.CurrentRun.State);
        Assert.Equal(EvidenceState.Complete, workflow.Current.CurrentRun.EvidenceState);
        Assert.Equal(UnitTestResultState.Passed, workflow.Current.CurrentRun.Result);
        Assert.Equal(new uint[] { 1, 2, 3 }, workflow.Current.CurrentRun.EventStream.Select(item => item.SequenceId));
    }

    [Fact]
    public async Task Externally_observed_abort_publishes_aborted_epoch_without_abort_command()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);

        lifecycle.SimulateExternalRunStarted();
        lifecycle.SimulateExternalAbortStarted();
        lifecycle.SimulateExternalAbortCompleted();
        eventStream.PublishRunControlAbortCompleted();

        Assert.Equal(TestRunMonitoringState.Aborted, workflow.Current.MonitorState);
        Assert.Equal(RunWorkflowState.Monitoring, workflow.Current.State);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Aborted, workflow.Current.CurrentRun.State);
        Assert.Equal(UnitTestResultState.Abort, workflow.Current.CurrentRun.Result);
        Assert.Equal(EvidenceState.Complete, workflow.Current.CurrentRun.EvidenceState);
    }

    [Fact]
    public async Task Second_externally_observed_run_after_reset_keeps_event_stream_processor_observing()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);

        lifecycle.SimulateExternalRunStarted();
        lifecycle.SimulateExternalResetStarted();
        eventStream.PublishRunControlResetObserved();
        lifecycle.SimulateExternalResetCompleted();

        Assert.Equal(TestRunMonitoringState.WaitingForRun, workflow.Current.MonitorState);
        Assert.Equal(RunWorkflowState.Monitoring, workflow.Current.State);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Invalidated, workflow.Current.CurrentRun.State);
        Assert.Equal(EvidenceState.Incomplete, workflow.Current.CurrentRun.EvidenceState);
        Assert.True(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);

        lifecycle.SimulateExternalRunStarted();
        lifecycle.SimulateExternalRunCompleted(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompleted(UnitTestResultState.Passed);

        Assert.Equal(TestRunMonitoringState.Completed, workflow.Current.MonitorState);
        Assert.Equal(RunWorkflowState.RunCompleted, workflow.Current.State);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Completed, workflow.Current.CurrentRun.State);
        Assert.Equal(UnitTestResultState.Passed, workflow.Current.CurrentRun.Result);
        Assert.Equal(new uint[] { 3, 4, 5 }, workflow.Current.CurrentRun.EventStream.Select(item => item.SequenceId));
        Assert.DoesNotContain(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.NotObserving");
    }

    [Fact]
    public async Task Event_stream_batch_updates_current_run_publication_before_terminal_state()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();

        eventStream.PublishBatchWithoutCompletionAnchor();

        Assert.Equal(RunWorkflowState.RunActive, workflow.Current.State);
        Assert.Equal(TestRunMonitoringState.RunActive, workflow.Current.MonitorState);
        Assert.NotNull(workflow.Current.CurrentRun);
        Assert.Equal(UnitTestRunState.Running, workflow.Current.CurrentRun.State);
        Assert.Equal(EvidenceState.Partial, workflow.Current.CurrentRun.EvidenceState);
        Assert.Equal(new uint[] { 1, 2 }, workflow.Current.CurrentRun.EventStream.Select(item => item.SequenceId));
    }

    [Fact]
    public async Task Lifecycle_completion_without_event_stream_anchor_does_not_publish_run_completed()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();

        lifecycle.CompleteRun(UnitTestResultState.Passed);
        eventStream.PublishBatch(new UnitTestEventBatch(
            eventStream.CurrentMonitoringState.SessionId,
            Array.Empty<UnitTestEventEntry>(),
            ProducerSequenceId: eventStream.CurrentMonitoringState.ProducerSequenceId,
            DeletedSequenceId: eventStream.CurrentMonitoringState.DeletedSequenceId,
            LostEventCount: 0,
            BufferObservedEmpty: true,
            EventBufferReadState.Empty));

        Assert.Equal(TestRunMonitoringState.RunActive, workflow.Current.MonitorState);
        Assert.NotEqual(RunWorkflowState.RunCompleted, workflow.Current.State);
    }

    [Fact]
    public async Task Missing_final_event_sample_evidence_times_out_without_run_completed_publication()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services, timeProvider);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(workflow, config, services, timeProvider);
        workflow.Configure(CreateRunSetup(config, plcStateChangeTimeout: TimeSpan.FromSeconds(1)));
        await PrepareTransferredRuntimeAsync(workflow);
        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();

        lifecycle.CompleteRun(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompletionAnchor(bufferObservedEmpty: false);
        timeProvider.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(TestRunMonitoringState.TimedOut, workflow.Current.MonitorState);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.NotEqual(UnitTestRunState.Completed, workflow.Current.CurrentRun?.State);
        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.ObservationTimeout"
                && diagnostic.Message.Contains("Final event-stream evidence observation", StringComparison.Ordinal));
        Assert.DoesNotContain(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code.StartsWith("Workflow.UnitTestCommand.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reset_after_completed_run_returns_workflow_to_startable_monitoring_state()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();
        lifecycle.CompleteRun(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompletionAnchor();
        Assert.Equal(RunWorkflowState.RunCompleted, workflow.Current.State);
        Assert.Equal(TestRunMonitoringState.Completed, workflow.Current.MonitorState);
        Assert.Equal(UnitTestRunState.Completed, workflow.Current.CurrentRun?.State);

        var reset = await workflow.ResetUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.CompleteReset();

        Assert.True(reset.Result.IsSuccessful);
        Assert.True(reset.Result.Value?.Accepted);
        Assert.Equal(UnitTestLifecycleState.Idle, workflow.Current.LifecycleObservation?.State);
        Assert.Equal(TestRunMonitoringState.WaitingForRun, workflow.Current.MonitorState);
        Assert.Equal(RunWorkflowState.Monitoring, workflow.Current.State);
        Assert.Null(workflow.Current.CurrentRun);
        Assert.True(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
    }

    [Fact]
    public async Task Second_run_after_reset_keeps_event_stream_processor_observing()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);

        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();
        lifecycle.CompleteRun(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompletionAnchor();
        await workflow.ResetUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.CompleteReset();

        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();
        lifecycle.CompleteRun(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompletionAnchor();

        Assert.Equal(RunWorkflowState.RunCompleted, workflow.Current.State);
        Assert.Equal(TestRunMonitoringState.Completed, workflow.Current.MonitorState);
        Assert.Equal(UnitTestRunState.Completed, workflow.Current.CurrentRun?.State);
        Assert.Equal(new uint[] { 3, 4 }, workflow.Current.CurrentRun?.EventStream.Select(item => item.SequenceId));
        Assert.DoesNotContain(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.NotObserving");
    }

    [Fact]
    public async Task Stale_event_sample_evidence_is_diagnosed_without_rewriting_current_run_stream()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();

        eventStream.PublishBatchWithoutCompletionAnchor();
        eventStream.PublishOldEvent(1);

        Assert.Equal(RunWorkflowState.RunActive, workflow.Current.State);
        Assert.Equal(new uint[] { 1, 2 }, workflow.Current.CurrentRun?.EventStream.Select(item => item.SequenceId));
        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.StaleEvent");
    }

    [Fact]
    public async Task Reset_that_has_not_reached_ready_keeps_start_blocked()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();

        var reset = await workflow.ResetUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalResetStarted();

        Assert.True(reset.Result.IsSuccessful);
        Assert.True(reset.Result.Value?.Accepted);
        Assert.Equal(UnitTestLifecycleState.Reset, workflow.Current.LifecycleObservation?.State);
        Assert.False(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
    }

    [Fact]
    public async Task UnitTest_command_gates_follow_lifecycle_observation_not_monitor_state()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);
        await PrepareTransferredRuntimeAsync(workflow);
        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
        Assert.False(Gate(workflow, WorkflowAction.AbortUnitTest).IsAvailable);

        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();

        Assert.False(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
        Assert.True(Gate(workflow, WorkflowAction.AbortUnitTest).IsAvailable);

        await workflow.AbortUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalAbortStarted();

        Assert.Equal(UnitTestLifecycleState.Aborting, lifecycle.CurrentObservation.State);
        Assert.False(Gate(workflow, WorkflowAction.AbortUnitTest).IsAvailable);

        lifecycle.CompleteAbort();

        Assert.False(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);

        await workflow.ResetUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.CompleteReset();

        Assert.True(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
    }

    [Fact]
    public async Task Reset_that_never_reaches_ready_times_out_and_keeps_start_blocked()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var workflow = CreateWorkflow(services, timeProvider);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(workflow, config, services, timeProvider);
        workflow.Configure(CreateRunSetup(config, plcStateChangeTimeout: TimeSpan.FromSeconds(5)));
        await PrepareTransferredRuntimeAsync(workflow);
        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        ObserveRunStarted(services);

        var reset = await workflow.ResetUnitTestAsync(CreatePolicy(), CancellationToken.None);
        Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle)
            .SimulateExternalResetStarted();
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        Assert.True(reset.Result.IsSuccessful);
        Assert.True(reset.Result.Value?.Accepted);
        Assert.Equal(TestRunMonitoringState.WaitingForRun, workflow.Current.MonitorState);
        Assert.Equal(RunWorkflowState.Monitoring, workflow.Current.State);
        Assert.False(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.CommandReactionTimeout"
                && diagnostic.Message.Contains("ResetReady", StringComparison.Ordinal));
        Assert.DoesNotContain(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code.StartsWith("Workflow.UnitTestCommand.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rejected_start_command_adds_workflow_diagnostic()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);

        lifecycle.RejectNextCommand = true;

        var start = await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(start.Result.IsSuccessful);
        Assert.False(start.Result.Value?.Accepted);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.UnitTestCommand.StartUnitTest.Rejected");
    }

    [Fact]
    public async Task Failed_abort_command_adds_workflow_diagnostic()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();
        lifecycle.NextCommandResult = OperationResult<TestRunCommandResult>.Failure("Abort command failed.");

        var abort = await workflow.AbortUnitTestAsync(CreatePolicy(), CancellationToken.None);

        Assert.False(abort.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.UnitTestCommand.AbortUnitTest.ServiceFailed"
                && diagnostic.Message == "Abort command failed.");
    }

    [Fact]
    public async Task Unexpected_reset_command_result_adds_workflow_diagnostic()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();
        lifecycle.NextCommandResult = OperationResult<TestRunCommandResult>.Success(
            new TestRunCommandResult(UnitTestRunCommand.Start, Accepted: true));

        var reset = await workflow.ResetUnitTestAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(reset.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.UnitTestCommand.ResetUnitTest.UnexpectedCommand");
    }

    [Fact]
    public async Task Held_start_command_waits_for_lifecycle_service_result_without_shared_timeout()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var workflow = CreateWorkflow(services, timeProvider);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(workflow, config, services, timeProvider);
        workflow.Configure(CreateRunSetup(config, commandTimeout: TimeSpan.FromSeconds(5)));
        await PrepareTransferredRuntimeAsync(workflow);
        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.HoldNextCommand = true;

        var startTask = workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        await Task.Yield();
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        Assert.False(startTask.IsCompleted);
        Assert.Equal(UnitTestRunCommand.Start, lifecycle.PendingCommand);
        Assert.Equal(RunWorkflowState.Monitoring, workflow.Current.State);
        Assert.Equal(WorkflowAction.StartUnitTest, workflow.Current.ActiveOperation?.Action);
        Assert.DoesNotContain(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.UnitTestCommand.StartUnitTest.Timeout");

        lifecycle.CompletePendingCommandAccepted();
        var start = await startTask;
        lifecycle.SimulateExternalRunStarted();

        Assert.True(start.Result.IsSuccessful);
        Assert.True(start.Result.Value?.Accepted);
        Assert.Equal(RunWorkflowState.RunActive, workflow.Current.State);
    }

    [Theory]
    [InlineData(WorkflowAction.StartUnitTest, UnitTestRunCommand.Start)]
    [InlineData(WorkflowAction.AbortUnitTest, UnitTestRunCommand.Abort)]
    [InlineData(WorkflowAction.ResetUnitTest, UnitTestRunCommand.Reset)]
    public async Task Lifecycle_service_technical_timeout_is_failed_and_not_monitor_observation_timeout(
        WorkflowAction action,
        UnitTestRunCommand command)
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var workflow = CreateWorkflow(services, timeProvider);
        await PrepareMonitoringAsync(workflow, services, timeProvider);

        if (action == WorkflowAction.AbortUnitTest)
        {
            await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
            lifecycle.SimulateExternalRunStarted();
        }

        lifecycle.NextCommandResult = OperationResult<TestRunCommandResult>.TimedOut(
            $"Fake UnitTest lifecycle command '{command}' acknowledgement timed out.");
        var result = action switch
        {
            WorkflowAction.StartUnitTest => await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None),
            WorkflowAction.AbortUnitTest => await workflow.AbortUnitTestAsync(CreatePolicy(), CancellationToken.None),
            WorkflowAction.ResetUnitTest => await workflow.ResetUnitTestAsync(CreatePolicy(), CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };

        Assert.Equal(OperationResultState.Failed, result.Result.State);
        Assert.Null(result.Result.Value);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Null(lifecycle.PendingCommand);
        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == $"Workflow.AccessProfile.UnitTestLifecycle.{action}.TechnicalTimeout"
                && diagnostic.Message.Contains("acknowledgement timed out", StringComparison.Ordinal));
        Assert.DoesNotContain(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == $"Workflow.UnitTestCommand.{action}.ServiceFailed");
        Assert.DoesNotContain(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.ObservationTimeout");
    }

    [Fact]
    public async Task Cancelling_pending_plc_start_clears_active_operation_without_error_state()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        plcControl.HoldNextStart = true;
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);
        await PrepareTransferredRuntimeAsync(workflow);
        using var cancellation = new CancellationTokenSource();

        var start = workflow.StartPlcAsync(CreatePolicy(), cancellation.Token);
        await WaitUntilAsync(() => plcControl.IsStartPending
            && workflow.Current.ActiveOperation?.Action == WorkflowAction.StartPlc);

        var pendingRequest = Assert.Single(workflow.EvidenceAnalysis.CurrentTrace!.Entries.Where(entry =>
            entry.Payload is CommandActivityPayload { Command: UnitTestActivityCommandKind.PlcStart,
                Phase: UnitTestActivityCommandPhase.Requested }));
        Assert.NotNull(pendingRequest.LifecycleObservationId);
        Assert.DoesNotContain(workflow.EvidenceAnalysis.CurrentTrace.Entries, entry =>
            entry.LifecycleObservationId == pendingRequest.LifecycleObservationId
            && entry.Payload is CommandActivityPayload { Phase: UnitTestActivityCommandPhase.Completed });

        cancellation.Cancel();
        var result = await start;

        Assert.Equal(OperationResultState.Cancelled, result.Result.State);
        Assert.False(plcControl.IsStartPending);
        Assert.Contains(workflow.EvidenceAnalysis.CurrentTrace!.Entries, entry =>
            entry.LifecycleObservationId == pendingRequest.LifecycleObservationId
            && entry.Payload is CommandActivityPayload { Phase: UnitTestActivityCommandPhase.Completed,
                ResultState: OperationResultState.Cancelled });
        Assert.Null(workflow.Current.ActiveOperation);
        Assert.Equal(RunWorkflowState.ProjectTransferred, workflow.Current.State);
        Assert.True(Gate(workflow, WorkflowAction.StartPlc).IsAvailable);
        Assert.DoesNotContain(
            Gate(workflow, WorkflowAction.StartPlc).Reasons,
            reason => reason.Code == ActionGateReasonCode.OperationInProgress);
    }

    [Fact]
    public async Task Cancelling_pending_unittest_start_clears_lifecycle_expectation_without_error_state()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var workflow = CreateWorkflow(services);
        await PrepareMonitoringAsync(workflow, services);
        lifecycle.HoldNextCommand = true;
        using var cancellation = new CancellationTokenSource();

        var start = workflow.StartUnitTestAsync(CreatePolicy(), cancellation.Token);
        await WaitUntilAsync(() => lifecycle.PendingCommand == UnitTestRunCommand.Start
            && workflow.Current.ActiveOperation?.Action == WorkflowAction.StartUnitTest);

        cancellation.Cancel();
        var result = await start;

        Assert.Equal(OperationResultState.Cancelled, result.Result.State);
        Assert.Null(lifecycle.PendingCommand);
        Assert.Null(workflow.Current.ActiveOperation);
        Assert.Equal(RunWorkflowState.Monitoring, workflow.Current.State);
        Assert.Equal(TestRunMonitoringState.WaitingForRun, workflow.Current.MonitorState);
        Assert.True(Gate(workflow, WorkflowAction.StartUnitTest).IsAvailable);
        Assert.DoesNotContain(
            Gate(workflow, WorkflowAction.StartUnitTest).Reasons,
            reason => reason.Code == ActionGateReasonCode.OperationInProgress);
    }

    [Fact]
    public async Task Pending_plc_start_exceeding_shared_deadline_returns_timed_out()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        plcControl.HoldNextStart = true;
        var workflow = CreateWorkflow(services, timeProvider);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await LoadAndConfigureAsync(workflow, config, services, timeProvider);
        await PrepareTransferredRuntimeAsync(workflow);

        var start = workflow.StartPlcAsync(
            new WorkflowActionPolicy(TimeSpan.FromSeconds(5)),
            CancellationToken.None);
        await WaitUntilAsync(() => plcControl.IsStartPending);

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var result = await start;

        Assert.Equal(OperationResultState.TimedOut, result.Result.State);
        Assert.False(plcControl.IsStartPending);
        Assert.Null(workflow.Current.ActiveOperation);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.StartPlc.TimedOut");
    }

    [Fact]
    public async Task Pending_unittest_command_exceeding_shared_deadline_returns_timed_out()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var workflow = CreateWorkflow(services, timeProvider);
        await PrepareMonitoringAsync(workflow, services, timeProvider);
        lifecycle.HoldNextCommand = true;

        var start = workflow.StartUnitTestAsync(
            new WorkflowActionPolicy(TimeSpan.FromSeconds(3)),
            CancellationToken.None);
        await WaitUntilAsync(() => lifecycle.PendingCommand == UnitTestRunCommand.Start);

        timeProvider.Advance(TimeSpan.FromSeconds(3));
        var result = await start;

        Assert.Equal(OperationResultState.TimedOut, result.Result.State);
        Assert.Null(lifecycle.PendingCommand);
        Assert.Null(workflow.Current.ActiveOperation);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Equal(TestRunMonitoringState.WaitingForRun, workflow.Current.MonitorState);
        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.StartUnitTest.TimedOut");
    }

    [Fact]
    public async Task Build_and_transfer_share_one_token_but_post_transfer_release_uses_its_own_deadline()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var project = new RecordingProjectService(services.Project);
        services = services with { Project = project };
        var workflow = CreateWorkflow(services, timeProvider);
        await LoadAndConfigureAsync(
            workflow,
            FakeAccessProfileFactory.CreateDefaultConfig(),
            services,
            timeProvider);
        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);

        var transfer = await workflow.TransferProjectAsync(
            new WorkflowActionPolicy(TimeSpan.FromMinutes(10)),
            new WorkflowActionPolicy(TimeSpan.FromSeconds(30)),
            CancellationToken.None);

        Assert.True(transfer.Result.IsSuccessful);
        Assert.True(project.BuildToken.CanBeCanceled);
        Assert.True(project.ReleaseToken.CanBeCanceled);
        Assert.Equal(project.BuildToken, project.TransferToken);
        Assert.NotEqual(project.BuildToken, project.ReleaseToken);
    }

    [Fact]
    public async Task Build_and_transfer_share_one_total_deadline_budget()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var project = new RecordingProjectService(services.Project);
        services = services with { Project = project };
        var transferStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        project.BuildHandler = cancellationToken =>
        {
            timeProvider.Advance(TimeSpan.FromSeconds(4));
            return Task.FromResult(OperationResult<PlcBuildResult>.Success(
                new PlcBuildResult(true, false, Array.Empty<Diagnostic>())));
        };
        project.TransferHandler = async cancellationToken =>
        {
            transferStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, cancellationToken);
            throw new InvalidOperationException("The transfer wait must end through the shared deadline.");
        };
        var workflow = CreateWorkflow(services, timeProvider);
        await LoadAndConfigureAsync(
            workflow,
            FakeAccessProfileFactory.CreateDefaultConfig(),
            services,
            timeProvider);
        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);

        var transfer = workflow.TransferProjectAsync(
            new WorkflowActionPolicy(TimeSpan.FromSeconds(5)),
            new WorkflowActionPolicy(TimeSpan.FromSeconds(30)),
            CancellationToken.None);
        await transferStarted.Task;

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var result = await transfer;

        Assert.Equal(OperationResultState.TimedOut, result.Result.State);
        Assert.Equal(project.BuildToken, project.TransferToken);
        Assert.Null(workflow.Current.ActiveOperation);
        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.TransferProject.TimedOut");
    }

    [Fact]
    public async Task Cli_style_complete_setup_flow_can_run_without_interactive_discovery()
    {
        var workflow = CreateWorkflow(FakeAccessProfileFactory.CreatePlugin(FakeTime.Create()));
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        var policy = CreatePolicy();

        var load = await RunWorkflowProfileTestHelper.LoadAndBindDefaultFakeAsync(workflow, config);
        var configure = workflow.Configure(CreateRunSetup(config));
        var prepareProject = await workflow.PrepareProjectAsync(policy, CancellationToken.None);
        var prepareRuntime = await workflow.PrepareRuntimeAsync(policy, CancellationToken.None);
        var transfer = await workflow.TransferProjectAsync(policy, CancellationToken.None);
        var startPlc = await workflow.StartPlcAsync(policy, CancellationToken.None);
        var lifecycleObservation = workflow.Current.LifecycleObservation;
        var monitoringState = workflow.Current.MonitorState;
        var startUnitTest = await workflow.StartUnitTestAsync(policy, CancellationToken.None);
        var cleanup = await workflow.CleanupAsync(policy, CancellationToken.None);

        Assert.True(load.Result.IsSuccessful);
        Assert.True(configure.IsSuccessful);
        Assert.True(prepareProject.Result.IsSuccessful);
        Assert.True(prepareRuntime.Result.IsSuccessful);
        Assert.True(transfer.Result.IsSuccessful);
        Assert.True(startPlc.Result.IsSuccessful);
        Assert.NotNull(lifecycleObservation);
        Assert.Equal(TestRunMonitoringState.WaitingForRun, monitoringState);
        Assert.True(startUnitTest.Result.IsSuccessful);
        Assert.True(cleanup.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.NotConfigured, workflow.Current.State);
    }

    [Fact]
    public async Task Scoped_release_actions_follow_prepared_context_order()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var workflow = CreateWorkflow(services);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);
        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);
        await workflow.TransferProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);

        var blockedRuntimeRelease = await workflow.ReleaseRuntimeAsync(CreatePolicy(), CancellationToken.None);
        Assert.False(blockedRuntimeRelease.Result.IsSuccessful);
        Assert.Contains(
            Gate(workflow, WorkflowAction.ReleaseRuntime).Reasons,
            reason => reason.Code == ActionGateReasonCode.UnitTestCommunicationStillPrepared);

        var communicationRelease = await workflow.ReleaseUnitTestCommunicationAsync(CreatePolicy(), CancellationToken.None);
        Assert.True(communicationRelease.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.ProjectTransferred, workflow.Current.State);
        Assert.Contains(
            Gate(workflow, WorkflowAction.StartUnitTest).Reasons,
            reason => reason.Code == ActionGateReasonCode.UnitTestCommunicationNotPrepared);

        var runtimeRelease = await workflow.ReleaseRuntimeAsync(CreatePolicy(), CancellationToken.None);
        Assert.True(runtimeRelease.Result.IsSuccessful);
        Assert.Equal(
            RuntimeResourceReleaseMode.KeepResource,
            Assert.IsType<FakePlcControlService>(services.PlcControl).LastReleaseRuntimeRequest?.ReleaseMode);

        var projectRelease = await workflow.ReleaseProjectAsync(CreatePolicy(), CancellationToken.None);
        Assert.True(projectRelease.Result.IsSuccessful);

        var profileUnload = await workflow.UnloadAccessProfileAsync(CreatePolicy(), CancellationToken.None);
        Assert.True(profileUnload.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.NotConfigured, workflow.Current.State);
    }

    [Fact]
    public async Task Cleanup_requests_owned_runtime_destruction()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var workflow = CreateWorkflow(services);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(
            workflow,
            config,
            services,
            FakeTime.Create());
        workflow.Configure(CreateRunSetup(config) with
        {
            RuntimeProvisioningMode = RuntimeProvisioningMode.CreateOwned,
            RuntimeTarget = null
        });
        await PrepareTransferredRuntimeAsync(workflow);

        var cleanup = await workflow.CleanupAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(cleanup.Result.IsSuccessful);
        Assert.Equal(
            RuntimeResourceReleaseMode.DestroyResource,
            Assert.IsType<FakePlcControlService>(services.PlcControl).LastReleaseRuntimeRequest?.ReleaseMode);
    }

    [Fact]
    public async Task Cleanup_after_owned_runtime_reconnect_still_requests_destruction()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var workflow = CreateWorkflow(services);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(
            workflow,
            config,
            services,
            FakeTime.Create());
        workflow.Configure(CreateRunSetup(config) with
        {
            RuntimeProvisioningMode = RuntimeProvisioningMode.CreateOwned,
            RuntimeTarget = null
        });
        await PrepareTransferredRuntimeAsync(workflow);

        var reconnect = await workflow.ReconnectRuntimeAsync(CreatePolicy(), CancellationToken.None);
        var cleanup = await workflow.CleanupAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(reconnect.Result.IsSuccessful);
        Assert.True(cleanup.Result.IsSuccessful);
        Assert.Equal(
            RuntimeResourceReleaseMode.DestroyResource,
            Assert.IsType<FakePlcControlService>(services.PlcControl).LastReleaseRuntimeRequest?.ReleaseMode);
    }

    [Fact]
    public async Task Require_existing_runtime_release_cannot_destroy_external_resource()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var workflow = CreateWorkflow(services);
        var config = FakeAccessProfileFactory.CreateDefaultConfig() with
        {
            Values = new AccessProfileConfigurationPayload("""{ "cleanup": { "runtime": "release" } }""")
        };
        await LoadAndConfigureAsync(workflow, config, services);
        await PrepareTransferredRuntimeAsync(workflow);

        var release = await workflow.ReleaseRuntimeAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(release.Result.IsSuccessful);
        Assert.Equal(
            RuntimeResourceReleaseMode.KeepResource,
            Assert.IsType<FakePlcControlService>(services.PlcControl).LastReleaseRuntimeRequest?.ReleaseMode);
    }

    [Fact]
    public async Task Unsupported_owned_runtime_destruction_can_be_retried()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        plcControl.SupportsRuntimeDestruction = false;
        var workflow = CreateWorkflow(services);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(
            workflow,
            config,
            services,
            FakeTime.Create());
        workflow.Configure(CreateRunSetup(config) with
        {
            RuntimeProvisioningMode = RuntimeProvisioningMode.CreateOwned,
            RuntimeTarget = null
        });
        await PrepareTransferredRuntimeAsync(workflow);

        var release = await workflow.ReleaseRuntimeAsync(CreatePolicy(), CancellationToken.None);
        plcControl.SupportsRuntimeDestruction = true;
        var retry = await workflow.ReleaseRuntimeAsync(CreatePolicy(), CancellationToken.None);

        Assert.False(release.Result.IsSuccessful);
        Assert.Equal(RuntimeResourceReleaseMode.DestroyResource, plcControl.LastReleaseRuntimeRequest?.ReleaseMode);
        Assert.True(retry.Result.IsSuccessful);
        Assert.False(workflow.Current.Status.RuntimePrepared);
    }

    [Fact]
    public async Task Partial_owned_runtime_destruction_clears_unusable_runtime_context()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        plcControl.ReleaseRuntimeResult = OperationResult<PlcRuntimeReleaseOutcome>.Failure(
            PlcRuntimeReleaseOutcome.ResourcePartiallyDestroyed,
            "Fake runtime was only partially destroyed.");
        var workflow = CreateWorkflow(services);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(
            workflow,
            config,
            services,
            FakeTime.Create());
        workflow.Configure(CreateRunSetup(config) with
        {
            RuntimeProvisioningMode = RuntimeProvisioningMode.CreateOwned,
            RuntimeTarget = null
        });
        await PrepareTransferredRuntimeAsync(workflow);

        var release = await workflow.ReleaseRuntimeAsync(CreatePolicy(), CancellationToken.None);

        Assert.False(release.Result.IsSuccessful);
        Assert.Equal(RuntimeResourceReleaseMode.DestroyResource, plcControl.LastReleaseRuntimeRequest?.ReleaseMode);
        Assert.False(workflow.Current.Status.RuntimePrepared);
    }

    [Fact]
    public async Task Session_profile_load_failure_reports_diagnostic()
    {
        var config = new AccessProfileConfig(
            "missing",
            null,
            AccessProfileConfigurationPayload.Empty,
            "sample.ap");

        var result = await RunWorkflowProfileTestHelper.TryLoadAsync(
            config,
            FakeAccessProfileFactory.CreatePlugin(FakeTime.Create()),
            FakeTime.Create());

        Assert.Equal(AccessProfileLoadState.NotFound, result.State);
        Assert.Null(result.Session);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Profile.Load.NotFound");
    }

    [Fact]
    public async Task Workflow_logs_action_boundaries_and_blocked_actions()
    {
        var loggerFactory = new ListLoggerFactory<RunWorkflowService>();
        var logger = loggerFactory.Logger;
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);
        var workflow = CreateWorkflow(services, loggerFactory);
        await LoadAndConfigureAsync(workflow, FakeAccessProfileFactory.CreateDefaultConfig(), services);

        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);
        await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);
        await workflow.TransferProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.SimulateExternalRunStarted();
        lifecycle.CompleteRun(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompletionAnchor();

        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Workflow action started", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Workflow action finished", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Workflow action blocked", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("UnitTest command finished", StringComparison.Ordinal)
            && entry.Message.Contains("Accepted=True", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug
            && entry.Message.Contains("UnitTest lifecycle observation changed", StringComparison.Ordinal)
            && entry.Message.Contains(nameof(UnitTestLifecycleState.Completed), StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug
            && entry.Message.Contains("UnitTest event-stream batch received", StringComparison.Ordinal)
            && entry.Message.Contains("CompletionAnchorObserved=True", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message.Contains("Test run monitor update published", StringComparison.Ordinal)
            && entry.Message.Contains(nameof(TestRunMonitorUpdateKind.TerminalStateReached), StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Run workflow configured", StringComparison.Ordinal)
            && entry.Message.Contains("AccessProfileId=fake", StringComparison.Ordinal)
            && entry.Message.Contains("RuntimeProvisioningMode=UseExisting", StringComparison.Ordinal)
            && entry.Message.Contains("ConfigurationFingerprint=sha256:", StringComparison.Ordinal)
            && entry.Message.Contains("ConfigurationPayloadLength=", StringComparison.Ordinal)
            && entry.Message.Contains("ProjectTargetId=fake-project-target", StringComparison.Ordinal)
            && entry.Message.Contains("RuntimeTargetId=fake-runtime-target", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("PLC project prepared", StringComparison.Ordinal)
            && entry.Message.Contains("ProjectContextId=fake-project", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("PLC runtime prepared", StringComparison.Ordinal)
            && entry.Message.Contains("RuntimeContextId=fake-runtime", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Starting PLC runtime", StringComparison.Ordinal)
            && entry.Message.Contains("RuntimeTargetId=fake-runtime-target", StringComparison.Ordinal));
    }

    private static RunWorkflowService CreateWorkflow(
        IAccessProfilePlugin plugin,
        ListLoggerFactory<RunWorkflowService>? loggerFactory = null)
    {
        var timeProvider = FakeTime.Create();
        return new RunWorkflowService(
            new RunSetupValidator(timeProvider),
            TestCoreServiceContext.Create(timeProvider: timeProvider, loggerFactory: loggerFactory));
    }

    private static RunWorkflowService CreateWorkflow(FakeAccessProfileServices services)
    {
        return CreateWorkflow(new FakeAccessProfilePlugin((_, _) => services));
    }

    private static RunWorkflowService CreateWorkflow(
        FakeAccessProfileServices services,
        ListLoggerFactory<RunWorkflowService>? loggerFactory)
    {
        var timeProvider = FakeTime.Create();
        return new RunWorkflowService(
            new RunSetupValidator(timeProvider),
            TestCoreServiceContext.Create(timeProvider: timeProvider, loggerFactory: loggerFactory));
    }

    private static RunWorkflowService CreateWorkflow(FakeAccessProfileServices services, TimeProvider timeProvider)
    {
        return new RunWorkflowService(
            new RunSetupValidator(timeProvider),
            TestCoreServiceContext.Create(timeProvider: timeProvider));
    }

    private static RunSetup CreateRunSetup(
        AccessProfileConfig config,
        TimeSpan? commandTimeout = null,
        TimeSpan? plcStateChangeTimeout = null)
    {
        return new RunSetup(
            config,
            RuntimeProvisioningMode.UseExisting,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(CreateTimeoutPolicy(commandTimeout, plcStateChangeTimeout)),
            CreateProjectTarget(),
            CreateRuntimeTarget());
    }

    private static RunOperationTimeoutPolicy CreateTimeoutPolicy(
        TimeSpan? commandTimeout,
        TimeSpan? plcStateChangeTimeout)
    {
        var defaults = RunConfigurationDefaults.CreateDefaultTimeoutPolicy();
        return defaults with
        {
            PlcCommand = commandTimeout ?? defaults.PlcCommand,
            UnitTestCommand = commandTimeout ?? defaults.UnitTestCommand,
            PlcStartReaction = plcStateChangeTimeout ?? defaults.PlcStartReaction,
            PlcStopReaction = plcStateChangeTimeout ?? defaults.PlcStopReaction,
            UnitTestCommandReaction = plcStateChangeTimeout ?? defaults.UnitTestCommandReaction
        };
    }

    private static void PublishCompletionBatchWithResult(
        FakeUnitTestEventStreamService eventStream,
        UnitTestResultState result)
    {
        eventStream.PublishBatch(new UnitTestEventBatch(
            eventStream.CurrentMonitoringState.SessionId,
            [
                new UnitTestEventEntry(
                    SequenceId: 2,
                    Timestamp: FakeTime.DefaultStart.AddMilliseconds(1),
                    UnitTestOwnerScope.TestCase,
                    UnitTestEventType.Result,
                    Microsoft.Extensions.Logging.LogLevel.Information,
                    "SampleSuite.SampleCase",
                    "SampleCase",
                    UnitTestLifecycleState.Completed,
                    result,
                    $"Sample case finished with {result}."),
                new UnitTestEventEntry(
                    SequenceId: 3,
                    Timestamp: FakeTime.DefaultStart.AddMilliseconds(2),
                    UnitTestOwnerScope.RunControl,
                    UnitTestEventType.Result,
                    Microsoft.Extensions.Logging.LogLevel.Information,
                    "FakeRunControl",
                    "FakeRunControl",
                    UnitTestLifecycleState.Completed,
                    result,
                    $"Run Control finished with {result}."),
                new UnitTestEventEntry(
                    SequenceId: 4,
                    Timestamp: FakeTime.DefaultStart.AddMilliseconds(3),
                    UnitTestOwnerScope.RunControl,
                    UnitTestEventType.StateTransition,
                    Microsoft.Extensions.Logging.LogLevel.Information,
                    "FakeRunControl",
                    "FakeRunControl",
                    UnitTestLifecycleState.Completed,
                    UnitTestResultState.NotEvaluated,
                    "Run Control completed.")
            ],
            ProducerSequenceId: 4,
            DeletedSequenceId: 4,
            LostEventCount: 0,
            BufferObservedEmpty: true,
            EventBufferReadState.Empty));
    }

    private static WorkflowActionPolicy CreatePolicy()
    {
        return new WorkflowActionPolicy(TimeSpan.FromMinutes(1));
    }

    private static SecureString CreateSecureString(string value)
    {
        var result = new SecureString();
        foreach (var character in value)
        {
            result.AppendChar(character);
        }

        result.MakeReadOnly();
        return result;
    }

    private static ActionGateState Gate(RunWorkflowService workflow, WorkflowAction action)
    {
        return workflow.Current.ActionGates.Single(gate => gate.Action == action);
    }

    private static async Task LoadAndConfigureAsync(
        RunWorkflowService workflow,
        AccessProfileConfig config,
        FakeAccessProfileServices? services = null,
        TimeProvider? timeProvider = null)
    {
        if (services is null)
        {
            await RunWorkflowProfileTestHelper.LoadAndBindDefaultFakeAsync(workflow, config);
        }
        else
        {
            await RunWorkflowProfileTestHelper.LoadAndBindAsync(
                workflow,
                config,
                services,
                timeProvider ?? FakeTime.Create());
        }

        workflow.Configure(CreateRunSetup(config));
    }

    private static async Task PrepareTransferredRuntimeAsync(RunWorkflowService workflow)
    {
        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);
        await workflow.TransferProjectAsync(CreatePolicy(), CancellationToken.None);
    }

    private static async Task PrepareMonitoringAsync(
        RunWorkflowService workflow,
        FakeAccessProfileServices? services = null,
        TimeProvider? timeProvider = null)
    {
        await LoadAndConfigureAsync(
            workflow,
            FakeAccessProfileFactory.CreateDefaultConfig(),
            services,
            timeProvider);
        await PrepareTransferredRuntimeAsync(workflow);
        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(1);
        }

        Assert.True(predicate());
    }

    private static void ObserveRunStarted(FakeAccessProfileServices services)
    {
        Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle)
            .SimulateExternalRunStarted();
    }

    private static ProjectTargetRef CreateProjectTarget()
    {
        return new ProjectTargetRef(new ProjectTargetId("fake-project-target"), "Fake PLC Project Target", "FakeProjectTarget");
    }

    private static RuntimeTargetRef CreateRuntimeTarget()
    {
        return new RuntimeTargetRef(new RuntimeTargetId("fake-runtime-target"), "Fake PLC Runtime Target", "FakeRuntimeTarget");
    }

    private static string CreateTempProfileRoot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "plcsim-unittest-workflow-profile", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class RecordingProjectService(IPlcProjectService inner) : IPlcProjectService
    {
        public CancellationToken BuildToken { get; private set; }

        public CancellationToken TransferToken { get; private set; }

        public CancellationToken ReleaseToken { get; private set; }

        public Func<CancellationToken, Task<OperationResult<PlcBuildResult>>>? BuildHandler { get; set; }

        public Func<CancellationToken, Task<OperationResult<PlcTransferResult>>>? TransferHandler { get; set; }

        public PlcProjectContext? CurrentProject => inner.CurrentProject;

        public ServiceState State => inner.State;

        public AccessHealth Health => inner.Health;

        public event EventHandler<DiagnosticEventArgs> DiagnosticEventHandler
        {
            add => inner.DiagnosticEventHandler += value;
            remove => inner.DiagnosticEventHandler -= value;
        }

        public event EventHandler<ServiceStateChangedEventArgs> StateChangedEventHandler
        {
            add => inner.StateChangedEventHandler += value;
            remove => inner.StateChangedEventHandler -= value;
        }

        public event EventHandler<ProgressChangedEventArgs> ProgressChangedEventHandler
        {
            add => inner.ProgressChangedEventHandler += value;
            remove => inner.ProgressChangedEventHandler -= value;
        }

        public event EventHandler<PlcProjectStateChangedEventArgs> ProjectStateChangedEventHandler
        {
            add => inner.ProjectStateChangedEventHandler += value;
            remove => inner.ProjectStateChangedEventHandler -= value;
        }

        public Task<OperationResult<ProjectTargetDiscoveryResult>> DiscoverProjectTargetsAsync(
            PlcProjectRequest request,
            CancellationToken cancellationToken)
        {
            return inner.DiscoverProjectTargetsAsync(request, cancellationToken);
        }

        public Task<OperationResult<PlcProjectContext>> PrepareProjectAsync(
            PlcProjectRequest request,
            CancellationToken cancellationToken)
        {
            return inner.PrepareProjectAsync(request, cancellationToken);
        }

        public Task<OperationResult<ProjectTargetResolution>> ResolveProjectTargetAsync(
            PlcProjectContext project,
            CancellationToken cancellationToken)
        {
            return inner.ResolveProjectTargetAsync(project, cancellationToken);
        }

        public Task<OperationResult<SafetyProgramAuthorizationRequirementResult>> GetSafetyProgramAuthorizationRequirementAsync(
            PlcProjectContext project,
            ProjectTargetRef target,
            CancellationToken cancellationToken)
        {
            return inner.GetSafetyProgramAuthorizationRequirementAsync(project, target, cancellationToken);
        }

        public Task<OperationResult<SafetyProgramAuthorizationResult>> AuthorizeSafetyProgramAsync(
            PlcProjectContext project,
            ProjectTargetRef target,
            SecureString password,
            CancellationToken cancellationToken)
        {
            return inner.AuthorizeSafetyProgramAsync(project, target, password, cancellationToken);
        }

        public Task<OperationResult<PlcBuildResult>> BuildAsync(
            PlcProjectContext project,
            ProjectTargetRef target,
            CancellationToken cancellationToken)
        {
            BuildToken = cancellationToken;
            return BuildHandler is null
                ? inner.BuildAsync(project, target, cancellationToken)
                : BuildHandler(cancellationToken);
        }

        public Task<OperationResult<PlcTransferResult>> TransferToAsync(
            PlcProjectContext project,
            ProjectTargetRef projectTarget,
            RuntimeTargetRef runtimeTarget,
            CancellationToken cancellationToken)
        {
            TransferToken = cancellationToken;
            return TransferHandler is null
                ? inner.TransferToAsync(project, projectTarget, runtimeTarget, cancellationToken)
                : TransferHandler(cancellationToken);
        }

        public Task<OperationResult> ReleaseProjectAsync(CancellationToken cancellationToken)
        {
            ReleaseToken = cancellationToken;
            return inner.ReleaseProjectAsync(cancellationToken);
        }

        public Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
        {
            return inner.CleanupAsync(cancellationToken);
        }
    }
}
