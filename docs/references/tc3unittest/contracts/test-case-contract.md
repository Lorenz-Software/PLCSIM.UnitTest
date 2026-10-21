# Test Case Contract

## Purpose

This contract defines the PLC-side producer contract for Test Case lifecycle behavior, status, and the related Preparation and Assertion interface rules.

## Scope

This contract defines:

- `ST_TestCaseStatus`,
- `ST_TestCaseExecutionStatus`,
- `ST_PreparationStatus`,
- Test Case lifecycle metadata used for capacity-aware orchestration,
- assertion interface expectations relevant to the producer contract.

This contract references `general-framework-contract.md` for shared execution/result semantics and shared type rules.

Event-entry structure, event classifications, event-stream rules, and consumer-facing buffer projection are defined by `event-stream-contract.md` and are outside this Test Case, Preparation, and Assertion contract.

## Version

- Contract version: `1.0.0`

## Requirements

- `TCC-001` A Test Case owner MUST expose a read-only `Status` property of type `ST_TestCaseStatus`.
- `TCC-002` `Name` MUST be a stable read-only owner property and MUST NOT be modeled inside `ST_TestCaseStatus`.
- `TCC-003` Case-status fields MUST follow Table A.
- `TCC-004` Test Case execution-status fields MUST follow Table B.
- `TCC-005` Start, Abort, and Reset MUST be accepted through dedicated object-oriented request operations. The regular Test Case FB call performs the lifecycle processing.
- `TCC-006` The base-owned `Initialize(...)` MUST allow repeated explicit calls and clear transient owner state. It MUST validate the Parent reference and hierarchical identity before calling protected abstract `Configure()` without a return value. The hook configures owned children and implementation-specific state. Afterwards, protected virtual `CheckInitialization() : BOOL` MUST validate the resulting setup; overrides MUST include `SUPER^.CheckInitialization()`. The owner MUST remain uninitialized on any failure and expose a consistent status when Initialize returns. The PLC application initiates hierarchical initialization before regular execution using the initialization trigger defined in [Run Control](run-control-contract.md#initialization-trigger). Reinitialization during an active run is not a supported recovery path. Setup changes that invalidate initialization after Online Change require configuration reactivation or a PLC Cold Reset and renewed hierarchical initialization; normal PLC Stop/Start is insufficient. Configuration is immutable between initialization calls. Rejected input may be retained for diagnosis but MUST NOT enable execution.
- `TCC-007` The owning Test Suite MUST initialize each statically bound Test Case by passing itself as `Parent : I_TestSuite` and the local TestName separately before regular lifecycle execution starts. The Parent identity MUST already be established.
- `TCC-008` Preparation status fields MUST follow Table C and MUST NOT contain `Result` or `Inactive`.
- `TCC-009` Preparation MUST be controlled through its block interface by level-based `Enable` semantics and MUST report its execution state through `ST_PreparationStatus`.
- `TCC-010` Preparation timeout monitoring MUST remain the responsibility of the owning Test Case.
- `TCC-011` Test Case preparation MUST be modeled under `E_ExecutionStatus.Running`.
- `TCC-012` Assertions MUST use the block interface only and MUST NOT use a dedicated assertion structure in framework v1.
- `TCC-013` Assertions MUST expose a public `Reset()` request method and an `Enable` FB input with a read-only `Enabled` property. All Assertions MUST expose read-only `EvaluationActive` and `ObservationActive` as their effective evaluation and observation gates. Assertions with separate observation and evaluation MUST expose an `Evaluate` FB input. Assertions MUST accept `ObserveFirstCycle` through Initialize, defaulting to `TRUE`, and expose it through a read-only property. It controls whether the first enabled call contributes an observation.
- `TCC-014` The decisive assertion result toward the owning Test Case MUST be the read-only `Result : E_Result` property exposed through `I_ResultProvider`.
- `TCC-015` AssertionBase MUST provide instance-owned `Initialize(Parent : I_TestCase, AssertionName, ResourceName := '', ObserveFirstCycle := TRUE) : BOOL` and read-only IsInitialized. Initialize establishes Name, ResourceName and ObserveFirstCycle, exposed through read-only properties; the owning Test Case State remains a cyclic input. Initialization MUST NOT be part of I_Assertion.
- `TCC-016` Timeout deactivation MUST NOT be allowed. Timeout values MUST be greater than `T#0ms`; `T#0ms` MUST be treated as invalid.
- `TCC-017` A Test Case configuration DUT is internal implementation data and MUST NOT be published as a PLC/PC contract structure.
- `TCC-018` A Test Case status snapshot MUST NOT be exposed through a direct owner-instance symbol.
- `TCC-019` An application that publishes Test Case status MUST use an explicit data-oriented wrapper containing Test Case identity and `ST_TestCaseStatus`.
- `TCC-020` Assertion-generated `Failed` and `Error` event entries MUST be emitted by the assertion itself as one-shot entries using the event structures and classifications defined in `event-stream-contract.md`.
- `TCC-021` Aggregation and higher-level test-case result events MUST remain the responsibility of the owning Test Case and MUST use the event structures and classifications defined in `event-stream-contract.md`.
- `TCC-022` A Test Case preparation failure or preparation timeout MUST prevent test-state execution and MUST complete the Test Case with `E_Result.Error`.
- `TCC-023` Assertion `Reset()` MUST record a request. The next Assertion FB call MUST consume it, deterministically clear resettable assertion-local state, and suppress observation, evaluation, and assertion-generated events for that call. Configuration MUST remain unchanged.
- `TCC-024` Assertion `Enable = FALSE` MUST disable enable-controlled observation and direct evaluation, and MUST NOT implicitly clear latched assertion state. For Assertions with a separate `Evaluate` input, requested evaluation MUST remain possible independently of `Enable`.
- `TCC-025` Assertion `Enable = FALSE` alone MUST NOT overwrite a valid previously produced assertion `Result`; an independent `Evaluate` request MAY update it under the normal result-holding rules.
- `TCC-026` A pending Assertion reset MUST have priority over both `Enable` and `Evaluate` during the next FB call.
- `TCC-027` Assertion-generated event entries MUST use `E_OwnerScope.Assertion`.
- `TCC-028` Assertion-generated event entries MUST write the owning Test Case state provided through the assertion interface as `State`.
- `TCC-029` Text values rendered into assertion-generated event messages MUST be bounded to `40` visible characters per assertion value.
- `TCC-030` Preparation MUST NOT use a separate external control structure.
- `TCC-031` `PreparationStatus.Ready = TRUE` MUST mean that the current resource condition permits the Preparation to establish its required pre-test target condition and that the Preparation is not actively driving the resource. `Enable = FALSE` MUST NOT imply `Ready = TRUE`.
- `TCC-032` `PreparationStatus.Completed = TRUE` MUST mean that the Preparation target condition has been reached.
- `TCC-033` The owning Test Case MUST NOT treat `PreparationStatus.Ready = TRUE` without `Completed = TRUE` as a direct error while the Test Case is in `Preparing`; deterministic termination MUST be handled by the preparation timeout.
- `TCC-034` Preparation status flags MUST follow the execution-status mapping defined in Table D.
- `TCC-035` Preparation error and preparation timeout diagnostic events emitted by a Test Case MUST include the reported Preparation `State` in the event message.
- `TCC-036` Test Case `Ready = TRUE` MUST require both `State = E_State.Idle` and a positive implementation-specific ready release.
- `TCC-037` Before entering `Preparing`, the owning Test Case SHOULD evaluate `PreparationStatus.Completed` and skip the active Preparation phase if the preparation target condition is already reached.
- `TCC-038` Test Case finalization MUST use `E_State.Completing` for final Test Case result and Assertion aggregation publication before the Test Case enters `E_State.Completed`.
- `TCC-039` Test Case MUST emit exactly one final Test Case Result event for each controllable Test Case lifecycle from `Idle` to `Completed`.
- `TCC-040` Test Case MUST emit exactly one final Test Case Aggregation event for each controllable Test Case lifecycle from `Idle` to `Completed`.
- `TCC-041` Test Case MUST emit its final Result and Aggregation events while in `E_State.Completing` and before the final state-transition event into `E_State.Completed`.
- `TCC-042` Test Case `Name` MUST be the full hierarchical Test Case name `<runControlName>.<suiteName>.<testCaseName>` defined by `event-stream-contract.md`.
- `TCC-043` Initialize MUST read the Suite path through `Parent.Name` after validating the interface reference. The local TestName MUST be nonempty and contain no hierarchy separators. The unshortened full name MUST be validated by IsNameValid(Name, Depth := 3) before Configure. FName retains the length-limited name for diagnostics even when validation fails; truncation MUST NOT make the configuration valid.
- `TCC-044` Assertion Initialize MUST validate the Parent interface before reading Parent.Name, validate its three Owner segments, require a nonempty ASCII AssertionName, and reject a combined name exceeding the event Name capacity. Additional dots in AssertionName remain allowed. Initialize MUST build the unshortened Parent.Name + '.' + AssertionName and validate it through IsNameValid(Name, Depth := 4). FName MUST receive the length-limited full name before validation for diagnostics; truncation MUST NOT make the configuration valid. Initialize MUST NOT retain Parent. Failure returns FALSE, leaves IsInitialized FALSE and emits a Framework diagnostic if a publisher is bound. Observation, evaluation and assertion findings MUST remain disabled until successful initialization. Each Initialize call clears the base-owned result and accepted activity/history and queues a reset, including on failure. ResetAssertion MUST run only in the next regular FB call with its inputs bound, before any Assert processing; that reset call MUST NOT perform a check. Initialization MUST NOT call ResetAssertion directly. Normal Reset retains identity and initialization validity. The Test Case MUST check initialization success before execution; duplicate local names remain its responsibility.
- `TCC-045` Test Case lifecycle-capacity metadata MUST follow Table A and the lifecycle-capacity metadata rules in this contract.
- `TCC-046` `PreparationStateCount` MUST describe the maximum number of Preparation-state entries along one executable Test Case lifecycle path from `Idle` to `Completed`.
- `TCC-047` `TestStateCount` MUST describe the maximum number of Test-state entries along one executable Test Case lifecycle path from `Idle` to `Completed`.
- `TCC-048` `PreparationStateCount = 0` MUST represent a Test Case without a Preparation state, and `TestStateCount` MUST be greater than `0`. The concrete Test Case MUST set the protected count fields in Configure, not receive them from its caller. Both protected count fields and their public read-only properties use INT. Declaration initialization and every Initialize call set both fields to the named sentinel StateCountNotConfigured = -1. Configure MUST explicitly set both fields, including zero for no Preparation. CheckInitialization MUST distinguish unconfigured counts from range errors and validate 0 <= PreparationStateCount <= E_State.Running - E_State.Preparing and 1 <= TestStateCount <= E_State.Completing - E_State.Running. UINT status fields receive only in-range values through explicit conversion; otherwise they expose zero, with Uninitialized or ConfigError identifying the unusable owner.
- `TCC-049` `PreparationStateCount`, `TestStateCount`, and `AssertionCountTotal` MUST be valid when the Test Case reports `State = E_State.Idle` and `Ready = TRUE`.
- `TCC-050` `PreparationStateCount`, `TestStateCount`, and `AssertionCountTotal` MUST remain stable throughout the following Test Case execution through `E_State.Completed`.
- `TCC-051` A Test Case with branching lifecycle paths MUST publish the longest executable path, and a Test Case with an unbounded number of Preparation-state or Test-state entries MUST be treated as invalid for capacity-aware orchestration.
- `TCC-052` A MultiPrepareState Test Case MUST use a configured contiguous Preparation-state interval beginning at `E_State.Preparing`, MUST publish the current value as its Test Case `State`, and MUST keep `ExecutionStatus = E_ExecutionStatus.Running` throughout that interval.
- `TCC-053` `PreparationStateCount` MUST be the configured maximum number of Preparation-state entries and MUST remain inside the defined Preparation-state range. A configured count of `0` MUST retain its meaning of no Preparation phase.
- `TCC-054` A MultiPrepareState implementation MUST advance only inside its configured Preparation-state interval. A state outside that interval or a transition into the Test-state range before Preparation completion MUST follow the existing unhandled-state and framework-error finalization rules.

## Requirement Details

### Owner-local timeout handling

TestCaseBase owns execution, Preparation, Reset and Abort timeout detection and the resulting lifecycle reaction. ST_TestCaseConfig provides Timeout := T#5s, PreparationTimeout := T#2s, ResetTimeout := T#1s and AbortTimeout := T#1s, all TIME and strictly positive. The parent observes the child's normal status and result; it does not duplicate the child's timeout checks.

Execution and Preparation timeouts enter controlled error termination. A Reset timeout emits a Framework diagnostic, holds Error and leaves Reset through Completing to Completed instead of Idle. An Abort timeout likewise emits a Framework diagnostic and completes with Error without waiting further for CompleteAbort. The original abort deadline survives escalation from Aborting to AbortingFromError. Repeated requests for the current phase do not restart its deadline.

The base exposes read-only BOOL HasTimeout (execution timeout), HasPreparationTimeout, HasResetTimeout and HasAbortTimeout as stored diagnostics, not additions to I_TestCase or ST_TestCaseStatus. A detected timeout latches its corresponding flag through finalization and Completed. Explicit Initialize and actual entry into a new Reset clear the flags; a timeout during that Reset sets HasResetTimeout again. Getters perform no timer, event or lifecycle work. These flags do not replace timer-based lifecycle checks. Invalid time-source measurements are Framework errors, not expired-timeout flags.

### Shared orchestration interface

I_TestCase MUST extend the shared I_Orchestratable child view, which extends I_ResultProvider. It retains its typed Status : ST_TestCaseStatus property. Shared orchestration MUST use the read-only State, IsInitialized, IsReady, IsComplete, HasError and ReservedEntryCount properties, writable configuration ExecutionSlot, lifecycle requests and existing reservation methods. HasError MUST include configuration and runtime errors; Name and Result remain inherited identity/result observations. No additional common status snapshot or published PLC/PC data is introduced. I_TestCase inherits these members from I_Orchestratable; its declaration adds only the typed Status property.

### Initialization observation

I_TestCase exposes read-only IsInitialized. The composing Suite checks this after its Configure hook has completed; registering a Test Case does not require it to be initialized already. Initialize remains an instance operation, not an interface request.

Successful Initialize MUST leave the lifecycle in Uninitialized until the parent has reserved Reset capacity and submitted RequestReset. The regular Test Case FB call MUST NOT autonomously enter Reset. IsInitialized describes validated setup; it does not imply Idle or Ready. Failed initialization remains Uninitialized and cannot accept lifecycle processing.

CompleteReset MUST enter Idle only without a held Error. With a held Error, it MUST enter Completing and then Completed. Reset failures and Reset timeouts MUST NOT pass through Aborting or AbortingFromError; they retain Error through finalization. A later explicit Reset remains possible.

TestCaseBase.IsReady requires successful initialization, valid configuration, Idle and no held Error. A concrete Test Case with additional start prerequisites MUST override IsReady and combine SUPER^.IsReady with its scenario-specific permission. Without additional prerequisites, the base getter is sufficient. The status snapshot MUST expose that same effective readiness.


### Table A: `ST_TestCaseStatus` fields

| Field | Type | Default |
| --- | --- | --- |
| `Execution` | `ST_TestCaseExecutionStatus` | field defaults of nested structure |
| `PreparationStateCount` | `UINT` | `0` |
| `TestStateCount` | `UINT` | `0` |
| `AssertionCountTotal` | `UINT` | `0` |
| `AssertionCountPassed` | `UINT` | `0` |
| `AssertionCountFailed` | `UINT` | `0` |
| `AssertionCountError` | `UINT` | `0` |
| `Runtime` | `TIME` | `T#0ms` |

### Lifecycle-capacity metadata

`PreparationStateCount` and `TestStateCount` describe the maximum number of entries into the corresponding state categories during one controllable Test Case lifecycle. They count state entries along an executable path, not merely distinct numeric state values.

For a branching Test Case, the published values describe the longest executable path. Finite repeated state sequences are included according to their maximum number of entries. An unbounded loop cannot provide stable capacity metadata and is therefore invalid for capacity-aware orchestration.

`AssertionCountTotal` describes the number of Assertions owned by the Test Case for the same execution. These three values are structural execution metadata. They are published cyclically in the Test Case status view, become valid with the executable `Idle AND Ready` state, and remain stable until the execution reaches `Completed`.

### Naming

The Test Case `Name` is the full hierarchical Test Case identity used in event entries:

```text
<runControlName>.<suiteName>.<testCaseName>
```

The owning Test Suite calls `Initialize(Parent, TestName, Config)`, passing itself through `I_TestSuite` and the local Test Case name separately. The Test Case validates the Parent reference, reads `Parent.Name`, and checks both name components and their combined length before calling Configure; silent truncation MUST NOT enable execution. It exposes the full identity through the read-only Name property inherited by I_TestCase from I_ResultProvider. The Parent reference is not retained. ST_TestCaseConfig contains timeouts, not Name. Assertion identities use the full Test Case name as their parent path. External HMI or PC status uses an explicit data-oriented wrapper combining identity and ST_TestCaseStatus.

Assertion event names passed to Assertion blocks follow:

```text
<runControlName>.<suiteName>.<testCaseName>.<assertionName>
```

For Assertion events, everything after the third dot belongs to the assertion-local name. AssertionBase validates the Parent identity and combined length during Initialize, without applying the Owner segment restrictions to additional dots in the assertion-local name. The event-stream consumer can use the first three path segments to derive the owning Test Case.

Run Control, Suite, and Test Case name segments must follow the hierarchy and character rules from `event-stream-contract.md`. Invalid Test Case names are configuration errors for the owning implementation path.

### Table B: Test Case execution-status fields

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
| `PreparationActive` | `BOOL` | `FALSE` |
| `TestActive` | `BOOL` | `FALSE` |

### Table C: Preparation status fields

| Field | Type | Default |
| --- | --- | --- |
| `State` | `E_State` (`INT` base) | `E_State.Uninitialized` |
| `ExecutionStatus` | `E_ExecutionStatus` (`INT` base) | `E_ExecutionStatus.Unknown` |
| `Ready` | `BOOL` | `FALSE` |
| `Running` | `BOOL` | `FALSE` |
| `Completed` | `BOOL` | `FALSE` |
| `Error` | `BOOL` | `FALSE` |

### Table D: Preparation execution-status mapping

| Preparation condition | `ExecutionStatus` | Required flags |
| --- | --- | --- |
| No active preparation work, target condition not reached, and ready for execution | `E_ExecutionStatus.Idle` | `Ready = TRUE`, `Running = FALSE`, `Completed = FALSE`, `Error = FALSE` |
| No active preparation work, target condition not reached, and not ready for execution | `E_ExecutionStatus.NotReady` | `Ready = FALSE`, `Running = FALSE`, `Completed = FALSE`, `Error = FALSE` |
| Actively driving the resource toward the target condition | `E_ExecutionStatus.Running` | `Ready = FALSE`, `Running = TRUE`, `Completed = FALSE`, `Error = FALSE` |
| Target condition reached | `E_ExecutionStatus.Completed` | `Running = FALSE`, `Completed = TRUE`, `Error = FALSE` |
| Preparation cannot continue meaningfully | `E_ExecutionStatus.Error` | `Running = FALSE`, `Error = TRUE` |

### Preparation execution semantics

`ST_TestCaseExecutionStatus` is intentionally aligned with `ST_ExecutionStatus` and extends it through an explicit field copy with test-case-specific execution flags.

For non-state-machine-based preparations:

- while `Enable = TRUE`, the preparation MAY establish the target state over one or more cycles,
- if `Enable = FALSE`, the preparation performs no resource actions and continues reporting the currently observed target, readiness and error conditions; disabling does not force `Ready = TRUE`.

For state-machine-based preparations:

- the internal preparation state machine runs only while enabled,
- if `Enable = FALSE` before the target state is reached, the preparation returns to performing no action on the resource and its internal state machine returns to its executable base state.

The standard Preparation block interface is:

- `Enable : BOOL` as level-based execution and resource-ownership signal from the owning Test Case,
- `Status : ST_PreparationStatus` as structured status returned to the owning Test Case.

`Ready` describes whether the resource is currently in a condition from which this Preparation can establish the required pre-test target condition. It is not merely an indication that the FB is callable or disabled, and it does not mean the target has already been reached. The existing status mapping suppresses `Ready` during active preparation work. `Enable` grants permission to perform that work; removing it does not make an unsuitable resource condition suitable.

When no error is present, no preparation work is active and the target condition has not been reached, `Ready` MUST distinguish the execution status: `Ready = TRUE` maps to `E_ExecutionStatus.Idle`; `Ready = FALSE` maps to `E_ExecutionStatus.NotReady`. `Idle` with `Ready = FALSE` is not a valid combination. The initial or explicitly reset snapshot retains `E_ExecutionStatus.Unknown` until observations are supplied; it is not a reported `NotReady` observation.

`Running = TRUE` indicates that the Preparation is actively trying to establish the target condition. `Completed = TRUE` indicates that the target condition has been reached. Once `Completed = TRUE`, the Preparation must not perform further preparation work in the current preparation phase unless keeping the reached condition stable is explicitly part of the preparation behavior.

`Ready = TRUE` and `Completed = TRUE` may be set at the same time when the target condition has been reached and no further active preparation work is required. `Running = TRUE` is exclusive to `Ready = TRUE` and `Completed = TRUE`.

If `Enable = FALSE` while the Preparation is running, it MUST stop active resource manipulation and continue observing the resource. With no error and the target not reached, it reports `Idle` with `Ready = TRUE` only if the current resource condition permits a new preparation; otherwise it reports `NotReady` with `Ready = FALSE`. If the target condition has already been reached, it reports `Completed = TRUE`. Disabling is neither a resource-cancellation command nor a lifecycle Reset.

After the Preparation phase has completed and the Test Case has entered its test states, the behavior under test may move the resource away from both the pre-test target and the conditions in which the Preparation can start. The disabled Preparation may consequently report `Ready = FALSE` and `Completed = FALSE`. These observations MUST NOT by themselves restart Preparation, block test execution or change the Test Case result. Preparation status remains observational in this phase; test-specific Assertions evaluate the behavior under test.

While the owning Test Case is in `Preparing`, `PreparationStatus.Completed = TRUE` is the normal transition condition into the test states. `PreparationStatus.Error = TRUE` is a preparation error condition. Other non-final states, including `Ready = TRUE` with `Completed = FALSE`, keep the Test Case in `Preparing` until the preparation completes, errors, or the Test Case-owned preparation timeout expires.

When a Test Case starts and the Preparation already reports `Completed = TRUE`, the owning Test Case skips the active `Preparing` phase and enters the test states directly. This prevents enabling active Preparation behavior when the resource is already in the required target condition.

### Owner interactions

- Test Case is orchestrated by a containing Test Suite or equivalent higher-level invocation context.
- Test Case owns the interpretation of Preparation status and the supervision of Preparation timeout.
- `ST_TestCaseStatus` is a mandatory typed snapshot returned by the owner Status property. Copying that snapshot into a global HMI or PC view is optional and must use an explicit data-oriented wrapper; owner instance data is never published.
- Test Case `Ready` may include implementation-specific startability conditions such as `PreparationStatus.Ready`.
- Test Case owns aggregation of Assertion results. Once initialized, it continues collecting existing Assertion results and refreshing status counters in Completed without executing test or Assertion processing again. The completed owner result and previously emitted event payloads remain unchanged.
- Assertion-generated one-shot failed/error events remain assertion-owned, while higher-level scenario and aggregation events remain Test Case-owned.
- Test Case uses the event structures and classifications defined in `event-stream-contract.md` when emitting test-case-level events.
- The Test Case base integrates lifecycle processing, lifecycle events, state-transition events, preparation runtime accumulation, and preparation-timeout detection.
- The concrete Test Case owns Preparation and Assertion calls and supplies combined Preparation status and contract-conforming Assertion aggregation through the base-defined extension points. The base coordinates final Test Case Result and Aggregation events within owner finalization.
- Test Case emits test-case-level events with `E_OwnerScope.TestCase`, including events emitted by its integrated base implementation.

### Assertion interface semantics

Assertion configuration is established by the owning Test Case during its initialization. Initialize establishes read-only Name and requests reset of assertion-local state on the next FB call; ResourceName and ObserveFirstCycle are Initialize parameters, defaulting to '' and TRUE respectively, and are exposed through read-only properties. A normal Assertion reset retains identity, initialization validity and settings. I_Assertion extends I_ResultProvider without additional members; initialization and reset remain concrete instance APIs.

The standard cyclic input order inside each declaration group is:

1. `Enable`
2. `Evaluate`, where supported
3. `State`
4. assertion-specific checked values and limits

`State` means the current owning Test Case state, not an Assertion lifecycle state. `Name` exposes the length-limited hierarchical Assertion event name assigned by AssertionBase.Initialize. It may contain an invalid diagnostic name after rejected initialization; IsInitialized indicates validity.

Read-only `Enabled` mirrors the accepted Enable input. Public read-only `EvaluationActive` exposes the effective evaluation gate for the current/last FB call and is FALSE during a reset call. It remains observable until the next FB call. Public read-only ObservationActive controls observation. EvaluationActive delegates to ObservationActive in the common base. Both getters are side-effect-free, use call-stable input/history values, and must preserve reset suppression.

Observation uses `Enable AND (ObserveFirstCycle OR EnablePrevious)`, suppressed during reset. Direct evaluation follows ObservationActive. The read-only ObserveFirstCycle property reflects the Initialize parameter, which defaults to TRUE; Reset retains its configured value. FALSE skips the first enabled call after startup, reset, or a disabled call. The body captures the previous call's Enable before accepting new inputs; a reset call contributes FALSE. History stays unchanged during and after Assert until the next FB call. Explicit evaluation uses the accepted Evaluate input independently of Enable and ObserveFirstCycle, unless reset takes priority. The inherited ObservationActive independently controls observation, including ObserveFirstCycle. Family-specific initial sampling and history remain family-owned. The gate is not proof of valid checked data or a newly produced result.

Assertion results are held until reset, subject to the established result-escalation rules. Disabling an Assertion does not clear or de-escalate its result. An explicit `Evaluate` request may still evaluate stored observations while `Enable = FALSE`.

`Reset()` only records a request; the current `Result` remains unchanged until the next FB call processes it. That call resets the shared and specific resettable state and suppresses observation, evaluation, and finding events. Reset is not a cyclic input and does not erase configuration.

Text values rendered into assertion messages are diagnostics only. The preview limit applies to the rendered value in the message, not to the actual checked value.

### Assertion-specific text length constants

| Symbolic constant | Value | Description |
| --- | ---: | --- |
| Assertion value preview | `40` | Max visible chars of one assertion value in event messages. |

### Test Case orchestration metadata and reservations

I_TestCase exposes writable ExecutionSlot for initialization-time assignment by the Suite configuration builder, and read-only ReservedEntryCount for the Test Case's actual event reservation. These members are PLC-side orchestration data, not additions to the published ST_TestCaseStatus snapshot. ExecutionSlot MUST remain unchanged during execution.

I_TestCase provides ReserveExecutionEvents() : E_EventReservationResult, ReserveResetEvents() : E_EventReservationResult and FreeReservedEvents() : BOOL. The Test Case calculates the required budget and headroom internally, using the capacity rules in test-suite-contract.md and the corresponding library parameters. Execution budget uses AssertionCountTotal; no MaxAssertionCount setting or additional count-limit guard is introduced. The existing requirement for complete, stable metadata at Idle AND Ready remains binding.

Reservation methods MUST execute immediately without changing lifecycle state. They MUST use the Event Buffer reservation operations to update the Test Case's local contribution and the global aggregate together. Rejection MUST leave both unchanged. Reserving adjusts the existing contribution to the required total rather than adding that total repeatedly; release frees the entire own contribution and succeeds when it is already zero.

ReserveExecutionEvents and ReserveResetEvents return E_EventReservationResult: Accepted for a successful update, WaitingForCapacity only for temporarily insufficient capacity, and Error for an invalid prerequisite or a budget that cannot fit. They MUST reject an uninitialized Test Case with Error and a Framework error event, without a separate diagnostic latch. The Orchestrator MUST check IsInitialized before reservation. Capacity waiting remains silent; the Orchestrator may retry under the owning Suite's timeout supervision. Error MUST enter the caller's error path instead of being retried as capacity waiting. The permanent capacity check MUST include the owning Suite's corresponding fixed reservation and shared headroom; it uses the actual publisher Capacity, not the configured array length. Other concurrently held reservations affect temporary availability, not individual configuration validity.

The Orchestrator controls reservation timing: execution reservation before Start, release on missing Start reaction or observed Completed, Reset reservation before Reset, and release on Idle AND Ready. Active execution and Abort retain the reservation through finalization. The Test Case MUST NOT silently zero ReservedEntryCount in lifecycle Reset. A parent task change from Reset to Idle does not itself release an unfinished Test Case reservation. However, the completed Suite's hierarchical FreeReservedEvents may release that contribution during Reset-error finalization before the Test Case itself completes. Cyclic calls continue so the Test Case can finish its own Reset or reach its own timeout. The accepted failure-path capacity tradeoff is defined in test-suite-contract.md.

During full hierarchical PLC-start initialization, Initialize MUST clear the Test Case's local reservation contribution together with its transient owner state. It MUST NOT call FreeReservedEvents or reject initialization because a contribution from the preceding run cannot be released against the newly initialized Event Buffer. Event Buffer initialization precedes this hierarchy rebuild; isolated reinitialization during regular execution is not a supported recovery operation. Normal lifecycle Reset and FreeReservedEvents retain their existing reservation responsibilities.

Event Buffer Reset is a separate destructive operation governed by event-stream-contract.md. Run Control attempts hierarchical reservation release before resetting the buffer regardless of release return values. An active Test Case without its required reservation remains a framework error for the parent Orchestrator; no transparent continuation of the interrupted event log is promised.

## Compatibility

This contract defines the producer-side Test Case, Preparation, and Assertion interaction structures only.

## Non-Requirements

This contract does not define:

- project-specific resource model,
- concrete assertion prototypes,
- event transport details,
- event structure or communication classifications.
