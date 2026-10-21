# tc3unittest Consumer Mapping

## Purpose And Authority

This document records the agreed mapping from the PLC-owned TwinCAT UnitTest
contracts to existing application-facing models. It does not redefine the PLC
contracts, introduce new shared DTOs, or claim an implemented ADS transport.

The [reference source](../../../../docs/references/tc3unittest/SOURCE.md)
identifies the reviewed source revision and synchronization policy. Normative
PLC definitions are kept in the unchanged local snapshots:

- [General framework](../../../../docs/references/tc3unittest/contracts/general-framework-contract.md)
- [Run Control](../../../../docs/references/tc3unittest/contracts/run-control-contract.md)
- [Event Stream](../../../../docs/references/tc3unittest/contracts/event-stream-contract.md)
- [Test Suite](../../../../docs/references/tc3unittest/contracts/test-suite-contract.md)
- [Test Case](../../../../docs/references/tc3unittest/contracts/test-case-contract.md)

The [shared event-stream model](../../../../docs/architecture/400-unittest-eventstream-processing.md)
and [artifact export design](../../../../docs/architecture/implementation/510-artifact-export.md)
remain authoritative for PC-side evaluation and reporting.

## Responsibility Boundary

| Owner | Responsibility |
| --- | --- |
| PLC framework | Owner lifecycle, command qualification, event generation, published metadata, FIFO mutation and deletion/reset processing. |
| Beckhoff profile and internal transport | Vendor access, symbol/type validation, coherent capture, field normalization, permitted writes, observed status, transport failures and resource lifetime. |
| Shared Core | RunEpoch attribution, hierarchy reconstruction, lifecycle/result evidence, action gates and result interpretation. |
| Trace and analysis | Existing activity capture and on-demand interpretation, including events outside a RunEpoch. No second TwinCAT-specific analysis pipeline. |
| Applications and exporters | Generic workflow presentation, command requests, CI outcomes and artifacts from the existing shared models. No Beckhoff API objects or symbol paths. |

The existing four functional service boundaries are retained:
`IPlcProjectService`, `IPlcControlService`, `IUnitTestLifecycleService` and
`IUnitTestEventStreamService`. Internal binding/process design is not decided
by this mapping.

## Published Data And Commands

| PLC publication | Consumer use |
| --- | --- |
| `GVL_UnitTest.RunControl.ContractInfo` | Direct-access compatibility validation. |
| `GVL_UnitTest.RunControl.ControlData.Status.Execution` | `UnitTestRunControlObservation`: execution status, signal flags, detailed state and result projection. |
| `GVL_UnitTest.RunControl.ControlData.Cmd.Start`, `.Abort`, `.Reset` | Individual lifecycle command writes; never write back the entire ControlData structure. |
| `GVL_UnitTest.RunControl.ControlData.BufferCmd.Reset` | EventBuffer reset request through the existing event-stream service. |
| `GVL_UnitTest.RunControl.ControlData.BufferCmd.DeleteSequenceId` | Contiguous captured-event release watermark. |
| `GVL_UnitTest.EventStream.ContractInfo` | Event-stream compatibility validation, separate from direct-access compatibility. |
| `GVL_UnitTest.EventStream.FifoControl` | Readable FIFO range and actual buffer-state observation. |
| `GVL_UnitTest.EventStream.Status` | Producer sequence, actual deleted sequence and loss counter. |
| `GVL_UnitTest.EventStream.Buffer` | Source for normalized `UnitTestEventEntry` values in `UnitTestEventBatch`. |

Validate the applicable contract versions before interpreting data or writing
commands. Equal version strings in different contracts do not establish equal
wire layouts. `FrameworkVersion` describes the PLC library as Major.Minor.Build,
not the PC profile or binding package version.

Lifecycle command results describe the API write, not successful PLC execution.
The PLC owns two-sample command qualification and clearing accepted bits. Do not
add command-bit readback acknowledgement, consumer-side bit clearing or automatic
command replay. Existing lifecycle observations remain separate from transport
acceptance. Startup initialization and its framework Reset are PLC-owned.

RunControl observation uses the configured `unitTest.monitoringIntervalMs`.
Coordinate it with EventBuffer observation so draining does not introduce a
second competing RunControl read loop. System/PLC operating-state observation
has its own lifetime as defined in the
[runtime access contract](runtime-provisioning.md#state-observation); it must not
wait for EventBuffer drain completion.

On a read failure, retain the last real RunControl observation and report the
access failure separately through the existing health model. Before the first
successful observation, expose unknown state, not synthetic Idle/Ready. Do not
carry observations across replaced communication contexts as fresh evidence.

Run Control counters and runtime projections do not replace missing event
evidence. Suite/TestCase object interfaces and private FB data are not mandatory
external symbol paths. Optional status wrappers do not become prerequisites for
the shared consumer. A child already Idle/Ready need not receive another Reset,
so absence of a per-child Reset trigger alone is not failure evidence.

## Field Normalization

| PLC field/type | Shared representation and constraint |
| --- | --- |
| Event `SequenceId`, status `SequenceId` and `DeletedSequenceId`: `UDINT` | `uint`; status SequenceId becomes ProducerSequenceId and may exceed the last stored event after loss. |
| `LostEventCount`: `UINT` | `ushort`; retain observed producer loss. |
| FIFO indices, length and count: `UINT` | Unsigned transport metadata; do not expose Siemens indexing assumptions through shared models. |
| `OwnerScope`, `EventType`: `USINT` | Existing matching `UnitTestOwnerScope` and `UnitTestEventType` values. |
| `Severity`: `USINT` | Existing logging levels Trace through Critical; logging severity is not a test result. |
| `State`, `Result`: `INT` | Existing signed lifecycle/result values. Preserve contract-defined custom preparation/test states, including numeric values without a named C# enum member. |
| `Name`: `STRING(80)`; `ResourceName`: `STRING(32)`; `Message`: `STRING(254)` | Contract ASCII text; validate actual symbol layout and string representation instead of assuming Siemens encoding or C# structure packing. |
| `Timestamp`: `LINT` | UTC Unix nanoseconds normalized to `DateTimeOffset` at 100 ns resolution using integer conversion. Sub-100 ns precision is not retained; no raw-nanosecond DTO extension is required. |
| Owner runtime: `TIME`; aggregation `runtime` | Unsigned integer milliseconds. Preserve range without narrowing through signed DINT; available values map to existing nullable runtime fields. |

Sequence IDs remain authoritative for ordering and release even when normalized
timestamps coincide. PC observation timestamps and PLC timestamps are separate
clock domains and must not be mixed to calculate durations.

Event `Message` remains available to the existing processor and trace, including
aggregation JSON and assertion details. Hierarchical names identify Run Control,
Suite, TestCase and assertion context; orchestrator diagnostics are not additional
reportable owner levels. Successful assertions need not emit individual events.

## FIFO, Handover And Status

`BufferLength` is the physical upper array index, not usable capacity. The FIFO
has indices 1 through N and usable capacity N-1. A non-empty readable range starts
at IndexOut and ends before IndexIn, wrapping from N to 1. Even a full FIFO has
only N-1 readable entries. Use published length and control data, not a fixed
consumer capacity or the Siemens full-buffer rule.

Producer reservations are private PLC accounting. They do not create readable
events or require another reservation ledger in the consumer. ConfigError and
IndexError cannot be treated as a trustworthy ordinary buffer observation.

Only safely captured events from the readable contiguous FIFO range may be
released. The producer high-water mark alone is not an acknowledgement target.
The PLC publishes DeletedSequenceId from actual deletion, while
DeleteSequenceId is an outstanding request. Neither is the PC-side
BatchReleaseState, which concerns batch handover for PC processing.

Transport release must not depend on successful higher-level RunEpoch
attribution. BufferFull alone does not invalidate stored events; LostEventCount
marks incomplete stream evidence without stopping valid consumption. Preserve
the existing separation of communication health, buffer condition and run
evidence.

Status publications must represent actual observations. Failed or skipped reads
must not manufacture an empty buffer and zero counters. An API write does not
prove PLC deletion or an empty buffer. Known metadata, observation validity and
communication-context lifetime must follow the existing shared model.

The PLC permits replacing a pending deletion watermark with a higher valid one;
the initial consumer does not use this optimization. The following capture and
handover design is agreed; implementation and ADS V7 qualification remain
separate from that agreement.

### Snapshot And Bounded Drain

Resolve and validate contract versions, field types, byte sizes, relative
offsets, array bounds and element stride when preparing access. Do not hard-code
a layout measured by a probe. Symbol changes invalidate the resolved access and
require revalidation; compatibility metadata is not reread for every batch.

Each observation reads FIFO/status metadata and the pending Delete command.
While DeleteSequenceId is nonzero, continue refreshing metadata but do not read
or republish the same payload or replace the pending request. This keeps status
observation independent of batch processing.

With no pending deletion, use the pre-observation to select one contiguous FIFO
prefix, bounded by maxEventsPerBatch and the published usable capacity. Read
complete entries as contiguous memory ranges, split into two ranges at wrap.
There is no additional fixed chunk size whose results are combined into a
larger batch.

Read post-metadata before committing the captured batch. Validate bounds,
count/flags, stable IndexOut and actual DeletedSequenceId, unchanged
symbol/communication context, and absence of observed counter regression.
Captured event sequences must be positive, strictly increasing and plausible
against producer metadata. Producer append or increasing LostEventCount alone
does not invalidate an otherwise unchanged captured prefix. Sequence gaps due
to lost events do not make that physical FIFO prefix non-contiguous.

Do not salvage part of an incomplete or inconsistent capture. Do not publish or
release it; retry from a new observation. Pre/post checks do not establish ADS
atomicity or guarantee detection of an unobservable reset-and-repopulation
cycle.

Use the post-observation to determine remaining backlog. Wait drainIntervalMs
while backlog or an outstanding PLC deletion is known; otherwise wait
monitoringIntervalMs. These waits follow the completed attempt, not a fixed
wall-clock schedule. Covering the pre-observed range or successfully writing a
watermark does not establish an empty buffer. Publish the first trusted empty
observation after nonempty evidence as an empty batch; repeated unchanged empty
observations may be coalesced.

### Commit And Monitoring State

The commit order is:

1. Copy and validate the complete selected prefix into profile-owned batch data.
2. Write its highest captured SequenceId to the PLC DeleteSequenceId command.
3. Publish the batch only after that API write succeeds.

Do not wait for actual PLC deletion or a separate Core-processing
acknowledgement. Do not gate this path on RunEpoch attribution. An unsuccessful
write produces a diagnostic and no batch publication; a later attempt
re-observes PLC state before considering another write. It does not blindly
replay a previous watermark.

Latch successfully validated FIFO/status metadata together, independently of
payload classification. A successful post-observation replaces the
pre-observation; a failed payload or post-read does not erase known metadata.
Never synthesize DeletedSequenceId from a successful command write.

| Situation | LastReadState | BatchReleaseState |
| --- | --- | --- |
| No observation in a new communication context | NotObserved | Idle |
| Valid batch released for PC processing | Valid | Completed |
| Valid captured batch withheld for rereading | Valid | Idle |
| Trusted empty observation | Empty | Idle |
| No new payload outcome while deletion is pending | Retain the prior read outcome | Retain the prior PC batch outcome |

Other ineligible captures report their actual read state with Idle. Access
failures retain known metadata and previous batch-release evidence while
reporting the actual failure/read invalidation. Pending PLC deletion does not
become BatchReleaseState.Pending. Initial zero counters and false buffer flags
with NotObserved are not evidence of a reset. Retention applies within the same
logical communication context, not across unrelated sessions or targets, and
does not make retained data fresh during access revalidation.

### Concurrency And Access Lifetime

Serialize EventBuffer reads/commit and explicit Buffer Reset within the
event-stream service. Keep the existing independent RunControl and system-state
observers; do not add another RunControl reader while draining. Host callbacks
must not run while holding ADS-access locks, and delivery must not introduce an
unbounded queue.

Stop and reconnect must join the observation loop before disposing its access.
A cancellation point must not silently discard a batch after its successful
watermark write. Lifecycle and reset commands remain single writes without
automatic replay. Recoverable access failures use the existing revalidation
and coalesced system-state-check path, including prolonged CONFIG mode, with
diagnostics on meaningful changes rather than every unsuccessful poll.

## Reset, Completion And Result Evidence

RunControl Reset and EventBuffer Reset are distinct commands. EventBuffer Reset
clears stored events, FIFO state, producer/deleted/lost counters and both buffer
commands. It starts a new buffer epoch and invalidates old deletion assumptions;
it has no dedicated reset event. During an active run it destroys evidence, not
merely a performance backlog. Do not introduce an automatic reset or a new start
gate as part of the TwinCAT mapping.

Retain the shared RunControl Starting and Completed event anchors. Successful
completion observation requires the completion anchor plus trusted empty-buffer
evidence. A timeout ends observation with incomplete evidence, not successful
completion. Direct status, trigger events and logging severity do not replace
required result/completion events. Preserve existing handling of independently
delivered observations rather than imposing batch adjacency.

Evaluation severity progresses Passed -> Failed -> Error. An explicit controlled
abort ends the owner lifecycle in Completed with Result Abort; Abort is not an
intermediate severity. Previously completed Failed children retain their own
results when an active parent completes an abort. Error evidence must not be
downgraded. Mapping retains these PLC outcomes; it must not work around shared
evaluation defects by rewriting vendor event results.

## Minimal Consumer And Export Requirements

PLC publication obligations and PC export prerequisites are different. The PLC
contracts specify final Result, Aggregation and Completed publication. The
shared consumer intentionally does not require all aggregation fields, or even
aggregation evidence itself, to establish otherwise complete lifecycle/result
evidence and produce JUnit output.

Retain the existing minimal consumer requirements. Do not tighten the shared
parser or add mandatory counters/runtime solely because the producer specifies
them. Available aggregation remains optional validation/reporting evidence;
existing checks for contradictions remain applicable.

JUnit derives test counts and outcomes from captured concrete test cases.
Runtime is used when available; absent testcase runtime currently maps to zero,
and absent suite runtime falls back to the sum of testcase times. No extra ADS
reads of Suite/TestCase internals are required to fill optional export details.

## Validation Boundaries

The mapping does not establish ADS atomicity. Validate coherent capture, layout
changes and symbol invalidation against the actual APIs. The
[configuration reference](../configuration.md#unittest-communication) defines
the initial UnitTest polling strategy, intervals and batch defaults; this does
not establish their measured performance or qualify the concrete drain algorithm.

Qualify split batches, trailing child diagnostics after parent errors, STOP/RUN,
reconnect and externally initiated EventBuffer reset. In particular, an external
reset followed by immediate production may hide the all-zero observation window;
do not claim reliable detection solely from the nominal reset values or inject
synthetic observations. These are validation obligations, not approval for new
reset heuristics or lifecycle semantics.

The existing shared reset detector requires an actual Empty observation with
BufferObservedEmpty and zero producer, deleted and lost counters. A reset with
immediate production can evade that boundary. Do not compensate by rewriting
sequence IDs, opportunistically changing logical session identity or adding a
profile-local RunEpoch algorithm. Any required shared-model correction needs
separate alignment.
