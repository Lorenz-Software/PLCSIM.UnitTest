# Test Suite Contract

## Purpose

This contract defines the PLC-side producer contract for Test Suite lifecycle behavior and status.

## Scope

This contract defines:

- `ST_TestSuiteStatus`,
- suite-local naming rules,
- status and request boundaries of a Test Suite owner.

This contract references `general-framework-contract.md` for shared execution/result semantics and shared type rules.

Event-entry structure, event classifications, event-stream rules, and consumer-facing buffer projection are defined by `event-stream-contract.md` and are outside this Test Suite contract.

## Version

- Contract version: `1.0.0`

## Requirements

- `TSC-001` A Test Suite owner MUST expose a read-only `Status` property of type `ST_TestSuiteStatus`.
- `TSC-002` `Name` MUST be a stable owner property and MUST NOT be modeled inside `ST_TestSuiteStatus`.
- `TSC-003` Suite-status fields MUST follow Table A.
- `TSC-004` Start, Abort, and Reset MUST be accepted through dedicated object-oriented request operations. The regular Test Suite FB call performs the lifecycle processing.
- `TSC-005` A concrete Test Suite MUST receive its typed configuration through explicit `Initialize(...)` and validate it before execution. Hierarchical initialization rebuilds transient setup and registration using the initialization trigger defined in [Run Control](run-control-contract.md#initialization-trigger). Reinitialization during an active run is not a supported recovery path. Rejected input may remain available for diagnostics but MUST NOT enable execution. Configuration is immutable during regular execution.
- `TSC-006` The owning Run Control MUST initialize each statically bound Test Suite with `Parent : I_RunControl` and a local Suite name before regular execution. The Suite MUST derive its full hierarchical identity from the validated Parent.Name and local name; the Parent identity MUST already be established.
- `TSC-007` Timeout deactivation MUST NOT be allowed. Timeout values MUST be greater than `T#0ms`; `T#0ms` MUST be treated as invalid.
- `TSC-008` A Test Suite configuration DUT is internal implementation data and MUST NOT be published as a PLC/PC contract structure.
- `TSC-009` A Test Suite status snapshot MUST NOT be exposed through a direct owner-instance symbol.
- `TSC-010` An application that publishes Suite status MUST use an explicit data-oriented wrapper containing Suite identity and `ST_TestSuiteStatus`.
- `TSC-011` Test Suite finalization MUST use `E_State.Completing` for final Suite result and Test Case aggregation publication before the Suite enters `E_State.Completed`.
- `TSC-012` Test Suite MUST emit exactly one final Suite Result event for each controllable Suite lifecycle from `Idle` to `Completed`.
- `TSC-013` Test Suite MUST emit exactly one final Suite Aggregation event for each controllable Suite lifecycle from `Idle` to `Completed`.
- `TSC-014` Test Suite MUST emit its final Result and Aggregation events while in `E_State.Completing` and before the final state-transition event into `E_State.Completed`.
- `TSC-015` Test Suite `Name` MUST be the full hierarchical Suite name `<runControlName>.<suiteName>` defined by `event-stream-contract.md`.
- `TSC-016` The owning Run Control implementation MUST configure each Suite with a valid full hierarchical Suite name.
- `TSC-017` A Test Suite MUST configure contained Test Cases with full hierarchical names below its own Suite name.
- `TSC-018` A Test Suite implementation MAY use the framework Suite base and the composed Orchestrator or MAY implement the required lifecycle and child-execution behavior directly.
- `TSC-019` Regardless of the selected implementation pattern, the Test Suite implementation MUST provide the same command handling, lifecycle signals, status, result, aggregation, timeout handling, and owner-level event semantics defined by this contract.
- `TSC-020` If subordinate Test Case abort handling does not finish within `AbortTimeout`, the Test Suite MUST emit a framework diagnostic event and complete its lifecycle with `E_Result.Error`.
- `TSC-021` If subordinate Test Case reset handling does not finish within `ResetTimeout`, the Test Suite MUST emit a framework diagnostic event and complete its lifecycle with `E_Result.Error`.
- `TSC-022` Orchestrator events MUST NOT be emitted or interpreted as Test Suite lifecycle, Result, or Aggregation evidence.
- `TSC-023` Test Suite reset handling MUST bound subordinate Test Case reset dispatch against the available event-buffer capacity so that the expected reset events fit while the required producer headroom remains available.
- `TSC-024` The framework-provided Orchestrator MUST use capacity-bounded reset batches and MUST wait for sufficient event-buffer capacity before starting additional required Test Case resets.
- `TSC-025` A subordinate Test Case in `E_State.Idle` with `Status.Execution.Ready = TRUE` MAY be treated as reset-complete without receiving another reset command.
- `TSC-026` Test Suite `ResetTimeout` monitoring MUST cover the complete reset operation, including time spent waiting for event-buffer capacity and subordinate Test Case completion.
- `TSC-027` A project-specific Test Suite implementation that does not use a framework Orchestrator MUST provide equivalent capacity-bounded reset dispatch and completion behavior.
- `TSC-028` The framework-provided Orchestrator MUST gate normal Test Case Start dispatch according to the normal-execution capacity model defined in this contract.
- `TSC-029` One Orchestrator instance MUST start at most one subordinate Test Case in one PLC cycle.
- `TSC-030` Serial, Parallel and ParallelSlots modes MUST preserve the candidate-selection semantics defined under Normal execution capacity responsibility.
- `TSC-031` Temporarily insufficient event-buffer capacity MUST delay Test Case dispatch without producing a cyclic event or a configuration error.
- `TSC-032` Capacity-waiting time after an accepted Suite Start MUST remain part of the active Test Suite runtime and its execution timeout monitoring. The Orchestrator MUST NOT maintain a separate execution timer or Runtime property.
- `TSC-033` Invalid Test Case lifecycle-capacity metadata, arithmetic overflow during budget calculation, or an individual Test Case budget that cannot fit with the configured execution headroom MUST be treated as a Test Case orchestration configuration error.
- `TSC-034` The framework-provided Orchestrator MUST reserve each accepted Test Case event budget in the producer-owned global event-buffer reservation count before issuing the corresponding Test Case Start command.
- `TSC-035` Global Test Case event-buffer reservations MUST prevent concurrently active Orchestrator instances from allocating the same event-buffer capacity.
- `TSC-036` A project-specific Test Suite implementation that does not use a framework Orchestrator MUST provide equivalent normal-execution reservation and capacity-gating behavior when it claims the capacity guarantee defined by this contract.
- `TSC-037` Each Test Case MUST retain its own reservation contribution. Its reservation methods MUST update that contribution together with the producer-owned global count; the Orchestrator MUST coordinate these methods without maintaining a second reservation ledger.
- `TSC-038` A Test Case Start reservation MUST remain pending until the subordinate Test Case has left `E_State.Idle` or has reached `E_State.Completed` when the Orchestrator is called again.
- `TSC-039` If a subordinate Test Case remains in `E_State.Idle` on that following Orchestrator call, the Orchestrator MUST release the pending reservation, emit a Framework diagnostic event, and enter its error handling path.
- `TSC-040` An accepted Test Case execution reservation MUST be retained through active execution, Abort, and finalization until the subordinate Test Case reaches `E_State.Completed`.
- `TSC-041` Parent Reset MUST replace execution reservations with the reservations required for subordinate Reset processing; each Reset reservation MUST be released when the corresponding subordinate Test Case reaches `E_State.Idle` with `Status.Execution.Ready = TRUE`.
- `TSC-042` A successfully completed Run Control Reset MUST leave no Test Suite or Test Case execution or Reset reservation in the producer-owned global event-buffer reservation count.
- `TSC-043` The library parameter `EventBufferExecutionHeadroom` MUST remain outside Test Case reservations as shared producer capacity for Run Control, Orchestrator, and additional exceptional Framework events. Test Suite owner events have a separate Suite-owned reservation.
- `TSC-044` The shared execution headroom MUST NOT be interpreted as a per-owner reservation or as a guarantee for an unbounded number of simultaneously produced non-TestCase events.

## Requirement Details

### Shared orchestration interfaces and collection ownership

I_TestSuite MUST extend I_Orchestratable, which extends I_ResultProvider, while retaining Status : ST_TestSuiteStatus. The shared view exposes read-only State, IsInitialized, IsReady, IsComplete, HasError and ReservedEntryCount, writable configuration ExecutionSlot, lifecycle requests and existing reservation methods. HasError includes configuration and runtime errors; Name and Result remain inherited. Initialize remains instance-owned. The Suite's ExecutionSlot identifies its place in its parent's orchestration, not the active slot among its own Test Cases.

For shared scheduling, the Suite MUST implement I_OrchestratableCollection and provide indexed GetChild(Index) : I_Orchestratable access with collection bounds/count. The Suite retains its typed local Test Case array and provides the common child view from that storage; it MUST NOT maintain a parallel generic child array. Concrete child calls, owner aggregation, timeout supervision and lifecycle events remain owner responsibilities. Typed status snapshots and PLC/PC publication are unchanged.

The builder MUST determine actual array bounds, register children, assign slots and validate capacity and the completed configuration. The owner/collection MUST retain the finalized configuration with its typed array and expose read-only ChildArrayLowerBound, ChildArrayUpperBound and ChildCount. ChildCount denotes the contiguous occupied range starting at ChildArrayLowerBound; ChildArrayUpperBound denotes the capacity boundary. GetChild(Index) MUST use the actual array index and return zero outside the occupied range without invoking the child.

The Orchestrator MUST cache the first and last occupied indices during initialization from ChildArrayLowerBound and ChildCount. It MUST NOT cyclically query these unchanged metadata or maintain independent registration data. ChildArrayUpperBound and repeated capacity validation are not required by the Orchestrator; capacity validation belongs to the builder and collection initialization. Child access continues through the collection interface, not a retained array pointer. Composition changes require configuration reactivation or a PLC Cold Reset and explicit hierarchical initialization; lifecycle Reset preserves this configuration. Owner Name and ResourceName remain initialization inputs for technical diagnostics under <OwnerName>.Orchestrator, not owner lifecycle evidence.

### Base initialization and Test Case collection

TestSuiteBase MUST provide the default framework Orchestrator composition and own helper initialization, cyclic invocation and state-to-task mapping. Initialization, execution and observations MUST use the same selected I_Orchestrator binding. A controlled test adapter MAY replace the protected binding before owner initialization; lifecycle Reset MUST retain it and on-the-fly replacement is not supported. Ordinary derived Suites MUST NOT be required to supply or call a helper. Direct I_TestSuite implementations remain permitted and own equivalent contract behavior.

The base Initialize establishes identity, creates a local TestSuiteConfigurationBuilder and passes it by VAR_IN_OUT to protected abstract Configure(Builder), without a return value. The concrete Suite initializes its static children, binds its local ARRAY OF I_TestCase through Builder.Initialize and selects E_OrchestrationMode. After Configure returns, the base MUST call Finalize and reject an invalid returned configuration. It MUST expose the finalized collection and initialize its owned Orchestrator through I_OrchestratableCollection before CheckInitialization validates the complete setup, including the children. Only successful validation permits successful Suite initialization and cyclic Orchestrator execution. Failed Suite initialization MUST leave Uninitialized and ConfigError set and MUST NOT expose stale collection metadata as a valid current configuration. The Suite retains finalized collection metadata before helper initialization and exposes it during synchronous initialization validation or after successful initialization; no additional cyclic Enable is required. Missing builder initialization, invalid registration or uninitialized children MUST prevent success. No separate RegisterTestCases or RegisterOrchestrator handover is required.

Register(TestCase) MUST accept only Serial and Parallel mode. Serial appends and assigns ExecutionSlot in registration order starting at 1; Parallel appends and assigns slot 1. RegisterWithSlot(TestCase, ExecutionSlot) MUST accept only ParallelSlots and positive slots. It inserts before the first higher-slot child, preserving registration order within equal slots. The tail is shifted backwards from the last occupied entry to avoid overwriting uncopied entries.

Mode, non-null reference, uniqueness, slot and capacity checks MUST precede array, count and child-slot mutation. A rejected registration MUST leave the registered composition unchanged and latch an error until builder Initialize. FirstIndex and LastIndex MUST be the actual LOWER_BOUND and UPPER_BOUND. Count MUST describe the contiguous occupied range starting at FirstIndex; the last occupied index is FirstIndex + Count - 1, not necessarily LastIndex. Bounds/count arithmetic MUST be validated before access.

Finalize MUST discard the temporary array pointer on success and failure. It MUST accept only an initialized, nonempty, error-free configuration with contiguous populated slots starting at 1; temporary slot gaps during registration are permitted. Finalize MUST be idempotent: subsequent calls return the stored outcome without further array access. IsFinalized records completion even on failure; read-only IsValid records successful validation. Registration after finalization MUST fail without modifying the completed configuration. Only Initialize begins a new build. The base remains responsible for finalization even if Configure already called it.

The typed array pointer is confined to this synchronous setup sequence with stable storage and no concurrent mutation. Finalized bounds/count, validity and mode are transferred as configuration values; neither builder nor array pointer is retained for cyclic execution.

The Suite implements I_OrchestratableCollection. Its read-only ChildArrayLowerBound, ChildArrayUpperBound and ChildCount read the Suite-owned finalized configuration and implement the common collection directly. ChildArrayLowerBound and ChildArrayUpperBound denote the actual array bounds, including unused capacity; the last registered index is ChildArrayLowerBound + ChildCount - 1. GetTestCase(Index) returns the typed child at its array index or zero outside the occupied range, without executing it. GetChild(Index) checks availability and the occupied range and returns that same child as I_Orchestratable, without a parallel array. The Orchestrator retains the collection interface and accesses children by index; collection metadata is not delegated back to the helper. Only ChildCount entries are processed, not unused capacity.

ExecutionSlot is a writable Test Case configuration property assigned by the builder. It MUST remain unchanged during regular execution. Array layout, registration and mode changes require configuration reactivation or a PLC Cold Reset and explicit hierarchical initialization; lifecycle Reset preserves configuration. The framework MUST NOT introduce a parallel slot/control array or a copied child-entry structure.

One E_OrchestrationMode defines Serial, Parallel and ParallelSlots. The owned Orchestrator processes contiguous slot groups in the sorted collection: Serial has one child per slot, Parallel one slot, and ParallelSlots explicit groups. It MUST finish the current slot before starting a higher slot and preserve first-fitting selection within that slot.

### Table A: `ST_TestSuiteStatus` fields

| Field | Type | Default |
| --- | --- | --- |
| `Execution` | `ST_ExecutionStatus` | field defaults of nested structure |
| `TestCaseCountTotal` | `UINT` | `0` |
| `TestCaseCountInactive` | `UINT` | `0` |
| `TestCaseCountIdle` | `UINT` | `0` |
| `TestCaseCountRunning` | `UINT` | `0` |
| `TestCaseCountCompleted` | `UINT` | `0` |
| `TestCaseCountPassed` | `UINT` | `0` |
| `TestCaseCountFailed` | `UINT` | `0` |
| `TestCaseCountError` | `UINT` | `0` |
| `Runtime` | `TIME` | `T#0ms` |

### Naming

The Test Suite `Name` is not a local display label. It is the full hierarchical Suite identity used in event entries:

```text
<runControlName>.<suiteName>
```

The owning Run Control supplies itself as Parent : I_RunControl and the local Suite name during Initialize. The Suite validates the reference and name components and builds its full hierarchical identity from Parent.Name and the local name. This identity is the parent path for Test Case names. An external Suite status view combines the identity with ST_TestSuiteStatus in an explicit data-oriented wrapper. Test Case names configured by the Suite follow:

```text
<runControlName>.<suiteName>.<testCaseName>
```

Local Suite and Test Case name segments must follow the hierarchy and character rules from `event-stream-contract.md`. Invalid Suite names are Suite configuration errors. Invalid Test Case names are detected by the owning Test Case and propagate upward through Test Case readiness and result handling.

### Owner interactions

- Test Suite is orchestrated by the owning Run Control path or by a higher-level explicit suite invocation context.
- Test Suite is the semantic owner of its lifecycle, request handling, lifecycle signals, result, aggregation, and owner-level events.
- `ST_TestSuiteStatus` is a mandatory typed snapshot returned by the owner Status property. Copying that snapshot into a global HMI or PC view is optional and must use an explicit data-oriented wrapper; owner instance data is never published.
- Test Suite owns execution control of contained Test Cases.
- Test Suite aggregates test-case-level execution and result information into its own status structure.
- Test Suite does not own assertion logic.
- Test Suite uses the event structures and classifications defined in `event-stream-contract.md` when emitting suite-level events.
- Test Suite emits suite-level lifecycle, Result, and Aggregation evidence with `E_OwnerScope.TestSuite` and its full hierarchical Suite name.
- The framework Suite base provides the Suite lifecycle state machine, command processing, runtime handling, lifecycle signals, timeout monitoring, and Suite owner state-transition events.
- One base-owned Orchestrator provides Serial, Parallel and ParallelSlots execution through E_OrchestrationMode.
- Use of the framework Suite base and Orchestrators is optional. A project-specific implementation that does not use them is responsible for implementing the same contract behavior directly.
- Orchestrator instances are technical helpers below Test Suite, not independent reportable UnitTest owner levels and not owners of the Suite lifecycle.
- The Suite-owned Orchestrator does not emit normal task/slot transition events. It retains contextual Framework error diagnostics; these are not Suite lifecycle, Result or Aggregation evidence.

Once initialized, TestSuiteBase MUST call ProcessTestCases every PLC cycle, including Completing and Completed. Its concrete implementation MUST call every owned Test Case exactly once, without an Enable parameter or a parent-state guard. Child requests and child lifecycle state determine work; the completed Suite result remains unchanged while aggregation of child observations and status counters continues.

### Owner-local timeout handling

Successful Suite initialization MUST leave the lifecycle in Uninitialized until the parent reserves Reset capacity and submits RequestReset. Regular child calls MUST NOT trigger an autonomous initial Reset. IsInitialized reports validated configuration independently of the lifecycle state. This applies to direct interface implementations and framework doubles as well as TestSuiteBase.

TestSuiteBase owns its execution, Reset and Abort timeout detection and lifecycle reactions. Its Reset and Abort deadlines cover the complete Suite operation, including child handling and capacity waiting. Child bases enforce their own phase limits; the Orchestrator consumes their ordinary status and result instead of duplicating those timeout checks. A Suite Reset or Abort timeout emits a Framework diagnostic and completes with Error through Completing to Completed. Escalation from Aborting to AbortingFromError preserves the original abort deadline.

The base exposes read-only BOOL HasTimeout (execution timeout), HasResetTimeout and HasAbortTimeout as stored diagnostics, not members of I_TestSuite or ST_TestSuiteStatus. Once detected, each flag remains set through finalization and Completed. Explicit Initialize and actual entry into a new Reset clear the flags; a timeout in that Reset sets HasResetTimeout again. Getters have no processing side effects. Invalid time-source measurements are Framework errors, not expired-timeout flags.

### Suite-directed Orchestrator tasks

The Suite owns the lifecycle and supplies Task : E_OrchestratorTask at every Orchestrator FB call. Its mapping has no separate request storage, request processing or task-transition decisions. The Orchestrator MUST NOT expose its former RequestReset, RequestStart, RequestAbort or RequestIdle operations. It retains the previously processed task only to detect a change and initialize task-specific progress once. The new task MUST be processed in that same call without an entry-cycle delay. Repeating the same task MUST preserve its progress and held errors. Initialization establishes Idle. The Suite MUST select Execution only when leaving a ready Idle state. Start authorization belongs exclusively to the Suite; Orchestrator task-entry initialization MUST NOT duplicate its Idle/readiness checks. Child-state, reservation and Start-response checks remain Orchestrator responsibilities during processing.

Task completion MUST NOT autonomously change the task. Reset progress, current slot and pending Start confirmation remain internal execution data. Technical Error MUST prevent further execution Starts without cancelling remaining Reset attempts. Error remains held until explicit Initialize or entry into a new Reset task. The helper Result carries NotEvaluated or Error only, not a child aggregate or a separate Passed/Abort lifecycle result.

Read-only properties IsResetFinished and IsAbortFinished report the respective task completion without side effects. Reset completion includes failed terminal children and is distinct from start readiness. IsExecutionFinished reports completion of all slots with reconciled reservations and no helper Error. IsComplete selects the current task completion and is FALSE in Idle. IsResetActive, IsExecutionActive and IsAbortActive identify the assigned task, including after its completion until the next task change. IsReady requires initialization, Idle, ready children, successful reservation cleanup and no held helper Error. ActiveSlot reports the selected Execution slot while execution is unfinished, otherwise zero. Task remains available for diagnosis; regular consumers prefer the dedicated properties. Getters MUST NOT publish diagnostics, dispatch requests or release reservations.

For Suite start readiness, the Orchestrator MUST be the authoritative summary of child readiness and reservation cleanup. The Suite combines that summary and the neutral helper result with its own initialization, configuration, collection validity, Idle state and error checks; it MUST NOT maintain a second child-readiness aggregate. Reset completion uses IsResetFinished and the helper result, not IsReady, because the helper remains in its Reset task until the Suite changes state. Typed child-result aggregation and status counters remain Suite responsibilities.

The Suite maps Reset to Reset, Starting/Running to Execution, and Aborting/AbortingFromError to Abort. Completing preserves the previously assigned Execution or Abort task; a previous Reset or Idle task maps to Idle. Error alone MUST NOT select Abort. Reset failure and Reset timeout MUST NOT dispatch child Abort requests. Idle, Completed and Uninitialized map to Idle. Completed therefore stops dispatch even after a timeout. Helper Idle MUST NOT issue new child Start/Reset/Abort requests and does not itself stop children. It releases eligible finished reservations under the established release rules. The task transition from Reset to Idle MUST NOT itself force release of unfinished child reservations. It MUST retain held errors and MUST NOT zero reservation counters independently. Owner final results remain retained.

### Reset capacity responsibility

Reset errors from children already processed by reset dispatch MUST be retained without cancelling remaining child Reset attempts. Previous-run results from children not yet processed MUST NOT be interpreted as Reset failures. Dispatch remains capacity-bounded. The Orchestrator MUST continue until all children have been processed and each has reached Idle AND Ready or terminal Completed; Completed during Reset denotes a failed Reset, not successful readiness. The Suite MUST retain the failure but wait for Orchestrator completion before error finalization, unless its own ResetTimeout or an invalid processing prerequisite requires termination. A fully successful Reset ends in Idle; a failed Reset ends through Completing in Completed with Error. Rejected Reset reservations MUST NOT prevent attempts on later children while the initialized collection remains valid. Missing or unusable child bindings invalidate that prerequisite; recovery of corrupted or invalidated collections is not required. Repeated Reset requests while Reset is active MUST NOT clear its held error or restart dispatch progress.

Test Suite owns capacity-safe dispatch of subordinate Test Case resets. Capacity waiting is part of the active Suite reset operation; it does not suspend or restart reset timeout monitoring.

The framework Orchestrators satisfy this responsibility by starting only the number of concurrent Test Case resets for which event-buffer capacity is currently available. Direct implementations may use another scheduling algorithm, but they must preserve the same capacity and completion guarantees.

### Normal execution capacity responsibility

The Test Case computes its complete execution event budget for orchestration as:

```text
TestCaseEventBudget =
    PreparationStateCount
  + TestStateCount
  + AssertionCountTotal
  + 9
```

The fixed value consists of six normal fixed owner events outside the counted Preparation/Test state entries, one Abort trigger allowance, one additional transition into the abort path, and one exceptional Framework event. The six normal fixed events are the Start trigger, the three fixed lifecycle transitions outside the counted Preparation/Test entries, the final Result event, and the final Aggregation event. One finding event is budgeted per Assertion. A possible later escalation from a Failed finding to an Error finding is not budgeted for every Assertion.

Before starting one candidate, an Orchestrator calls that Test Case's ReserveExecutionEvents(). The Test Case computes its complete budget and atomically updates its own contribution and the producer-owned global reservation count. The increase is accepted only when:

```text
PhysicalFreeEntries >=
    GlobalReservedEntryCount
  + candidate TestCase event budget
  + EventBufferExecutionHeadroom
```

`EventBufferExecutionHeadroom` is a project-overridable TwinCAT library parameter with the framework default `10`. It is independent of consumer polling, draining, and batch settings. It remains unreserved shared producer capacity for Run Control, Orchestrator, and additional exceptional Framework events; Test Suite owner events are covered by their own reservation. It is not a separate reservation for every active owner and does not guarantee capacity for an unbounded number of simultaneous non-TestCase events.

The producer maintains one global reservation count combining Test Suite and Test Case contributions. Each Test Case owns its local contribution; an Orchestrator may start at most one Test Case per PLC cycle. It obtains the complete candidate reservation through the child before submitting its Start request. On the following Orchestrator call, the subordinate Test Case must have left `Idle` or already reached `Completed`; otherwise the reservation is rolled back and the failed Start reaction is handled as a Framework error. The complete original budget remains reserved until the Test Case reaches `Completed`. This is intentionally conservative because already emitted events do not reduce an active Test Case reservation.

Candidate selection preserves the established Orchestrator meanings:

- `Serial` considers the next Test Case in ascending array order and does not move a later Test Case ahead when the next candidate is waiting for capacity.
- `Parallel` scans not-yet-started Test Cases in ascending array order and starts the first candidate whose budget currently fits.
- `ParallelSlots` applies the same first-fitting behavior within the current slot and advances only after all Test Cases in that slot are `Completed`.

A valid Suite Start is accepted once even when no Test Case currently fits. The Orchestrator retains the requested execution task and waits without requiring another Start command. The Suite measures active runtime, including this wait, and owns execution timeout monitoring. Abort prevents further dispatch and retains reservations for already accepted Test Cases through their completion.

Parent Reset calls ReserveResetEvents() on each selected child before requesting its Reset, replacing its execution reservation with the capacity required by the subordinate Reset operation. The Orchestrator calls FreeReservedEvents() as children reach `Idle AND Ready`. A successfully completed hierarchical Run Control Reset therefore leaves the global reservation count at `0`. When Reset processing ends unsuccessfully, the task transition to Idle stops that helper's dispatch but does not itself force release. Cyclic child calls continue so their own lifecycle and timeout handling can reach stable states.

The global reservation count combines the independently owned Test Suite and Test Case contributions. Sequential PLC invocation therefore cannot cause multiple Orchestrators to allocate the same physical event-buffer capacity.

### Test Suite orchestration metadata and reservations

I_TestSuite MUST expose read-only IsInitialized and ReservedEntryCount, writable configuration ExecutionSlot, ReserveExecutionEvents() : E_EventReservationResult, ReserveResetEvents() : E_EventReservationResult, and FreeReservedEvents() : BOOL. These are PLC-only orchestration members, not additions to ST_TestSuiteStatus or the PLC/PC symbol contract.

The Suite owns only its own event reservation. It MUST NOT reserve the sum of child budgets or duplicate the Test Case contributions. Suite execution uses a fixed ten-entry allowance: seven normal owner events (Start trigger, four lifecycle transitions, Result and Aggregation), plus one Abort trigger, one additional abort-path transition and one Framework diagnostic. Suite Reset uses four entries: Reset trigger, transitions into Reset and Idle, and the active-reset warning allowance. Extra diagnostics remain covered by shared headroom rather than an unbounded guarantee.

The Run-Control-owned Orchestrator reserves before dispatch, checks Start reaction on the next call, and releases on Completed or a failed Start reaction. Reset replaces the Suite's execution reservation before dispatch and releases it on Idle AND Ready. The Suite's Orchestrator independently coordinates child reservations. Neither parent keeps a second ledger. During full hierarchical PLC-start initialization, Initialize clears the Suite's own reservation contribution and transient setup before Configure rebuilds its Test Case registration and initializes the children. Each child clears its own previous contribution in Initialize; the Suite MUST NOT traverse the old registration to release reservations or make initialization depend on their release. The Event Buffer has already been initialized for the new run context. Isolated reinitialization during regular execution is not a supported recovery operation. Lifecycle Reset preserves reservation accounting and ExecutionSlot; Initialize preserves ExecutionSlot.

Accepted means the local and global update succeeded. WaitingForCapacity means temporary pressure and MUST remain silent. Error means an invalid prerequisite or permanently impossible budget and MUST NOT be retried as capacity waiting. Rejection leaves both counts unchanged. FreeReservedEvents releases the complete registered subtree, including the Suite's own contribution, synchronously without changing lifecycle states. A zero own contribution MUST NOT skip child cleanup. It MUST attempt all available children and its own release even after an individual failure. TRUE means all releases succeeded; FALSE permits partial success, retains unresolved contributions, and allows idempotent retry. Already released contributions succeed without changing counters.

Capacity validation MUST account for simultaneous Suite and child reservations plus shared headroom. Test Case reservation validates its computed budget together with the fixed Suite allowance against the actual publisher Capacity. Thus a permanently impossible child cannot wait indefinitely behind its own Suite reservation. Other concurrent Suites may temporarily prevent admission; their reservations are not part of the candidate's permanent minimum. Reset requires capacity for the Suite allowance plus one child Reset allowance and reset headroom. This does not guarantee progress for arbitrary concurrent occupancy or absent event draining; owner timeouts remain applicable.

### Hierarchical Reset admission

Before accepting a nonzero Suite Reset reservation, the Suite MUST require:

```text
PhysicalFreeEntries >=
    GlobalReservedEntryCount - OwnReservedEntryCount + TestSuiteResetEventCount
  + EventBufferResetHeadroom
  + TestCaseResetEventCount
```

Only the Suite contribution is updated. The additional Test Case allowance remains shared unreserved admission capacity, not a per-Suite reservation or a second ledger. The same check applies when reducing an execution reservation or requesting an unchanged Reset budget. Insufficient free capacity returns WaitingForCapacity without modifying either count. Releasing the complete own contribution remains independent of free capacity; the permanent minimum-capacity check remains in force.

Test Case Reset admission MUST NOT add the fixed Suite allowance to its free-capacity requirement: the parent's actual reservation is already included in the global count. Its permanent minimum-capacity check still includes that allowance. Each Test Case acquires and releases its own contribution normally.

In the single-task invocation model, every admitted Suite leaves room, at that admission point, for one otherwise valid Test Case Reset reservation plus shared headroom. Additional Suite admission cannot consume that allowance through reservation accounting alone. Events emitted afterwards reduce physical free capacity without reducing held reservations, so admission is not a promise of immediate child progress. Continued consumer draining, valid bindings and bounded child completion remain prerequisites for progress; owner Reset timeouts include capacity waiting. Child errors and timeouts retain the defined lifecycle outcomes. A Reset-to-Idle task change does not directly clear reservation accounting. When a Suite completes after a Reset failure, its hierarchical FreeReservedEvents may release still-resetting descendants. The completed Suite stops dispatch, while child FB calls continue until each child completes or reaches its own timeout. This deliberately accepts temporary loss of reservation coverage for late events in the failed operation; it does not extend the normal capacity guarantee to arbitrary faulty event production. No additional cancellation request, child-call guard or reservation ledger is required.

### Childless test double

TestSuiteMock is an explicitly childless I_TestSuite test double, not a production Suite composition. It does not implement a child collection or instantiate subordinate Test Cases. Its Test Case counters MUST remain zero, while ResultValue supplies the simulated Suite outcome. It MUST retain Suite identity, cyclic request processing, status consistency, owner event ordering and real Event Buffer reservation accounting. No child lifecycle evidence may be fabricated. The production collection and child-orchestration requirements above are not implemented by this double; they remain unchanged for production Suites.

## Compatibility

This contract defines the producer-side Test Suite status and lifecycle/orchestration responsibilities.

## Non-Requirements

This contract does not define:

- project-specific grouping policy,
- communication transport details,
- event structure or communication classifications.
