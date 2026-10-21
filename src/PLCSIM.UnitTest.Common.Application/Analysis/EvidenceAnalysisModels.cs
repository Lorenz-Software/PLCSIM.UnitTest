using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Common.Analysis;

/// <summary>
/// Immutable application-facing snapshot of retained evidence-analysis incidents.
/// </summary>
public sealed record EvidenceAnalysisSnapshot(
    EvidenceSnapshotId Id,
    ApplicationSessionId SessionId,
    EvidenceAnalysisScope Scope,
    RunEpochId? RunEpochId,
    LifecycleObservationId? LifecycleObservationId,
    DateTimeOffset GeneratedAt,
    long AnalyzedFromSequence,
    long AnalyzedThroughSequence,
    EvidenceCompleteness Completeness,
    IReadOnlyList<EvidenceIncident> Incidents,
    EvidenceAnalysisAssessment Assessment = EvidenceAnalysisAssessment.Inconclusive,
    string Summary = "")
{
    public static EvidenceAnalysisSnapshot Empty(
        EvidenceSnapshotId id,
        ApplicationSessionId sessionId,
        EvidenceAnalysisScope scope,
        RunEpochId? runEpochId,
        LifecycleObservationId? lifecycleObservationId,
        DateTimeOffset generatedAt,
        long analyzedFromSequence,
        long analyzedThroughSequence,
        EvidenceCompleteness completeness)
    {
        return new EvidenceAnalysisSnapshot(
            id,
            sessionId,
            scope,
            runEpochId,
            lifecycleObservationId,
            generatedAt,
            analyzedFromSequence,
            analyzedThroughSequence,
            completeness,
            Array.Empty<EvidenceIncident>());
    }
}

/// <summary>
/// Deterministic interpretation of one bounded set of observed evidence.
/// </summary>
public sealed record EvidenceIncident(
    EvidenceIncidentId Id,
    EvidenceIncidentClassification Classification,
    EvidenceIncidentSeverity Severity,
    EvidenceCompleteness Completeness,
    EvidenceIncidentScope Scope,
    string Title,
    string Summary,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    RunEvidenceAnalysis? Run,
    LifecycleEvidenceAnalysis? Lifecycle,
    IReadOnlyList<EvidenceAffectedOwner> AffectedOwners,
    IReadOnlyList<EvidenceAnalysisStatement> ObservedFacts,
    IReadOnlyList<EvidenceAnalysisStatement> DeterministicConclusions,
    IReadOnlyList<EvidenceSuggestedCheck> SuggestedChecks,
    IReadOnlyList<EvidenceTimelineItem> Timeline);

/// <summary>
/// Run-specific correlation attached to an incident.
/// </summary>
public sealed record RunEvidenceAnalysis(
    RunEpochId EpochId,
    UnitTestRunState State,
    UnitTestResultState Result,
    EvidenceState EvidenceState,
    ushort LostEventCount)
{
    public UnitTestResultState? PlcReportedResult { get; init; }
}

/// <summary>
/// Lifecycle-window correlation attached to an incident.
/// </summary>
public sealed record LifecycleEvidenceAnalysis(
    LifecycleObservationId ObservationId,
    LifecycleAnchorKind Anchor,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    bool? ReachedIdleAndReady);

/// <summary>
/// Reference to an affected UnitTest owner without duplicating its run-tree payload.
/// </summary>
public sealed record EvidenceAffectedOwner(
    UnitTestNodeId Id,
    UnitTestNodeKind Kind,
    string DisplayName);

/// <summary>
/// One observed fact or deterministic conclusion and its supporting references.
/// </summary>
public sealed record EvidenceAnalysisStatement(
    string Code,
    string Message,
    IReadOnlyList<EvidenceReference> Evidence);

/// <summary>
/// A non-causal next check offered to the operator.
/// </summary>
public sealed record EvidenceSuggestedCheck(
    string Code,
    string Message);

/// <summary>
/// One ordered item in the compact incident timeline.
/// </summary>
public sealed record EvidenceTimelineItem(
    DateTimeOffset Timestamp,
    EvidenceTimelineKind Kind,
    string Message,
    IReadOnlyList<EvidenceReference> Evidence);

/// <summary>
/// Stable reference to retained evidence owned elsewhere.
/// </summary>
public sealed record EvidenceReference(
    long ActivitySequence,
    uint? PlcEventSequenceId = null);

public enum EvidenceAnalysisScope
{
    Session,
    Run,
    Lifecycle
}

public enum EvidenceAnalysisAssessment
{
    Healthy,
    Warning,
    Error,
    Inconclusive
}

public enum EvidenceIncidentClassification
{
    Unknown,
    RunAborted,
    TestFailure,
    LifecycleReactionFailure,
    EvidenceIncomplete,
    CommunicationFailure,
    CommandTransportFailure,
    ContractConflict,
    EventBufferCapacity,
    EventBufferProgress,
    RunObservationTimeout
}

public enum EvidenceIncidentSeverity
{
    Information,
    Warning,
    Error
}

public enum EvidenceCompleteness
{
    Complete,
    Incomplete,
    Unavailable
}

public enum EvidenceIncidentScope
{
    Session,
    Run,
    Lifecycle
}

public enum LifecycleAnchorKind
{
    CommunicationSessionStarted,
    PlcOperatingStateChanged,
    StartCommand,
    AbortCommand,
    ResetCommand,
    EventBufferResetCommand,
    RunEpochStarted,
    RunEpochEnded,
    CommunicationRevalidation,
    CommunicationRecovered,
    SessionEnded,
    PlcStartCommand
}

public enum EvidenceTimelineKind
{
    LifecycleAnchor,
    PlcEvent,
    CommandTransport,
    RunControlObservation,
    EventStreamState,
    CommunicationHealth,
    Diagnostic,
    Analysis
}
