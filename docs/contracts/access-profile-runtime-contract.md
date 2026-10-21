# Access Profile Runtime Contract

This document defines the application-facing runtime contract between
PLCSIM.UnitTest applications/shared core and loaded access profiles.

It does not define package discovery, manifest metadata, or configuration-file
shape. Those are defined by `access-profile-package-contract.md` and
`configuration-file-contract.md`.

## Plugin And Session Boundary

`IAccessProfilePlugin` is the package-level object selected by discovery and
loading. It is initialized with an `AccessProfileContext` for its immutable
package root, writable profile-data directory, logging, and shared
infrastructure. Plugin initialization must not open vendor projects, start
runtimes, prepare PLC communication, or create a run session.

`RegisterAsync` is the explicit registration operation. It may inspect
profile-owned environment and binding availability and may update
profile-owned registration/cache state. It returns application-facing
registration/capability output. It is not a run/session lifetime.

### Package And Mutable-Data Boundary

`AccessProfileContext.ProfileRootPath` identifies the selected package source.
It is read-only input for package resources, private dependencies, binding
packages, and profile documentation. A profile must not create registry files,
caches, temporary files, or other derived state below this path.

`AccessProfileContext.ProfileDataDirectory` is the host-provided writable root
for derived state owned by that profile candidate. The host creates it before
plugin initialization and keeps candidates isolated by profile identity,
profile version, source kind, and normalized package-source identity. The path
is stable for repeated activation of the same deployed candidate; independently
deployed copies of the same profile do not share it.

Profile data may contain reproducible registration evidence and caches. It must
not contain application artifacts, application registry data, user project
files, credentials, or active vendor/session handles. A profile must not infer
the writable path from `ProfileRootPath`. Package-local legacy cache data is not
migrated implicitly; registration regenerates derived state in the supplied
profile-data directory.

`ValidateConfig` is synchronous and lightweight. It validates the provided
configuration shape and profile-specific payload before discovery or execution
setup. This validation is separate from setup-only session loading and must not
perform heavy vendor operations.

`LoadAsync` creates a loaded `IAccessProfileSession`. Success means the returned
session is non-null and owns the functional services needed by the workflow.
The session exposes four required functional services:

- `IPlcProjectService`
- `IPlcControlService`
- `IUnitTestLifecycleService`
- `IUnitTestEventStreamService`

The loaded session may use any internal composition needed by the concrete
technology. Siemens DLLs, API handles, sessions, registry details, symbolic
paths, and vendor-specific exception types must not cross this contract as
control-flow objects.

### Session Setup And Run Configuration

`LoadAsync` receives an `AccessProfileSessionRequest`. This immutable setup
contains profile identity/version, project source, and project ownership only.
It does not contain `AccessProfileConfigurationPayload` and must not establish
the operational configuration for later prepare operations.

The application owns the current `AccessProfileConfig` snapshot. It passes that
snapshot explicitly to project and runtime discovery and, after final
`RunSetup` validation, to project preparation, runtime preparation, and
UnitTest communication preparation. A loaded profile session must not use the
configuration seen by an earlier discovery operation as the configuration of a
later prepare operation.

Runtime discovery uses `PlcRuntimeDiscoveryRequest`. Runtime preparation uses
the separate `PlcRuntimePrepareRequest`, which also carries the
application-selected `RuntimeProvisioningMode` and optional runtime target:

- `UseExisting` requires a selected existing runtime target.
  Before final setup validation, the host resolves the explicit configured target
  identifier or, only when that identifier is absent, the profile-designated
  `RuntimeTargetDiscoveryResult.DefaultTarget`. This optional value is a neutral
  target reference, not a permission to provision or delete a runtime. Profiles
  without an environment-defined default leave it null. Hosts must not infer a
  default from discovery order/count or fall back after an explicit selection fails.
- `CreateOwned` creates the effective target and therefore does not accept a
  preselected runtime target.

Successful runtime preparation returns `PreparedPlcRuntime`, containing both
the prepared runtime context and its effective runtime target. A prepared
project, runtime, or communication resource may retain only the accepted values
needed for its own operation and release lifecycle. This does not make the
complete run configuration session state.

`IAccessProfileSession.ReconnectRuntimeAsync` revalidates technical runtime
access for an already prepared target and releases profile-owned UnitTest
communication that depends on the previous technical access. Its
`PlcRuntimeReconnectRequest` carries the effective configuration, provisioning
mode, current runtime context, and effective runtime target. Reconnect preserves
the selected target, technical worker/session boundary, resource identity, and
ownership. It must not perform discovery, project handling, build, transfer,
download, provisioning, target release, target deletion, PLC start/stop, or
automatic worker replacement.

Caller cancellation may be accepted only before profile-owned communication
teardown begins. After that boundary, the profile owns a bounded revalidation
or containment attempt and returns success or failure rather than caller
cancellation. A successful result returns the revalidated
`PreparedPlcRuntime`. A failed reconnect preserves target ownership and leaves
a truthful release or cleanup path. Loss of the worker or its transport is a
terminal reconnect failure for the current loaded session; automatic worker
replacement and resource rehydration require a separate explicit contract.

Shared Core owns the complete UnitTest communication configuration and operation
deadlines. After profile reconnect returns, Shared Core discards its previous
communication context and, when the observed PLC state permits communication,
prepares UnitTest communication again through
`PrepareUnitTestCommunicationAsync`. The profile must not cache an earlier
communication request as an implicit reconnect input. Profiles that do not
implement reconnect return a structured failed/not-implemented result through
the default contract method.

### Runtime Resource Release

Runtime provisioning and runtime release are separate application-owned
intents. `RuntimeProvisioningMode` determines how the prepared resource is
obtained. For every final release action, Shared Core derives an explicit
`RuntimeResourceReleaseMode` and sends it in `PlcRuntimeReleaseRequest`:

- `UseExisting` derives `KeepResource`.
- `CreateOwned` derives `DestroyResource`.

Reconnect is a technical revalidation operation and sends no resource-release
request. It preserves the original provisioning context, worker/session
boundary, runtime resource identity, and final release responsibility.

The profile executes the authorized request without repeating the application
ownership decision. `KeepResource` closes technical access while retaining the
runtime resource. `DestroyResource` requests provider-specific shutdown,
deregistration, storage cleanup, and technical close as applicable.

`OperationResult<PlcRuntimeReleaseOutcome>` reports operation state and known
resource postcondition independently:

- `ResourceKept` and `ResourceDestroyed` are terminal known postconditions.
- `ResourcePartiallyDestroyed` is terminal but unsuccessful and the previous
  runtime context is no longer reusable.
- `Unspecified` means no reliable resource postcondition is known.
- Unsupported destruction is `NotSupported` with `ResourceKept` when the
  resource is known to remain intact.

Shared Core retains release responsibility after an unsuccessful result without
a terminal resource outcome so the operation can be retried. Profile fallback
cleanup closes technical access only. It must not infer destruction from
profile configuration, technical session ownership, or a previous
`CreateOwned` prepare request.

Discovery should leave as little state as the technology permits. A technical
manager/worker session may be retained, and managed project discovery may
intentionally retain project access, but retained resources must apply the
current prepare request rather than stale discovery configuration.

### Configuration Projection

A loaded `IAccessProfileSession` must expose its
`IAccessProfileConfigurationService` through `Configuration`. Profiles without
user-configurable values expose an empty field projection; the service itself
is not optional.

The service is a synchronous, lightweight, stateful editing boundary owned by
the loaded profile session. It holds the current application-neutral
`AccessProfileConfig` snapshot, while the profile remains the only component
that interprets the opaque `accessProfile.values` payload. It must not open
vendor resources, perform discovery, or prepare runtime state.

The boundary supports the complete editing lifecycle:

- `LoadConfig` replaces the profile-owned values from raw JSON.
- `SerializeConfig` returns the current profile-owned values as raw JSON.
- `GetFields` and `GetField` return fields applicable to the supplied
  `RuntimeProvisioningMode`.
- Typed getters, add/set operations, removal, and presence checks edit
  published fields without exposing profile-specific JSON paths.
- `SetProjectTarget` and `SetRuntimeTarget` are neutral convenience operations
  backed by profile-owned values.
- `ValidateConfig` performs authoritative profile-owned validation of the
  complete current snapshot for the supplied runtime provisioning intent.

Projected fields contain only semantic data: a stable profile-scoped,
case-sensitive id, display name, primitive value type, nullable current value,
nullable default value, required flag, and optional allowed values with display
names. They do not specify controls, layout, groups, validation expressions, or
vendor types.

Projected values use invariant canonical text:

- `String` is unchanged; `null` means not set and an empty string remains a
  value.
- `Integer` uses invariant `Int64` text.
- `Float` uses invariant round-trip `System.Single` text.
- `Boolean` uses lowercase `true` or `false`.

Editing operations must preserve unrelated, unknown, inactive, and currently
non-projected JSON content. Expected malformed payloads, invalid values,
unknown field ids, duplicate add or missing set/remove operations, and
validation failures are returned as diagnostics rather than thrown exceptions.
Failed operations leave the current configuration unchanged. Successful
operations update the service snapshot and return that new snapshot.

The application may perform immediate generic checks for required values,
primitive conversion, and allowed values. Those checks do not replace
profile-owned `ValidateConfig`, which remains authoritative for dependencies and
context-sensitive rules. The application copies the service's immutable
configuration snapshot into discovery and final `RunSetup` requests; it never
passes the mutable service instance into shared workflow. Projection and
validation diagnostics used while editing are local presentation feedback and
are not automatically republished to the global diagnostic sink.

### Project Ownership

Each loaded run session has an application-supplied `ProjectOwnershipMode`:

- `ApplicationManaged` means the application owns project discovery,
  preparation, build, and transfer for the current workflow.
- `ExternallyManaged` means the user or an external tool owns project build
  and transfer. The application owns only runtime, PLC control, UnitTest
  communication, and run observation.

Project ownership is session intent, not configuration-file or profile-specific
payload. CLI automation uses `ApplicationManaged`. WPF selects the mode for
the active session.

All access profiles must support `ExternallyManaged`; registration does not
publish a separate capability for it. A project source is optional in that
mode. When present, it may be used for cheap profile/source evaluation and
binding selection, but the profile must not open the engineering project,
discover project-owned targets, build, or transfer it.

A loaded technology binding is not the same as an active engineering-project
session. Profiles may keep a deterministically selected binding package loaded
for the access-profile session while opening vendor project/API sessions only
for bounded managed operations. Logical project discovery and transfer evidence
must not depend on a live vendor session or handle.

Shared workflow distinguishes logical project setup from live project access:

- `ManagedProjectPrepared` and `ManagedTransferCompleted` describe managed
  workflow evidence.
- `ExternalManagementAccepted` records the caller's explicit decision that
  project build and transfer are externally owned. Runtime preparation remains
  separate required evidence.
- `ProjectAccessActive` reports whether project-side vendor access is currently
  held; it is not a prerequisite for PLC control or UnitTest communication once
  managed transfer has completed.

`ProjectExecutionContext` carries ownership plus optional neutral project and
project-target references into communication services. Managed execution may
retain these neutral references after vendor access is released. External
execution leaves both references absent; profiles and applications must not
fabricate project contexts or targets to satisfy a common workflow path.

After a successful application-managed transfer, the workflow records
`ManagedTransferCompleted` before releasing project access through the existing
project-service release operation. Release closes only project-owned vendor
resources; selected runtime, PLC, communication, profile session, and loaded
binding packages remain owned independently. A release failure is reported as a
cleanup diagnostic and does not invalidate the successful transfer evidence.
A later managed project operation reopens project access through the normal
project preparation operation and must resolve the configured source and target
deterministically; no separate reopen contract is required.

### Project Authorization

`IPlcProjectService` owns optional Safety-program authorization for managed
project handling. The workflow first asks for the authorization requirement of
the prepared project and selected target:

- `NotRequired` continues without obtaining a credential.
- `PasswordRequired` allows an interactive application or configured automation
  client to provide a transient credential and invoke authorization.
- `OperationResultState.NotSupported` terminates managed project setup before
  build or transfer.

Requirement discovery is authoritative for the current project target. It is
not a registry capability and must not be inferred from the PLC family alone.
An access profile may support this workflow even when another profile or one of
its technology bindings does not.

Passwords cross the application/profile boundary as `SecureString` and live
only for the bounded authorization operation. Implementations must not place
them in command-line arguments, output, logs, diagnostics, evidence, exception
text, IPC traces, or configuration summaries. Persisted CLI configuration is
the one explicit plaintext input boundary. Interactive applications must not
persist or export the entered credential.

Authorization concerns project build and transfer only. It does not authorize
PLC runtime communication, change PLC access protection, or alter the UnitTest
communication contract.

### UnitTest RunControl Evidence

`IUnitTestLifecycleService` exposes the current RunControl observation as four
independent PLC-owned evidence axes:

- `UnitTestExecutionStatus` is the stable coarse execution phase used by
  profile-independent lifecycle handling.
- `UnitTestExecutionSignals` preserves the independent `Uninitialized`,
  `ConfigError`, `Ready`, `Running`, `Completed`, `Aborted`, `Inactive`, and
  `Error` signals.
- `UnitTestLifecycleState` is the detailed PLC RunControl state-machine state
  and remains supplemental evidence.
- `UnitTestResultState` is the PLC-side RunControl result projection.

Profiles must preserve these values independently. They must not reconstruct
the detailed lifecycle state from execution status or signals, collapse signal
combinations into a synthesized result, or silently discard known signals.
Convenience properties may derive individual booleans from the flags value but
must not become separate stored state.

Start readiness requires `UnitTestExecutionStatus.Idle` together with the
`Ready` signal. Neither detailed state nor `Ready` alone establishes
startability. Other generic lifecycle decisions may consume the minimum
required execution status and signals; the remaining evidence stays available
for status projection, plausibility checks, logging, and diagnostics.

`TestRunCommandResult` contains command identity and immediate transport/API
acceptance only. It contains no lifecycle observation. A successful command
result means the external write operation was accepted without an API-level
failure; later PLC reaction is observed and monitored independently through
RunControl observation events.

When communication stops or becomes invalid, the exposed safe observation is
`UnitTestExecutionStatus.Unknown`, no execution signals,
`UnitTestLifecycleState.Unknown`, and `UnitTestResultState.NotEvaluated`.
Communication failures are represented by access health and diagnostics; they
must not synthesize PLC-side error signals.

RunControl status and result fields are lifecycle and plausibility evidence.
Event-stream evidence remains authoritative for reconstructed run results,
testcase results, aggregation, completeness, and generated artifacts.

### Operation Cancellation And Recovery

Application-facing asynchronous operations receive a caller token that may
represent explicit application cancellation or a Shared Core overall deadline.
The access profile consumes that token but does not create a competing
application deadline.

Operation results follow these rules:

- `Cancelled` means the caller requested cancellation.
- `TimedOut` means the application-owned overall deadline expired.
- An unrequested internal cancellation is a failure, not a caller cancellation.
- Binding transport, protocol, worker, or vendor containment failures remain
  technical failures unless they are enforcing the propagated overall budget.
- Cancellation is not rollback. A mutating operation may have completed after
  the caller stopped waiting.

After cancellation, the profile must report truthful service state and health.
If an in-flight operation reaches a known terminal boundary and retained
resources remain valid, the operation may be retried in the same loaded session.
If the final outcome, correlation, transport, or resource state cannot be
trusted, the affected health boundary must become
`RevalidationRequired` or `Invalidated`; the profile must not advertise a
retryable state merely because the caller requested cancellation.

Resources created or retained by a cancelled operation remain owned by the same
profile/session owner. A later retry may adopt a known compatible completion.
Back navigation, End Session, unload, and application shutdown use the normal
owner-driven release path. Release and cleanup operations are bounded but are
not cancelled by normal WPF interaction. Cleanup is idempotent and continues
across individual cleanup failures while preserving all diagnostics.

`IAccessProfileSession.DisposeAsync` is the normal loaded-session cleanup path.
`IAccessProfilePlugin.DisposeAsync` is plugin-level cleanup and must not be used
as a substitute for disposing an active session. Plugins own vendor-specific
concurrency rules; forbidden concurrent operations return failure/error results
rather than relying on the application to infer vendor API semantics.

#### Operation Policy Matrix

The following matrix is normative for every access profile. "After dispatch"
means that the profile or one of its bindings has handed the request to a
stateful worker, vendor API, runtime, or other external owner. Profiles may use
different internal mechanisms, but they must preserve the listed
application-facing semantics.

Every asynchronous operation must observe the supplied token before dispatch
and at safe internal boundaries. A token supplied to release, cleanup, or
dispose is an owner deadline/containment token; normal WPF interaction does not
cancel those operations.

| Contract operation | Possible owned effect | Cancellation requirement | Requirement after dispatch | Recovery and health requirement |
| --- | --- | --- | --- | --- |
| `RegisterAsync` | Temporary environment/binding probes and profile-owned registry replacement. | Stop before starting further probes when cancellation is observed. | Finish or contain the active probe; discard an incomplete registry generation. | Leave no run/session resources. Preserve the previous valid registry or publish the defined empty/error result; do not publish a partial registry as successful. |
| `LoadAsync` | Loaded profile session and loaded internal packages. | Cancellation before successful session creation returns no session. | If a session was created, ownership must be retained until it is returned as a known result or disposed by the loader/profile owner. | Never leak an unreturned session. A successful result always contains one usable session; otherwise cleanup all created load resources. |
| Project target discovery and resolution | Discovery evidence and, where technology requires it, retained project access. | Caller cancellation is supported. | Reach a known vendor/protocol boundary. Retain and report any intentionally opened project access truthfully. | A reusable retained project context may support retry; uncertain project access requires `RevalidationRequired` or `Invalidated`. |
| Project preparation, build, and transfer | Open project access, prepared target, build output, and transferred PLC software. | Caller cancellation is supported for the containing setup operation. | Drain or otherwise contain the dispatched operation. Cancellation does not undo compilation or transfer. | Preserve known completed stages for compatible retry. Unclear project/transfer state blocks dependent actions until revalidation or owner release. |
| `ReleaseProjectAsync` and project `CleanupAsync` | Release of profile-owned project/vendor resources. | No normal user cancellation; observe the owner deadline token. | Continue bounded, idempotent cleanup and collect all failures. | Publish released/disposed health only for resources actually released. Preserve diagnostics and mark remaining ownership truthfully after incomplete cleanup. |
| Runtime target discovery and resolution | Runtime candidate evidence and temporary manager access. | Caller cancellation is supported. | Finish or contain the active probe without fabricating a selected/prepared runtime. | Discard incomplete discovery output. Retained technical manager state may be reused only while its health remains trustworthy. |
| `PrepareRuntimeAsync` | Attached runtime or a newly created owned runtime target. | Caller cancellation is supported for the containing setup operation. | Retain ownership of a runtime created or attached after cancellation; cancellation is not release. | A known compatible completion may be adopted by retry. Unknown runtime state requires revalidation; final owner release must send the release mode derived from the retained provisioning context. |
| `IAccessProfileSession.ReconnectRuntimeAsync` | In-place revalidation of technical runtime access while retaining the worker/session boundary, prepared runtime target, identity, and ownership; profile-owned UnitTest communication using the old access is released. | Caller cancellation is accepted only before communication teardown begins. | After teardown starts, establish a bounded in-place revalidation or contained failure; do not report caller cancellation, replace the worker, release/delete the target, or provision a replacement target. | Return the revalidated runtime context on success. Shared Core then recreates UnitTest communication from its retained configuration when the observed PLC state permits it. Worker/transport loss is terminal for the current loaded session; target ownership and final cleanup responsibility remain truthful. |
| PLC `StartAsync` and `StopAsync` | Accepted runtime control command. | The caller token bounds command dispatch; normal WPF operation cancellation is not required. | Return command/API acceptance only. Do not synthesize the later PLC operating state and do not undo an accepted command. | PLC-state events remain authoritative. Reaction monitoring is caller/shared-workflow responsibility; unclear transport state affects `PlcControl` health. |
| `ReleaseRuntimeAsync` | Execute the explicit `KeepResource` or `DestroyResource` request authorized by Shared Core. | No normal user cancellation; observe the owner deadline token. | Continue bounded, idempotent release and report only resource effects known to have completed. | Return the known `PlcRuntimeReleaseOutcome`. Unsupported destruction remains unsuccessful. An unknown failure retains application release responsibility; partial destruction invalidates the old context. |
| Runtime `CleanupAsync` | Close remaining technical runtime access owned by the profile session. | No normal user cancellation; observe the owner deadline token. | Continue bounded, idempotent technical cleanup without inferring resource-destruction intent. | Never destroy a runtime resource without an explicit `DestroyResource` request. Report remaining technical ownership and health truthfully. |
| `PrepareUnitTestCommunicationAsync` | Lifecycle/event-stream communication session and internal observation resources. | Caller cancellation is supported while setup is active. | Retain or compensate any communication session created after cancellation and reach a known protocol boundary. | Reuse is allowed only for a known compatible healthy session. Otherwise close it or mark lifecycle/event-stream health for revalidation or invalidation. |
| UnitTest `StartAsync`, `AbortAsync`, and `ResetAsync` | Accepted RunControl signal write. | The caller token bounds command dispatch; infrastructure cancellation is not a replacement for the `Abort` domain command. | Return write/API acceptance only. Do not synthesize lifecycle state or undo an accepted signal. | Profile-published lifecycle evidence is authoritative. Reaction monitoring belongs to Shared Core and does not extend command completion. |
| Event-stream `ResetEventBufferAsync` | Accepted destructive EventBuffer reset signal write. | The caller token bounds command dispatch. | Return write/API acceptance only. Do not wait for reset completion, synthesize a clean buffer state, or undo an accepted signal. | Later event-stream observations are authoritative for `LostEventCount`, buffer state, and renewed start eligibility. Shared Core treats active or finalizing run evidence as incomplete using its existing timeout semantics. |
| Event-stream `StartMonitoringAsync` and `StopMonitoringAsync` | Monitoring/observation loop state. | No normal WPF cancellation; observe the supplied operation/deadline token. | Resolve the requested monitoring transition or report an unclear/failed transition; do not leave an unowned loop. | Publish the actual monitoring state. A failed stop keeps ownership until close/cleanup contains the loop. |
| Lifecycle/event-stream `ReleaseCommunicationAsync` and `CleanupAsync` | Communication close, observation stop, and release of communication resources. | No normal user cancellation; observe the owner deadline token. | Continue bounded close/cleanup across individual failures. | Both services must return to safe observations and truthful released, revalidation-required, or invalidated health. |
| `IAccessProfileSession.DisposeAsync` | Final release of all session-owned services, packages, workers, and vendor resources. | No normal user cancellation. | Execute owner cleanup in dependency order and continue after individual cleanup failures. | Disposal is idempotent. No session-owned resource may remain intentionally usable after successful disposal; failures remain logged/diagnosed. |

Profile-internal bindings may define stricter technical containment and
operation-specific compensation. Those details must not weaken this matrix or
turn an ordinary caller cancellation into a fabricated successful rollback.

## Logging Policy

`AccessProfileContext` provides a profile-specific `ILoggerFactory` plus an
application-owned operational logging policy. Profiles use the logging
abstractions and must not depend on the host's concrete logging provider or
create the host-owned profile file sink.

The policy defines the profile minimum level, log base directory, bounded file
policy, and an effective raw-event logging flag. Raw-event logging is false by
default and may be true only when the profile minimum level is `Debug`.
Profiles must enforce the effective value before formatting high-volume event
payloads. Profile-internal bindings and workers inherit this policy unless
their owning composition supplies an explicit override.

The application contract does not expose profile-internal worker or binding
identities as application logging settings.

## Registration And Selection Boundary

Registration and manual project-source selection are not the same lifetime as a
loaded `IAccessProfileSession`.

Registration may inspect profile-owned environment information and
technology binding availability, for example installed product versions, binding
package loadability, and supported project/runtime forms. It must return shared
capability DTOs and must not expose vendor API objects.

Registration must not:

- open an engineering project as a long-lived workflow context,
- create or start a runtime instance,
- download PLC software,
- prepare UnitTest communication,
- start lifecycle or event-stream monitoring,
- leave profile-owned sessions or handles alive after the registration
  operation completes.

The result distinguishes usable, unavailable, and error states. Expected
unavailable capabilities are normal capability state. Real failures are reported
through diagnostics and logs by the application/core registration boundary.

Manual project-source selection calls
`EvaluateProjectSource(string projectSource)`. This call may read
profile-owned registration state and inspect the source path, but it must stay
cheap and side-effect free. It must not trigger registration, open projects,
probe vendor APIs, create runtimes, or start workers. Explicit configuration
loading bypasses this manual filtering gate: the host loads the setup-only
session and uses profile-owned configuration validation for subsequent setup.

## Health Contract

Loaded access-profile sessions report application-facing context trust through
`AccessHealth`.
Health is separate from service progress state and from diagnostics.

Each health update belongs to one `AccessServiceKind`:

| Kind | Meaning |
| --- | --- |
| `Profile` | The loaded profile session as a whole. |
| `Project` | Project preparation and project-side PLC target context. |
| `PlcControl` | Runtime/PLC preparation, control, and runtime trust. |
| `UnitTestLifecycle` | RunControl command and lifecycle-observation trust. |
| `UnitTestEventStream` | Event-stream monitoring, evidence intake, and release trust. |

`AccessHealth` contains:

- `State`
- `Problem`
- `ReasonCode`
- `Message`
- `UpdatedAt`

`AccessHealthState` values:

| State | Meaning |
| --- | --- |
| `NotEstablished` | The context has not yet been prepared or bound. This is neutral by itself. |
| `Healthy` | The profile currently trusts the context. |
| `RevalidationRequired` | The context may still be recoverable, but normal use requires revalidation first. |
| `Invalidated` | A previously established context is no longer trustworthy. |
| `Disposed` | The profile/service context has been released and must not be used. |

`AccessHealthProblem` values provide coarse problem categories:

- `None`
- `ContextLost`
- `ContractInvalid`
- `CommunicationLost`
- `RevalidationRequired`
- `Released`
- `InternalFailure`

Profiles must map internal technology failures to the affected
`AccessServiceKind` values. If one internal condition affects multiple
application-facing boundaries, the profile publishes multiple health updates.

## EventStream Observation State

`EventStreamState.LastReadState` uses `EventBufferReadState`. `NotObserved`
means no read outcome exists yet; it is neither an observed empty buffer nor
a fault. Initialization must select it explicitly rather than use the enum
default (`Valid`). Initial counters are zero, `BufferFull` is false and
`BufferObservedEmpty` is false. Only an actual observation establishes buffer
emptiness or reset evidence.

`BatchReleaseState` uses `EventStreamBatchReleaseState` and describes PC-side
release of captured data for further processing, not a PLC deletion request
or acknowledgement. Use `Idle` when no batch is released and `Completed`
when a valid batch is released for processing. A skipped attempt retains the
previous state without republishing its payload. `NotRequired` remains a
reserved legacy enum value and is not emitted for initial or empty states.

The batch property/type replaces `ReleaseState`/`EventStreamReleaseState`.
Analysis JSON uses `batchReleaseState`. There are no legacy property aliases;
contract assemblies and consuming packages must be updated together. Existing
enum numeric values remain unchanged; `NotObserved` is appended.

## Event Surface

`IAccessProfileSession.HealthChangedEventHandler` is the single subscription
point for loaded session and functional-service health changes.

Each functional service also exposes its current `Health` property. Consumers
use those properties for initial snapshots and direct validation, but shared
workflow should not need separate service-specific health subscriptions.

The loaded session owns consistency of the health update stream. When a profile
internal failure invalidates multiple services, the profile should publish a
coherent batch of affected service updates.

## Interpretation Rules

Access profiles own detection and normalization. Shared workflow owns
interpretation.

Access profiles and services must:

- detect vendor/API-specific context loss, contract mismatch, communication
  loss, disposal, and revalidation requirements,
- normalize those conditions to `AccessHealth`,
- perform local containment such as stopping profile-owned monitoring loops or
  releasing invalid handles,
- emit diagnostics where technical detail helps troubleshooting.

Shared workflow must:

- publish the current health snapshot as part of workflow status,
- evaluate action gates from workflow state plus access health,
- decide whether active monitoring/evidence becomes invalidated,
- create workflow diagnostics only when the health change is relevant to the
  current workflow state,
- keep diagnostics non-authoritative for gates and UnitTest result meaning.

`NotEstablished` is not a failure by itself. `RevalidationRequired`,
`Invalidated`, and `Disposed` block the actions that depend on the affected
service kind. During active run observation, invalidated `Profile`,
`PlcControl`, `UnitTestLifecycle`, or `UnitTestEventStream` health invalidates
active observation/evidence unless a normal completed exportable snapshot
already exists. `Project` invalidation blocks project-dependent paths but does
not retroactively invalidate an active observation when runtime, lifecycle, and
event-stream health remain trustworthy.
