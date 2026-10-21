using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.CommonContracts;

public sealed class DiagnosticContractTests
{
    [Fact]
    public void Diagnostic_uses_logging_level_and_structured_correlation()
    {
        var timestamp = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var diagnostic = new Diagnostic(
            "Runtime.Start.Timeout",
            DiagnosticCategory.Workflow,
            LogLevel.Error,
            DiagnosticSource.SharedWorkflow,
            DiagnosticPhase.RuntimeStartup,
            "The PLC runtime did not reach running state in time.",
            new DiagnosticCorrelation(
                OperationId: new OperationId("op-1"),
                RuntimeTargetId: new RuntimeTargetId("plc-1")),
            timestamp,
            TechnicalDetail: "Timeout after 00:01:00.",
            EvidenceReference: new DiagnosticEvidenceReference("operation", "op-1"));

        Assert.Equal(LogLevel.Error, diagnostic.Level);
        Assert.Equal(DiagnosticCategory.Workflow, diagnostic.Category);
        Assert.Equal("op-1", diagnostic.Correlation.OperationId?.Value);
        Assert.Equal("plc-1", diagnostic.Correlation.RuntimeTargetId?.Value);
        Assert.Equal("operation", diagnostic.EvidenceReference?.Kind);
        Assert.Equal(timestamp, diagnostic.Timestamp);
    }

    [Fact]
    public void Diagnostic_fingerprint_ignores_message_detail_evidence_timestamp_and_emission_id()
    {
        var first = CreateDiagnostic(
            timestamp: new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero),
            message: "First message.",
            technicalDetail: "First detail.",
            evidenceReference: new DiagnosticEvidenceReference("event", "1"),
            id: "emission-1");
        var repeated = CreateDiagnostic(
            timestamp: new DateTimeOffset(2026, 6, 19, 12, 1, 0, TimeSpan.Zero),
            message: "Updated message.",
            technicalDetail: "Updated detail.",
            evidenceReference: new DiagnosticEvidenceReference("event", "2"),
            id: "emission-2");

        var firstFingerprint = DiagnosticFingerprint.FromDiagnostic(first);
        var repeatedFingerprint = DiagnosticFingerprint.FromDiagnostic(repeated);

        Assert.Equal(firstFingerprint, repeatedFingerprint);
    }

    [Fact]
    public void Diagnostic_fingerprint_includes_structured_correlation_scope()
    {
        var first = CreateDiagnostic(
            correlation: new DiagnosticCorrelation(
                OperationId: new OperationId("op-1"),
                RuntimeTargetId: new RuntimeTargetId("runtime-a")));
        var second = CreateDiagnostic(
            correlation: new DiagnosticCorrelation(
                OperationId: new OperationId("op-1"),
                RuntimeTargetId: new RuntimeTargetId("runtime-b")));

        var firstFingerprint = DiagnosticFingerprint.FromDiagnostic(first);
        var secondFingerprint = DiagnosticFingerprint.FromDiagnostic(second);

        Assert.NotEqual(firstFingerprint, secondFingerprint);
        Assert.Equal("runtime-a", firstFingerprint.CorrelationScope.RuntimeTargetId?.Value);
        Assert.Equal("runtime-b", secondFingerprint.CorrelationScope.RuntimeTargetId?.Value);
    }

    [Fact]
    public void Diagnostic_fingerprint_ignores_volatile_correlation_values()
    {
        var first = CreateDiagnostic(
            correlation: new DiagnosticCorrelation(
                OperationId: new OperationId("op-1"),
                RuntimeTargetId: new RuntimeTargetId("runtime-a"),
                UnitTestRunEpochId: new RunEpochId("epoch-1"),
                LifecycleObservationId: new LifecycleObservationId("lifecycle-1"),
                EventStreamContextId: new EventStreamContextId("event-stream-1"),
                EvidenceSnapshotId: new EvidenceSnapshotId("evidence-1"),
                OutputRequestId: new OutputRequestId("output-1")));
        var repeated = CreateDiagnostic(
            correlation: new DiagnosticCorrelation(
                OperationId: new OperationId("op-2"),
                RuntimeTargetId: new RuntimeTargetId("runtime-a"),
                UnitTestRunEpochId: new RunEpochId("epoch-2"),
                LifecycleObservationId: new LifecycleObservationId("lifecycle-2"),
                EventStreamContextId: new EventStreamContextId("event-stream-2"),
                EvidenceSnapshotId: new EvidenceSnapshotId("evidence-2"),
                OutputRequestId: new OutputRequestId("output-2")));

        var firstFingerprint = DiagnosticFingerprint.FromDiagnostic(first);
        var repeatedFingerprint = DiagnosticFingerprint.FromDiagnostic(repeated);

        Assert.Equal(firstFingerprint, repeatedFingerprint);
        Assert.Null(firstFingerprint.CorrelationScope.OperationId);
        Assert.Null(firstFingerprint.CorrelationScope.UnitTestRunEpochId);
        Assert.Null(firstFingerprint.CorrelationScope.LifecycleObservationId);
        Assert.Null(firstFingerprint.CorrelationScope.EventStreamContextId);
        Assert.Null(firstFingerprint.CorrelationScope.EvidenceSnapshotId);
        Assert.Null(firstFingerprint.CorrelationScope.OutputRequestId);
        Assert.Equal("runtime-a", firstFingerprint.CorrelationScope.RuntimeTargetId?.Value);
    }

    [Fact]
    public void Diagnostic_correlation_scope_from_correlation_preserves_all_context_values()
    {
        var correlation = new DiagnosticCorrelation(
            SessionId: new ApplicationSessionId("session-1"),
            WorkflowRunId: new WorkflowRunId("workflow-1"),
            OperationId: new OperationId("operation-1"),
            AccessProfileId: "fake",
            ServiceId: "unit-test-lifecycle",
            ProjectContextId: new ProjectContextId("project-context-1"),
            RuntimeTargetId: new RuntimeTargetId("runtime-1"),
            UnitTestRunEpochId: new RunEpochId("epoch-1"),
            LifecycleObservationId: new LifecycleObservationId("lifecycle-1"),
            EventStreamContextId: new EventStreamContextId("event-stream-1"),
            EvidenceSnapshotId: new EvidenceSnapshotId("evidence-1"),
            OutputRequestId: new OutputRequestId("output-1"));

        var scope = DiagnosticCorrelationScope.FromCorrelation(correlation);

        Assert.Equal("session-1", scope.SessionId?.Value);
        Assert.Equal("workflow-1", scope.WorkflowRunId?.Value);
        Assert.Equal("operation-1", scope.OperationId?.Value);
        Assert.Equal("fake", scope.AccessProfileId);
        Assert.Equal("unit-test-lifecycle", scope.ServiceId);
        Assert.Equal("project-context-1", scope.ProjectContextId?.Value);
        Assert.Equal("runtime-1", scope.RuntimeTargetId?.Value);
        Assert.Equal("epoch-1", scope.UnitTestRunEpochId?.Value);
        Assert.Equal("lifecycle-1", scope.LifecycleObservationId?.Value);
        Assert.Equal("event-stream-1", scope.EventStreamContextId?.Value);
        Assert.Equal("evidence-1", scope.EvidenceSnapshotId?.Value);
        Assert.Equal("output-1", scope.OutputRequestId?.Value);
    }

    [Fact]
    public void Diagnostic_correlation_identity_scope_keeps_only_stable_coalescing_context()
    {
        var correlation = new DiagnosticCorrelation(
            SessionId: new ApplicationSessionId("session-1"),
            WorkflowRunId: new WorkflowRunId("workflow-1"),
            OperationId: new OperationId("operation-1"),
            AccessProfileId: "fake",
            ServiceId: "unit-test-lifecycle",
            ProjectContextId: new ProjectContextId("project-context-1"),
            RuntimeTargetId: new RuntimeTargetId("runtime-1"),
            UnitTestRunEpochId: new RunEpochId("epoch-1"),
            LifecycleObservationId: new LifecycleObservationId("lifecycle-1"),
            EventStreamContextId: new EventStreamContextId("event-stream-1"),
            EvidenceSnapshotId: new EvidenceSnapshotId("evidence-1"),
            OutputRequestId: new OutputRequestId("output-1"));

        var scope = DiagnosticCorrelationScope.FromCorrelationIdentity(correlation);

        Assert.Null(scope.SessionId);
        Assert.Null(scope.WorkflowRunId);
        Assert.Null(scope.OperationId);
        Assert.Equal("fake", scope.AccessProfileId);
        Assert.Equal("unit-test-lifecycle", scope.ServiceId);
        Assert.Equal("project-context-1", scope.ProjectContextId?.Value);
        Assert.Equal("runtime-1", scope.RuntimeTargetId?.Value);
        Assert.Null(scope.UnitTestRunEpochId);
        Assert.Null(scope.LifecycleObservationId);
        Assert.Null(scope.EventStreamContextId);
        Assert.Null(scope.EvidenceSnapshotId);
        Assert.Null(scope.OutputRequestId);
    }

    [Fact]
    public void Diagnostic_correlation_scope_normalizes_blank_text_context()
    {
        var correlation = new DiagnosticCorrelation(
            AccessProfileId: " ",
            ServiceId: "\t");

        var fullScope = DiagnosticCorrelationScope.FromCorrelation(correlation);
        var identityScope = DiagnosticCorrelationScope.FromCorrelationIdentity(correlation);

        Assert.Null(fullScope.AccessProfileId);
        Assert.Null(fullScope.ServiceId);
        Assert.Null(identityScope.AccessProfileId);
        Assert.Null(identityScope.ServiceId);
    }

    [Fact]
    public void Diagnostic_correlation_scope_rejects_null_correlation()
    {
        Assert.Throws<ArgumentNullException>(() => DiagnosticCorrelationScope.FromCorrelation(null!));
        Assert.Throws<ArgumentNullException>(() => DiagnosticCorrelationScope.FromCorrelationIdentity(null!));
    }

    [Fact]
    public void Diagnostic_entry_tracks_first_last_and_occurrence_count()
    {
        var firstTimestamp = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var secondTimestamp = new DateTimeOffset(2026, 6, 19, 12, 2, 0, TimeSpan.Zero);
        var first = CreateDiagnostic(firstTimestamp, "First message.");
        var second = CreateDiagnostic(secondTimestamp, "Second message.");

        var entry = DiagnosticEntry.Create(first).ApplyOccurrence(second);

        Assert.Equal(firstTimestamp, entry.FirstSeenAt);
        Assert.Equal(secondTimestamp, entry.LastSeenAt);
        Assert.Equal(2, entry.OccurrenceCount);
        Assert.Equal("Second message.", entry.Current.Message);
    }

    [Fact]
    public void Diagnostic_entry_does_not_move_current_or_last_seen_backwards()
    {
        var firstTimestamp = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var secondTimestamp = new DateTimeOffset(2026, 6, 19, 12, 2, 0, TimeSpan.Zero);
        var olderTimestamp = new DateTimeOffset(2026, 6, 19, 12, 1, 0, TimeSpan.Zero);
        var first = CreateDiagnostic(firstTimestamp, "First message.");
        var second = CreateDiagnostic(secondTimestamp, "Second message.");
        var older = CreateDiagnostic(olderTimestamp, "Older repeated message.");

        var entry = DiagnosticEntry.Create(first)
            .ApplyOccurrence(second)
            .ApplyOccurrence(older);

        Assert.Equal(firstTimestamp, entry.FirstSeenAt);
        Assert.Equal(secondTimestamp, entry.LastSeenAt);
        Assert.Equal(3, entry.OccurrenceCount);
        Assert.Equal("Second message.", entry.Current.Message);
    }

    [Fact]
    public void Diagnostic_entry_rejects_different_fingerprint_occurrence()
    {
        var entry = DiagnosticEntry.Create(CreateDiagnostic(
            correlation: new DiagnosticCorrelation(RuntimeTargetId: new RuntimeTargetId("runtime-a"))));
        var different = CreateDiagnostic(
            correlation: new DiagnosticCorrelation(RuntimeTargetId: new RuntimeTargetId("runtime-b")));

        var exception = Assert.Throws<InvalidOperationException>(() => entry.ApplyOccurrence(different));

        Assert.Equal("Diagnostic occurrence fingerprint does not match the coalesced entry.", exception.Message);
    }

    [Fact]
    public void Diagnostic_snapshot_empty_exposes_empty_entry_list_and_update_time()
    {
        var updatedAt = new DateTimeOffset(2026, 6, 19, 12, 3, 0, TimeSpan.Zero);

        var snapshot = DiagnosticSnapshot.Empty(updatedAt);

        Assert.Empty(snapshot.Entries);
        Assert.Equal(updatedAt, snapshot.UpdatedAt);
    }

    private static Diagnostic CreateDiagnostic(
        DateTimeOffset? timestamp = null,
        string message = "Runtime diagnostic.",
        string? technicalDetail = null,
        DiagnosticEvidenceReference? evidenceReference = null,
        string? id = null,
        DiagnosticCorrelation? correlation = null)
    {
        return new Diagnostic(
            "Runtime.Start.Timeout",
            DiagnosticCategory.Workflow,
            LogLevel.Error,
            DiagnosticSource.SharedWorkflow,
            DiagnosticPhase.RuntimeStartup,
            message,
            correlation ?? new DiagnosticCorrelation(
                OperationId: new OperationId("op-1"),
                RuntimeTargetId: new RuntimeTargetId("runtime-a")),
            timestamp ?? new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero),
            id,
            technicalDetail,
            evidenceReference);
    }
}
