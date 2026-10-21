using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Common.Analysis;

/// <summary>
/// Immutable view of the bounded UnitTest activity retained for one application session.
/// </summary>
public sealed record UnitTestActivityTraceSnapshot(
    ApplicationSessionId SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset CapturedAt,
    bool IsCaptureActive,
    long OldestSequence,
    long NewestSequence,
    long EvictedEntryCount,
    IReadOnlyList<UnitTestActivityEntry> Entries);

/// <summary>
/// One neutral application observation retained for later on-demand analysis.
/// </summary>
public sealed record UnitTestActivityEntry(
    long Sequence,
    DateTimeOffset ObservedAt,
    CommunicationSessionId? CommunicationSessionId,
    RunEpochId? RunEpochId,
    LifecycleObservationId? LifecycleObservationId,
    EventStreamContextId? EventStreamContextId,
    OperationId? OperationId,
    UnitTestActivityPayload Payload)
{
    public UnitTestActivityKind Kind => Payload.Kind;
}

/// <summary>
/// Base contract for factual activity payloads. Payloads never contain analysis results.
/// </summary>
public abstract record UnitTestActivityPayload
{
    public abstract UnitTestActivityKind Kind { get; }
}

public sealed record SessionActivityPayload(UnitTestSessionActivityKind Activity) : UnitTestActivityPayload
{
    public override UnitTestActivityKind Kind => UnitTestActivityKind.Session;
}

public sealed record PlcStateActivityPayload(PlcState State) : UnitTestActivityPayload
{
    public override UnitTestActivityKind Kind => UnitTestActivityKind.PlcState;
}

public sealed record CommandActivityPayload(
    UnitTestActivityCommandKind Command,
    UnitTestActivityCommandPhase Phase,
    OperationResultState? ResultState = null,
    bool? Accepted = null,
    string? Message = null) : UnitTestActivityPayload
{
    public override UnitTestActivityKind Kind => UnitTestActivityKind.Command;
}

public sealed record RunControlActivityPayload(UnitTestRunControlObservation Observation) : UnitTestActivityPayload
{
    public override UnitTestActivityKind Kind => UnitTestActivityKind.RunControl;
}

public sealed record EventStreamActivityPayload(EventStreamState State) : UnitTestActivityPayload
{
    public override UnitTestActivityKind Kind => UnitTestActivityKind.EventStream;
}

public sealed record PlcEventActivityPayload(UnitTestEventEntry Event) : UnitTestActivityPayload
{
    public override UnitTestActivityKind Kind => UnitTestActivityKind.PlcEvent;
}

public sealed record RunEpochActivityPayload(
    UnitTestRunEpochActivityKind Activity,
    UnitTestRunEpochSnapshot Snapshot) : UnitTestActivityPayload
{
    public override UnitTestActivityKind Kind => UnitTestActivityKind.RunEpoch;
}

public sealed record AccessHealthActivityPayload(
    AccessServiceKind ServiceKind,
    AccessHealth Health) : UnitTestActivityPayload
{
    public override UnitTestActivityKind Kind => UnitTestActivityKind.AccessHealth;
}

public sealed record DiagnosticActivityPayload(Diagnostic Diagnostic) : UnitTestActivityPayload
{
    public override UnitTestActivityKind Kind => UnitTestActivityKind.Diagnostic;
}

public enum UnitTestActivityKind
{
    Session,
    PlcState,
    Command,
    RunControl,
    EventStream,
    PlcEvent,
    RunEpoch,
    AccessHealth,
    Diagnostic
}

public enum UnitTestSessionActivityKind
{
    Started,
    Ended
}

public enum UnitTestActivityCommandKind
{
    Start,
    Abort,
    Reset,
    EventBufferReset,
    PlcStart
}

public enum UnitTestActivityCommandPhase
{
    Requested,
    Completed
}

public enum UnitTestRunEpochActivityKind
{
    Started,
    Updated,
    Ended
}
