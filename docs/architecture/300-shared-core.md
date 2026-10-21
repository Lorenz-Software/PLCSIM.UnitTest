# Shared Core Architecture

This document describes shared core responsibilities and shared application semantics used by WPF, CLI, and access-profile integrations.

Use this document as the shared-core design entry point:

- `Shared Core Orchestration Boundary` defines ownership.
- `TestRun Monitoring Model`, `Action Availability Model`, and
  `Timeout And Recovery Policy` define the test-run behavior.
- `Service Integration Model` and `Event Intake And Result/Log Update Boundary`
  define how service evidence enters the shared core and how structured
  log/result updates are produced.
- `Representative TestRun Cases` explains behavior boundaries for implementation
  and regression review; the table is not a pending delivery plan.

## Shared Core

The shared core should contain common application behavior that is not specific
to WPF or CLI presentation.

It should own or coordinate:

- planned-run validation semantics,
- lifecycle state and action availability,
- result interpretation from UnitTest evidence,
- diagnostics classification and common diagnostic context,
- artifact coordination and output state,
- access-profile-independent orchestration,
- testable behavior that can run without Siemens installations where practical.

Configuration validity may have shared and application-specific parts. The
shared core should contain the common base where useful, while WPF and CLI can
retain application-specific validation and presentation behavior.

## Activity Trace Capture Ownership

The bounded UnitTest activity trace is observational infrastructure for later
on-demand evidence analysis. Trace capture must not control workflow state,
commands, communication, timeouts, recovery, or result interpretation. Capture
is best effort: a rejected entry or internal trace failure must not interrupt
the producing operation.

Core components record factual activities through a source-independent trace
sink. The component that owns the observed logic also owns the trace trigger.
For example, EventStream processing records classified PLC events and RunEpoch
transitions, while lifecycle monitoring, diagnostic reporting, access-health
handling, and command execution record their respective observations. The
workflow orchestrator records only session boundaries and orchestration facts
that it owns; it is not a central trace-forwarding layer.

A thread-safe, session-bound correlation context supplies shared application,
communication, lifecycle-observation, and EventStream identifiers. Producers
provide more specific correlations that they own, especially per-event
`RunEpochId` and command `OperationId`. Source-provided values override shared
context values. Correlation and trace retention are factual capture concerns;
relevance and causal interpretation belong exclusively to the later analyzer.

A requested PLC start is recorded as `PlcStart` before invoking the access
profile, independently of acceptance or a RUN observation. Its completion uses
the same operation and lifecycle identifiers. The first subsequent RUN
observation retains that startup window; an externally observed RUN without a
pending start request opens a new window. `PlcStart` is distinct from the
UnitTest `Start` command. These correlations do not change command execution,
PLC-state monitoring, or timeout handling.

## Evidence Analysis Responsibilities

Evidence analysis uses one immutable activity-trace snapshot but separates
three purposes. Run analysis evaluates exactly one `RunEpoch` and owns UnitTest
result propagation and Run-specific completeness. PLC lifecycle analysis
evaluates one concrete, correlated startup or lifecycle-command reaction window.
Communication health analysis evaluates the latest health episode for the
active communication context: access health, revalidation, invalidation,
EventStream contract state, sequence gaps, lost events, EventBuffer progress,
and bounded-trace retention. Earlier adverse episodes may be retained as
non-status-affecting history when they explain the current communication
context.

Run analysis preserves the result reported by each PLC owner and evaluates it
separately from the effective result derived by the application. A PLC-reported
parent result is not rewritten when retained child evidence contradicts it.
Instead, the report shows both values and identifies the result-contract
conflict. This distinction also applies when a PC-side observation timeout has
already made the application result terminal: later PLC completion evidence may
be correlated for diagnosis, but it does not retroactively replace the PC-side
timeout result.

Completeness follows the selected focus. A globally evicted trace prefix makes
the communication-health view incomplete, but does not invalidate a PLC
lifecycle window that is fully retained. An eviction that may have removed the
start of the selected lifecycle window does make that focused analysis
incomplete. RunControl lifecycle conflicts belong to the correlated PLC
lifecycle analysis; EventStream communication-contract conflicts belong to the
communication-health analysis. Missing lifecycle correlation is retained as
an evidence-completeness problem rather than silently reclassifying the PLC
conflict as a communication failure.

Run-wide confidence and owner-local evidence completeness are separate. Event
loss, an invalidated observation, or a missing Run boundary can make the whole
Run analysis incomplete. Missing Result, Aggregation, or Completion evidence is
attributed only to the directly affected owner and its ancestors; complete
unrelated branches are not described as missing evidence. Parent evidence can
close the evaluation window for a child branch, but it cannot fabricate the
missing child event.

Lifecycle owner localization uses structured PLC-event ownership. Reports may
name both the directly affected owner and the last observed subordinate owner,
but the latter is only a localization hint and is never presented as a proven
PLC-internal cause. Analysis remains observational and does not change trace
capture, workflow behavior, access-profile contracts, or PLC communication.

Communication-health conclusions are limited to what the retained observations
prove. A sequence gap, lost-event counter transition, access invalidation, or
observed EventBuffer progress is factual evidence. A suggested PLC integration
check, such as verifying unconditional cyclic EventBuffer request processing,
is guidance rather than a claimed root cause. Sparse observations do not prove
that a condition persisted for the complete interval between them. Raw worker
logging remains the fallback when the structured activity trace cannot localize
an API, IPC, or worker failure.

## Shared Core Orchestration Boundary

Shared core orchestration is the common application logic used by WPF and CLI to
coordinate a planned UnitTest run without depending on UI behavior, CLI process
behavior, Siemens APIs, or concrete access-profile internals.

It owns the application-level test run meaning:

- whether the current planned run is ready for preparation,
- whether the runtime and UnitTest communication context are trusted enough for
  a run action,
- whether `Start`, `Abort`, `Reset`, cleanup, or monitoring restart are allowed,
- whether an application-triggered run or PLC-side-started run is currently
  being observed,
- whether the observed run is starting, active, finishing, completed, aborted,
  timed out, invalidated, or updating the shared log/result model,
- which timeout or cancellation condition changed the test-run state,
- when collected evidence is stable enough to update the shared UnitTest log
  model and notify WPF/CLI consumers through events or callbacks.

The application layer owns presentation and interaction policy:

- WPF decides how shared states, available actions, progress, and diagnostics
  are shown to the engineer.
- WPF decides which manual action the engineer requested, but not the shared
  semantics of that action.
- CLI decides how command-line options, config files, process cancellation,
  console output, and exit behavior are mapped to shared orchestration actions
  and results.
- WPF and CLI may add application-specific validation and presentation details,
  but they should not duplicate test-run state machines with different
  semantics.

The access profile layer owns concrete capability binding and communication:

- loading and validating the selected access profile,
- creating the functional service instances,
- managing profile-owned resources and cleanup,
- translating concrete runtime/API failures into normalized diagnostics,
- keeping vendor API object models and version-specific behavior behind the
  access-profile boundary.

Functional services own operation execution and observations, not application
test-run decisions:

| Service | Provides | Does not decide |
| --- | --- | --- |
| `IPlcProjectService` | Project open/prepare/compile/download operation outcomes, progress, and diagnostics. | Whether the application workflow may start a UnitTest run. |
| `IPlcControlService` | Runtime target preparation, PLC start/stop state, target validity, and diagnostics. | Final UnitTest run state or result meaning. |
| `IUnitTestLifecycleService` | RunControl command outcomes, readiness/progress/status observations, lifecycle context validity, and diagnostics. | Application-level action availability, shared log/result updates, or final result meaning. |
| `IUnitTestEventStreamService` | Event-stream evidence delivery, stream context validity, buffer-empty observations, release/acknowledgement outcomes, and diagnostics. | Event-to-run assignment, capture/evidence/result interpretation, or application test-run state. |

The shared core observes normalized access-health state from the loaded profile
and functional services. `AccessHealth` describes whether an application-facing
context is not established, healthy, requires revalidation, invalidated, or
disposed. It is separate from service progress state and from diagnostics.
`RunWorkflowService` publishes the current health snapshot, while
`WorkflowActionGateEvaluator` uses it as one input for action availability.

EventStream Processing is a separate shared-core concern. `TestRunMonitor` uses
lifecycle observations, EventStream Processing outputs, and timeout policy to
coordinate the active application test run. EventStream Processing interprets
event evidence and updates the shared UnitTest log/result model. Shared core
consumers are notified through application-facing events, callbacks, or
observable state updates.

`EventStreamProcessor` opens the test-run epoch from the exact RunControl
`Starting` event anchor and materializes its event evidence as `UnitTestRunEpoch`.
`TestRunMonitor` consumes those snapshots and lifecycle observations to decide
when monitoring is terminal, timed out, invalidated, reset, or stopped; it does
not open epochs from lifecycle status projections.

## TestRun Monitoring Model

TestRun Monitoring is the shared-core state model for one application-level
UnitTest execution cycle. It coordinates lifecycle command outcomes, runtime
trust, event-stream observation readiness, timeout/cancellation decisions, and
application-visible state changes. It does not interpret the UnitTest result
itself.

The implementation owner is `TestRunMonitor`. The monitor owns timeout
scheduling and elapsed-time state through `TimeProvider`. It consumes lifecycle
updates and `EventProcessingResult` values, emits monitor events and diagnostics,
and exposes current monitoring/timing state for workflow publication.

The shared core manages exactly one active test-run epoch at a time. Parallel
epoch tracking is intentionally out of scope. `EventStreamProcessor` opens that
epoch only from the exact RunControl `StateTransition` to `Starting` and includes
the anchor as its first event. `TestRunMonitor` consumes processor snapshots; it
does not create epochs from lifecycle projections. Previously collected evidence
is either already reflected in the shared UnitTest log/result model or classified
as incomplete, stale, invalidated, or diagnostic before the new epoch becomes
active.

Replacing the active run-epoch working state must not require synchronous log
writing to finish. When an epoch reaches a processing boundary, the shared core
should create a separate immutable snapshot/processing instance for the data
that still needs UnitTest log/result model updates. The active epoch instance can
then be reinitialized for the next boundary while asynchronous snapshot
processing continues independently.

PLC/runtime startup is part of the test-run precondition model. UnitTest
RunControl actions require a started PLC/runtime and trusted lifecycle and
event-stream communication. Event-stream reading should be active before a
UnitTest run is started or accepted as PLC-side-started, because short UnitTest
runs can produce relevant events faster than the application can safely attach
after the fact.

The model is used by both WPF and CLI:

- WPF uses it to enable/disable manual actions and present the current
  operation state.
- CLI uses it to drive unattended execution and deterministic process outcome
  mapping.

### TestRun States

| State | Meaning |
| --- | --- |
| `NotReady` | The selected setup is not sufficiently validated for UnitTest run preparation or execution. |
| `ReadyForPreparation` | Configuration/profile prerequisites are valid enough to start preparation. |
| `Preparing` | Project preparation, runtime provisioning, transfer, or communication validation is running as part of the composed prepare workflow. |
| `Prepared` | The runtime target has the transferred project state and the shared core can proceed toward PLC startup or communication validation according to workflow mode. |
| `RuntimeStarting` | The runtime/PLC is being started and communication trust is not established yet. |
| `MonitoringReady` | Runtime/PLC is started, lifecycle communication is trusted, event-stream reading is active, and no run is currently assigned to a new observation boundary. |
| `StartingRun` | A UnitTest start request was issued and the shared core is waiting for accepted/rejected/active/timeout/unclear lifecycle evidence. |
| `RunActive` | A run is active or plausibly active. Event-stream reading was already active before this state; the shared core now assigns delivered evidence to the active run boundary. This covers application-triggered and PLC-side-started runs. |
| `FinishingRun` | RunControl reached terminal lifecycle evidence or abort/failure evidence and the shared core is waiting for completion stabilization or timeout. |
| `Completed` | The test run is operationally completed. EventStream Processing may still update the shared UnitTest log/result model with final evidence interpretation. |
| `AbortingRun` | An abort request was issued and the shared core is waiting for a defined abort outcome, timeout, or invalidation. |
| `Resetting` | A reset request was issued and the shared core is waiting until the UnitTest lifecycle and communication context are reusable or failed/unclear. |
| `TimedOut` | A test-run timeout expired and no further waiting can change the current cycle without explicit recovery. |
| `Invalidated` | Runtime, lifecycle, or event-stream context trust was lost before the current test run could finish normally. |
| `CleaningUp` | Shared core is coordinating release of application-visible test-run resources and profile/service cleanup. |

The state names are conceptual. Implementation may split or combine internal
states if the externally visible semantics remain equivalent.

`MonitoringReady` does not mean that a UnitTest run is active. It means the PLC
is running, the UnitTest communication context is trusted, and the event stream
is already being drained/acknowledged so buffered evidence does not accumulate
unnecessarily before a run boundary is opened.

### Normal Transitions

| From | Trigger / Evidence | To |
| --- | --- | --- |
| `NotReady` | Setup/profile validation succeeds enough for preparation. | `ReadyForPreparation` |
| `ReadyForPreparation` | WPF/CLI starts prepare workflow. | `Preparing` |
| `Preparing` | Project, runtime target, transfer, and required setup steps complete. | `Prepared` |
| `Prepared` | WPF/CLI starts PLC/runtime. | `RuntimeStarting` |
| `RuntimeStarting` | Runtime/PLC is running and UnitTest lifecycle/event-stream communication is trusted; event-stream reading has started. | `MonitoringReady` |
| `MonitoringReady` | Application requests UnitTest start. | `StartingRun` |
| `MonitoringReady` | Shared core observes a PLC-side-started run according to configured mode. | `RunActive` |
| `StartingRun` | Start accepted and active lifecycle evidence is observed. | `RunActive` |
| `StartingRun` | Start rejected, failed, timed out, or remains unclear. | `TimedOut`, `Invalidated`, or `MonitoringReady` with diagnostics depending on evidence. |
| `RunActive` | RunControl terminal lifecycle evidence is observed. | `FinishingRun` |
| `RunActive` | WPF/CLI requests abort. | `AbortingRun` |
| `StartingRun`, `RunActive`, or `FinishingRun` | Reset is requested by application policy, observed from PLC-side behavior, or required as recovery. | `Resetting` or `Invalidated` depending on whether lifecycle/event-stream context remains trusted. |
| `FinishingRun` | Completion stabilization succeeds or waiting ends with defined timeout/invalid evidence. | `Completed` |
| `AbortingRun` | Abort reaches defined terminal lifecycle evidence or timeout/invalidation. | `FinishingRun`, `TimedOut`, or `Invalidated` |
| `Completed` | WPF/CLI requests reset for another run. | `Resetting` |
| `Resetting` | Reset reaches reusable lifecycle/communication state. | `MonitoringReady` |
| Any non-cleanup state | Cleanup requested or application closes. | `CleaningUp` |
| `CleaningUp` | Cleanup completes. | `NotReady` or a later reusable state defined by application policy. |

### Invalidation And Recovery Transitions

Runtime, lifecycle, or event-stream context invalidation can occur from service
diagnostics, runtime stop, project download, profile reload, communication loss,
contract mismatch, event-stream epoch reset, or cleanup.

Invalidation rules:

- shared core receives invalidation through normalized `AccessHealth` updates
  and other trusted service evidence, not through WPF control state,
- If invalidation occurs before a run starts, the cycle moves to `Invalidated`
  or back to `NotReady`/`ReadyForPreparation` according to the failed context.
- If invalidation occurs during `StartingRun`, `RunActive`, `FinishingRun`,
  or `AbortingRun`, the current observed run cannot become a
  clean completed run.
- Abort during `StartingRun` or `RunActive` is an intentional interruption path.
  The shared core keeps monitoring until a defined abort outcome, timeout, or
  context invalidation is reached.
- Reset during `StartingRun`, `RunActive`, or `FinishingRun` is not a normal
  clean-run transition. It either starts a controlled reset/recovery path or
  invalidates the current observation if the shared core can no longer trust
  lifecycle and event-stream context.
- Recovery requires an explicit shared-core transition such as restart
  monitoring, reset, revalidation, profile reload, or cleanup.
- A recovered context gets a new observation boundary before another run can be
  evaluated.
- The shared core does not keep multiple run epochs alive for later
  reconciliation. A new exact RunControl `Starting` anchor replaces the
  previous internal epoch working state. Previous evidence remains available
  only through the shared log/result model, diagnostics, or a separate immutable processing
  snapshot, not as a second active epoch.

### Terminal And Reusable States

Terminal for the current test run:

- `Completed`
- `TimedOut`
- `Invalidated`

Reusable for another run without full application restart only after the shared
core has verified lifecycle and communication trust:

- `MonitoringReady`
- `Prepared` when PLC/runtime startup must be repeated
- `ReadyForPreparation` when preparation must be repeated

`Completed` is not automatically reusable. The shared core still needs a
policy decision or action such as reset, cleanup, or start-next-run gating before
another run starts.

## Action Availability Model

Action availability is evaluated by the shared core from test-run state,
runtime trust, lifecycle evidence, event-stream state, pending operations,
configuration policy, and diagnostics. WPF and CLI consume the same availability
decision.

The shared core should expose more than a boolean. An action decision should be
able to represent:

- `Available`
- `Unavailable`
- `Pending`
- `RecoveryOnly`
- `Unsafe`

The decision should include a reason or diagnostic reference so WPF can explain
disabled or recovery-only actions and CLI can produce deterministic diagnostics.

### Readiness And Start Gate Evaluation

`IUnitTestLifecycleService` provides RunControl readiness, command-progress,
status, and context-validity evidence. The shared core interprets that evidence
as the application-level start gate.

Start is available only when all required conditions are true:

- runtime/PLC is running,
- lifecycle communication context is trusted,
- event-stream reading is active,
- RunControl readiness/start gate is positive, such as `Ready = TRUE`,
- no start, abort, reset, cleanup, or profile reload operation is pending,
- the workflow is not in `Preparing`, `RuntimeStarting`, `CleaningUp`, or another
  incompatible operation state,
- there is no unresolved invalidation or recovery condition that blocks a clean
  start request.

Start does not require a manually cleared previous run epoch. When the event
stream delivers the exact RunControl `Starting` anchor, the active run-epoch
working state is reinitialized according to the single-active-epoch rule. Previous data
must already be represented through the shared log/result model, diagnostics, or
an immutable processing snapshot.

### Action Availability Rules

| Action | Available when | Blocked or restricted when | Effect |
| --- | --- | --- | --- |
| Prepare | Setup/profile configuration is valid enough to bind services and no active run or cleanup operation is in progress. | Blocked during active run states, cleanup, profile reload, or unsafe runtime/service operations. | Starts project/runtime preparation workflow. |
| Start PLC | Runtime target is prepared or provisioned and no incompatible operation is active. | Blocked before preparation/provisioning; unsafe while cleanup or profile reload owns the context. | Starts runtime/PLC and moves toward trusted communication and `MonitoringReady`. |
| Start UnitTest | Runtime/PLC is running, lifecycle context is trusted, event-stream reading is active, RunControl ready evidence is positive, and no conflicting command is pending. | Blocked while start/abort/reset/cleanup/profile reload is pending, while communication is untrusted, or while RunControl is not ready. | Requests RunControl start and monitors the lifecycle reaction; the event stream independently opens the epoch from the RunControl `Starting` anchor. |
| Observe PLC-side run | Runtime/PLC is running, lifecycle context is trusted, and event-stream reading is active. | Blocked when context is untrusted or event-stream reading is not active. | Continuous processing opens an epoch from the same RunControl `Starting` event anchor without sending `Start`. |
| Abort | A run is starting or active and lifecycle communication can request abort. | Blocked without reachable lifecycle command access or while cleanup/profile unload owns the context. May be unavailable after stable terminal lifecycle evidence. | Requests controlled interruption and keeps monitoring until abort outcome, timeout, or invalidation. |
| Reset | Runtime/PLC is running and lifecycle communication is reachable enough to request reset. | Blocked only when runtime/PLC is not running, lifecycle command access is unavailable, or cleanup/profile unload owns the context. | Recovery/interruption action. Current run epoch cannot become clean completed; successful reset moves toward reusable communication state. |
| Restart monitoring | Runtime/PLC is still trusted enough to restart lifecycle/event-stream reading without replacing runtime access. | Blocked when target context changed, runtime access must be replaced, or revalidation/profile reload is required first. | Rebuilds monitoring context and creates a new observation boundary. |
| Reconnect runtime | Setup, profile session, prepared runtime context, and effective target exist. Stale/invalid lifecycle or event-stream health and stopped/unknown PLC state are valid recovery sources. | Blocked while another operation owns the workflow, required prepared-target context is missing, or profile health is unusable. | Invalidates an active/finalizing run as incomplete, asks the profile to revalidate technical runtime access in place and release stale communication, adopts the revalidated runtime context, and lets observed PLC readiness drive communication restart. |
| Stop PLC | Runtime is prepared and has a PLC configuration or running target that can be stopped. | Blocked only when runtime service is unavailable or cleanup/profile unload owns the context. | Stops PLC/runtime. Any active test-run observation becomes invalidated. |
| Cleanup | Application is closing, CLI execution ends, user requests cleanup, or recovery requires releasing profile/service resources. | May become pending/cancellation-driven while service operations are still running. | Releases shared/application-visible resources and delegates profile/service cleanup. |

Reset and Stop PLC are intentionally broad recovery actions. They may interrupt
an active or finishing run. Their availability does not imply that the current
run can still produce a clean completed result.

### Availability Consumers

WPF should use the shared action availability model to drive manual controls,
status indicators, recovery actions, and explanatory diagnostics.

CLI should use the same model to decide whether the configured unattended flow
can continue, should wait, should invoke recovery, or must fail with a
deterministic process outcome.

## Timeout And Recovery Policy

The host/shared operation coordinator owns overall operation deadlines and passes
their cancellation to functional services. Profiles own bounded vendor/API calls
and their truthful completion/containment results; they do not independently
replace the caller's overall policy. Shared core also owns test-run monitoring
deadlines. A timeout is not automatically
a UnitTest failure. It changes operation, test-run, evidence, or cleanup state;
EventStream Processing and result/output coordination then consume the available
evidence and diagnostics.

The timeout model distinguishes seven categories.

| Timeout type | Owner | Starts when | Ends when | On timeout | Recovery options |
| --- | --- | --- | --- | --- | --- |
| Overall operation deadline and bounded service calls | Host/shared coordinator for overall policy; profile for bounded vendor/API calls. | A concrete operation starts, such as project preparation, compile, download, PLC start/stop, or cleanup; internal reads/writes may have their own technical bounds. | The operation reaches a known completed, failed, canceled, or unclear boundary. | Coordinator cancellation and service diagnostics preserve dispatched-operation ownership and truthful outcomes. | Retry when safe, revalidate context, stop PLC, cleanup. |
| Lifecycle command transport timeout | Selected `IUnitTestLifecycleService`. | Shared core requests Start, Abort, or Reset. | The bounded external API write returns accepted, rejected, failed, cancelled, timed out, or unclear. | Command transport result is timed out or unclear; access health and workflow impact follow the trust of the write outcome. | Reobserve before retry, reset or abort if still meaningful, revalidate communication, stop PLC, cleanup. |
| Lifecycle command reaction timeout | Shared core `TestRunMonitor`. | A UnitTest command write was accepted and reaction monitoring starts. | A fresh RunControl observation satisfies the command-specific execution-status/signal expectation. | A warning diagnostic records the missing observed reaction; the transport result is not rewritten and run observation remains independently governed by event-stream evidence. | Continue observation, reset, revalidate communication, stop PLC, or cleanup according to current gates. |
| Test run timeout | Shared core TestRun Monitoring. | EventStream Processing publishes an epoch opened by the exact RunControl `Starting` anchor. | Monitoring leaves `RunActive`, including entry into `CompletionPending`, or recovery resolves the run. | Current test run becomes `TimedOut`; collected evidence is partial/incomplete/diagnostic and must not become a clean completed result. | Abort, reset, continue observing manually where policy allows, stop PLC, cleanup. |
| Final event timeout | Shared core `TestRunMonitor`. | Completion is detected and monitoring enters `CompletionPending`. | Final event-stream evidence resolves the run. | The run becomes `TimedOut` with incomplete evidence. | Inspect retained evidence and recover according to current gates. |
| Recovery / reconnect timeout | Shared core plus affected access profile/service. | A recovery operation starts, such as restart monitoring, revalidate technical runtime access and rebuild communication, revalidate contract context, or reload profile state. | Recovery establishes a trusted context or fails. | Recovery fails or context remains untrusted; an active/finalizing test run remains or becomes `Invalidated` and retained evidence stays incomplete. | Retry recovery, reload profile, reprepare, stop PLC, cleanup. |
| Cleanup / shutdown timeout | Shared core coordinates; access profile/services release resources. | Cleanup, application close, CLI shutdown, or forced release starts. | Resources are released or cleanup outcome becomes known enough to continue shutdown. | Cleanup diagnostic is recorded; shutdown must not block indefinitely. | Continue shutdown with cleanup failure diagnostics, force application-level release boundary where possible. |

### TestRun Timeout Boundary

Test run timeout is tied to the run epoch boundary, not to the trigger event or
to the moment the application writes `Start`.

For application-triggered runs:

- the Start command result covers only whether the external API call was
  accepted or failed,
- independent command-reaction monitoring observes lifecycle `Running` or
  `Completed` and reports a warning if no reaction is seen,
- test run timeout starts only when EventStream Processing opens the epoch from
  the RunControl `Starting` anchor.

For PLC-side-started runs:

- test run timeout starts when the RunControl `Starting` anchor is observed,
- no start-command timeout is involved.

`testRun` bounds active execution. Once completion is detected and monitoring
enters `CompletionPending`, `finalEvent` bounds waiting for the remaining final
event-stream evidence, including completion anchor and buffer-empty evidence.
These are separate configured deadlines, not one shared completion budget.

### Recovery Policy Rules

Recovery options are capabilities, not automatic obligations.

- WPF presents available recovery actions and lets the engineer choose, unless
  application-close cleanup policy requires automatic cleanup.
- CLI applies configured automation policy and must end with deterministic
  diagnostics and process outcome.
- Recovery actions that reset, stop, reload, or revalidate context can
  invalidate the current run epoch.
- A new exact RunControl `Starting` anchor reinitializes the active epoch working state.
- Untrusted runtime, lifecycle, or event-stream context must not be treated as
  normal continuation.
- Reset is the preferred recovery action when PLC/runtime is running and
  lifecycle communication is reachable enough to request reset.
- Cleanup is always the final application-level recovery boundary.

## Service Integration Model

Shared core orchestration consumes functional service updates through
application-facing abstractions. The services provide operation outcomes,
observations, evidence, context validity, and diagnostics. The shared core
turns those inputs into test-run state, action availability, timeout/recovery
decisions, and shared UnitTest log/result updates.

### Lifecycle Service Integration

`IUnitTestLifecycleService` provides RunControl lifecycle access:

- readiness/start-gate observations,
- Start, Abort, and Reset command outcomes,
- command progress and unclear/pending command states,
- lifecycle status/projection evidence,
- lifecycle context validity,
- lifecycle diagnostics.

The shared core requests lifecycle commands through this service and consumes
its outcomes and observations. The service does not decide application-level
states such as `StartingRun`, `RunActive`, `TimedOut`, or `Invalidated`.

Lifecycle observations should be delivered to the shared core in a way that
supports event/callback/observable integration for application-facing state
updates. If a concrete access profile must poll internally to observe lifecycle
state, that polling remains hidden behind `IUnitTestLifecycleService`.

### EventStream Service Integration

`IUnitTestEventStreamService` provides UnitTest event-stream access:

- event-stream context validity,
- active/faulted/stopped reading state,
- newly delivered event-stream evidence,
- event-buffer empty/non-empty observations,
- stream epoch and ordering evidence,
- release/acknowledgement outcomes,
- event-stream diagnostics.

Event-stream evidence is delivered push-based to the shared core through
application-facing updates. The shared core must not depend on WPF, CLI, or
shared orchestration code polling raw event buffers. If the selected access
profile must poll a concrete PLC/runtime API internally, that polling is an
implementation detail of the access profile or service.

Event-stream reading is expected to be active in `MonitoringReady`, before a
UnitTest run is started or accepted as PLC-side-started. Stale evidence can be
read, classified, acknowledged/released, and retained diagnostically without
being assigned to a new run epoch.

### Shared Core Outputs

Shared core orchestration should publish application-facing updates for:

- test-run state changes,
- action availability changes,
- shared UnitTest log/result model updates,
- diagnostics changes,
- recovery option changes,
- timeout/cancellation state changes.

WPF and CLI consume these updates differently, but the shared semantics are the
same. WPF maps them to manual workflow controls and structured views. CLI maps
them to unattended flow control, diagnostics, and process outcome.

### Service Fault And Invalidation Handling

Functional service faults are inputs to shared core decisions.

| Service input | Shared core responsibility |
| --- | --- |
| Lifecycle command transport timeout or unclear outcome | Evaluate access-health and recovery impact without synthesizing a PLC reaction. |
| Lifecycle command reaction timeout | Keep transport acceptance unchanged, publish the missing-reaction diagnostic, and choose recovery from current RunControl/access evidence. |
| Lifecycle context invalidated | Mark affected test run invalidated or require reset/revalidation according to current state. |
| EventStream reading faulted | Stop trusting event evidence for the active epoch, update action availability, and offer restart monitoring/reset/cleanup where applicable. |
| Event-stream epoch reset | Treat current epoch evidence according to EventStream Processing rules and create a new observation boundary when trusted. |
| Release/acknowledgement failure | Keep event-stream diagnostics and decide whether monitoring can continue, must retry, or must invalidate context. |
| Runtime/PLC state change | Reevaluate monitoring, action availability, and active test-run trust. |

Services report facts and diagnostics. The shared core decides application
state, recovery options, and which updates WPF/CLI receive.

## Event Intake And Result/Log Update Boundary

The PLC-side event buffer is the critical pressure point. It has bounded
capacity while the PLC can produce events quickly. Event intake and PLC-buffer
release must therefore stay independent from slower semantic processing, UI
updates, log writing, or artifact generation.

### PLC Buffer Relief Priority

The shared-core policy is that PLC-buffer relief has priority over semantic
processing, UI updates, log writing, and artifact generation. The concrete
hot-path intake and release behavior belongs to `IUnitTestEventStreamService`
and the selected access profile.

From shared-core perspective, PLC-buffer release means that event evidence was
safely captured on the PC side. It does not mean that the events were already
semantically interpreted, written to the UnitTest log, rendered in WPF,
summarized by CLI, or serialized to an artifact.

### Event Processing Path

EventStream Processing consumes captured batches and may run independently from
the intake path. It owns semantic interpretation, shared UnitTest log/result
model updates, diagnostics generation, and WPF/CLI update publication. This
processing must not block PLC-buffer release for already captured event entries.

### Boundary Handling

Captured batches carry enough context to let EventStream Processing interpret
them relative to the current observation and epoch. Boundary ownership belongs
to `EventStreamProcessor`: the exact RunControl `Starting` event opens the epoch
and the corresponding RunControl `Completed` event closes its inclusive sequence
range. Previously captured data can continue to be processed as immutable
batches or snapshots, but it is not kept as a second active epoch.

Boundary handling must preserve two properties:

- only one active run epoch is managed by the shared core,
- processing of earlier captured batches must not delay intake for the next
  PLC-buffer entries.

### Published Update Boundaries

The shared core publishes data through update boundaries. These boundaries are
not test-run states. They define what information becomes available to another
shared-core concern, WPF, CLI, diagnostics, or output coordination.

| Update boundary | Published data | Primary consumers |
| --- | --- | --- |
| Event intake batch captured | Immutable event batch, stream context, epoch/boundary context, sequence range, capture timestamp or monotonic capture order, minimal validation status, release/acknowledgement status, and diagnostics. | EventStream Processing. |
| Run epoch boundary detected | RunControl anchor event, inclusive sequence boundary, local epoch id, and previous epoch classification when applicable. | TestRun Monitoring and WPF/CLI state updates. |
| Test-run state changed | Previous state, new state, reason, triggering evidence reference, recalculated action availability, diagnostics, and recovery options. | WPF, CLI, diagnostics. |
| UnitTest log/result model updated | Changed RunControl/Suite/TestCase/AssertionFinding nodes, status/result per level, evidence completeness/consistency, diagnostics, and optional source batch references. | WPF structured process view, CLI summary, output/artifact coordination. |
| Terminal or recovery state reached | Terminal/recovery state, reason, current epoch classification, available recovery actions, diagnostic summary, and whether a writeable log snapshot exists. | WPF recovery workflow, CLI process outcome, output/artifact coordination. |

Application-facing updates should be event/callback/observable based. UI,
console output, log writing, and artifact generation must not block the
PLC-buffer intake path.

### UnitTest Log Write Trigger

Writing a UnitTest log is triggered by shared core policy when a writeable
UnitTest log/result snapshot exists. The trigger is not the same as PLC-side
RunControl status becoming completed.

Typical log-write trigger situations are:

- test run becomes operationally `Completed`,
- test run becomes `TimedOut`,
- test run becomes `Invalidated`,
- reset interrupts the current run epoch and a partial/diagnostic snapshot is
  available,
- cleanup or application close occurs while captured evidence exists,
- WPF explicitly requests writing/exporting the current snapshot,
- CLI reaches its configured final output step.

The log writer consumes a structured UnitTest log/result snapshot or immutable
processing snapshot. It must not consume the mutable active epoch working state
directly.

Log writing may produce:

- final logs for operationally completed runs with complete/consistent evidence,
- partial or incomplete logs for timeout, invalidation, reset, cleanup, or
  incomplete evidence,
- diagnostic logs when result meaning cannot be interpreted cleanly.

The log-write operation may run asynchronously and must not block PLC-buffer
intake or release. Completion or failure of log writing is reported back through
shared diagnostics and output state updates.

### Application-Facing Run Log View

WPF and CLI should not need to consume the raw event protocol for normal
operation. EventStream Processing updates a structured UnitTest log/result model
that represents the run at process-relevant levels:

```text
RunControl
  TestSuite
    TestCase
      AssertionFinding
```

Each visible level should expose at least:

- hierarchical name and display name,
- current lifecycle/result status such as `NotEvaluated`, `Passed`, `Failed`,
  or `Error`,
- evidence/completeness/consistency flags where they affect trust,
- diagnostic details for failures, errors, incomplete evidence, or invalidated
  context.

The WPF application should be able to present this model as a structured
overview, for example a tree view of RunControl, suites, test cases, and problem
findings. The raw event protocol remains available for diagnostics and
exceptional analysis, but it is not the primary user-facing workflow view.

## Representative TestRun Cases

The following cases define representative shared-core orchestration scenarios
for later tests and implementation review. They are conceptual cases, not C#
test code.

| Case | Initial state | Input / trigger | Expected test-run behavior | Expected action availability / recovery |
| --- | --- | --- | --- | --- |
| Prepare succeeds | `ReadyForPreparation` | WPF/CLI starts prepare workflow and services report successful project/runtime preparation. | State moves through `Preparing` to `Prepared`. | `Start PLC` becomes available; `Start UnitTest` remains unavailable until runtime and communication are trusted. |
| PLC starts and communication becomes trusted | `Prepared` | Runtime starts and lifecycle/event-stream communication becomes trusted; event-stream reading is active. | State moves through `RuntimeStarting` to `MonitoringReady`. | `Start UnitTest`, `Observe PLC-side run`, `Reset`, `Stop PLC`, and `Cleanup` follow their availability rules. |
| Start requested and accepted | `MonitoringReady` | WPF/CLI requests `Start`; the API call succeeds, lifecycle reaction is monitored independently, and the event stream later delivers the RunControl `Starting` anchor. | State moves to `StartingRun`; the event anchor opens the epoch and moves monitoring to `RunActive`. | `Abort`, `Reset`, `Stop PLC`, and `Cleanup` are recovery/control options; `Start UnitTest` is unavailable while active. |
| Start command transport fails or times out | `MonitoringReady` | The external command write does not return accepted before its bounded call timeout. | No PLC reaction is synthesized; state and health reflect the failed or unclear transport outcome. | Reobserve/revalidate, `Reset`, `Stop PLC`, or `Cleanup` are offered according to reachable services. |
| Start reaction is not observed | `StartingRun` | The write was accepted but no fresh compatible execution status/signal observation arrives within the reaction window. | A warning diagnostic records the missing reaction; the event-stream start anchor remains the authority for opening a run epoch. | Continue observing or use `Reset`, `Stop PLC`, or `Cleanup` according to current gates. |
| PLC-side-started run detected | `MonitoringReady` | The event stream delivers the RunControl `Starting` anchor without application `Start`. | EventStream Processing opens the epoch and monitoring moves to `RunActive`. | `Abort`, `Reset`, `Stop PLC`, and `Cleanup` follow the same active-run rules as application-triggered runs. |
| Short run produces many events | `StartingRun` or `RunActive` | Event-stream service delivers batches quickly while PLC buffer remains under pressure. | Intake captures immutable batches and releases PLC buffer without waiting for semantic processing. | UI/log/artifact updates must not block intake; diagnostics are raised only if trust or capture is affected. |
| Run completes normally | `RunActive` | RunControl terminal evidence and event-stream completion conditions are reached within test run timeout. | State moves to `FinishingRun`, then operational `Completed`; structured log/result updates are published. | `Reset`, `Stop PLC`, `Cleanup`, and start-next-run policy become available according to readiness and reset rules. |
| Test run timeout | `RunActive` or `FinishingRun` | Test run timeout expires before operational completion. | State becomes `TimedOut`; current evidence is partial/incomplete/diagnostic and cannot become clean completed. | `Abort`, `Reset`, continue observing manually where policy allows, `Stop PLC`, or `Cleanup`. |
| Abort active run | `StartingRun` or `RunActive` | WPF/CLI requests abort and lifecycle service reports defined abort/terminal evidence. | State moves to `AbortingRun`, then `FinishingRun` or terminal timeout/invalidation depending on evidence. | `Reset`, `Stop PLC`, and `Cleanup` remain recovery/control options. |
| Reset during active run | `StartingRun`, `RunActive`, or `FinishingRun` | WPF/CLI requests reset or reset is required by recovery policy. | Current run cannot become clean completed; state moves to `Resetting` or `Invalidated` depending on context trust. | On successful reset and trusted communication, state returns toward `MonitoringReady`; otherwise recovery/cleanup remains. |
| Event-stream context invalidated | `MonitoringReady`, `StartingRun`, `RunActive`, or `FinishingRun` | Event-stream service reports context invalidation, epoch reset, unrecoverable read fault, or incompatible contract context. | Active epoch evidence is invalidated or classified according to EventStream Processing rules; test run becomes `Invalidated` when active trust is lost. | `Restart monitoring`, `Reset`, `Revalidate`, `Stop PLC`, or `Cleanup` depending on target trust. |
| Release/ack failure | Any state with active event-stream intake | Event-stream service cannot release safely captured entries or reports unclear release state. | Shared core records diagnostics and decides whether monitoring can retry, continue, or must invalidate context. | `Restart monitoring`, `Reset`, `Stop PLC`, or `Cleanup` according to trust and service reachability. |
| Stop PLC during active run | `StartingRun`, `RunActive`, `FinishingRun`, or `MonitoringReady` | WPF/CLI requests `Stop PLC`. | Runtime stops; active test-run observation becomes invalidated unless no run was active. | `Start PLC`, revalidate communication, prepare again, or cleanup according to runtime state. |
| Cleanup while operation is active | Any non-cleanup state | Application closes, CLI ends, or user requests cleanup. | State moves to `CleaningUp`; service/profile cleanup is coordinated with timeout policy. | Shutdown continues with diagnostics if cleanup cannot complete cleanly. |
| Log write requested after terminal/recovery state | `Completed`, `TimedOut`, `Invalidated`, or cleanup path with captured evidence | Shared core policy or WPF/CLI requests writing a UnitTest log snapshot. | Log writer consumes structured snapshot asynchronously and reports output diagnostics/state updates. | Event intake is not blocked; WPF/CLI show output success/failure separately from test-run state. |

## Communication And Lifecycle Model

The UnitTest communication and lifecycle model is based on PLC-owned contracts
referenced by this repository.

At architecture level:

- RunControl commands are application requests, not final result evidence.
- RunControl status is readiness, progress, plausibility, lifecycle-completion,
  and diagnostic evidence.
- Event-stream evidence is the authoritative source for event-log completion,
  test results, aggregation evidence, and completeness evaluation.
- Shared core EventStream Processing interprets event-stream evidence
  independent from the concrete access technology.
- A concrete access profile provides the transport or PLC access path and maps
  technology-specific failures to shared diagnostics.

TestRun Monitoring and EventStream Processing are separate shared-core
concerns:

- `TestRunMonitor` coordinates the active observation cycle, including start,
  continuation, timeout, abort, reset, invalidation, and terminal monitoring
  boundaries.
- `EventStreamProcessor` owns the active `UnitTestRunEpoch`, its event stream,
  owner tree, framework event references, aggregation/finding state, and
  snapshots inside that observation cycle.

The detailed shared event-stream processing, observed-run reconstruction, and
neutral result interpretation concept is defined in
`docs/architecture/400-unittest-eventstream-processing.md`.

## Diagnostics And Error Handling

Diagnostics are split by responsibility.

`AccessProfilePluginLoader` owns diagnostics for:

- configured access profile not found,
- duplicate or ambiguous profile identities,
- missing profile plugin files or generic metadata,
- profile plugin metadata that cannot be read,
- generic plugin load failures before profile-specific initialization starts.

`IAccessProfilePlugin` and `IAccessProfileSession` own diagnostics for:

- profile-specific configuration that cannot be resolved,
- incompatible access profile metadata or plugin contract version,
- unsupported architecture, framework, runtime, or access API requirements,
- missing or unloadable external API dependencies,
- stale installation or registry evidence,
- internal technology binding selection failures,
- internal technology binding load or initialization failures,
- `IAccessProfileSession` creation and cleanup failures.

Functional service interfaces own operation-related diagnostics, such as:

- engineering project open, compile, target resolution, and download failures,
- simulation/runtime target, lifecycle, readiness, and cleanup failures,
- UnitTest communication contract, command, event-stream, release, and symbolic
  access failures.

### Foreign Profile Failure Boundary

Access-profile sessions and their functional services are foreign execution
boundaries from the shared core perspective. Expected operational failures stay
structured profile results and diagnostics. Shared core must not relabel or
duplicate an unsuccessful `OperationResult` as an unexpected exception.

An unexpected exception is normalized only around the direct profile or service
invocation. Result mapping, shared-core state changes, publication, gate
evaluation, and invariant checks remain outside that boundary so shared-core
defects are not misclassified as profile failures.

Cancellation is classified from the caller-owned cancellation request. An
`OperationCanceledException` raised after the caller requested cancellation
continues through the workflow cancellation path. Without that request, it is an
unexpectedly aborted foreign operation with an unclear outcome and is reported
as a failed operation. Overall operation deadlines and user-cancellation policy
are owned by the application/shared workflow execution policy rather than by
this exception boundary.

Profile-originated callbacks are containment boundaries in the opposite
direction. A shared-core processing failure must not escape into the profile,
binding, worker, or vendor event source. The workflow records the processing
failure and invalidates affected active observation evidence when it can no
longer be trusted. It does not synthesize profile health; only the profile owns
and publishes `AccessHealth`.

Cleanup remains an owner-driven, best-effort sequence in which one failed
release step must not prevent later release steps. Bounded shutdown,
application-exit coordination, and forced termination of owned workers are
separate lifecycle responsibilities.

### Generic Loader Diagnostics

Generic loader diagnostics are limited to the profile plugin boundary. They
should explain why the requested profile plugin could not be found, identified,
read, matched, or loaded far enough to hand over to profile-specific
initialization.

Typical generic loader diagnostic sources are:

- configured profile identity,
- configured plugin search locations,
- discovered profile plugin candidates,
- profile metadata readout,
- profile contract version compatibility,
- generic assembly/plugin loadability before profile code executes.

The generic loader should not classify vendor-specific installation, registry,
runtime, API, or internal binding failures. Those belong to the access profile
plugin because only the profile plugin understands its own environment and
internal composition.

### Access Profile Diagnostics

Access profile diagnostics start after `AccessProfilePluginLoader` has selected and
loaded a profile plugin far enough to call its profile-specific validation or
`IAccessProfileSession` creation behavior.

Typical access profile diagnostic sources are:

- profile-specific settings,
- profile-specific configuration parsing and validation,
- profile-owned environment detection,
- profile-owned runtime/API compatibility checks,
- profile-owned internal plugin, DLL, or API loading,
- profile-owned initialization order,
- profile-owned creation of functional access objects.

The access profile plugin should report whether the configured profile can
provide the full application-required service set. A profile that cannot provide
or initialize one of the four functional service boundaries for the configured
run is not partially usable by the application.

### IAccessProfileSession Cleanup

`IAccessProfileSession` owns cleanup for resources acquired while
creating or using the loaded profile session.

Scope cleanup should cover:

- fully loaded profile resources,
- resources acquired before a profile initialization failure,
- internal binding instances or sessions owned by the profile,
- profile-owned API handles or process/session references,
- cleanup diagnostics when release or disposal fails.

If profile initialization fails after some resources were acquired, the profile
plugin or partially created `IAccessProfileSession` is responsible for
rollback/cleanup before returning failure diagnostics. The application should
receive a clean failure result with diagnostics, not a half-usable profile.

The application-level error handling model should define how WPF, CLI, and
shared core consume these diagnostics and translate them into validation state,
workflow state, result state, output state, user-facing messages, technical
details, and exit behavior.

## Output And Artifact Architecture

Outputs and artifacts are application results, not plugin implementation
details.

The architecture should support:

- on-screen WPF state and diagnostics,
- CLI console diagnostics,
- structured environment and validation snapshots,
- execution summaries,
- diagnostic logs,
- UnitTest result artifacts,
- CI/CD test-report artifacts.

JUnit XML is the implemented format for CI/CD test-report artifacts. The
exporter boundary permits additional report artifact types without
coupling result interpretation to one output format.

## Testability And Development Shape

The architecture should support development without requiring every Siemens
installation or every version-specific plugin project to be loaded for all
tasks.

Expected testability boundaries:

- shared core logic should be testable without Siemens software where practical,
- access-profile selection and metadata validation should support fake metadata,
- functional access behavior should support fake or sample access objects for
  non-runtime tests,
- communication interpretation should be testable from sample evidence,
- real TIA Portal and PLCSIM Advanced installations remain required for real
  integration validation.

Solution filters should support focused work on:

- shared core and tests,
- WPF application,
- CLI application,
- TIA Openness V21 binding,
- PLCSIM Advanced 8.0 binding,
- integrated Siemens profile validation.
