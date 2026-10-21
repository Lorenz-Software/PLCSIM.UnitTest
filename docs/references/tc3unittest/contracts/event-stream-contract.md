# Event Stream Contract

## Purpose

This contract defines the PLC-owned UnitTest event stream and its producer/consumer handover model. It preserves event availability, order, acknowledgement, deletion, completion evidence, and payload interpretation independently of the internal FIFO implementation.

## Scope

This contract defines:

- `ST_Event`, event classifications, and fixed event text lengths;
- event-stream publication below `GVL_UnitTest.EventStream`;
- event sequence, epoch, deletion-watermark, lost-event, and single-consumer semantics;
- event-stream and direct-access compatibility metadata;
- the external Event Buffer command boundary below `GVL_UnitTest.RunControl.ControlData.BufferCmd`.

It does not define FIFO implementation, PC-runner implementation, or an access-profile-specific ADS client.

## Terms

- **Producer**: PLC-side UnitTest framework logic that creates events and owns event storage.
- **Consumer**: the one logical participant that reads events and requests contiguous release.
- **Event-buffer epoch**: interval between two Event Buffer resets.
- **Watermark**: highest contiguous processed `SequenceId` requested for release.

## Version

- Contract version: `1.0.0`

## Requirements

- `ESC-001` The framework MUST be the event-stream producer, and exactly one logical consumer MUST consume one event-buffer epoch.
- `ESC-002` Event Stream publication MUST contain the fields and ownership boundary in Table A.
- `ESC-003` `ST_Event` MUST contain the fields and types in Table B.
- `ESC-004` Event classification enums MUST use the values in Tables C through E.
- `ESC-005` `SequenceId` MUST be greater than zero for a produced event, monotonically increase within an epoch, and be the sole handover, acknowledgement, release, and plausibility identifier.
- `ESC-006` `SequenceId = 0` MUST identify a buffer entry without a produced event. Timestamps MUST NOT replace `SequenceId` for ordering, acknowledgement, or release.
- `ESC-007` The producer MAY assign a `SequenceId` before storage succeeds. `Status.SequenceId` MUST expose the last assigned value, not necessarily the highest currently readable value.
- `ESC-008` A full Event Buffer MUST NOT overwrite unread events. Each event that cannot be retained MUST increase `LostEventCount`.
- `ESC-009` A nonzero `LostEventCount` MUST make the delivered event log incomplete.
- `ESC-010` Consumers MUST derive readable entries from `FifoControl` according to Table F, preserve FIFO order, and process only entries in that range with `SequenceId > 0`.
- `ESC-011` `DeleteSequenceId` MUST be a contiguous release watermark. Consumers MUST only request a value after all earlier not-yet-released events have been processed.
- `ESC-012` The producer MUST release events only from the FIFO output side, up to and including the requested watermark, and MUST update `DeletedSequenceId` from the actually released event.
- `ESC-013` After processing or rejecting a Delete request, the producer MUST clear the external request only when its field still equals the processed snapshot. A distinguishably newer watermark MUST remain pending.
- `ESC-014` Event Buffer Reset MUST take priority over Delete processing. It MUST clear event storage and producer FIFO state, reset `SequenceId`, `DeletedSequenceId`, `LostEventCount`, and private reservation accounting to zero, and start a new epoch.
- `ESC-015` An Event Buffer Reset MUST clear both external Buffer commands. Rewriting `Reset = TRUE` while it is already true MUST NOT identify a second reset request.
- `ESC-016` Event Buffer Reset is independent of UnitTest lifecycle Reset and MUST NOT produce an event solely to report the reset.
- `ESC-017` The producer MUST own FIFO processing, event entries, FIFO control, Event Buffer status, and contract metadata. Consumers MUST NOT modify them.
- `ESC-018` `ReservedEntryCount` MUST remain private producer state and MUST NOT be published through ADS or OPC UA.
- `ESC-019` Event-stream and direct-access contract versions MUST remain separate as defined in Table G.
- `ESC-020` Event names, event payload categories, aggregation payloads, and completion evidence MUST follow the rules in this contract.
- `ESC-021` The Run Control StateTransition event into `E_State.Completed` MUST be the completion anchor of an observed run. Streaming finishes only after that anchor was delivered and the Event Buffer was observed empty, or after a consumer-defined timeout.
- `ESC-022` Status projections MUST NOT replace missing lifecycle, Result, or Aggregation event evidence.
- `ESC-023` Producer-owned Event Stream data MUST be read-only in OPC UA. Only Buffer command fields in Table H are externally writable.
- `ESC-024` `DeleteSequenceId = 0` MUST mean that no delete request is pending. A consumer MAY replace a pending request only with a higher valid watermark and MUST NOT replace it with a lower value.
- `ESC-025` The producer MUST reject or stop Delete processing when the requested sequence is stale, not yet produced, unreachable from the FIFO output, or the FIFO state is inconsistent.
- `ESC-026` The producer MUST expose a valid event-buffer epoch after startup or Event Buffer initialization. Consumers MUST treat an epoch reset as invalidating pending Delete assumptions from the preceding epoch.
- `ESC-027` Event Stream publication MUST NOT contain consumer-owned command bits or other consumer control functionality.
- `ESC-028` External consumers MUST use `FifoControl.BufferLength` as the effective upper index of `Buffer` and MUST NOT depend on a locally configured buffer-length constant.
- `ESC-029` A complete lifecycle event log MUST include the required completion evidence for every observed Run Control, Test Suite, and Test Case lifecycle in one epoch.
- `ESC-030` The Event Stream MUST be the normative source for event-log, Result, Aggregation, and lifecycle-completion evidence. Producer status projections are diagnostic and MUST NOT replace absent evidence.
- `ESC-031` Aggregation messages MUST be compact JSON objects with the required stable field names. Numeric values MUST be unsigned decimal JSON numbers; consumers MAY ignore unknown fields, but a missing required field makes that aggregation evidence incomplete.
- `ESC-032` Event Buffer Reset MUST NOT be rejected because UnitTest execution is active, reservations are outstanding, or a hierarchical reservation release reports failure. Run Control MUST attempt release through every registered Suite's hierarchical owner operation before unconditionally invoking the initialized Event Buffer's destructive Reset and clearing both Buffer commands. An active child without its required reservation MUST remain a framework error handled by the existing orchestration error path; buffer reset MUST NOT suppress this check or promise successful continuation of the affected run.
- `ESC-033` During normal initialized operation, `ReservedEntryCount = 0` MUST mean that no Test Suite or Test Case execution or Reset reservation is held. A reservation increase MUST be atomic and MUST be rejected without partial change when free capacity cannot cover the resulting aggregate reservation and required producer headroom. Destructive reset after a failed release follows ESC-032; it does not guarantee repair of already inconsistent local contributions.

## Requirement Details

### Table A: Event Stream publication

| Field | Type | Access | Meaning |
| --- | --- | --- | --- |
| `GVL_UnitTest.EventStream.ContractInfo` | `ST_ContractInfo` | Read | Event-stream compatibility metadata |
| `GVL_UnitTest.EventStream.FifoControl` | `ST_FifoStatus` | Read | Producer-owned readable-range metadata |
| `GVL_UnitTest.EventStream.Status` | `ST_EventBufferStatus` | Read | Producer-owned sequence and loss status |
| `GVL_UnitTest.EventStream.Buffer` | `ARRAY[1..EventBufferLength] OF ST_Event` | Read | FIFO-backed event entry storage |

`ST_EventBufferStatus` contains `SequenceId : UDINT`, `DeletedSequenceId : UDINT`, and `LostEventCount : UINT`. `EventBufferLength` is published as `FifoControl.BufferLength`; external consumers MUST use that field as the effective upper buffer index.

The Event Buffer service owns updates to the bound `FifoControl` and `Status` fields as part of its storage operations. Run Control owns external Buffer command intake and hierarchical reservation release before Buffer Reset, not Event Stream status publication. The event array is the actual FIFO-backed storage, not a periodically copied instance-data view.

### Table B: `ST_Event`

| Field | Type | Meaning |
| --- | --- | --- |
| `SequenceId` | `UDINT` | Producer-assigned handover identifier |
| `Timestamp` | `LINT` | UTC Unix time in nanoseconds |
| `OwnerScope` | `E_OwnerScope : USINT` | Semantic owner or diagnostic context |
| `EventType` | `E_EventType : USINT` | Payload category |
| `Severity` | `E_EventSeverity : USINT` | Logging severity; explicit field default `E_EventSeverity.Info` |
| `Name` | `STRING(80)` | Hierarchical owner or diagnostic name |
| `ResourceName` | `STRING(32)` | Optional resource context |
| `State` | `E_State : INT` | Related lifecycle state |
| `Result` | `E_Result : INT` | Related result |
| `Message` | `STRING(254)` | Event-specific payload |

All communicated text is ASCII-based. Assertion value previews are limited to `40` visible characters before they are embedded in `Message`.

### Table C: `E_OwnerScope`

| Value | Symbol |
| ---: | --- |
| `0` | `Unknown` |
| `5` | `Framework` |
| `10` | `RunControl` |
| `15` | `TestSuiteOrchestrator` |
| `20` | `TestSuite` |
| `25` | `TestCaseOrchestrator` |
| `30` | `TestCase` |
| `40` | `Assertion` |
| `50` | `Preparation` |

### Table D: `E_EventType`

| Value | Symbol |
| ---: | --- |
| `0` | `Unknown` |
| `10` | `Result` |
| `20` | `Trigger` |
| `30` | `StateTransition` |
| `40` | `Info` |
| `50` | `Aggregation` |
| `60` | `Framework` |

### Table E: `E_EventSeverity`

| Value | Symbol |
| ---: | --- |
| `0` | `Trace` |
| `1` | `Debug` |
| `2` | `Info` |
| `3` | `Warning` |
| `4` | `Error` |
| `5` | `Critical` |

### Table F: `ST_FifoStatus` consumer interpretation

| Field | Type | Meaning |
| --- | --- | --- |
| `IndexOut` | `UINT` | Current FIFO output position; readable range starts here when not empty |
| `IndexIn` | `UINT` | Next producer write position; never the last produced position |
| `BufferEmpty` | `BOOL` | No readable entry exists |
| `BufferFull` | `BOOL` | Exactly `BufferLength - 1` positions are readable from `IndexOut`; one ring position remains unused |
| `BufferAlmostFull` | `BOOL` | Producer capacity warning only |
| `ElementCount` | `UINT` | Occupied entry count for diagnostics and plausibility |
| `BufferLength` | `UINT` | Configured FIFO length and external upper index |
| `ConfigError` | `BOOL` | Consumption is unreliable |
| `IndexError` | `BOOL` | FIFO index state is invalid or being repaired |

If the Buffer is non-empty, positions from `IndexOut` up to but excluding `IndexIn` are readable, including when `BufferFull` is true. Positions wrap from `BufferLength` to `1`.

`BufferLength` is the physical array length, not the usable event capacity. The FIFO keeps one ring position unused to distinguish empty from full. Usable capacity is `BufferLength - 1`; for a valid FIFO, physically free event capacity is `(BufferLength - 1) - ElementCount`, before subtracting producer reservations. The unused ring position MUST NOT be counted as a readable event or available event capacity.

### Table G: Contract information

| Publication | `ContractVersion` meaning | `FrameworkVersion` meaning |
| --- | --- | --- |
| `GVL_UnitTest.EventStream.ContractInfo` | Event Stream contract version | Effective TwinCAT library version as `Major.Minor.Build`, without revision |
| `GVL_UnitTest.RunControl.ContractInfo` | Direct-access/Run-Control contract version | Effective TwinCAT library version as `Major.Minor.Build`, without revision |

Consumers MUST validate the applicable `ContractVersion` before interpreting Event Stream data or writing external command fields. Unsupported versions MUST NOT be interpreted by guessing layout or semantics.

### Table H: External Buffer commands

| Path | Type | Access | Meaning |
| --- | --- | --- | --- |
| `GVL_UnitTest.RunControl.ControlData.BufferCmd.Reset` | `BOOL` | Read/Write | Destructive Event Buffer reset request |
| `GVL_UnitTest.RunControl.ControlData.BufferCmd.DeleteSequenceId` | `UDINT` | Read/Write | Highest contiguous processed event watermark |

The Run Control integration path owns external Buffer command intake. It transfers Reset to the Event Buffer service, delegates Delete processing to that service, and resets external fields according to request-snapshot rules.

### Delete request processing

The consumer acknowledges only events it processed from the readable FIFO range. `DeleteSequenceId` permits release through the requested event; it does not grant random access to a physical array entry. A producer evaluates a captured request snapshot from the FIFO output side and updates `DeletedSequenceId` only after each actual deletion, using that deleted entry’s `SequenceId`.

A request may be stale, ahead of the producer sequence, outside the currently reachable FIFO range, or encounter inconsistent FIFO state. The producer must then reject or stop the request without deleting unrelated entries. It resets the external command only if the external value still equals the snapshot. This preserves a distinguishably newer request written asynchronously by the consumer.

### Epoch, loss, and reservation model

Event Buffer startup or initialization establishes a valid epoch. An Event Buffer Reset clears all event entries and producer FIFO state, resets `SequenceId`, `DeletedSequenceId`, `LostEventCount`, and private reservation accounting, and invalidates buffered events and Delete assumptions from the preceding epoch. It has priority over Delete processing and clears both external Buffer command fields. Buffer Reset is distinct from a UnitTest lifecycle Reset and is not itself reported as an event.

Run Control first attempts reservation release through every registered Suite's hierarchical `FreeReservedEvents()` operation, then invokes the Event Buffer reset and clears both Buffer commands regardless of the release return values. One failed release does not skip the remaining attempts or prevent buffer clearing. The attempts precede clearing the aggregate count so valid local/global contributions can be released together. Buffer reset does not forcibly rewrite failed local contributions or claim that corrupted accounting has been repaired. It neither waits for successful release nor requires a consumer to drain the buffer first. Full PLC-start initialization rebuilds both global and local accounting independently of the preceding run.

Reset during a running test deliberately invalidates that run's event-log completeness. Existing Orchestrator checks for active children without reservations remain enabled and lead affected execution through the normal framework-error path. Buffer command handling does not introduce a separate hierarchy Reset or Abort request, replace lifecycle states directly, or automatically rebuild the discarded reservations. Subsequent error diagnostics are not events reporting the buffer reset itself.

The intended short-lived UnitTest execution MUST remain below exhaustion of the `UDINT` sequence range within one epoch. Sequence exhaustion is outside supported operation; the counter saturates instead of wrapping, without an additional exhaustion error path or automatic epoch reset.

`LostEventCount` accounts for every event that could not be stored because capacity was exhausted. A nonzero count makes the event stream incomplete even when later events, status projections, or a completion anchor are available. `SequenceId` gaps can additionally indicate producer loss, transport loss, an epoch transition, or consumer-side handling problems.

Reservations prevent independently operating Orchestrators from accepting Suite or Test Case work against the same physical free capacity. They do not create readable events and do not alter FIFO indices, element count, or consumer acknowledgement. The Event Buffer service adjusts the aggregate reservation and the owning Test Suite's or Test Case's local reservation in one deterministic call in the initial single-task model. Test Suite and Test Case reservation methods own their respective joint updates; Orchestrators coordinate when to call them without a second reservation ledger.

### Names and event payloads

Full Owner names use `<runControl>`, `<runControl>.<suite>`, and `<runControl>.<suite>.<testCase>`. Owner-local segments may contain only ASCII letters, digits, `_`, and `-`; they must not be empty, contain whitespace, `.`, or control characters. Names that do not fit `STRING(80)` are invalid configuration and MUST NOT be accepted by truncation. The length-limited name may be retained in FName and emitted in an error diagnostic; validation always examines the unshortened full name.

Assertion names begin with the full Test Case name and a dot. Everything after the third dot is assertion-local and may contain additional dots. Orchestrator diagnostics use `<parentName>.Orchestrator` and are technical diagnostics, not Full Owner completion evidence. The owner-directed Orchestrator publishes Framework error diagnostics with OwnerScope Framework, not normal task/slot transitions. The numeric Orchestrator OwnerScope values remain defined; their presence does not require events using those values. Internal task identifiers are not encoded as owner lifecycle states.

`Result` events provide outcome or finding evidence. `Trigger`, `StateTransition`, `Info`, and `Framework` events are diagnostic events and do not provide result evidence. `Severity` is a logging-domain level; consumers classify test outcomes through `Result`, not through `Severity`. `StateTransition.State` is the new state; its previous state may appear only as diagnostic text. `Aggregation` events use one compact JSON object in `Message`:

| Owner scope | Required fields |
| --- | --- |
| Run Control | `total`, `inactive`, `idle`, `running`, `completed`, `passed`, `failed`, `error`, `lostEvents`; `runtime` as a non-negative integer millisecond value |
| Test Suite | `total`, `inactive`, `idle`, `running`, `completed`, `passed`, `failed`, `error`; `runtime` as a non-negative integer millisecond value |
| Test Case | `total`, `passed`, `failed`, `error`; `runtime` as a non-negative integer millisecond value |

Aggregation `runtime` is formatted from the owner's `TIME` duration as an unsigned integer number of milliseconds. Conversion occurs when constructing the event payload and MUST NOT narrow the value through `DINT`. This representation does not change the `LINT` UTC Unix-nanosecond event timestamp.

Final Result and Aggregation evidence is emitted before the corresponding Full Owner StateTransition into `E_State.Completed`. A Full Owner lifecycle requires one final Result, one final Aggregation, and one StateTransition into `E_State.Completed`; strict adjacency is not required. Orchestrator events are never Full Owner lifecycle, Result, or Aggregation evidence.

For one observed Run Control start, streaming finishes only after the Run Control completion anchor was delivered and the FIFO was observed empty, or after the consumer timeout. Only then can missing lifecycle completion evidence be classified as incomplete. A reset/epoch transition, timeout, or nonzero `LostEventCount` also makes the lifecycle event log incomplete; missing Result or Aggregation evidence remains missing evidence of that respective kind.

## Compatibility

This is the transport-independent Event Stream contract. An ADS access profile may define connection and read/write mechanics, but it MUST preserve these producer-owned event, FIFO-range, acknowledgement, and deletion semantics.

## Non-Requirements

This contract does not define generic FIFO implementation, ADS client design, transport batching, consumer retry policy, or PC-side report formatting.
