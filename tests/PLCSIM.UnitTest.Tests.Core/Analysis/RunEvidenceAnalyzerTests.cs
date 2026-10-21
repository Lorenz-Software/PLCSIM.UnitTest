using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.Analysis;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Analysis;

public sealed class RunEvidenceAnalyzerTests
{
    [Fact]
    public void Completed_passing_run_produces_a_healthy_focus_specific_summary()
    {
        var testCase = Node("case", UnitTestNodeKind.TestCase);
        var suite = Node("suite", UnitTestNodeKind.TestSuite, children: [testCase]);
        var run = Run(root: Node(children: [suite]));

        var analysis = Analyzer().Analyze(Trace(run), run.EpochId);

        Assert.Equal(EvidenceAnalysisAssessment.Healthy, analysis.Assessment);
        Assert.Empty(analysis.Incidents);
        Assert.Contains("1 TestSuite(s) and 1 TestCase(s)", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("No UnitTest failure", analysis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Active_run_remains_inconclusive_without_inventing_a_failure()
    {
        var run = Run(state: UnitTestRunState.Running, result: UnitTestResultState.NotEvaluated);

        var analysis = Analyzer().Analyze(Trace(run), run.EpochId);

        Assert.Equal(EvidenceAnalysisAssessment.Inconclusive, analysis.Assessment);
        Assert.Empty(analysis.Incidents);
        Assert.Contains("has not reached a terminal state", analysis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_passed_run_produces_no_incidents()
    {
        var run = Run();

        var analysis = Analyzer().Analyze(Trace(run), run.EpochId);

        Assert.Equal(EvidenceCompleteness.Complete, analysis.Completeness);
        Assert.Equal(EvidenceAnalysisScope.Run, analysis.Scope);
        Assert.Equal(run.EpochId, analysis.RunEpochId);
        Assert.Empty(analysis.Incidents);
    }

    [Fact]
    public void Requested_epoch_is_selected_from_multiple_retained_runs()
    {
        var first = Run("first", result: UnitTestResultState.Passed);
        var second = Run("second", result: UnitTestResultState.Failed, root: Node(result: UnitTestResultState.Failed));
        var trace = Trace(first, additionalRun: second);

        var firstAnalysis = Analyzer().Analyze(trace, first.EpochId);
        var secondAnalysis = Analyzer().Analyze(trace, second.EpochId);

        Assert.Equal(first.EpochId, firstAnalysis.RunEpochId);
        Assert.Equal(1, firstAnalysis.AnalyzedFromSequence);
        Assert.Equal(2, firstAnalysis.AnalyzedThroughSequence);
        Assert.DoesNotContain(firstAnalysis.Incidents, incident => incident.Classification == EvidenceIncidentClassification.TestFailure);
        Assert.Equal(second.EpochId, secondAnalysis.RunEpochId);
        Assert.Equal(3, secondAnalysis.AnalyzedFromSequence);
        Assert.Equal(4, secondAnalysis.AnalyzedThroughSequence);
        Assert.EndsWith(":4", secondAnalysis.Id.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_requested_epoch_produces_unavailable_analysis()
    {
        var run = Run();

        var analysis = Analyzer().Analyze(Trace(run), new RunEpochId("missing"));

        Assert.Equal(EvidenceCompleteness.Unavailable, analysis.Completeness);
        var incident = Assert.Single(analysis.Incidents);
        Assert.Equal(EvidenceIncidentClassification.EvidenceIncomplete, incident.Classification);
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "run-snapshot-missing");
    }

    [Fact]
    public void Aborted_run_is_reported_as_information_not_failure()
    {
        var run = Run(
            state: UnitTestRunState.Aborted,
            result: UnitTestResultState.Abort,
            root: Node(result: UnitTestResultState.Abort, lifecycle: UnitTestLifecycleState.Aborted));

        var analysis = Analyzer().Analyze(Trace(run), run.EpochId);

        var incident = Assert.Single(analysis.Incidents);
        Assert.Equal(EvidenceIncidentClassification.RunAborted, incident.Classification);
        Assert.Equal(EvidenceIncidentSeverity.Information, incident.Severity);
    }

    [Fact]
    public void Timed_out_run_reports_run_observation_timeout()
    {
        var run = Run(
            state: UnitTestRunState.TimedOut,
            evidence: EvidenceState.Incomplete,
            root: Node(result: UnitTestResultState.NotEvaluated, lifecycle: UnitTestLifecycleState.Running, terminalEvidence: false));

        var analysis = Analyzer().Analyze(Trace(run), run.EpochId);

        Assert.Contains(analysis.Incidents, incident =>
            incident.Classification == EvidenceIncidentClassification.RunObservationTimeout);
        Assert.Contains(analysis.Incidents, incident =>
            incident.Classification == EvidenceIncidentClassification.EvidenceIncomplete);
    }

    [Fact]
    public void Failed_finding_is_mapped_to_owner_and_plc_event_reference()
    {
        var finding = new UnitTestFinding(
            UnitTestFindingKind.Assertion,
            UnitTestResultState.Failed,
            "AssertEqual",
            "Expected 1 but observed 0.",
            new UnitTestEventRef(7));
        var testCase = Node(
            "case",
            UnitTestNodeKind.TestCase,
            UnitTestResultState.Failed,
            findings: [finding]);
        var suite = Node(
            "suite",
            UnitTestNodeKind.TestSuite,
            UnitTestResultState.Failed,
            children: [testCase]);
        var run = Run(result: UnitTestResultState.Failed, root: Node(result: UnitTestResultState.Failed, children: [suite]));

        var analysis = Analyzer().Analyze(Trace(run, plcEvent: PlcEvent(7)), run.EpochId);

        var incident = Assert.Single(analysis.Incidents.Where(incident =>
            incident.Classification == EvidenceIncidentClassification.TestFailure));
        Assert.Contains(incident.AffectedOwners, owner => owner.Id == testCase.Id);
        Assert.Contains(incident.ObservedFacts.SelectMany(fact => fact.Evidence), reference =>
            reference.PlcEventSequenceId == 7);
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "first-failing-assertion");
        Assert.Contains(incident.SuggestedChecks, check =>
            check.Message.Contains("AssertEqual", StringComparison.Ordinal));
        Assert.Contains("1 failed assertion finding(s)", incident.Summary, StringComparison.Ordinal);
        Assert.Contains("3 owner(s) with a failed or erroneous effective PC result", incident.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("4 failed finding(s)", incident.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_uses_the_snapshot_of_each_referenced_run_epoch_activity()
    {
        var finding = new UnitTestFinding(
            UnitTestFindingKind.Assertion,
            UnitTestResultState.Failed,
            "AssertEqual",
            "Expected TRUE but observed FALSE.",
            new UnitTestEventRef(7));
        var partialRun = Run(
            state: UnitTestRunState.Running,
            result: UnitTestResultState.Failed,
            evidence: EvidenceState.Partial,
            root: Node(
                result: UnitTestResultState.Failed,
                evidence: EvidenceState.Partial,
                children: [Node("case", UnitTestNodeKind.TestCase, UnitTestResultState.Failed, evidence: EvidenceState.Partial, findings: [finding])]));
        var completedRun = Run(
            state: UnitTestRunState.Completed,
            result: UnitTestResultState.Failed,
            evidence: EvidenceState.Complete,
            root: Node(
                result: UnitTestResultState.Failed,
                children: [Node("case", UnitTestNodeKind.TestCase, UnitTestResultState.Failed, findings: [finding])]));
        var entries = new UnitTestActivityEntry[]
        {
            Activity(1, partialRun, UnitTestRunEpochActivityKind.Started),
            Activity(2, partialRun, UnitTestRunEpochActivityKind.Updated),
            new(
                3,
                Timestamp().AddMilliseconds(3),
                new CommunicationSessionId("communication"),
                completedRun.EpochId,
                null,
                new EventStreamContextId("stream"),
                null,
                new PlcEventActivityPayload(PlcEvent(7))),
            Activity(4, completedRun, UnitTestRunEpochActivityKind.Ended)
        };
        var trace = new UnitTestActivityTraceSnapshot(
            new ApplicationSessionId("session"),
            Timestamp(),
            Timestamp().AddSeconds(5),
            Timestamp().AddSeconds(5),
            IsCaptureActive: false,
            entries[0].Sequence,
            entries[^1].Sequence,
            EvictedEntryCount: 0,
            entries);

        var analysis = Analyzer().Analyze(trace, completedRun.EpochId);

        var incident = Assert.Single(analysis.Incidents.Where(item =>
            item.Classification == EvidenceIncidentClassification.TestFailure));
        var updated = Assert.Single(incident.Timeline.Where(item =>
            item.Evidence.Any(reference => reference.ActivitySequence == 2)));
        Assert.Equal(Timestamp().AddSeconds(4), updated.Timestamp);
        Assert.Equal("RunEpoch Updated: Running/Failed/Partial.", updated.Message);
        var ended = Assert.Single(incident.Timeline.Where(item =>
            item.Evidence.Any(reference => reference.ActivitySequence == 4)));
        Assert.Equal("RunEpoch Ended: Completed/Failed/Complete.", ended.Message);
    }

    [Fact]
    public void Framework_timeout_is_the_root_cause_and_recovery_results_are_not_test_failures()
    {
        var frameworkFinding = new UnitTestFinding(
            UnitTestFindingKind.Framework,
            UnitTestResultState.NotEvaluated,
            "RunControl.Timeout",
            "Run-control run timeout reached.",
            new UnitTestEventRef(7));
        var testCase = Node(
            "case",
            UnitTestNodeKind.TestCase,
            UnitTestResultState.Abort,
            lifecycle: UnitTestLifecycleState.Aborted,
            findings: [OwnerFinding(8, UnitTestResultState.Abort, "Case aborted during recovery.")]);
        var suite = Node(
            "suite",
            UnitTestNodeKind.TestSuite,
            UnitTestResultState.Abort,
            lifecycle: UnitTestLifecycleState.Aborted,
            findings: [OwnerFinding(9, UnitTestResultState.Abort, "Suite aborted during recovery.")],
            children: [testCase]);
        var root = Node(
            result: UnitTestResultState.Error,
            lifecycle: UnitTestLifecycleState.Completed,
            findings:
            [
                frameworkFinding,
                OwnerFinding(10, UnitTestResultState.Error, "RunControl completed with Error.")
            ],
            children: [suite]);
        var run = Run(result: UnitTestResultState.Error, root: root);
        var plcClock = Timestamp().AddHours(-23);
        var trace = Trace(
            run,
            plcEvents:
            [
                PlcEvent(6, plcClock, UnitTestOwnerScope.TestCase, UnitTestEventType.StateTransition, LogLevel.Information, "Run.Suite.Case", UnitTestLifecycleState.Running, UnitTestResultState.NotEvaluated, "Case running."),
                PlcEvent(7, plcClock.AddMilliseconds(1), UnitTestOwnerScope.Framework, UnitTestEventType.Framework, LogLevel.Error, "RunControl.Timeout", UnitTestLifecycleState.Running, UnitTestResultState.NotEvaluated, "Run-control run timeout reached."),
                PlcEvent(8, plcClock.AddMilliseconds(2), UnitTestOwnerScope.TestCase, UnitTestEventType.Result, LogLevel.Warning, "Run.Suite.Case", UnitTestLifecycleState.Aborted, UnitTestResultState.Abort, "Case aborted during recovery."),
                PlcEvent(9, plcClock.AddMilliseconds(3), UnitTestOwnerScope.TestSuite, UnitTestEventType.Result, LogLevel.Warning, "Run.Suite", UnitTestLifecycleState.Aborted, UnitTestResultState.Abort, "Suite aborted during recovery."),
                PlcEvent(10, plcClock.AddMilliseconds(4), UnitTestOwnerScope.RunControl, UnitTestEventType.Result, LogLevel.Error, "Run", UnitTestLifecycleState.Completed, UnitTestResultState.Error, "RunControl completed with Error.")
            ]);

        var analysis = Analyzer().Analyze(trace, run.EpochId);

        var incident = Assert.Single(analysis.Incidents.Where(item =>
            item.Classification == EvidenceIncidentClassification.LifecycleReactionFailure));
        Assert.DoesNotContain(analysis.Incidents, item =>
            item.Classification == EvidenceIncidentClassification.TestFailure);
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "plc-framework-timeout");
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "recovery-consequence");
        Assert.Contains(incident.Timeline.SelectMany(item => item.Evidence), reference =>
            reference.PlcEventSequenceId == 6);
        Assert.Contains(incident.Timeline.SelectMany(item => item.Evidence), reference =>
            reference.PlcEventSequenceId == 7);
        Assert.True(incident.OpenedAt > plcClock.AddHours(22));
        Assert.True(incident.ClosedAt - incident.OpenedAt < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Lost_events_and_missing_child_result_report_incomplete_evidence()
    {
        var testCase = Node(
            "missing",
            UnitTestNodeKind.TestCase,
            UnitTestResultState.NotEvaluated,
            evidence: EvidenceState.Incomplete,
            terminalEvidence: false);
        var run = Run(
            evidence: EvidenceState.Incomplete,
            lostEvents: 4,
            root: Node(evidence: EvidenceState.Incomplete, children: [testCase]));

        var analysis = Analyzer().Analyze(Trace(run), run.EpochId);

        Assert.Equal(EvidenceCompleteness.Incomplete, analysis.Completeness);
        var incident = Assert.Single(analysis.Incidents.Where(incident =>
            incident.Classification == EvidenceIncidentClassification.EvidenceIncomplete));
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "lost-events");
        Assert.Contains(incident.AffectedOwners, owner => owner.Id == testCase.Id);
    }

    [Fact]
    public void Stronger_child_result_reports_contract_conflict()
    {
        var child = Node("case", UnitTestNodeKind.TestCase, UnitTestResultState.Error);
        var run = Run(
            result: UnitTestResultState.Passed,
            evidence: EvidenceState.Conflicting,
            root: Node(result: UnitTestResultState.Passed, evidence: EvidenceState.Conflicting, children: [child]));

        var analysis = Analyzer().Analyze(Trace(run), run.EpochId);

        var incident = Assert.Single(analysis.Incidents.Where(incident =>
            incident.Classification == EvidenceIncidentClassification.ContractConflict));
        Assert.Equal(EvidenceCompleteness.Complete, analysis.Completeness);
        Assert.Equal(EvidenceCompleteness.Complete, incident.Completeness);
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "hierarchy-result-conflict");
    }

    [Fact]
    public void Missing_evidence_check_names_only_the_missing_terminal_event_types()
    {
        var testCase = Node("case", UnitTestNodeKind.TestCase) with
        {
            ResultEvent = null,
            AggregationEvent = null
        };
        var run = Run(
            evidence: EvidenceState.Incomplete,
            root: Node(evidence: EvidenceState.Incomplete, children: [testCase]));

        var analysis = Analyzer().Analyze(Trace(run), run.EpochId);
        var incident = Assert.Single(analysis.Incidents.Where(item =>
            item.Classification == EvidenceIncidentClassification.EvidenceIncomplete));
        var check = Assert.Single(incident.SuggestedChecks);

        Assert.Contains("missing Result, Aggregation evidence", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("completion events", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Timed_out_run_reports_bounded_late_plc_completion_without_changing_the_run_outcome()
    {
        var run = Run(
            state: UnitTestRunState.TimedOut,
            result: UnitTestResultState.NotEvaluated,
            evidence: EvidenceState.Incomplete,
            root: Node(
                result: UnitTestResultState.NotEvaluated,
                lifecycle: UnitTestLifecycleState.Running,
                evidence: EvidenceState.Incomplete,
                terminalEvidence: false));
        var lateEvents = new[]
        {
            PlcEvent(100, Timestamp().AddSeconds(6), UnitTestOwnerScope.RunControl, UnitTestEventType.Result, LogLevel.Information, "RunControl", UnitTestLifecycleState.Completing, UnitTestResultState.Passed, "Passed."),
            PlcEvent(101, Timestamp().AddSeconds(6), UnitTestOwnerScope.RunControl, UnitTestEventType.Aggregation, LogLevel.Information, "RunControl", UnitTestLifecycleState.Completing, UnitTestResultState.Passed, "Passed."),
            PlcEvent(102, Timestamp().AddSeconds(6), UnitTestOwnerScope.RunControl, UnitTestEventType.StateTransition, LogLevel.Information, "RunControl", UnitTestLifecycleState.Completed, UnitTestResultState.Passed, "Completed.")
        };

        var trace = Trace(run, latePlcEvents: lateEvents);
        var analysis = Analyzer().Analyze(trace, run.EpochId);
        var timeout = Assert.Single(analysis.Incidents.Where(item =>
            item.Classification == EvidenceIncidentClassification.RunObservationTimeout));

        Assert.Equal(UnitTestRunState.TimedOut, timeout.Run?.State);
        Assert.Equal(EvidenceCompleteness.Incomplete, analysis.Completeness);
        Assert.Equal(trace.NewestSequence, analysis.AnalyzedThroughSequence);
        Assert.Equal(3, timeout.ObservedFacts.Count(fact => fact.Code == "late-run-terminal-evidence"));
        Assert.Contains(timeout.DeterministicConclusions, conclusion => conclusion.Code == "late-plc-completion-observed");
        Assert.Contains("PLC later published terminal RunControl evidence", analysis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Contradictory_parent_result_reports_plc_value_and_effective_escalation()
    {
        var child = Node(
            "case",
            UnitTestNodeKind.TestCase,
            UnitTestResultState.Failed,
            plcReportedResult: UnitTestResultState.Failed);
        var suite = Node(
            "suite",
            UnitTestNodeKind.TestSuite,
            UnitTestResultState.Failed,
            plcReportedResult: UnitTestResultState.Passed,
            children: [child]);
        var root = Node(
            result: UnitTestResultState.Failed,
            plcReportedResult: UnitTestResultState.Passed,
            children: [suite]);
        var run = Run(result: UnitTestResultState.Failed, root: root);

        var analysis = Analyzer().Analyze(Trace(run), run.EpochId);

        var incident = Assert.Single(analysis.Incidents.Where(item =>
            item.Classification == EvidenceIncidentClassification.ContractConflict));
        Assert.Contains(incident.ObservedFacts, fact =>
            fact.Code == "hierarchy-result-conflict"
            && fact.Message.Contains("PLC-reported result 'Passed'", StringComparison.Ordinal)
            && fact.Message.Contains("effective PC result is 'Failed'", StringComparison.Ordinal));
        Assert.Equal(UnitTestResultState.Passed, incident.Run?.PlcReportedResult);
        Assert.Contains("PLC-reported result 'Passed'", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("effective PC result 'Failed'", analysis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_terminal_evidence_is_localized_to_affected_branch()
    {
        var missingCase = Node(
            "missing",
            UnitTestNodeKind.TestCase,
            UnitTestResultState.NotEvaluated,
            evidence: EvidenceState.Incomplete,
            terminalEvidence: false);
        var affectedSuite = Node("affected", UnitTestNodeKind.TestSuite, children: [missingCase]);
        var healthyCase = Node("healthy-case", UnitTestNodeKind.TestCase);
        var healthySuite = Node("healthy-suite", UnitTestNodeKind.TestSuite, children: [healthyCase]);
        var run = Run(
            evidence: EvidenceState.Incomplete,
            root: Node(evidence: EvidenceState.Incomplete, children: [affectedSuite, healthySuite]));

        var analysis = Analyzer().Analyze(Trace(run), run.EpochId);

        var incident = Assert.Single(analysis.Incidents.Where(item =>
            item.Classification == EvidenceIncidentClassification.EvidenceIncomplete));
        Assert.Equal(3, incident.AffectedOwners.Count);
        Assert.Contains(incident.AffectedOwners, owner => owner.Id == missingCase.Id);
        Assert.Contains(incident.AffectedOwners, owner => owner.Id == affectedSuite.Id);
        Assert.Contains(incident.AffectedOwners, owner => owner.Id == run.Root.Id);
        Assert.DoesNotContain(incident.AffectedOwners, owner => owner.Id == healthyCase.Id || owner.Id == healthySuite.Id);
        Assert.Single(incident.ObservedFacts.Where(fact => fact.Code == "owner-terminal-evidence-missing"));
    }

    [Fact]
    public void Producer_loss_reduces_global_confidence_without_claiming_owner_evidence_is_missing()
    {
        var capacityFinding = new UnitTestFinding(
            UnitTestFindingKind.Framework,
            UnitTestResultState.NotEvaluated,
            "UnitTestEventBuffer",
            "UnitTest event buffer almost full. Further events may be lost.",
            new UnitTestEventRef(7));
        var run = Run(
            evidence: EvidenceState.Incomplete,
            lostEvents: 4,
            root: Node(evidence: EvidenceState.Incomplete, findings: [capacityFinding], children: [Node("case", UnitTestNodeKind.TestCase, evidence: EvidenceState.Incomplete)]));
        var capacityEvent = PlcEvent(
            7,
            Timestamp().AddSeconds(2),
            UnitTestOwnerScope.Framework,
            UnitTestEventType.Framework,
            LogLevel.Error,
            "UnitTestEventBuffer",
            UnitTestLifecycleState.Uninitialized,
            UnitTestResultState.NotEvaluated,
            capacityFinding.Message);

        var analysis = Analyzer().Analyze(Trace(run, plcEvent: capacityEvent), run.EpochId);

        Assert.Contains(analysis.Incidents, item => item.Classification == EvidenceIncidentClassification.EventBufferCapacity);
        Assert.DoesNotContain(analysis.Incidents, item => item.Classification == EvidenceIncidentClassification.LifecycleReactionFailure);
        var incomplete = Assert.Single(analysis.Incidents.Where(item => item.Classification == EvidenceIncidentClassification.EvidenceIncomplete));
        Assert.Empty(incomplete.AffectedOwners);
        Assert.DoesNotContain(incomplete.ObservedFacts, fact => fact.Code == "owner-terminal-evidence-missing");
    }

    [Fact]
    public void Timed_out_run_describes_effective_result_as_partial_not_terminal()
    {
        var run = Run(
            state: UnitTestRunState.TimedOut,
            result: UnitTestResultState.Passed,
            evidence: EvidenceState.Incomplete,
            root: Node(
                result: UnitTestResultState.Passed,
                lifecycle: UnitTestLifecycleState.Running,
                evidence: EvidenceState.Incomplete,
                terminalEvidence: false,
                plcReportedResult: null));

        var analysis = Analyzer().Analyze(Trace(run), run.EpochId);

        Assert.Contains("timed out before complete terminal evidence", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("No PLC-reported terminal RunControl result", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("last effective PC-side partial result was 'Passed'", analysis.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("ended in state 'TimedOut' with result 'Passed'", analysis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_result_escalation_is_incomplete_evidence_not_test_failure_or_conflict()
    {
        var child = Node("case", UnitTestNodeKind.TestCase, UnitTestResultState.Passed);
        var root = Node(
            result: UnitTestResultState.NotEvaluated,
            evidence: EvidenceState.Incomplete,
            terminalEvidence: false,
            children: [child]);
        var run = Run(
            state: UnitTestRunState.TimedOut,
            result: UnitTestResultState.Error,
            evidence: EvidenceState.Incomplete,
            root: root);

        var analysis = Analyzer().Analyze(Trace(run), run.EpochId);

        Assert.Contains(analysis.Incidents, incident =>
            incident.Classification == EvidenceIncidentClassification.EvidenceIncomplete);
        Assert.DoesNotContain(analysis.Incidents, incident =>
            incident.Classification is EvidenceIncidentClassification.TestFailure
                or EvidenceIncidentClassification.ContractConflict);
    }

    [Fact]
    public void Invalidated_run_is_incomplete_even_when_snapshot_evidence_claims_complete()
    {
        var run = Run(state: UnitTestRunState.Invalidated);

        var analysis = Analyzer().Analyze(Trace(run), run.EpochId);

        Assert.Equal(EvidenceCompleteness.Incomplete, analysis.Completeness);
        var incident = Assert.Single(analysis.Incidents);
        Assert.Equal(EvidenceIncidentClassification.EvidenceIncomplete, incident.Classification);
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "run-terminal-state");
    }

    [Fact]
    public void Eviction_only_marks_run_incomplete_when_start_boundary_is_missing()
    {
        var run = Run();
        var complete = Analyzer().Analyze(Trace(run, evicted: 5, includeStart: true), run.EpochId);
        var incomplete = Analyzer().Analyze(Trace(run, evicted: 5, includeStart: false), run.EpochId);

        Assert.Equal(EvidenceCompleteness.Complete, complete.Completeness);
        Assert.Equal(EvidenceCompleteness.Incomplete, incomplete.Completeness);
        Assert.Contains(incomplete.Incidents, incident =>
            incident.Classification == EvidenceIncidentClassification.EvidenceIncomplete);
    }

    [Theory]
    [InlineData(UnitTestResultState.Abort, UnitTestResultState.Failed, false)]
    [InlineData(UnitTestResultState.Abort, UnitTestResultState.Error, true)]
    [InlineData(UnitTestResultState.Passed, UnitTestResultState.Abort, true)]
    [InlineData(UnitTestResultState.Failed, UnitTestResultState.Abort, true)]
    [InlineData(UnitTestResultState.Error, UnitTestResultState.Abort, false)]
    public void Abort_is_checked_as_explicit_outcome_not_severity(
        UnitTestResultState parentResult, UnitTestResultState childResult, bool conflict)
    {
        var child = Node("case", UnitTestNodeKind.TestCase, childResult);
        var run = Run(result: parentResult,
            state: parentResult == UnitTestResultState.Abort ? UnitTestRunState.Aborted : UnitTestRunState.Completed,
            root: Node(result: parentResult, children: [child]));
        var analysis = Analyzer().Analyze(Trace(run), run.EpochId);
        Assert.Equal(conflict, analysis.Incidents.Any(i => i.Classification == EvidenceIncidentClassification.ContractConflict));
        if (parentResult == UnitTestResultState.Abort && childResult == UnitTestResultState.Failed)
        {
            Assert.Contains(analysis.Incidents, i => i.Classification == EvidenceIncidentClassification.RunAborted);
            Assert.Contains(analysis.Incidents, i => i.Title == "UnitTest failures observed");
        }
    }

    [Fact]
    public void Missing_parent_result_with_aborted_child_remains_incomplete()
    {
        var root = Node(result: UnitTestResultState.NotEvaluated,
            children: [Node("case", UnitTestNodeKind.TestCase, UnitTestResultState.Abort)])
            with { PlcReportedResult = null, ResultEvent = null };
        var run = Run(result: UnitTestResultState.NotEvaluated, evidence: EvidenceState.Incomplete, root: root);
        var analysis = Analyzer().Analyze(Trace(run), run.EpochId);
        Assert.Equal(EvidenceCompleteness.Incomplete, analysis.Completeness);
        Assert.DoesNotContain(analysis.Incidents, i => i.Classification == EvidenceIncidentClassification.RunAborted);
    }

    private static RunEvidenceAnalyzer Analyzer() => new(FakeTime.Create());

    private static UnitTestActivityTraceSnapshot Trace(
        UnitTestRunEpochSnapshot run,
        UnitTestRunEpochSnapshot? additionalRun = null,
        UnitTestEventEntry? plcEvent = null,
        IReadOnlyList<UnitTestEventEntry>? plcEvents = null,
        IReadOnlyList<UnitTestEventEntry>? latePlcEvents = null,
        long evicted = 0,
        bool includeStart = true)
    {
        var entries = new List<UnitTestActivityEntry>();
        long sequence = evicted + 1;
        if (includeStart)
        {
            entries.Add(Activity(sequence++, run, UnitTestRunEpochActivityKind.Started));
        }

        foreach (var entry in plcEvents ?? (plcEvent is null ? [] : [plcEvent]))
        {
            entries.Add(new UnitTestActivityEntry(
                sequence++,
                Timestamp().AddMilliseconds(sequence),
                new CommunicationSessionId("communication"),
                run.EpochId,
                null,
                new EventStreamContextId("stream"),
                null,
                new PlcEventActivityPayload(entry)));
        }

        entries.Add(Activity(sequence++, run, UnitTestRunEpochActivityKind.Ended));
        foreach (var entry in latePlcEvents ?? [])
        {
            entries.Add(new UnitTestActivityEntry(
                sequence++,
                Timestamp().AddMilliseconds(sequence),
                new CommunicationSessionId("communication"),
                null,
                null,
                new EventStreamContextId("stream"),
                null,
                new PlcEventActivityPayload(entry)));
        }

        if (additionalRun is not null)
        {
            entries.Add(Activity(sequence++, additionalRun, UnitTestRunEpochActivityKind.Started));
            entries.Add(Activity(sequence++, additionalRun, UnitTestRunEpochActivityKind.Ended));
        }

        return new UnitTestActivityTraceSnapshot(
            new ApplicationSessionId("session"),
            Timestamp(),
            Timestamp().AddSeconds(5),
            Timestamp().AddSeconds(5),
            IsCaptureActive: false,
            entries[0].Sequence,
            entries[^1].Sequence,
            evicted,
            entries.AsReadOnly());
    }

    private static UnitTestActivityEntry Activity(
        long sequence,
        UnitTestRunEpochSnapshot run,
        UnitTestRunEpochActivityKind activity)
    {
        return new UnitTestActivityEntry(
            sequence,
            activity == UnitTestRunEpochActivityKind.Started ? Timestamp() : Timestamp().AddSeconds(4),
            new CommunicationSessionId("communication"),
            run.EpochId,
            null,
            new EventStreamContextId("stream"),
            null,
            new RunEpochActivityPayload(activity, run));
    }

    private static UnitTestRunEpochSnapshot Run(
        string id = "run",
        UnitTestRunState state = UnitTestRunState.Completed,
        UnitTestResultState result = UnitTestResultState.Passed,
        EvidenceState evidence = EvidenceState.Complete,
        ushort lostEvents = 0,
        UnitTestRunNode? root = null)
    {
        return new UnitTestRunEpochSnapshot(
            new RunEpochId(id),
            state,
            result,
            evidence,
            Timestamp(),
            Timestamp().AddSeconds(4),
            [],
            [],
            root ?? Node(result: result, evidence: evidence),
            lostEvents);
    }

    private static UnitTestRunNode Node(
        string id = "run-control",
        UnitTestNodeKind kind = UnitTestNodeKind.RunControl,
        UnitTestResultState result = UnitTestResultState.Passed,
        UnitTestLifecycleState lifecycle = UnitTestLifecycleState.Completed,
        EvidenceState evidence = EvidenceState.Complete,
        bool terminalEvidence = true,
        IReadOnlyList<UnitTestFinding>? findings = null,
        IReadOnlyList<UnitTestRunNode>? children = null,
        UnitTestResultState? plcReportedResult = null)
    {
        return new UnitTestRunNode(
            new UnitTestNodeId(id),
            id,
            id,
            kind,
            lifecycle,
            result,
            evidence,
            null,
            new UnitTestEventRef(1),
            terminalEvidence ? new UnitTestEventRef(2) : null,
            terminalEvidence ? new UnitTestEventRef(3) : null,
            terminalEvidence ? new UnitTestEventRef(4) : null,
            Timestamp(),
            Timestamp().AddSeconds(4),
            findings ?? [],
            children ?? [])
        {
            PlcReportedResult = terminalEvidence ? plcReportedResult ?? result : null
        };
    }

    private static UnitTestEventEntry PlcEvent(uint sequence)
    {
        return PlcEvent(
            sequence,
            Timestamp().AddSeconds(2),
            UnitTestOwnerScope.Assertion,
            UnitTestEventType.Result,
            LogLevel.Error,
            "Run.Suite.Case.AssertEqual",
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Failed,
            "Expected 1 but observed 0.");
    }

    private static UnitTestEventEntry PlcEvent(
        uint sequence,
        DateTimeOffset timestamp,
        UnitTestOwnerScope ownerScope,
        UnitTestEventType eventType,
        LogLevel severity,
        string name,
        UnitTestLifecycleState state,
        UnitTestResultState result,
        string message)
    {
        return new UnitTestEventEntry(
            sequence,
            timestamp,
            ownerScope,
            eventType,
            severity,
            name,
            name.Split('.').Last(),
            state,
            result,
            message);
    }

    private static UnitTestFinding OwnerFinding(
        uint sequence,
        UnitTestResultState result,
        string message) =>
        new(UnitTestFindingKind.OwnerResult, result, $"OwnerResult-{sequence}", message, new UnitTestEventRef(sequence));

    private static DateTimeOffset Timestamp() =>
        new(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);
}
