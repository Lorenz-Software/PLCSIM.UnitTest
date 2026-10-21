# General Framework Contract

## Purpose

This contract defines cross-cutting PLC-side framework rules that are shared by Run Control, Test Suite, Test Case, Preparation, Assertion, and Communication contracts.

## Scope

This contract defines:

- shared execution/result data types,
- shared numeric and time field types relevant to execution/result structures,
- shared constant definitions,
- shared execution and result semantics,
- shared default rules.

This contract does not define owner-specific control/config/status structures or event-entry layout.

## Terms

- `Owner`: a framework element with PLC-side execution responsibility.
- `Full Owner`: Run Control, Test Suite, or Test Case.
- `ExecutionStatus`: the derived coarse execution classification of an owner.
- `State`: the actual owner state-machine value.
- `Result`: the current stable evaluation standing of the current run.
- `Lifecycle event`: an event that describes owner lifecycle progress, especially state transitions.
- `Final owner event`: an owner-level Result or Aggregation event emitted as part of controlled owner finalization.

## Version

- Contract version: `1.0.0`

## Requirements

- `GFC-001` The shared execution status structure MUST be `ST_ExecutionStatus`.
- `GFC-002` `ST_ExecutionStatus` MUST contain the fields defined in Table A.
- `GFC-003` Numeric field types for execution/result structures MUST follow Table B.
- `GFC-004` `Runtime` type MUST follow Table C.
- `GFC-005` Control/status data fields MUST start from defined safe defaults before owner-specific values overwrite them. Explicit assignments or TwinCAT-guaranteed per-call initialization of a method-local VAR snapshot with the specified datatype defaults satisfy this requirement. Persistent instance storage, including VAR_INST, MUST NOT rely on declaration initialization to restore defaults on subsequent calls.
- `GFC-006` Symbolic execution-status constants MUST follow Table D.
- `GFC-007` Symbolic result constants MUST follow Table E.
- `GFC-008` `ExecutionStatus` MUST be driven by `State` and MUST NOT be treated as an independent second state machine.
- `GFC-009` All transitions that end in `E_ExecutionStatus.Idle` MUST pass through `E_ExecutionStatus.Reset`.
- `GFC-010` The default initialization path MUST be `Uninitialized -> Reset -> Idle`, unless the owner is intentionally inactive, in which case `Uninitialized -> Reset -> Inactive` MUST be used.
- `GFC-011` `Result` MUST use `E_Result.NotEvaluated` as the default and reset value.
- `GFC-012` `Result` MUST represent the current stable evaluation standing of the current run and MUST NOT move back to a less severe value within the same run.
- `GFC-013` `E_ExecutionStatus.Error` MUST imply `E_Result.Error` in the same run.
- `GFC-014` `E_Result.Error` MUST NOT require `E_ExecutionStatus.Error`.
- `GFC-015` `E_State.Uninitialized` MUST represent a defined state in which the owner has not yet reached a reset-defined executable base state.
- `GFC-016` `Ready = TRUE` MUST indicate an executable base state with all required owner-local and subordinate startability conditions fulfilled.
- `GFC-017` For each status refresh, `ExecutionStatus` MUST initially be `E_ExecutionStatus.Unknown` and MUST then be actively overwritten by the owner logic. Guaranteed per-call initialization of a method-local snapshot according to GFC-005 satisfies this initial preset; a finalized held snapshot need not be refreshed.
- `GFC-018` Public contract symbols intended for OPC UA publication MUST use TwinCAT OPC UA data-access attributes and MUST set `OPC.UA.DA.Access = 3` only for contract-defined writable command fields and `OPC.UA.DA.Access = 1` for producer-owned read-only fields.
- `GFC-019` The hierarchical nature of PLC structures MUST be respected for OPC UA access attributes. Higher structural levels MAY further restrict lower-level accessibility or writability, but MUST NOT expand permissions beyond what is defined by nested fields or nested structures.
- `GFC-020` The top-level storage instance definition MUST be treated as the highest authority for external-access restriction. It MAY further restrict nested structures and fields, but MUST NOT relax restrictions defined below it. PLC symbols that are not part of the public contract SHOULD use `TcNoSymbol`.
- `GFC-021` Symbolic full-owner lifecycle state constants MUST follow Table F.
- `GFC-022` `E_State.Completed` MUST be the only stable final lifecycle state used by new owner logic.
- `GFC-023` Explicit abort completion MUST be communicated by `E_Result.Abort` while `ExecutionStatus` is `E_ExecutionStatus.Completed`.
- `GFC-024` Error completion MUST be communicated by `E_Result.Error` while `ExecutionStatus` may remain `E_ExecutionStatus.Completed` when the owner reached a controlled final state.
- `GFC-025` `E_State.AbortingFromError` MUST communicate controlled error termination and MUST NOT be mapped to `E_Result.Abort`.
- `GFC-026` An owner that starts subordinate owners MUST verify subordinate `Ready` immediately before each concrete subordinate start dispatch.
- `GFC-027` Cyclic processing in `E_State.Uninitialized` MUST be passive: it MUST NOT emit lifecycle, result, aggregation, or framework diagnostic events and MUST only leave that state through explicit reset handling. Explicit Initialize calls MAY emit framework diagnostics for rejected identity or configuration while the owner remains Uninitialized. Such diagnostics MUST NOT enable execution or replace lifecycle finalization events.
- `GFC-028` An unhandled lifecycle state value MUST be treated as a framework error, MUST produce `E_Result.Error`, and MUST enter the controlled finalization path through `E_State.Completing` before reaching `E_State.Completed`.
- `GFC-029` Full-owner finalization MUST use `E_State.Completing` for final result and aggregation publication before the owner enters `E_State.Completed`.
- `GFC-030` The state-transition event to `E_State.Completed` MUST be the final lifecycle event of the affected owner run and MUST NOT be emitted in the same PLC cycle as the owner's final result or aggregation event.
- `GFC-031` A generic Orchestrator `E_Result.Abort` MUST only confirm an owner abort path that has already been selected by the owning Full Owner; otherwise the owning Full Owner MUST treat it as a framework error and finalize with `E_Result.Error`.
- `GFC-032` `OwnerScope` in UnitTest events MUST describe the semantic framework level represented by the event, not necessarily the technical block that emitted the event.
- `GFC-033` For each controllable Full Owner lifecycle run from `E_State.Idle` to `E_State.Completed`, exactly one final owner Result event MUST be emitted.
- `GFC-034` For each controllable aggregating Full Owner lifecycle run from `E_State.Idle` to `E_State.Completed`, exactly one final owner Aggregation event MUST be emitted.
- `GFC-035` Final owner Result and Aggregation events MUST be emitted while the owner is in `E_State.Completing`.
- `GFC-036` The final `StateTransition` event into `E_State.Completed` MUST be emitted after the final owner Result and Aggregation events of the same lifecycle run.
- `GFC-037` Diagnostic and framework events MAY explain exceptional conditions, but they MUST NOT replace required final owner Result or Aggregation events.
- `GFC-038` Trigger events MUST describe command or trigger edges and MUST NOT be used as lifecycle completion or result evidence.
- `GFC-039` State-transition events MUST describe lifecycle state changes and MUST NOT carry aggregation or result semantics beyond the state values themselves.
- `GFC-040` PLC-side startup or restart initialization MUST be owned by the PLC-side UnitTest application and MUST NOT depend on an external consumer issuing the initial reset only to establish a valid base state.
- `GFC-041` Within owner finalization, the final owner Result event MUST be emitted before the final owner Aggregation event.
- `GFC-042` Generic Orchestrators MAY emit technical orchestration state-transition diagnostics with dedicated Orchestrator `OwnerScope` values, but these diagnostics MUST NOT be treated as independent Full Owner lifecycle completion, Result, or Aggregation evidence.
- `GFC-043` Framework startup and reset behavior MUST explicitly account for TwinCAT declaration initialization, `FB_Init`, `TcInitOnReset`, normal storage, `RETAIN`, `PERSISTENT`, reset variants, download, restart, and Online Change.
- `GFC-044` Transient framework execution, command, reservation, and communication state MUST NOT become remanent unless a dedicated contract requirement explicitly requires it.
- `GFC-045` Runtime initialization, UnitTest command reset, and Event Stream buffer reset MUST remain distinct operations with separately defined effects.

## Requirement Details

### Table A: `ST_ExecutionStatus`

| Field | Type | Default |
| --- | --- | --- |
| `State` | `E_State` (`INT` base) | `E_State.Uninitialized` |
| `ExecutionStatus` | `E_ExecutionStatus` (`INT` base) | `E_ExecutionStatus.Unknown` |
| `Result` | `E_Result` (`INT` base) | `E_Result.NotEvaluated` |
| `Uninitialized` | `BOOL` | `FALSE` |
| `ConfigError` | `BOOL` | `FALSE` |
| `Ready` | `BOOL` | `FALSE` |
| `Running` | `BOOL` | `FALSE` |
| `Completed` | `BOOL` | `FALSE` |
| `Aborted` | `BOOL` | `FALSE` |
| `Inactive` | `BOOL` | `FALSE` |
| `Error` | `BOOL` | `FALSE` |

### Table B: Numeric field types for execution/result structures

| Field | Type |
| --- | --- |
| `State` | `E_State` (`INT` base) |
| `ExecutionStatus` | `E_ExecutionStatus` (`INT` base) |
| `Result` | `E_Result` (`INT` base) |

### Table C: Time field types

| Field | Type | Semantics |
| --- | --- | --- |
| `Runtime` | `TIME` | Relative owner runtime projected to milliseconds, default `T#0ms`; snapshot-based timeout helpers compare this TIME value directly with the configured TIME timeout without hidden execution-status-bit gating. Owner-internal monitoring uses native-resolution elapsed time. |

Configured timeouts and delays and status `Runtime` fields MUST use `TIME`. This includes `Timeout`, `PreparationTimeout`, `ResetTimeout`, `AbortTimeout`, and `CompletionDelay`. Internal measurement MUST retain the selected time source's native resolution; elapsed duration is calculated from timestamps rather than accumulated rounded millisecond increments. Timeout comparisons MUST preserve the selected time source's resolution and use a sufficiently wide, overflow-safe representation. One-time conversion of immutable timeout thresholds SHOULD be preferred when it materially reduces cyclic execution cost. The implementation MUST explicitly weigh that performance benefit against duplicated configuration storage, synchronization responsibility and readability. Per-comparison conversion is permitted when its real-time cost is acceptable and avoids unnecessary duplicate configuration state. Status projection MUST convert the complete elapsed duration to TIME by discarding fractional milliseconds and saturating at the TIME maximum. Aggregation events publish numeric milliseconds without narrowing through DINT. Event timestamps remain UTC Unix nanoseconds in LINT and are distinct from elapsed durations.

Owner runtime is the active execution time published by the owner status view. Additional helper-local runtime values may exist for narrower phases, for example `StateRuntime`, `PreparationRuntime`, or `TestStateRuntime`. Such helper-local values are diagnostics or internal monitoring inputs unless an owner-specific contract explicitly publishes them.

### Table D: Execution-status constants

| Symbolic constant | Value |
| --- | ---: |
| `E_ExecutionStatus.Error` | `-10` |
| `E_ExecutionStatus.Unknown` | `0` |
| `E_ExecutionStatus.Inactive` | `1` |
| `E_ExecutionStatus.NotReady` | `9` |
| `E_ExecutionStatus.Idle` | `10` |
| `E_ExecutionStatus.Starting` | `20` |
| `E_ExecutionStatus.Running` | `30` |
| `E_ExecutionStatus.Completing` | `40` |
| `E_ExecutionStatus.Completed` | `50` |
| `E_ExecutionStatus.Aborting` | `60` |
| `E_ExecutionStatus.Aborted` | `70` |
| `E_ExecutionStatus.Reset` | `80` |

`E_ExecutionStatus.Aborted` is retained as a legacy constant. New owner logic MUST NOT use it as a stable final execution status.

### Table E: Result constants

| Symbolic constant | Value |
| --- | ---: |
| `E_Result.Error` | `-10` |
| `E_Result.NotEvaluated` | `0` |
| `E_Result.Passed` | `10` |
| `E_Result.Failed` | `20` |
| `E_Result.Abort` | `30` |

### Table F: Full-owner lifecycle state constants

| Symbolic constant | Value |
| --- | ---: |
| `E_State.Error` | `-10` |
| `E_State.Uninitialized` | `0` |
| `E_State.Reset` | `1` |
| `E_State.Idle` | `10` |
| `E_State.Starting` | `11` |
| `E_State.Preparing` | `20` |
| `E_State.Running` | `50` |
| `E_State.Completing` | `80` |
| `E_State.Completed` | `89` |
| `E_State.Aborting` | `90` |
| `E_State.AbortingFromError` | `91` |
| `E_State.Aborted` | `99` |

`E_State.Error` and `E_State.Aborted` are retained as legacy constants. New owner logic MUST NOT use either value as a stable final lifecycle state.

### Execution-status semantics

```text
default initialization:
E_ExecutionStatus.NotReady
 -> E_ExecutionStatus.Reset
 -> E_ExecutionStatus.Idle

inactive initialization:
E_ExecutionStatus.NotReady
 -> E_ExecutionStatus.Reset
 -> E_ExecutionStatus.Inactive

normal forward execution:
E_ExecutionStatus.Idle
 -> E_ExecutionStatus.Starting
 -> E_ExecutionStatus.Running
 -> E_ExecutionStatus.Completing
 -> E_ExecutionStatus.Completed

abort path:
... -> E_ExecutionStatus.Aborting -> E_ExecutionStatus.Completed

return to idle:
Completed/Error/Inactive
 -> E_ExecutionStatus.Reset
 -> E_ExecutionStatus.Idle
```

Owner-specific state machines MAY have additional internal state steps, but they MUST map their current execution situation to this shared `ExecutionStatus` model.

`E_State.Uninitialized` maps to `E_ExecutionStatus.NotReady`, sets `Uninitialized = TRUE`, and keeps `Ready = FALSE`, `Running = FALSE`, `Completed = FALSE`, and `Error = FALSE`. It is a defined non-executable state, not an error state. It is used when an owner has not yet been brought into a reset-defined base state.

PLC-side startup or restart initialization must bring the relevant Full Owner path from `E_State.Uninitialized` through explicit reset handling into the defined executable base state before normal start processing is expected. An external consumer may request reset as an operational command, but the PLC-side UnitTest application must not require that external request only to make the startup state valid.

An unhandled state value is not `Uninitialized`. It represents a framework or programming error such as an incomplete state-machine branch, invalid state assignment, or manual overwrite of lifecycle state. The owner must report this as an error and complete through the controlled finalization path so higher levels can aggregate the error deterministically.

### Result semantics

```text
default/reset:
E_Result.NotEvaluated

stable progression within one run:
E_Result.NotEvaluated
 -> E_Result.Passed
 -> E_Result.Failed
 -> E_Result.Error

allowed direct jumps:
E_Result.NotEvaluated -> E_Result.Failed
E_Result.NotEvaluated -> E_Result.Error
E_Result.NotEvaluated -> E_Result.Abort
E_Result.Passed -> E_Result.Abort
E_Result.Failed -> E_Result.Abort
E_Result.Passed -> E_Result.Error

forbidden:
E_Result.Failed -> E_Result.Passed
E_Result.Error -> E_Result.Failed
E_Result.Error -> E_Result.Passed
E_Result.Abort -> E_Result.Passed
E_Result.Abort -> E_Result.Failed
E_Result.* -> E_Result.NotEvaluated (except by reset)
```

`E_Result.Abort` is a separate terminal outcome for explicitly aborted runs. It is not part of the passed/failed/error evaluation-severity order. `E_Result.Error` remains the highest-priority outcome if abort handling itself fails.

`E_ExecutionStatus.Completing` and `E_ExecutionStatus.Aborting` are not phases for new test checks or Assertion evaluations. In `Completing`, the owner MAY derive its final `Passed` or `Failed` result from already established evaluation results according to its lifecycle rules; this finalization is not a new test evaluation. `Result` MAY still escalate to `E_Result.Error` or be set to `E_Result.Abort` for an explicit abort path.

`E_State.Aborting` represents explicit abort handling and normally leads to `E_Result.Abort`.

`E_State.AbortingFromError` represents controlled termination after an internal, subordinate, timeout, or framework error and leads to `E_Result.Error`. It is not an explicit user abort outcome.

Where a generic Orchestrator exposes lifecycle results, they are interpreted in the context of the owning Full Owner. `E_Result.Error` from a generic helper always escalates to the owner result. `E_Result.Abort` from a generic helper is only valid when the owner has already selected its explicit abort path. If no owner abort path exists, a generic helper abort result represents an inconsistent lifecycle situation and must be treated as `E_Result.Error`. Subordinate owner results are not blindly combined as abort outcomes; an unexpected subordinate abort must be routed into the owner's error finalization path before the owner completes.

`Ready = TRUE` indicates an executable base state. For full owners, `Ready = TRUE` requires that the owner itself is in its idle base state, all required owner-local ready releases are true, and all subordinate owners required for the next start are ready as well. For Preparation, this additionally implies that no action is currently performed on the tested resource.

If a subordinate owner is no longer ready immediately before a concrete start dispatch, the starting owner MUST treat this as a framework error, avoid starting that subordinate, and enter its controlled error termination path instead of waiting for a regular timeout.

### Producer-side event semantics

UnitTest events are producer-side semantic records. They document the framework lifecycle, final outcomes, aggregation, trigger edges, and diagnostic context. They are not a replacement for owner state machines and do not define transport or consumer polling behavior.

`OwnerScope` describes the semantic context represented by an event. Full Owner lifecycle, Result, and Aggregation evidence uses the Full Owner scope (`RunControl`, `TestSuite`, or `TestCase`) even when a generic helper technically emits the event on behalf of that owner.

Generic Orchestrators may additionally emit technical orchestration state-transition diagnostics with dedicated Orchestrator scopes. These events describe the helper state machine that coordinates subordinate owners. They do not introduce additional reportable Full Owner levels and must not be interpreted as owner finalization, final Result, or Aggregation evidence.

`StateTransition` events document lifecycle state changes. The state-transition event into `E_State.Completed` is the deterministic lifecycle-completion event of a Full Owner run. It must be emitted only after that owner's final Result and Aggregation events have already been produced.

`Result` values are escalated by the owning PLC-side result logic before a final owner Result event is emitted. Result events do not perform escalation themselves.

Final Full Owner Result events document the final owner outcome after owner-side result escalation has completed. They are emitted during `E_State.Completing`.

Assertion failed/error Result events are assertion-owned finding events. They can contribute to Test Case result aggregation and escalation, but they are not final Test Case Result events.

Assertion finding publication MUST follow result escalation. `Fail()` changes `NotEvaluated` or `Passed` to `Failed` and emits the Failed finding only on that transition. `Error()` changes a non-Error assertion result to `Error` and emits the Error finding only on that transition. Repeated calls at the same severity MUST NOT emit another event. After `Error`, `Fail()` MUST leave the result unchanged and MUST NOT emit a Failed finding. Reset permits a new assertion evaluation lifecycle.

`Aggregation` events document how subordinate outcomes contributed to an owner outcome. Run Control, Test Suite, and Test Case are aggregating Full Owners. Preparation, Assertion, and generic lifecycle helpers are not independent aggregating Full Owners.

Within a Full Owner finalization cycle, the final owner Result event is emitted first and the final owner Aggregation event is emitted afterwards. The subsequent state-transition event into `E_State.Completed` remains the final lifecycle-completion event for that owner run.

Diagnostic and framework events provide technical context such as invalid configuration, timeout, unhandled state, resource readiness violation, or event-buffer capacity problems. They do not complete an owner lifecycle and do not replace final owner Result or Aggregation events.

Explicit abort paths finalize controllably through the owner abort path and produce `E_Result.Abort`. Error, timeout, subordinate-error, and framework-error paths should finalize controllably through the owner error path and produce `E_Result.Error`. If controlled finalization cannot be reached, the required final event set may be incomplete; detecting missing events is outside this local producer lifecycle contract.

`OwnerScope` classifies the represented framework level or technical diagnostic context. The concrete parent/child identity of UnitTest owner events is defined by the hierarchical event `Name` rules in `event-stream-contract.md`.

The owner-directed Orchestrator does not own a Full Owner lifecycle. Its Result is limited to neutral or technical Error; task completion is reported separately. The conditional helper-Abort rules do not require it to produce Abort results.

## Compatibility

This contract intentionally uses PLC-owned structures and symbolic constants that can be implemented on different PLC platforms.

## Non-Requirements

This contract does not define:

- event-buffer transport semantics,
- event-entry layout, transport semantics, or communication classifications such as `OwnerScope`, `EventType`, `Severity`, or `Timestamp`,
- owner-specific orchestration rules,
- PC-side classes,
- dynamic runtime FB dispatch.
