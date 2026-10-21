# Shared Workflow Design

## `RunWorkflowService`

`RunWorkflowService` is the primary shared behavior entry point for WPF and CLI.
It owns workflow orchestration, action gates, service calls, subscriptions,
publication, exportability projection, and cleanup coordination. Test-run
monitoring is delegated to `TestRunMonitor`.

Shared workflow services receive application-owned infrastructure through
`CoreServiceContext`. The context is intentionally limited to infrastructure:
`ILoggerFactory`, `TimeProvider`, and diagnostic role access through
`DiagnosticServices`. It does not contain workflow state, run setup,
access-profile configuration, PLC/runtime references, or application/UI
services.

The code sketches in this document illustrate responsibilities and selected
members; they are not complete, independently maintained API declarations.
Use [RunWorkflowService.cs](../../../src/PLCSIM.UnitTest.Core/Workflow/RunWorkflowService.cs)
and the owning Common/Core model types as the source for exact signatures,
record constructors and optional members.

Selected workflow surface:

```csharp
public sealed class RunWorkflowService
{
    public RunWorkflowPublication Current { get; }

    public event EventHandler<RunWorkflowPublicationEventArgs> PublicationEventHandler;

    public Task<WorkflowActionResult> BindAccessProfileSessionAsync(
        AccessProfileSession session,
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<WorkflowActionResult> UnloadAccessProfileAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<WorkflowActionResult> DiscoverProjectTargetsAsync(
        AccessProfileConfig config,
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<WorkflowActionResult> DiscoverRuntimeTargetsAsync(
        AccessProfileConfig config,
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public OperationResult Configure(RunSetup setup);

    public Task<WorkflowActionResult> PrepareProjectAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<WorkflowActionResult> PrepareRuntimeAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<WorkflowActionResult> ReconnectRuntimeAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<WorkflowActionResult> TransferProjectAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<WorkflowActionResult> StartPlcAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<WorkflowActionResult> StopPlcAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<WorkflowActionResult> PrepareUnitTestCommunicationAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<WorkflowActionResult> StartMonitoringAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<WorkflowActionResult> StopMonitoringAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<WorkflowActionResult> ReleaseUnitTestCommunicationAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<WorkflowActionResult> ReleaseRuntimeAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<WorkflowActionResult> ReleaseProjectAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<UnitTestWorkflowActionResult> StartUnitTestAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<UnitTestWorkflowActionResult> AbortUnitTestAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<UnitTestWorkflowActionResult> ResetUnitTestAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);

    public Task<WorkflowActionResult> CleanupAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken);
}

public sealed record RunSetup(
    AccessProfileConfig AccessProfile,
    RuntimeProvisioningMode RuntimeProvisioningMode,
    OutputPolicy OutputPolicy,
    ExecutionPolicy Execution,
    ProjectTargetRef? ProjectTarget = null,
    RuntimeTargetRef? RuntimeTarget = null);

public sealed record WorkflowActionPolicy(
    TimeSpan Timeout);

public sealed record WorkflowActionResult(
    WorkflowAction Action,
    OperationResult Result,
    RunWorkflowPublication Publication);

public sealed record UnitTestWorkflowActionResult(
    WorkflowAction Action,
    OperationResult<TestRunCommandResult> Result,
    RunWorkflowPublication Publication);

public sealed record RunWorkflowPublicationEventArgs(
    RunWorkflowPublication Publication);

```

Required behavior:

1. Only one workflow operation may run at a time.
2. Access-profile capability registry lookup, explicit capability registration,
   package discovery, and profile-session loading are application-owned helper
   concerns. `BindAccessProfileSessionAsync` binds one loaded
   `AccessProfileSession` into the workflow, wires diagnostics/state events,
   and makes target-discovery-backed workflow operations available before a
   final `RunSetup` exists.
3. `Configure` stores the final selected run setup after required discovery and
   selection. The workflow requires a loaded matching access profile and the
   targets required by project ownership and runtime provisioning mode.
   `UseExisting` requires a runtime target; `CreateOwned` requires none and
   accepts the effective target returned by runtime preparation. `Configure` does
   not load the profile or perform preparation/execution work.
4. Discovery methods receive the current application-owned profile configuration
   snapshot explicitly. They may open/read profile-owned project/runtime context
   where required for selection, but must not retain that snapshot as the
   operational configuration of a later prepare call, build, transfer, start the
   PLC, or use UnitTest lifecycle/event-stream communication.
5. Prepare methods are state-changing operations that consume the final setup.
   They do not own UI discovery, manual target selection, or access profile
   loading.
6. Release methods mirror the lifecycle levels: UnitTest
   communication/monitoring, runtime/target, project context, and access
   profile.
7. `CleanupAsync` is a composed shutdown/recovery convenience action that
   delegates to the more specific release methods in a defined order.
8. Workflow publications include `ActionGateState[]` calculated from shared
   workflow status. Gate calculation is independent from WPF controls or CLI
   command names.
9. Each public operation method maps to one stable `WorkflowAction` for action
   gates, active operation state, diagnostics, logging, and tests.
10. Normal workflow operations use `WorkflowActionGateEvaluator` as their
   functional entry-validation source before calling access-profile services.
   The operation entry path evaluates a fresh `WorkflowStatusSnapshot`, so it
   does not rely on a stale published `ActionGateState`. Workflow-private
   handles remain internal invariants after a successful gate.
11. `BindAccessProfileSessionAsync`, `Configure`, cleanup internals, and
   post-gate service-result interpretation are intentionally outside the normal
   action gate model because they depend on concrete session identity,
   configuration payloads, side-effect-aware cleanup, or service response
   consistency.
12. Applications may compose multiple public operation methods into user-facing
   actions or unattended command sequences.
13. WPF and CLI must not call functional access services directly to bypass
   workflow state, action gates, diagnostics, or cleanup boundaries.
14. `StartUnitTestAsync` starts a run only after UnitTest communication and
   monitoring are active. It calls `IUnitTestLifecycleService` and interprets
   accepted, rejected, cancelled, or failed transport/API-write results. The
   lifecycle service owns the bounded external call; accepted results contain no
   lifecycle observation. Shared workflow monitors the later PLC reaction from
   independently published RunControl observations.
15. `AbortUnitTestAsync` and `ResetUnitTestAsync` follow the same boundary:
   `RunWorkflowService` checks gates and interprets command results, while
   `IUnitTestLifecycleService` owns the concrete command write and publishes
   subsequent RunControl observations independently.
16. A PLC Start/Stop command result is successful when the corresponding
   external-API function could be invoked without an error. The call returns
   without waiting serially for a later PLC operating-state transition. After a
   successful command call, shared core independently monitors profile-published
   `PlcState.OperatingState` observations for the expected reaction.
   `StartPlcAsync` is available only for an available runtime instance with an
   observed `Stopped` PLC. `StopPlcAsync` is available for observed
   `Starting`, `Running`, `Frozen`, or `Held` PLC states. Runtime-instance
   `Starting`/`Stopping` observations do not satisfy PLC command-reaction
   monitoring.
17. The workflow service subscribes to lifecycle, event-stream, processor, and
    monitor events. Applications consume workflow publication instead of
    subscribing directly to lower-level services.
18. Artifact export is enabled only from a stable completed run epoch projection
    with complete evidence. `RunWorkflowService` publishes this as
    `WorkflowStatusSnapshot.IsExportable`; the application-composed
    `ArtifactExportService` observes workflow publications and executes export.
19. Diagnostics from access profile, functional services, event processing,
    and test-run monitoring are written through the diagnostic writer role and
    exposed through workflow publication snapshots. Export diagnostics are
    written through the same writer role by `ArtifactExportService`. The writer
    role is backed by the single application-owned `DiagnosticSink`.
20. Event-stream monitoring/observation is a communication-session state, not a
    run-epoch state. A healthy monitor/observation status means the event-stream
    channel is active for the current `CommunicationSession`; it does not mean a
    UnitTest run is currently active.
21. `RunSessionReleaseService` owns the application-facing full-session release
    sequence. It first prevents new workflow operations, requests cancellation
    of the one registered active operation, and waits within the cleanup budget.
    It then runs workflow cleanup and finally unloads the access-profile session.
    Cleanup and unload are still attempted after an earlier release phase reports
    failure, cancellation, or timeout, and each phase retains its own result.

`RunWorkflowService` should not interpret vendor API details and should not
serialize output artifacts directly.

## Operation Deadlines And Cancellation

Every public asynchronous workflow operation receives one positive
`WorkflowActionPolicy.Timeout`. Shared Core combines that deadline with the
caller token through a `TimeProvider`-backed cancellation scope and forwards the
linked token to the selected profile service.

Result classification is deterministic:

- caller-requested cancellation wins and produces `Cancelled`;
- expiry of the Shared Core deadline produces `TimedOut`;
- a structured lower-layer `Cancelled` or `TimedOut` result without the
  corresponding caller/deadline condition is an unexpected operation failure;
- foreign `OperationCanceledException` without caller cancellation or deadline
  expiry is an unexpected cancellation failure;
- command acceptance never synthesizes later PLC or UnitTest state.

The overall workflow deadline and lower-layer technical containment are
different boundaries. Bindings may need bounded worker startup, pipe response,
protocol drain, vendor call, or process-exit limits. Those limits stay below the
profile contract and consume the available overall budget where practical.

Cancellation after a mutating request was dispatched is not rollback. A
serialized worker facade retains ownership of the request until it drains the
correlated terminal response or reaches a hard technical containment failure.
A successful drain preserves a healthy session and returns caller cancellation.
Failed drain, response-correlation loss, pipe/worker loss, or hard containment
expiry invalidates the affected session.

Partial setup remains owned by the configured workflow. A retry of the same
`RunSetup` skips already completed project/runtime stages and continues from the
first incomplete stage. Changing the setup while prepared resources remain is
blocked until the owner-driven release path runs. Back, End Session, unload, and
application shutdown therefore release partial as well as complete setups.

The active-operation registration is a Shared Core implementation boundary, not
an access-profile contract. Normal workflow actions, UnitTest commands, and
automatic communication preparation register against the same single-operation
owner. Full-session and stepper-scoped release atomically close that owner to new
functional operations before capturing and cancelling the current operation.
Only dependency-ordered release actions may start while that gate is closed. A
scoped gate is reopened after its selected context has been released; the
full-session gate remains closed until workflow cleanup and profile unload have
both been attempted.

### Application Operation Policy Matrix

The access-profile runtime contract defines the behavior every profile must
provide when these operations are cancelled or time out. The following matrix
defines the current application and Shared Core policy applied to that
contract. "WPF cancel" describes explicit user cancellation; every row remains
bounded by its listed Shared Core deadline or observation timeout.

| Operation | Default boundary | WPF cancel | Shared Core completion boundary | Recovery / follow-up |
| --- | ---: | --- | --- | --- |
| Registration refresh | 3 min | Yes | All selected profile registration attempts and atomic application-registry update. | Cancellation keeps the previous valid application registry; incomplete replacement output is not activated. |
| Profile load | 30 s | Yes | Plugin load plus creation of one valid loaded profile session. | An unreturned partial session is disposed. Retry may load the profile again after cleanup. |
| Profile unload | 30 s | No | Owner-driven session unload/disposal. | Caller may stop waiting at the deadline, but owner disposal continues and is awaited by final application disposal. |
| Project discovery | 3 min | Yes | Profile project-target discovery, including any technology-required project open. | Same-session retry is allowed when project health and retained access are known usable; otherwise Back/End Session releases the context. |
| Runtime discovery | 30 s | Yes | Profile runtime-target discovery. | Discard incomplete candidates; retry only with trustworthy profile/runtime health. |
| Project prepare | 1 min | Yes, through setup | Project access and selected project target are prepared. | Known completion is retained. Retry resumes the same setup; changed setup requires release first. |
| Build plus transfer | 10 min total | Yes, through setup | Build and transfer share one budget; the budget is not reset between stages. | Cancellation is not rollback. Drained completion may be retained and retry resumes at the first incomplete stage. Unclear transfer state blocks forward progress. |
| Runtime prepare | 1 min | Yes, through setup | Attach to the selected runtime or create the requested owned runtime. | A known compatible completion may be adopted on retry. Created resources remain owner-held until normal runtime release. |
| PLC command call | 10 s | No | External start/stop API acceptance. | Later PLC state is event-observed under the separate reaction boundary; no state is synthesized from the command result. |
| PLC start reaction | 30 s | No | Profile-published `Starting` or `Running` evidence. | Missing reaction reports monitoring failure without changing command acceptance evidence. |
| PLC stop reaction | 30 s | No | Profile-published `Stopping` or `Stopped` evidence. | Missing reaction reports monitoring failure without changing command acceptance evidence. |
| Communication prepare | 30 s | Yes, through setup/load | Lifecycle and event-stream communication context is established and validated. | Known healthy communication may be retained; uncertain state requires close/revalidation before retry. |
| Communication revalidation | 30 s; zero disables terminal expiry | No | Complete valid communication observation after a stale period. | Positive expiry invalidates the affected communication boundary. Zero keeps revalidation active until recovery or another terminal condition. |
| Monitoring start/stop | 30 s | No | Event-stream monitoring reaches the requested actual state. | Failed stop remains owner-held for communication release/cleanup; published monitoring state remains authoritative. |
| UnitTest command call | 3 s | No | RunControl Start/Abort/Reset write/API acceptance. | Later lifecycle evidence is observed separately. UnitTest Abort is a domain command, not infrastructure cancellation. |
| UnitTest command reaction | 10 s | No | Expected profile-published lifecycle reaction. | Missing reaction reports monitoring failure without rewriting command acceptance. |
| Test-run observation | 2 min | No | A trusted active epoch reaches terminal lifecycle evidence. | Timeout finalizes the current monitoring attempt; UnitTest Abort remains separately available when gated. |
| Final event evidence | 10 s | No | Terminal event-stream evidence stabilizes after lifecycle completion. | Timeout reports incomplete final evidence and preserves the available epoch snapshot/diagnostics. |
| Project release | 30 s | No | Project-owned vendor access is released after transfer, Back, End Session, or cleanup. | Release diagnostics do not erase successful transfer evidence; owner cleanup continues as required. |
| Runtime release | 30 s | No | Shared Core derives `KeepResource` for `UseExisting` and `DestroyResource` for `CreateOwned`, then the profile executes the explicit request. | Unknown failure retains release responsibility for retry. A known kept, destroyed, or partially destroyed postcondition clears the old runtime context; partial destruction remains unsuccessful and diagnosed. |
| End Session / cleanup | 2 min total | No | Monitoring, communication, runtime, project, and profile resources are released in dependency order. | Active cancellable setup is cancelled and awaited first. Cleanup then continues across individual failures and reports the aggregate outcome. |

Registration/profile administrative defaults and release/cleanup defaults come
from `ApplicationOperationTimeoutPolicy`. Run and observation values come from
`RunOperationTimeoutPolicy`. Binding-specific pipe, worker, protocol, and vendor
containment remains below this matrix and is documented by the owning binding.

## Common Workflow Publication And Action Gates

`RunWorkflowService` publishes one shared workflow view for WPF and CLI. WPF
binds this state to manual controls. CLI uses the same state to decide whether
the unattended run can continue or must fail.

```csharp
public sealed record RunWorkflowPublication(
    long Sequence,
    RunWorkflowState State,
    ActiveOperation? ActiveOperation,
    WorkflowStatusSnapshot Status,
    UnitTestRunEpochSnapshot? CurrentRun,
    TestRunMonitoringState MonitorState,
    UnitTestRunControlObservation? LifecycleObservation,
    EventStreamState? EventStreamState,
    ProjectTargetDiscoveryResult? ProjectTargets,
    RuntimeTargetDiscoveryResult? RuntimeTargets,
    IReadOnlyList<ActionGateState> ActionGates,
    DiagnosticSnapshot DiagnosticSnapshot)
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}

public sealed record WorkflowStatusSnapshot(
    RunWorkflowState State,
    ActiveOperation? ActiveOperation,
    bool OperationInProgress,
    bool HasAccessProfile,
    string? AccessProfileId,
    string? AccessProfileVersion,
    bool HasRunSetup,
    bool HasProjectTarget,
    bool HasRuntimeTarget,
    bool ProjectPrepared,
    bool RuntimePrepared,
    bool ProjectTransferred,
    PlcState PlcState,
    bool UnitTestCommunicationPrepared,
    bool UnitTestCommunicationContextActive,
    bool MonitoringActive,
    UnitTestRunControlObservation? LifecycleObservation,
    EventStreamState? EventStreamState,
    TestRunMonitoringState MonitorState,
    UnitTestRunEpochSnapshot? CurrentRun,
    AccessHealthSnapshot AccessHealth,
    bool IsExportable);

public sealed record AccessHealthSnapshot(
    AccessHealth Profile,
    AccessHealth Project,
    AccessHealth PlcControl,
    AccessHealth UnitTestLifecycle,
    AccessHealth UnitTestEventStream);

public sealed record ActiveOperation(
    string OperationId,
    WorkflowAction Action,
    string Phase,
    DateTimeOffset StartedAt,
    OperationProgress? Progress);

public sealed record ActionGateState(
    WorkflowAction Action,
    ActionGateAvailability Availability,
    IReadOnlyList<ActionGateReason> Reasons)
{
    public bool IsAvailable => Availability == ActionGateAvailability.Available;
}

public enum ActionGateAvailability
{
    Available,
    Pending,
    Blocked,
    Unsafe,
    NotApplicable
}

public sealed record ActionGateReason(
    ActionGateReasonCode Code,
    string Message);

public enum RunWorkflowState
{
    NotConfigured,
    AccessProfileLoaded,
    DiscoveryReady,
    Configured,
    PreparingProject,
    RuntimePrepared,
    ProjectTransferred,
    PlcStopped,
    PlcRunning,
    UnitTestCommunicationPrepared,
    Monitoring,
    RunActive,
    RunCompleted,
    CleaningUp,
    Error
}

public enum WorkflowAction
{
    SelectConfiguration,
    LoadAccessProfile,
    UnloadAccessProfile,
    DiscoverProjectTargets,
    DiscoverRuntimeTargets,
    PrepareProject,
    PrepareRuntime,
    TransferProject,
    StartPlc,
    StopPlc,
    PrepareUnitTestCommunication,
    StartMonitoring,
    StopMonitoring,
    StartUnitTest,
    AbortUnitTest,
    ResetUnitTest,
    ReleaseUnitTestCommunication,
    ReleaseRuntime,
    ReleaseProject,
    Cleanup
}

public enum ActionGateReasonCode
{
    NotConfigured,
    AccessProfileNotLoaded,
    ProjectTargetNotSelected,
    RuntimeTargetNotSelected,
    OperationInProgress,
    ProjectNotPrepared,
    RuntimeNotPrepared,
    RuntimeInstanceNotAvailable,
    ProjectNotTransferred,
    PlcNotRunning,
    PlcNotStopped,
    UnitTestCommunicationNotPrepared,
    UnitTestCommunicationStillPrepared,
    EventStreamNotMonitoring,
    UnitTestNotReady,
    RunAlreadyActive,
    NoRunAvailable,
    RuntimeStillPrepared,
    AccessProfileUnhealthy,
    ProjectAccessUnhealthy,
    PlcControlAccessUnhealthy,
    UnitTestLifecycleAccessUnhealthy,
    UnitTestEventStreamAccessUnhealthy,
    AccessRevalidationRequired,
    ErrorState
}
```

Direction:

- `RunWorkflowService` publishes `WorkflowStatusSnapshot` as the shared,
  presentation-independent workflow status projection,
- every `RunWorkflowPublication` carries a monotonically increasing `Sequence`
  assigned by `RunWorkflowService`; applications must ignore older
  publications that arrive after a newer one,
- action gates are calculated by `WorkflowActionGateEvaluator` from
  `WorkflowStatusSnapshot`,
- `WorkflowStatusSnapshot.AccessHealth` is the current normalized context-trust
  snapshot for the loaded profile and all functional access services,
- `WorkflowStatusSnapshot.PlcState` is the latest neutral state published by the
  loaded access profile; shared core and applications must not synthesize it from
  Start/Stop command results or active-operation labels,
- a successful PLC Start/Stop command starts an independent shared-core reaction
  monitor. Start accepts a later observed `Starting` or `Running`; Stop accepts a
  later observed `Stopping` or `Stopped`. A newer successful command replaces the
  previous monitor. Timeout produces a warning diagnostic and never fabricates or
  overwrites `PlcState`,
- `WorkflowActionGateEvaluator` treats `NotEstablished` as a neutral state and
  blocks relevant actions for `RevalidationRequired`, `Invalidated`, and
  `Disposed` health states,
- `IsExportable` is a functional fact that describes whether the current run
  snapshot can be used as artifact-export input; it is true only for a normally
  completed monitored run with complete evidence. It is not artifact execution
  state and does not describe export success or failure,
- project and runtime discovery results are published as neutral candidate data
  before the final `RunSetup` is configured,
- action gates describe shared workflow action availability, not UI control
  state or CLI parser behavior,
- normal workflow operation entry validation evaluates the same
  `WorkflowActionGateEvaluator` from a fresh `WorkflowStatusSnapshot` before
  calling functional access services,
- unavailable gates produce the blocked operation result reason/message for
  applicable operation calls; post-gate service-result inconsistencies remain
  workflow diagnostics rather than alternate gate facts,
- WPF maps `ActionGateAvailability` to enabled, blocked, pending, unsafe, or
  not applicable manual commands,
- CLI maps action gates to deterministic plan continuation, early failure, or
  explicit recovery/cleanup behavior,
- contract mismatch is represented by diagnostics and action gate reasons,
- `StartPlc` and `StopPlc` gates use the latest profile-published `PlcState`;
  `Starting`/`Running` block another start and allow stop, while
  `Stopping`/`Stopped` allow start and block stop,
- `ResetUnitTest` is available as a recovery action when the PLC is running and
  UnitTest communication was prepared.

## Application-Level Operation Composition

The workflow service exposes concrete operation methods instead of a generic
action-command dispatcher. `WorkflowAction` remains the shared identity used for
gates, diagnostics, active operation state, logging, and tests.

WPF and CLI may compose public workflow methods into their own interaction or
execution sequences:

```text
WPF Step 1 Next command
  -> AccessProfileSessionService.LoadAsync with the selected ProjectOwnershipMode
  -> BindAccessProfileSessionAsync
  -> DiscoverProjectTargetsAsync only for ApplicationManaged ownership
  -> DiscoverRuntimeTargetsAsync

WPF Step 2 Next command
  -> validate target selection and open Step 3 profile configuration

WPF Step 3 Next command
  -> Configure
  -> ApplicationManaged: PrepareProjectAsync
  -> PrepareRuntimeAsync
  -> ApplicationManaged: TransferProjectAsync and release live project access
  -> ExternallyManaged: accept external project handling without fabricated project evidence

WPF PLC readiness observation
  -> profile-published PlcState independently reports the current/changed PLC state
  -> observed ReadyForExecution triggers communication preparation
  -> successful communication preparation triggers event-stream monitoring
  -> applies equally after StartPlcAsync, external PLC start, and already-running attach

WPF Start PLC command
  -> StartPlcAsync returns from the profile/API command result
  -> shared core command-reaction monitoring observes later profile-published PlcState

WPF runtime reconnect
  -> gate requires configured setup, loaded profile, prepared runtime, and effective runtime target
  -> invalidate an active/finalizing run observation as incomplete
  -> IAccessProfileSession.ReconnectRuntimeAsync releases stale communication and revalidates runtime access in place
  -> reset Shared Core communication/observation contexts after a non-cancelled profile result
  -> adopt the returned runtime context while preserving worker/session, target, and provisioning ownership
  -> profile-published PlcState determines whether communication preparation and monitoring restart

CLI full run command
  -> configuration check and registered-profile resolution
  -> AccessProfileSessionService.LoadAsync with ApplicationManaged ownership
  -> BindAccessProfileSessionAsync
  -> load the profile configuration
  -> DiscoverProjectTargetsAsync and resolve the configured project target
  -> DiscoverRuntimeTargetsAsync and resolve configured provisioning intent/target
  -> Configure with resolved intent (omission: CreateOwned) and target
  -> PrepareProjectAsync
  -> PrepareRuntimeAsync
  -> TransferProjectAsync
  -> StartPlcAsync when the PLC is not already running
  -> wait for profile-published Running state and active UnitTest communication
  -> wait for current Idle + Ready without an initial Reset, keeping event consumption active
  -> optionally request one Reset after readiness timeout, then wait for ResetReady
  -> StartUnitTestAsync
  -> wait for exportable terminal workflow publication
  -> explicit required JUnit export through ArtifactExportService
  -> coordinated bounded release through RunSessionReleaseService
```

The CLI owns the initial UnitTest readiness deadline (`--unittest-ready-timeout`,
10 seconds by default). It uses existing publications, not an additional Core
monitor. Only explicit `--reset-on-startup-timeout` allows one recovery Reset;
the existing command acceptance and ResetReady reaction deadlines then apply.
Explicit RunControl configuration/error states abort without recovery. Timeout
does not establish a stable PLC state. There is no automatic EventBuffer reset.
These CLI options do not add JSON fields or change WPF behavior.

PLC readiness observations are independent from workflow command completion and
may arrive while setup is still active. If `ReadyForExecution` is observed while
a workflow operation prevents communication preparation, shared workflow retains
the pending auto-prepare intent and reconciles it after the operation completes.
It must not require a second PLC state transition or synthesize state from a
command result.

This keeps the public API readable and avoids introducing a configurable
workflow engine before there is a concrete requirement. If configurable workflow
sequences become a real requirement later, a dispatcher or request builder can
be added on top of the same public methods and `WorkflowAction` identities.

## Setup, Discovery, And Release Flow

The setup flow is split into six concerns:

| Concern | Workflow Owner | Contract Owner | Notes |
| --- | --- | --- | --- |
| Access profile registry/registration | Application-owned capability registry store and registration service. | `IAccessProfilePlugin.RegisterAsync` and shared capability DTOs. | Reads persisted profile usability for selection. Explicit registration may use package discovery and temporary profile loading, but normal selection does not run live capability checks. |
| Access profile load | Application-owned `AccessProfileSessionService` through `AccessProfilePluginLoader`. | `IAccessProfilePlugin` / `IAccessProfileSession`. | Loads exactly one profile session from setup-only `AccessProfileSessionRequest`; no operational profile payload or final `RunSetup` is required yet. |
| Access profile workflow binding | `RunWorkflowService.BindAccessProfileSessionAsync`. | Common workflow model. | Binds one already loaded profile session into the workflow and wires workflow-visible events. |
| Discovery / selection candidates | `RunWorkflowService` orchestration; access profile functional services provide profile-specific data. | Functional services. | Produces application-facing project/PLC/runtime candidates from the current application-owned profile configuration. Discovery does not establish later prepare configuration. No Siemens or vendor object handles leave the profile. |
| Final run setup binding | `RunWorkflowService.Configure`. | Common `RunSetup` model. | Stores the complete selected setup after discovery/selection. Does not load profile or prepare resources. |
| Preparation / execution | `RunWorkflowService` operation methods. | Functional services. | Passes the configured run snapshot to project, runtime, and communication prepare; performs transfer, PLC start, monitoring, and UnitTest commands. Prepared resources retain only their own accepted lifecycle configuration. |

WPF uses this split for stepper navigation. CLI uses the same split without
interactive selection: it loads the profile, performs deterministic project and
runtime discovery, resolves the configured project target, validates the
configured provisioning intent using the shared runtime-target resolver,
configures the complete run setup, then executes
the prepare/run sequence.

Reconnect is an in-place technical revalidation path, not a resource-release or
replacement path. It preserves the worker/session boundary, selected runtime
target, resource identity, provisioning mode, effective configuration, and
project setup. It performs no discovery, project operation, download, runtime
provisioning, target release, worker replacement, or PLC command. A running PLC
causes the normal observed-state path to prepare UnitTest communication and
monitoring again; a stopped PLC remains runtime-prepared with communication
inactive. Existing completed run results remain historical evidence and must
not be republished or exported as a new completion. An active or finalizing run
is retained as incomplete evidence.

Release follows the reverse lifecycle direction:

```text
ReleaseUnitTestCommunicationAsync
  -> stop monitoring
  -> release UnitTest communication/lifecycle resources

ReleaseRuntimeAsync
  -> derive release mode from the retained provisioning context
  -> UseExisting: request KeepResource
  -> CreateOwned: request DestroyResource
  -> clear runtime/target context only for a known terminal resource outcome

ReleaseProjectAsync
  -> release project/discovery context after runtime is released

UnloadAccessProfileAsync
  -> release all lower levels if needed
  -> dispose loaded access profile
```

WPF back navigation maps to these shared workflow operations:

- returning to Step 2 releases operation/test-run and communication/runtime
  state far enough that PLC/runtime/communication selection is editable again,
- returning to Step 1 additionally releases project context and unloads the
  access profile,
- `End Session` calls the composed cleanup/release path and discards the draft
  configuration at application level.

Runtime reconnect does not use this release path. It replaces technical access
to the retained target and preserves the original provisioning context. Profile
fallback cleanup may close remaining technical sessions, but it cannot infer or
perform runtime-resource destruction without the explicit Shared Core release
request.

WPF recovery navigation is intentionally separate from functional readiness
gates. A failed asynchronous step transition may leave the UI on the attempted
step surface, for example Step 2 after access-profile load started but failed.
In that state, functional controls stay disabled by workflow/action gates, but
Back to the previous setup step and End Session remain available so the user can
recover without closing the application.

## Responsibility Map

The implementation keeps these responsibility boundaries explicit:

| Area | Code ownership and boundary |
| --- | --- |
| Session binding | `AccessProfileSessionService` loads/unloads sessions; `RunWorkflowService.BindAccessProfileSessionAsync` binds an already loaded session. Discovery/loading is outside workflow orchestration. |
| Target discovery | Workflow discovery operations publish neutral candidates; application target selection supplies final setup/preparation. Discovery does not establish later operation configuration. |
| Configuration | `RunWorkflowService.Configure` validates/stores `RunSetup` for the loaded matching profile and selected targets. |
| Preparation | `PrepareProjectAsync` requires final setup; it does not implicitly load a profile or choose targets. |
| Status and gates | `WorkflowStatusSnapshot` carries facts; `WorkflowActionGateEvaluator` derives published gates and normal operation entry checks. Session binding, configuration validation, side-effect-aware cleanup and post-gate result diagnostics retain their explicit special cases. |
| Release | Scoped release methods support back-navigation; `CleanupAsync` is idempotent and available without a loaded profile. |
| Services | Functional services own their project, runtime and communication release operations; workflow coordinates their order and publication. |
| Regression composition | Fake-profile tests exercise discovery/configuration, full execution, release order and gate categories through the same boundary as applications. |

### Foreign Call And Callback Containment

`RunWorkflowService` uses one narrow generic/non-generic execution guard for
direct calls into the bound access-profile session and its project, PLC-control,
UnitTest lifecycle, and UnitTest event-stream services.

The foreign-operation guard follows these rules:

- an ordinary `OperationResult` is returned unchanged for action-specific
  interpretation,
- caller-requested cancellation and deadline expiry are classified by the
  enclosing workflow cancellation scope,
- an `OperationCanceledException` without caller cancellation or deadline
  expiry becomes a failed foreign operation with an `UnexpectedCancellation`
  diagnostic,
- any other unexpected foreign exception becomes a failed operation with an
  `UnexpectedException` diagnostic,
- the diagnostic is correlated with the workflow operation, access profile,
  service, project context, and runtime target where available,
- only the direct foreign invocation is guarded; result mapping, workflow state
  mutation, publication, gate evaluation, and invariant checks are not.

The diagnostic code shape is
`Workflow.AccessProfile.<Service>.<Operation>.UnexpectedException` or
`Workflow.AccessProfile.<Service>.<Operation>.UnexpectedCancellation`. Full
exception details and stack traces remain in the technical log.

Profile-originated diagnostic, project-state, PLC-state, lifecycle-observation,
event-batch, and health callbacks enter shared core through top-level
containment. A callback-processing exception uses
`Workflow.AccessProfile.Callback.<Callback>.ProcessingFailed`, cannot propagate
back into the profile event source, and invalidates an affected active
observation when its evidence is no longer reliable. Diagnostic publication,
observation invalidation, and fallback Error publication are independent
best-effort steps so a failing subscriber cannot suppress the remaining
containment work.

This boundary does not assign profile health, operation deadlines, WPF
cancellation policy, or shutdown ownership. Profiles remain the only publishers
of `AccessHealth`; the workflow deadline policy classifies bounded operation
execution; coordinated shutdown owns cancellation, ordered disposal, bounded
release, and final worker termination.

## Access Health Flow

Access health is the shared workflow input for profile and service context
trust. It is separate from service lifecycle/progress state and from
diagnostics:

```text
IAccessProfileSession / functional services
  -> IAccessProfileSession.HealthChangedEventHandler
  -> AccessHealthMonitor
  -> WorkflowStatusSnapshot.AccessHealth
  -> WorkflowActionGateEvaluator / RunWorkflowService interpretation
  -> RunWorkflowPublication / diagnostics / recovery gates
```

`AccessHealthMonitor` is intentionally passive. It binds to the loaded
`IAccessProfileSession`, reads the initial `Health` value of the profile and all four
functional services, stores the latest value per `AccessServiceKind`, and
publishes updated `AccessHealthSnapshot` values when the profile reports health
changes.

`AccessHealthMonitor` must not create diagnostics, evaluate action gates,
invalidate run evidence, or decide recovery behavior. Those decisions belong to
`RunWorkflowService` and `WorkflowActionGateEvaluator`, because only the
workflow knows whether a health state is expected, currently relevant, or
dangerous for an active run observation.

Access health interpretation rules:

- `Profile` health affects the loaded profile session as a whole.
- `Project` health blocks project-dependent preparation, discovery, transfer,
  and setup paths, but does not retroactively invalidate a running observation
  when runtime, lifecycle, and event-stream health remain trustworthy.
- `PlcControl` health affects runtime/PLC operations and can invalidate active
  observation when runtime trust is lost.
- `UnitTestLifecycle` health affects RunControl command and lifecycle
  observation trust.
- `UnitTestEventStream` health affects event-stream monitoring, evidence
  intake, and release/acknowledgement trust.
- During active run monitoring, invalidated `Profile`, `PlcControl`,
  `UnitTestLifecycle`, or `UnitTestEventStream` health invalidates active
  observation/evidence unless a normal completed exportable snapshot already
  exists.
- Profiles map vendor/API-specific details to `AccessHealth` state, problem,
  reason code, and message. Shared core does not inspect Siemens DLLs,
  sessions, handles, or raw API exception types.

## Handover And Data Flow

The shared implementation should use explicit handover points between
applications, workflow, access profile services, event processing, diagnostics,
and artifact export.

## Preparation And Execution Flow

Preparation turns an application run setup into a runnable PLC and a validated
UnitTest communication setup. WPF and CLI use the same underlying workflow
actions, but they group them differently.

Technical action order:

```text
WPF/CLI
  -> AccessProfileSessionService
  -> AccessProfilePluginLoader
  -> IAccessProfileSession
  -> RunWorkflowService.BindAccessProfileSessionAsync
  -> RunWorkflowService
  -> IPlcProjectService
  -> IPlcControlService
  -> IAccessProfileSession.PrepareUnitTestCommunicationAsync
  -> IUnitTestEventStreamService.StartMonitoringAsync
```

Shared technical order:

1. The application selects or loads access-profile configuration.
2. `AccessProfileSessionService.LoadAsync` loads the selected access profile
   through `AccessProfilePluginLoader` from setup-only profile identity, source,
   and project-ownership intent.
3. The selected access profile validates the current profile-specific
   configuration snapshot for the operation that consumes it.
4. `RunWorkflowService.BindAccessProfileSessionAsync` binds the loaded profile
   session into workflow state.
5. `RunWorkflowService` performs project/PLC/runtime discovery where the
   consumer or profile needs it before final setup binding.
6. The application or CLI provides the final selected `RunSetup`, including
   runtime provisioning mode and the targets required by that mode.
7. `RunWorkflowService.Configure` validates and stores the final run setup and
   its current application-owned profile configuration snapshot.
8. `IPlcProjectService` prepares the PLC project context from that snapshot.
9. `IPlcControlService` provisions the runtime context from that snapshot and
   returns the effective runtime target.
10. The selected project-side PLC target comes from the final `RunSetup`.
11. The runtime-side PLC target comes from the final `RunSetup` for
    `UseExisting` or from successful runtime preparation for `CreateOwned`.
12. `IPlcProjectService` builds the prepared project where the profile supports
   or requires an explicit build step.
13. `IPlcProjectService` transfers/downloads the prepared project target to the
    runtime target/destination.
14. `IPlcControlService` starts the PLC runtime target.
15. `IAccessProfileSession.PrepareUnitTestCommunicationAsync` reads and validates
   UnitTest RunControl and EventStream contract info while the PLC is running.
16. A valid `CommunicationSession` is attached to `UnitTestContext`.
17. `IUnitTestEventStreamService.StartMonitoringAsync` starts event-buffer
    observation for that session.

WPF command grouping:

- configuration selection is one or more manual actions before preparation,
- Step 1 `Next` loads the selected access profile and enables discovery for
  Step 2,
- Step 2 `Next` validates target selection and opens Step 3 profile configuration,
- Step 3 `Next` validates the final profile/setup values, stores `RunSetup`, and
  composes project preparation, runtime preparation, build, and project transfer,
- PLC Start and Stop remain separately available manual actions,
- after PLC Start, `RunWorkflowService` prepares UnitTest communication and
  starts EventStream monitoring when the action gates allow it,
- Start, Abort, Reset, Stop PLC, and Cleanup are exposed according to action
  gates.

CLI command grouping:

- CLI uses one unattended execution path from configuration to terminal result,
- runtime preparation, build, project transfer, PLC start, UnitTest communication
  preparation, monitoring, UnitTest start, completion wait, artifact export, and
  cleanup are executed as one policy-controlled run,
- any required gate failure maps to a non-zero CLI exit result.

Contract mismatch handling:

- UnitTest communication preparation fails.
- No UnitTest start command is allowed.
- Event-stream data must not be interpreted as compatible UnitTest evidence.
- WPF receives diagnostics and keeps Start unavailable.
- CLI terminates with an error exit result.

## Event Batch Flow

Event batches are the handover from access-profile communication to shared-core
run processing.

```text
PLC Event Buffer
  -> IUnitTestEventStreamService
  -> UnitTestEventBatch
  -> EventBatchReceived
  -> RunWorkflowService / EventStreamProcessor
  -> ActiveRunEpochState.EventStream
  -> UnitTestRunEpoch projection
```

Access-profile responsibilities:

1. Read producer-owned event-buffer contract data only after UnitTest
   communication was prepared.
2. Derive readable FIFO positions from producer-owned FIFO control state.
3. Read only produced event entries with `SequenceId > 0`.
4. Preserve FIFO order in the emitted batch.
5. Validate technical read plausibility and expose the result through
   `EventBufferReadState`.
6. Request producer-side release through `DeleteSequenceId` for the highest
   contiguous processed `SequenceId`.
7. Publish `UnitTestEventBatch` through `EventBatchReceived`.
8. Keep no long-lived application event history.

Shared-core responsibilities:

1. Ignore or reject batches whose `SessionId` does not match the active
   `CommunicationSession`.
2. Forward batches to `EventStreamProcessor` while event-stream observation is
   active.
3. Forward event-stream monitoring evidence to `TestRunMonitor`.
4. Update workflow publication from processor and monitor events.
5. Publish a read-only `UnitTestRunEpoch` projection for applications and
   exporters when a stable projection is available.

The access profile releases PLC buffer entries after successful technical read
and validation. It does not wait until WPF, CLI, artifact export, or result
interpretation has finished.

## Test Run Monitoring Flow

`TestRunMonitor` owns test-run monitoring. It combines lifecycle evidence from
`IUnitTestLifecycleService`, event-stream monitoring evidence from
`IUnitTestEventStreamService`, processor snapshots, and its own time/timeout
evaluation into test-run monitoring state.

`RunWorkflowService` owns the monitor instance lifecycle and wires it to lower
layers:

```text
IUnitTestLifecycleService
  -> RunWorkflowService
  -> TestRunMonitor.ApplyLifecycleUpdate(...)

IUnitTestEventStreamService
  -> RunWorkflowService
  -> EventStreamProcessor.ProcessBatch(...)
  -> TestRunMonitor.ApplyEventStreamUpdate(...)

TestRunMonitor
  -> RunWorkflowService
  -> RunWorkflowPublication / export / cleanup decisions
```

`TestRunMonitor` uses `TimeProvider` for time and timeout handling. It owns the
monitoring timer and emits monitor events when timeout, terminal state,
invalidation, or relevant state progress is detected. `RunWorkflowService` does
not poll the monitor and does not pass time ticks into it.

Expected `TestRunMonitor` outline:

```csharp
public sealed class TestRunMonitor : IAsyncDisposable
{
    public TestRunMonitoringState Current { get; }

    public DateTimeOffset? MonitoringStartedAt { get; }
    public DateTimeOffset? RunStartedAt { get; }
    public DateTimeOffset? LastLifecycleEvidenceAt { get; }
    public DateTimeOffset? LastEventStreamEvidenceAt { get; }
    public DateTimeOffset? CompletionDetectedAt { get; }

    public TimeSpan? MonitoringElapsed { get; }
    public TimeSpan? RunElapsed { get; }
    public TimeSpan? TimeSinceLastLifecycleEvidence { get; }
    public TimeSpan? TimeSinceLastEventStreamEvidence { get; }

    public TestRunMonitor(
        TimeProvider timeProvider,
        ILogger<TestRunMonitor> logger);

    public OperationResult StartMonitoring(TestRunMonitoringContext context);

    public TestRunMonitorUpdate ApplyLifecycleUpdate(
        UnitTestLifecycleUpdate update);

    public TestRunMonitorUpdate ApplyEventStreamUpdate(
        TestRunEventStreamUpdate update);

    public Task StopMonitoringAsync(CancellationToken cancellationToken);

    public event EventHandler<DiagnosticEventArgs> DiagnosticEventHandler;

    // Concrete monitor outcome events are finalized during implementation.
    // Prefer specific EventHandler members over a generic event bus when the
    // consumer reactions are semantically different.
}

public sealed record TestRunMonitoringContext(
    CommunicationSessionId SessionId,
    RunOperationTimeoutPolicy TimeoutPolicy,
    uint? EventStreamSequenceBaseline,
    DateTimeOffset ObservationStartedAt);

public sealed record TestRunMonitorUpdate(
    TestRunMonitorUpdateKind Kind,
    TestRunMonitoringState State,
    RunEpochId? EpochId,
    UnitTestRunEpochSnapshot? Snapshot,
    IReadOnlyList<Diagnostic> Diagnostics);

public enum TestRunMonitorUpdateKind
{
    NoChange,
    MonitoringStarted,
    EpochBoundaryDetected,
    StateChanged,
    EvidenceUpdated,
    TimeoutDetected,
    TerminalStateReached,
    MonitoringStopped,
    Invalidated
}
```

Expected monitor event categories are:

- state/progress changed,
- terminal condition reached,
- timeout detected,
- monitoring invalidated or stopped.

The exact event handler set should be finalized during implementation once the
`RunWorkflowService` reactions are concrete.

`TestRunMonitor` is responsible for:

- consuming processor-owned run-epoch snapshots without creating epoch
  boundaries from lifecycle projections,
- monitoring the observed reaction to an accepted Start command independently
  from run-epoch assignment,
- active-run timeout,
- final-event stabilization timeout,
- transition from waiting/starting/running/completing to terminal states,
- recognizing abort/reset/target invalidation effects on the active test run,
- diagnostics for monitor-level inconsistency, timeout, or invalidated evidence,
- exposing current timing information through properties.

The monitor timeouts are application-side observation/monitoring timeouts. They
do not define the PLC-side maximum UnitTest execution duration and they must not
be treated as the authoritative "the test ran too long" decision. The PLC-side
UnitTest runner/RunControl owns any functional test-duration supervision because
only the PLC has authoritative knowledge of internal sequences, waits, and
runner-controlled abort behavior.

Application-side monitoring timeouts protect the application from waiting
forever after a command was accepted or after an externally started run was
observed, for example when:

- a start command was accepted but no trusted active-run boundary is observed,
- an active run does not reach completion before `testRun`,
- lifecycle completion was observed but the final RunControl completion anchor
  or buffer-empty evidence was not received before `finalEvent`,
- reset was accepted but ready evidence is not observed.

When such a timeout is reached, the workflow should mark the observation as
timed out, publish diagnostics, and prevent false trusted-success
interpretation. It should not automatically issue a UnitTest abort command by
default. Automatic abort on active-run observation timeout would be a separate
explicit execution policy and must not apply to final-event stabilization
timeouts.

`Completed` lifecycle evidence with a `Passed`, `Failed`, or `Error` result is
valid terminal run evidence once the required final event-stream completion
anchor is observed. A lifecycle error state that is not a normal completed test
result still invalidates monitoring.

`TestRunMonitor` does not know whether the active run was application-triggered
or externally started. Both flows produce the same observed PLC-side lifecycle
and event-stream picture after the active boundary. The only relevant difference
is that application-triggered runs have a prior lifecycle-service command
result. Command acknowledgement timeout belongs to `IUnitTestLifecycleService`;
post-accept lifecycle and event-stream observation timeout belongs to
`TestRunMonitor`.

`EventStreamProcessor` owns active run epoch reconstruction. The mutable state
lives inside `ActiveRunEpochState`; the published application model is
`UnitTestRunEpoch`.

The processor observation is started when event-stream monitoring starts and
continues for the communication session until monitoring is stopped, UnitTest
communication is released, cleanup runs, or the communication context is
invalidated. Releasing UnitTest communication always returns the technical
`TestRunMonitor` to `NotMonitoring`; completed, aborted, timed-out, or
invalidated run-epoch evidence remains owned by the event-stream processor and
result model until a defined result-clear boundary. Run completion, abort,
timeout, or reset completion finalizes only the current run epoch. A later exact
RunControl start anchor creates a new
`ActiveRunEpochState` inside the already active observation.

`EventStreamProcessor` detects and owns epoch boundaries directly from the
normative event stream:

```text
RunControl StateTransition -> Starting
  -> EventStreamProcessor opens ActiveRunEpochState
  -> TestRunMonitor consumes the new snapshot
  -> RunWorkflowPublication update
```

`EventStreamProcessor.Reset()` is reserved for cleanup, profile unload, or an
invalidated context. It is not used as the normal signal for starting a new
test-run epoch.

Run epoch creation:

- Application-triggered and PLC-side-started runs use the same exact start
  anchor: `OwnerScope = RunControl`, `EventType = StateTransition`, and
  `State = Starting`.
- The command result only confirms that the Start call reached the external API
  without an API error. Lifecycle command-reaction monitoring remains separate.
- Polled lifecycle `Running`, trigger events, or sequence ids do not open an
  epoch and do not repair a missing start anchor.
- The start anchor is the first event of the new `ActiveRunEpochState`.
- Older mutable run state is no longer the active epoch. Any data needed by UI
  or export must already be available as a stable projection or copy.

Run epoch update:

Owner result propagation separates evaluation severity (`Passed -> Failed -> Error`)
from explicit abort outcomes. A PLC-reported owner `Abort` is retained even when
completed children are `Failed`; their results and findings remain available.
Actual assertion/owner Error evidence remains dominant. Child Abort or an accepted
PC Abort command alone never manufactures a parent Abort. Missing or contradictory
parent evidence is reported by analysis, not replaced with a synthetic PLC Error.
Explicit abort finalization still requires the RunControl `Completed` anchor and
a trustworthy empty-buffer observation; the PC epoch is then `Aborted`, while the
PLC owner lifecycle remains `Completed`.

WPF preserves the aborted parent and failed children separately, with existing
error/incomplete-evidence presentation taking precedence. CLI treats Abort as a
non-passing run. JUnit retains its testcase-level mapping: existing Failed/Error
findings are exported, and an aborted testcase without such a finding maps to
`error`. JUnit mapping does not rewrite the captured PLC hierarchy.

- `StateTransition` events update contract-near lifecycle state.
- `Result` events update owner result state.
- `Aggregation` events are parsed from the event `Message` JSON payload and
  update `UnitTestAggregation`.
- `Trigger` events may create an owner node when they are the first owner
  evidence, but they do not define result or completion.
- Current `Info` events do not update the structured model.
- `Framework` events are retained in `EventStream` and indexed through
  `FrameworkEvents`.
- Assertion result events create findings on the parent TestCase derived from
  `OwnerScope` and hierarchical `Name`.

Run epoch completion:

- The RunControl `StateTransition` event to `Completed` is the final event and
  completion anchor of the inclusive epoch sequence range.
- Completion-anchor and empty-buffer observations are independent latched facts;
  either may arrive first, and normal completion requires both.
- Test-run monitoring is terminal only when `TestRunMonitor` has consumed the
  processor snapshot and applied timeout/context policy to a terminal monitoring
  outcome.
- `RunWorkflowState.RunCompleted` is published only for normal completed runs.
  Abort and timeout are terminal monitoring outcomes but are published through
  the corresponding workflow state while still carrying the current
  `UnitTestRunEpochSnapshot` when available.
- Missing lifecycle completion evidence, event-buffer epoch reset, timeout, or
  `LostEventCount > 0` makes the delivered lifecycle event log incomplete.
- Missing result or aggregation evidence affects result/aggregation evidence,
  not the lifecycle completion state itself.

## Diagnostic Flow

Diagnostics are emitted by services, core components, exporters, and
application-level producers as structured application-facing findings. A
diagnostic emission is an observation from one component at one point in time.
The application-owned shared diagnostic sink turns these emissions into the
current diagnostic snapshot that applications present. Core and application
components access this sink through `DiagnosticServices` roles instead of
depending on the concrete sink unless they are the composition root or a
transition/compatibility boundary.

```text
Access profile / Core / Exporter
  -> DiagnosticSink
  -> DiagnosticSnapshot
  -> WPF/CLI presentation and logging integration

WPF application services / view-model coordination
  -> same DiagnosticSink
  -> WPF diagnostics presentation
```

Ownership:

- The application composition root owns one `DiagnosticSink` instance for the
  application run.
- `DiagnosticServices` exposes writer, reader, and controller roles over that
  same sink. It is not a factory for additional sinks.
- `DiagnosticSink` owns diagnostic collection, correlation, and coalescing.
- `DiagnosticSink` publishes snapshot changes so applications can render the
  current diagnostic list without mediating producer-specific diagnostic events.
- `RunWorkflowService` uses the writer role to publish workflow-origin
  diagnostics; it is not the semantic owner of diagnostics.
- `ArtifactExportService` uses the writer role to publish export-origin
  diagnostics; WPF does not forward export diagnostics into the sink.
- `UnitTestRunEpoch` does not own diagnostics.
- Diagnostics may reference `RunEpochId`, `SequenceId`, operation id, service
  context, access profile, or artifact request.
- Application publication may combine current workflow state, active run epoch,
  diagnostics, and output state into one view model or CLI execution result.
- WPF may publish WPF/application-level diagnostics such as configuration-load,
  application-log, or artifact-access problems into the same sink. WPF must not
  keep a second diagnostic truth list; only presentation state such as selected
  diagnostic row belongs to the view model.

The Common diagnostic model separates emitted findings from coalesced entries:

```csharp
public sealed record Diagnostic(
    string Code,
    DiagnosticCategory Category,
    LogLevel Level,
    DiagnosticSource Source,
    DiagnosticPhase Phase,
    string Message,
    DiagnosticCorrelation Correlation,
    DateTimeOffset Timestamp,
    string? Id = null,
    string? TechnicalDetail = null,
    DiagnosticEvidenceReference? EvidenceReference = null);

public sealed record DiagnosticFingerprint(
    string Code,
    DiagnosticCategory Category,
    DiagnosticSource Source,
    DiagnosticPhase Phase,
    DiagnosticCorrelationScope CorrelationScope);

public sealed record DiagnosticCorrelationScope(
    ApplicationSessionId? SessionId,
    WorkflowRunId? WorkflowRunId,
    OperationId? OperationId,
    string? AccessProfileId,
    string? ServiceId,
    ProjectContextId? ProjectContextId,
    RuntimeTargetId? RuntimeTargetId,
    RunEpochId? UnitTestRunEpochId,
    LifecycleObservationId? LifecycleObservationId,
    EventStreamContextId? EventStreamContextId,
    EvidenceSnapshotId? EvidenceSnapshotId,
    OutputRequestId? OutputRequestId);

public sealed record DiagnosticEntry(
    DiagnosticFingerprint Fingerprint,
    Diagnostic Current,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int OccurrenceCount);

public sealed record DiagnosticSnapshot(
    IReadOnlyList<DiagnosticEntry> Entries,
    DateTimeOffset UpdatedAt);

public sealed record DiagnosticSnapshotChangedEventArgs(
    DiagnosticSnapshot Snapshot);
```

Identity and coalescing rules:

- `Diagnostic.Code`, `Category`, `Source`, and `Phase` are always part of the
  fingerprint.
- `DiagnosticCorrelationScope` includes the stable profile, service, project
  and runtime identities from `Diagnostic.Correlation`. Volatile workflow,
  operation, run-epoch, event-sequence and output-request correlation remains
  available on the latest occurrence but does not split the fingerprint.
- `Message`, `TechnicalDetail`, `EvidenceReference`, `Timestamp`, and the
  optional emission `Id` are not identity fields. They describe the latest
  emitted occurrence and may change while the same finding remains one
  diagnostic entry.
- Repeated emissions with the same fingerprint update `LastSeenAt`,
  `OccurrenceCount`, and `Current`.
- A materially different structured context produces a distinct fingerprint and
  therefore a distinct diagnostic entry.
- Applications present `DiagnosticSnapshot` entries as diagnostic help for the
  user. Diagnostics are not authoritative for workflow state, action gates, run
  evidence, or UnitTest result interpretation.
- Output and application logs remain the chronological surfaces for repeated
  operational events and repeated diagnostic emissions.
- Clearing diagnostics clears the shared `DiagnosticSink` and publishes an empty
  diagnostic snapshot. It must not change workflow state, action gates, run
  evidence, UnitTest result interpretation, output history, or application logs.
  In WPF this clears workflow-origin and WPF-origin current diagnostic entries
  together.

## Output And Artifact Flow

Artifact export consumes stable run epoch projections and export-service output
options.

```text
RunWorkflowService
  -> RunWorkflowPublication(Status.IsExportable, CurrentRun)
  -> application composition binds ArtifactExportService.OnWorkflowPublication
  -> ArtifactExportService
  -> IArtifactExporter
  -> OutputStateChanged / DiagnosticSink
```

Rules:

- Artifact export is triggered from workflow publication, not by exporters
  watching PLC communication directly.
- The workflow publishes only exportability and the stable run snapshot. It does
  not emit artifact-export commands, build export plans, or own export output
  state.
- The application composition root binds the application-owned
  `ArtifactExportService` as an additional workflow publication handler.
- `ArtifactExportService` triggers automatic export when `IsExportable` changes
  from `false` to `true`.
- Exporters receive `UnitTestRunEpochSnapshot` and the relevant export context.
- Exporters do not call access profile services.
- Exporters do not decide UnitTest result semantics.
- JUnit XML is produced by the JUnit exporter from the neutral run epoch model.
- Required artifact write failure after a trusted run result maps to artifact
  error and affects CLI process result.
- `ArtifactExportService` deduplicates automatic export by run-epoch id in
  addition to observing `IsExportable: false -> true` edges; repeated publication
  of the same exportable epoch must not produce another automatic export.
- The exact RunControl `Starting` event opens the new run boundary, including
  externally triggered runs. Writing `StartUnitTestAsync` does not itself create
  an epoch. Reset, invalidation, cleanup, and manual result clearing
  can clear presentation state, but they do not by themselves create a new
  automatic export trigger.
- Future manual export is an application/export-service action gated by
  `WorkflowStatusSnapshot.IsExportable`. It may export repeatedly while the
  status remains exportable.
- `OutputState.TerminalText` is a concise human-facing output trace for artifact
  coordination. It is not the artifact payload and must not be parsed by CI or
  result interpretation.
- `OutputState.TerminalText` is published as a current snapshot, not as an
  append-only application log. WPF renders that snapshot with its normal local
  terminal timestamp formatting; shared core and exporters do not stamp those
  lines themselves.

## Application Handover

WPF consumes:

- workflow state,
- action gates,
- active operation state,
- current `UnitTestRunEpochSnapshot`,
- diagnostics from the application-owned `DiagnosticSink`,
- artifact output state from `ArtifactExportService`.

CLI consumes:

- terminal workflow state,
- final or failed `UnitTestRunEpochSnapshot` where available,
- diagnostics from its application-owned `DiagnosticSink`,
- artifact output state from its application-owned `ArtifactExportService`,
- process-result mapping for exit code.

Applications own presentation and command entry. They do not own event-stream
interpretation, UnitTest result meaning, artifact serialization, or Siemens API
binding behavior.
