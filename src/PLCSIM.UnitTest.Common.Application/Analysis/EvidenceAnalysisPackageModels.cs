using PLCSIM.UnitTest.Common.Identifiers;

namespace PLCSIM.UnitTest.Common.Analysis;

/// <summary>
/// Correlation metadata shared by every document in an immutable analysis package.
/// </summary>
public sealed record EvidenceAnalysisPackageMetadata(
    EvidenceAnalysisPackageId PackageId,
    EvidenceSnapshotId SnapshotId,
    ApplicationSessionId SessionId,
    EvidenceAnalysisScope Scope,
    RunEpochId? RunEpochId,
    LifecycleObservationId? LifecycleObservationId,
    DateTimeOffset CapturedAt,
    long ActivityFromSequence,
    long ActivityThroughSequence,
    EvidenceCompleteness Completeness);

/// <summary>
/// One self-contained analysis handoff created from a single activity-trace snapshot.
/// </summary>
public sealed record EvidenceAnalysisPackage(
    EvidenceAnalysisPackageMetadata Metadata,
    EvidenceAnalysisSnapshot Analysis,
    string AnalysisMarkdown,
    string? HierarchyJson,
    string EventsNdjson);
