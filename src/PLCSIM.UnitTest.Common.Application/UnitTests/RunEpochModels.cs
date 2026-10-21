using PLCSIM.UnitTest.Common.Identifiers;

namespace PLCSIM.UnitTest.Common.UnitTests;

/// <summary>
/// Stable, read-only projection of one observed UnitTest run epoch.
/// </summary>
public sealed class UnitTestRunEpochSnapshot
{
    public UnitTestRunEpochSnapshot(
        RunEpochId epochId,
        UnitTestRunState state,
        UnitTestResultState result,
        EvidenceState evidenceState,
        DateTimeOffset? createdAt,
        DateTimeOffset? lastUpdatedAt,
        IReadOnlyList<UnitTestEventEntry> eventStream,
        IReadOnlyList<UnitTestEventRef> frameworkEvents,
        UnitTestRunNode root,
        ushort lostEventCount = 0)
    {
        EpochId = epochId;
        State = state;
        Result = result;
        EvidenceState = evidenceState;
        CreatedAt = createdAt;
        LastUpdatedAt = lastUpdatedAt;
        EventStream = eventStream;
        FrameworkEvents = frameworkEvents;
        Root = root;
        LostEventCount = lostEventCount;
    }

    public RunEpochId EpochId { get; }
    public UnitTestRunState State { get; }
    public UnitTestResultState Result { get; }
    public EvidenceState EvidenceState { get; }
    public DateTimeOffset? CreatedAt { get; }
    public DateTimeOffset? LastUpdatedAt { get; }
    public IReadOnlyList<UnitTestEventEntry> EventStream { get; }
    public IReadOnlyList<UnitTestEventRef> FrameworkEvents { get; }
    public UnitTestRunNode Root { get; }
    public ushort LostEventCount { get; }
    public bool HasLostEvents => LostEventCount > 0;
}

/// <summary>
/// Structured node in the neutral UnitTest run hierarchy.
/// </summary>
public sealed record UnitTestRunNode(
    UnitTestNodeId Id,
    string Name,
    string DisplayName,
    UnitTestNodeKind Kind,
    UnitTestLifecycleState LifecycleState,
    UnitTestResultState Result,
    EvidenceState EvidenceState,
    UnitTestAggregation? Aggregation,
    UnitTestEventRef? CreationEvent,
    UnitTestEventRef? CompletionEvent,
    UnitTestEventRef? ResultEvent,
    UnitTestEventRef? AggregationEvent,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastUpdatedAt,
    IReadOnlyList<UnitTestFinding> Findings,
    IReadOnlyList<UnitTestRunNode> Children)
{
    /// <summary>
    /// Result carried by the PLC owner's own Result event. <see cref="Result"/> remains the
    /// effective PC-side result after child-result escalation.
    /// </summary>
    public UnitTestResultState? PlcReportedResult { get; init; }
}

/// <summary>
/// Parsed aggregation values for a UnitTest owner node.
/// </summary>
public sealed record UnitTestAggregation(
    uint Total,
    uint? Inactive,
    uint? Idle,
    uint? Running,
    uint? Completed,
    uint Passed,
    uint Failed,
    uint Error,
    uint? Runtime,
    uint? LostEvents);

/// <summary>
/// Finding attached to a UnitTest run node, for example an assertion failure or consistency issue.
/// </summary>
public sealed record UnitTestFinding(
    UnitTestFindingKind Kind,
    UnitTestResultState Result,
    string Name,
    string Message,
    UnitTestEventRef? SourceEvent);

/// <summary>
/// Reference to an event in a run epoch event stream.
/// </summary>
public sealed record UnitTestEventRef(
    uint SequenceId);

/// <summary>
/// PC-side interpretation state of an observed UnitTest run epoch.
/// </summary>
public enum UnitTestRunState
{
    NotObserved,
    Observing,
    Running,
    Completing,
    Completed,
    Aborted,
    TimedOut,
    Invalidated
}

/// <summary>
/// PC-side evidence completeness and consistency state.
/// </summary>
public enum EvidenceState
{
    Unknown,
    None,
    Partial,
    Complete,
    Incomplete,
    Ambiguous,
    Conflicting
}

/// <summary>
/// Supported node levels in the neutral UnitTest run hierarchy.
/// </summary>
public enum UnitTestNodeKind
{
    RunControl,
    TestSuite,
    TestCase
}

/// <summary>
/// Classification of findings attached to UnitTest run nodes.
/// </summary>
public enum UnitTestFindingKind
{
    Assertion,
    OwnerResult,
    Framework,
    Consistency,
    MissingEvidence
}
