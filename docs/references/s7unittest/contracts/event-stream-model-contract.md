# UnitTest Event Stream Model Contract

## Purpose

This contract defines the PLC-owned UnitTest event-stream model.

It separates producer/consumer handover rules from PLC-side lifecycle and result semantics. Lifecycle ownership and result ownership are defined by the local framework contracts and are consumed here.

## Scope

This contract defines:

- producer/consumer roles for the PLC-owned UnitTest event stream,
- the single-consumer event-buffer handover model,
- event sequence and acknowledgement semantics,
- event-buffer epoch and lost-event semantics,
- event-entry payload fields, classification constants, and payload interpretation,
- producer contract metadata exposed as part of the event-stream model,
- client obligations that are required to evaluate the delivered UnitTest event log.

This contract does not define:

- PLC owner lifecycle semantics,
- result or aggregation ownership rules,
- consumer/client implementation design,
- report file formats,
- implementation details of a specific transport adapter.

## Terms

- `Producer`: PLC-side UnitTest framework logic that creates events and owns the event buffer.
- `Consumer`: the technical handover participant that reads event-buffer entries and acknowledges entries that may be released by the producer.
- `Client`: the application that uses the delivered UnitTest event log and result evidence.
- `Event stream`: the ordered set of produced UnitTest event entries identified by producer-assigned sequence ids.
- `Event-buffer epoch`: the time span between two event-buffer clear/reset operations. Sequence ids are scoped to one epoch.

## Version

- Contract version: `1.0.0`

## Requirements

- `CC-001` The PLC-side UnitTest framework MUST be the producer of the UnitTest event stream.
- `CC-002` The UnitTest event stream MUST have exactly one logical consumer.
- `CC-003` A consumer MUST read available entries from the event buffer according to the producer-owned FIFO control state.
- `CC-004` The producer MUST assign `SequenceId : UDInt` values to produced event entries.
- `CC-005` Produced event `SequenceId` values MUST be greater than `0` and monotonically increasing within one event-buffer epoch.
- `CC-006` `SequenceId = 0` MUST mean that an event-buffer entry does not contain a produced event.
- `CC-007` `SequenceId` MUST be used as event-stream handover identifier for acknowledgement, release, gap checks, and plausibility checks.
- `CC-008` `SequenceId` MUST NOT define UnitTest lifecycle or result semantics.
- `CC-009` The producer MUST expose the last produced sequence id as event-buffer status field `SequenceId`.
- `CC-010` The producer MUST expose the last deleted sequence id as event-buffer status field `DeletedSequenceId`.
- `CC-011` The producer MUST expose the producer-side lost-event counter as event-buffer status field `LostEventCount`.
- `CC-012` Event-buffer acknowledgement MUST be requested through a producer-owned buffer command field `DeleteSequenceId`.
- `CC-013` `DeleteSequenceId = 0` MUST mean that no delete request is pending.
- `CC-014` A consumer MAY replace a pending `DeleteSequenceId` with a higher valid acknowledgement watermark and MUST NOT replace it with a lower value.
- `CC-015` `DeleteSequenceId` MUST specify the highest `SequenceId` that the consumer has processed and that the producer may delete from the event buffer.
- `CC-016` A consumer MUST set `DeleteSequenceId` only when all earlier not-yet-released event-buffer entries have also been processed.
- `CC-017` The producer MUST process delete requests from the FIFO output side and delete entries up to and including the event with `SequenceId = DeleteSequenceId`.
- `CC-018` After processing or rejecting a delete request, the producer MUST reset `DeleteSequenceId` to `0` only when the command field still equals the processed request snapshot. A distinguishably newer request MUST remain pending.
- `CC-019` The producer MUST reject or stop delete processing when the requested `DeleteSequenceId` is stale, has not yet been produced, is not reachable from the current FIFO output, or the buffer state is inconsistent.
- `CC-020` Clearing or resetting the event buffer MUST start a new event-buffer epoch and reset `SequenceId` and `DeletedSequenceId` to `0`.
- `CC-021` `LostEventCount > 0` MUST mean that the produced event stream is incomplete due to producer-side event loss.
- `CC-022` The Run Control aggregation event MUST include `lostEvents` in its structured aggregation payload.
- `CC-023` The UnitTest event stream is the normative source for UnitTest event-log and result-evidence data exposed by this contract.
- `CC-024` Producer-side diagnostic projections are not normative sources for UnitTest event-log or result-evidence data exposed by this contract.
- `CC-025` `SequenceId` gaps are possible indications of event-stream handover or plausibility problems.
- `CC-026` A missing lifecycle completion event is only determinable after event streaming for the observed Run Control start is finished or timed out.
- `CC-027` Event timestamps MUST NOT replace `SequenceId` for acknowledgement or event-buffer release.
- `CC-028` Common event-entry fields have the meanings defined in Table A.
- `CC-029` UnitTest event-stream constants have the values and meanings defined in Tables B through F.
- `CC-030` UnitTest event types have the meanings defined in Table G.
- `CC-031` Aggregation events MUST use the compact JSON object payload defined in Table H in the event `Message` field.
- `CC-032` Aggregation event `Message` payloads provide structured UnitTest log formatting and aggregation reporting data.
- `CC-033` Missing required aggregation JSON fields MUST make the affected aggregation event incomplete for client evaluation.
- `CC-034` Result event payloads MUST follow Table I.
- `CC-035` Trigger event payloads MUST follow Table J.
- `CC-036` State-transition event payloads MUST follow Table K.
- `CC-037` Framework diagnostic event payloads MUST follow Table L.
- `CC-038` Informational diagnostic event payloads MUST follow Table M.
- `CC-039` `Severity` is a logging-domain log level with the values and meanings defined in Table N.
- `CC-040` `FifoControl.Index_IN` MUST be interpreted as the next producer write position and MUST NOT be interpreted as the last produced event position.
- `CC-041` If `FifoControl.BufferEmpty = TRUE`, a consumer MUST treat the event buffer as having no available event entries.
- `CC-042` If `FifoControl.BufferEmpty = FALSE` and `FifoControl.BufferFull = FALSE`, a consumer MUST treat the readable FIFO range as starting at `FifoControl.Index_OUT` and ending before `FifoControl.Index_IN`.
- `CC-043` If `FifoControl.BufferFull = TRUE`, a consumer MUST treat the readable FIFO range as starting at `FifoControl.Index_OUT` and containing exactly `FifoControl.BufferLength` entries.
- `CC-044` For the published FIFO control model, a consumer MUST advance readable FIFO positions by wrapping from `FifoControl.BufferLength` to index `1`.
- `CC-045` A consumer MUST treat only entries inside the readable FIFO range and with `SequenceId > 0` as produced UnitTest events.
- `CC-046` The UnitTest event producer MUST write event entries with FIFO overwrite disabled and MUST NOT overwrite unread event entries when the FIFO is full.
- `CC-047` If event-buffer capacity is exhausted and an event cannot be stored, the producer MUST account for the rejected event through `LostEventCount`.
- `CC-048` The producer MAY assign a `SequenceId` before the corresponding event entry is successfully stored in the FIFO.
- `CC-049` The event-buffer status field `SequenceId` MUST represent the last producer-assigned sequence id, not necessarily the highest sequence id currently available in the FIFO.
- `CC-050` A `SequenceId` gap in consumed events MAY indicate a handover or plausibility issue and MAY be caused by producer-side event loss, transport loss, reset/epoch transition, or consumer-side processing behavior.
- `CC-051` The producer MUST update `DeletedSequenceId` only after successfully deleting an event-buffer entry and MUST set it to the deleted entry's `SequenceId`.
- `CC-052` A consumer MUST acknowledge only `SequenceId` values that it has consumed from the readable FIFO range.
- `CC-053` The producer MUST expose event-stream contract metadata through event-buffer contract info according to Table O.
- `CC-054` Event-buffer `ContractInfo` is the compatibility metadata for interpreting a UnitTest event stream.
- `CC-055` A complete lifecycle event log MUST contain the lifecycle completion events defined in `Lifecycle Completion and Evidence Events` for every observed Run Control, Test Suite, and Test Case lifecycle.
- `CC-056` The Run Control StateTransition event to `UnitTest.State.Completed` is the completion anchor for the observed Run Control lifecycle in the event stream.
- `CC-057` Event streaming for one Run Control start MUST be treated as finished only after the client has received the Run Control completion anchor and the consumer has observed an empty event buffer, or after an internal client timeout expires.
- `CC-058` After producer startup or event-buffer initialization, the producer MUST expose a valid event-buffer epoch before a consumer evaluates produced event entries.
- `CC-059` A consumer MUST treat an event-buffer epoch reset as invalidating any pending acknowledgement assumptions from the previous epoch.
- `CC-060` Consumers MUST interpret the event-buffer publication area and FIFO control fields according to Tables P and Q.
- `CC-061` The producer MUST own the event stream, event-buffer contract info, FIFO control state, event-buffer status, and event-buffer entry storage.
- `CC-062` A consumer MUST treat event-buffer contract info, FIFO control state, event-buffer status, and event-buffer entry storage as producer-owned read-only data.
- `CC-063` A consumer MUST NOT modify FIFO control state, event-buffer status, or event-buffer entry storage to release processed entries.
- `CC-064` The producer MUST own all FIFO processing, including event insertion, delete-request validation, FIFO entry deletion, deleted-sequence status updates, and delete-request reset.
- `CC-065` External consumers MUST use `FifoControl.BufferLength` as the effective upper index of the published event-buffer array and MUST NOT depend on a locally known PLC constant for the buffer length.
- `CC-066` Event streaming for an observed Run Control start is finished only after the Run Control completion anchor was delivered and the event buffer was observed empty, or after an implementation-defined timeout.
- `CC-067` After event streaming is finished, a missing lifecycle completion event, event-buffer epoch reset, or `LostEventCount > 0` defines the delivered UnitTest lifecycle event log as incomplete. Missing Result or Aggregation evidence defines missing result or aggregation evidence, not a different lifecycle completion state.
- `CC-068` Event-buffer contract info, FIFO control state, event-buffer status, and event-buffer entry storage MUST NOT contain consumer-owned command bits or control functionality.
- `CC-069` Event-buffer processing MUST be performed by the producer owner; consumers only request release through the producer-owned delete request field.
- `CC-070` Run Control, Test Suite, and Test Case aggregation events MUST include `runtime` in their structured aggregation payload when the owner status runtime is non-negative.
- `CC-071` Event `Name` values for UnitTest owners MUST encode the PLC-side UnitTest hierarchy as a dot-separated owner path.
- `CC-072` Full Owner names MUST follow the hierarchy `<runControlName>`, `<runControlName>.<suiteName>`, and `<runControlName>.<suiteName>.<testCaseName>`.
- `CC-073` Assertion event names MUST start with `<runControlName>.<suiteName>.<testCaseName>.` and MUST contain a non-empty assertion-local name after the third dot.
- `CC-074` A client MUST derive the parent of a hierarchical UnitTest event name by removing the last path segment, except for Assertion events where the Test Case parent is the first three path segments.
- `CC-075` Run Control, Test Suite, and Test Case local name segments MUST contain only ASCII letters, ASCII digits, `_`, or `-`.
- `CC-076` Run Control, Test Suite, and Test Case local name segments MUST NOT contain whitespace, `.`, control characters, or be empty.
- `CC-077` The complete event `Name` MUST fit into `UnitTest.TextLength.Name`; truncated hierarchical names MUST NOT be produced.
- `CC-078` Orchestrator diagnostic event names MUST use the reserved form `<parentName>.Orchestrator`.
- `CC-079` An event-buffer reset MUST be requested through the Run Control buffer command field `Reset`.
- `CC-080` `Reset = FALSE` MUST mean that no event-buffer reset request is pending.
- `CC-081` The producer MUST process event-buffer Reset and Delete requests from request snapshots captured before request dispatch.
- `CC-082` Event-buffer Reset processing MUST have priority over Delete processing when both requests are present in the same producer processing cycle.
- `CC-083` A processed event-buffer Reset request MUST clear all buffered event entries, reset `SequenceId`, `DeletedSequenceId`, `LostEventCount`, and `ReservedEntryCount` to `0`, and reinitialize producer-owned FIFO control state.
- `CC-084` After processing an event-buffer Reset request, the producer MUST reset both `Reset` to `FALSE` and `DeleteSequenceId` to `0` unconditionally.
- `CC-085` Processing an event-buffer Reset request MUST start a new event-buffer epoch and invalidate all buffered events and outstanding Delete requests from the previous epoch.
- `CC-086` Rewriting `Reset = TRUE` while `Reset` is already `TRUE` MUST NOT represent a distinguishable second Reset request.
- `CC-087` A successful event-buffer Reset MUST NOT produce an event solely to report that Reset.
- `CC-088` The producer MUST maintain the aggregate Test Case event-buffer reservation internally as event-buffer status field `ReservedEntryCount : UInt`.
- `CC-089` `ReservedEntryCount` MUST be producer-owned and MUST represent the sum of event-buffer reservations currently held by Test Case Orchestrators.
- `CC-090` `ReservedEntryCount` MUST NOT be externally accessible or visible and MUST NOT form part of the consumer interface.
- `CC-091` A producer reservation increase MUST be atomic and MUST be rejected without a partial change when physical free capacity cannot cover the resulting global reservation count and the producer headroom applicable to the requested operation.
- `CC-092` `ReservedEntryCount = 0` MUST mean that no Test Case execution or Reset reservation is currently held.
- `CC-093` An event-buffer Reset request MUST NOT be issued during active UnitTest execution; doing so is invalid external use because the request discards the reservation accounting used by active Test Case Orchestrators.

## Requirement Details

### Event Stream Model

The UnitTest event stream is produced by the PLC-side UnitTest framework. It is consumed by exactly one logical consumer.

The producer owns the event stream and the complete event-buffer publication area. This includes event-buffer contract info, FIFO control state, event-buffer status, and event-buffer entry storage.

The consumer reads produced event entries, transfers or otherwise makes them available to a client, and acknowledges entries that may be released by the producer. The consumer does not take ownership of FIFO state or buffer entries by reading them.

The delivered UnitTest event log and result evidence are derived from the event stream. The event stream does not define or drive the PLC-side lifecycle; it records lifecycle and result evidence produced by the PLC-side owner.

The event stream is transport-independent. Concrete access profiles define how the event-buffer model is mapped to symbolic PLC paths, transport endpoints, or other handover mechanisms.

### Producer Capacity Reservations

The producer uses `Status.ReservedEntryCount` to coordinate capacity across Test Case Orchestrators. The value is the aggregate number of event-buffer entries currently reserved for accepted Test Case execution or Reset work. It prevents separate Orchestrators from admitting work against the same physical free capacity.

Reservations do not create readable FIFO entries and do not change `Index_IN`, `Index_OUT`, `ElementCnt`, or the consumer acknowledgement model. `ReservedEntryCount` remains producer-internal; consumers do not read, modify, or otherwise participate in reservation accounting.

An event-buffer Reset through `BufferCmd.Reset` clears all reservation accounting together with the buffered events and event-buffer status. This event-buffer operation is distinct from a Run Control lifecycle Reset. A successful hierarchical Run Control Reset releases reservations through normal owner and Orchestrator processing as all subordinate Test Cases return to `Idle AND Ready`.

### Event Buffer Handover

The event buffer is a FIFO-backed exchange area. It consists of two separate parts:

- `FifoControl` is the producer-owned FIFO metadata area. It describes which positions in the buffer array are currently readable.
- `Buffer` is the event-entry storage array. It contains the event payload data at the positions described by `FifoControl`.

The producer owns all FIFO processing. Consumers never insert, delete, overwrite, reinitialize, repair, or otherwise modify FIFO control state, event-buffer status, or event-buffer entry storage.

The event-buffer publication area contains no consumer-owned control functionality. It contains no consumer-owned command bits and no direct consumer control fields. Event-buffer processing is performed by the producer owner. Consumers request release of processed entries only through the producer-owned delete request field.

Available event-buffer entries are determined from the producer-owned FIFO control state, especially:

- `Index_OUT`,
- `Index_IN`,
- `BufferEmpty`,
- `BufferFull`,
- the configured buffer length.

The effective event-buffer array range for external consumers is `1..FifoControl.BufferLength`. External consumers do not need to know the PLC project constant that was used to declare the producer-side array. PLC-internal consumers may use compile-time array bounds or PLC constants when they are compiled against the same data model, but this is not required for external consumption.

`FifoControl.Index_IN` represents the next producer write position. It is the FIFO input boundary, not the last produced event position.

`FifoControl.Index_OUT` represents the current FIFO output position. If entries are available, the next event entry to be processed by the consumer is located at `Buffer[Index_OUT]`.

If `FifoControl.BufferEmpty = TRUE`, no event-buffer entry is available for consumption.

If `FifoControl.BufferEmpty = FALSE` and `FifoControl.BufferFull = FALSE`, the readable FIFO range starts at `FifoControl.Index_OUT` and ends before `FifoControl.Index_IN`.

If `FifoControl.BufferFull = TRUE`, the readable FIFO range starts at `FifoControl.Index_OUT` and contains exactly `FifoControl.BufferLength` entries.

The consumer derives FIFO read order from `FifoControl`. It must not assume that readable entries are stored in simple ascending physical array order.

Only entries inside the readable FIFO range and with `SequenceId > 0` are produced UnitTest events.

Consumers derive the available area from FIFO control state. Available entries are not necessarily linearly ordered in the buffer array. The physical array order is therefore not the same thing as FIFO read order.

The UnitTest event producer writes event entries with FIFO overwrite disabled. If the FIFO is full, the producer does not overwrite unread event entries. The rejected event is not available in the event-buffer entry array.

Functionally, a consumer derives readable event-entry indexes from `FifoControl` as follows:

1. Validate event-buffer `ContractInfo`.
2. Read `FifoControl`, event-buffer `Status`, and the relevant event-buffer entries.
3. If `FifoControl.BufferEmpty = TRUE`, no event entry is available.
4. If `FifoControl.BufferFull = FALSE`, process FIFO positions from `Index_OUT` up to but not including `Index_IN`.
5. If `FifoControl.BufferFull = TRUE`, process exactly `BufferLength` FIFO positions starting at `Index_OUT`.
6. Treat only entries inside that readable FIFO range and with `SequenceId > 0` as produced events.
7. Preserve FIFO order when deciding which processed `SequenceId` can be acknowledged for release.

For the current PLC-side FIFO control model, FIFO position advances by incrementing the current index. If the next index would be greater than `FifoControl.BufferLength`, it wraps to `1`. For example, with `BufferLength = 5`, `Index_OUT = 4`, `Index_IN = 2`, and `BufferFull = FALSE`, the readable FIFO positions are `4`, `5`, and `1`. Position `2` is the next producer write position and is not part of the readable range.

Consumers use the derived FIFO positions as indexes into `Buffer`. In the example above, the consumer reads `Buffer[4]`, `Buffer[5]`, and `Buffer[1]` in that order.

#### Event-Buffer Publication Area

The event-stream model owns the event-buffer publication structure. Concrete access profiles define how this structure is accessed, but they do not redefine the event-buffer field model or FIFO semantics.

##### Table P: Event-buffer publication area

| Symbolic field | Type | Access | Idle/default value | Consumer interpretation |
| --- | --- | --- | --- | --- |
| `ContractInfo.ContractVersion` | `String[10]` | Read | Producer-defined | Event-stream model contract version. Validate before interpreting event-buffer data. |
| `ContractInfo.FrameworkVersion` | `String[10]` | Read | Producer-defined | PLC-side UnitTest framework implementation version. Used for compatibility and diagnostics. |
| `FifoControl` | Structure | Read | See Table Q | Producer-owned FIFO state used to derive the readable event-buffer range. |
| `Status.SequenceId` | `UDInt` | Read | `0` | Last producer-assigned event sequence id in the current event-buffer epoch. This is not necessarily the highest sequence id currently readable from `Buffer`. |
| `Status.DeletedSequenceId` | `UDInt` | Read | `0` | Last event sequence id deleted by the producer after a valid consumer acknowledgement. |
| `Status.LostEventCount` | `UInt` | Read | `0` | Producer-side count of events that could not be retained in the event buffer. Non-zero means the event stream is incomplete. |
| `Buffer` | `ARRAY[1..FifoControl.BufferLength] OF UDT_UnitTestEventEntry` from the external consumer perspective | Read | Entries with `SequenceId = 0` | FIFO-backed event entry storage. External consumers use `FifoControl.BufferLength` as the effective upper index and do not depend on a locally known PLC constant. Consumers interpret only entries inside the readable FIFO range and with `SequenceId > 0`. Event-entry fields and payload semantics are defined by this contract. |

##### Table Q: FifoControl fields

| Field | Type | Consumer interpretation |
| --- | --- | --- |
| `Index_OUT` | `UInt` | FIFO output position. The readable range starts here when `BufferEmpty = FALSE`. |
| `Index_IN` | `UInt` | Next producer write position. It is the FIFO input boundary, not the last produced event position. |
| `BufferEmpty` | `Bool` | `TRUE` means no event-buffer entry is available for consumption. |
| `BufferFull` | `Bool` | `TRUE` means the readable range starts at `Index_OUT` and contains exactly `BufferLength` entries. |
| `BufferAlmostFull` | `Bool` | Producer-side capacity warning state. Relevant for diagnostics; the event stream remains authoritative for retained events and lost-event reporting. |
| `ElementCnt` | `UInt` | Producer-side count of currently occupied FIFO entries. Useful for diagnostics and plausibility; readable range rules remain authoritative. |
| `BufferLength` | `UInt` | Configured FIFO length and effective upper buffer index for external consumers. |
| `ConfigError` | `Bool` | `TRUE` means FIFO configuration is invalid. Consumers must treat event-buffer consumption as not reliable while this is true. |
| `IndexError` | `Bool` | `TRUE` means FIFO index state is invalid or being repaired by the producer. Consumers must retry or defer event-buffer processing. |

#### Sequence Ids

`SequenceId` is an event-stream handover identifier. It identifies produced event entries within one event-buffer epoch and supports acknowledgement, release, gap checks, and handover diagnostics.

The producer assigns sequence ids during event creation. Assignment can happen before the event entry is successfully stored in the FIFO. Therefore the event-buffer status field `SequenceId` represents the last producer-assigned sequence id, not necessarily the highest sequence id currently readable from the event-buffer entry array.

One logical event-creation call can assign more than one sequence id when the framework also creates an automatic diagnostic event, for example a capacity diagnostic event.

If an event entry cannot be stored because the FIFO capacity is exhausted, the assigned sequence id is still part of the producer-side event sequence. The corresponding event entry is not available to the consumer, and the producer accounts for the loss through `LostEventCount`.

A gap in consumed sequence ids is a possible plausibility issue. It can indicate producer-side event loss, transport loss, a reset/epoch transition, or consumer-side processing behavior. Gap interpretation depends on the event-buffer epoch, `LostEventCount`, and the consumer/transport context.

#### Delete Requests

`DeleteSequenceId` is part of the general producer/consumer handover model.

The consumer requests release of processed event-buffer entries by writing the producer-owned Run Control buffer command field `DeleteSequenceId`. Concrete access profiles define the symbolic path for this field; for the PLCSIM Advanced profile this is `DB_UnitTestRunControl.ControlData.BufferCmd.DeleteSequenceId`.

The producer deletes from the FIFO output side. The producer does not delete arbitrary entries by buffer index. The request identifies the highest processed sequence id that may be released.

Delete processing compares the request against the sequence ids of readable FIFO entries, starting at the current FIFO output position. `DeletedSequenceId` advances only after the producer has removed a contiguous FIFO sequence up to and including the deleted event entry. A consumer acknowledges only sequence ids consumed from the readable FIFO range.

`DeletedSequenceId` represents the last sequence id that the producer deleted from the FIFO after a valid consumer acknowledgement. It is updated from the deleted event entry, not from the requested delete value. Because the producer deletes from the FIFO output side, setting `DeletedSequenceId` confirms that all earlier FIFO entries in the deleted sequence were also removed.

Functionally, delete acknowledgement works as follows:

1. The consumer reads one or more available event entries from the readable FIFO range.
2. The consumer processes those entries in FIFO order.
3. The consumer determines the highest processed `SequenceId` for which all earlier not-yet-released entries were also processed.
4. The consumer writes that value to the producer-owned `DeleteSequenceId` request field. If a request is already pending, the consumer may replace it with a higher valid acknowledgement watermark but does not replace it with a lower value.
5. The producer captures the current `DeleteSequenceId` as its processing snapshot.
6. The producer validates the snapshot against the current FIFO output side and deletes entries up to and including the requested sequence id.
7. The producer updates `DeletedSequenceId` from the last successfully deleted event entry after all earlier FIFO entries in that delete sequence were also removed.
8. After processing or rejecting the snapshot, the producer resets `DeleteSequenceId` to `0` only if the command field still equals that snapshot. A distinguishably newer request remains pending for subsequent processing.

The consumer does not clear event entries itself. `DeleteSequenceId = 0` confirms that no delete request is pending. A nonzero value may be replaced only with a higher valid acknowledgement watermark.

#### Event-Buffer Reset Requests

`Reset` is the destructive event-buffer reset request in the general producer/consumer handover model. Concrete access profiles define the symbolic path for this field.

The Reset request is independent of the UnitTest Run Control lifecycle commands. It resets event-stream storage and handover state; it does not reset, abort, or start a UnitTest lifecycle.

Functionally, event-buffer reset processing works as follows:

1. A consumer writes `Reset := TRUE`.
2. The producer captures `Reset` and `DeleteSequenceId` before dispatching buffer requests.
3. If the captured Reset request is `TRUE`, the producer processes it before any Delete request from the same processing cycle.
4. The producer clears all buffered event entries, resets `SequenceId`, `DeletedSequenceId`, and `LostEventCount` to `0`, and reinitializes FIFO control state.
5. The reset starts a new event-buffer epoch. All buffered events and outstanding Delete requests from the previous epoch are discarded.
6. The producer resets both `Reset := FALSE` and `DeleteSequenceId := 0` unconditionally.
7. The cleared `Reset` field confirms completion of the request.

Writing `TRUE` again while `Reset` is already `TRUE` does not identify a second request. A successful reset does not create an event solely to report the reset because that event would immediately become the first entry of the newly initialized epoch.

#### Event-Buffer Epochs

When the event buffer is cleared or reset, `SequenceId` and `DeletedSequenceId` return to `0`. Consumers and clients treat this as a new event-buffer epoch. Outstanding assumptions from the previous epoch are no longer valid.

After producer startup or event-buffer initialization, the producer is responsible for exposing a valid event-buffer epoch before produced event entries are evaluated. A consumer does not repair event-buffer state by writing event-buffer data directly.

#### Lost Events

`LostEventCount > 0` is the producer-side indication that at least one produced event could not be retained in the event stream.

The producer increments `LostEventCount` when an event entry could not be stored because event-buffer capacity was exhausted. This includes a rejected normal event write and an automatic capacity diagnostic event that itself could not be stored.

Sequence-id gaps can additionally indicate possible handover problems. A sequence-id gap without a corresponding producer-side lost-event indication may be caused by transport, consumer behavior, reset/epoch, or plausibility issues.

For automated UnitTest evaluation data, `LostEventCount > 0` defines the delivered event stream as incomplete. Events delivered before or after the loss remain valid event entries, but the event stream no longer represents a complete, fully reliable UnitTest log.

### Event Entry Model

Every event-buffer entry uses the common UnitTest event payload. The concrete PLC datatype is defined by the producer data model; client-side interpretation is defined here.

UnitTest semantic interpretation comes from the event fields and the local framework contracts, especially:

- `OwnerScope`,
- `EventType`,
- `Name`,
- `ResourceName`,
- `State`,
- `Result`,
- aggregation `Message` payloads defined by this contract.

#### Table A: Common event-entry fields

| Field | Client interpretation |
| --- | --- |
| `SequenceId` | Producer-assigned event-stream handover identifier. `0` means no produced event entry. Values greater than `0` identify produced events within the current event-buffer epoch. |
| `Timestamp` | PLC-side event timestamp. Relevant for UnitTest log output and diagnostic ordering context. It is not the acknowledgement identity. |
| `OwnerScope` | Semantic owner or technical diagnostic context represented by the event. Values are defined in Table B. |
| `EventType` | Event payload category. Values are defined in Table C and interpreted according to Table G. |
| `Severity` | Logging-domain log level. Values are defined in Table N for UnitTest event-stream consumers. |
| `Name` | Hierarchical UnitTest owner path or framework diagnostic name. UnitTest owner names follow the hierarchy defined in `Hierarchical Names`. |
| `ResourceName` | Optional referenced resource name. Empty string means no resource context is provided. |
| `State` | Event-related UnitTest lifecycle state. Values are defined in Table E. |
| `Result` | Event-related UnitTest result. Values are defined in Table D. |
| `Message` | Event-type specific payload. Aggregation events use the JSON payload in Table H; other event types use human-readable diagnostic text unless this contract states otherwise. |

#### Hierarchical Names

UnitTest owner events use `Name` as the machine-readable parent/child identity. The event stream does not provide a separate parent id. Clients derive parent/child relationships from the hierarchical `Name` according to this section.

Full Owner names use these forms:

| Owner scope | Required `Name` form | Parent |
| --- | --- | --- |
| `UnitTest.OwnerScope.RunControl` | `<runControlName>` | none |
| `UnitTest.OwnerScope.TestSuite` | `<runControlName>.<suiteName>` | `<runControlName>` |
| `UnitTest.OwnerScope.TestCase` | `<runControlName>.<suiteName>.<testCaseName>` | `<runControlName>.<suiteName>` |

Assertion event names use:

```text
<runControlName>.<suiteName>.<testCaseName>.<assertionName>
```

For Assertion events, the Test Case parent is always the first three path segments. Everything after the third dot belongs to the assertion-local name. This allows assertion authors to use additional dots inside assertion-local diagnostic names without changing the Test Case parent relationship. The assertion-local name must still be non-empty and must not contain control characters.

Run Control, Test Suite, and Test Case local name segments are intentionally restrictive. They may contain only ASCII letters, ASCII digits, `_`, and `-`. They must not contain `.`, whitespace, control characters, or be empty. The complete event `Name` must fit into `UnitTest.TextLength.Name`; producers must treat names that do not fit as invalid configuration instead of truncating them.

Technical Orchestrator diagnostics use the reserved suffix:

```text
<parentName>.Orchestrator
```

These events use Orchestrator owner scopes and are diagnostics for generic helper state machines. They are not Full Owner lifecycle completion evidence. The reserved suffix describes the technical diagnostic event name; it does not reserve `Orchestrator` as a normal authored local name segment.

Framework-global diagnostics that are not tied to a UnitTest owner may use a framework component name instead of a hierarchical owner path.

#### Table F: Field data types and text length constants

| Field or constant | Value | Client interpretation |
| --- | --- | --- |
| `SequenceId` | `UDInt` | Produced event sequence id. |
| `DeletedSequenceId` | `UDInt` | Last producer-deleted event sequence id. |
| `LostEventCount` | `UInt` | Producer-side lost-event counter. |
| `OwnerScope` | `USInt` | Owner scope value according to Table B. |
| `EventType` | `USInt` | Event type value according to Table C. |
| `Severity` | `USInt` | Logging-domain log level value according to Table N. |
| `State` | `Int` | UnitTest state value according to Table E. |
| `Result` | `Int` | UnitTest result value according to Table D. |
| `UnitTest.TextLength.Name` | `80` | Maximum number of characters for `Name`. |
| `UnitTest.TextLength.ResourceName` | `32` | Maximum number of characters for `ResourceName`. |
| `UnitTest.TextLength.Message` | `254` | Maximum number of characters for `Message`. |
| `UnitTest.TextLength.AssertionValue` | `40` | Maximum number of characters for one formatted assertion value before it is embedded in an event message. |

#### Contract Info

Event-buffer `ContractInfo` exposes the event-stream model version and framework version for consumers.

This contract info answers whether the UnitTest event stream can be interpreted according to this contract. It is separate from access-profile or Run Control contract info that may be exposed by a concrete communication profile.

##### Table O: ContractInfo fields

| Field | Type | Client interpretation |
| --- | --- | --- |
| `ContractVersion` | `STRING[10]` | Version of the PLC-owned UnitTest event-stream model implemented by the producer. |
| `FrameworkVersion` | `STRING[10]` | Version of the PLC-side UnitTest framework implementation. |

Event-stream compatibility is defined by the exposed event-buffer `ContractVersion`. Unsupported contract versions are not covered by this contract and must not be interpreted by guessing field or payload semantics.

### Event Classification

This event-stream model contract is the source of truth for client-side interpretation of UnitTest event entries.

This contract defines the event entry data model, symbolic event-type constants, and client-side event interpretation at the event-stream boundary.

#### Table B: OwnerScope constants

| Symbolic constant | Value | Client interpretation |
| --- | ---: | --- |
| `UnitTest.OwnerScope.Unknown` | `0` | Unknown or unset owner scope. |
| `UnitTest.OwnerScope.Framework` | `5` | Framework or producer diagnostic context. |
| `UnitTest.OwnerScope.RunControl` | `10` | Run Control level. |
| `UnitTest.OwnerScope.TestSuiteOrchestrator` | `15` | Run Control-level Suite Orchestrator diagnostic context; not Full Owner lifecycle completion evidence. |
| `UnitTest.OwnerScope.TestSuite` | `20` | Test Suite level. |
| `UnitTest.OwnerScope.TestCaseOrchestrator` | `25` | Test Suite-level Test Case Orchestrator diagnostic context; not Full Owner lifecycle completion evidence. |
| `UnitTest.OwnerScope.TestCase` | `30` | Test Case level. |
| `UnitTest.OwnerScope.Assertion` | `40` | Assertion-level finding context. |
| `UnitTest.OwnerScope.Preparation` | `50` | Preparation context. |

#### Table C: EventType constants

| Symbolic constant | Value | Client interpretation |
| --- | ---: | --- |
| `UnitTest.EventType.Unknown` | `0` | Unknown or unset event type. |
| `UnitTest.EventType.Result` | `10` | Result event payload. |
| `UnitTest.EventType.Trigger` | `20` | Trigger diagnostic payload. |
| `UnitTest.EventType.StateTransition` | `30` | Lifecycle transition payload. |
| `UnitTest.EventType.Info` | `40` | Informational diagnostic payload. |
| `UnitTest.EventType.Aggregation` | `50` | Aggregation event payload. |
| `UnitTest.EventType.Framework` | `60` | Framework diagnostic payload. |

#### Table D: Result constants

| Symbolic constant | Value | Client interpretation |
| --- | ---: | --- |
| `UnitTest.Result.Error` | `-10` | Error outcome or error finding. |
| `UnitTest.Result.NotEvaluated` | `0` | No evaluated result for this event context. |
| `UnitTest.Result.Passed` | `10` | Passed outcome. |
| `UnitTest.Result.Failed` | `20` | Failed outcome. |
| `UnitTest.Result.Abort` | `30` | Explicit abort outcome. |

#### Table E: State constants

| Symbolic constant | Value | Client interpretation |
| --- | ---: | --- |
| `UnitTest.State.Error` | `-10` | Legacy/error state value. New owner finalization does not use this as stable final state. |
| `UnitTest.State.Uninitialized` | `0` | Defined non-executable state before explicit reset established a base state. |
| `UnitTest.State.Reset` | `1` | Reset lifecycle state. |
| `UnitTest.State.Idle` | `10` | Startable base state when the owner reports ready. |
| `UnitTest.State.Starting` | `11` | Start transition state. |
| `UnitTest.State.Preparing` | `20` | Preparation lifecycle state. |
| `UnitTest.State.Running` | `50` | Active execution/evaluation state. |
| `UnitTest.State.Completing` | `80` | Finalization state where final result and aggregation events are emitted. |
| `UnitTest.State.Completed` | `89` | Stable final lifecycle state and event-log completion anchor for the observed Run Control lifecycle. |
| `UnitTest.State.Aborting` | `90` | Explicit abort handling state. |
| `UnitTest.State.AbortingFromError` | `91` | Controlled error-abort handling state. |
| `UnitTest.State.Aborted` | `99` | Legacy aborted state value. New owner finalization does not use this as stable final state. |

#### Table N: Severity log-level constants

`Severity` classifies the log level, relevance, and urgency of an event entry. It does not replace `Result`, `State`, `EventType`, or PLC-side lifecycle semantics.

Trace and Debug levels are diagnostic log levels for code-level observability. They are not normative result evidence and are not required to determine the final UnitTest outcome.

| Symbolic constant | Type | Value | Client interpretation |
| --- | --- | ---: | --- |
| `Logging.Severity.Trace` | `USInt` | `0` | Log level for code traces. |
| `Logging.Severity.Debug` | `USInt` | `1` | Log level for debugging. |
| `Logging.Severity.Info` | `USInt` | `2` | Log level for informational logs. |
| `Logging.Severity.Warning` | `USInt` | `3` | Log level for warnings. |
| `Logging.Severity.Error` | `USInt` | `4` | Log level for errors. |
| `Logging.Severity.Critical` | `USInt` | `5` | Log level for critical errors. |

For result-oriented UnitTest events, clients classify test outcome by `Result`, not by `Severity`. Severity may be used for log presentation, filtering, alerting, and diagnostics.

#### Table G: Event types

| Event type | Client interpretation |
| --- | --- |
| `UnitTest.EventType.Result` | Result event. Defines an owner or assertion result/finding according to `OwnerScope`, `Name`, `State`, and `Result`. Final owner result events are required by the lifecycle contracts. |
| `UnitTest.EventType.Trigger` | Trigger diagnostic event. Documents command or trigger edges and MUST NOT be used as result or completion evidence. |
| `UnitTest.EventType.StateTransition` | Lifecycle transition event. `State` contains the new state. Run Control transition to `UnitTest.State.Completed` is the event-log completion anchor consumed by the client. |
| `UnitTest.EventType.Info` | Informational diagnostic event. Provides context and MUST NOT replace required Result, Aggregation, or completion-anchor events. |
| `UnitTest.EventType.Aggregation` | Aggregation event. Contains structured aggregation data in `Message` according to Table H. |
| `UnitTest.EventType.Framework` | Framework diagnostic event. Provides technical diagnostics and MUST NOT replace required Result, Aggregation, or completion-anchor events. |

Clients distinguish event classes through `EventType` first. `OwnerScope` defines the semantic context represented by the event. Run Control, Test Suite, and Test Case scopes represent Full Owner lifecycle, Result, and Aggregation evidence. Orchestrator scopes represent technical orchestration diagnostics only.

### Event Payloads

#### Result Event Payload

Result events use `EventType = UnitTest.EventType.Result`.

##### Table I: Result event payload

| Field | Required value or meaning |
| --- | --- |
| `OwnerScope` | Semantic owner or assertion scope represented by the result. |
| `Name` | Owner or assertion name represented by the result. |
| `ResourceName` | Optional resource context. Empty string means no resource context. |
| `State` | Lifecycle state at which the result was emitted. Final owner result events are emitted in `UnitTest.State.Completing`. |
| `Result` | Result value according to Table D. |
| `Message` | Human-readable result or finding text. Clients do not require parsing this field for normative result classification. |

#### Aggregation Payloads

Aggregation events are UnitTest event entries that represent final owner aggregation data for Run Control, Test Suite, or Test Case owners.

Aggregation events use `UnitTest.EventType.Aggregation`; clients identify the aggregation level by `OwnerScope`:

- Run Control aggregation: `OwnerScope = UnitTest.OwnerScope.RunControl` and `Message` follows the Run Control JSON payload in Table H.
- Test Suite aggregation: `OwnerScope = UnitTest.OwnerScope.TestSuite` and `Message` follows the Test Suite JSON payload in Table H.
- Test Case aggregation: `OwnerScope = UnitTest.OwnerScope.TestCase` and `Message` follows the Test Case JSON payload in Table H.

For aggregation events, `Message` is a contract-stable compact JSON object payload. Other event messages remain human-readable diagnostic text unless this event-stream model contract explicitly defines a structured payload.

Aggregation JSON payload rules:

- Payloads are encoded as one compact JSON object without insignificant whitespace.
- Field names use the stable names listed below.
- Numeric values are unsigned decimal JSON numbers.
- Clients can ignore unknown additional fields.
- Missing required fields make the aggregation payload incomplete for the affected event.
- `runtime` is included when the owner status runtime is non-negative. It is omitted when no valid runtime value is available.
- The owner context is defined by `OwnerScope`, `Name`, `State`, and `Result`; JSON field names do not include the owner level.

##### Table H: Aggregation JSON payloads

| Aggregation owner | Required JSON payload shape |
| --- | --- |
| Run Control | `{"total":0,"inactive":0,"idle":0,"running":0,"completed":0,"passed":0,"failed":0,"error":0,"runtime":0,"lostEvents":0}` |
| Test Suite | `{"total":0,"inactive":0,"idle":0,"running":0,"completed":0,"passed":0,"failed":0,"error":0,"runtime":0}` |
| Test Case | `{"total":0,"passed":0,"failed":0,"error":0,"runtime":0}` |

#### Trigger Event Payload

Trigger events use `EventType = UnitTest.EventType.Trigger`.

##### Table J: Trigger event payload

| Field | Required value or meaning |
| --- | --- |
| `OwnerScope` | Semantic owner scope receiving or emitting the trigger context. |
| `Name` | Owner name. |
| `ResourceName` | Optional resource context. Empty string means no resource context. |
| `State` | Owner state at the trigger edge. |
| `Result` | Not result-relevant. Trigger events are not result evidence. |
| `Message` | Human-readable trigger text such as start, abort, or reset trigger. |

#### State-Transition Event Payload

State-transition events use `EventType = UnitTest.EventType.StateTransition`.

##### Table K: State-transition event payload

| Field | Required value or meaning |
| --- | --- |
| `OwnerScope` | Owner or technical orchestration scope whose state changed. |
| `Name` | Owner name. |
| `ResourceName` | Optional resource context. Empty string means no resource context. |
| `State` | New lifecycle state after the transition. |
| `Result` | Not result-relevant. Clients use result events for result classification. |
| `Message` | Human-readable transition text. The previous state is diagnostic text only in the current payload model. |

The Run Control state-transition event with `State = UnitTest.State.Completed` is the event-log completion anchor for the observed Run Control lifecycle.

Only Full Owner state-transition events (`RunControl`, `TestSuite`, `TestCase`) are lifecycle completion evidence. Orchestrator-scoped state-transition events (`TestSuiteOrchestrator`, `TestCaseOrchestrator`) are technical diagnostics for helper state machines and are not expected final owner events.

For a Test Suite lifecycle, owner state-transition events use `OwnerScope = UnitTest.OwnerScope.TestSuite` and the full hierarchical Test Suite name. This observable event-stream rule is independent of whether the project implementation uses the generic Suite Sequencer and Test Case Orchestrators or implements the required Suite behavior directly. Test Case Orchestrator events remain technical diagnostics under `UnitTest.OwnerScope.TestCaseOrchestrator` and `<suiteName>.Orchestrator`.

### Lifecycle Completion and Evidence Events

The final lifecycle event of a Full Owner is the `StateTransition` event into `UnitTest.State.Completed`. This event states that the observed owner lifecycle has reached its stable completed state.

Result and Aggregation events are not lifecycle completion events. They provide the final owner result and the aggregation data that a client may use to build a UnitTest log entry or enrich the log with subordinate evaluation data.

For one observed Full Owner lifecycle, the event stream distinguishes:

| Owner lifecycle | Lifecycle completion event | Result evidence | Aggregation evidence |
| --- | --- | --- | --- |
| Run Control | One Run Control `StateTransition` event with `State = UnitTest.State.Completed`. | One Run Control Result event. | One Run Control Aggregation event. |
| Test Suite | One Test Suite `StateTransition` event with `State = UnitTest.State.Completed`. | One Test Suite Result event. | One Test Suite Aggregation event. |
| Test Case | One Test Case `StateTransition` event with `State = UnitTest.State.Completed`. | One Test Case Result event. | One Test Case Aggregation event. |

The Run Control `StateTransition` event with `State = UnitTest.State.Completed` is the completion anchor for the observed Run Control lifecycle in the event stream. Test Suite and Test Case completion events are expected when their lifecycles were observed in the same event-buffer epoch.

Result and Aggregation evidence is emitted before the corresponding owner lifecycle completion event. Strict adjacency between these events is not part of the contract; the relevant contract property is that lifecycle completion, result evidence, and aggregation evidence belong to the same event-buffer epoch.

Typical successful Run Control event evidence:

1. Run Control trigger and state-transition diagnostics.
2. Test Suite and Test Case lifecycle, result, and aggregation events.
3. Run Control Result event.
4. Run Control Aggregation event.
5. Run Control StateTransition event with `State = UnitTest.State.Completed`.

Typical aborted or error-aborted owner evidence:

1. Trigger or framework diagnostic events may document the cause.
2. The affected owner emits its final Result event with the appropriate `Result` value.
3. The affected owner emits its Aggregation event if that owner defines aggregation.
4. The affected owner lifecycle ends with its `StateTransition` event with `State = UnitTest.State.Completed`.
5. The Run Control lifecycle still ends with the Run Control `StateTransition` event with `State = UnitTest.State.Completed` when the overall run lifecycle reaches its completion anchor.

#### Framework Diagnostic Event Payload

Framework diagnostic events use `EventType = UnitTest.EventType.Framework` and `OwnerScope = UnitTest.OwnerScope.Framework`.

##### Table L: Framework diagnostic event payload

| Field | Required value or meaning |
| --- | --- |
| `Name` | Framework component or diagnostic context name. |
| `ResourceName` | Empty string unless a specific resource context is available. |
| `State` | Related state if available; otherwise a defined neutral state may be used by the producer. |
| `Result` | Not result-relevant unless a later contract revision defines a specific structured diagnostic result. |
| `Severity` | Logging-domain severity for the diagnostic. |
| `Message` | Human-readable diagnostic text. Clients do not use framework diagnostics as replacement for required result, aggregation, or completion-anchor events. |

#### Informational Diagnostic Event Payload

Informational diagnostic events use `EventType = UnitTest.EventType.Info`.

##### Table M: Informational diagnostic event payload

| Field | Required value or meaning |
| --- | --- |
| `OwnerScope` | Semantic owner scope represented by the information. |
| `Name` | Owner or diagnostic context name. |
| `ResourceName` | Optional resource context. Empty string means no resource context. |
| `State` | Related state if available. |
| `Result` | Not result-relevant unless a later contract revision defines a specific interpretation. |
| `Severity` | Logging-domain severity for the information. |
| `Message` | Human-readable diagnostic text. |

### Event-Log Completeness Model

UnitTest lifecycle completion, result evidence, and aggregation evidence are derived from the delivered event stream and the local lifecycle/event contracts. Other producer-side projections are outside the event-stream model unless an access profile explicitly exposes them as diagnostic data.

The PLC-side Run Control owner determines the actual lifecycle of the UnitTest run. The event stream records lifecycle evidence. The Run Control StateTransition event to `UnitTest.State.Completed` is therefore the completion anchor for the observed run lifecycle in the event stream, not the mechanism that drives or defines the PLC-side lifecycle.

The completion anchor is not a transport-end marker. Events may still be transferred to or processed by the client after the PLC-side Run Control already reached `UnitTest.State.Completed`.

For one Run Control start, event streaming is finished only when both conditions are fulfilled:

- the client has received the Run Control completion anchor,
- the consumer has observed that the event buffer is empty.

If these conditions are not fulfilled within an implementation-defined monitoring time, event streaming for that observed Run Control start is timed out.

A complete lifecycle event log contains the lifecycle completion events defined in `Lifecycle Completion and Evidence Events` for every observed Run Control, Test Suite, and Test Case lifecycle. Missing lifecycle completion events make the delivered UnitTest lifecycle event log incomplete only after event streaming for the observed Run Control start is finished or timed out. Missing Result or Aggregation events leave the corresponding result or aggregation evidence incomplete.

Functionally, event-log completeness for one observed Run Control lifecycle is defined by these event-stream facts:

1. Delivered event entries belong to one event-buffer epoch.
2. Observed Run Control, Test Suite, and Test Case lifecycles have lifecycle completion events in that epoch.
3. The Run Control StateTransition event with `State = UnitTest.State.Completed` is the completion anchor for the observed Run Control lifecycle in the event stream.
4. Event streaming for that observed Run Control lifecycle is finished when the completion anchor was delivered and the event buffer is observed empty, or when an implementation-defined timeout expires.
5. Lifecycle completion events are only complete if they were delivered in the same epoch before event streaming finished or timed out.
6. A missing lifecycle completion event, event-buffer epoch reset, timeout, or `LostEventCount > 0` defines an incomplete UnitTest lifecycle event log.
7. Result and Aggregation events are separate result and aggregation evidence for the completed lifecycles.

Producer-side status projections are not replacement evidence for missing event-stream entries. Status projections may indicate that the PLC-side lifecycle progressed, but they do not repair incomplete lifecycle, result, or aggregation evidence in the event stream.

## Compatibility

This contract defines the transport-independent producer/consumer handover model.

Specific access profiles may define concrete access paths or transport behavior, but they must preserve the producer-owned event-stream semantics defined here.

### Future Access Profiles

Future access profiles consume this event-stream model. Examples can include direct PLC access, a PLC-internal consumer, an MQTT bridge, OPC UA, socket-based transfer, file export, REST endpoints, or another transport adapter.

An access profile defines only transport- or access-specific concerns, for example:

- symbolic paths, topics, endpoints, files, or API resources,
- serialization format,
- session, authentication, or connection handling,
- retry, batching, buffering, or delivery behavior,
- access-specific read/write boundaries,
- access-specific acknowledgement mapping.

An access profile must not redefine event meaning, event types, owner scopes, result values, state values, `SequenceId` semantics, `LostEventCount` semantics, lifecycle completion event rules, or producer-owned FIFO/event-buffer ownership unless it explicitly introduces a versioned contract change.

If an access profile adds restrictions, mirrors, batching, server-owned acknowledgements, or read-only transfer without direct producer acknowledgement, those restrictions belong in that profile's contract. They do not change this event-stream model unless this contract is explicitly revised.

The event-stream model has exactly one logical consumer from the PLC producer perspective. If a future transport allows multiple technical readers, that profile must define which component is the single logical consumer that acknowledges or otherwise owns release progress toward the PLC producer.

Producer-side status projections remain diagnostic data unless a future contract explicitly changes their role. They do not replace the event stream as source for event-log completeness or result evidence.
