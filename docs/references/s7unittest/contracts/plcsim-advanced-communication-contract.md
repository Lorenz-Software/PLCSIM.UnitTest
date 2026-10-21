# PLCSIM Advanced Communication Contract

## Purpose

This contract defines the PLCSIM Advanced communication profile for controlling a PLC-owned UnitTest run and consuming its event stream through direct symbolic PLC access.

It specifies which symbolic PLC paths a PLCSIM Advanced client uses to request Run Control lifecycle actions and which symbolic PLC paths the direct-access consumer part uses to consume the UnitTest event stream and acknowledge processed event entries.

The event-stream model contract in `event-stream-model-contract.md` remains the source of truth for event meaning, sequence ids, FIFO handover, acknowledgement semantics, event payloads, and client evaluation rules.

## Scope

This contract defines:

- the PLCSIM Advanced direct-access profile for symbolic PLC access,
- the PLC data areas used by that profile for Run Control commands and event-stream consumption,
- the read/write boundary for direct-access consumers,
- the relation between PLCSIM Advanced direct access and the event-stream model contract.

This contract does not define:

- HMI, OPC, engineering, debug, or diagnostic visualization use cases,
- alternate event-stream semantics,
- PLC lifecycle or result semantics,
- PC-side class design,
- report file formats,
- implementation details of a specific PLCSIM Advanced API wrapper.

## Terms

- `PLCSIM Advanced client`: the application that uses this direct-access profile to request UnitTest lifecycle actions and receive delivered UnitTest event logs.
- `Direct-access consumer`: the technical event-stream consumer that accesses PLC data directly through PLCSIM Advanced symbolic reads and writes.
- `Direct access`: symbolic PLC reads and writes performed through PLCSIM Advanced without an intermediate transport service.
- `Symbolic path`: the PLC symbol path used by the direct-access consumer to access a contract-owned UnitTest data area.
- `Event-stream model contract`: the general UnitTest event-stream handover and payload contract defined in `event-stream-model-contract.md`.

## Version

- Contract version: `1.0.0`

## Requirements

- `DRC-001` The PLCSIM Advanced communication profile MUST consume the UnitTest event stream according to `event-stream-model-contract.md`; event-stream model requirements apply unless this profile explicitly defines a PLCSIM Advanced access-specific restriction.
- `DRC-002` The PLCSIM Advanced communication profile MUST access PLC UnitTest interface data through symbolic PLC paths, not through absolute byte offsets.
- `DRC-003` The PLCSIM Advanced profile MUST use the symbolic paths defined in this contract for Run Control lifecycle command requests, UnitTest event-stream consumption, and acknowledgement.
- `DRC-004` The PLCSIM Advanced client MUST read and validate `DB_UnitTestRunControl.ContractInfo` before writing Run Control lifecycle command fields or buffer command fields.
- `DRC-005` The direct-access consumer MUST read and validate `DB_UnitTestEventBuffer.ContractInfo` before interpreting event-buffer data.
- `DRC-006` The direct-access consumer MUST read `DB_UnitTestEventBuffer.FifoControl`, `DB_UnitTestEventBuffer.Status`, and `DB_UnitTestEventBuffer.Buffer` to consume the UnitTest event stream through this profile.
- `DRC-007` The direct-access consumer MUST request event-buffer release only by writing `DB_UnitTestRunControl.ControlData.BufferCmd.DeleteSequenceId`.
- `DRC-008` The PLCSIM Advanced client MUST request UnitTest Run Control lifecycle triggers only by writing `DB_UnitTestRunControl.ControlData.Cmd.Start`, `DB_UnitTestRunControl.ControlData.Cmd.Abort`, and `DB_UnitTestRunControl.ControlData.Cmd.Reset`.
- `DRC-009` The PLCSIM Advanced client MAY read `DB_UnitTestRunControl.ControlData.Status.Execution` for Run Control command handshake and readiness evaluation.
- `DRC-010` The PLCSIM Advanced client MUST NOT write `DB_UnitTestRunControl.ContractInfo`.
- `DRC-011` The direct-access consumer MUST NOT write `DB_UnitTestEventBuffer.ContractInfo`, `DB_UnitTestEventBuffer.FifoControl`, `DB_UnitTestEventBuffer.Status`, or `DB_UnitTestEventBuffer.Buffer`.
- `DRC-012` The PLCSIM Advanced profile MUST NOT write other Run Control, Test Suite, Test Case, Preparation, Assertion, configuration, or status data areas unless this contract explicitly adds them later.
- `DRC-013` Status DB snapshots are not UnitTest event-log, lifecycle-completion, result-evidence, or aggregation-evidence sources in this profile.
- `DRC-014` The PLCSIM Advanced communication profile MUST preserve the single logical consumer model defined by `event-stream-model-contract.md`.
- `DRC-015` The direct-access consumer MUST treat direct access as non-atomic across the full set of symbolic paths.
- `DRC-016` The direct-access consumer SHOULD read highly dynamic event-buffer data during a defined PLC cycle gap when this can be guaranteed by the application.
- `DRC-017` If a defined PLC cycle gap is not guaranteed, the direct-access consumer MUST tolerate symbolic reads that observe the event buffer while the PLC cycle is updating it.
- `DRC-018` After PLC startup, download, or restart, the PLC-side UnitTest application MUST establish a reset-defined Run Control base state before normal Run Control start processing is expected.
- `DRC-019` `Reset` is an exposed operational request, but the PLC-side UnitTest application remains responsible for establishing a valid startup state.
- `DRC-020` The PLCSIM Advanced client MUST issue Run Control command requests as positive-edge requests and MUST NOT continuously rewrite command bits while a previous request is pending.
- `DRC-021` The PLCSIM Advanced client MUST wait until the affected command bit has returned to `FALSE` before issuing another command request through the same command field.
- `DRC-022` `DB_UnitTestRunControl.ControlData.Status.Execution.Ready = TRUE` defines the exposed normal start gate.
- `DRC-023` After a `Reset` request, `DB_UnitTestRunControl.ControlData.Status.Execution.Ready = TRUE` defines that the reset-defined startable base state is exposed again.
- `DRC-024` After requesting `Start`, UnitTest lifecycle completion, result evidence, and aggregation evidence are exposed through the event stream according to `event-stream-model-contract.md`; Run Control status is only a lifecycle handshake and diagnostic projection.
- `DRC-025` The PLCSIM Advanced profile MUST interpret Run Control direct-access field structures according to this contract. Event-buffer field structures and FIFO semantics are defined by `event-stream-model-contract.md`.
- `DRC-026` The direct-access consumer MUST request an event-buffer reset only by writing `DB_UnitTestRunControl.ControlData.BufferCmd.Reset`.
- `DRC-027` The direct-access consumer MUST treat `DB_UnitTestRunControl.ControlData.BufferCmd.Reset = FALSE` as confirmation that no event-buffer reset request is pending.

## Requirement Details

### Communication Model

This profile maps the PLC-owned UnitTest interface to PLCSIM Advanced symbolic reads and writes. It does not define a new UnitTest event model. Event meaning, event-buffer FIFO semantics, sequence ids, acknowledgements, lifecycle completion evidence, result evidence, and aggregation evidence are defined by `event-stream-model-contract.md`.

The profile exposes two PLC data blocks:

- `DB_UnitTestRunControl` is the direct-access control interface. It publishes the direct-access contract metadata, accepts Run Control lifecycle command requests, exposes the Run Control command-handshake projection, and accepts event-buffer release and reset requests.
- `DB_UnitTestEventBuffer` is the event-stream publication area. It publishes event-stream contract metadata, FIFO control state, event-buffer status, and event entries. Its field model and FIFO semantics are defined by `event-stream-model-contract.md`.

The two `ContractInfo` publication points have different functions:

- `DB_UnitTestRunControl.ContractInfo` validates this PLCSIM Advanced direct-access interface before lifecycle commands or buffer command fields are written.
- `DB_UnitTestEventBuffer.ContractInfo` validates the event-stream model before event-buffer data is interpreted.

### Direct-Access Data Areas

Only the data areas and fields listed in this section are part of the PLCSIM Advanced direct-access contract. Other PLC data may exist for PLC-side orchestration, diagnostics, engineering, HMI, or implementation details, but it is outside this profile unless this contract explicitly adds it later.

#### `DB_UnitTestRunControl`

`DB_UnitTestRunControl` contains direct-access metadata, command request fields, the event-buffer request fields, and the Run Control status projection used for command handshake and diagnostics.

| Symbolic path | Access | Owner | Direct-access interpretation |
| --- | --- | --- | --- |
| `DB_UnitTestRunControl.ContractInfo` | Read | PLC producer | Direct-access contract metadata. Validate before writing Run Control lifecycle commands or buffer command fields. |
| `DB_UnitTestRunControl.ControlData.Cmd.Start` | Read/Write | Client request, PLC producer processing/reset | Run Control start command. The client writes a positive command edge to request a UnitTest run start. |
| `DB_UnitTestRunControl.ControlData.Cmd.Abort` | Read/Write | Client request, PLC producer processing/reset | Run Control abort command. The client writes a positive command edge to request controlled abort handling. |
| `DB_UnitTestRunControl.ControlData.Cmd.Reset` | Read/Write | Client request, PLC producer processing/reset | Run Control reset command. The client writes a positive command edge to request reset to the defined startable base state. |
| `DB_UnitTestRunControl.ControlData.BufferCmd.Reset` | Read/Write | Consumer request, PLC producer processing/reset | Symbolic path for the destructive event-buffer reset request. Semantics are defined by `event-stream-model-contract.md`. |
| `DB_UnitTestRunControl.ControlData.BufferCmd.DeleteSequenceId` | Read/Write | Consumer request, PLC producer processing/reset | Symbolic path for the event-stream delete request field. Semantics are defined by `event-stream-model-contract.md`. |
| `DB_UnitTestRunControl.ControlData.Status.Execution` | Read | PLC producer | Run Control lifecycle handshake projection. Exposes readiness, command progress, and diagnostics. Not UnitTest event-log, lifecycle-completion, result, or aggregation evidence. |

`DB_UnitTestRunControl` contract-facing structure:

| Symbolic field | Type | Access | Idle/default value | Direct-access interpretation |
| --- | --- | --- | --- | --- |
| `ContractInfo.ContractVersion` | `String[10]` | Read | Producer-defined | PLCSIM Advanced direct-access contract version. Validate before writing command fields. |
| `ContractInfo.FrameworkVersion` | `String[10]` | Read | Producer-defined | PLC-side UnitTest framework implementation version. Used for compatibility and diagnostics. |
| `ControlData.Name` | `String[80]` | Read | Producer-defined | Public Run Control name for diagnostics and event correlation. Not a command or result source. |
| `ControlData.Cmd.Start` | `Bool` | Read/Write | `FALSE` | Positive-edge start request. Client writes `TRUE` once; PLC producer processes and resets it. |
| `ControlData.Cmd.Abort` | `Bool` | Read/Write | `FALSE` | Positive-edge abort request. Client writes `TRUE` once; PLC producer processes and resets it. |
| `ControlData.Cmd.Reset` | `Bool` | Read/Write | `FALSE` | Positive-edge reset request. Client writes `TRUE` once; PLC producer processes and resets it. |
| `ControlData.BufferCmd.Reset` | `Bool` | Read/Write | `FALSE` | Event-buffer reset request exposed by this direct-access profile. `FALSE` confirms that no reset request is pending. Semantics are defined by `event-stream-model-contract.md`. |
| `ControlData.BufferCmd.DeleteSequenceId` | `UDInt` | Read/Write | `0` | Event-stream delete request field exposed by this direct-access profile. Semantics are defined by `event-stream-model-contract.md`. |
| `ControlData.Status.Execution` | Structure | Read | See `ControlData.Status.Execution` fields below | Run Control lifecycle handshake projection. |

`ControlData.Status.Execution` fields:

| Field | Type | Direct-access interpretation |
| --- | --- | --- |
| `State` | `Int` | Current Run Control lifecycle state value. Symbolic constants are defined by `event-stream-model-contract.md`. Used for diagnostics and plausibility, not as final completion source. |
| `ExecutionStatus` | `Int` | Coarse execution classification derived by the PLC producer. Symbolic constants are defined in `ExecutionStatus` constants below. Used for diagnostics and command-handshake plausibility. |
| `Result` | `Int` | Current producer-side run result projection. Not normative for final UnitTest result evaluation in this PLCSIM Advanced profile. |
| `Uninitialized` | `Bool` | `TRUE` means Run Control has not reached a reset-defined executable base state. Normal startability is not exposed while this is true. |
| `ConfigError` | `Bool` | `TRUE` means Run Control configuration prevents a valid run. |
| `Ready` | `Bool` | `TRUE` means Run Control is in the startable base state and subordinate startability conditions are fulfilled. Preferred readiness gate for `Start`. |
| `Running` | `Bool` | `TRUE` means Run Control is in active execution. Used for progress monitoring only. |
| `Completed` | `Bool` | `TRUE` means Run Control has reached its stable completed state. Not a replacement for the Run Control completion event in the event stream. |
| `Aborted` | `Bool` | `TRUE` means the current completed projection corresponds to an explicit abort outcome. Not a replacement for final event-stream result evaluation. |
| `Inactive` | `Bool` | `TRUE` means Run Control is intentionally inactive. Normal startability is not exposed while this is true. |
| `Error` | `Bool` | `TRUE` means Run Control reports an error condition in its current status projection. |

`ExecutionStatus` constants:

| Symbolic constant | Value | Direct-access interpretation |
| --- | ---: | --- |
| `UnitTest.ExecutionStatus.Error` | `-10` | Error classification. Run Control is not startable. |
| `UnitTest.ExecutionStatus.Unknown` | `0` | Unknown or not yet overwritten classification. Treat as not startable. |
| `UnitTest.ExecutionStatus.Inactive` | `1` | Intentionally inactive. Treat as not startable. |
| `UnitTest.ExecutionStatus.NotReady` | `9` | Defined not-ready state, for example before reset-defined base state is reached. Treat as not startable. |
| `UnitTest.ExecutionStatus.Idle` | `10` | Idle classification. Use `Ready = TRUE` as the actual start gate. |
| `UnitTest.ExecutionStatus.Starting` | `20` | Start transition in progress. |
| `UnitTest.ExecutionStatus.Running` | `30` | Active run execution. |
| `UnitTest.ExecutionStatus.Completing` | `40` | Run finalization in progress. |
| `UnitTest.ExecutionStatus.Completed` | `50` | Stable completed classification. Not a replacement for event-log completeness evaluation. |
| `UnitTest.ExecutionStatus.Aborting` | `60` | Controlled abort handling in progress. |
| `UnitTest.ExecutionStatus.Aborted` | `70` | Legacy aborted classification. New Run Control completion should use `Completed` plus result projection. |
| `UnitTest.ExecutionStatus.Reset` | `80` | Reset handling in progress. |

#### `DB_UnitTestEventBuffer`

`DB_UnitTestEventBuffer` contains the event-stream publication area. This profile only defines the symbolic PLCSIM Advanced paths used to read that area.

| Symbolic path | Access | Owner | Direct-access interpretation |
| --- | --- | --- | --- |
| `DB_UnitTestEventBuffer.ContractInfo` | Read | PLC producer | Event-stream contract metadata defined by `event-stream-model-contract.md`. The direct-access consumer reads this before interpreting event-buffer data. |
| `DB_UnitTestEventBuffer.FifoControl` | Read | PLC producer | Event-stream FIFO control state defined by `event-stream-model-contract.md`. |
| `DB_UnitTestEventBuffer.Status` | Read | PLC producer | Event-stream buffer status defined by `event-stream-model-contract.md`. |
| `DB_UnitTestEventBuffer.Buffer` | Read | PLC producer | Event-stream entry array defined by `event-stream-model-contract.md`. |

### Run Control Command Interface

Run Control command fields are positive-edge request fields. The PLCSIM Advanced client writes `TRUE` to request an action and then stops writing that command field. The PLC producer processes the request and resets the command field.

Command fields are pending while they remain `TRUE`. A new command request through the same command field is valid only after that field has returned to `FALSE`.

`DB_UnitTestRunControl.ControlData.Status.Execution` is exposed as a read-only lifecycle handshake projection. It communicates whether Run Control is startable, active, completed, not ready, or in an error/configuration condition from the command-interface perspective.

`Ready = TRUE` defines the exposed normal start gate. The start request itself is issued through the `Start` command bit.

The normal start handshake is:

1. Validate `DB_UnitTestRunControl.ContractInfo`.
2. Wait until no Run Control command request is pending.
3. Wait until `DB_UnitTestRunControl.ControlData.Status.Execution.Ready = TRUE`.
4. Write `DB_UnitTestRunControl.ControlData.Cmd.Start := TRUE` once.
5. Wait until `DB_UnitTestRunControl.ControlData.Cmd.Start = FALSE` before issuing another start request.
6. Consume the UnitTest event stream according to `event-stream-model-contract.md`.

Reset is an operational command to return Run Control and subordinate UnitTest levels to the defined base state. After reset processing, `Ready = TRUE` is the exposed startability signal. If `Ready = TRUE` is not reached, the direct-access interface exposes a PLC-side readiness or initialization problem.

Abort is an operational command to request controlled termination of active execution. After requesting abort, lifecycle completion, result evidence, and aggregation evidence are still exposed through the event stream. The Run Control status projection may be used to monitor command progress, but it does not replace required event-stream entries.

### Startup And Initial Reset

The PLC-side UnitTest application owns its startup initialization. After PLC startup, download, or restart, it must establish a reset-defined Run Control base state before normal Run Control start processing is expected.

This may be implemented by an internal first-scan reset or an equivalent PLC-side initialization path. The important contract behavior is that startup validity is owned by the PLC-side UnitTest application, not by an external initial reset requirement.

The PLCSIM Advanced client may still issue `DB_UnitTestRunControl.ControlData.Cmd.Reset` as an explicit operational command. That reset is a normal request to return Run Control and subordinate UnitTest levels to the defined base state; it is not a mandatory precondition for every CI-triggered execution.

If the PLC-side UnitTest application cannot establish the reset-defined base state, it must not present normal startable behavior. This condition is exposed as a PLC-side readiness or initialization problem and is not repaired through undocumented external writes.

### Event-Stream Access Mapping

The event-stream model contract defines the normative FIFO handover, sequence-id, acknowledgement, and lost-event semantics. This PLCSIM Advanced profile does not redefine those rules.

For PLCSIM Advanced direct access, the event-stream model is exposed through these symbolic PLC paths:

- `DB_UnitTestEventBuffer.FifoControl` maps to the producer-owned FIFO control state from `event-stream-model-contract.md`.
- `DB_UnitTestEventBuffer.Status` maps to the producer-owned event-buffer status from `event-stream-model-contract.md`.
- `DB_UnitTestEventBuffer.Buffer` maps to the producer-owned event-entry storage from `event-stream-model-contract.md`.
- `DB_UnitTestRunControl.ControlData.BufferCmd.Reset` maps to the producer-owned event-buffer reset request field from `event-stream-model-contract.md`.
- `DB_UnitTestRunControl.ControlData.BufferCmd.DeleteSequenceId` maps to the producer-owned delete request field from `event-stream-model-contract.md`.

The consumer interprets these fields according to the event-stream handover requirements in `event-stream-model-contract.md`. This profile does not redefine FIFO range derivation, sequence-id acknowledgement, lost-event handling, or event-buffer ownership.

This profile adds only the direct-access boundary: the event-buffer fields are read-only symbolic PLC data for the consumer. The consumer writes the Run Control buffer-command fields to request release of processed entries or a destructive event-buffer reset; the producer performs the requested FIFO operation.

### Direct-Access Read Consistency

Direct-access reads across `FifoControl`, `Status`, and `Buffer` are not defined as one atomic snapshot.

PLCSIM Advanced symbolic reads can observe PLC data while the PLC cycle is updating highly dynamic event-buffer data. A direct-access consumer should therefore read event-buffer data during a defined PLC cycle gap where the application can guarantee such a gap.

If the consumer cannot guarantee cycle-gap access, it must treat the read data as a non-atomic snapshot. In that case, the most recently written or currently published event entry can be inconsistent across fields or relative to `FifoControl` and `Status` during the read.

The direct-access consumer uses the producer/consumer rules from `event-stream-model-contract.md` to handle this:

- derive readable entries from `FifoControl`,
- process only entries inside the readable FIFO range,
- process only entries with `SequenceId > 0`,
- use `SequenceId`, `DeletedSequenceId`, and `LostEventCount` for plausibility and acknowledgement handling,
- retry or defer processing when the currently observed data is not plausible,
- treat event-buffer reset or clear as a new event-buffer epoch.

### Access Boundary

The PLC producer owns the UnitTest data model. Reading PLC data through this profile does not transfer ownership of that data.

The only externally writable fields in this profile are request fields:

- `DB_UnitTestRunControl.ControlData.Cmd.Start`,
- `DB_UnitTestRunControl.ControlData.Cmd.Abort`,
- `DB_UnitTestRunControl.ControlData.Cmd.Reset`,
- `DB_UnitTestRunControl.ControlData.BufferCmd.Reset`,
- `DB_UnitTestRunControl.ControlData.BufferCmd.DeleteSequenceId`.

The PLCSIM Advanced client writes Run Control command fields to request lifecycle actions. The direct-access consumer writes `BufferCmd.DeleteSequenceId` to request event-buffer release and `BufferCmd.Reset` to discard the current event-buffer epoch. These are distinct from `Cmd.Reset`, which resets the UnitTest lifecycle. The PLC producer processes and acknowledges all request fields according to their contract semantics.

All event-buffer fields are producer-owned and read-only from the direct-access consumer perspective. The consumer never modifies event-buffer entries, FIFO control, event-buffer status, or event-buffer contract metadata directly.

Run Control `Status.Execution.Result`, `Status.Execution.Completed`, or any other status field is not the normative UnitTest event-log or result-evidence source for the PLCSIM Advanced profile. Missing lifecycle completion events are lifecycle event-log completeness errors only after event streaming has finished or timed out according to `event-stream-model-contract.md`. Missing Result or Aggregation events leave the corresponding result or aggregation evidence incomplete.

Status DBs and status projections are diagnostic views for engineering, monitoring, or HMI/OPC use cases. They are not direct-access UnitTest event-log, lifecycle-completion, result-evidence, or aggregation-evidence sources.

### Symbolic Access

Direct access uses symbolic PLCSIM Advanced reads and writes. The symbolic paths listed in this contract are contract-facing identifiers.

Absolute addresses, byte offsets, compiler layout assumptions, and inferred DB layouts are not part of the PLCSIM Advanced communication contract. If the PLC data model changes, the contract must be updated explicitly instead of relying on layout-compatible guesses.

All other PLC data areas are outside the PLCSIM Advanced direct-access contract unless this contract explicitly adds them.

## Compatibility

This contract is specific to the PLCSIM Advanced communication profile.

Future communication profiles may use different transport mechanisms, but they must preserve the event-stream semantics defined by `event-stream-model-contract.md`.

This contract must not be used as implicit specification for MQTT, OPC UA, PLC-internal, REST, socket, file-based, or other future access profiles. Those profiles require their own access-profile contracts that map the common event-stream model to their concrete transport and access boundaries.
