using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.Analysis;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.Tests.Core.TestLogging;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Workflow;

public sealed class EventStreamProcessorTests
{
    [Fact]
    public void Process_batch_without_observation_is_ignored()
    {
        var processor = new EventStreamProcessor(FakeTime.Create());

        var result = processor.ProcessBatch(CreateBatch([CreateStartAnchor(1)]));

        Assert.Equal(EventProcessingState.Ignored, result.State);
        Assert.Null(result.CurrentRun);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.NotObserving");
    }

    [Fact]
    public void Process_batch_rejects_session_mismatch()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch(
            [CreateStartAnchor(1)],
            sessionId: new CommunicationSessionId("other-session")));

        Assert.Equal(EventProcessingState.Ignored, result.State);
        Assert.Null(processor.Current);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.SessionMismatch");
    }

    [Fact]
    public void Processor_traces_each_classified_event_once_with_only_accepted_events_correlated_to_epoch()
    {
        var sink = new RecordingUnitTestActivityTraceSink();
        var processor = CreateStartedProcessor(activityTraceSink: sink);

        var accepted = processor.ProcessBatch(CreateBatch([
            CreateStartAnchor(1),
            CreateEntry(2)
        ]));
        processor.ProcessBatch(CreateBatch([
            CreateEntry(2),
            CreateCompletionAnchor(3),
            CreateEntry(4, UnitTestEventType.Framework, UnitTestOwnerScope.Framework),
            CreateEntry(5, UnitTestEventType.Result, UnitTestOwnerScope.TestCase, "Outside", result: UnitTestResultState.Passed)
        ]));

        var events = sink.Activities
            .Where(static activity => activity.Payload is PlcEventActivityPayload)
            .Select(static activity => (
                Event: ((PlcEventActivityPayload)activity.Payload).Event,
                activity.Correlation.RunEpochId))
            .ToArray();

        Assert.Equal(new uint[] { 1, 2, 2, 3, 4, 5 }, events.Select(static item => item.Event.SequenceId));
        Assert.All(events.Take(2), item => Assert.Equal(accepted.CurrentRun?.EpochId, item.RunEpochId));
        Assert.Null(events[2].RunEpochId);
        Assert.Equal(accepted.CurrentRun?.EpochId, events[3].RunEpochId);
        Assert.All(events.Skip(4), static item => Assert.Null(item.RunEpochId));
    }

    [Fact]
    public void Processor_traces_run_epoch_transitions_without_duplicate_start_after_observation_restart()
    {
        var sink = new RecordingUnitTestActivityTraceSink();
        var processor = CreateStartedProcessor(activityTraceSink: sink);

        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1)]));
        processor.CompleteObservation(EventStreamCompletionReason.Cleanup, FakeTime.DefaultStart.AddSeconds(1));
        processor.StartObservation(new EventStreamObservationContext(
            new CommunicationSessionId("session"),
            1,
            FakeTime.DefaultStart.AddSeconds(2)));
        processor.ProcessBatch(CreateBatch([]));

        var epochActivities = sink.Activities
            .Where(static activity => activity.Payload is RunEpochActivityPayload)
            .Select(static activity => ((RunEpochActivityPayload)activity.Payload).Activity)
            .ToArray();

        Assert.Single(epochActivities, UnitTestRunEpochActivityKind.Started);
        Assert.DoesNotContain(epochActivities.Skip(1), activity => activity == UnitTestRunEpochActivityKind.Started);
    }

    [Fact]
    public void Run_control_starting_anchor_opens_epoch_and_is_included()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch([CreateStartAnchor(1)]));

        Assert.True(result.RunEpochStarted);
        Assert.Equal(EventProcessingState.Accepted, result.State);
        var snapshot = Assert.IsType<UnitTestRunEpochSnapshot>(result.CurrentRun);
        Assert.Equal(UnitTestRunState.Running, snapshot.State);
        Assert.Equal(EvidenceState.Partial, snapshot.EvidenceState);
        Assert.Equal(new uint[] { 1 }, snapshot.EventStream.Select(item => item.SequenceId));
        Assert.Equal(1u, snapshot.Root.CreationEvent?.SequenceId);
    }

    [Theory]
    [InlineData(UnitTestOwnerScope.RunControl, UnitTestLifecycleState.Running)]
    [InlineData(UnitTestOwnerScope.RunControl, UnitTestLifecycleState.Completed)]
    [InlineData(UnitTestOwnerScope.TestSuite, UnitTestLifecycleState.Starting)]
    public void Non_start_anchor_does_not_open_epoch(
        UnitTestOwnerScope ownerScope,
        UnitTestLifecycleState state)
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch([
            CreateEntry(1, UnitTestEventType.StateTransition, ownerScope, state: state)
        ]));

        Assert.False(result.RunEpochStarted);
        Assert.Null(result.CurrentRun);
    }

    [Fact]
    public void Process_batch_accepts_epoch_events_in_sequence_order()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch([
            CreateEntry(3),
            CreateStartAnchor(1),
            CreateEntry(2)
        ]));

        Assert.Equal(EventProcessingState.Accepted, result.State);
        Assert.Equal(new uint[] { 1, 2, 3 }, result.AcceptedEvents.Select(item => item.SequenceId));
        Assert.Equal(new uint[] { 1, 2, 3 }, processor.AcceptedEvents.Select(item => item.SequenceId));
    }

    [Fact]
    public void New_start_anchor_replaces_incomplete_epoch_and_reports_overlap()
    {
        var processor = CreateStartedProcessor();
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1), CreateEntry(2)]));

        var result = processor.ProcessBatch(CreateBatch([CreateStartAnchor(3)]));

        Assert.True(result.RunEpochStarted);
        Assert.Equal(new uint[] { 3 }, processor.Current?.EventStream.Select(item => item.SequenceId));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.OverlappingRunEpoch");
    }

    [Fact]
    public void Framework_event_inside_epoch_is_correlated_and_kept()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch([
            CreateStartAnchor(1),
            CreateEntry(2, UnitTestEventType.Framework, UnitTestOwnerScope.Framework)
        ]));

        var snapshot = Assert.IsType<UnitTestRunEpochSnapshot>(result.CurrentRun);
        Assert.Equal(new uint[] { 1, 2 }, snapshot.EventStream.Select(item => item.SequenceId));
        Assert.Equal(new uint[] { 2 }, snapshot.FrameworkEvents.Select(item => item.SequenceId));
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("Workflow.EventStreamProcessor.FrameworkEvent", diagnostic.Code);
        Assert.Equal(snapshot.EpochId, diagnostic.Correlation.UnitTestRunEpochId);
    }

    [Fact]
    public void Framework_event_outside_epoch_is_reported_without_run_correlation()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch([
            CreateEntry(1, UnitTestEventType.Framework, UnitTestOwnerScope.Framework)
        ]));

        Assert.Null(result.CurrentRun);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("Workflow.EventStreamProcessor.FrameworkEvent", diagnostic.Code);
        Assert.Null(diagnostic.Correlation.UnitTestRunEpochId);
    }

    [Fact]
    public void Run_owned_event_outside_epoch_is_diagnosed()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch([
            CreateEntry(
                1,
                UnitTestEventType.Result,
                UnitTestOwnerScope.TestCase,
                name: "Math.AddsNumbers",
                result: UnitTestResultState.Passed)
        ]));

        Assert.Null(result.CurrentRun);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.RunEvidenceOutsideEpoch");
    }

    [Fact]
    public void Initialization_and_idle_evidence_outside_epoch_is_not_diagnosed()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch([
            CreateEntry(1, UnitTestEventType.Trigger, UnitTestOwnerScope.TestSuite, "UnitTest.Sample", UnitTestLifecycleState.Uninitialized),
            CreateEntry(2, UnitTestEventType.Trigger, UnitTestOwnerScope.TestCase, "UnitTest.Sample.Case", UnitTestLifecycleState.Uninitialized),
            CreateEntry(3, UnitTestEventType.StateTransition, UnitTestOwnerScope.TestCase, "UnitTest.Sample.Case", UnitTestLifecycleState.Idle),
            CreateEntry(4, UnitTestEventType.StateTransition, UnitTestOwnerScope.TestSuite, "UnitTest.Sample", UnitTestLifecycleState.Reset)
        ]));

        Assert.Null(result.CurrentRun);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Completion_anchor_and_empty_buffer_complete_fast_run_in_one_batch()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch(
            [
                CreateStartAnchor(1),
                CreateEntry(2, UnitTestEventType.Result, result: UnitTestResultState.Passed),
                CreateCompletionAnchor(3)
            ],
            deletedSequenceId: 3,
            bufferObservedEmpty: true,
            readState: EventBufferReadState.Empty));

        Assert.True(result.RunEpochStarted);
        Assert.True(result.RunControlCompletionAnchorObserved);
        Assert.Equal(EventProcessingState.Completed, result.State);
        var snapshot = Assert.IsType<UnitTestRunEpochSnapshot>(result.CurrentRun);
        Assert.Equal(UnitTestRunState.Completed, snapshot.State);
        Assert.Equal(UnitTestResultState.Passed, snapshot.Result);
        Assert.Equal(EvidenceState.Complete, snapshot.EvidenceState);
        Assert.Equal(new uint[] { 1, 2, 3 }, snapshot.EventStream.Select(item => item.SequenceId));
    }

    [Fact]
    public void Completion_anchor_then_later_empty_buffer_completes_epoch()
    {
        var processor = CreateStartedProcessor();
        var anchor = processor.ProcessBatch(CreateBatch([
            CreateStartAnchor(1),
            CreateEntry(2, UnitTestEventType.Result, result: UnitTestResultState.Passed),
            CreateCompletionAnchor(3)
        ]));

        var pending = processor.ProcessBatch(CreateBatch(
            [],
            producerSequenceId: 3,
            deletedSequenceId: 2,
            bufferObservedEmpty: false));
        var completion = processor.ProcessBatch(CreateBatch(
            [],
            producerSequenceId: 3,
            deletedSequenceId: 3,
            bufferObservedEmpty: true,
            readState: EventBufferReadState.Empty));

        Assert.Equal(UnitTestRunState.Completing, anchor.CurrentRun?.State);
        Assert.True(anchor.RunControlCompletionAnchorObserved);
        Assert.Equal(UnitTestRunState.Completing, pending.CurrentRun?.State);
        Assert.Equal(EventProcessingState.Completed, completion.State);
        Assert.Equal(UnitTestRunState.Completed, completion.CurrentRun?.State);
    }

    [Fact]
    public void Empty_read_with_remaining_content_preserves_evidence_and_requires_actual_empty_observation()
    {
        var processor = CreateStartedProcessor();
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1)]));

        var emptyRead = processor.ProcessBatch(CreateBatch(
            [], producerSequenceId: 3, deletedSequenceId: 1,
            bufferObservedEmpty: false, readState: EventBufferReadState.Empty));

        Assert.Equal(EventProcessingState.Empty, emptyRead.State);
        Assert.Equal(EvidenceState.Partial, emptyRead.CurrentRun?.EvidenceState);
        Assert.Empty(emptyRead.Diagnostics);

        var anchor = processor.ProcessBatch(CreateBatch([
            CreateEntry(2, UnitTestEventType.Result, result: UnitTestResultState.Passed),
            CreateCompletionAnchor(3)
        ]));
        Assert.Equal(UnitTestRunState.Completing, anchor.CurrentRun?.State);
        Assert.Equal(EvidenceState.Partial, anchor.CurrentRun?.EvidenceState);

        var completed = processor.ProcessBatch(CreateBatch(
            [], producerSequenceId: 3, deletedSequenceId: 3,
            bufferObservedEmpty: true, readState: EventBufferReadState.Empty));
        Assert.Equal(UnitTestRunState.Completed, completed.CurrentRun?.State);
        Assert.Equal(EvidenceState.Complete, completed.CurrentRun?.EvidenceState);
        Assert.Equal(UnitTestResultState.Passed, completed.CurrentRun?.Result);
        Assert.Equal(new uint[] { 1, 2, 3 }, completed.CurrentRun!.EventStream.Select(e => e.SequenceId));
    }

    [Theory]
    [InlineData(EventBufferReadState.Empty, 1, EventProcessingState.Incomplete)]
    [InlineData(EventBufferReadState.Valid, 0, EventProcessingState.Incomplete)]
    [InlineData(EventBufferReadState.RetryRequired, 0, EventProcessingState.Incomplete)]
    [InlineData(EventBufferReadState.Inconsistent, 0, EventProcessingState.Ambiguous)]
    [InlineData(EventBufferReadState.ContractInvalid, 0, EventProcessingState.Invalidated)]
    [InlineData(EventBufferReadState.SessionInvalid, 0, EventProcessingState.Invalidated)]
    public void Empty_read_correction_preserves_other_classifications(
        EventBufferReadState readState, ushort lost, EventProcessingState expected)
    {
        var processor = CreateStartedProcessor();
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1)]));
        var result = processor.ProcessBatch(CreateBatch(
            [], producerSequenceId: 3, deletedSequenceId: 1,
            lostEventCount: lost, bufferObservedEmpty: false, readState: readState));
        Assert.Equal(expected, result.State);
    }

    [Fact]
    public void Empty_read_with_remaining_content_does_not_repair_previous_evidence_loss()
    {
        var processor = CreateStartedProcessor();
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1)], lostEventCount: 1));
        processor.ProcessBatch(CreateBatch(
            [], producerSequenceId: 3, deletedSequenceId: 1,
            bufferObservedEmpty: false, readState: EventBufferReadState.Empty));
        var completed = processor.ProcessBatch(CreateBatch([
            CreateEntry(2, UnitTestEventType.Result, result: UnitTestResultState.Passed),
            CreateCompletionAnchor(3)
        ], bufferObservedEmpty: true));
        Assert.Equal(EvidenceState.Incomplete, completed.CurrentRun?.EvidenceState);
        Assert.Equal((ushort)1, completed.CurrentRun?.LostEventCount);
    }

    [Fact]
    public void Empty_buffer_then_later_completion_anchor_completes_epoch()
    {
        var processor = CreateStartedProcessor();
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1)]));
        processor.ProcessBatch(CreateBatch(
            [],
            producerSequenceId: 1,
            deletedSequenceId: 1,
            bufferObservedEmpty: true,
            readState: EventBufferReadState.Empty));

        var completion = processor.ProcessBatch(CreateBatch([
            CreateEntry(2, UnitTestEventType.Result, result: UnitTestResultState.Passed),
            CreateCompletionAnchor(3)
        ]));

        Assert.Equal(EventProcessingState.Completed, completion.State);
        Assert.Equal(UnitTestRunState.Completed, completion.CurrentRun?.State);
    }

    [Fact]
    public void Completion_anchor_without_start_is_out_of_epoch()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch([CreateCompletionAnchor(1)]));

        Assert.Null(result.CurrentRun);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.RunEvidenceOutsideEpoch");
    }

    [Fact]
    public void Events_after_completion_anchor_are_not_attached_to_epoch()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch([
            CreateStartAnchor(1),
            CreateEntry(2, UnitTestEventType.Result, result: UnitTestResultState.Passed),
            CreateCompletionAnchor(3),
            CreateEntry(4, UnitTestEventType.Result, UnitTestOwnerScope.TestCase, "Late.Case", result: UnitTestResultState.Passed)
        ]));

        Assert.Equal(new uint[] { 1, 2, 3 }, result.CurrentRun?.EventStream.Select(item => item.SequenceId));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.RunEvidenceOutsideEpoch");
    }

    [Fact]
    public void Invalid_read_state_invalidates_active_epoch()
    {
        var processor = CreateStartedProcessor();
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1), CreateEntry(2)]));

        var result = processor.ProcessBatch(CreateBatch(
            [CreateEntry(3)],
            readState: EventBufferReadState.SessionInvalid));

        Assert.Equal(EventProcessingState.Invalidated, result.State);
        Assert.Equal(UnitTestRunState.Invalidated, result.CurrentRun?.State);
        Assert.Equal(EvidenceState.Incomplete, result.CurrentRun?.EvidenceState);
        Assert.Equal(new uint[] { 1, 2 }, result.CurrentRun?.EventStream.Select(item => item.SequenceId));
    }

    [Fact]
    public void Snapshot_is_a_stable_copy()
    {
        var processor = CreateStartedProcessor();
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1), CreateEntry(2)]));
        var first = Assert.IsType<UnitTestRunEpochSnapshot>(processor.CreateSnapshot());

        processor.ProcessBatch(CreateBatch([CreateEntry(3)]));
        var second = Assert.IsType<UnitTestRunEpochSnapshot>(processor.CreateSnapshot());

        Assert.Equal(new uint[] { 1, 2 }, first.EventStream.Select(item => item.SequenceId));
        Assert.Equal(new uint[] { 1, 2, 3 }, second.EventStream.Select(item => item.SequenceId));
    }

    [Fact]
    public void Result_notification_and_readers_share_the_committed_snapshot()
    {
        var processor = CreateStartedProcessor();
        EventStreamProcessorUpdate? published = null;
        processor.ProcessorUpdateEventHandler += (_, args) => published = args.Update;

        var result = processor.ProcessBatch(CreateBatch([CreateStartAnchor(1), CreateEntry(2)]));

        Assert.Same(result.CurrentRun, published?.CurrentRun);
        Assert.Same(result.CurrentRun, processor.Current);
        Assert.Same(processor.Current, processor.CreateSnapshot());
        var noChange = processor.ProcessBatch(CreateBatch(
            [], producerSequenceId: 2, deletedSequenceId: 2, bufferObservedEmpty: true, readState: EventBufferReadState.Empty));
        Assert.Same(result.CurrentRun, noChange.CurrentRun);
        Assert.Same(result.CurrentRun, processor.Current);
    }

    [Fact]
    public async Task Readers_keep_previous_snapshot_until_batch_completion_is_committed()
    {
        using var time = new PausingTimeProvider();
        var processor = new EventStreamProcessor(time);
        processor.StartObservation(new(new CommunicationSessionId("session"), null, FakeTime.DefaultStart));
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1)]));
        var previous = processor.Current;
        var previousEvents = processor.AcceptedEvents;
        time.Arm();

        var writer = Task.Run(() => processor.ProcessBatch(CreateBatch(
            [CreateEntry(2), CreateCompletionAnchor(3)], bufferObservedEmpty: true)));
        try
        {
            Assert.True(time.Entered.Wait(TimeSpan.FromSeconds(5)));
            // Completion has processed the entries but has not committed the batch yet.
            var reader = Task.Run(() => (processor.Current, processor.AcceptedEvents));
            var read = await reader.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(previous, read.Current);
            Assert.Same(previousEvents, read.AcceptedEvents);
            Assert.Single(read.Current!.EventStream);
        }
        finally
        {
            time.Release.Set();
            await writer;
        }

        Assert.Equal(3, processor.Current!.EventStream.Count);
        Assert.Equal(UnitTestRunState.Completed, processor.Current.State);
        Assert.Single(previous!.EventStream);
        Assert.Single(previousEvents);
    }

    [Fact]
    public void Published_collections_cannot_be_modified_or_changed_by_reset()
    {
        var processor = CreateStartedProcessor();
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1), CreateEntry(2)]));
        var snapshot = processor.Current!;
        var accepted = processor.AcceptedEvents;

        Assert.Throws<NotSupportedException>(() => ((IList<UnitTestEventEntry>)snapshot.EventStream).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<UnitTestEventRef>)snapshot.FrameworkEvents).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<UnitTestRunNode>)snapshot.Root.Children).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<UnitTestFinding>)snapshot.Root.Findings).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<UnitTestEventEntry>)accepted).Clear());
        processor.Reset();

        Assert.Null(processor.Current);
        Assert.Empty(processor.AcceptedEvents);
        Assert.Equal(2, snapshot.EventStream.Count);
        Assert.Equal(2, accepted.Count);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("invalidate")]
    [InlineData("loss")]
    [InlineData("clear")]
    [InlineData("reset")]
    [InlineData("stop")]
    public async Task Competing_state_change_waits_for_batch_and_does_not_restore_old_run(string change)
    {
        using var time = new PausingTimeProvider();
        var processor = new EventStreamProcessor(time);
        processor.StartObservation(new(new CommunicationSessionId("session"), null, FakeTime.DefaultStart));
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1)]));
        var before = processor.Current;
        time.Arm();
        // The sequence-gap diagnostic pauses after sequence 2 has been appended.
        var batch = Task.Run(() => processor.ProcessBatch(CreateBatch([CreateEntry(2), CreateEntry(4)])));
        using var competingStarted = new ManualResetEventSlim();
        Task? competing = null;
        try
        {
            Assert.True(time.Entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Same(before, processor.Current);
            competing = Task.Run(() =>
            {
                competingStarted.Set();
                switch (change)
                {
                    case "timeout": processor.CompleteCurrentEpoch(EventStreamCompletionReason.Timeout, FakeTime.DefaultStart); break;
                    case "invalidate": processor.CompleteCurrentEpoch(EventStreamCompletionReason.TargetInvalidated, FakeTime.DefaultStart); break;
                    case "loss": processor.ApplyMonitoringState(new EventStreamState(
                        new CommunicationSessionId("session"), true, EventBufferReadState.Valid,
                        EventStreamBatchReleaseState.Completed, false, 2, ProducerSequenceId: 4)); break;
                    case "clear": processor.ClearCurrentEpoch(); break;
                    case "reset": processor.Reset(); break;
                    case "stop": processor.StopObservation(); break;
                }
            });
            Assert.True(competingStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.Same(before, processor.Current);
        }
        finally
        {
            time.Release.Set();
            await batch.WaitAsync(TimeSpan.FromSeconds(5));
            if (competing is not null) await competing.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Single(before!.EventStream);
        var batchResult = await batch;
        Assert.Equal(new uint[] { 1, 2, 4 }, batchResult.CurrentRun!.EventStream.Select(entry => entry.SequenceId));
        if (change is "clear" or "reset")
        {
            Assert.Null(processor.Current);
            Assert.Empty(processor.AcceptedEvents);
        }
        else
        {
            Assert.Equal(new uint[] { 1, 2, 4 }, processor.Current!.EventStream.Select(entry => entry.SequenceId));
            if (change == "timeout") Assert.Equal(UnitTestRunState.TimedOut, processor.Current.State);
            if (change == "invalidate") Assert.Equal(UnitTestRunState.Invalidated, processor.Current.State);
            if (change == "loss")
            {
                Assert.Equal((ushort)2, processor.Current.LostEventCount);
                Assert.Equal(EvidenceState.Incomplete, processor.Current.EvidenceState);
            }
            if (change == "stop")
            {
                var retained = processor.Current;
                processor.ProcessBatch(CreateBatch([CreateEntry(5)]));
                Assert.Same(retained, processor.Current);
            }
        }
    }

    [Fact]
    public async Task Parallel_readers_observe_only_complete_monotonic_batches()
    {
        var processor = CreateStartedProcessor();
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1)]));
        using var start = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            start.Wait();
            for (uint sequence = 2; sequence < 1602; sequence += 16)
            {
                processor.ProcessBatch(CreateBatch(Enumerable.Range(0, 16)
                    .Select(index => CreateEntry(sequence + (uint)index)).ToArray()));
            }
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait();
            var lastCount = 0;
            do
            {
                var snapshot = processor.Current!;
                Assert.True(snapshot.EventStream.Count >= lastCount);
                Assert.Equal(1, snapshot.EventStream.Count % 16);
                Assert.Equal((uint)snapshot.EventStream.Count, snapshot.EventStream[^1].SequenceId);
                lastCount = snapshot.EventStream.Count;
            } while (!writer.IsCompleted);
        })).ToArray();

        start.Set();
        await Task.WhenAll(readers.Append(writer)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1601, processor.Current!.EventStream.Count);
    }

    [Fact]
    public async Task Blocked_trace_does_not_block_state_changes_or_reorder_processor_updates()
    {
        using var sink = new PausingTraceSink();
        var processor = new EventStreamProcessor(FakeTime.Create(), activityTraceSink: sink);
        processor.StartObservation(new(new CommunicationSessionId("session"), null, FakeTime.DefaultStart));
        var updates = new List<EventStreamProcessorUpdate>();
        processor.ProcessorUpdateEventHandler += (_, args) => updates.Add(args.Update);
        var writer = Task.Run(() => processor.ProcessBatch(CreateBatch([CreateStartAnchor(1), CreateEntry(2)])));

        try
        {
            Assert.True(sink.Entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, processor.Current!.EventStream.Count);
            await Task.Run(processor.ClearCurrentEpoch).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(processor.Current);
        }
        finally
        {
            sink.Release.Set();
            await writer;
        }

        Assert.Equal(new[] { EventStreamProcessorUpdateKind.BatchProcessed, EventStreamProcessorUpdateKind.EpochCleared },
            updates.Select(update => update.Kind));
        Assert.Equal(2, updates[0].CurrentRun!.EventStream.Count);
        Assert.Null(updates[1].CurrentRun);
        Assert.Null(processor.Current);
    }

    [Fact]
    public void Reentrant_clear_is_published_after_the_batch_without_restoring_old_state()
    {
        var processor = CreateStartedProcessor();
        var kinds = new List<EventStreamProcessorUpdateKind>();
        processor.ProcessorUpdateEventHandler += (_, args) =>
        {
            if (args.Update.Kind == EventStreamProcessorUpdateKind.BatchProcessed)
            {
                processor.ClearCurrentEpoch();
            }

            kinds.Add(args.Update.Kind);
        };

        var result = processor.ProcessBatch(CreateBatch([CreateStartAnchor(1)]));

        Assert.Single(result.CurrentRun!.EventStream);
        Assert.Null(processor.Current);
        Assert.Equal(new[] { EventStreamProcessorUpdateKind.BatchProcessed, EventStreamProcessorUpdateKind.EpochCleared }, kinds);
    }

    [Fact]
    public void Throwing_subscriber_does_not_leave_notification_drain_active()
    {
        var processor = CreateStartedProcessor();
        EventHandler<EventStreamProcessorUpdateEventArgs> throwing = (_, _) => throw new InvalidOperationException("Subscriber failed.");
        processor.ProcessorUpdateEventHandler += throwing;

        Assert.Throws<InvalidOperationException>(() => processor.ProcessBatch(CreateBatch([CreateStartAnchor(1)])));
        Assert.Single(processor.Current!.EventStream);
        processor.ProcessorUpdateEventHandler -= throwing;
        var updates = new List<EventStreamProcessorUpdate>();
        processor.ProcessorUpdateEventHandler += (_, args) => updates.Add(args.Update);
        processor.ProcessBatch(CreateBatch([CreateEntry(2)]));

        Assert.Equal(2, Assert.Single(updates).CurrentRun!.EventStream.Count);
    }

    [Fact]
    public async Task Subscriber_failure_does_not_strand_an_already_queued_update()
    {
        var processor = CreateStartedProcessor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var eventCounts = new List<int>();
        processor.ProcessorUpdateEventHandler += (_, args) =>
        {
            var count = args.Update.CurrentRun!.EventStream.Count;
            eventCounts.Add(count);
            if (count == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Subscriber probe was not released.");
                }

                throw new InvalidOperationException("Subscriber failed.");
            }
        };
        var first = Task.Run(() => processor.ProcessBatch(CreateBatch([CreateStartAnchor(1)])));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() => processor.ProcessBatch(CreateBatch([CreateEntry(2)])))
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        }

        Assert.Equal(new[] { 1, 2 }, eventCounts);
        Assert.Equal(2, processor.Current!.EventStream.Count);
    }

    [Fact]
    public void Clear_current_epoch_releases_evidence_but_keeps_observation()
    {
        var processor = CreateStartedProcessor();
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1), CreateEntry(2)]));

        processor.ClearCurrentEpoch();
        var next = processor.ProcessBatch(CreateBatch([CreateStartAnchor(3)]));

        Assert.True(next.RunEpochStarted);
        Assert.Equal(new uint[] { 3 }, processor.Current?.EventStream.Select(item => item.SequenceId));
    }

    [Fact]
    public void Reset_clears_epoch_and_observation()
    {
        var processor = CreateStartedProcessor();
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1)]));

        processor.Reset();
        var result = processor.ProcessBatch(CreateBatch([CreateStartAnchor(2)]));

        Assert.Null(processor.Current);
        Assert.Empty(processor.AcceptedEvents);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.NotObserving");
    }

    [Fact]
    public void Processor_publishes_anchor_and_completion_through_batch_update()
    {
        var processor = CreateStartedProcessor();
        var updates = new List<EventStreamProcessorUpdate>();
        processor.ProcessorUpdateEventHandler += (_, args) => updates.Add(args.Update);

        var result = processor.ProcessBatch(CreateBatch(
            [
                CreateStartAnchor(1),
                CreateEntry(2, UnitTestEventType.Result, result: UnitTestResultState.Passed),
                CreateCompletionAnchor(3)
            ],
            deletedSequenceId: 3,
            bufferObservedEmpty: true,
            readState: EventBufferReadState.Empty));

        var update = Assert.Single(updates);
        Assert.Equal(EventStreamProcessorUpdateKind.BatchProcessed, update.Kind);
        Assert.Equal(EventProcessingState.Completed, update.State);
        Assert.Equal(UnitTestRunState.Completed, update.CurrentRun?.State);
        Assert.True(result.RunEpochStarted);
        Assert.True(result.RunControlCompletionAnchorObserved);
    }

    [Fact]
    public void Processor_logs_batch_and_epoch_summaries_without_each_normal_event()
    {
        var logger = new ListLogger<EventStreamProcessor>();
        var processor = CreateStartedProcessor(logger: logger);

        processor.ProcessBatch(CreateBatch(
            [
                CreateStartAnchor(1),
                CreateEntry(2, UnitTestEventType.Result, result: UnitTestResultState.Passed),
                CreateCompletionAnchor(3)
            ],
            deletedSequenceId: 3,
            bufferObservedEmpty: true,
            readState: EventBufferReadState.Empty));

        Assert.Contains(logger.Entries, entry => entry.Message.Contains("UnitTest run epoch started", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("UnitTest run epoch completed", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug
            && entry.Message.Contains("AcceptedEventCount=3", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("Event 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Process_batch_builds_suite_and_testcase_tree()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch([
            CreateStartAnchor(1),
            CreateEntry(2, UnitTestEventType.StateTransition, UnitTestOwnerScope.TestSuite, "Math", UnitTestLifecycleState.Running),
            CreateEntry(3, UnitTestEventType.StateTransition, UnitTestOwnerScope.TestCase, "Math.AddsNumbers", UnitTestLifecycleState.Running),
            CreateEntry(4, UnitTestEventType.Result, UnitTestOwnerScope.TestCase, "Math.AddsNumbers", result: UnitTestResultState.Passed)
        ]));

        var suite = Assert.Single(Assert.IsType<UnitTestRunEpochSnapshot>(result.CurrentRun).Root.Children);
        var testCase = Assert.Single(suite.Children);
        Assert.Equal(UnitTestNodeKind.TestSuite, suite.Kind);
        Assert.Equal("Math", suite.Name);
        Assert.Equal(UnitTestNodeKind.TestCase, testCase.Kind);
        Assert.Equal("AddsNumbers", testCase.Name);
        Assert.Equal(UnitTestResultState.Passed, testCase.Result);
        Assert.Equal(4u, testCase.ResultEvent?.SequenceId);
    }

    [Fact]
    public void Contract_hierarchy_excludes_orchestrators_and_completes_full_owner_evidence()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch(
            [
                CreateStartAnchor(1),
                CreateEntry(2, UnitTestEventType.StateTransition, UnitTestOwnerScope.TestSuiteOrchestrator, "UnitTest.Orchestrator", UnitTestLifecycleState.Starting),
                CreateEntry(3, UnitTestEventType.StateTransition, UnitTestOwnerScope.TestSuite, "UnitTest.Math", UnitTestLifecycleState.Starting),
                CreateEntry(4, UnitTestEventType.StateTransition, UnitTestOwnerScope.TestCaseOrchestrator, "UnitTest.Math.Orchestrator", UnitTestLifecycleState.Starting),
                CreateEntry(5, UnitTestEventType.StateTransition, UnitTestOwnerScope.TestCase, "UnitTest.Math.AddsNumbers", UnitTestLifecycleState.Starting),
                CreateEntry(6, UnitTestEventType.Result, UnitTestOwnerScope.TestCase, "UnitTest.Math.AddsNumbers", result: UnitTestResultState.Passed),
                CreateEntry(7, UnitTestEventType.StateTransition, UnitTestOwnerScope.TestCase, "UnitTest.Math.AddsNumbers", UnitTestLifecycleState.Completed),
                CreateEntry(8, UnitTestEventType.Result, UnitTestOwnerScope.TestSuite, "UnitTest.Math", result: UnitTestResultState.Passed),
                CreateEntry(9, UnitTestEventType.StateTransition, UnitTestOwnerScope.TestSuite, "UnitTest.Math", UnitTestLifecycleState.Completed),
                CreateEntry(10, UnitTestEventType.Result, result: UnitTestResultState.Passed),
                CreateCompletionAnchor(11)
            ],
            deletedSequenceId: 11,
            bufferObservedEmpty: true,
            readState: EventBufferReadState.Empty));

        var snapshot = Assert.IsType<UnitTestRunEpochSnapshot>(result.CurrentRun);
        var suite = Assert.Single(snapshot.Root.Children);
        var testCase = Assert.Single(suite.Children);
        Assert.Equal("Math", suite.Name);
        Assert.Equal("AddsNumbers", testCase.Name);
        Assert.DoesNotContain(snapshot.Root.Children, node => node.Name == "Orchestrator");
        Assert.DoesNotContain(suite.Children, node => node.Name == "Orchestrator");
        Assert.Equal(EvidenceState.Complete, snapshot.EvidenceState);
        Assert.Equal(EvidenceState.Complete, suite.EvidenceState);
        Assert.Equal(EvidenceState.Complete, testCase.EvidenceState);
    }

    [Fact]
    public void Failed_assertion_creates_finding_and_propagates_failure()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch([
            CreateStartAnchor(1),
            CreateEntry(2, UnitTestEventType.Result, UnitTestOwnerScope.TestCase, "Math.AddsNumbers", result: UnitTestResultState.Passed),
            CreateEntry(3, UnitTestEventType.Result, UnitTestOwnerScope.Assertion, "Math.AddsNumbers.ExpectedSum", result: UnitTestResultState.Failed)
        ]));

        var snapshot = Assert.IsType<UnitTestRunEpochSnapshot>(result.CurrentRun);
        var testCase = Assert.Single(Assert.Single(snapshot.Root.Children).Children);
        Assert.Equal(UnitTestFindingKind.Assertion, Assert.Single(testCase.Findings).Kind);
        Assert.Equal(UnitTestResultState.Failed, testCase.Result);
        Assert.Equal(UnitTestResultState.Passed, testCase.PlcReportedResult);
        Assert.Equal(UnitTestResultState.Failed, snapshot.Root.Result);
    }

    [Fact]
    public void Child_escalation_preserves_plc_reported_parent_results()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch([
            CreateStartAnchor(1),
            CreateEntry(2, UnitTestEventType.Result, UnitTestOwnerScope.TestCase, "UnitTest.Math.AddsNumbers", result: UnitTestResultState.Failed),
            CreateEntry(3, UnitTestEventType.Result, UnitTestOwnerScope.TestSuite, "UnitTest.Math", result: UnitTestResultState.Passed),
            CreateEntry(4, UnitTestEventType.Result, UnitTestOwnerScope.RunControl, "UnitTest", result: UnitTestResultState.Passed)
        ]));

        var snapshot = Assert.IsType<UnitTestRunEpochSnapshot>(result.CurrentRun);
        var suite = Assert.Single(snapshot.Root.Children);

        Assert.Equal(UnitTestResultState.Failed, suite.Result);
        Assert.Equal(UnitTestResultState.Passed, suite.PlcReportedResult);
        Assert.Equal(UnitTestResultState.Failed, snapshot.Root.Result);
        Assert.Equal(UnitTestResultState.Passed, snapshot.Root.PlcReportedResult);
    }

    [Fact]
    public void Aggregation_event_updates_owner_node()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch([
            CreateStartAnchor(1),
            CreateEntry(
                2,
                UnitTestEventType.Aggregation,
                UnitTestOwnerScope.TestSuite,
                "Math",
                message: """{"total":2,"inactive":0,"idle":0,"running":0,"completed":2,"passed":1,"failed":1,"error":0,"runtime":42,"lostEvents":0}""")
        ]));

        var suite = Assert.Single(Assert.IsType<UnitTestRunEpochSnapshot>(result.CurrentRun).Root.Children);
        Assert.Equal(2u, suite.Aggregation?.Total);
        Assert.Equal(1u, suite.Aggregation?.Passed);
        Assert.Equal(1u, suite.Aggregation?.Failed);
        Assert.Equal(42u, suite.Aggregation?.Runtime);
        Assert.Equal(2u, suite.AggregationEvent?.SequenceId);
    }

    [Fact]
    public void Invalid_aggregation_payload_creates_consistency_finding()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch([
            CreateStartAnchor(1),
            CreateEntry(2, UnitTestEventType.Aggregation, UnitTestOwnerScope.TestSuite, "Math", message: "not-json")
        ]));

        var suite = Assert.Single(Assert.IsType<UnitTestRunEpochSnapshot>(result.CurrentRun).Root.Children);
        Assert.Equal(UnitTestFindingKind.Consistency, Assert.Single(suite.Findings).Kind);
        Assert.Equal(EvidenceState.Conflicting, suite.EvidenceState);
    }

    [Fact]
    public void Process_batch_ignores_stale_events_and_reports_sequence_gap()
    {
        var processor = CreateStartedProcessor();
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1), CreateEntry(2)]));

        var result = processor.ProcessBatch(CreateBatch([CreateEntry(2), CreateEntry(4)]));

        Assert.Equal(new uint[] { 4 }, result.AcceptedEvents.Select(item => item.SequenceId));
        Assert.Equal(new uint[] { 1, 2, 4 }, processor.AcceptedEvents.Select(item => item.SequenceId));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.StaleEvent");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.SequenceGap");
    }

    [Fact]
    public void Lost_event_batch_is_incomplete_and_keeps_available_events()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch(
            [CreateStartAnchor(1), CreateEntry(2)],
            lostEventCount: 2));

        Assert.Equal(EventProcessingState.Incomplete, result.State);
        Assert.Equal(new uint[] { 1, 2 }, result.AcceptedEvents.Select(item => item.SequenceId));
        Assert.Equal(EvidenceState.Incomplete, result.CurrentRun?.EvidenceState);
        Assert.Equal((ushort)2, result.CurrentRun?.LostEventCount);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.LostEvents");
    }

    [Fact]
    public void Independent_lost_event_state_permanently_marks_current_epoch_incomplete()
    {
        var processor = CreateStartedProcessor();
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1), CreateEntry(2)]));
        var sessionId = new CommunicationSessionId("session");

        var lost = processor.ApplyMonitoringState(new EventStreamState(
            sessionId,
            IsMonitoring: true,
            EventBufferReadState.Valid,
            EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty: false,
            LostEventCount: 3,
            BufferFull: true));
        var recovered = processor.ApplyMonitoringState(new EventStreamState(
            sessionId,
            IsMonitoring: true,
            EventBufferReadState.Empty,
            EventStreamBatchReleaseState.Completed,
            BufferObservedEmpty: true,
            LostEventCount: 3,
            BufferFull: false));
        var continued = processor.ProcessBatch(CreateBatch([CreateEntry(3)]));

        Assert.Equal(EventProcessingState.Incomplete, lost.State);
        Assert.Equal(EvidenceState.Incomplete, lost.CurrentRun?.EvidenceState);
        Assert.Equal((ushort)3, lost.CurrentRun?.LostEventCount);
        Assert.Contains(lost.Diagnostics, diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.LostEvents");
        Assert.Equal(EvidenceState.Incomplete, recovered.CurrentRun?.EvidenceState);
        Assert.Equal((ushort)3, recovered.CurrentRun?.LostEventCount);
        Assert.Equal(new uint[] { 3 }, continued.AcceptedEvents.Select(item => item.SequenceId));
        Assert.Equal(EvidenceState.Incomplete, continued.CurrentRun?.EvidenceState);
        Assert.Equal((ushort)3, continued.CurrentRun?.LostEventCount);
    }

    [Fact]
    public void Observed_event_buffer_epoch_reset_accepts_sequences_from_new_epoch()
    {
        var processor = CreateStartedProcessor(sequenceBaseline: 471);
        var sessionId = new CommunicationSessionId("session");

        var reset = processor.ApplyMonitoringState(new EventStreamState(
            sessionId,
            IsMonitoring: true,
            EventBufferReadState.Empty,
            EventStreamBatchReleaseState.Completed,
            BufferObservedEmpty: true,
            LostEventCount: 0,
            BufferFull: false,
            ProducerSequenceId: 0,
            DeletedSequenceId: 0));
        var nextEpoch = processor.ProcessBatch(CreateBatch([
            CreateStartAnchor(1),
            CreateEntry(2)
        ]));

        Assert.Equal(EventProcessingState.Ignored, reset.State);
        Assert.Equal(new uint[] { 1, 2 }, nextEpoch.AcceptedEvents.Select(item => item.SequenceId));
        Assert.DoesNotContain(nextEpoch.Diagnostics, diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.StaleEvent");
    }

    [Fact]
    public void Not_observed_state_does_not_reset_sequence_baseline()
    {
        var processor = CreateStartedProcessor(sequenceBaseline: 471);
        processor.ApplyMonitoringState(new EventStreamState(
            new CommunicationSessionId("session"), true, EventBufferReadState.NotObserved,
            EventStreamBatchReleaseState.Idle, false, 0));

        var result = processor.ProcessBatch(CreateBatch([CreateEntry(1)]));

        Assert.Empty(result.AcceptedEvents);
        Assert.Contains(result.Diagnostics, item => item.Code == "Workflow.EventStreamProcessor.StaleEvent");
    }

    [Fact]
    public void Retained_pending_observations_keep_epoch_and_contiguous_sequence_intake()
    {
        var processor = CreateStartedProcessor(sequenceBaseline: 286);
        var first = processor.ProcessBatch(CreateBatch([CreateStartAnchor(287), CreateEntry(288)]));
        var pending = new EventStreamState(new CommunicationSessionId("session"), true,
            EventBufferReadState.Valid, EventStreamBatchReleaseState.Completed, false, 0,
            BufferFull: false, ProducerSequenceId: 652, DeletedSequenceId: 256);
        for (var i = 0; i < 3; i++)
        {
            var update = processor.ApplyMonitoringState(pending);
            Assert.Equal(first.CurrentRun?.EpochId, update.CurrentRun?.EpochId);
            Assert.DoesNotContain(update.Diagnostics, diagnostic => diagnostic.Code.Contains("Reset"));
        }
        var next = processor.ProcessBatch(CreateBatch([CreateEntry(289), CreateEntry(290)]));
        Assert.Equal(first.CurrentRun?.EpochId, next.CurrentRun?.EpochId);
        Assert.Equal(new uint[] { 289, 290 }, next.AcceptedEvents.Select(entry => entry.SequenceId));
        Assert.DoesNotContain(next.Diagnostics, diagnostic =>
            diagnostic.Code.Contains("StaleEvent") || diagnostic.Code.Contains("SequenceGap"));
    }

    [Fact]
    public void Observed_event_buffer_epoch_reset_times_out_active_run_before_next_epoch()
    {
        var processor = CreateStartedProcessor();
        processor.ProcessBatch(CreateBatch([CreateStartAnchor(1), CreateEntry(2)]));

        var reset = processor.ApplyMonitoringState(new EventStreamState(
            new CommunicationSessionId("session"),
            IsMonitoring: true,
            EventBufferReadState.Empty,
            EventStreamBatchReleaseState.Completed,
            BufferObservedEmpty: true,
            LostEventCount: 0,
            BufferFull: false,
            ProducerSequenceId: 0,
            DeletedSequenceId: 0));

        Assert.Equal(EventProcessingState.Incomplete, reset.State);
        Assert.Equal(UnitTestRunState.TimedOut, reset.CurrentRun?.State);
        Assert.Equal(EvidenceState.Incomplete, reset.CurrentRun?.EvidenceState);
    }

    [Fact]
    public void Independent_lost_event_state_invalidates_completed_epoch_evidence()
    {
        var processor = CreateStartedProcessor();
        var completed = processor.ProcessBatch(CreateBatch(
            [
                CreateStartAnchor(1),
                CreateEntry(2, UnitTestEventType.Result, result: UnitTestResultState.Passed),
                CreateCompletionAnchor(3)
            ],
            deletedSequenceId: 3,
            bufferObservedEmpty: true,
            readState: EventBufferReadState.Empty));

        var lost = processor.ApplyMonitoringState(new EventStreamState(
            new CommunicationSessionId("session"),
            IsMonitoring: true,
            EventBufferReadState.Valid,
            EventStreamBatchReleaseState.Completed,
            BufferObservedEmpty: false,
            LostEventCount: 2,
            BufferFull: true));

        Assert.Equal(UnitTestRunState.Completed, completed.CurrentRun?.State);
        Assert.Equal(EvidenceState.Complete, completed.CurrentRun?.EvidenceState);
        Assert.Equal(UnitTestRunState.Completed, lost.CurrentRun?.State);
        Assert.Equal(EvidenceState.Incomplete, lost.CurrentRun?.EvidenceState);
        Assert.Equal((ushort)2, lost.CurrentRun?.LostEventCount);
    }

    [Fact]
    public void Inconsistent_batch_is_ambiguous_but_keeps_available_events()
    {
        var processor = CreateStartedProcessor();

        var result = processor.ProcessBatch(CreateBatch(
            [CreateStartAnchor(1), CreateEntry(2)],
            readState: EventBufferReadState.Inconsistent));

        Assert.Equal(EventProcessingState.Ambiguous, result.State);
        Assert.Equal(new uint[] { 1, 2 }, result.AcceptedEvents.Select(item => item.SequenceId));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.ReadStateInconsistent");
    }

    [Fact]
    public void Processor_respects_observation_sequence_baseline()
    {
        var processor = CreateStartedProcessor(sequenceBaseline: 5);

        var result = processor.ProcessBatch(CreateBatch([
            CreateEntry(5),
            CreateStartAnchor(6),
            CreateEntry(7)
        ]));

        Assert.Equal(new uint[] { 6, 7 }, result.AcceptedEvents.Select(item => item.SequenceId));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Workflow.EventStreamProcessor.StaleEvent");
    }

    private sealed class PausingTimeProvider : TimeProvider, IDisposable
    {
        private int armed;
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public void Arm() => Interlocked.Exchange(ref armed, 1);

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Completion probe was not released.");
                }
            }

            return FakeTime.DefaultStart;
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private sealed class PausingTraceSink : IUnitTestActivityTraceSink, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public bool TryStartSession(ApplicationSessionId sessionId) => true;
        public bool TryEndSession(ApplicationSessionId sessionId) => true;

        public bool TryRecord(UnitTestActivityPayload payload, UnitTestActivityCorrelation correlation = default)
        {
            if (payload is PlcEventActivityPayload plc && plc.Event.SequenceId == 2)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Trace probe was not released.");
                }
            }

            return true;
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private static EventStreamProcessor CreateStartedProcessor(
        uint? sequenceBaseline = null,
        ILogger<EventStreamProcessor>? logger = null,
        RecordingUnitTestActivityTraceSink? activityTraceSink = null)
    {
        var processor = new EventStreamProcessor(FakeTime.Create(), logger, activityTraceSink);
        processor.StartObservation(new EventStreamObservationContext(
            new CommunicationSessionId("session"),
            sequenceBaseline,
            FakeTime.DefaultStart));
        return processor;
    }

    private static UnitTestEventBatch CreateBatch(
        IReadOnlyList<UnitTestEventEntry> entries,
        CommunicationSessionId? sessionId = null,
        uint? producerSequenceId = null,
        uint? deletedSequenceId = null,
        ushort lostEventCount = 0,
        bool bufferObservedEmpty = false,
        EventBufferReadState readState = EventBufferReadState.Valid)
    {
        return new UnitTestEventBatch(
            sessionId ?? new CommunicationSessionId("session"),
            entries,
            producerSequenceId ?? (entries.Count == 0 ? 0 : entries.Max(static entry => entry.SequenceId)),
            deletedSequenceId ?? 0,
            lostEventCount,
            bufferObservedEmpty,
            readState);
    }

    private static UnitTestEventEntry CreateStartAnchor(uint sequenceId)
    {
        return CreateEntry(
            sequenceId,
            UnitTestEventType.StateTransition,
            UnitTestOwnerScope.RunControl,
            state: UnitTestLifecycleState.Starting);
    }

    private static UnitTestEventEntry CreateCompletionAnchor(uint sequenceId)
    {
        return CreateEntry(
            sequenceId,
            UnitTestEventType.StateTransition,
            UnitTestOwnerScope.RunControl,
            state: UnitTestLifecycleState.Completed);
    }

    private static UnitTestEventEntry CreateEntry(
        uint sequenceId,
        UnitTestEventType eventType = UnitTestEventType.Info,
        UnitTestOwnerScope ownerScope = UnitTestOwnerScope.RunControl,
        string name = "FakeRunControl",
        UnitTestLifecycleState state = UnitTestLifecycleState.Running,
        UnitTestResultState result = UnitTestResultState.NotEvaluated,
        string? message = null)
    {
        return new UnitTestEventEntry(
            sequenceId,
            FakeTime.DefaultStart.AddMilliseconds(sequenceId),
            ownerScope,
            eventType,
            LogLevel.Information,
            name,
            string.Empty,
            state,
            result,
            message ?? $"Event {sequenceId}");
    }
}
