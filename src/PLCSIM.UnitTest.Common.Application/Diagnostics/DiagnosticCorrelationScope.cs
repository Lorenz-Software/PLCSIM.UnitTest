using PLCSIM.UnitTest.Common.Identifiers;

namespace PLCSIM.UnitTest.Common.Diagnostics;

/// <summary>
/// Defines the structured context used to separate coalesced diagnostic entries.
/// </summary>
public sealed record DiagnosticCorrelationScope(
    ApplicationSessionId? SessionId = null,
    WorkflowRunId? WorkflowRunId = null,
    OperationId? OperationId = null,
    string? AccessProfileId = null,
    string? ServiceId = null,
    ProjectContextId? ProjectContextId = null,
    RuntimeTargetId? RuntimeTargetId = null,
    RunEpochId? UnitTestRunEpochId = null,
    LifecycleObservationId? LifecycleObservationId = null,
    EventStreamContextId? EventStreamContextId = null,
    EvidenceSnapshotId? EvidenceSnapshotId = null,
    OutputRequestId? OutputRequestId = null)
{
    public static DiagnosticCorrelationScope FromCorrelation(DiagnosticCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);

        return new DiagnosticCorrelationScope(
            correlation.SessionId,
            correlation.WorkflowRunId,
            correlation.OperationId,
            Normalize(correlation.AccessProfileId),
            Normalize(correlation.ServiceId),
            correlation.ProjectContextId,
            correlation.RuntimeTargetId,
            correlation.UnitTestRunEpochId,
            correlation.LifecycleObservationId,
            correlation.EventStreamContextId,
            correlation.EvidenceSnapshotId,
            correlation.OutputRequestId);
    }

    public static DiagnosticCorrelationScope FromCorrelationIdentity(DiagnosticCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);

        return new DiagnosticCorrelationScope(
            AccessProfileId: Normalize(correlation.AccessProfileId),
            ServiceId: Normalize(correlation.ServiceId),
            ProjectContextId: correlation.ProjectContextId,
            RuntimeTargetId: correlation.RuntimeTargetId);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
