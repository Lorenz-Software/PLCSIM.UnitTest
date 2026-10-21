# Run Control Contract

## Purpose

This contract defines the PLC-side producer contract for Run Control.

## Scope

This contract defines:

- `ST_RunControl`,
- `ST_RunControlStatus`,
- `ST_ContractInfo`,
- Run-Control-specific external projection and naming rules.

This contract references `general-framework-contract.md` for shared execution/result semantics and shared type rules.

Event-entry structure, event classifications, event-stream rules, and consumer-facing buffer projection are defined by `event-stream-contract.md` and are outside this Run Control contract.

## Version

- Contract version: `1.0.0`

## Requirements

- `RCC-001` The external `ST_RunControl` projection MUST use the top-level structure `Name`, `Cmd`, `BufferCmd`, and `Status`.
- `RCC-002` The Run Control owner MUST expose `Name` and `ST_RunControlStatus` through its object-oriented view. `Name` MUST NOT be modeled inside `ST_RunControlStatus`.
- `RCC-003` The external Run Control projection MUST follow Table A, and its `Status` field MUST use `ST_RunControlStatus` defined by Table D.
- `RCC-004` External lifecycle command fields MUST follow Table B.
- `RCC-005` External Event Buffer command fields MUST follow Table C.
- `RCC-006` Run-status fields MUST follow Table D.
- `RCC-007` Contract info fields MUST follow Table E.
- `RCC-008` A separate signal adapter MUST qualify external lifecycle command fields, translate accepted commands into the corresponding Run Control request operations, and reset accepted fields according to this contract. The adapter MUST execute before Run Control in each PLC cycle. Run Control MUST copy its `Name` and current status into the external projection after internal processing.
- `RCC-009` The Run Control integration path MUST process external Event Buffer commands according to `event-stream-contract.md`.
- `RCC-010` Timeout deactivation MUST NOT be allowed. Timeout values MUST be greater than `T#0ms`; `T#0ms` MUST be treated as invalid.
- `RCC-011` Run Control MUST own the project-specific suite binding and SHOULD use the framework-provided Run Control base for integrated generic run lifecycle handling.
- `RCC-012` Run Control Suite orchestration MUST be implemented through the shared framework Orchestrator when serial, parallel, or slot-based parallel Suite execution matches the required project behavior.
- `RCC-013` Run Control finalization MUST keep `E_State.Completing` active for at least the configured `CompletionDelay` before entering `E_State.Completed`. If the delay time measurement becomes invalid, Run Control MUST emit one Framework diagnostic and enter Completed without changing already published results; the minimum delay cannot be guaranteed in this exception.
- `RCC-014` Run Control MUST emit exactly one final Run Control Result event for each controllable run lifecycle from `Idle` to `Completed`.
- `RCC-015` Run Control MUST emit exactly one final Run Control Aggregation event for each controllable run lifecycle from `Idle` to `Completed`.
- `RCC-016` Run Control MUST emit its final Result and Aggregation events while in `E_State.Completing` and before the final state-transition event into `E_State.Completed`.
- `RCC-017` Project-specific suite binding MUST belong to the concrete Run Control implementation, and generic suite execution MUST belong to the shared Orchestrator.
- `RCC-018` Run Control reset handling MUST bound subordinate Suite reset dispatch against the available event-buffer capacity so that the expected reset events fit while the required producer headroom remains available.
- `RCC-019` The shared framework Orchestrator MUST dispatch at most one subordinate Suite Reset request per call and MUST acquire its Reset reservation before dispatch. Multiple Suite resets MAY overlap only under the hierarchical admission rule defined in test-suite-contract.md; insufficient capacity MUST delay further dispatch.
- `RCC-020` A subordinate Suite in `E_State.Idle` with `Status.Execution.Ready = TRUE` MAY be treated as reset-complete without receiving another reset command.
- `RCC-021` Run Control `ResetTimeout` monitoring MUST cover the complete reset operation, including time spent waiting for event-buffer capacity and subordinate Suite completion.
- `RCC-022` A project-specific Run Control implementation that does not use a framework Orchestrator MUST provide equivalent capacity-bounded reset dispatch and completion behavior.
- `RCC-023` The library MUST publish Run Control contract data below `GVL_UnitTest.RunControl` using the top-level fields `ContractInfo` and `ControlData` defined in Table F.
- `RCC-024` `GVL_UnitTest.RunControl.ContractInfo` MUST use `ST_ContractInfo`, and `GVL_UnitTest.RunControl.ControlData` MUST use `ST_RunControl`.
- `RCC-025` `FrameworkVersion` MUST contain the effective TwinCAT library version as `Major.Minor.Build`, without the revision component, and MUST be obtained or generated from the TwinCAT project or library metadata as its single source of truth.
- `RCC-026` An externally written lifecycle command bit MUST be present in two consecutive command-adapter samples before it is accepted, so a write occurring during PLC execution cannot be lost.
- `RCC-027` An accepted lifecycle command MUST be processed and reset by the PLC in its acceptance or processing cycle. No acknowledgement handshake or consumer-side command reset is required.
- `RCC-028` External lifecycle command triggers MUST be interpreted by positive edge.
- `RCC-029` External lifecycle command triggers SHOULD normally be reset again at the end of the same internal cycle.

## Requirement Details

### Table A: Run Control top-level fields

| Field | Type | Default |
| --- | --- | --- |
| `Name` | `STRING(80)` | `''` |
| `Cmd` | `ST_Command` | field defaults of nested structure |
| `BufferCmd` | `ST_EventBufferCommand` | fields defaulted explicitly in code |
| `Status` | `ST_RunControlStatus` | field defaults of nested structure |

### Table B: `ST_Command` fields

| Field | Type | Default |
| --- | --- | --- |
| `Start` | `BOOL` | `FALSE` |
| `Abort` | `BOOL` | `FALSE` |
| `Reset` | `BOOL` | `FALSE` |

### Table C: `ST_EventBufferCommand` fields

| Field | Type | Default |
| --- | --- | --- |
| `Reset` | `BOOL` | `FALSE` |
| `DeleteSequenceId` | `UDINT` | `0` |

### Table D: `ST_RunControlStatus` fields

| Field | Type | Default |
| --- | --- | --- |
| `Execution` | `ST_ExecutionStatus` | field defaults of nested structure |
| `SuiteCountTotal` | `UINT` | `0` |
| `SuiteCountInactive` | `UINT` | `0` |
| `SuiteCountIdle` | `UINT` | `0` |
| `SuiteCountRunning` | `UINT` | `0` |
| `SuiteCountCompleted` | `UINT` | `0` |
| `SuiteCountPassed` | `UINT` | `0` |
| `SuiteCountFailed` | `UINT` | `0` |
| `SuiteCountError` | `UINT` | `0` |
| `Runtime` | `TIME` | `T#0ms` |

### Table E: Contract info fields

| Field | Type | Default |
| --- | --- | --- |
| `ContractVersion` | `STRING(10)` | `''` |
| `FrameworkVersion` | `STRING(10)` | `''` |

### Table F: Published Run Control fields

| Field | Type | Access |
| --- | --- | --- |
| `ContractInfo` | `ST_ContractInfo` | producer-owned, read-only |
| `ControlData` | `ST_RunControl` | producer-owned except for the command fields explicitly defined as writable |

### Initialization trigger

Reinitialization requires configuration reactivation or a PLC Cold Reset (XAE: Reset kalt), followed by PLC execution. A normal PLC Stop/Start does not trigger the FirstCycle-based initialization path and MUST NOT be used as a substitute. In this contract, PLC-start initialization means the complete first-cycle initialization sequence under these conditions, not every transition from PLC Stop to Run.

Changes that invalidate registered children, array layout/capacity, orchestration configuration or service bindings MUST be followed by this full hierarchical reinitialization before regular framework execution resumes. Online Change alone is not sufficient. UnitTest lifecycle Reset and Event Buffer Reset do not rebuild configuration or replace reinitialization.

### Owner interactions

- Successful Run Control Initialize MUST queue one RequestReset after establishing valid setup. Only the regular FB call processes that request. The Sequencer MUST NOT repeatedly derive Reset from Uninitialized; failed initialization queues no startup Reset. Suites and Test Cases wait for capacity-admitted Reset requests from their parents. A failed first or later Reset follows the same Completing-to-Completed Error path, never Aborting or AbortingFromError.
- Run Control start readiness MUST combine its own initialization, configuration, collection validity, Idle state and error checks with the Orchestrator readiness and neutral helper result. The Orchestrator owns the child-readiness summary; Run Control MUST NOT maintain another readiness aggregate. Successful Reset completion is established through IsResetFinished and the helper result, independently of the helper's Idle-only IsReady. Child-result aggregation and status counters remain Run Control responsibilities.
- Run Control is the single intended PLC-side entry point of the framework slice.
- The PLC program invokes `Initialize(Name, EventBuffer : I_EventBuffer)` after Event Buffer initialization and publisher setup. Run Control retains this administrative interface, which belongs to the same service as its selected instance-local publisher. The normal publisher binding is obtained from the global default on first access; controlled selftest subjects may use an adapter-supplied binding. The composition or test arrangement owns the consistency of both views; this introduces no automatic rebinding protocol. The base accepts and validates the supplied root name before invoking Configure or initializing Suites. Timing configuration and Suite composition belong to the concrete Configure implementation, not additional external initialization parameters.
- PLC-start initialization MUST rebuild the complete configured hierarchy before regular execution, independently of transient values from the preceding run. Event Buffer initialization clears global reservation accounting; Suite and Test Case Initialize clear their respective local contributions without attempting release against that newly initialized buffer. A previous contribution MUST NOT prevent startup initialization. Registration is rebuilt through Configure and remains fixed afterwards. The existing lifecycle Reset then establishes Idle and readiness on success; initialization/configuration failures and failed test resets MUST NOT expose false readiness. The PC application is not responsible for establishing this startup state.
- The Run Control owner owns top-level run orchestration and the concrete Test Suite binding below it.
- The concrete Run Control owns static Suite instances and a local `ARRAY OF I_TestSuite`; array capacity is locally defined and the occupied count is established by registration during initialization.
- The concrete `Configure` implementation initializes and registers Suites through `RunControlConfigurationBuilder`. The base finalizes the builder after Configure returns and retains validated collection metadata. Registration is not performed during regular execution.
- Typed `GetTestSuite(Index)` access remains with the concrete implementation. The base exposes `GetChild(Index)` through the common collection interface without a second interface array. Concrete Suite FB calls remain in `ProcessTestSuites()`. Once Run Control is initialized, this hook MUST call every owned Suite exactly once per PLC cycle, including Completing and Completed, without an Enable input or parent-state gating. Requests and each child's own lifecycle control its work; the finalized parent result remains held while child aggregation and status counters continue to refresh.
- `ST_RunControl` is exclusively an external communication projection. It is not the owner’s authoritative internal state.
- RunControlBase receives `ControlData : ST_RunControl` through `VAR_IN_OUT` on its cyclic FB call. Normal application wiring supplies `GVL_UnitTest.RunControl.ControlData`; isolated selftest subjects may supply local communication storage. Run Control owns Name and status projection, not lifecycle command qualification.
- The signal adapter receives the selected `I_RunControl` and direct `VAR_IN_OUT` access to its external `Cmd` storage. It runs exactly once per PLC cycle before the corresponding Run Control FB call. It qualifies and clears each command independently, without selecting priority or publishing trigger events. Internal Run Control request processing retains those responsibilities. Qualification history belongs to the adapter and is separate from the owner request storage.
- The PLC application MUST call the signal adapter's Initialize with the selected ControlData in the first-cycle initialization sequence defined above, before Event Buffer and hierarchy initialization and before cyclic command intake. Adapter initialization MUST clear Cmd.Start, Cmd.Abort, Cmd.Reset, BufferCmd.Reset, BufferCmd.DeleteSequenceId and its own qualification history. It MUST preserve Name and Status and MUST NOT invoke owner requests or Event Buffer operations. Commands pending before this initialization are discarded; commands written afterwards use the unchanged cyclic protocol. Run Control initialization clears its own request storage; lifecycle Reset does not reinitialize the adapter. Before successful Run Control initialization, qualified commands are cleared by the adapter and discarded through the passive owner request path, not retained for later execution. Name and status projection MUST remain active on every Run Control FB call, including Uninitialized and Completed.
- Communication accesses MUST target the responsible fields only. The complete ControlData structure MUST NOT be copied back over asynchronously written commands. VAR_IN_OUT provides direct access, not synchronization or an atomic snapshot. The two-sample qualification and selective command clearing remain required; repeated identical writes to an already set BOOL do not encode additional distinguishable requests.
- The Run Control integration path owns external `BufferCmd` intake and delegates Event Buffer operations according to `event-stream-contract.md`.
- The Run Control base implements the generic Run Control lifecycle when it is used.
- Run Control provides the default shared Orchestrator composition and initializes, invokes and observes its selected helper through I_Orchestrator. A controlled test adapter may bind another helper before initialization; lifecycle Reset retains the binding and no on-the-fly replacement is supported. The helper coordinates Suite execution using I_OrchestratableCollection and I_Orchestratable; concrete Suite calls, aggregation and owner timeouts remain with Run Control.
- Run Control aggregates suite-level execution and result information into its own status structure.
- Run Control does not own suite-internal test logic.
- Run Control uses the event structures and classifications defined in `event-stream-contract.md` when emitting run-level events.
- Run Control emits run-level lifecycle, Result, and Aggregation evidence with `E_OwnerScope.RunControl`, even when a generic helper technically emits the event on behalf of the Run Control lifecycle.
- The Orchestrator is a technical helper below Run Control, not an independent reportable UnitTest owner level.
- The shared Orchestrator emits contextual Framework error diagnostics, not normal task/slot transition events. It has no independent owner lifecycle or state machine. The defined E_OwnerScope.TestSuiteOrchestrator value remains available for technical diagnostics but is not used by this helper; such diagnostics are never Run Control lifecycle, Result or Aggregation evidence.

### Completion delay and finalization

On entry to Completing, Run Control updates its final result and status and publishes Result followed by Aggregation exactly once. The final owner result and emitted event payloads remain unchanged during CompletionDelay; child aggregation and status counters continue to refresh cyclically, including in Completed. Run Control remains in Completing for at least CompletionDelay and emits the transition to Completed in a later PLC cycle, including when CompletionDelay is zero. If the delay measurement becomes invalid after finalization publication, Run Control publishes one Framework diagnostic and transitions to Completed. It neither republishes the final events nor changes the final owner result; current status counters remain independent of the emitted aggregation payload. This exceptional path does not guarantee the minimum delay. Explicit lifecycle Reset remains available.

CompletionDelay belongs exclusively to the Run Control lifecycle, not to the Orchestrator. It provides additional temporal separation before the terminal lifecycle event; event sequence IDs remain the authoritative stream ordering mechanism. The delay is not a consumer acknowledgement or a guarantee that the consumer has already read preceding events.

### Reset capacity responsibility

Reset can produce lifecycle, result, aggregation, and diagnostic events at several hierarchy levels in a short period. Run Control therefore owns capacity-safe dispatch of subordinate Suite resets. Capacity waiting is part of the active reset operation; it does not suspend or restart reset timeout monitoring.

The shared framework Orchestrator satisfies this responsibility through capacity-bounded dispatch, acquiring each Suite's own Reset reservation before dispatch and starting at most one Suite reset per call. An earlier Suite need not finish before another is admitted. Suite admission must leave the shared reset headroom plus one Test Case Reset allowance unreserved, using the rule in test-suite-contract.md. It uses the Suite-owned reservation methods and E_EventReservationResult defined in test-suite-contract.md. Normal Start also acquires the Suite's own execution reservation; child Test Case budgets remain independently owned. The parent releases Suite reservations on the defined completion boundaries without a duplicate ledger. Direct implementations may use another scheduling algorithm, but they must preserve the same capacity and completion guarantees.

On Reset failure, Run Control continues remaining child Reset attempts until completion or its Reset deadline. When it leaves Reset for error finalization, its Orchestrator task changes from Reset to Idle, never Abort. The task transition stops this Orchestrator's dispatch but does not itself release unfinished child reservations. Children continue processing their own lifecycle and timeout through regular cyclic calls, including after root completion. CompletionDelay and final Result/Aggregation publication remain unchanged.

After a Suite reaches Completed, hierarchical FreeReservedEvents releases its entire subtree even if a Test Case is still finishing its own Reset. The completed Suite issues no new child requests, but continued cyclic calls allow outstanding child processing to finish or reach its own timeout. Reservation accounting need not cover all remaining events in this failure path; the temporary capacity-overbooking risk is accepted rather than adding a second cancellation protocol or reservation ledger. This is not a general guarantee that faulty processing cannot overflow the Event Buffer. The PLC program must continue cyclic Run Control calls for child completion and cleanup. Final Result/Aggregation events and CompletionDelay remain unchanged; delayed child diagnostics and consumer interpretation are verified in integration/selftests.

## Compatibility

This contract defines the producer-side Run Control data structures only.

## Non-Requirements

This contract does not define:

- normal Suite execution scheduling details and concrete reset-capacity reservation formulas,
- event transport,
- event structure or communication classifications,
- PC-side contract consumption.
