# Siemens PLCSIM Advanced Binding Contract

This document defines the Siemens-internal PLCSIM Advanced binding contract
between the Siemens access profile shell and PLCSIM Advanced runtime / UnitTest
communication bindings.

The Siemens binding contract assembly version is `2.0.0`.
`PlcsimRuntimeProvisioningMode.UseExisting` replaces `RequireExisting`
without an alias, retaining numeric value 0; `CreateOwned` remains 1.
Profile, binding and worker packages must use the matching contract artifact.
This change does not alter runtime ownership, PLC communication contracts or
the worker's existing-versus-owned preparation behavior.

The contract is local to the Siemens access-profile subtree. It does not define
a generic access-profile plugin contract and it must not expose PLCSIM Runtime
API objects, runtime manager pointers, instance handles, PLC handles, callback
handles, or worker objects to Common, Core, WPF, CLI, or generic access-profile
contracts.

## Purpose

The PLCSIM Advanced binding contract isolates PLCSIM Advanced Runtime API access
from the Siemens profile shell.

It covers:

- PLCSIM Advanced Runtime API availability and loadability evidence;
- runtime manager and runtime instance session ownership;
- neutral runtime session status, health, progress, diagnostics, and evidence;
- runtime discovery, preparation, control, cleanup, lifecycle, and event-stream
  communication operations over the same profile-owned runtime session.

This document defines the runtime facade/session foundation, runtime discovery,
selection, preparation, observation, control, and release/dispose boundaries,
and the UnitTest lifecycle and event-stream communication boundaries.

## API Evidence

The contract is based on the PLCSIM Advanced V8 Runtime API shape observed in
the local reference files:

- `SimulationRuntimeApi.h` under the copied PLCSIM Advanced V8 API references;
- `Siemens.Simatic.Simulation.Runtime.Api.x64.dll` and
  `Siemens.Simatic.Simulation.Runtime.Api.x86.dll` with ProductVersion
  `8.0.0.0` and FileVersion `80.0.259.0`.

Relevant API characteristics:

- the Runtime API is initialized through `InitializeApi`, which returns an
  `ISimulationRuntimeManager` interface;
- the runtime manager binds existing runtime instances through
  `CreateInterface` and creates/registers new runtime instances through
  `RegisterInstance` or related creation operations;
- runtime instance operations are exposed through `IInstance`;
- `IInstance` owns operating state, storage path, network/interface mapping,
  power/run/stop operations, symbolic tag reads/writes, callbacks/events, and
  storage cleanup operations;
- callback/event surfaces include runtime-manager-lost, configuration-changed,
  operating-state-changed, sync-point-reached, and related runtime/PLC events;
- API resources are explicitly released through `DestroyInterface` and
  `ShutdownAndFreeApi`.

These characteristics require a session-based binding boundary. Stateless calls
from the Siemens profile shell would not match the Runtime API ownership model.

## Contract Assembly

The runtime facade and DTO contracts belong to:

```text
access-profiles/siemens/src/PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts
```

The contract assembly must stay free of direct PLCSIM Advanced Runtime API
assembly references and must stay compatible with the Siemens facade/worker
boundary.

## Runtime Binding Entry Point

The Siemens shell loads a PLCSIM binding package through the Siemens-internal
binding package model. The PLCSIM binding entry point implements the existing
Siemens technology binding base contract and exposes a runtime-session open
operation. Through `ISiemensTechnologyBinding`, the entry point also implements
`ResolveReadiness(SiemensBindingReadinessRequest)`. The readiness call is the
registration-time boundary for PLCSIM-specific installed-product, Runtime API,
worker, and loadability evidence.

Materialized shape:

```csharp
public interface IPlcsimAdvancedRuntimeBinding : ISiemensTechnologyBinding
{
    Task<PlcsimRuntimeSessionOpenResult> OpenRuntimeSessionAsync(
        PlcsimRuntimeSessionOpenRequest request,
        CancellationToken cancellationToken);
}
```

For the V8 binding, `ResolveReadiness` is read-only. It checks PLCSIM Advanced
Runtime API evidence from the documented Runtime API registry entry
`SOFTWARE\Wow6432Node\Siemens\Shared Tools\PLCSIMADV_SimRT\Path`, the expected
`API\8.0` subdirectory, the x64 Runtime API DLL, the Runtime API interface
version `0x00080000`, and the required Runtime API entry points
`RuntimeApiEntry_Initialize` and `RuntimeApiEntry_DestroyInterface`.

Missing product or API evidence is reported as `Unavailable`. Registry security
or read failures, Runtime API DLL inspection/load failures, and missing required
entry points are reported as `Error` with diagnostics. The readiness probe must
not create runtime instances, start PLCSIM Advanced, open runtime manager ports,
power on instances, or modify runtime storage.

`OpenRuntimeSessionAsync` opens or binds a PLCSIM Runtime API work session. It
does not mean that a runtime has been prepared, powered on, downloaded, or made
ready for UnitTest communication. Those operations are separate session-bound
operations defined below.

The binding implementation owns concrete PLCSIM API initialization, worker
startup if needed, Runtime API DLL loading, runtime manager handles, instance
handles, callbacks, event handles, and API cleanup.

## Runtime Session Ownership

The successful open result returns one `IPlcsimRuntimeSession`.

Materialized shape:

```csharp
public interface IPlcsimRuntimeSession
{
    PlcsimRuntimeSessionInfo Runtime { get; }

    PlcsimRuntimeHealthState Health { get; }

    event EventHandler<PlcsimRuntimeHealthChangedEventArgs>? HealthChanged;

    event EventHandler<PlcsimRuntimeStateChangedEventArgs>? RuntimeStateChanged;

    event EventHandler<PlcsimRuntimeProgressSnapshot>? ProgressChanged;

    Task<PlcsimRuntimeDiscoveryResult> DiscoverRuntimeTargetsAsync(
        PlcsimRuntimeDiscoveryRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRuntimePrepareResult> PrepareRuntimeAsync(
        PlcsimRuntimePrepareRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRuntimeObservationResult> ObserveRuntimeAsync(
        PlcsimRuntimeObservationRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRuntimeControlResult> StartRuntimeAsync(
        PlcsimRuntimeControlRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRuntimeControlResult> StopRuntimeAsync(
        PlcsimRuntimeControlRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRuntimeReleaseResult> ReleaseRuntimeAsync(
        PlcsimRuntimeReleaseRequest request,
        CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}
```

Session ownership rules:

- the Siemens profile shell owns the session object after a successful open;
- the session owns all PLCSIM API-adjacent resources below the Siemens boundary;
- session disposal unregisters callbacks/events, releases runtime instance
  interfaces, shuts down worker resources where applicable, and releases the
  runtime manager/API interface according to binding ownership;
- disposal must be idempotent and best-effort;
- session disposal must not expose PLCSIM API handles or worker handles to the
  Siemens profile shell;
- the session must publish runtime state changes through `RuntimeStateChanged`,
  not through progress messages or command results;
- the session must publish terminal `Disposed` health after intended disposal;
- if `OpenRuntimeSessionAsync` fails, no session is returned.

## Open Request

Materialized shape:

```csharp
public sealed record PlcsimRuntimeSessionOpenRequest(
    PlcsimRuntimeApiExpectation ApiExpectation,
    PlcsimRuntimeReference? RuntimeReference,
    PlcsimRuntimeStoragePreference StoragePreference,
    PlcsimRuntimeCleanupPreference CleanupPreference,
    string? CorrelationId);
```

The request contains only session-binding and runtime-selection hints needed by
the PLCSIM binding. It must not contain PLCSIM API objects, runtime manager
interfaces, instance interfaces, event handles, callback delegates, PLC handles,
or worker objects.

The runtime-session request does not carry UnitTest RunControl or EventBuffer
contract-version expectations. Those expectations belong to UnitTest
communication preparation, because RunControl `ContractInfo` validation and
EventBuffer contract validation are properties of the prepared UnitTest
communication session.

## Open Result

Materialized shape:

```csharp
public sealed record PlcsimRuntimeSessionOpenResult(
    PlcsimOperationStatus Status,
    IPlcsimRuntimeSession? Session,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded =>
        Status is PlcsimOperationStatus.Succeeded
            or PlcsimOperationStatus.SucceededWithWarnings;
}
```

Result rules:

- `Succeeded` and `SucceededWithWarnings` require `Session` to be non-null;
- every non-success status requires `Session` to be null;
- bindings must catch API/worker exceptions at this boundary and return a
  structured result instead of allowing implementation exceptions to cross the
  contract boundary;
- diagnostics and evidence must be neutral and actionable; concrete PLCSIM API
  objects must not be serialized or exposed.

## Operation Status

Materialized shape:

```csharp
public enum PlcsimOperationStatus
{
    Succeeded,
    SucceededWithWarnings,
    Unavailable,
    Rejected,
    Failed,
    Cancelled,
    TimedOut,
    Unclear
}
```

Status meaning:

| Status | Meaning |
| --- | --- |
| `Succeeded` | Operation completed as requested. |
| `SucceededWithWarnings` | Operation completed but produced warnings that should be logged or surfaced as diagnostics/evidence. |
| `Unavailable` | Required PLCSIM environment, API, license, runtime manager, communication interface, or selected runtime is not usable or not present. This is distinct from an operation failure. |
| `Rejected` | The binding rejected the request because inputs or current state are invalid for the requested operation. |
| `Failed` | Operation failed after it was attempted against an available API/session. |
| `Cancelled` | Operation was cancelled through the provided cancellation token or binding-owned cancellation path. |
| `TimedOut` | Operation did not reach the expected API/session outcome within the defined operation timeout. |
| `Unclear` | The binding cannot prove whether the requested operation completed, failed, or partially took effect. |

Typical Runtime API evidence for `Unavailable` includes API load failure,
uninitialized API, missing runtime manager, missing or unavailable runtime,
license absence, unavailable communication interface, or unsupported local
runtime environment.

## Runtime Health

Materialized shape:

```csharp
public enum PlcsimRuntimeHealthState
{
    Unknown,
    Healthy,
    Unavailable,
    Degraded,
    RevalidationRequired,
    Invalidated,
    Faulted,
    Disposed
}
```

Health meaning:

| Health | Meaning |
| --- | --- |
| `Unknown` | The binding has not established reliable runtime trust yet. |
| `Healthy` | Runtime manager/session trust is currently usable. |
| `Unavailable` | The PLCSIM environment/API/runtime is not usable or not present. |
| `Degraded` | Runtime access is still usable but warning evidence exists. |
| `RevalidationRequired` | Runtime trust may still be recoverable but the shell must revalidate before relying on it. |
| `Invalidated` | The selected runtime/session context is no longer trustworthy, for example because the instance disappeared or the runtime manager configuration changed incompatibly. |
| `Faulted` | The session was usable but failed due to API/worker/runtime communication failure. |
| `Disposed` | The session was intentionally disposed and must not be used. |

The Siemens profile shell maps session health to application-facing service
health. Runtime/session health is not a replacement for operation progress or
operation results.

## Runtime Discovery, Selection, Preparation, And Observation

Runtime discovery, selection, preparation, and state observation are
session-bound operations. They must stay below the Siemens profile shell and
must not expose PLCSIM Advanced Runtime API objects, manager interfaces,
instance interfaces, callback handles, event handles, or worker objects.

The PLCSIM binding discovers runtime candidates from the Runtime API session.
Runtime candidates are neutral DTOs derived from Runtime API evidence such as
registered instance information, instance name or identifier, runtime
availability, locality, and cheap state evidence. Runtime candidates are not
runtime handles. The Siemens profile shell may use them for selection,
progress, diagnostics, and validation, but it must not rely on concrete PLCSIM
API identity.

Runtime selection uses the Siemens profile configuration and the resolved
profile-owned runtime registry:

- an explicit runtime reference selects the matching local runtime candidate;
- an empty runtime reference may select the only usable local runtime candidate
  when exactly one exists;
- an empty runtime reference is rejected as ambiguous when multiple usable local
  runtime candidates exist;
- if no usable runtime candidate exists, runtime creation or registration is
  allowed only when the request/configuration explicitly permits the binding to
  create an owned runtime.

The binding distinguishes attached runtimes from owned runtimes:

- an attached runtime existed independently of the current binding preparation
  and must not be deleted or storage-cleaned by the binding;
- an owned runtime was created or registered by the current binding preparation
  for the current Siemens profile session;
- ownership is binding-internal session state and must be reflected in neutral
  evidence, diagnostics, and later release/cleanup behavior.

The prepare request carries an explicit provisioning mode:

- `UseExisting`: the binding resolves and prepares an already registered
  PLCSIM Advanced runtime instance. It must not call creation/registration,
  power-on, power-off, unregister, or storage mutation operations as an implicit
  side effect. Missing or ambiguous candidates are reported as unavailable or
  rejected with diagnostics.
- `CreateOwned`: the binding may create/register a new runtime instance and
  marks the prepared runtime as binding-owned. Ownership and cleanup are then
  explicit session state and release behavior.

If `CreateOwned` fails after registration, the binding owns compensation of the
partially created resource. A preparation result may remain `Failed` only when
the binding can prove that compensation removed the registration. A failed or
unprovable unregister makes the resource postcondition `Unclear`; the result
must contain the original preparation diagnostic plus bounded resource identity
and per-step compensation evidence. The binding must not return the partial
runtime as prepared. An unclear postcondition invalidates the runtime session,
and recovery requires a new session plus explicit inspection/removal of the
evidenced PLCSIM instance before retry. Technical session close must not perform
an unreported second destructive cleanup attempt.

A failed power-off does not by itself make the postcondition unclear when a
later unregister proves that the runtime registration was removed. It remains a
separate compensation warning alongside the original preparation failure.

The prepare request also carries the configured runtime CPU type and prepare
timeout:

- `CpuType` is a neutral Siemens-internal enum. V1 supports `S71500` only. The
  concrete binding maps it to the Runtime API CPU enum/value required by its
  access API version.
- `Timeout` is the operation boundary for runtime preparation. It applies to
  binding-owned selection/creation/registration/initial observation work. The
  binding may use Runtime API timeout overloads where available; otherwise it
  must enforce the timeout around its own observation/worker protocol.

Runtime preparation prepares the selected or created runtime for later project
transfer and PLC control. It may validate or configure storage, local runtime
mode, network/interface mapping, tag-list readiness, and runtime identity as
needed by the PLCSIM Advanced Runtime API. `CreateOwned` may power on the newly
created instance to make it ready for engineering transfer. Power-on is not PLC
RUN. Preparation alone does not imply PLC RUN, project download, UnitTest
communication readiness, or event-stream readiness; those remain separate
profile/service operations.

V1 supports local PLCSIM Advanced runtime usage only. Remote runtime API
connections are explicitly out of scope for V1 even though the Runtime API
contains remote-connect operations. Network mode and interface handling should
therefore validate or establish a local-compatible setup without exposing
PLCSIM-specific network handles to the Siemens profile shell.

Runtime storage handling follows the configured Siemens profile intent and the
runtime ownership:

- configured storage is assigned directly to the newly registered owned runtime
  instance before power-on; bindings must not mutate the PLCSIM Advanced global
  default storage path for this purpose;
- attached runtimes should not have their storage path changed unless the
  profile configuration explicitly requires such mutation and the binding can
  prove that it is safe;
- the default runtime-storage location is separate from application artifacts
  and should be based on an operating-system temporary location unless a profile
  configuration overrides it.

Runtime instance names are PLCSIM API inputs, not arbitrary application ids.
Bindings must validate names before calling `RegisterInstance`. For the V8
Runtime API, names should use a conservative alphanumeric/underscore subset and
stay within the documented instance-name length limits. Invalid names are
reported as `Rejected`, not hidden behind generic PLCSIM API failures.

Runtime state observation is session-owned. The binding maps Runtime API
operating-state evidence and callbacks/events into neutral runtime state change
events, health, diagnostics, and evidence. Runtime API callbacks/events must
never cross the binding boundary. Runtime-manager loss or incompatible
configuration changes invalidate or revalidate the session according to the
observed state.

## Runtime State Events

Runtime state changes are a first-class binding signal. They must not be encoded
as progress text, and they must not be inferred from application start/stop
command results. The authoritative runtime state publication is
`RuntimeStateChanged`. Facades and profile services that expose PLC-control state
must update and publish that state from `RuntimeStateChanged`, not from the
return value of `StartRuntimeAsync` or `StopRuntimeAsync`.

Materialized event shape:

```csharp
public interface IPlcsimRuntimeSession
{
    event EventHandler<PlcsimRuntimeStateChangedEventArgs>? RuntimeStateChanged;
}

public enum PlcsimRuntimeStateChangeKind
{
    OperatingStateChanged,
    RuntimeManagerLost,
    ConnectionLost,
    SessionDisposed
}

public sealed record PlcsimRuntimeStateChangedEventArgs(
    PlcsimRuntimeStateChange Change);

public sealed record PlcsimRuntimeStateChange(
    string RuntimeSessionId,
    PlcsimRuntimeReference? RuntimeReference,
    PlcsimRuntimeStateChangeKind Kind,
    PlcsimRuntimeControlState? PreviousState,
    PlcsimRuntimeControlState CurrentState,
    DateTimeOffset ObservedAt,
    string Message,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence);
```

State-event rules:

- `OperatingStateChanged` is raised from the PLCSIM Advanced runtime instance
  operating-state event, such as `OnOperatingStateChanged` in the V8 Runtime
  API.
- The event payload uses neutral `PlcsimRuntimeControlState` values. Raw Siemens
  enum values may be retained only as diagnostic/evidence text.
- `RuntimeManagerLost` and `ConnectionLost` set `CurrentState` to `Invalid`,
  publish runtime health `Invalidated`, and require the Siemens profile shell to
  invalidate dependent runtime and UnitTest communication context.
- `SessionDisposed` is emitted only for intended session shutdown/disposal and
  must not be mistaken for a PLC fault.
- Worker callbacks must do minimal work. They enqueue or forward immutable event
  data into the binding-owned event pipeline and must not perform heavy PLCSIM
  operations, blocking waits, application workflow calls, or direct UI work.
- `ObserveRuntimeAsync` remains available for initial snapshots, revalidation,
  and diagnostics. It is not a polling-based PLC state monitor and is not used
  to serialize command handling with state-reaction monitoring.
- `StartRuntimeAsync` and `StopRuntimeAsync` report success when their
  asynchronous PLCSIM API function could be invoked without an error. They do
  not wait for a subsequent runtime state event. Any state value included in the
  operation result is request/API evidence only and must not be used as an
  authoritative state publication.

## Runtime Release And Dispose Boundary

The normal runtime release/cleanup path is a session-bound operation. The
Siemens profile shell triggers the release/cleanup point through the
application-facing profile services. The PLCSIM binding executes the concrete
release behavior from the explicit application-authorized request and its
internal session state.

Shared Core selects `KeepResource` for `UseExisting` and `DestroyResource` for
`CreateOwned` and passes that intent in `PlcRuntimeReleaseRequest`. The Siemens
adapter maps it to binding-internal `Keep` or `Release` cleanup preference.
There is no operational `cleanup.runtime` profile-payload field controlling
this decision. The binding combines the authorized intent with its knowledge of
runtime identity, ownership and storage; it does not infer destruction intent
from session disposal or earlier provisioning alone.

Release rules:

- attached runtimes are not storage-cleaned by the binding;
- runtimes released with `KeepResource` keep their runtime/storage state
  where technically possible;
- owned runtimes released with `DestroyResource` may be stopped, released, and
  storage-cleaned by the binding when technically possible;
- release/cleanup failures are reported as operation results, diagnostics, and
  logs, but they do not retroactively change UnitTest result semantics.

`IPlcsimRuntimeSession.CloseAsync` is a technical fallback, not the normal
policy-driven cleanup path. Disposal without a preceding release must:

- unregister callbacks/events;
- stop binding-owned observers, workers, and IPC resources;
- release API/session handles as far as possible;
- publish disposed health;
- log or report that the session was disposed without normal release.

Disposal without release must not execute storage cleanup, must not implicitly
infer `DestroyResource` intent, and must not delete or reset an attached or
owned runtime as a hidden side effect.

## Runtime Start, Stop, Release, And Cleanup

Runtime control operations are session-bound. They operate on the prepared
runtime selected or created by the current `IPlcsimRuntimeSession`. They must
return structured `PlcsimOperationStatus` results and must not throw
PLCSIM-specific implementation exceptions across the binding contract boundary.

Materialized operation shape:

```csharp
public interface IPlcsimRuntimeSession
{
    Task<PlcsimRuntimeControlResult> StartRuntimeAsync(
        PlcsimRuntimeControlRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRuntimeControlResult> StopRuntimeAsync(
        PlcsimRuntimeControlRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRuntimeReleaseResult> ReleaseRuntimeAsync(
        PlcsimRuntimeReleaseRequest request,
        CancellationToken cancellationToken);
}
```

The request and result DTOs are neutral. They carry operation identifiers,
timeouts, expected target state, configured release intent, diagnostics, and
evidence. They must not carry PLCSIM Runtime API interfaces, callback handles,
event handles, or worker handles.

### Start Runtime

Start means that the prepared PLCSIM runtime is asked to enter a state where TIA
transfer and later UnitTest communication can proceed. The binding maps this
request to the Runtime API power/run operation appropriate for the currently
observed runtime state.

The binding uses the current Runtime API operating-state evidence only to choose
the immediate API command safely:

- `Off` may require the API's power-on/start request appropriate for that state;
- `Stop` may require the API's run request;
- `Run` may be treated as idempotent command success;
- transitional, invalid, or unknown states may reject the immediate request when
  the API does not permit a deterministic command.

The operation timeout bounds only the attempt to invoke the asynchronous PLCSIM
API function. The binding must not extend that call by waiting serially for a
later `OperatingStateChanged` event. Cancellation cancels the local API request where
supported and returns `Cancelled`; it must not imply a hidden stop or cleanup
operation. Shared core performs independent command-reaction monitoring from
later profile-published neutral `PlcState` observations.

### Stop Runtime

Stop means that the prepared PLCSIM runtime is asked to leave PLC run state. The
binding maps this to the asynchronous Runtime API stop command and returns its
operation result without waiting for the later operating-state event.

Rules:

- `Stop` is idempotent success when the runtime is already stopped;
- `Run` should be transitioned to `Stop` through the Runtime API stop operation;
- `Off` is considered stopped for application workflow but must be reported with
  evidence because it is not the same runtime state as a powered stopped PLC;
- transitional states may reject the immediate stop request when the Runtime API
  does not permit a deterministic command; the command call itself does not wait
  for a later stable-state event;
- stop does not release the runtime session, unregister callbacks, delete
  storage, or apply runtime resource-destruction intent.

### Release Runtime

Release is the normal fachliche close path for the prepared runtime. It is
distinct from `CloseAsync`.

The Siemens profile shell calls release when the application workflow reaches a
runtime release or session cleanup point. The release request contains the
application-authorized release intent mapped by the profile. The binding combines it with
session-owned state:

- whether the runtime is attached or owned;
- whether the binding created or registered the runtime;
- whether the binding started the runtime;
- current operating state;
- current storage path;
- active callbacks, observers, workers, and IPC resources.

Release must first make the binding unable to publish further normal runtime
operation events for the released session. It then releases observation
resources and API/session handles in an order that prevents stale callbacks from
re-entering disposed state.

An owned-runtime unregister invalidates the concrete Runtime API instance handle
as soon as the API accepts the operation. The worker must therefore capture the
neutral runtime identity/state needed for final disposal publication before
unregister, suppress normal operating-state callbacks during the release window,
and never read the invalidated handle after successful unregister. Runtime
Manager loss during that window remains terminal and must not be reported as
successful cleanup. A genuine unregister failure may keep the session retryable
only when the worker can restore its operating-state subscription, reread the
current runtime state, and reconcile any state change that occurred while the
subscription was detached. Failed recovery invalidates the session.

For attached runtimes, release must not delete storage and must not unregister or
destroy a runtime that existed independently of the session. It releases only
the binding-owned session resources.

For runtimes released with `KeepResource`, release must keep runtime and
storage state where technically possible and release only binding-owned session
resources. The result evidence should state that the owned runtime was kept.

For owned runtimes released with `DestroyResource`, release may stop or power
down the runtime, release the binding-created runtime interface, and call Runtime
API storage cleanup when the binding can prove that the storage belongs to the
owned runtime. Storage cleanup must never be attempted for an attached runtime or
for a storage path that the binding cannot associate with the owned runtime.

Release is idempotent. A second release call returns success or warning evidence
that the session was already released.

### Cleanup Failure Semantics

Cleanup failure is reported as the release operation result and as diagnostic/log
evidence. It does not alter already determined UnitTest test-case, test-suite,
or artifact result semantics. It may still make the overall application command
or CLI execution fail because environment cleanup did not complete as requested.

Partial cleanup must be reported explicitly. If the binding cannot determine
whether PLCSIM accepted or completed a release/cleanup operation, it returns
`Unclear`, not `Succeeded`.

The binding must prefer conservative behavior over destructive cleanup:

- do not delete storage on ambiguous ownership;
- do not infer `DestroyResource` intent from `CloseAsync`;
- do not hide cleanup failures behind successful session disposal;
- do not keep publishing normal runtime state after release completed.

## UnitTest Communication Session Ownership

UnitTest communication is prepared after the runtime is prepared, the project is
transferred, and the PLC runtime is running. The Siemens profile shell exposes
this to Common/Core as one `CommunicationSession`. The PLCSIM binding owns the
lower-level communication session that validates and accesses the PLC-side
RunControl and EventBuffer areas through PLCSIM Advanced direct or symbolic PLC
access.

The PLC-side contracts are the source of truth for UnitTest communication
semantics:

- `docs/references/s7unittest/contracts/plcsim-advanced-communication-contract.md`
  defines the PLCSIM Advanced direct-access symbolic roots, writable request
  fields, RunControl projection, and `ContractInfo` validation points;
- `docs/references/s7unittest/contracts/event-stream-model-contract.md` defines
  event-stream payload, FIFO handover, sequence id, acknowledgement,
  completeness, and single-consumer semantics.

This binding contract does not redefine those semantics. It defines how the
PLCSIM Advanced binding validates, observes, reports, and maps those existing
contracts into Siemens-internal operation results, health, observations,
diagnostics, and evidence. Siemens profile configuration may provide symbolic DB roots and
operation policies; it must not redefine field layout, enum values, FIFO rules,
event meaning, result semantics, or compatibility rules below those roots.

The PLCSIM communication session is shared by the Siemens profile's
application-facing `IUnitTestLifecycleService` and `IUnitTestEventStreamService`
adapters:

- the lifecycle adapter uses the shared session for RunControl contract
  validation, command writes, command-bit/lifecycle observations, and lifecycle-status
  observation;
- the event-stream adapter uses the shared session for EventBuffer contract
  validation, FIFO/status/buffer reads, event-batch publication, and processed
  event release;
- both adapters must use the same prepared communication context and
  `CommunicationSessionId`;
- neither adapter owns the underlying PLCSIM Runtime API session independently;
- releasing communication invalidates both adapters for the released
  communication session.

Materialized operation shape:

```csharp
public interface IPlcsimUnitTestCommunicationBinding : ISiemensTechnologyBinding
{
    Task<PlcsimCommunicationPrepareResult> PrepareCommunicationAsync(
        PlcsimCommunicationPrepareRequest request,
        CancellationToken cancellationToken);
}

public interface IPlcsimUnitTestCommunicationSession
{
    PlcsimCommunicationSessionInfo Communication { get; }

    PlcsimCommunicationHealthState Health { get; }

    event EventHandler<PlcsimCommunicationHealthChangedEventArgs>? HealthChanged;

    event EventHandler<PlcsimRunControlObservationChangedEventArgs>? RunControlObservationChanged;

    event EventHandler<PlcsimEventBatchReceivedEventArgs>? EventBatchReceived;

    Task CloseAsync(CancellationToken cancellationToken);
}
```

The prepared communication session is binding-internal. The Siemens profile
shell maps it to the application-facing `CommunicationSession` and stores the
binding session as profile-owned state. Common, Core, WPF, CLI, and generic
access-profile contracts see only the application-facing session id and neutral
service results.

The prepare request contains neutral information derived from the already
prepared runtime and transferred project:

- operation id plus separate preparation and active-session revalidation
  timeout/cancellation expectations;
- the prepared PLCSIM runtime session reference in a Siemens-internal neutral
  form;
- selected project/runtime target references;
- configured UnitTest symbolic names such as RunControl and EventBuffer data
  block names;
- expected PLCSIM Advanced direct-access and UnitTest event-stream contract
  versions;
- typed communication-session worker options such as observation strategy,
  regular monitoring interval, active drain interval, and maximum events per
  published batch.

The request must not contain PLCSIM Runtime API pointers, PLC handles, callback
handles, raw worker handles, or Common/Core mutable workflow objects.

Communication preparation and active-session revalidation are separate
application-owned policies:

- `PrepareTimeout` must be positive and bounds only initial communication
  preparation, including tag-list and communication-contract validation.
- `RevalidationTimeout` must be zero or positive and applies only after the
  communication session was established. A zero value disables automatic
  terminal timeout handling; the worker continues revalidation until access
  recovers, the operation is cancelled, the session is closed, or a terminal
  failure is observed.
- The binding and worker must preserve both values across their IPC boundary.
  They must not substitute a binding-local project/configuration value.
- These execution policies are session input. They are not persisted in the
  access-profile project configuration payload.

Preparation responsibilities:

1. Ensure the runtime session is usable and the PLC is in a state where symbolic
   UnitTest access can be validated.
2. Resolve the configured RunControl and EventBuffer symbolic roots.
3. Validate `DB_UnitTestRunControl.ContractInfo` before any RunControl command
   field or event-buffer release field is written.
4. Validate `DB_UnitTestEventBuffer.ContractInfo` before event-buffer data is
   interpreted.
5. Establish one configured communication observation/trigger strategy for the
   whole communication session. RunControl lifecycle observation, EventBuffer
   observation, and release acknowledgement must use the same strategy.
6. Establish communication-session identity, evidence, and health for both
   lifecycle and event-stream adapters.
7. Publish neutral diagnostics/evidence for unavailable symbols, unsupported
   contract versions, inconsistent read evidence, or communication loss.

No automatic observation-strategy fallback is allowed. If a caller configures a
PLC-cycle strategy and the binding cannot establish the required PLCSIM
sync-point/cycle path, preparation must fail with an explicit diagnostic instead
of silently switching to polling.

Observation intervals are application/profile policy. The binding requires a
positive drain interval. Polling requires a positive monitoring interval after
the profile default is resolved. PLC-cycle observation accepts an omitted
monitoring interval to observe every usable cycle; when present, that interval
must be positive and acts as the minimum time between complete idle
observations. The binding must preserve the resolved strategy-specific values
across its worker boundary. Known EventBuffer backlog or pending PLC-side
release confirmation bypasses PLC-cycle monitoring throttling. Runtime API
processing time is additive; neither interval defines a fixed periodic
deadline.

The configured maximum events per batch is a positive integer. The binding
must cap its effective value against the PLC-reported EventBuffer length and
capture only the oldest contiguous FIFO prefix. It must validate and release
that prefix before publication. A validated post-snapshot determines whether
the next observation remains on the drain cadence. Invalid, incomplete, or
inconsistent snapshots are neither released nor published.

The binding must not impose other arbitrary interval or batch clamps based on
an IPC integer representation. Operating-system timer resolution and provider
API wait precision are implementation characteristics, not additional
configuration contract limits.

Preparation must not start a UnitTest run, reset RunControl, release event-buffer
entries, or interpret event-stream result evidence. It establishes only that the
communication boundary is compatible and usable for later lifecycle and
event-stream operations.

Communication release is distinct from runtime release. It stops lifecycle and
event-stream observations, prevents further command or event-stream operations
for the released communication session, and releases binding-owned communication
resources. It does not stop the PLC runtime, release the PLCSIM runtime session,
or apply runtime storage cleanup.

The communication close boundary is binding-internal. The Siemens profile shell
may release application-facing UnitTest communication through its lifecycle and
event-stream services, but Common/Core/WPF/CLI do not receive a PLCSIM-specific
`CloseCommunication` API. The PLCSIM facade translates the profile release into
one worker-level close request for the prepared communication session. That
request closes only the UnitTest communication context inside the worker and
must not terminate the runtime worker process or release the prepared PLCSIM
runtime session.

After communication close:

- `IPlcsimUnitTestCommunicationSession.Health` becomes `Disposed` for an
  intended close;
- RunControl validation, command, and observation calls for that session return
  `Unavailable` or `Rejected` with communication-session evidence instead of
  silently reusing stale worker state;
- EventBuffer validation and monitoring calls follow the same closed-session
  rule, and the worker must not perform an internal release after close;
- a second close is idempotent;
- worker/IPC interruption during close is treated as bounded cleanup evidence.
  If the worker is already gone, the facade may report
  `SucceededWithWarnings` because there is no longer a live worker-owned
  communication context to leak.

`IPlcsimUnitTestCommunicationSession.CloseAsync` is a technical fallback. It
must release callbacks, readers, workers, subscriptions, and buffered
communication resources. It must not write hidden RunControl commands, must not
request EventBuffer release for data that was not explicitly processed, and must
not perform runtime cleanup.

Only a `Healthy` communication session is operational. A closing, invalidated,
faulted, or disposed session rejects further validation, command, observation,
and monitoring operations without forwarding them to the worker; worker-internal
release work is cancelled or rejected at the same boundary. Close
failure or cancellation still makes the facade session terminal: it publishes
safe RunControl defaults once, detaches worker event subscriptions, and marks the
session `Invalidated`. If the worker cannot prove that its observation loop was
stopped, the facade terminates that worker session best-effort because reusing it
would allow stale communication work to cross a later session boundary.

The worker must retain ownership of its observation execution and cancellation
source until the observation has actually stopped. A stop timeout is a failed
close, not a successful or idempotent stop. A new observation loop or
communication session must not start while a previous loop is still alive.
Normal successful communication close releases observation execution resources
while keeping the runtime worker available so an external PLC stop and later
restart can prepare a fresh communication session on the same runtime session.

## RunControl Validation, Commands, And Lifecycle Observation

RunControl access is owned by the prepared PLCSIM UnitTest communication
session. The Siemens profile's application-facing lifecycle adapter maps
`IUnitTestLifecycleService` operations to this binding-owned RunControl
boundary. The binding must keep PLC symbolic/direct access details below the
Siemens binding boundary.

Materialized operation shape:

```csharp
public interface IPlcsimUnitTestCommunicationSession
{
    Task<PlcsimRunControlValidationResult> ValidateRunControlAsync(
        PlcsimRunControlValidationRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRunControlCommandResult> RequestRunControlCommandAsync(
        PlcsimRunControlCommandRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRunControlObservationResult> ObserveRunControlAsync(
        PlcsimRunControlObservationRequest request,
        CancellationToken cancellationToken);

    event EventHandler<PlcsimRunControlObservationChangedEventArgs>? RunControlObservationChanged;
}
```

The request/result DTOs are neutral. They may contain command kind, operation id,
expected contract versions, timeouts, current `CommunicationSessionId`,
configured RunControl symbolic root, and neutral evidence. They must not contain
PLCSIM API objects, PLC handles, callback handles, event handles, or raw PLC
memory pointers.

### RunControl Contract Validation

The binding must read and validate `DB_UnitTestRunControl.ContractInfo` before
writing RunControl lifecycle command fields or the event-buffer release command
field, as required by the PLCSIM Advanced direct-access communication contract.
A missing, unreadable, unsupported, or incompatible RunControl contract prevents
command access and must be reported as `Unavailable`, `Rejected`, or `Failed`
according to the observed cause.

Validation must produce neutral evidence containing the observed contract
version and framework version where available. It must not write any RunControl
field. It must not infer compatibility from successful tag reads outside the
RunControl `ContractInfo` data.

RunControl validation is distinct from EventBuffer validation. At the binding
boundary, RunControl compatibility enables command and delete-request field
access. EventBuffer compatibility enables event-buffer interpretation.

### Command Requests And Lifecycle Observation

Start, abort, and reset are positive-edge RunControl requests through the
symbolic command fields defined by the PLCSIM Advanced direct-access
communication contract. A command request writes the selected command bit once.
The command request result only reports whether the PLCSIM Runtime API accepted
the write operation at the binding boundary. It does not wait for PLC-side
command-bit reset and does not prove that the PLC RunControl state machine has
accepted, started, aborted, or reset the run.

PLC-side command reaction is lifecycle observation. It is derived from
subsequent RunControl observations: command-bit reset where observed and a
compatible `Status.Execution` projection. This observation is not part of the
command-write call result. It does not mean the UnitTest run completed and does
not provide final test result evidence.

Command-specific observation rules:

- `Start` may be requested only when the application-facing lifecycle gate
  observes `Status.Execution.ExecutionStatus = Idle` and
  `Status.Execution.Ready = TRUE`. Observation accepts subsequent lifecycle
  projection compatible with starting, running, completing, or already completed
  execution.
- `Abort` may be requested while a run is active or abort is already in
  progress. Observation accepts subsequent lifecycle projection compatible with
  aborting, completed, or already terminal execution.
- `Reset` may be requested to return RunControl to the reset-defined startable
  base state. Observation accepts subsequent lifecycle projection compatible with
  reset handling or the resulting `ExecutionStatus = Idle` plus `Ready = TRUE`
  base state.

If the expected lifecycle projection is not observed by the component that owns
observation, that later observation path reports `TimedOut` or `Unclear`
according to the available evidence. The command-write result itself must remain
the API-write boundary result. If command access is blocked by contract mismatch,
not-ready state, a pending conflicting command observed by lifecycle policy, or
invalid communication session, the lifecycle service returns `Rejected` or
`Unavailable` and no command bit is written.

Cancellation stops the local command write request and returns `Cancelled` when
the write did not complete. It must not write a compensating RunControl command.
Recovery from a cancelled command is a new explicit workflow action after the
current RunControl projection has been observed again.

### Lifecycle Observation

The binding observes the RunControl status projection defined by the PLCSIM
Advanced direct-access communication contract as command-handshake and lifecycle
projection. It maps that projection to neutral RunControl lifecycle evidence for
the Siemens profile's lifecycle adapter. The binding should retain raw
classification values and decoded meanings in neutral evidence where practical.
Whenever a binding-owned command response, explicit observation request, polling
cycle, or PLC-cycle observation detects a new trusted RunControl projection, the
communication session publishes `RunControlObservationChanged`. The Siemens
profile lifecycle adapter consumes that event; WPF, CLI, and shared core must
not read Siemens PLC symbols directly.

`PlcsimRunControlObservation` includes four independent PLC-owned values:

- `ExecutionStatus` as the coarse execution phase;
- `ExecutionSignals` containing `Uninitialized`, `ConfigError`, `Ready`,
  `Running`, `Completed`, `Aborted`, `Inactive`, and `Error` as independent
  flags;
- `State` as the detailed RunControl state-machine value;
- `Result` as the RunControl result projection.

The worker reads these fields from one observation cycle, decodes the numeric
contract values once, and transports them without reconstructing one axis from
another. The facade and Siemens profile preserve the values independently.
Convenience booleans may derive from `ExecutionSignals` but must not become
duplicated stored state. The result and signals remain lifecycle/plausibility
evidence only; they are not authoritative testcase, aggregation, artifact, or
CLI-exit evidence.

The PLC-owned `Status.Execution.State` value is the RunControl state-machine
projection. `Status.Execution.Ready` is a separate exposed start-gate signal and
must not be collapsed into the state-machine value. `Status.Execution.ExecutionStatus`
is a coarse classification for handshake plausibility, diagnostics, and stable
application-level gating.

Lifecycle observation rules:

- the application-level start gate requires `ExecutionStatus = Idle` and
  `Ready = TRUE`;
- `Ready = TRUE` by itself does not mean the RunControl state machine is idle;
- `State = Idle` by itself does not mean the RunControl is startable, because
  custom PLC-side RunControl implementations may expose more detailed states
  while keeping the coarse `ExecutionStatus` stable;
- `Completed = TRUE` and `ExecutionStatus.Completed` are PLC-side lifecycle
  completion evidence, but not final UnitTest result evidence;
- abort projection indicates command/lifecycle state, but final abort/result
  evidence still comes from the event stream when available;
- `ExecutionStatus.Starting`, `Running`, `Completing`, `Aborting`, and `Reset`
  are progress states;
- `Error`, `Unknown`, `Inactive`, and `NotReady` block normal startability and
  must produce actionable evidence.

Contradictory signal combinations remain observable and may produce diagnostics;
the binding must not silently normalize them into a different status, detailed
state, or result. When the communication session closes or faults, the facade
publishes the safe default of unknown execution status, no signals, unknown
detailed state, and not-evaluated result. Communication health and diagnostics,
not synthetic PLC signals, report the failure.

RunControl status is not the normative event-log, result, aggregation, or
artifact evidence source. The binding must not synthesize passed/failed/error
test results from RunControl status fields. Event-stream evidence remains the
authoritative source for run result details and completeness.

Lifecycle observation becomes invalid when the communication session is released,
the runtime session is invalidated, the RunControl contract becomes invalid, or
communication health is lost. Invalidation must be surfaced through neutral
health/progress/evidence and through the Siemens profile's application-facing
service health.

## EventBuffer Validation, Intake, Release, Backpressure, And Invalidation

EventBuffer access is owned by the same prepared PLCSIM UnitTest communication
session as RunControl access. The Siemens profile's application-facing
event-stream adapter maps `IUnitTestEventStreamService` operations to this
binding-owned EventBuffer boundary. The binding must preserve the producer-owned
event-stream semantics from the UnitTest event-stream model.

Materialized operation shape:

```csharp
public interface IPlcsimUnitTestCommunicationSession
{
    Task<PlcsimEventBufferValidationResult> ValidateEventBufferAsync(
        PlcsimEventBufferValidationRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimEventBufferResetResult> RequestEventBufferResetAsync(
        PlcsimEventBufferResetRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimEventStreamMonitoringResult> StartEventStreamMonitoringAsync(
        PlcsimEventStreamMonitoringRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimEventStreamMonitoringResult> StopEventStreamMonitoringAsync(
        PlcsimEventStreamStopRequest request,
        CancellationToken cancellationToken);
}
```

The public request/result DTOs are neutral. They may contain operation id,
timeout, `CommunicationSessionId`, expected event-stream contract version,
configured EventBuffer symbolic root, read-state evidence, and monitoring-state
evidence. They must not contain PLCSIM Runtime API objects, PLC handles,
callback handles, event handles, raw PLC memory pointers, or mutable shared-core
processor state. Delete-sequence requests and release results remain internal to
the binding worker/facade protocol and are not operations of
`IPlcsimUnitTestCommunicationSession`.

### EventBuffer Reset Request

The explicit EventBuffer reset request writes
`DB_UnitTestRunControl.ControlData.BufferCmd.Reset := TRUE` once through the
PLCSIM Runtime API. Its result reports only whether that API write was accepted.
It does not wait for the PLC to reset the command bit, clear `LostEventCount`,
empty the buffer, or reach another producer state. Those PLC-owned changes are
visible only through later normal EventBuffer observations.

The binding does not retry the reset write automatically and does not synthesize
a clean EventBuffer state from an accepted write. Cancellation bounds local
dispatch; once delivery may have occurred, the result preserves accepted or
ambiguous write evidence without issuing a compensating command.

### EventBuffer Contract Validation

The binding must read and validate `DB_UnitTestEventBuffer.ContractInfo` before
interpreting event-buffer data, as required by the UnitTest event-stream model
and the PLCSIM Advanced direct-access communication contract. Missing,
unreadable, unsupported, or incompatible EventBuffer contract metadata prevents
event-buffer interpretation and event-stream monitoring.

EventBuffer validation is separate from RunControl validation:

- RunControl validation protects command fields and the
  `DeleteSequenceId` release field;
- EventBuffer validation protects FIFO/status/buffer interpretation;
- both must be valid before monitoring can be considered active for one
  `CommunicationSession`.

Validation must produce neutral evidence containing the observed event-stream
contract version and framework version where available. It must not write
EventBuffer fields or repair producer-owned FIFO/status/buffer data.

### Event Intake

The binding consumes event entries through the EventBuffer symbolic areas
defined by the PLCSIM Advanced direct-access communication contract. It must
treat `ContractInfo`, `FifoControl`, `Status`, and `Buffer` as producer-owned
read-only data and must not write or repair them.

Readable entries are derived according to the UnitTest event-stream model. The
binding must not replace FIFO range derivation, sequence id semantics, lost
event semantics, or buffer-length handling with locally invented rules.

The binding publishes accepted entries as neutral `UnitTestEventBatch` data for
the application-facing event-stream service. The batch must preserve FIFO order,
sequence id identity, PLC timestamp, owner scope, event type, severity, name,
resource name, lifecycle state, result, message, event-buffer epoch evidence
where available, read-state classification, lost-event count, and buffer-empty
evidence. Event timestamps must not replace `SequenceId` for ordering, release,
or plausibility checks.

Direct-access reads across `FifoControl`, `Status`, and `Buffer` are not
guaranteed to be one atomic snapshot. A binding implementation may use a
cycle-synchronized strategy or another proven strategy to reduce inconsistency.
If the implementation cannot prove a coherent snapshot, it must classify the
read as retry/defer/inconsistent instead of publishing false event evidence.

### Monitoring State Representation

Monitoring results and notifications carry `batchReleaseState`, not
`releaseState`, over worker IPC. The facade maps this to `BatchReleaseState`
using `EventStreamBatchReleaseState`. Facade and worker must be deployed
together; no old-key fallback is provided. Analysis exports likewise use
`batchReleaseState`.

`lastReadState=NotObserved` explicitly represents initialization without a
read outcome. A missing monitoring read-state field also maps to NotObserved,
not Empty. Initial zero counters and false buffer flags are not reset evidence.
Contract validation alone does not establish an empty event buffer.

The monitoring factories are `CreateMonitoringResult`,
`CreateBufferObservationResult` and `CreateFailureResult`. Their result names
do not imply a PLC deletion acknowledgement. Internal deletion-operation
results remain separate from PC batch release state.

`NotRequired` is retained only as an unused numeric enum value. Initial,
empty and no-batch states use `Idle`; their read state distinguishes
NotObserved, Empty and other actual read outcomes. This name change does not
change PLC contract fields, trigger scheduling or write/publication ordering.

Successfully read FIFO/status metadata is latched together independently of
payload classification. Pre-read metadata remains available if payload or
post-read access fails; a successful post-read replaces it. Monitoring results
and published batch metadata use that latest observation. Captured entries and
their release watermark remain those of the validated batch.

Without a new read, retain the last read and PC batch-release state and the
observed counters/flags. Do not copy a previous payload into another result.
This applies to pending deletion, monitoring stop and reconnect within the same
logical communication session, including replacement of the API interface.
New sessions/targets initialize with NotObserved/Idle, zero counters and false
buffer flags. An actual PLC reset updates these fields through real reads.

A valid nonempty captured batch eligible for publication uses Valid/Completed.
Captured data withheld for rereading uses Valid/Idle; empty observations use
Empty/Idle and other ineligible snapshots use their actual read state with Idle.
Internal deletion Pending/Failed outcomes do not become PC batch states.
Read/transport failures retain known metadata and prior batch-release evidence,
while preserving the operation failure and read/session invalidation signal.
Synchronous monitoring errors carry these state fields alongside the error code
and message. Only errors without a structured observation use the facade's
same-session retained state; they must not replace known observations with zeros.

### Release And Backpressure

The UnitTest event-stream model has exactly one logical consumer. For this
profile, the PLCSIM binding is that consumer toward the PLC producer. The
binding requests release of processed entries only through the delete-request
field defined by the PLCSIM Advanced direct-access communication contract.

Release follows the UnitTest event-stream model: release must be based on
contiguously processed event entries from the readable FIFO range, and the
binding must not acknowledge unprocessed or non-contiguous sequence ids.

The worker exclusively owns the event-stream release write. No profile,
application, or public communication-session operation may request release of a
processed batch. Release is part of the worker-internal eventstream commit phase:
after a readable FIFO batch has been copied into worker-owned immutable DTO data,
the worker writes the delete/release request through the PLCSIM Runtime API. The
batch is published to the facade only
when that API write returns success. The release write does not wait for a
separate PLC acknowledgement loop. If the API write fails, the worker does not
publish the batch, logs a warning, keeps the session alive, and lets the next
poll-cycle re-read the still-unreleased PLC data.

Backpressure is normal. If the PLC producer cannot release entries quickly
enough, or if `DeleteSequenceId` remains non-zero, the binding must not issue
another delete request. A captured but withheld batch is discarded locally and
reread from the PLC on a later attempt, not retained for delayed publication.
Any application/profile-owned
monitoring threshold is evaluated above this binding-local retry behavior; the
binding must not invent a threshold or polling limit.

The named pipe is the facade/worker event-delivery backpressure boundary. The
facade does not add another event-dispatch queue. If a slow event consumer blocks
pipe progress until a command or close request times out, that ambiguity is
terminal for the worker and communication session; recovery requires a new
session. Worker-local callback queues that cannot safely block a Siemens API
callback must be bounded, and overflow must terminate the affected worker session
rather than silently discard state events.

If the FIFO is almost full or full, the binding should surface pressure evidence
without inventing missing events. If `LostEventCount > 0`, the delivered event
stream is incomplete even if later entries remain readable and valid.

### Event Stream Completion And Invalidation

Event streaming for one RunControl start is considered complete only according
to the event-stream model: the final RunControl completion anchor has been
received and the consumer has observed an empty event buffer, or the client-side
event-stream observation timeout expires. RunControl status projection alone is
not sufficient.

A transition from a non-empty accepted snapshot to an empty snapshot must be
published once as an empty neutral batch. That batch carries the producer and
deleted sequence evidence, `BufferObservedEmpty = true`, and
`ReadState = Empty`; it does not contain entries and does not issue another
release write. Repeated empty polls may be coalesced until a later non-empty
snapshot is accepted.

The binding invalidates or rejects monitoring and its worker-internal release
work when:

- the communication session id does not match the active session;
- communication was released or disposed;
- Runtime or UnitTest communication health was invalidated;
- RunControl or EventBuffer contract validation is no longer trustworthy;
- an event-buffer epoch reset invalidates pending release assumptions;
- FIFO control indicates index error, inconsistent state, or producer repair;
- a read inconsistency prevents acceptance of a coherent FIFO prefix.

Lost events or sequence gaps alone mark evidence incomplete; they do not
automatically invalidate healthy transport or stop draining. Preserve valid
delivered entries and loss evidence. Apply retry/defer for inconsistent snapshots
and invalidate only when the access/session or contract is no longer trustworthy.

Invalidation must be surfaced through neutral read state and release state
values such as `RetryRequired`, `Inconsistent`, `ContractInvalid`,
`SessionInvalid`, `Rejected`, `Failed`, `Unclear`, or `Invalidated` according to
the evidence. It must not be hidden by silently dropping event batches or
pretending the buffer is empty.

Stopping monitoring stops observation and event publication for the current
communication session. The stop operation is local to the communication session,
is idempotent, and remains valid when the PLC runtime is already stopped or the
observation loop has already ended. The worker retains ownership of the
observation thread until termination is confirmed; a replacement monitoring
cycle cannot start while the prior loop is still alive. A running-runtime precondition applies to
starting or using PLC communication, not to stopping that communication. Stopping
monitoring does not release processed entries by itself, does not reset
RunControl, does not stop the PLC runtime, and does not clean runtime storage.

## Progress And Evidence

The Runtime API provides callback/event surfaces for operating-state changes,
runtime manager loss, configuration changes, sync points, and related runtime
activity. The PLCSIM binding converts these API-specific observations into
neutral progress, health, diagnostics, and evidence models. The
[Siemens profile architecture](../../../docs/architecture/access-profile.md)
owns profile composition; the shared diagnostics and access-health contracts own
the general reporting model. This local contract defines only the PLCSIM binding-specific
classification inputs and mapping expectations.

Progress shape:

```csharp
public sealed record PlcsimRuntimeProgressSnapshot(
    string OperationId,
    string Operation,
    PlcsimOperationStatus? Status,
    string Message,
    IReadOnlyList<PlcsimEvidence> Evidence);
```

Progress snapshots are informational. The authoritative operation outcome is the
operation result returned by the invoked session method.

## Communication Read Revalidation And Failure Semantics

PLCSIM communication failures are classified by operation, current runtime and
session lifecycle, and preserved API evidence. A Siemens error code is mapping
input, not a complete health decision by itself. The version-specific binding
must preserve enough structured error evidence for this classification instead
of relying on diagnostic message text.

### Transient stale communication context

`NotUpToDate` from a RunControl or EventBuffer read means that the current tag
list or PLC data context cannot be trusted for that poll cycle. It is a
revalidation condition, not an immediate terminal worker-session failure.

The observation loop must apply these rules:

- one poll cycle contains the RunControl and EventBuffer reads needed for one
  coherent observation attempt;
- `NotUpToDate` from any of those reads discards the complete poll cycle;
- no RunControl projection, event batch, empty-buffer observation, or partial
  evidence from the discarded cycle is published;
- the first discarded active-session cycle publishes communication health
  `RevalidationRequired` once; repeated stale cycles do not repeat the health
  transition;
- tag-list status/update and subsequent reads may be retried within the active
  communication session while the runtime remains usable and the operation is
  not cancelled;
- successful complete reads after revalidation restore usable communication
  and publish communication health `Healthy` before recovered RunControl or
  EventBuffer observations are forwarded, without silently selecting another
  runtime target or restarting the profile;
- when the configured revalidation timeout is positive, exhausting it ends the
  revalidation attempt; cancellation, runtime leaving the usable operating
  state, or a classified terminal failure always ends the attempt and returns
  the corresponding explicit outcome;
- when the configured revalidation timeout is zero, stale reads alone do not
  make revalidation terminal.

The transient `RevalidationRequired` state is workflow and UI state, not an
application diagnostic. It temporarily blocks dependent UnitTest actions while
keeping the established runtime, PLC state, communication session, and
observation loop intact. Successful recovery restores the existing session
without a routine warning/error diagnostic. A terminal failure or exhausted
positive revalidation timeout remains diagnostic-worthy and invalidates or
faults the affected communication boundary.

Opening and cancelling a TIA Portal download dialog may temporarily produce this
stale context even when no download is completed. Therefore the first
`NotUpToDate` result must not permanently invalidate communication. A completed
software or hardware download can produce the same read evidence. Communication
is trusted again only after tag-list revalidation and complete, valid reads of
both UnitTest contracts; the fact that a download-related callback was observed
is not sufficient by itself.

Runtime Manager configuration, software-configuration, hardware-configuration,
and IP-address callbacks are diagnostic correlation evidence only. They must
not directly change runtime or communication health, invalidate a session,
authorize recovery, refresh the tag list, or publish a runtime-state
transition. Runtime Manager configuration events can occur during normal setup
and do not identify an affected selected instance reliably. Software events can
arrive after stale reads and do not cover a cancelled download dialog. Hardware
and IP-address events can arrive only after the communication-relevant read or
operating-state evidence.

The authoritative reactions remain:

- operating-state events determine runtime availability;
- complete successful RunControl and EventBuffer reads authorize communication
  recovery after configured revalidation;
- structured API, Runtime Manager loss, worker, and transport failures determine
  terminal invalidation.

### Communication preparation

Communication preparation must validate a usable tag list and both configured
UnitTest contracts before it returns a communication session. A temporarily
stale tag list is handled through a bounded, cancellable revalidation sequence
rather than by failing on the first contract read.

Preparation must stop and return an explicit non-success result when:

- cancellation or the preparation timeout is reached;
- the selected PLC runtime leaves the operating state required for symbolic
  communication;
- either UnitTest contract is readable but missing, malformed, incompatible, or
  otherwise invalid;
- the Runtime API, selected runtime instance, worker transport, or required
  communication interface becomes unavailable or terminally faulted.

No partially prepared communication session is exposed. Contract/configuration
failures remain distinct from transient stale-context failures and require a
compatible PLC program or configuration before another preparation can succeed.

### Writes and ambiguous delivery

RunControl command writes are never retried automatically. Once a write was
attempted, timeout, cancellation, or an unclear API result can make delivery
ambiguous; the caller must receive that outcome and decide the next action.

An EventBuffer release write is part of worker-owned batch intake. A batch is
published only after its corresponding release write succeeds. A failed release
write does not publish the batch. The next poll may read the producer-owned
buffer state again and derive a new release attempt from current evidence, but
must not blindly replay an old write request.

### Expected shutdown races

After PLC shutdown, external Power Off, communication close, monitoring stop, or
session release has begun, an in-flight read may complete with
`InvalidOperatingState` because the runtime became unusable before the read
returned. When the binding has already observed `ShuttingDown`, `Stop`, `Off`,
or an intended close/release boundary, that completion is expected teardown
evidence. It must not publish a new communication fault or invalidate an
otherwise orderly cleanup.

The same error outside an established teardown or non-running-runtime boundary
remains an operation failure and may require revalidation or invalidation.
Cleanup and close operations are idempotent and must tolerate worker-side
observation termination or a communication session that has already finalized.

### Terminal communication failures

A communication failure is terminal when the binding can no longer trust or
revalidate the selected runtime/session boundary. Terminal evidence includes
worker or named-pipe loss, Runtime Manager loss, removed or replaced selected
runtime interfaces, unavailable/uninitialized Runtime API state, malformed IPC
responses, internal/resource failures, or an exhausted positive revalidation
timeout.

A terminal failure stops observation, publishes no later stale callbacks as
valid observations, moves affected lifecycle and event-stream health to a
non-usable state, and requires explicit communication preparation or profile
session recovery. It must not trigger hidden runtime selection, PLC restart,
profile registration refresh, or monitoring restart.

PLCSIM configuration and IP/software/hardware change callbacks are diagnostic
and correlation evidence. They do not independently authorize state changes,
revalidation success, or terminal invalidation.

## PLCSIM Failure Classification Matrix

The binding must classify PLCSIM-specific failures consistently across runtime
control, RunControl communication, and EventBuffer communication. Diagnostics
explain the problem, operation results report the current operation outcome, and
health reports whether the affected service boundary remains trustworthy.
Diagnostics must not become authoritative for workflow gates or UnitTest result
semantics.

| Condition | Operation status | Health effect | Recovery expectation |
| --- | --- | --- | --- |
| PLCSIM Advanced API DLL, runtime manager, license, or required local runtime environment is missing or not loadable. | `Unavailable` | `Unavailable` for PLC control or affected communication service. | User installs/repairs environment or refreshes profile registration; no automatic fallback. |
| Selected runtime instance cannot be found, disappeared, or no longer matches selected evidence. | `Unavailable` or `Unclear` | `RevalidationRequired` or `Invalidated` for PLC control. | Re-discover/reprepare runtime or reload profile session. |
| Runtime manager lost/configuration changed incompatibly. | Current operation `Unclear` or `Failed`; future operations `Unavailable` until revalidated. | `Invalidated` for PLC control and dependent communication services. | Stop active observations, release/dispose affected session resources, require revalidation. |
| Runtime start/stop reaches a stable but unexpected operating state. | `Unclear` or `Failed` depending on evidence. | `Degraded`, `RevalidationRequired`, or `Invalidated` according to trust impact. | Observe again when safe; otherwise reprepare runtime. |
| Runtime command or release operation times out. | `TimedOut` | Usually `RevalidationRequired`; `Invalidated` when resulting state is untrustworthy. | Observe current state before retry; do not assume command success. |
| Runtime cleanup fails after a normal release request. | `Failed`, `TimedOut`, or `Unclear` | PLC control may become `Degraded`, `RevalidationRequired`, or `Disposed` with cleanup diagnostics. | Continue best-effort cleanup; preserve diagnostics/log evidence. |
| A RunControl or EventBuffer read returns `NotUpToDate`. | Current poll cycle is discarded; active observation enters configured revalidation without publishing partial data. | Communication remains recoverable while revalidation is active. Exhausting a positive revalidation timeout requires a non-usable health state; zero has no automatic terminal timeout. | Revalidate the tag list and both communication contracts, then resume only after a complete valid read cycle. |
| An in-flight communication read returns `InvalidOperatingState` after shutdown, Power Off, close, stop-monitoring, or release has begun. | Expected teardown completion; no additional operation fault. | No new invalidation beyond the already established shutdown/close state. | Complete idempotent teardown and suppress stale callbacks. |
| A communication read returns `InvalidOperatingState` outside an established teardown or non-running-runtime boundary. | `Failed` or `Unclear` according to preserved API evidence. | `RevalidationRequired` or `Invalidated` for the affected communication services. | Revalidate runtime/session state before further communication. |
| RunControl contract info remains missing, unreadable, malformed, or unsupported after configured stale-context revalidation. | `Unavailable`, `Rejected`, or `Failed` | `Invalidated` or `RevalidationRequired` for UnitTest lifecycle. | Fix PLC project/contract version or reload after compatible download. |
| RunControl command rejected by readiness, pending command, or invalid session. | `Rejected` | Health remains `Healthy` when rejection is expected and explained. | User/workflow waits, resets, or revalidates according to projection. |
| RunControl command write API call times out or fails. | `TimedOut` or `Failed` | `RevalidationRequired` for UnitTest lifecycle when the write outcome is unclear. | Observe RunControl projection before retry; no hidden compensating command. |
| RunControl lifecycle projection after a command is incompatible or ambiguous. | `Unclear` | `RevalidationRequired` or `Invalidated` for UnitTest lifecycle. | Observe/revalidate before another command. |
| EventBuffer contract info remains missing, unreadable, malformed, or unsupported after configured stale-context revalidation. | `Unavailable`, `Rejected`, or `Failed` | `Invalidated` or `RevalidationRequired` for UnitTest event stream. | Fix PLC project/contract version or reload after compatible download. |
| EventBuffer snapshot is non-atomic, inconsistent, or indicates producer-side repair. | `SucceededWithWarnings` with retry/defer evidence, or `Unclear` when trust is lost. | `Degraded` or `RevalidationRequired` for UnitTest event stream. | Retry/defer according to observation strategy; do not publish false evidence. |
| EventBuffer epoch reset invalidates pending release or active run evidence. | `Unclear` or `Failed` for active operation. | `Invalidated` for UnitTest event stream and active run observation. | Stop affected observation and let shared core classify incomplete/invalidated evidence. |
| `LostEventCount > 0` or unrecoverable sequence gap indicates incomplete stream. | Event intake may succeed with incomplete evidence; completion/result evaluation is incomplete. | Event-stream health may remain `Healthy` or `Degraded` depending on access trust; run evidence is incomplete. | Preserve delivered entries and lost-event evidence; shared core determines result completeness. |
| Worker-internal release/backpressure write remains pending beyond the configured threshold. | The current batch is not published; internal release is `TimedOut` or `Unclear`. | `RevalidationRequired` for UnitTest event stream when release trust is unclear. | Do not start another release write; revalidate communication before the next polling attempt. |
| Communication session released, disposed, or id mismatch. | `Rejected` or `Unavailable` | `Disposed` or `Invalidated` for affected communication services. | Create a new communication session through normal preparation. |
| Worker/IPC failure below the binding boundary. | `Failed` or `Unclear` | Affected service becomes `Invalidated`; profile may become `Invalidated` for fatal worker failures. | Contain failure, dispose partial resources, require profile/session reload when fatal. |

## Progress, Diagnostics, And Recovery Rules

Progress is informational and should be emitted for long-running PLCSIM
operations such as API/session open, runtime discovery/preparation, runtime
start/stop, communication preparation, RunControl command writes,
event-stream monitoring, release/backpressure waiting, and cleanup. Progress must
not be used as the authoritative operation result.

Diagnostics are application-facing findings for actionable problems. Expected
unavailable capability state during registration is not a diagnostic by itself.
Selected binding load failure, invalid configuration, incompatible contract
metadata, command timeout, release failure, cleanup failure, communication loss,
and fatal containment are diagnostic-worthy.

Diagnostic ownership follows the functional service boundaries:

- PLC-control diagnostics are owned by runtime manager, runtime instance, PLC
  state/control, runtime preparation, and runtime cleanup boundaries;
- UnitTest lifecycle diagnostics are owned by RunControl contract validation,
  command writes, command-bit/lifecycle observation, and lifecycle projection
  boundaries;
- UnitTest event-stream diagnostics are owned by EventBuffer contract validation,
  snapshot classification, event intake, release/backpressure, and event-stream
  invalidation boundaries.

The binding must not expose raw Siemens API object models, handles, callback
types, worker object identities, or serialized API structures through diagnostic
messages. Detailed technical evidence belongs in logs or bounded evidence
references. Application diagnostics should remain contract-oriented and
actionable.

Recovery is explicit:

- do not auto-refresh profile registration during normal runtime failures;
- do not auto-select another binding after a selected binding failed;
- do not write hidden RunControl reset/abort commands during cancellation or
  disposal;
- do not automatically repair EventBuffer producer-owned state;
- do not hide failed cleanup behind successful disposal;
- after invalidation, require revalidation, reprepare, new communication
  preparation, or profile reload according to the affected boundary.

## Boundary Rules

- The Siemens profile shell must not reference PLCSIM Advanced Runtime API
  assemblies.
- The Siemens profile shell must not hold `ISimulationRuntimeManager`,
  `IInstance`, callback handles, event handles, or worker transport objects.
- The PLCSIM binding must normalize Runtime API error codes to
  `PlcsimOperationStatus`, diagnostics, and evidence before returning to the
  shell.
- Runtime manager lost, configuration changed, selected instance disappeared,
  invalid operating state, communication-interface failure, and API/worker
  connection loss must become health/progress/result evidence at the PLCSIM
  boundary.
- Real PLCSIM validation remains opt-in and must verify the concrete V8 Runtime
  API behavior separately from fake/sample binding tests.

## Facade And Worker Boundary

The PLCSIM Advanced binding facade owns the shell-facing implementation of the
PLCSIM runtime and UnitTest communication binding contracts. The facade is loaded
by the Siemens profile shell as a Siemens-internal binding package. It must not
be a generic access-profile plugin and it must not be discovered directly by the
application.

The facade owns:

- facade lifetime and worker supervision;
- request and session correlation;
- cancellation and timeout translation;
- validation of worker responses;
- normalization of PLCSIM worker results into Siemens-internal DTOs;
- operation-scoped progress, diagnostics, health, and bounded evidence;
- facade-visible cleanup.

The worker is API-adjacent. It owns:

- PLCSIM Advanced Runtime API assembly/native dependency loading;
- runtime manager and runtime instance handles;
- operating-state callbacks/events and runtime-manager loss observation;
- direct/symbolic PLC access;
- UnitTest RunControl command reads/writes and projection reads;
- UnitTest EventBuffer reads, release writes, and intake hot path;
- worker-local buffers, backpressure, and retry/defer state;
- PLCSIM exception/return-code capture and mapping input;
- API-adjacent resource cleanup.

The worker may be a .NET Framework, native-adjacent, or otherwise
Siemens-compatible host when required by the selected PLCSIM Advanced API.
Production facade/worker IPC follows the Siemens worker IPC contract. The
worker process owns the named-pipe server endpoint; the shell-facing binding
facade starts the worker and connects as the named-pipe client. Standard output
and standard error are diagnostic channels only and must not carry the worker
protocol.

For PLCSIM Advanced command traffic, every facade request carries a non-empty
binding-local `requestId`. Every worker response produced for that request must
echo the same value. Runtime-state, RunControl-observation, event-batch, and
other unsolicited worker events are not command responses and do not require a
`requestId`. The facade keeps command execution serialized and rejects a missing
or mismatched response correlation as a terminal worker-session failure. This
correlation validates response ownership; it does not enable parallel commands.

Once a request may have crossed the named-pipe boundary, the facade retains
ownership of the serialized request and drains its correlated terminal response
under a binding-internal hard containment limit. Caller cancellation after a
successful drain returns `Cancelled` without making the worker session
unavailable. Returned state and side-effect evidence remain authoritative so the
runtime-session owner can retain, adopt, compensate, or release resources.

A missing or mismatched terminal response, hard containment timeout, unexpected
pipe-reader termination, or worker exit after `Ready` makes the worker session
terminally unavailable. The facade disposes the pipe and terminates the worker
instead of accepting a late response as input for a later command. Terminal
loss publishes exactly one
`RuntimeStateChanged` notification with `ConnectionLost` and an unknown current
state. That terminal notification remains observable by the first subscriber
even when the worker exits before event subscription is complete. A normal
`Close` does not publish `ConnectionLost`. Close itself is bounded; timeout,
cancellation, or write failure terminates the worker and releases facade-owned
IPC resources deterministically.

The facade/worker boundary is DTO-only. The following must not cross into the
Siemens shell, Common, Core, WPF, CLI, or generic access-profile contracts:

- PLCSIM Runtime API manager or instance interfaces;
- PLC handles, runtime handles, callback handles, event handles, or wait handles;
- worker-local buffer objects or IPC implementation objects;
- PLCSIM API exception objects or native result structures;
- raw PLC memory pointers or mutable PLC data structures.

The event-stream hot path belongs as close as practical to the PLCSIM API access
layer. The worker should read PLC/EventBuffer data, perform snapshot
classification, preserve FIFO order, manage release/backpressure state, and emit
bounded immutable batch/evidence DTOs to the facade. The facade should not ask
the worker for one PLC tag at a time from application-facing workflow code.

## Binding Package Declaration Expectations

PLCSIM binding packages use the Siemens-internal binding package descriptor. A
PLCSIM Advanced package declares:

- `bindingKind = "plcsim-advanced"`;
- binding package version;
- supported PLCSIM Advanced product/API/runtime capability evidence;
- compatible Siemens project-source kinds for every declared runtime capability;
- `unitTestCommunicationContractVersions`, initially including `1.0.0` for
  bindings that implement RunControl direct access;
- `unitTestEventStreamContractVersions`, initially including `1.0.0` for
  bindings that implement EventBuffer intake.

The descriptor contains static package facts and declarations only. It must not
contain runtime instance ids, active session ids, worker process ids, IPC
endpoints, PLC handles, registry scan history, user selections, or API object
state. Binding-owned `ResolveReadiness` converts local PLCSIM Runtime API and
worker loadability evidence into normalized capability availability during
profile registration. Load-time selected-binding evidence may persist package
root, descriptor path, facade assembly/type, fingerprint, selected product/API
evidence, runtime capability, compatible project-source kinds, and supported
UnitTest contract versions in the Siemens profile-owned registry.

## Implementation And Test Owners

Concrete package behavior and qualification are documented by the owning
binding subtree, not as additional family-wide contract requirements:

- [V8 implementation and binding-level validation](../../v8/docs/validation.md).
- [Fake binding and deterministic conformance](../../fake/README.md).

## Verification Expectations

Pure tests should verify:

- PLCSIM contract DTOs do not expose PLCSIM API types, handles, callback objects,
  worker objects, or application UI/Core internals;
- concrete binding packages are discoverable and their facade/worker
  behavior conforms to the contract;
- fake PLCSIM package shape can be discovered, registered, selected, and loaded
  through the Siemens binding package path;
- fake PLCSIM runtime/control/lifecycle/event-stream behavior covers success,
  warning, failure, cancellation, timeout, invalidated, and unclear operation
  outcomes;
- fake communication evidence preserves event-stream ordering, release, and
  invalidation semantics from the source contracts;
- cleanup disposes fake resources and reports cleanup failures without hiding the
  original operation outcome;
- no production path relies on exact diagnostic display text.
