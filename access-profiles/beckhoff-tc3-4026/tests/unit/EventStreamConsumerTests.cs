using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.Analysis;
using PLCSIM.UnitTest.Core.Workflow;
using Xunit;
using static PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit.FakeEventStreamAccess;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

// Qualifies the profile-to-Core boundary with deterministic PLC-shaped data, without ADS access.
public sealed class EventStreamConsumerTests
{
    [Fact]
    public async Task LostCaseResultCannotBecomeHealthyWhenRemainingRunCompletes()
    {
        var test = new Pipeline(32);
        var events = CompleteRun().Where(e => !(e.OwnerScope == UnitTestOwnerScope.TestCase
            && e.EventType == UnitTestEventType.Result)).ToArray();
        test.Access.Value = Observation((ushort)events.Length, events[^1].SequenceId, lost: 1);
        test.Access.Payload = events;
        await test.Capture();
        test.Access.Value = Observation(0, events[^1].SequenceId, events[^1].SequenceId, lost: 1);
        await test.Capture();
        var run = test.Processor.Current!;
        Assert.Equal((ushort)1, run.LostEventCount);
        Assert.Equal(EvidenceState.Incomplete, run.EvidenceState);
        Assert.Equal(events, test.Trace.Events.Select(e => e.Event));
        Assert.Equal(events[^1].SequenceId, Assert.Single(test.Access.Writes));
        var analysis = new RunEvidenceAnalyzer(TimeProvider.System).Analyze(test.Trace.Buffer.Current!, run.EpochId);
        Assert.NotEqual(EvidenceAnalysisAssessment.Healthy, analysis.Assessment);
    }

    [Fact]
    public async Task LateChildDiagnosticAfterSuiteTimeoutRetainsItsEvidenceAndEpoch()
    {
        var test = new Pipeline(1);
        var entries = CompleteRun().ToList();
        var index = entries.FindIndex(e => e.OwnerScope == UnitTestOwnerScope.TestSuite
            && e.EventType == UnitTestEventType.Result);
        entries.Insert(index, Event(0, UnitTestOwnerScope.Framework, UnitTestEventType.Framework,
            UnitTestLifecycleState.Completing, UnitTestResultState.Error, "Test Suite execution timeout reached.")
            with { Name = "Run.Suite", Severity = LogLevel.Error });
        entries.Insert(index + 1, Event(0, UnitTestOwnerScope.Assertion, UnitTestEventType.Result,
            UnitTestLifecycleState.Running, UnitTestResultState.Failed, "Late child assertion")
            with { Name = "Run.Suite.Case.Check", Severity = LogLevel.Error });
        var events = entries.Select((e, i) => e with { SequenceId = (uint)(i + 1) }).ToArray();
        await test.Drain(events);
        await test.Empty(events[^1].SequenceId);
        var run = test.Processor.Current!;
        Assert.Equal(events, run.EventStream);
        var late = Assert.Single(test.Trace.Events.Where(e => e.Event.Message == "Late child assertion"));
        Assert.Equal(run.EpochId, late.Epoch);
        Assert.Contains(Assert.Single(Assert.Single(run.Root.Children).Children).Findings,
            finding => finding.Message == "Late child assertion");
        Assert.Contains(test.Results.SelectMany(r => r.Diagnostics), d => d.Message.Contains("execution timeout"));
    }

    [Fact]
    public async Task UnsignedMaximumRuntimeSurvivesDrainAndCoreAggregationParsing()
    {
        var test = new Pipeline(1);
        var events = CompleteRun().Select(e => e.EventType == UnitTestEventType.Aggregation
            ? e with { Message = "{\"total\":1,\"passed\":1,\"failed\":0,\"error\":0,\"runtime\":4294967295}" } : e).ToArray();
        await test.Drain(events);
        await test.Empty(events[^1].SequenceId);
        var run = test.Processor.Current!;
        Assert.Equal(EvidenceState.Complete, run.EvidenceState);
        var suite = Assert.Single(run.Root.Children);
        Assert.Equal(uint.MaxValue, suite.Aggregation!.Runtime);
        Assert.Equal(uint.MaxValue, Assert.Single(suite.Children).Aggregation!.Runtime);
        Assert.Equal(events, test.Trace.Events.Select(e => e.Event));
    }

    [Fact]
    public async Task ResetFrameworkFailureOutsideEpochKeepsOwnerAndRawEvidenceForAnalysis()
    {
        var test = new Pipeline(1);
        var lifecycle = new LifecycleObservationId("startup-reset");
        test.Trace.Lifecycle = lifecycle;
        test.Trace.TryRecord(new CommandActivityPayload(UnitTestActivityCommandKind.Reset,
            UnitTestActivityCommandPhase.Requested));
        var events = new[]
        {
            Event(1, UnitTestOwnerScope.TestSuite, UnitTestEventType.StateTransition, UnitTestLifecycleState.Reset),
            Event(2, UnitTestOwnerScope.TestCase, UnitTestEventType.Trigger, UnitTestLifecycleState.Reset),
            Event(3, UnitTestOwnerScope.Framework, UnitTestEventType.Framework, UnitTestLifecycleState.Reset,
                message: "Test Suite reset timeout reached.") with { Name = "Run.Suite", Severity = LogLevel.Error }
        };
        await test.Drain(events);
        await test.Empty(3);
        // The diagnostic reporter records processor diagnostics separately from raw PLC activities.
        foreach (var diagnostic in test.Results.SelectMany(r => r.Diagnostics))
            test.Trace.TryRecord(new DiagnosticActivityPayload(diagnostic));
        Assert.Null(test.Processor.Current);
        Assert.All(test.Trace.Events, item => Assert.Null(item.Epoch));
        var snapshot = test.Trace.Buffer.Current!;
        var analysis = new LifecycleEvidenceAnalyzer(TimeProvider.System).Analyze(snapshot, lifecycle);
        var incident = Assert.Single(analysis.Incidents.Where(i =>
            i.Classification == EvidenceIncidentClassification.LifecycleReactionFailure));
        Assert.Contains(incident.AffectedOwners, owner => owner.DisplayName == "Run.Suite");
        Assert.Contains(incident.AffectedOwners, owner => owner.DisplayName == "Run.Suite.Case");
        var package = new EvidenceAnalysisPackageBuilder().Build(analysis, snapshot);
        Assert.Contains("Test Suite reset timeout reached.", package.EventsNdjson);
        Assert.Contains("Run.Suite.Case", package.AnalysisMarkdown);
        Assert.Equal(events, test.Trace.Events.Select(item => item.Event));
    }

    [Fact]
    public async Task DecodedPlcTimestampAndOwnerReachPackageWithoutPcClockSubstitution()
    {
        var fixture = new EventStreamFixture();
        var layout = await fixture.Resolve();
        var bytes = fixture.Event(4, 1720000000123456799L);
        fixture.Text(bytes, EventStreamFixture.EventPath, "Name", "Run.Suite.Case");
        var decoded = layout.Entry.Decode(bytes);
        var test = new Pipeline(1);
        var entries = CompleteRun().ToArray();
        entries[3] = decoded;
        await test.Drain(entries);
        await test.Empty(entries[^1].SequenceId);
        var raw = Assert.Single(test.Trace.Buffer.Current!.Entries.Where(e =>
            e.Payload is PlcEventActivityPayload plc && plc.Event.SequenceId == 4));
        Assert.Equal(decoded, ((PlcEventActivityPayload)raw.Payload).Event);
        var snapshot = test.Trace.Buffer.Current!;
        var analysis = new RunEvidenceAnalyzer(TimeProvider.System).Analyze(snapshot, test.Processor.Current!.EpochId);
        var package = new EvidenceAnalysisPackageBuilder().Build(analysis, snapshot);
        Assert.Contains("2024-07-03T09:46:40.1234567", package.EventsNdjson);
        Assert.Contains("plcTimestamp", package.EventsNdjson);
        Assert.Contains("pcObservedAt", package.EventsNdjson);
    }

    [Theory]
    [InlineData(UnitTestResultState.Passed)]
    [InlineData(UnitTestResultState.Failed)]
    [InlineData(UnitTestResultState.Error)]
    [InlineData(UnitTestResultState.Abort)]
    public async Task TerminalResultsWithoutAggregationReachSharedAnalysis(UnitTestResultState result)
    {
        var test = new Pipeline(1);
        var events = CompleteRun().Where(e => e.EventType != UnitTestEventType.Aggregation)
            .Select((e, i) => e with { SequenceId = (uint)(i + 1),
                Result = e.EventType == UnitTestEventType.Result ? result : e.Result }).ToArray();
        await test.Drain(events);
        await test.Empty(events[^1].SequenceId);
        var run = test.Processor.Current!;
        Assert.Equal(result, run.Result);
        Assert.Equal(EvidenceState.Complete, run.EvidenceState);
        Assert.Equal(result == UnitTestResultState.Abort ? UnitTestRunState.Aborted : UnitTestRunState.Completed, run.State);
        var analysis = new RunEvidenceAnalyzer(TimeProvider.System).Analyze(test.Trace.Buffer.Current!, run.EpochId);
        Assert.Equal(EvidenceCompleteness.Complete, analysis.Completeness);
        Assert.DoesNotContain(analysis.Incidents, i => i.Classification == EvidenceIncidentClassification.ContractConflict);
        if (result == UnitTestResultState.Passed)
            Assert.Equal(EvidenceAnalysisAssessment.Healthy, analysis.Assessment);
        if (result == UnitTestResultState.Failed)
            Assert.Contains(analysis.Incidents, i => i.Classification == EvidenceIncidentClassification.TestFailure);
    }

    [Fact]
    public async Task MissingCaseResultIsNotHealthyDespitePassedParentEvidence()
    {
        var test = new Pipeline(1);
        var events = CompleteRun().Where(e => e.EventType != UnitTestEventType.Aggregation
            && !(e.OwnerScope == UnitTestOwnerScope.TestCase && e.EventType == UnitTestEventType.Result))
            .Select((e, i) => e with { SequenceId = (uint)(i + 1) }).ToArray();
        await test.Drain(events);
        await test.Empty(events[^1].SequenceId);
        var run = test.Processor.Current!;
        var analysis = new RunEvidenceAnalyzer(TimeProvider.System).Analyze(test.Trace.Buffer.Current!, run.EpochId);
        Assert.NotEqual(EvidenceCompleteness.Complete, analysis.Completeness);
        Assert.NotEqual(EvidenceAnalysisAssessment.Healthy, analysis.Assessment);
        Assert.Contains(analysis.Incidents, i => i.Classification == EvidenceIncidentClassification.EvidenceIncomplete);
        Assert.Equal(events, test.Trace.Events.Select(e => e.Event));
    }

    [Fact]
    public async Task CapturedAnalysisPackageIsUnaffectedByLaterTraceAndEpochChanges()
    {
        var test = new Pipeline(5);
        var events = CompleteRun();
        await test.Drain(events);
        await test.Empty(events[^1].SequenceId);
        var firstEpoch = test.Processor.Current!.EpochId;
        var snapshot = test.Trace.Buffer.Current!;
        var analysis = new RunEvidenceAnalyzer(TimeProvider.System).Analyze(snapshot, firstEpoch);
        var builder = new EvidenceAnalysisPackageBuilder();
        var before = builder.Build(analysis, snapshot);
        await test.Empty(0);
        await test.Drain(events);
        await test.Empty(events[^1].SequenceId);
        Assert.NotEqual(firstEpoch, test.Processor.Current!.EpochId);
        Assert.Equal(before, builder.Build(analysis, snapshot));
        var fromCurrentTrace = builder.Build(analysis, test.Trace.Buffer.Current!);
        Assert.DoesNotContain(test.Processor.Current.EpochId.Value, fromCurrentTrace.EventsNdjson);
        Assert.NotNull(before.HierarchyJson);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(32)]
    public async Task SplitCompletionNeedsActualEmptyAndKeepsMinimalAggregationAndTrace(int batchSize)
    {
        var test = new Pipeline(batchSize);
        var events = CompleteRun();
        await test.Drain(events);
        Assert.Equal(UnitTestRunState.Completing, test.Processor.Current!.State);
        Assert.Equal(events, test.Processor.Current.EventStream);
        await test.Empty(events[^1].SequenceId);
        var run = test.Processor.Current!;
        Assert.Equal(UnitTestRunState.Completed, run.State);
        Assert.Equal(EvidenceState.Complete, run.EvidenceState);
        Assert.Equal(UnitTestResultState.Passed, run.Result);
        var suite = Assert.Single(run.Root.Children);
        var testCase = Assert.Single(suite.Children);
        Assert.Equal(UnitTestResultState.Passed, testCase.Result);
        Assert.Null(testCase.Aggregation!.Runtime);
        Assert.Equal(events, test.Trace.Events.Select(item => item.Event));
        Assert.All(test.Trace.Events, item => Assert.Equal(run.EpochId, item.Epoch));
        Assert.Single(test.Trace.Epochs.Where(item => item.Activity == UnitTestRunEpochActivityKind.Ended));
    }

    [Fact]
    public async Task PendingPlcDeletionDoesNotResetCoreOrDuplicateEvidence()
    {
        var test = new Pipeline(32);
        await test.Drain([Event(1, UnitTestOwnerScope.RunControl, UnitTestEventType.StateTransition, UnitTestLifecycleState.Starting)]);
        var epoch = test.Processor.Current!.EpochId;
        test.Access.Value = Observation(1, 1, pending: 1);
        Assert.Null(await test.Capture());
        Assert.Equal(epoch, test.Processor.Current!.EpochId);
        Assert.Equal(UnitTestRunState.Running, test.Processor.Current.State);
        Assert.Single(test.Trace.Events);
        Assert.Single(test.Access.Writes);
        Assert.False(test.DrainState.State.BufferObservedEmpty);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task FullBufferWithOrWithoutLossStillReleasesAndTracesValidEvents(int lost)
    {
        var test = new Pipeline(32);
        var events = new[]
        {
            Event(1, UnitTestOwnerScope.RunControl, UnitTestEventType.StateTransition, UnitTestLifecycleState.Starting),
            Event(2, UnitTestOwnerScope.TestSuite, UnitTestEventType.StateTransition, UnitTestLifecycleState.Starting),
            Event((uint)(3 + lost), UnitTestOwnerScope.TestCase, UnitTestEventType.StateTransition, UnitTestLifecycleState.Starting)
        };
        test.Access.ArrayLength = 4;
        test.Access.Value = Observation(3, events[^1].SequenceId, lost: (ushort)lost, length: 4);
        test.Access.Payload = events;
        var result = await test.Capture();
        Assert.Equal(events, test.Processor.Current!.EventStream);
        Assert.Equal(events[^1].SequenceId, Assert.Single(test.Access.Writes));
        Assert.True(test.DrainState.State.BufferFull);
        Assert.Equal((ushort)lost, test.Processor.Current.LostEventCount);
        Assert.Equal(lost == 0 ? EvidenceState.Partial : EvidenceState.Incomplete, test.Processor.Current.EvidenceState);
        Assert.Equal(events, test.Trace.Events.Select(item => item.Event));
        Assert.Equal(lost > 0, result!.Diagnostics.Any(d => d.Code.EndsWith("SequenceGap", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ResetEvidenceOutsideRunIsReleasedAndTracedWithoutInventingEpoch()
    {
        var test = new Pipeline(1);
        var events = new[]
        {
            Event(1, UnitTestOwnerScope.TestCase, UnitTestEventType.Trigger, UnitTestLifecycleState.Reset),
            Event(2, UnitTestOwnerScope.TestCase, UnitTestEventType.StateTransition, UnitTestLifecycleState.Idle)
        };
        await test.Drain(events);
        await test.Empty(2);
        Assert.Null(test.Processor.Current);
        Assert.Empty(test.Processor.AcceptedEvents);
        Assert.Equal(new uint[] { 1, 2 }, test.Access.Writes);
        Assert.Equal(events, test.Trace.Events.Select(item => item.Event));
        Assert.All(test.Trace.Events, item => Assert.Null(item.Epoch));
    }

    [Fact]
    public async Task ChildAssertionAfterParentFailedResultRemainsInEpochUntilRunCompletion()
    {
        var test = new Pipeline(1);
        var events = CompleteRun().ToList();
        var parentResult = events.FindIndex(e => e.OwnerScope == UnitTestOwnerScope.TestSuite && e.EventType == UnitTestEventType.Result);
        events[parentResult] = events[parentResult] with { Result = UnitTestResultState.Failed };
        events.Insert(parentResult + 1, Event(0, UnitTestOwnerScope.Assertion, UnitTestEventType.Result,
            UnitTestLifecycleState.Running, UnitTestResultState.Failed, "Expected TRUE, actual FALSE") with { Name = "Run.Suite.Case.Check" });
        var sequenced = events.Select((e, i) => e with { SequenceId = (uint)(i + 1) }).ToArray();
        await test.Drain(sequenced);
        await test.Empty(sequenced[^1].SequenceId);
        var run = test.Processor.Current!;
        Assert.Equal(UnitTestRunState.Completed, run.State);
        Assert.Equal(sequenced, run.EventStream);
        Assert.Contains(Assert.Single(Assert.Single(run.Root.Children).Children).Findings,
            finding => finding.Kind == UnitTestFindingKind.Assertion && finding.Message == "Expected TRUE, actual FALSE");
        Assert.Equal(run.EpochId, test.Trace.Events.Single(item => item.Event.OwnerScope == UnitTestOwnerScope.Assertion).Epoch);
        var snapshot = test.Trace.Buffer.Current!;
        var analysis = new RunEvidenceAnalyzer(TimeProvider.System).Analyze(snapshot, run.EpochId);
        Assert.Contains(analysis.Incidents, i => i.Classification == EvidenceIncidentClassification.TestFailure);
        var package = new EvidenceAnalysisPackageBuilder().Build(analysis, snapshot);
        Assert.Contains("Expected TRUE, actual FALSE", package.EventsNdjson);
        Assert.Contains("Run.Suite.Case.Check", package.EventsNdjson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalResetQualifiesExistingCoreBoundaryWithoutTransportHeuristics(bool observeZero)
    {
        var test = new Pipeline(32);
        var start = Event(100, UnitTestOwnerScope.RunControl, UnitTestEventType.StateTransition, UnitTestLifecycleState.Starting);
        await test.Drain([start]);
        var oldEpoch = test.Processor.Current!.EpochId;
        if (observeZero) await test.Empty(0);
        await test.Drain([start with { SequenceId = 1 }]);
        Assert.Equal(new uint[] { 100, 1 }, test.Access.Writes);
        Assert.Equal(new uint[] { 100, 1 }, test.Trace.Events.Select(item => item.Event.SequenceId));
        // A missing zero window is a shared attribution limitation, not permission to drop/rewrite transport data.
        if (observeZero) Assert.NotEqual(oldEpoch, test.Processor.Current!.EpochId);
        else
        {
            Assert.Equal(oldEpoch, test.Processor.Current!.EpochId);
            Assert.Null(test.Trace.Events[^1].Epoch);
            Assert.Contains(test.Results.SelectMany(result => result.Diagnostics), d => d.Code.EndsWith("StaleEvent", StringComparison.Ordinal));
        }
    }

    private static UnitTestEventEntry[] CompleteRun()
    {
        const string aggregation = "{\"total\":1,\"passed\":1,\"failed\":0,\"error\":0}";
        var events = new List<UnitTestEventEntry>();
        foreach (var owner in new[] { UnitTestOwnerScope.RunControl, UnitTestOwnerScope.TestSuite, UnitTestOwnerScope.TestCase })
            events.Add(Event(0, owner, UnitTestEventType.StateTransition, UnitTestLifecycleState.Starting));
        foreach (var owner in new[] { UnitTestOwnerScope.TestCase, UnitTestOwnerScope.TestSuite, UnitTestOwnerScope.RunControl })
        {
            events.Add(Event(0, owner, UnitTestEventType.Result, UnitTestLifecycleState.Completing, UnitTestResultState.Passed));
            events.Add(Event(0, owner, UnitTestEventType.Aggregation, UnitTestLifecycleState.Completing, UnitTestResultState.Passed, aggregation));
            events.Add(Event(0, owner, UnitTestEventType.StateTransition, UnitTestLifecycleState.Completed));
        }
        return events.Select((entry, index) => entry with { SequenceId = (uint)(index + 1) }).ToArray();
    }

    private static UnitTestEventEntry Event(uint sequence, UnitTestOwnerScope owner, UnitTestEventType type,
        UnitTestLifecycleState state, UnitTestResultState result = UnitTestResultState.NotEvaluated, string message = "") =>
        new(sequence, DateTimeOffset.UnixEpoch, owner, type, LogLevel.Information,
            owner switch { UnitTestOwnerScope.RunControl => "Run", UnitTestOwnerScope.TestSuite => "Run.Suite", _ => "Run.Suite.Case" },
            "", state, result, message);

    private sealed class Pipeline
    {
        internal readonly FakeEventStreamAccess Access = new();
        internal readonly EventStreamDrain DrainState;
        internal readonly TraceSink Trace = new();
        internal readonly EventStreamProcessor Processor;
        internal readonly List<EventProcessingResult> Results = [];
        internal Pipeline(int size)
        {
            DrainState = new(new("test-session"), size);
            Processor = new(TimeProvider.System, activityTraceSink: Trace);
            Processor.StartObservation(new(new("test-session"), null, DateTimeOffset.UnixEpoch));
        }

        internal async Task<EventProcessingResult?> Capture()
        {
            var batch = await DrainState.CaptureAsync(Access, () => { }, default);
            var result = batch is null ? null : Processor.ProcessBatch(batch);
            if (result is not null) Results.Add(result);
            Processor.ApplyMonitoringState(DrainState.State with { IsMonitoring = true });
            return result;
        }

        internal async Task Drain(UnitTestEventEntry[] events)
        {
            var offset = 0;
            while (offset < events.Length)
            {
                Access.Value = Observation((ushort)(events.Length - offset), events[^1].SequenceId,
                    offset == 0 ? 0 : events[offset - 1].SequenceId, indexOut: (ushort)((250 + offset) % 256 + 1));
                Access.Payload = events.Skip(offset).ToArray();
                Assert.NotNull(await Capture());
                var nextOffset = Array.FindIndex(events, e => e.SequenceId == Access.Writes.Last()) + 1;
                Assert.InRange(nextOffset, offset + 1, events.Length);
                offset = nextOffset;
            }
        }

        internal async Task Empty(uint sequence)
        {
            Access.Value = Observation(0, sequence, sequence);
            await Capture();
        }
    }

    private sealed class TraceSink : IUnitTestActivityTraceSink
    {
        internal readonly List<(UnitTestEventEntry Event, RunEpochId? Epoch)> Events = [];
        internal readonly List<RunEpochActivityPayload> Epochs = [];
        internal readonly UnitTestActivityTrace Buffer = new(TimeProvider.System);
        internal LifecycleObservationId? Lifecycle;
        private readonly ApplicationSessionId session = new("consumer-test");
        internal TraceSink() => Buffer.StartSession(session);
        public bool TryStartSession(ApplicationSessionId sessionId) => true;
        public bool TryEndSession(ApplicationSessionId sessionId) => true;
        public bool TryRecord(UnitTestActivityPayload payload, UnitTestActivityCorrelation correlation = default)
        {
            if (payload is PlcEventActivityPayload plc) Events.Add((plc.Event, correlation.RunEpochId));
            if (payload is RunEpochActivityPayload epoch) Epochs.Add(epoch);
            Assert.True(Buffer.TryAppend(session, payload, correlation.CommunicationSessionId,
                correlation.RunEpochId, correlation.LifecycleObservationId ?? Lifecycle, correlation.EventStreamContextId, correlation.OperationId));
            return true;
        }
    }
}
