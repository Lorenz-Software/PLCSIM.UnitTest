using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.Analysis;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Analysis;

public sealed class LifecycleEvidenceAnalyzerTests
{
    [Fact]
    public void Successful_reset_transport_without_reaction_observation_produces_no_incident()
    {
        var observationId = new LifecycleObservationId("reset");
        var trace = Trace(
            Activity(1, new CommandActivityPayload(UnitTestActivityCommandKind.Reset, UnitTestActivityCommandPhase.Requested), observationId),
            Activity(2, new CommandActivityPayload(UnitTestActivityCommandKind.Reset, UnitTestActivityCommandPhase.Completed, OperationResultState.Succeeded, Accepted: true), observationId));

        var analysis = Analyzer().Analyze(trace, observationId);

        Assert.Equal(EvidenceCompleteness.Complete, analysis.Completeness);
        Assert.Equal(EvidenceAnalysisAssessment.Healthy, analysis.Assessment);
        Assert.Contains("Reset command handover", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("no reported lifecycle-reaction", analysis.Summary, StringComparison.Ordinal);
        Assert.Empty(analysis.Incidents);
    }

    [Fact]
    public void Failed_command_handover_reports_transport_failure()
    {
        var observationId = new LifecycleObservationId("reset");
        var trace = Trace(Activity(
            1,
            new CommandActivityPayload(
                UnitTestActivityCommandKind.EventBufferReset,
                UnitTestActivityCommandPhase.Completed,
                OperationResultState.Failed,
                Accepted: false,
                "Write failed."),
            observationId));

        var incident = Assert.Single(Analyzer().Analyze(trace, observationId).Incidents);

        Assert.Equal(EvidenceIncidentClassification.CommandTransportFailure, incident.Classification);
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "command-transport-failed");
    }

    [Theory]
    [InlineData("Workflow.TestRunMonitor.CommandReactionTimeout", "Reset reaction was not observed.")]
    [InlineData("Workflow.PlcCommand.StateObservationTimeout", "PLC start state was not observed.")]
    [InlineData("Workflow.EventStreamProcessor.FrameworkEvent", "Test Suite reset timeout reached.")]
    public void Existing_lifecycle_timeout_diagnostic_reports_reaction_failure(string code, string message)
    {
        var observationId = new LifecycleObservationId("lifecycle");
        var incident = Assert.Single(Analyzer().Analyze(
            Trace(Activity(1, Diagnostic(code, message), observationId)),
            observationId).Incidents);

        Assert.Equal(EvidenceIncidentClassification.LifecycleReactionFailure, incident.Classification);
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "lifecycle-failure-diagnostic");
    }

    [Fact]
    public void Framework_reset_timeout_correlates_command_plc_event_and_last_owner_progress()
    {
        var observationId = new LifecycleObservationId("reset");
        var trace = Trace(
            Activity(1, new CommandActivityPayload(UnitTestActivityCommandKind.Reset, UnitTestActivityCommandPhase.Requested), observationId),
            Activity(2, new CommandActivityPayload(UnitTestActivityCommandKind.Reset, UnitTestActivityCommandPhase.Completed, OperationResultState.Succeeded, Accepted: true), observationId),
            Activity(3, new RunControlActivityPayload(new UnitTestRunControlObservation(
                UnitTestExecutionStatus.Reset,
                UnitTestExecutionSignals.None,
                UnitTestLifecycleState.Reset,
                UnitTestResultState.NotEvaluated)), observationId),
            Activity(4, new PlcEventActivityPayload(PlcEvent(
                170,
                UnitTestOwnerScope.TestCase,
                UnitTestEventType.StateTransition,
                LogLevel.Information,
                "UnitTest.Suite.Case",
                UnitTestLifecycleState.Reset,
                UnitTestResultState.NotEvaluated,
                "Test Case remained in Reset.")), observationId),
            Activity(5, new PlcEventActivityPayload(PlcEvent(
                171,
                UnitTestOwnerScope.Framework,
                UnitTestEventType.Framework,
                LogLevel.Error,
                "TestSuite.ResetTimeout",
                UnitTestLifecycleState.Reset,
                UnitTestResultState.NotEvaluated,
                "Test Suite reset timeout reached.")), observationId),
            Activity(6, Diagnostic("Workflow.EventStreamProcessor.FrameworkEvent", "Test Suite reset timeout reached."), observationId));

        var incident = Assert.Single(Analyzer().Analyze(trace, observationId).Incidents);

        Assert.Equal(EvidenceIncidentClassification.LifecycleReactionFailure, incident.Classification);
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "command-handover-accepted");
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "plc-framework-event");
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "latest-run-control-state");
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "last-owner-progress");
        Assert.Contains(incident.AffectedOwners, owner => owner.DisplayName == "UnitTest.Suite.Case");
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, incident.Timeline
            .SelectMany(item => item.Evidence)
            .Select(reference => reference.ActivitySequence)
            .Distinct()
            .ToArray());
    }

    [Fact]
    public void Plc_restart_opens_a_new_startup_window_without_inheriting_an_older_reset_command()
    {
        var oldReset = new LifecycleObservationId("old-reset");
        var startup = new LifecycleObservationId("startup");
        var trace = Trace(
            Activity(1, new CommandActivityPayload(UnitTestActivityCommandKind.Reset, UnitTestActivityCommandPhase.Requested), oldReset),
            Activity(2, new CommandActivityPayload(UnitTestActivityCommandKind.Reset, UnitTestActivityCommandPhase.Completed, OperationResultState.Succeeded, Accepted: true), oldReset),
            Activity(10, new PlcStateActivityPayload(new PlcState(
                RuntimeInstanceState.Available,
                PlcOperatingState.Running,
                Array.Empty<RuntimeTargetRef>(),
                ReadyForTransfer: true,
                ReadyForExecution: true)), startup),
            Activity(11, new RunControlActivityPayload(new UnitTestRunControlObservation(
                UnitTestExecutionStatus.Reset,
                UnitTestExecutionSignals.None,
                UnitTestLifecycleState.Reset,
                UnitTestResultState.NotEvaluated)), startup),
            Activity(12, new PlcEventActivityPayload(PlcEvent(
                5,
                UnitTestOwnerScope.TestSuite,
                UnitTestEventType.StateTransition,
                LogLevel.Information,
                "UnitTest.TestSuite_AssertionBool",
                UnitTestLifecycleState.Reset,
                UnitTestResultState.NotEvaluated,
                "Suite remained in Reset.")), startup),
            Activity(13, new PlcEventActivityPayload(PlcEvent(
                7,
                UnitTestOwnerScope.Framework,
                UnitTestEventType.Framework,
                LogLevel.Error,
                "UnitTest.TestSuite_AssertionBool",
                UnitTestLifecycleState.Reset,
                UnitTestResultState.NotEvaluated,
                "Test Suite reset timeout reached.")), startup),
            Activity(14, Diagnostic("Workflow.EventStreamProcessor.FrameworkEvent", "Test Suite reset timeout reached."), startup));

        var analysis = Analyzer().Analyze(trace, startup);
        var incident = Assert.Single(analysis.Incidents);

        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "plc-startup-anchor");
        Assert.DoesNotContain(incident.ObservedFacts, fact => fact.Code is "command-requested" or "command-handover-accepted");
        Assert.Equal(Timestamp().AddMilliseconds(10), incident.OpenedAt);
        Assert.Contains(incident.AffectedOwners, owner => owner.DisplayName == "UnitTest.TestSuite_AssertionBool");
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "directly-affected-owner");
        Assert.DoesNotContain("Reset command handover", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("PLC startup initialization", analysis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Framework_timeout_reports_the_direct_owner_and_last_observed_subordinate_owner()
    {
        var observationId = new LifecycleObservationId("startup");
        var suiteName = "UnitTest.TestSuite_AssertionBool";
        var caseName = $"{suiteName}.AssertEqual_Bool";
        var trace = Trace(
            Activity(1, new PlcEventActivityPayload(PlcEvent(
                1, UnitTestOwnerScope.TestSuite, UnitTestEventType.StateTransition, LogLevel.Information,
                suiteName, UnitTestLifecycleState.Reset, UnitTestResultState.NotEvaluated, "Suite entered Reset.")), observationId),
            Activity(2, new PlcEventActivityPayload(PlcEvent(
                2, UnitTestOwnerScope.TestCase, UnitTestEventType.StateTransition, LogLevel.Information,
                caseName, UnitTestLifecycleState.Idle, UnitTestResultState.NotEvaluated, "Test Case reached Idle.")), observationId),
            Activity(3, new PlcEventActivityPayload(PlcEvent(
                3, UnitTestOwnerScope.Framework, UnitTestEventType.Framework, LogLevel.Error,
                suiteName, UnitTestLifecycleState.Reset, UnitTestResultState.NotEvaluated, "Test Suite reset timeout reached.")), observationId),
            Activity(4, Diagnostic("Workflow.EventStreamProcessor.FrameworkEvent", "Test Suite reset timeout reached."), observationId));

        var incident = Assert.Single(Analyzer().Analyze(trace, observationId).Incidents);

        Assert.Equal(new[] { suiteName, caseName }, incident.AffectedOwners.Select(static owner => owner.DisplayName).ToArray());
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "directly-affected-owner");
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "last-owner-progress");
    }

    [Fact]
    public void Accepted_event_buffer_reset_does_not_require_a_clean_buffer_reaction()
    {
        var observationId = new LifecycleObservationId("event-buffer-reset");
        var trace = Trace(Activity(
            1,
            new CommandActivityPayload(
                UnitTestActivityCommandKind.EventBufferReset,
                UnitTestActivityCommandPhase.Completed,
                OperationResultState.Succeeded,
                Accepted: true),
            observationId));

        Assert.Empty(Analyzer().Analyze(trace, observationId).Incidents);
    }

    [Fact]
    public void Contradictory_run_control_diagnostic_reports_contract_conflict()
    {
        var observationId = new LifecycleObservationId("reset");
        var trace = Trace(Activity(
            1,
            Diagnostic(
                "Workflow.RunControlObservation.ContradictorySignals",
                "Running and Completed are both set."),
            observationId));

        var incident = Assert.Single(Analyzer().Analyze(trace, observationId).Incidents);

        Assert.Equal(EvidenceIncidentClassification.ContractConflict, incident.Classification);
    }

    [Fact]
    public void Lost_event_state_marks_lifecycle_evidence_incomplete()
    {
        var observationId = new LifecycleObservationId("reset");
        var state = new EventStreamState(
            new CommunicationSessionId("communication"),
            IsMonitoring: true,
            EventBufferReadState.Valid,
            EventStreamBatchReleaseState.Completed,
            BufferObservedEmpty: false,
            LostEventCount: 3);
        var trace = Trace(Activity(1, new EventStreamActivityPayload(state), observationId));

        var analysis = Analyzer().Analyze(trace, observationId);

        Assert.Equal(EvidenceCompleteness.Incomplete, analysis.Completeness);
        var incident = Assert.Single(analysis.Incidents);
        Assert.Equal(EvidenceIncidentClassification.EvidenceIncomplete, incident.Classification);
    }

    [Fact]
    public void Requested_lifecycle_observation_excludes_other_correlations()
    {
        var selected = new LifecycleObservationId("selected");
        var other = new LifecycleObservationId("other");
        var trace = Trace(
            Activity(1, new CommandActivityPayload(UnitTestActivityCommandKind.Reset, UnitTestActivityCommandPhase.Completed, OperationResultState.Succeeded, true), selected),
            Activity(2, new CommandActivityPayload(UnitTestActivityCommandKind.Abort, UnitTestActivityCommandPhase.Completed, OperationResultState.Failed, false), other));

        var analysis = Analyzer().Analyze(trace, selected);

        Assert.Empty(analysis.Incidents);
        Assert.Equal(selected, analysis.LifecycleObservationId);
    }

    [Fact]
    public void Missing_requested_lifecycle_observation_produces_unavailable_analysis()
    {
        var trace = Trace(Activity(1, new SessionActivityPayload(UnitTestSessionActivityKind.Started)));

        var analysis = Analyzer().Analyze(trace, new LifecycleObservationId("missing"));

        Assert.Equal(EvidenceCompleteness.Unavailable, analysis.Completeness);
        Assert.Contains(analysis.Incidents, incident =>
            incident.Classification == EvidenceIncidentClassification.EvidenceIncomplete);
    }

    [Fact]
    public void Evicted_session_prefix_does_not_invalidate_a_fully_retained_targeted_window()
    {
        var observationId = new LifecycleObservationId("reset");
        var other = new LifecycleObservationId("other");
        var trace = Trace(
            [
                Activity(5, new SessionActivityPayload(UnitTestSessionActivityKind.Started), other),
                Activity(6, new CommandActivityPayload(UnitTestActivityCommandKind.Reset, UnitTestActivityCommandPhase.Completed, OperationResultState.Succeeded, true), observationId)
            ],
            evicted: 5);

        var targeted = Analyzer().Analyze(trace, observationId);

        Assert.Equal(EvidenceCompleteness.Complete, targeted.Completeness);
        Assert.Empty(targeted.Incidents);
    }

    [Fact]
    public void Evicted_prefix_marks_a_targeted_window_incomplete_when_its_first_entry_is_the_oldest_retained_entry()
    {
        var observationId = new LifecycleObservationId("reset");
        var trace = Trace(
            [Activity(6, new CommandActivityPayload(UnitTestActivityCommandKind.Reset, UnitTestActivityCommandPhase.Completed, OperationResultState.Succeeded, true), observationId)],
            evicted: 5);

        var analysis = Analyzer().Analyze(trace, observationId);

        Assert.Equal(EvidenceCompleteness.Incomplete, analysis.Completeness);
        Assert.Contains(analysis.Incidents, incident => incident.Classification == EvidenceIncidentClassification.EvidenceIncomplete);
    }

    private static LifecycleEvidenceAnalyzer Analyzer() => new(FakeTime.Create());

    private static UnitTestActivityTraceSnapshot Trace(params UnitTestActivityEntry[] entries) =>
        Trace(entries, evicted: 0);

    private static UnitTestActivityTraceSnapshot Trace(
        IReadOnlyList<UnitTestActivityEntry> entries,
        long evicted)
    {
        return new UnitTestActivityTraceSnapshot(
            new ApplicationSessionId("session"),
            Timestamp(),
            Timestamp().AddSeconds(5),
            Timestamp().AddSeconds(5),
            IsCaptureActive: false,
            entries.Count == 0 ? 0 : entries[0].Sequence,
            entries.Count == 0 ? 0 : entries[^1].Sequence,
            evicted,
            entries);
    }

    private static UnitTestActivityEntry Activity(
        long sequence,
        UnitTestActivityPayload payload,
        LifecycleObservationId? observationId = null)
    {
        return new UnitTestActivityEntry(
            sequence,
            Timestamp().AddMilliseconds(sequence),
            new CommunicationSessionId("communication"),
            null,
            observationId,
            new EventStreamContextId("stream"),
            new OperationId($"operation-{sequence}"),
            payload);
    }

    private static DiagnosticActivityPayload Diagnostic(string code, string message)
    {
        return new DiagnosticActivityPayload(new Diagnostic(
            code,
            DiagnosticCategory.Workflow,
            LogLevel.Error,
            DiagnosticSource.SharedWorkflow,
            DiagnosticPhase.ExecutionMonitoring,
            message,
            new DiagnosticCorrelation(SessionId: new ApplicationSessionId("session")),
            Timestamp()));
    }

    private static UnitTestEventEntry PlcEvent(
        uint sequence,
        UnitTestOwnerScope ownerScope,
        UnitTestEventType eventType,
        LogLevel severity,
        string name,
        UnitTestLifecycleState state,
        UnitTestResultState result,
        string message) =>
        new(
            sequence,
            Timestamp().AddMilliseconds(sequence),
            ownerScope,
            eventType,
            severity,
            name,
            name.Split('.').Last(),
            state,
            result,
            message);

    private static DateTimeOffset Timestamp() =>
        new(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);
}
