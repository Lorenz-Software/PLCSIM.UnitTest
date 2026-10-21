using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Workflow;

public sealed class AbortResultPropagationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_parent_abort_preserves_completed_failed_child(bool splitBatches)
    {
        var events = CreateEvents(UnitTestResultState.Failed, UnitTestResultState.Abort);
        var run = Process(events, splitBatches);
        var suite = Assert.Single(run.Root.Children);
        var child = Assert.Single(suite.Children);

        Assert.Equal(UnitTestLifecycleState.Completed, child.LifecycleState);
        Assert.Equal(UnitTestResultState.Failed, child.Result);
        Assert.Contains(child.Findings, f => f.Kind == UnitTestFindingKind.Assertion && f.Result == UnitTestResultState.Failed);
        Assert.Equal(UnitTestResultState.Abort, suite.PlcReportedResult);
        Assert.Equal(UnitTestResultState.Abort, suite.Result);
        Assert.Equal(UnitTestResultState.Abort, run.Result);
        Assert.Equal(UnitTestLifecycleState.Completed, run.Root.LifecycleState);
        Assert.Equal(UnitTestRunState.Aborted, run.State);
        Assert.Equal(EvidenceState.Complete, run.EvidenceState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Active_case_can_abort_without_losing_failed_assertion(bool splitBatches)
    {
        var run = Process(CreateEvents(UnitTestResultState.Abort, UnitTestResultState.Abort), splitBatches);
        var child = Assert.Single(Assert.Single(run.Root.Children).Children);

        Assert.Equal(UnitTestResultState.Abort, child.Result);
        Assert.Contains(child.Findings, f => f.Kind == UnitTestFindingKind.Assertion && f.Result == UnitTestResultState.Failed);
        Assert.Equal(UnitTestResultState.Abort, run.Result);
        Assert.Equal(UnitTestRunState.Aborted, run.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Child_error_is_not_hidden_by_reported_parent_abort(bool splitBatches)
    {
        var run = Process(CreateEvents(UnitTestResultState.Error, UnitTestResultState.Abort), splitBatches);
        Assert.Equal(UnitTestResultState.Error, run.Result);
        Assert.Equal(UnitTestResultState.Abort, run.Root.PlcReportedResult);
        Assert.NotEqual(UnitTestRunState.Aborted, run.State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Child_abort_does_not_manufacture_parent_abort(bool splitBatches, bool reportPassed)
    {
        var run = Process(CreateEvents(UnitTestResultState.Abort,
            reportPassed ? UnitTestResultState.Passed : null), splitBatches);

        Assert.Equal(reportPassed ? UnitTestResultState.Passed : (UnitTestResultState?)null, run.Root.PlcReportedResult);
        Assert.NotEqual(UnitTestResultState.Abort, run.Result);
        Assert.NotEqual(UnitTestRunState.Aborted, run.State);
        if (!reportPassed) Assert.Equal(EvidenceState.Incomplete, run.EvidenceState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Error_evidence_is_not_downgraded_by_later_owner_abort(bool assertion)
    {
        var events = CreateEvents(UnitTestResultState.Abort, UnitTestResultState.Abort);
        events[3] = events[3] with
        {
            Result = UnitTestResultState.Error,
            OwnerScope = assertion ? UnitTestOwnerScope.Assertion : UnitTestOwnerScope.TestCase,
            Name = assertion ? "Run.Suite.Case.Assertion" : "Run.Suite.Case"
        };
        var run = Process(events, splitBatches: true);
        var child = Assert.Single(Assert.Single(run.Root.Children).Children);
        Assert.Equal(UnitTestResultState.Abort, child.PlcReportedResult);
        Assert.Equal(UnitTestResultState.Error, child.Result);
        Assert.Equal(UnitTestResultState.Error, run.Result);
        Assert.NotEqual(UnitTestRunState.Aborted, run.State);
    }

    [Fact]
    public void Analysis_consistency_finding_does_not_invent_an_owner_error()
    {
        var events = CreateEvents(UnitTestResultState.Abort, UnitTestResultState.Abort);
        events[3] = events[3] with
        {
            OwnerScope = UnitTestOwnerScope.TestCase, Name = "Run.Suite.Case",
            EventType = UnitTestEventType.Aggregation, Result = UnitTestResultState.NotEvaluated,
            Message = "invalid aggregation"
        };
        var run = Process(events, splitBatches: true);
        var child = Assert.Single(Assert.Single(run.Root.Children).Children);
        Assert.Contains(child.Findings, f => f.Kind == UnitTestFindingKind.Consistency);
        Assert.Equal(EvidenceState.Conflicting, child.EvidenceState);
        Assert.Equal(UnitTestResultState.Abort, child.Result);
        Assert.Equal(UnitTestResultState.Abort, run.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Corrected_epoch_reaches_monitor_as_aborted_without_rewriting_child(bool splitBatches)
    {
        var run = Process(CreateEvents(UnitTestResultState.Failed, UnitTestResultState.Abort), splitBatches);
        var time = FakeTime.Create();
        var monitor = new TestRunMonitor(time);
        monitor.StartMonitoring(new(new CommunicationSessionId("abort-test"),
            RunConfigurationDefaults.CreateDefaultTimeoutPolicy(), null, null, time.GetUtcNow()));
        var update = monitor.ApplyEventStreamUpdate(new(
            new EventStreamState(new CommunicationSessionId("abort-test"), true,
                EventBufferReadState.Empty, EventStreamBatchReleaseState.Completed, true, 0), run));
        Assert.Equal(TestRunMonitorUpdateKind.TerminalStateReached, update.Kind);
        Assert.Equal(TestRunMonitoringState.Aborted, monitor.Current);
        Assert.Equal(UnitTestResultState.Failed, Assert.Single(Assert.Single(run.Root.Children).Children).Result);
    }

    [Fact]
    public void Explicit_abort_still_requires_completion_anchor_and_empty_buffer()
    {
        var processor = CreateProcessor();
        var events = CreateEvents(UnitTestResultState.Abort, UnitTestResultState.Abort);
        var beforeAnchor = processor.ProcessBatch(Batch(events[..^1]));
        Assert.NotEqual(UnitTestRunState.Aborted, beforeAnchor.CurrentRun!.State);
        var withAnchor = processor.ProcessBatch(Batch(events[^1..]));
        Assert.Equal(UnitTestRunState.Completing, withAnchor.CurrentRun!.State);
        var completed = processor.ProcessBatch(Batch([], events[^1].SequenceId));
        Assert.Equal(UnitTestRunState.Aborted, completed.CurrentRun!.State);
        Assert.Equal(UnitTestLifecycleState.Completed, completed.CurrentRun.Root.LifecycleState);
    }

    private static UnitTestEventEntry[] CreateEvents(UnitTestResultState childResult, UnitTestResultState? parentResult)
    {
        var events = new List<UnitTestEventEntry>();
        void Add(UnitTestOwnerScope scope, UnitTestEventType type, string name,
            UnitTestLifecycleState state, UnitTestResultState result = UnitTestResultState.NotEvaluated)
        {
            var id = (uint)events.Count + 1;
            events.Add(new(id, FakeTime.DefaultStart.AddMilliseconds(id), scope, type,
                LogLevel.Information, name, string.Empty, state, result, $"Event {id}"));
        }

        Add(UnitTestOwnerScope.RunControl, UnitTestEventType.StateTransition, "Run", UnitTestLifecycleState.Starting);
        Add(UnitTestOwnerScope.TestSuite, UnitTestEventType.StateTransition, "Run.Suite", UnitTestLifecycleState.Running);
        Add(UnitTestOwnerScope.TestCase, UnitTestEventType.StateTransition, "Run.Suite.Case", UnitTestLifecycleState.Running);
        if (parentResult == UnitTestResultState.Abort)
            Add(UnitTestOwnerScope.Assertion, UnitTestEventType.Result, "Run.Suite.Case.Assertion", UnitTestLifecycleState.Running, UnitTestResultState.Failed);
        if (childResult == UnitTestResultState.Abort)
            Add(UnitTestOwnerScope.TestCase, UnitTestEventType.StateTransition, "Run.Suite.Case", UnitTestLifecycleState.Aborting);
        Add(UnitTestOwnerScope.TestCase, UnitTestEventType.Result, "Run.Suite.Case", UnitTestLifecycleState.Completing, childResult);
        Add(UnitTestOwnerScope.TestCase, UnitTestEventType.StateTransition, "Run.Suite.Case", UnitTestLifecycleState.Completed);
        if (parentResult == UnitTestResultState.Abort)
        {
            Add(UnitTestOwnerScope.TestSuite, UnitTestEventType.StateTransition, "Run.Suite", UnitTestLifecycleState.Aborting);
            Add(UnitTestOwnerScope.RunControl, UnitTestEventType.StateTransition, "Run", UnitTestLifecycleState.Aborting);
        }
        if (parentResult.HasValue)
            Add(UnitTestOwnerScope.TestSuite, UnitTestEventType.Result, "Run.Suite", UnitTestLifecycleState.Completing, parentResult.Value);
        Add(UnitTestOwnerScope.TestSuite, UnitTestEventType.StateTransition, "Run.Suite", UnitTestLifecycleState.Completed);
        if (parentResult.HasValue)
            Add(UnitTestOwnerScope.RunControl, UnitTestEventType.Result, "Run", UnitTestLifecycleState.Completing, parentResult.Value);
        Add(UnitTestOwnerScope.RunControl, UnitTestEventType.StateTransition, "Run", UnitTestLifecycleState.Completed);
        return events.ToArray();
    }

    private static UnitTestRunEpochSnapshot Process(UnitTestEventEntry[] events, bool splitBatches)
    {
        var processor = CreateProcessor();
        if (splitBatches)
            foreach (var entry in events) processor.ProcessBatch(Batch([entry]));
        else
            processor.ProcessBatch(Batch(events));
        return processor.ProcessBatch(Batch([], events[^1].SequenceId)).CurrentRun!;
    }

    private static EventStreamProcessor CreateProcessor()
    {
        var processor = new EventStreamProcessor(FakeTime.Create());
        processor.StartObservation(new(new CommunicationSessionId("abort-test"), null, FakeTime.DefaultStart));
        return processor;
    }

    private static UnitTestEventBatch Batch(UnitTestEventEntry[] events, uint? drainedSequence = null)
        => new(new CommunicationSessionId("abort-test"), events,
            drainedSequence ?? events[^1].SequenceId, drainedSequence ?? 0, 0,
            drainedSequence.HasValue, drainedSequence.HasValue ? EventBufferReadState.Empty : EventBufferReadState.Valid);
}
