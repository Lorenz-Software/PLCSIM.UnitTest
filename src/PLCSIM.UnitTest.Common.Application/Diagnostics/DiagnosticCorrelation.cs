using PLCSIM.UnitTest.Common.Identifiers;

namespace PLCSIM.UnitTest.Common.Diagnostics;

/// <summary>
/// Carries optional identifiers that connect a diagnostic to workflow, service, run, evidence, or output context.
/// </summary>
public sealed record DiagnosticCorrelation(
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
    OutputRequestId? OutputRequestId = null);
