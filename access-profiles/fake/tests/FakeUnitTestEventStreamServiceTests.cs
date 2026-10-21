using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Fake;

public sealed class FakeUnitTestEventStreamServiceTests
{
    [Fact]
    public async Task Communication_invalidation_retains_buffer_evidence_without_publishing_a_batch()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);
        service.PublishEventBufferLostEvents(7, 412, 256);
        var previous = service.CurrentMonitoringState;
        var batchCount = 0;
        service.EventBatchReceivedEventHandler += (_, _) => batchCount++;
        service.SimulateEventStreamCommunicationInvalidated();
        Assert.Equal(previous with { IsMonitoring = false, LastReadState = EventBufferReadState.SessionInvalid },
            service.CurrentMonitoringState);
        Assert.Equal(0, batchCount);
    }

    [Fact]
    public void Initial_state_has_no_buffer_observation()
    {
        var state = new FakeUnitTestEventStreamService().CurrentMonitoringState;
        Assert.Equal(EventBufferReadState.NotObserved, state.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Idle, state.BatchReleaseState);
        Assert.False(state.BufferObservedEmpty);
        Assert.False(state.IsMonitoring);
    }

    [Fact]
    public async Task Start_monitoring_uses_context_session_and_empty_buffer_state()
    {
        var service = new FakeUnitTestEventStreamService();

        var result = await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.True(result.Value?.IsMonitoring);
        Assert.Equal("fake-session", result.Value?.SessionId.Value);
        Assert.Equal(EventBufferReadState.Empty, result.Value?.LastReadState);
        Assert.True(result.Value?.BufferObservedEmpty);
    }

    [Fact]
    public async Task Monitoring_state_changes_are_published_with_previous_and_current_state()
    {
        var service = new FakeUnitTestEventStreamService();
        EventStreamStateChangedEventArgs? published = null;
        service.MonitoringStateChangedEventHandler += (_, args) => published = args;

        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);

        Assert.NotNull(published);
        Assert.False(published.PreviousState.IsMonitoring);
        Assert.True(published.CurrentState.IsMonitoring);
        Assert.Equal(service.CurrentMonitoringState, published.CurrentState);
    }

    [Fact]
    public async Task Overflow_loss_and_recovery_states_are_published_without_batches()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);
        var states = new List<EventStreamState>();
        var batchCount = 0;
        service.MonitoringStateChangedEventHandler += (_, args) => states.Add(args.CurrentState);
        service.EventBatchReceivedEventHandler += (_, _) => batchCount++;

        service.PublishEventBufferFull(producerSequenceId: 288, deletedSequenceId: 255);
        service.PublishEventBufferLostEvents(
            lostEventCount: 19,
            producerSequenceId: 412,
            deletedSequenceId: 255);
        service.PublishEventBufferRecovered();

        Assert.Equal(3, states.Count);
        Assert.True(states[0].BufferFull);
        Assert.Equal((ushort)0, states[0].LostEventCount);
        Assert.Equal((ushort)19, states[1].LostEventCount);
        Assert.True(states[1].BufferFull);
        Assert.Equal(EventBufferReadState.Empty, states[2].LastReadState);
        Assert.Equal((ushort)0, states[2].LostEventCount);
        Assert.False(states[2].BufferFull);
        Assert.Equal((uint)0, states[2].ProducerSequenceId);
        Assert.Equal(0, batchCount);
    }

    [Fact]
    public async Task Start_monitoring_can_be_held_and_completed()
    {
        var service = new FakeUnitTestEventStreamService
        {
            HoldNextStartMonitoring = true
        };
        var context = CreateContext();

        var start = service.StartMonitoringAsync(context, CancellationToken.None);

        Assert.True(service.IsStartMonitoringPending);
        Assert.False(start.IsCompleted);

        service.CompletePendingStartMonitoring(context);
        var result = await start;

        Assert.True(result.IsSuccessful);
        Assert.False(service.IsStartMonitoringPending);
        Assert.True(service.CurrentMonitoringState.IsMonitoring);
        Assert.Equal("fake-session", service.CurrentMonitoringState.SessionId.Value);
    }

    [Fact]
    public async Task Start_monitoring_can_be_held_and_failed()
    {
        var service = new FakeUnitTestEventStreamService
        {
            HoldNextStartMonitoring = true
        };

        var start = service.StartMonitoringAsync(CreateContext(), CancellationToken.None);
        service.FailPendingStartMonitoring("Fake monitoring interrupted.");
        var result = await start;

        Assert.False(result.IsSuccessful);
        Assert.False(service.IsStartMonitoringPending);
        Assert.Equal("Fake monitoring interrupted.", result.Message);
        Assert.False(service.CurrentMonitoringState.IsMonitoring);
    }

    [Fact]
    public async Task Start_monitoring_cancels_held_operation()
    {
        var service = new FakeUnitTestEventStreamService
        {
            HoldNextStartMonitoring = true
        };
        using var cancellation = new CancellationTokenSource();

        var start = service.StartMonitoringAsync(CreateContext(), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.False(service.IsStartMonitoringPending);
    }

    [Fact]
    public async Task Release_communication_can_be_held_and_completed()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);
        service.HoldNextReleaseCommunication = true;

        var release = service.ReleaseCommunicationAsync(CancellationToken.None);

        Assert.True(service.IsReleaseCommunicationPending);
        Assert.False(release.IsCompleted);
        Assert.True(service.CurrentMonitoringState.IsMonitoring);

        service.CompletePendingReleaseCommunication();
        var result = await release;

        Assert.True(result.IsSuccessful);
        Assert.False(service.IsReleaseCommunicationPending);
        Assert.False(service.CurrentMonitoringState.IsMonitoring);
        Assert.Equal(EventStreamBatchReleaseState.Idle, service.CurrentMonitoringState.BatchReleaseState);
    }

    [Fact]
    public async Task Release_communication_cancels_held_operation()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);
        service.HoldNextReleaseCommunication = true;
        using var cancellation = new CancellationTokenSource();

        var release = service.ReleaseCommunicationAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => release);
        Assert.False(service.IsReleaseCommunicationPending);
    }

    [Fact]
    public async Task Completion_anchor_batch_contains_runcontrol_completed_transition()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);
        UnitTestEventBatch? received = null;
        service.EventBatchReceivedEventHandler += (_, args) => received = args.Batch;

        var batch = service.PublishRunControlCompletionAnchor();

        Assert.Same(batch, received);
        Assert.True(service.CurrentMonitoringState.BufferObservedEmpty);
        Assert.Equal(EventBufferReadState.Empty, service.CurrentMonitoringState.LastReadState);
        Assert.Single(batch.Events);
        Assert.Equal(UnitTestOwnerScope.RunControl, batch.Events[0].OwnerScope);
        Assert.Equal(UnitTestEventType.StateTransition, batch.Events[0].EventType);
        Assert.Equal(UnitTestLifecycleState.Completed, batch.Events[0].State);
        Assert.Equal(1u, batch.Events[0].SequenceId);
        Assert.Equal(1u, batch.ProducerSequenceId);
        Assert.Equal(1u, batch.DeletedSequenceId);
    }

    [Fact]
    public async Task Runcontrol_started_batch_contains_starting_anchor_without_empty_buffer()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);

        var batch = service.PublishRunControlStarted();

        Assert.Single(batch.Events);
        Assert.False(batch.BufferObservedEmpty);
        Assert.Equal(EventBufferReadState.Valid, service.CurrentMonitoringState.LastReadState);
        Assert.Equal(UnitTestLifecycleState.Starting, batch.Events[0].State);
        Assert.Equal(UnitTestResultState.NotEvaluated, batch.Events[0].Result);
    }

    [Fact]
    public async Task Runcontrol_completed_batch_can_carry_result_evidence()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);

        service.PublishRunControlStarted();
        var batch = service.PublishRunControlCompleted(UnitTestResultState.Failed);

        Assert.True(batch.BufferObservedEmpty);
        Assert.Equal(EventBufferReadState.Empty, service.CurrentMonitoringState.LastReadState);
        Assert.Equal(2, batch.Events.Count);
        Assert.Equal(UnitTestEventType.Result, batch.Events[0].EventType);
        Assert.Equal(UnitTestResultState.Failed, batch.Events[0].Result);
        Assert.Equal(2u, batch.Events[0].SequenceId);
        Assert.Equal(UnitTestLifecycleState.Completed, batch.Events[1].State);
        Assert.Equal(UnitTestResultState.NotEvaluated, batch.Events[1].Result);
        Assert.Equal(3u, batch.Events[1].SequenceId);
    }

    [Fact]
    public async Task Runcontrol_abort_and_reset_batches_are_named_helpers()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);

        var abort = service.PublishRunControlAbortCompleted();
        var reset = service.PublishRunControlResetObserved();

        Assert.Equal(UnitTestResultState.Abort, abort.Events[0].Result);
        Assert.Equal(UnitTestLifecycleState.Completed, abort.Events[1].State);
        Assert.Equal(UnitTestLifecycleState.Reset, reset.Events[0].State);
        Assert.Equal(UnitTestResultState.NotEvaluated, reset.Events[0].Result);
    }

    [Fact]
    public async Task Completion_anchor_can_be_published_before_buffer_is_empty()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);

        var batch = service.PublishRunControlCompletionAnchor(bufferObservedEmpty: false);

        Assert.False(batch.BufferObservedEmpty);
        Assert.False(service.CurrentMonitoringState.BufferObservedEmpty);
        Assert.Equal(EventBufferReadState.Valid, service.CurrentMonitoringState.LastReadState);
        Assert.Equal(0u, batch.DeletedSequenceId);
    }

    [Fact]
    public async Task Missing_result_evidence_helper_publishes_completion_anchor_only()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);

        var batch = service.PublishRunControlCompletedWithoutResult();

        Assert.Single(batch.Events);
        Assert.Equal(UnitTestEventType.StateTransition, batch.Events[0].EventType);
        Assert.Equal(UnitTestLifecycleState.Completed, batch.Events[0].State);
        Assert.Equal(UnitTestResultState.NotEvaluated, batch.Events[0].Result);
        Assert.True(batch.BufferObservedEmpty);
        Assert.Equal(EventBufferReadState.Empty, service.CurrentMonitoringState.LastReadState);
    }

    [Fact]
    public async Task Missing_completion_anchor_helper_publishes_result_only()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);

        var batch = service.PublishRunControlResultWithoutCompletionAnchor(UnitTestResultState.Passed);

        Assert.Single(batch.Events);
        Assert.Equal(UnitTestEventType.Result, batch.Events[0].EventType);
        Assert.Equal(UnitTestLifecycleState.Completed, batch.Events[0].State);
        Assert.Equal(UnitTestResultState.Passed, batch.Events[0].Result);
        Assert.True(batch.BufferObservedEmpty);
        Assert.Equal(EventBufferReadState.Empty, service.CurrentMonitoringState.LastReadState);
    }

    [Fact]
    public async Task Empty_buffer_batch_does_not_create_completion_anchor()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);

        var batch = service.PublishEmptyBuffer();

        Assert.Empty(batch.Events);
        Assert.True(batch.BufferObservedEmpty);
        Assert.Equal(EventBufferReadState.Empty, batch.ReadState);
        Assert.True(service.CurrentMonitoringState.BufferObservedEmpty);
    }

    [Fact]
    public async Task Diagnostic_batch_without_completion_anchor_is_controllable()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);

        var batch = service.PublishBatchWithoutCompletionAnchor(bufferObservedEmpty: true);

        Assert.Single(batch.Events);
        Assert.Equal(UnitTestEventType.Info, batch.Events[0].EventType);
        Assert.NotEqual(UnitTestLifecycleState.Completed, batch.Events[0].State);
        Assert.True(batch.BufferObservedEmpty);
        Assert.Equal(EventBufferReadState.Empty, service.CurrentMonitoringState.LastReadState);
    }

    [Fact]
    public async Task Lost_event_evidence_sets_lost_count_on_batch_and_state()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);

        var batch = service.PublishLostEventEvidence(lostEventCount: 3);

        Assert.Equal(3, batch.LostEventCount);
        Assert.Equal(3, service.CurrentMonitoringState.LostEventCount);
        Assert.Equal(batch.ProducerSequenceId, service.CurrentMonitoringState.ProducerSequenceId);
        Assert.Equal(batch.DeletedSequenceId, service.CurrentMonitoringState.DeletedSequenceId);
        Assert.Contains(batch.Events, entry => entry.OwnerScope == UnitTestOwnerScope.RunControl
            && entry.EventType == UnitTestEventType.StateTransition
            && entry.State == UnitTestLifecycleState.Completed);
    }

    [Fact]
    public async Task Accepted_event_buffer_reset_records_dispatch_without_synthesizing_clean_state()
    {
        var service = new FakeUnitTestEventStreamService();
        var context = CreateContext();
        await service.StartMonitoringAsync(context, CancellationToken.None);
        service.PublishLostEventEvidence(lostEventCount: 3);

        var result = await service.ResetEventBufferAsync(context, CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.True(result.Value!.Accepted);
        Assert.Equal(1, service.EventBufferResetCount);
        Assert.Equal(3, service.CurrentMonitoringState.LostEventCount);

        service.PublishEmptyBuffer();

        Assert.Equal(0, service.CurrentMonitoringState.LostEventCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Event_buffer_reset_failure_is_deterministic_and_preserves_observed_state(
        bool notSupported)
    {
        var service = new FakeUnitTestEventStreamService();
        var context = CreateContext();
        await service.StartMonitoringAsync(context, CancellationToken.None);
        service.PublishLostEventEvidence(lostEventCount: 2);
        service.EventBufferResetResult = notSupported
            ? PLCSIM.UnitTest.Common.Operations.OperationResult<EventBufferResetCommandResult>.NotSupported(
                new EventBufferResetCommandResult(Accepted: false),
                "Fake reset is not supported.")
            : PLCSIM.UnitTest.Common.Operations.OperationResult<EventBufferResetCommandResult>.Failure(
                new EventBufferResetCommandResult(Accepted: false),
                "Fake reset write failed.");

        var result = await service.ResetEventBufferAsync(context, CancellationToken.None);

        Assert.True(result.IsUnsuccessful);
        Assert.False(result.Value!.Accepted);
        Assert.Equal(1, service.EventBufferResetCount);
        Assert.Equal(2, service.CurrentMonitoringState.LostEventCount);
    }

    [Fact]
    public async Task Sequence_gap_helper_skips_one_producer_sequence()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);
        service.PublishRunControlStarted();

        var gap = service.PublishSequenceGap();

        Assert.Single(gap.Events);
        Assert.Equal(3u, gap.Events[0].SequenceId);
        Assert.Equal(3u, gap.ProducerSequenceId);
        Assert.Equal(2u, gap.DeletedSequenceId);
        Assert.False(gap.BufferObservedEmpty);
    }

    [Fact]
    public async Task Event_buffer_epoch_reset_helper_reuses_sequence_one()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);
        service.PublishRunControlStarted();
        service.PublishRunControlCompleted();

        var reset = service.PublishEventBufferEpochResetObserved();

        Assert.Single(reset.Events);
        Assert.Equal(1u, reset.Events[0].SequenceId);
        Assert.Equal(1u, reset.ProducerSequenceId);
        Assert.Equal(0u, reset.DeletedSequenceId);
        Assert.Equal(EventBufferReadState.Valid, reset.ReadState);
    }

    [Fact]
    public async Task Old_event_batch_can_reuse_an_earlier_sequence_id()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);
        service.PublishRunControlCompletionAnchor();

        var old = service.PublishOldEvent(sequenceId: 1);

        Assert.Equal(1u, old.Events[0].SequenceId);
        Assert.Equal(1u, old.DeletedSequenceId);
        Assert.False(old.BufferObservedEmpty);
    }

    [Fact]
    public async Task Retry_required_and_inconsistent_helpers_publish_matching_read_state()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);

        var retry = service.PublishRetryRequired();
        Assert.Empty(retry.Events);
        Assert.Equal(EventBufferReadState.RetryRequired, retry.ReadState);
        Assert.Equal(EventBufferReadState.RetryRequired, service.CurrentMonitoringState.LastReadState);

        var inconsistent = service.PublishInconsistentReadState();

        Assert.Empty(inconsistent.Events);
        Assert.Equal(EventBufferReadState.Inconsistent, inconsistent.ReadState);
        Assert.Equal(EventBufferReadState.Inconsistent, service.CurrentMonitoringState.LastReadState);
    }

    [Fact]
    public async Task Session_invalidated_helper_publishes_session_invalid_read_state()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);
        UnitTestEventBatch? received = null;
        service.EventBatchReceivedEventHandler += (_, args) => received = args.Batch;

        var batch = service.PublishSessionInvalidated();

        Assert.Same(batch, received);
        Assert.Empty(batch.Events);
        Assert.Equal(EventBufferReadState.SessionInvalid, batch.ReadState);
        Assert.Equal(EventBufferReadState.SessionInvalid, service.CurrentMonitoringState.LastReadState);
        Assert.False(service.CurrentMonitoringState.BufferObservedEmpty);
    }

    [Fact]
    public async Task Contract_invalidated_helper_publishes_contract_invalid_read_state()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);

        var batch = service.PublishContractInvalidated();

        Assert.Empty(batch.Events);
        Assert.Equal(EventBufferReadState.ContractInvalid, batch.ReadState);
        Assert.Equal(EventBufferReadState.ContractInvalid, service.CurrentMonitoringState.LastReadState);
        Assert.False(service.CurrentMonitoringState.BufferObservedEmpty);
    }

    [Fact]
    public async Task Aggregation_helpers_publish_valid_and_invalid_aggregation_payloads()
    {
        var service = new FakeUnitTestEventStreamService();
        await service.StartMonitoringAsync(CreateContext(), CancellationToken.None);

        var valid = service.PublishAggregation(total: 2, passed: 1, failed: 1, runtime: 42);
        var invalid = service.PublishInvalidAggregation(UnitTestOwnerScope.TestSuite, "SampleSuite");

        Assert.Single(valid.Events);
        Assert.Equal(UnitTestEventType.Aggregation, valid.Events[0].EventType);
        Assert.Contains(@"""total"":2", valid.Events[0].Message, StringComparison.Ordinal);
        Assert.Contains(@"""failed"":1", valid.Events[0].Message, StringComparison.Ordinal);
        Assert.Single(invalid.Events);
        Assert.Equal(UnitTestOwnerScope.TestSuite, invalid.Events[0].OwnerScope);
        Assert.Equal("not-json", invalid.Events[0].Message);
    }

    private static UnitTestContext CreateContext()
    {
        var session = new CommunicationSession(new CommunicationSessionId("fake-session"), FakeTime.DefaultStart);
        var project = new PlcProjectContext(new ProjectContextId("fake-project"), "Fake PLC Project");
        var runtime = new PlcRuntimeContext(new RuntimeContextId("fake-runtime"), "Fake PLC Runtime");
        var projectTarget = new ProjectTargetRef(new ProjectTargetId("fake-project-target"), "Fake PLC", "CPU 1516");
        var runtimeTarget = new RuntimeTargetRef(new RuntimeTargetId("fake-runtime-target"), "Fake Runtime", "192.0.2.10");

        return new UnitTestContext(
            session,
            new ProjectExecutionContext(ProjectOwnershipMode.ApplicationManaged, project, projectTarget),
            runtime,
            runtimeTarget);
    }
}
