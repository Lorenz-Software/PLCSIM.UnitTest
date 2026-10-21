using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.Analysis;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Analysis;

public sealed class CommunicationSessionEvidenceAnalyzerTests
{
    [Fact]
    public void Revalidation_followed_by_recovery_is_healthy()
    {
        var trace = Trace(
            Activity(1, Access(AccessHealthState.RevalidationRequired, AccessHealthProblem.RevalidationRequired, "Tag list stale.")),
            Activity(2, Access(AccessHealthState.Healthy, AccessHealthProblem.None, "Communication recovered.")));

        var analysis = Analyzer().Analyze(trace);

        Assert.Equal(EvidenceAnalysisScope.Session, analysis.Scope);
        Assert.Equal(EvidenceCompleteness.Complete, analysis.Completeness);
        Assert.Equal(EvidenceAnalysisAssessment.Healthy, analysis.Assessment);
        var history = Assert.Single(analysis.Incidents);
        Assert.Equal(EvidenceIncidentSeverity.Information, history.Severity);
        Assert.Contains("non-status-affecting history", analysis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Revalidation_without_recovery_is_reported()
    {
        var trace = Trace(Activity(
            1,
            Access(AccessHealthState.RevalidationRequired, AccessHealthProblem.RevalidationRequired, "Tag list stale.")));

        var incident = Assert.Single(Analyzer().Analyze(trace).Incidents);

        Assert.Equal(EvidenceIncidentClassification.CommunicationFailure, incident.Classification);
        Assert.Equal(EvidenceIncidentSeverity.Warning, incident.Severity);
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "communication-revalidation-pending");
    }

    [Fact]
    public void Terminal_access_invalidation_reports_communication_failure()
    {
        var trace = Trace(Activity(
            1,
            Access(AccessHealthState.Invalidated, AccessHealthProblem.CommunicationLost, "Signal configuration failed.")));

        var incident = Assert.Single(Analyzer().Analyze(trace).Incidents);

        Assert.Equal(EvidenceIncidentClassification.CommunicationFailure, incident.Classification);
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "communication-invalidated");
    }

    [Fact]
    public void Contract_invalid_event_stream_reports_contract_conflict_and_incomplete_evidence()
    {
        var state = EventStream(EventBufferReadState.ContractInvalid, lostEvents: 0);
        var analysis = Analyzer().Analyze(Trace(Activity(1, new EventStreamActivityPayload(state))));

        Assert.Equal(EvidenceCompleteness.Incomplete, analysis.Completeness);
        Assert.Contains(analysis.Incidents, incident => incident.Classification == EvidenceIncidentClassification.ContractConflict);
        Assert.Contains(analysis.Incidents, incident => incident.Classification == EvidenceIncidentClassification.EvidenceIncomplete);
    }

    [Fact]
    public void Lost_events_and_sequence_gap_mark_session_evidence_incomplete()
    {
        var trace = Trace(
            Activity(1, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, lostEvents: 3))),
            Activity(2, Diagnostic("Workflow.EventStreamProcessor.SequenceGap", "Expected event 10 but observed 12.")));

        var analysis = Analyzer().Analyze(trace);

        Assert.Equal(EvidenceCompleteness.Incomplete, analysis.Completeness);
        var incident = Assert.Single(analysis.Incidents);
        Assert.Equal(EvidenceIncidentClassification.EvidenceIncomplete, incident.Classification);
    }

    [Fact]
    public void Evicted_trace_prefix_is_a_session_level_completeness_problem()
    {
        var trace = Trace([Activity(6, new SessionActivityPayload(UnitTestSessionActivityKind.Started))], evicted: 5);

        var analysis = Analyzer().Analyze(trace);

        Assert.Equal(EvidenceCompleteness.Incomplete, analysis.Completeness);
        Assert.Contains(analysis.Incidents.SelectMany(static incident => incident.ObservedFacts), fact => fact.Code == "trace-prefix-evicted");
    }

    [Fact]
    public void Uncorrelated_run_control_contract_conflict_is_reported_as_missing_lifecycle_correlation()
    {
        var trace = Trace(Activity(
            1,
            Diagnostic("Workflow.RunControlObservation.ContradictorySignals", "Running and Completed are both set.")));

        var analysis = Analyzer().Analyze(trace);

        Assert.Equal(EvidenceCompleteness.Incomplete, analysis.Completeness);
        var incident = Assert.Single(analysis.Incidents);
        Assert.Equal(EvidenceIncidentClassification.EvidenceIncomplete, incident.Classification);
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "lifecycle-correlation-missing");
    }

    [Fact]
    public void Latest_communication_context_is_healthy_and_earlier_invalidation_is_history()
    {
        var trace = Trace(
            Activity(1, Access(AccessHealthState.Invalidated, AccessHealthProblem.CommunicationLost, "Old context failed."), "old"),
            Activity(2, Access(AccessHealthState.Healthy, AccessHealthProblem.None, "New context healthy."), "current"),
            Activity(3, new EventStreamActivityPayload(EventStream(EventBufferReadState.Empty, lostEvents: 0)), "current"));

        var analysis = Analyzer().Analyze(trace);

        Assert.Equal(EvidenceAnalysisAssessment.Healthy, analysis.Assessment);
        Assert.Equal(2, analysis.AnalyzedFromSequence);
        var history = Assert.Single(analysis.Incidents);
        Assert.Equal(EvidenceIncidentSeverity.Information, history.Severity);
        Assert.Contains(history.ObservedFacts, fact => fact.Code == "historical-communication-invalidation");
    }

    [Fact]
    public void Layered_summary_separates_runtime_binding_lifecycle_stream_and_integrity()
    {
        var trace = Trace(
            Activity(1, Access(AccessHealthState.Healthy, AccessHealthProblem.None, "PLC control healthy.", AccessServiceKind.PlcControl)),
            Activity(2, Access(AccessHealthState.Healthy, AccessHealthProblem.None, "Lifecycle binding healthy.", AccessServiceKind.UnitTestLifecycle)),
            Activity(3, new EventStreamActivityPayload(EventStream(EventBufferReadState.Empty, lostEvents: 0, producer: 42, deleted: 42, empty: true))));

        var analysis = Analyzer().Analyze(trace);

        Assert.Contains("Runtime access: Healthy", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("UnitTest binding: Healthy", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("Lifecycle communication: No communication conflict observed", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("EventStream/EventBuffer: Empty", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("Evidence integrity: Complete", analysis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Prolonged_deletion_stall_without_catch_up_affects_current_assessment()
    {
        var trace = Trace(
            Activity(1, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 303, deleted: 303))),
            Activity(501, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 400, deleted: 303))),
            Activity(1001, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 531, deleted: 303))));

        var analysis = Analyzer().Analyze(trace);

        Assert.Equal(EvidenceAnalysisAssessment.Warning, analysis.Assessment);
        var incident = Assert.Single(analysis.Incidents);
        Assert.Equal(EvidenceIncidentClassification.EventBufferProgress, incident.Classification);
        Assert.Equal(EvidenceIncidentSeverity.Warning, incident.Severity);
        Assert.Contains(incident.ObservedFacts, fact =>
            fact.Code == "event-buffer-deletion-stall"
            && fact.Message.Contains("303", StringComparison.Ordinal)
            && fact.Message.Contains("531", StringComparison.Ordinal));
        Assert.Contains(incident.ObservedFacts, fact =>
            fact.Code == "event-buffer-maximum-backlog"
            && fact.Message.Contains("228", StringComparison.Ordinal));
        Assert.Contains(incident.SuggestedChecks, check => check.Code == "verify-cyclic-event-buffer-requests");
    }

    [Fact]
    public void Recovered_deletion_stall_is_retained_without_affecting_current_health()
    {
        var trace = Trace(
            Activity(1, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 303, deleted: 303))),
            Activity(501, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 400, deleted: 303))),
            Activity(1001, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 531, deleted: 303))),
            Activity(1002, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 537, deleted: 335))),
            Activity(1010, new EventStreamActivityPayload(EventStream(EventBufferReadState.Empty, 0, producer: 537, deleted: 537, empty: true))));

        var analysis = Analyzer().Analyze(trace);

        Assert.Equal(EvidenceAnalysisAssessment.Healthy, analysis.Assessment);
        Assert.Equal(1002, analysis.AnalyzedFromSequence);
        var incident = Assert.Single(analysis.Incidents);
        Assert.Equal(EvidenceIncidentClassification.EventBufferProgress, incident.Classification);
        Assert.Equal(EvidenceIncidentSeverity.Information, incident.Severity);
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "event-buffer-catch-up");
        Assert.Contains(incident.DeterministicConclusions, conclusion =>
            conclusion.Message.Contains("does not prove the PLC-internal cause", StringComparison.Ordinal));
    }

    [Fact]
    public void Large_temporary_backlog_reports_recovery_and_cyclic_request_check()
    {
        var trace = Trace(
            Activity(1, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 4363, deleted: 4135))),
            Activity(2, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 4369, deleted: 4167))),
            Activity(3, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 4369, deleted: 4231))),
            Activity(4, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 4369, deleted: 4295))),
            Activity(5, new EventStreamActivityPayload(EventStream(EventBufferReadState.Empty, 0, producer: 4369, deleted: 4369, empty: true))));

        var analysis = Analyzer().Analyze(trace);

        Assert.Equal(EvidenceAnalysisAssessment.Warning, analysis.Assessment);
        var incident = Assert.Single(analysis.Incidents);
        Assert.Equal(EvidenceIncidentClassification.EventBufferProgress, incident.Classification);
        Assert.Contains(incident.ObservedFacts, fact =>
            fact.Code == "event-buffer-temporary-backlog"
            && fact.Message.Contains("228", StringComparison.Ordinal));
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "event-buffer-backlog-drained");
        Assert.Contains(incident.DeterministicConclusions, conclusion =>
            conclusion.Message.Contains("does not prove the PLC-internal cause", StringComparison.Ordinal));
        Assert.Contains(incident.SuggestedChecks, check =>
            check.Code == "verify-cyclic-event-buffer-requests"
            && check.Message == "Verify that PLC-side EventBuffer request processing executes cyclically without conditional interruption.");
    }

    [Fact]
    public void Small_temporary_backlog_does_not_create_a_progress_finding()
    {
        var trace = Trace(
            Activity(1, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 110, deleted: 100))),
            Activity(2, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 112, deleted: 106))),
            Activity(3, new EventStreamActivityPayload(EventStream(EventBufferReadState.Empty, 0, producer: 112, deleted: 112, empty: true))));

        var analysis = Analyzer().Analyze(trace);

        Assert.Equal(EvidenceAnalysisAssessment.Healthy, analysis.Assessment);
        Assert.DoesNotContain(analysis.Incidents, incident =>
            incident.Classification == EvidenceIncidentClassification.EventBufferProgress);
    }

    [Fact]
    public void Producer_loss_does_not_add_a_recovered_backlog_finding()
    {
        var trace = Trace(
            Activity(1, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 400, deleted: 150))),
            Activity(2, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 3, producer: 406, deleted: 182))),
            Activity(3, new EventStreamActivityPayload(EventStream(EventBufferReadState.Empty, 3, producer: 406, deleted: 406, empty: true))));

        var analysis = Analyzer().Analyze(trace);

        Assert.DoesNotContain(analysis.Incidents, incident =>
            incident.Classification == EvidenceIncidentClassification.EventBufferProgress);
    }

    [Fact]
    public void Sparse_event_buffer_endpoints_do_not_claim_a_continuous_deletion_stall()
    {
        var trace = Trace(
            Activity(1, new EventStreamActivityPayload(EventStream(EventBufferReadState.Empty, 0, producer: 289, deleted: 289, empty: true))),
            Activity(13001, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 406, deleted: 289))),
            Activity(13491, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 52, producer: 606, deleted: 321))));

        var analysis = Analyzer().Analyze(trace);

        Assert.DoesNotContain(analysis.Incidents, incident =>
            incident.Classification == EvidenceIncidentClassification.EventBufferProgress);
    }

    [Fact]
    public void Repeated_unchanged_lost_event_evidence_is_compacted_in_findings()
    {
        var trace = Trace(
            Activity(1, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 52, producer: 600, deleted: 300))),
            Activity(2, Diagnostic("Workflow.EventStreamProcessor.LostEvents", "52 lost events.")),
            Activity(3, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 52, producer: 700, deleted: 332))),
            Activity(4, Diagnostic("Workflow.EventStreamProcessor.LostEvents", "52 lost events.")),
            Activity(5, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 52, producer: 800, deleted: 364))),
            Activity(6, Diagnostic("Workflow.EventStreamProcessor.LostEvents", "52 lost events.")));

        var incident = Assert.Single(Analyzer().Analyze(trace).Incidents.Where(item =>
            item.Classification == EvidenceIncidentClassification.EvidenceIncomplete));

        Assert.Equal(4, incident.ObservedFacts.Count(fact => fact.Code == "communication-evidence-incomplete"));
    }

    [Fact]
    public void Counter_reset_and_current_progress_are_summarized_deterministically()
    {
        var trace = Trace(
            Activity(1, new EventStreamActivityPayload(EventStream(EventBufferReadState.Valid, 0, producer: 100, deleted: 90))),
            Activity(2, new EventStreamActivityPayload(EventStream(EventBufferReadState.Empty, 0, producer: 0, deleted: 0, empty: true))),
            Activity(3, new EventStreamActivityPayload(EventStream(EventBufferReadState.Empty, 0, producer: 5, deleted: 5, empty: true))));

        var analysis = Analyzer().Analyze(trace);

        Assert.Equal(EvidenceAnalysisAssessment.Healthy, analysis.Assessment);
        Assert.Contains("producer 5, deleted 5, current backlog 0", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("counter resets 1", analysis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Buffer_full_degrades_event_buffer_health_without_claiming_runtime_failure()
    {
        var state = EventStream(EventBufferReadState.Valid, 0, producer: 355, deleted: 100) with
        {
            BufferFull = true
        };

        var analysis = Analyzer().Analyze(Trace(Activity(1, new EventStreamActivityPayload(state))));

        Assert.Equal(EvidenceAnalysisAssessment.Warning, analysis.Assessment);
        var incident = Assert.Single(analysis.Incidents);
        Assert.Equal(EvidenceIncidentClassification.EventBufferCapacity, incident.Classification);
        Assert.Contains(incident.DeterministicConclusions, conclusion =>
            conclusion.Message.Contains("does not prove loss of Runtime access", StringComparison.Ordinal));
    }

    [Fact]
    public void Failed_release_is_reported_as_event_buffer_progress_not_total_communication_failure()
    {
        var state = EventStream(EventBufferReadState.Valid, 0, producer: 40, deleted: 8) with
        {
            BatchReleaseState = EventStreamBatchReleaseState.Failed
        };

        var analysis = Analyzer().Analyze(Trace(Activity(1, new EventStreamActivityPayload(state))));

        Assert.Equal(EvidenceAnalysisAssessment.Warning, analysis.Assessment);
        var incident = Assert.Single(analysis.Incidents);
        Assert.Equal(EvidenceIncidentClassification.EventBufferProgress, incident.Classification);
        Assert.Contains(incident.ObservedFacts, fact => fact.Code == "event-buffer-release-failed");
    }

    [Fact]
    public void Evicted_history_before_recovered_baseline_does_not_make_current_episode_incomplete()
    {
        var trace = Trace(
            [
                Activity(6, Access(AccessHealthState.RevalidationRequired, AccessHealthProblem.RevalidationRequired, "Stale.")),
                Activity(7, Access(AccessHealthState.Healthy, AccessHealthProblem.None, "Recovered.")),
                Activity(8, new EventStreamActivityPayload(EventStream(EventBufferReadState.Empty, 0, empty: true)))
            ],
            evicted: 5);

        var analysis = Analyzer().Analyze(trace);

        Assert.Equal(EvidenceCompleteness.Complete, analysis.Completeness);
        Assert.Equal(EvidenceAnalysisAssessment.Healthy, analysis.Assessment);
        Assert.Contains(analysis.Incidents.SelectMany(static incident => incident.ObservedFacts), fact => fact.Code == "historical-trace-prefix-evicted");
    }

    private static CommunicationSessionEvidenceAnalyzer Analyzer() => new(FakeTime.Create());

    private static UnitTestActivityTraceSnapshot Trace(params UnitTestActivityEntry[] entries) => Trace(entries, 0);

    private static UnitTestActivityTraceSnapshot Trace(IReadOnlyList<UnitTestActivityEntry> entries, long evicted) =>
        new(
            new ApplicationSessionId("session"),
            Timestamp(),
            Timestamp().AddSeconds(5),
            Timestamp().AddSeconds(5),
            IsCaptureActive: false,
            entries.Count == 0 ? 0 : entries[0].Sequence,
            entries.Count == 0 ? 0 : entries[^1].Sequence,
            evicted,
            entries);

    private static UnitTestActivityEntry Activity(
        long sequence,
        UnitTestActivityPayload payload,
        string communicationSessionId = "communication") =>
        new(
            sequence,
            Timestamp().AddMilliseconds(sequence),
            new CommunicationSessionId(communicationSessionId),
            null,
            null,
            new EventStreamContextId($"{communicationSessionId}-stream"),
            null,
            payload);

    private static AccessHealthActivityPayload Access(
        AccessHealthState state,
        AccessHealthProblem problem,
        string message,
        AccessServiceKind serviceKind = AccessServiceKind.UnitTestEventStream) =>
        new(serviceKind, new AccessHealth(state, problem, $"AccessHealth.{state}", message, Timestamp()));

    private static DiagnosticActivityPayload Diagnostic(string code, string message) =>
        new(new Diagnostic(
            code,
            DiagnosticCategory.Workflow,
            LogLevel.Warning,
            DiagnosticSource.SharedWorkflow,
            DiagnosticPhase.ExecutionMonitoring,
            message,
            new DiagnosticCorrelation(SessionId: new ApplicationSessionId("session")),
            Timestamp()));

    private static EventStreamState EventStream(
        EventBufferReadState readState,
        ushort lostEvents,
        uint producer = 0,
        uint deleted = 0,
        bool empty = false) =>
        new(
            new CommunicationSessionId("communication"),
            IsMonitoring: true,
            readState,
            EventStreamBatchReleaseState.Completed,
            BufferObservedEmpty: empty,
            LostEventCount: lostEvents,
            ProducerSequenceId: producer,
            DeletedSequenceId: deleted);

    private static DateTimeOffset Timestamp() => new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
}
