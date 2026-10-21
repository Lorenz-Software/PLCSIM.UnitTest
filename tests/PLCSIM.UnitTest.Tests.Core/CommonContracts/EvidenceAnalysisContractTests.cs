using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.CommonContracts;

public sealed class EvidenceAnalysisContractTests
{
    [Fact]
    public void Trace_entry_derives_kind_from_typed_payload()
    {
        var entry = new UnitTestActivityEntry(
            17,
            Timestamp(),
            new CommunicationSessionId("communication-1"),
            null,
            new LifecycleObservationId("lifecycle-1"),
            new EventStreamContextId("event-stream-1"),
            new OperationId("operation-1"),
            new CommandActivityPayload(
                UnitTestActivityCommandKind.Reset,
                UnitTestActivityCommandPhase.Completed,
                OperationResultState.Succeeded,
                Accepted: true));

        Assert.Equal(UnitTestActivityKind.Command, entry.Kind);
        Assert.Equal("lifecycle-1", entry.LifecycleObservationId?.Value);
        Assert.Equal("event-stream-1", entry.EventStreamContextId?.Value);
        var payload = Assert.IsType<CommandActivityPayload>(entry.Payload);
        Assert.Equal(UnitTestActivityCommandKind.Reset, payload.Command);
        Assert.True(payload.Accepted);
    }

    [Fact]
    public void Trace_snapshot_exposes_retention_and_eviction_bounds()
    {
        var entry = PlcEventEntry(42, 7);
        var snapshot = new UnitTestActivityTraceSnapshot(
            new ApplicationSessionId("session-1"),
            Timestamp(),
            null,
            Timestamp().AddSeconds(1),
            IsCaptureActive: true,
            OldestSequence: 42,
            NewestSequence: 42,
            EvictedEntryCount: 3,
            [entry]);

        Assert.True(snapshot.IsCaptureActive);
        Assert.Equal(3, snapshot.EvictedEntryCount);
        Assert.Equal(42, Assert.Single(snapshot.Entries).Sequence);
    }

    [Fact]
    public void Trace_payloads_reuse_existing_structured_application_models()
    {
        var eventEntry = PlcEventEntry(42, 7);
        var diagnostic = CreateDiagnostic();
        var runEpoch = CreateRunEpoch();

        Assert.Equal(7u, Assert.IsType<PlcEventActivityPayload>(eventEntry.Payload).Event.SequenceId);
        Assert.Same(diagnostic, new DiagnosticActivityPayload(diagnostic).Diagnostic);
        Assert.Equal(UnitTestActivityKind.Diagnostic, new DiagnosticActivityPayload(diagnostic).Kind);
        Assert.Same(
            runEpoch,
            new RunEpochActivityPayload(UnitTestRunEpochActivityKind.Ended, runEpoch).Snapshot);
    }

    [Fact]
    public void Empty_analysis_preserves_trace_range_and_completeness()
    {
        var generatedAt = Timestamp();

        var snapshot = EvidenceAnalysisSnapshot.Empty(
            new EvidenceSnapshotId("analysis-1"),
            new ApplicationSessionId("session-1"),
            EvidenceAnalysisScope.Run,
            new RunEpochId("epoch-1"),
            null,
            generatedAt,
            10,
            25,
            EvidenceCompleteness.Incomplete);

        Assert.Equal("analysis-1", snapshot.Id.Value);
        Assert.Equal(EvidenceAnalysisScope.Run, snapshot.Scope);
        Assert.Equal("epoch-1", snapshot.RunEpochId?.Value);
        Assert.Equal(generatedAt, snapshot.GeneratedAt);
        Assert.Equal(10, snapshot.AnalyzedFromSequence);
        Assert.Equal(25, snapshot.AnalyzedThroughSequence);
        Assert.Equal(EvidenceCompleteness.Incomplete, snapshot.Completeness);
        Assert.Equal(EvidenceAnalysisAssessment.Inconclusive, snapshot.Assessment);
        Assert.Empty(snapshot.Incidents);
    }

    [Theory]
    [InlineData(EvidenceAnalysisFormat.Markdown)]
    [InlineData(EvidenceAnalysisFormat.PlainText)]
    public void Formatter_separates_facts_conclusions_and_suggested_checks(EvidenceAnalysisFormat format)
    {
        var text = new EvidenceAnalysisFormatter().Format(CreateAnalysis(), format);

        Assert.Contains("Observed facts", text, StringComparison.Ordinal);
        Assert.Contains("Deterministic conclusions", text, StringComparison.Ordinal);
        Assert.Contains("Suggested checks", text, StringComparison.Ordinal);
        Assert.Contains("[event-loss] Producer reported lost events.", text, StringComparison.Ordinal);
        Assert.Contains("[incomplete-run] Run evidence is incomplete.", text, StringComparison.Ordinal);
        Assert.Contains("[inspect-load] Inspect PLC event production and communication load.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_formatter_is_stable_and_orders_timeline_by_timestamp()
    {
        var formatter = new EvidenceAnalysisFormatter();

        var first = formatter.Format(CreateAnalysis(), EvidenceAnalysisFormat.Markdown);
        var second = formatter.Format(CreateAnalysis(), EvidenceAnalysisFormat.Markdown);

        Assert.Equal(first, second);
        Assert.StartsWith("# Run Evidence Analysis", first, StringComparison.Ordinal);
        Assert.Contains("## Assessment", first, StringComparison.Ordinal);
        Assert.Contains("**Error**", first, StringComparison.Ordinal);
        Assert.Contains("Run evidence is incomplete and requires investigation.", first, StringComparison.Ordinal);
        Assert.True(
            first.IndexOf("## Assessment", StringComparison.Ordinal)
            < first.IndexOf("## Technical Details", StringComparison.Ordinal));
        Assert.True(
            first.IndexOf("2026-08-31T08:00:01.0000000+00:00", StringComparison.Ordinal)
            < first.IndexOf("2026-08-31T08:00:02.0000000+00:00", StringComparison.Ordinal));
        Assert.Contains("activity:42#plc:7", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_format_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvidenceAnalysisFormatter().Format(CreateAnalysis(), (EvidenceAnalysisFormat)99));
    }

    [Fact]
    public void Run_formatter_appends_the_complete_run_activity_timeline()
    {
        var trace = new UnitTestActivityTraceSnapshot(
            new ApplicationSessionId("session-1"),
            Timestamp(),
            null,
            Timestamp().AddSeconds(3),
            IsCaptureActive: true,
            OldestSequence: 42,
            NewestSequence: 43,
            EvictedEntryCount: 0,
            [PlcEventEntry(42, 7), PlcEventEntry(43, 8)]);

        var text = new EvidenceAnalysisFormatter().Format(
            CreateAnalysis(),
            trace,
            EvidenceAnalysisFormat.Markdown);
        var supportingEvidence = SupportingEvidence(text);

        Assert.Contains("# Activity Timeline", supportingEvidence, StringComparison.Ordinal);
        Assert.Contains("## Activity 42", supportingEvidence, StringComparison.Ordinal);
        Assert.Contains("**PLC sequence:** 7", supportingEvidence, StringComparison.Ordinal);
        Assert.Contains("**Message:** Case passed.", supportingEvidence, StringComparison.Ordinal);
        Assert.Contains("## Activity 43", supportingEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("**Communication session:**", supportingEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("**Lifecycle observation:**", supportingEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("**Event stream:**", supportingEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("**Operation:**", supportingEvidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Lifecycle_supporting_evidence_uses_a_command_focused_projection()
    {
        var lifecycleId = new LifecycleObservationId("lifecycle-1");
        var analysis = EvidenceAnalysisSnapshot.Empty(
            new EvidenceSnapshotId("analysis-lifecycle"),
            new ApplicationSessionId("session-1"),
            EvidenceAnalysisScope.Lifecycle,
            null,
            lifecycleId,
            Timestamp().AddSeconds(1),
            44,
            44,
            EvidenceCompleteness.Complete);
        var trace = new UnitTestActivityTraceSnapshot(
            new ApplicationSessionId("session-1"),
            Timestamp(),
            null,
            Timestamp().AddSeconds(1),
            IsCaptureActive: true,
            OldestSequence: 44,
            NewestSequence: 44,
            EvictedEntryCount: 0,
            [
                new UnitTestActivityEntry(
                    44,
                    Timestamp(),
                    new CommunicationSessionId("communication-1"),
                    null,
                    lifecycleId,
                    new EventStreamContextId("event-stream-1"),
                    new OperationId("operation-1"),
                    new CommandActivityPayload(
                        UnitTestActivityCommandKind.Reset,
                        UnitTestActivityCommandPhase.Completed,
                        OperationResultState.Succeeded,
                        Accepted: true,
                        Message: "Reset accepted."))
            ]);

        var text = new EvidenceAnalysisFormatter().Format(
            analysis,
            trace,
            EvidenceAnalysisFormat.Markdown);
        var supportingEvidence = SupportingEvidence(text);

        Assert.Contains("## Activity 44 - Lifecycle command", supportingEvidence, StringComparison.Ordinal);
        Assert.Contains("**Command:** Reset", supportingEvidence, StringComparison.Ordinal);
        Assert.Contains("**Accepted:** True", supportingEvidence, StringComparison.Ordinal);
        Assert.Contains("**Message:** Reset accepted.", supportingEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("**Communication session:**", supportingEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("**Lifecycle observation:** lifecycle-1", supportingEvidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_supporting_evidence_uses_a_session_focused_projection()
    {
        var analysis = EvidenceAnalysisSnapshot.Empty(
            new EvidenceSnapshotId("analysis-session"),
            new ApplicationSessionId("session-1"),
            EvidenceAnalysisScope.Session,
            null,
            null,
            Timestamp().AddSeconds(1),
            45,
            45,
            EvidenceCompleteness.Complete);
        var trace = new UnitTestActivityTraceSnapshot(
            new ApplicationSessionId("session-1"),
            Timestamp(),
            null,
            Timestamp().AddSeconds(1),
            IsCaptureActive: true,
            OldestSequence: 45,
            NewestSequence: 45,
            EvictedEntryCount: 0,
            [
                new UnitTestActivityEntry(
                    45,
                    Timestamp(),
                    new CommunicationSessionId("communication-1"),
                    null,
                    null,
                    null,
                    null,
                    new SessionActivityPayload(UnitTestSessionActivityKind.Started))
            ]);

        var text = new EvidenceAnalysisFormatter().Format(
            analysis,
            trace,
            EvidenceAnalysisFormat.Markdown);

        Assert.StartsWith("# Communication Health Evidence Analysis", text, StringComparison.Ordinal);
        Assert.Contains("## Activity 45 - Application session", text, StringComparison.Ordinal);
        Assert.Contains("**Activity:** Started", text, StringComparison.Ordinal);
        Assert.DoesNotContain("**Communication session:**", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_supporting_evidence_includes_the_latest_hierarchy_snapshot()
    {
        var trace = new UnitTestActivityTraceSnapshot(
            new ApplicationSessionId("session-1"),
            Timestamp(),
            null,
            Timestamp().AddSeconds(3),
            IsCaptureActive: true,
            OldestSequence: 42,
            NewestSequence: 44,
            EvictedEntryCount: 0,
            [PlcEventEntry(42, 7), PlcEventEntry(43, 8), RunEpochEntry(44, CreateRunEpochWithHierarchy())]);

        var text = new EvidenceAnalysisFormatter().Format(
            CreateAnalysis(),
            trace,
            EvidenceAnalysisFormat.Markdown);

        Assert.Contains("# Run Hierarchy Snapshot", text, StringComparison.Ordinal);
        Assert.Contains("```json", text, StringComparison.Ordinal);
        Assert.Contains("\"sourceActivitySequence\": 44", text, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"RunControl\"", text, StringComparison.Ordinal);
        Assert.Contains("\"displayName\": \"Suite 1\"", text, StringComparison.Ordinal);
        Assert.Contains("\"displayName\": \"Case 1\"", text, StringComparison.Ordinal);
        Assert.Contains("\"lifecycleState\": \"Completed\"", text, StringComparison.Ordinal);
        Assert.Contains("\"effectiveResult\": \"Failed\"", text, StringComparison.Ordinal);
        Assert.Contains("\"plcReportedResult\": null", text, StringComparison.Ordinal);
        Assert.Contains("\"evidenceState\": \"Incomplete\"", text, StringComparison.Ordinal);
        Assert.Contains("\"completionSequenceId\": null", text, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"MissingEvidence\"", text, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"Missing result\"", text, StringComparison.Ordinal);
    }

    private static string SupportingEvidence(string report)
    {
        var supportingEvidenceIndex = report.IndexOf("# Supporting Evidence", StringComparison.Ordinal);
        var timelineIndex = report.IndexOf("# Activity Timeline", StringComparison.Ordinal);
        var index = Math.Max(supportingEvidenceIndex, timelineIndex);
        Assert.True(index >= 0);
        return report[index..];
    }

    private static UnitTestActivityEntry RunEpochEntry(long activitySequence, UnitTestRunEpochSnapshot snapshot)
    {
        return new UnitTestActivityEntry(
            activitySequence,
            Timestamp().AddSeconds(2),
            new CommunicationSessionId("communication-1"),
            snapshot.EpochId,
            null,
            new EventStreamContextId("event-stream-1"),
            null,
            new RunEpochActivityPayload(UnitTestRunEpochActivityKind.Updated, snapshot));
    }

    [Fact]
    public void Formatter_with_trace_rejects_a_different_application_session()
    {
        var trace = new UnitTestActivityTraceSnapshot(
            new ApplicationSessionId("session-2"),
            Timestamp(),
            null,
            Timestamp(),
            IsCaptureActive: true,
            OldestSequence: 42,
            NewestSequence: 42,
            EvictedEntryCount: 0,
            [PlcEventEntry(42, 7)]);

        Assert.Throws<ArgumentException>(() =>
            new EvidenceAnalysisFormatter().Format(
                CreateAnalysis(),
                trace,
                EvidenceAnalysisFormat.Markdown));
    }

    private static UnitTestActivityEntry PlcEventEntry(long activitySequence, uint plcSequence)
    {
        return new UnitTestActivityEntry(
            activitySequence,
            Timestamp(),
            new CommunicationSessionId("communication-1"),
            new RunEpochId("epoch-1"),
            null,
            new EventStreamContextId("event-stream-1"),
            null,
            new PlcEventActivityPayload(new UnitTestEventEntry(
                plcSequence,
                Timestamp(),
                UnitTestOwnerScope.TestCase,
                UnitTestEventType.Result,
                LogLevel.Information,
                "Case",
                "UnitTest.Case",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Passed,
                "Case passed.")));
    }

    private static EvidenceAnalysisSnapshot CreateAnalysis()
    {
        var openedAt = Timestamp();
        var eventReference = new EvidenceReference(42, 7);
        var incident = new EvidenceIncident(
            new EvidenceIncidentId("incident-1"),
            EvidenceIncidentClassification.EvidenceIncomplete,
            EvidenceIncidentSeverity.Error,
            EvidenceCompleteness.Incomplete,
            EvidenceIncidentScope.Run,
            "Incomplete run evidence",
            "The producer reported lost events during the run.",
            openedAt,
            openedAt.AddSeconds(3),
            new RunEvidenceAnalysis(
                new RunEpochId("epoch-1"),
                UnitTestRunState.Invalidated,
                UnitTestResultState.Error,
                EvidenceState.Incomplete,
                3),
            null,
            [new EvidenceAffectedOwner(new UnitTestNodeId("suite-1"), UnitTestNodeKind.TestSuite, "Suite 1")],
            [new EvidenceAnalysisStatement("event-loss", "Producer reported lost events.", [eventReference])],
            [new EvidenceAnalysisStatement("incomplete-run", "Run evidence is incomplete.", [eventReference])],
            [new EvidenceSuggestedCheck("inspect-load", "Inspect PLC event production and communication load.")],
            [
                new EvidenceTimelineItem(openedAt.AddSeconds(2), EvidenceTimelineKind.Analysis, "Run invalidated.", []),
                new EvidenceTimelineItem(openedAt.AddSeconds(1), EvidenceTimelineKind.PlcEvent, "Lost-event count increased.", [eventReference])
            ]);

        return new EvidenceAnalysisSnapshot(
            new EvidenceSnapshotId("analysis-1"),
            new ApplicationSessionId("session-1"),
            EvidenceAnalysisScope.Run,
            new RunEpochId("epoch-1"),
            null,
            openedAt.AddSeconds(3),
            1,
            42,
            EvidenceCompleteness.Incomplete,
            [incident],
            EvidenceAnalysisAssessment.Error,
            "Run evidence is incomplete and requires investigation.");
    }

    private static Diagnostic CreateDiagnostic()
    {
        return new Diagnostic(
            "Test.Diagnostic",
            DiagnosticCategory.Communication,
            LogLevel.Warning,
            DiagnosticSource.SharedWorkflow,
            DiagnosticPhase.ExecutionMonitoring,
            "Diagnostic message.",
            new DiagnosticCorrelation(SessionId: new ApplicationSessionId("session-1")),
            Timestamp());
    }

    private static UnitTestRunEpochSnapshot CreateRunEpoch()
    {
        return new UnitTestRunEpochSnapshot(
            new RunEpochId("epoch-1"),
            UnitTestRunState.Completed,
            UnitTestResultState.Passed,
            EvidenceState.Complete,
            Timestamp(),
            Timestamp(),
            [],
            [],
            new UnitTestRunNode(
                new UnitTestNodeId("run-1"),
                "RunControl",
                "RunControl",
                UnitTestNodeKind.RunControl,
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Passed,
                EvidenceState.Complete,
                null,
                null,
                null,
                null,
                null,
                Timestamp(),
                Timestamp(),
                [],
            []));
    }

    private static UnitTestRunEpochSnapshot CreateRunEpochWithHierarchy()
    {
        var testcase = new UnitTestRunNode(
            new UnitTestNodeId("case-1"),
            "Case1",
            "Case 1",
            UnitTestNodeKind.TestCase,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.NotEvaluated,
            EvidenceState.Incomplete,
            null,
            new UnitTestEventRef(10),
            null,
            null,
            null,
            Timestamp(),
            Timestamp().AddSeconds(1),
            [
                new UnitTestFinding(
                    UnitTestFindingKind.MissingEvidence,
                    UnitTestResultState.Error,
                    "Missing result",
                    "The test-case result event was not observed.",
                    new UnitTestEventRef(11))
            ],
            []);
        var suite = new UnitTestRunNode(
            new UnitTestNodeId("suite-1"),
            "Suite1",
            "Suite 1",
            UnitTestNodeKind.TestSuite,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Error,
            EvidenceState.Incomplete,
            new UnitTestAggregation(1, 0, 0, 0, 1, 0, 0, 1, 100, 0),
            new UnitTestEventRef(9),
            new UnitTestEventRef(12),
            new UnitTestEventRef(13),
            new UnitTestEventRef(14),
            Timestamp(),
            Timestamp().AddSeconds(1),
            [],
            [testcase]);
        var root = new UnitTestRunNode(
            new UnitTestNodeId("run-1"),
            "RunControl",
            "RunControl",
            UnitTestNodeKind.RunControl,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Failed,
            EvidenceState.Incomplete,
            new UnitTestAggregation(1, 0, 0, 0, 1, 0, 1, 0, 100, 0),
            new UnitTestEventRef(1),
            new UnitTestEventRef(15),
            new UnitTestEventRef(16),
            new UnitTestEventRef(17),
            Timestamp(),
            Timestamp().AddSeconds(2),
            [],
            [suite]);

        return new UnitTestRunEpochSnapshot(
            new RunEpochId("epoch-1"),
            UnitTestRunState.Invalidated,
            UnitTestResultState.Error,
            EvidenceState.Incomplete,
            Timestamp(),
            Timestamp().AddSeconds(2),
            [],
            [],
            root,
            lostEventCount: 1);
    }

    private static DateTimeOffset Timestamp() =>
        new(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);
}
