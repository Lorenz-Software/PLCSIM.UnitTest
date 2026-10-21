# UnitTest Run Epoch Model

## `EventStreamProcessor`

`EventStreamProcessor` consumes accepted `UnitTestEventBatch` data from
`IUnitTestEventStreamService`, appends events to the active run epoch event
stream, and updates the neutral `UnitTestRunEpoch` model.

The code sketches below show selected members, not a complete API schema.
Use [EventStreamProcessor.cs](../../../src/PLCSIM.UnitTest.Core/Workflow/EventStreamProcessor.cs)
and the owning Common model types for exact record constructors, optional
members and current enum values. The prose defines the responsibility split;
these sketches must not be used as replacement contract declarations.

Selected processing surface:

```csharp
public sealed class EventStreamProcessor
{
    public UnitTestRunEpochSnapshot? Current { get; }

    public OperationResult StartObservation(EventStreamObservationContext context);

    public EventProcessingResult ProcessBatch(
        UnitTestEventBatch batch);

    public EventProcessingResult CompleteObservation(
        EventStreamCompletionReason reason,
        DateTimeOffset completedAt);

    public EventProcessingResult CompleteCurrentEpoch(
        EventStreamCompletionReason reason,
        DateTimeOffset completedAt);

    public void StopObservation();

    public UnitTestRunEpochSnapshot? CreateSnapshot();

    public void Reset();

    public event EventHandler<EventStreamProcessorUpdateEventArgs> ProcessorUpdateEventHandler;
}

public sealed record EventStreamObservationContext(
    CommunicationSessionId SessionId,
    uint? SequenceBaseline,
    DateTimeOffset StartedAt);

public sealed record EventProcessingResult(
    EventProcessingState State,
    UnitTestRunEpochSnapshot? CurrentRun,
    IReadOnlyList<UnitTestEventRef> AcceptedEvents,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record EventStreamProcessorUpdate(
    EventStreamProcessorUpdateKind Kind,
    EventProcessingState State,
    UnitTestRunEpochSnapshot? CurrentRun,
    IReadOnlyList<UnitTestEventRef> AcceptedEvents,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record EventStreamProcessorUpdateEventArgs(
    EventStreamProcessorUpdate Update);

public enum EventStreamCompletionReason
{
    CompletionAnchorAndBufferEmpty,
    AbortObserved,
    ResetObserved,
    Timeout,
    TargetInvalidated,
    Cleanup
}

public enum EventStreamProcessorUpdateKind
{
    ObservationStarted,
    ObservationStopped,
    EpochStarted,
    EpochCompleted,
    BatchProcessed,
    BatchIgnored,
    Reset
}

public enum EventProcessingState
{
    Ignored,
    Empty,
    Accepted,
    Completed,
    Incomplete,
    Ambiguous,
    Invalidated
}
```

Required behavior:

1. `StartObservation` establishes the active `CommunicationSessionId` and
   sequence baseline for the communication session. Batches are ignored until
   observation is active.
2. Reject or ignore batches whose `SessionId` does not match the active
   UnitTest communication session.
3. Open a new `ActiveRunEpochState` only when an accepted event is the exact
   RunControl start anchor: `OwnerScope = RunControl`,
   `EventType = StateTransition`, and `State = Starting`.
4. Include that start-anchor event as the first event of the new epoch.
5. Preserve all accepted events in `UnitTestRunEpoch.EventStream` before
   applying semantic updates.
6. Process accepted events in ascending `SequenceId` order.
7. Treat `SequenceId` only as ordering and inclusive range evidence after the
   start anchor has established epoch ownership. It must not create an epoch.
8. Use `SequenceId` continuity and buffer evidence to classify stale,
   incomplete, ambiguous, or invalidated evidence. Trigger events and polled
   lifecycle projections are not run-boundary authority.
9. Build/update the owner tree from `OwnerScope` and hierarchical `Name`.
10. Apply `StateTransition`, `Result`, `Aggregation`, assertion result, and
   framework events according to the contract-near event semantics.
11. Keep framework events in the event stream and index them separately through
   `FrameworkEvents`.
12. Keep capture/progress state, evidence completeness, evidence consistency,
    and interpreted result as separate concerns.
13. Return diagnostics in processing results and processor updates for
    processing inconsistencies, stale evidence, incomplete evidence,
    conflicting evidence, aggregation parse failures, framework events with
    critical meaning, session mismatch, and invalidated observation context.
14. Do not store diagnostics inside `UnitTestRunEpoch`.
15. Emit `ProcessorUpdateEventHandler` after observation, epoch, or batch
    changes that should update workflow publication, WPF tree state, CLI
    progress, or output readiness.
16. Latch the exact RunControl completion anchor
    (`OwnerScope = RunControl`, `EventType = StateTransition`,
    `State = Completed`) independently from the empty-buffer observation. Finish
    the epoch only after both facts were observed, regardless of observation
    order. The completion anchor is the final event in the inclusive epoch
    sequence range.
17. `CompleteCurrentEpoch` finalizes only the current run epoch. Event-stream
    observation stays active so a later start anchor can open another run without
    recreating the monitoring session.
18. `CompleteObservation` and `StopObservation` stop the active event-stream
    observation during stop-monitoring, release, cleanup, profile unload, or
    invalidated communication context handling.
19. Events that cannot be assigned to an active epoch remain outside the run
    snapshot. Framework events are routed to diagnostics immediately. Trigger,
    initialization, reset, and idle evidence is expected outside an epoch and is
    not diagnosed as run evidence. Result/aggregation, RunControl completion, and
    active subordinate lifecycle evidence without an epoch produce an explicit
    diagnostic.
20. `Current` and `CreateSnapshot` return the last committed, stable snapshot
    for UI publication or artifact export; readers never traverse mutable
    processor state.
21. Independent EventStream monitoring-state updates are authoritative for
    producer-side event loss even when no event batch can be published. A
    non-zero `LostEventCount` observed while an epoch is active, finalizing, or
    retained as the latest completed epoch permanently marks that epoch evidence
    incomplete. Later live recovery or EventBuffer reset must not clear the
    epoch-relative loss evidence.

An empty event list with `ReadState = Empty` and no reported event loss describes
an empty read, not missing run evidence. `BufferObservedEmpty` may still be false
when later metadata observes newly produced events. That false value remains
authoritative for the empty-buffer completion condition; classifying the read as
empty must not set the completion latch or repair previously incomplete evidence.

### State Synchronization and Snapshot Publication

`EventStreamProcessor` serializes its state-changing entry points, including
batch ingestion, monitoring observations, completion, invalidation and reset.
It constructs and atomically publishes the read-only snapshot before releasing
the state lock. A concurrent reader may see the preceding committed state, but
never a partially applied batch. Previously published event and hierarchy
collections remain unchanged after later updates.

Processor notifications, trace writes and logging are queued in state-commit
order and delivered outside the state lock. External callbacks must not run
under that lock. `RunWorkflowService` captures one processor snapshot per
publication and reuses it for run data, status and export eligibility.

`TestRunMonitor` preserves a terminal observation against delayed snapshots of
the same epoch. Communication-state processing and EventBuffer monitoring
continue independently; a genuinely new epoch can start another observation.

`EventStreamProcessor` currently owns both event-stream processing and neutral
result construction. There is no separate `UnitTestResultBuilder` contract or
pending extraction required by this model.

`EventStreamProcessor` does not own timeout monitoring and does not decide the
overall test-run lifecycle by itself. It reconstructs event-stream evidence and
reports processing results. `TestRunMonitor` combines these results with
lifecycle evidence and timeout policy.

`EventStreamProcessor` owns the active event model state:

```text
EventStreamProcessor
  -> ActiveRunEpochState
       -> EventStream: List<UnitTestEventEntry>
       -> Root / UnitTestRunNode builder state
       -> FrameworkEvents: List<UnitTestEventRef>
       -> aggregation, finding, evidence, and result builder state
```

`Reset` clears processor state for cleanup, profile unload, or invalidated
context. It is not the same operation as starting a new test-run epoch, and it
is not used after normal run completion. Normal completion closes only the
current run epoch while event-stream observation continues for the session.

## Common Event-Stream Batch And Entry Models

`IUnitTestEventStreamService` emits contract-near UnitTest event batches. The
access profile reads and validates the PLC-owned event-buffer data, releases the
read PLC events according to the producer/consumer handover contract, and pushes
the batch to the shared-core processing component.

The batch is the complete handover object from access profile to shared core.
The shared core appends accepted events to the active run epoch event stream and
then processes the newly appended events against the existing epoch state.

```csharp
public sealed record EventBatchReceivedEventArgs(
    UnitTestEventBatch Batch);


public sealed record UnitTestEventBatch(
    CommunicationSessionId SessionId,
    IReadOnlyList<UnitTestEventEntry> Events,
    uint ProducerSequenceId,
    uint DeletedSequenceId,
    ushort LostEventCount,
    bool BufferObservedEmpty,
    EventBufferReadState ReadState);

public sealed record UnitTestEventEntry(
    uint SequenceId,
    DateTimeOffset Timestamp,
    UnitTestOwnerScope OwnerScope,
    UnitTestEventType EventType,
    LogLevel Severity,
    string Name,
    string ResourceName,
    UnitTestLifecycleState State,
    UnitTestResultState Result,
    string Message);

public enum EventBufferReadState
{
    Valid,
    Empty,
    RetryRequired,
    Inconsistent,
    ContractInvalid,
    SessionInvalid,
    NotObserved
}

public enum EventStreamBatchReleaseState
{
    NotRequired, // Reserved legacy value; use Idle for no batch.
    Idle,
    Pending,
    Completed,
    Rejected,
    Failed,
    Unclear,
    Invalidated
}
```

Direction:

- `UnitTestEventEntry` follows the PLC-owned event-stream contract fields,
- `SequenceId` is the event-stream handover identifier for ordering,
  acknowledgement, release, gap checks, and plausibility checks,
- `Timestamp` is the PLC-side event timestamp and is part of the event entry,
- `OwnerScope`, `EventType`, `Severity`, `Name`, `ResourceName`, `State`,
  `Result`, and `Message` are explicit contract fields, not generic
  properties,
- `Severity` is a logging-domain level and must not replace `Result`, `State`,
  or `EventType`,
- UnitTest contract info is validated during UnitTest communication preparation,
  not repeated on every batch,
- `SessionId` ties the batch to the validated UnitTest communication session,
- the access profile does not own a long-lived application event history,
- the shared-core active run epoch owns the PC-side event stream after batch
  handover,
- event-buffer epoch detection is a shared-core processing concern; the access
  profile does not assign a run epoch id to batches,
- run assignment and stale-event handling are shared-core responsibilities,
- event names are hierarchical according to the PLC-owned contract,
- framework-only names such as orchestrator markers are interpreted by shared
  core rules, not by the application UI.

## Common UnitTest Contract Constants

Contract-near enums should keep PLC-owned values where those values are part of
the event-stream contract. Additional PC-side values are allowed only when they
represent application uncertainty rather than PLC state.

```csharp
public static class UnitTestContractVersions
{
    public const string PlcsimAdvancedCommunication = "1.0.0";
    public const string EventStreamModel = "1.0.0";
}

public enum UnitTestOwnerScope
{
    Unknown = 0,
    Framework = 5,
    RunControl = 10,
    TestSuiteOrchestrator = 15,
    TestSuite = 20,
    TestCaseOrchestrator = 25,
    TestCase = 30,
    Assertion = 40,
    Preparation = 50
}

public enum UnitTestEventType
{
    Unknown = 0,
    Result = 10,
    Trigger = 20,
    StateTransition = 30,
    Info = 40,
    Aggregation = 50,
    Framework = 60
}

public enum UnitTestLifecycleState
{
    Error = -10,
    Uninitialized = 0,
    Reset = 1,
    Idle = 10,
    Starting = 11,
    Preparing = 20,
    Running = 50,
    Completing = 80,
    Completed = 89,
    Aborting = 90,
    AbortingFromError = 91,
    Aborted = 99,
    Unknown = 999
}

public enum UnitTestResultState
{
    Error = -10,
    NotEvaluated = 0,
    Passed = 10,
    Failed = 20,
    Abort = 30,
    Unknown = 999
}

public enum UnitTestNodeKind
{
    RunControl,
    TestSuite,
    TestCase
}

public enum UnitTestFindingKind
{
    Assertion,
    OwnerResult,
    Framework,
    Consistency,
    MissingEvidence
}

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
```

Direction:

- `UnitTestOwnerScope`, `UnitTestEventType`, `UnitTestLifecycleState`, and
  `UnitTestResultState` mirror PLC-owned event-stream contract values,
- `EvidenceState`, `UnitTestRunState`, `UnitTestNodeKind`, and
  `UnitTestFindingKind` are PC-side interpretation states,
- PLC event timestamps are normalized to `DateTimeOffset` by the access profile.
  The original PLC datatype does not leak above the access profile boundary.

Additional shared enums:

```csharp
public enum PlcProjectState
{
    Unknown,
    Prepared,
    BuildSucceeded,
    BuildFailed,
    Transferred,
    Invalid
}

public enum RuntimeInstanceState
{
    Unknown,
    Unavailable,
    Starting,
    Available,
    Stopping,
    Faulted
}

public enum PlcOperatingState
{
    Unknown,
    Unavailable,
    Stopped,
    Starting,
    Running,
    Stopping,
    Frozen,
    Held
}

public enum RuntimeTargetState
{
    Unknown,
    NotAvailable,
    Ambiguous,
    Prepared,
    ReadyForTransfer,
    ReadyForExecution,
    Running,
    Stopped,
    Invalidated
}

public enum TargetResolutionState
{
    NotResolved,
    Resolved,
    NotAvailable,
    Ambiguous,
    Incompatible,
    InvalidConfiguration
}

public enum UnitTestRunCommand
{
    Start,
    Abort,
    Reset
}
```

## Common Neutral Run Epoch Model

`UnitTestRunEpoch` is the neutral, PLC-independent PC-side representation of one
recognized UnitTest run epoch. It is not the PLC communication contract and it is
not JUnit XML.

The published type should be read-only for applications and exporters. The
mutable working state belongs to `Core`, for example in `ActiveRunEpochState`.
If async export or delayed UI publication needs isolation from later epoch
updates, `Core` creates a stable read-only copy of `UnitTestRunEpoch`. The live
mutable state is never exposed to applications or exporters.

```csharp
public sealed class UnitTestRunEpoch
{
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
    public bool HasLostEvents { get; }
}

public sealed class UnitTestRunEpochSnapshot
{
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
    public bool HasLostEvents { get; }
}

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
    IReadOnlyList<UnitTestRunNode> Children);

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

public sealed record UnitTestFinding(
    UnitTestFindingKind Kind,
    UnitTestResultState Result,
    string Name,
    string Message,
    UnitTestEventRef? SourceEvent);

public sealed record UnitTestEventRef(
    uint SequenceId);
```

The intended hierarchy is:

```text
RunControl
└─ Suite
   └─ TestCase
      ├─ Finding: assertion failed
      └─ Finding: testcase error
```

Direction:

- the required tree levels are RunControl, TestSuite, and TestCase,
- `UnitTestRunEpoch.EventStream` contains all events assigned to the run epoch
  in `SequenceId` order,
- `UnitTestRunEpochSnapshot` has the same public shape as the published epoch
  projection but is detached from the mutable active state,
- `UnitTestRunEpoch.CreatedAt` and `LastUpdatedAt` are derived from the
  assigned event timestamps,
- `UnitTestRunNode` represents the structured owner hierarchy derived from
  `OwnerScope` and `Name`,
- `LifecycleState` is contract-near and represents the PLC-side UnitTest state
  value, with only minimal PC-side additions such as unknown/not-observed where
  required,
- `CreationEvent` references the first event that created the node in the
  PC-side model,
- `CompletionEvent` references the Full Owner `StateTransition` event to
  `Completed`,
- `ResultEvent` references the owner `Result` event,
- `AggregationEvent` references the owner `Aggregation` event,
- node `CreatedAt` and `LastUpdatedAt` are derived from PLC-side event
  timestamps,
- `Aggregation` contains parsed aggregation data from the event `Message`
  payload when available,
- assertion events are represented primarily as findings on the affected
  testcase,
- no mandatory assertion node level is introduced because successful assertions
  are not reliably emitted as individual events,
- a failed/error assertion finding means the owning testcase cannot be passed,
- framework events are indexed separately through `FrameworkEvents` so critical
  framework diagnostics do not disappear when they have no owner-specific node,
- trigger events may create a node when they are the first owner evidence, but
  they are not result or completion evidence,
- current informational events are not used for model building,
- events that do not create or update a node remain available through
  `EventStream`,
- capture state, evidence state, and interpreted result stay separate,
- WPF tree output, CLI summary text, and JUnit export consume this neutral
  epoch model,
- diagnostics are owned by `DiagnosticSink`; application publication can combine
  run epoch state and diagnostics, but `UnitTestRunEpoch` is not the diagnostic
  owner.
