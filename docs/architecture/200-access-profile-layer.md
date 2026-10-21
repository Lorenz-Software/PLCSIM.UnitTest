# Access Profile Layer Architecture

This document describes the plugin and access-profile layer that connects the shared application model to concrete engineering, runtime, and UnitTest communication technologies without exposing vendor API object models to WPF, CLI, or shared core.

## Access And Plugin Architecture

The access and plugin architecture separates application needs from concrete
engineering, runtime, and communication technologies.

The selected architecture is profile-based:

- the shared application layer contains `AccessProfilePluginLoader`,
- concrete `IAccessProfilePlugin` implementations provide profile-specific
  composition,
- access profile plugins may use one or more internal technology bindings,
- WPF, CLI, and shared core consume only application-facing service interfaces.

### Access Profiles

An access profile maps application-facing access needs to one concrete
integration strategy.

The central application configuration selects an access profile by profile
identity. The application does not need to know whether the selected profile is
implemented by one plugin, several internal technology bindings, or another
composition model.

An access profile may compose separate engineering and runtime/communication
bindings. Its own documentation and registration state describe the supported
composition and concrete versions; the application does not select those
internal dependencies.

A different access profile can use a different internal structure. For example,
a Beckhoff-oriented profile could be implemented by a single plugin if one local
runtime/API provides the required access capabilities.

The access profile exists so that WPF, CLI, and shared core can work against
project-owned concepts instead of directly depending on vendor APIs.

### Interchangeability And Contract Stability

Access profiles are interchangeable implementations of the existing
application-facing access-profile API. Adding or replacing a profile should
normally require no changes to WPF, CLI, shared core, exporters or their shared
interface contracts. Different capabilities and vendor behavior are expressed
through the existing contracts, profile-owned configuration and diagnostics,
not through profile-specific branches in the application.

Profile development therefore belongs within the profile and its internal
bindings, tests and documentation. The profile adapts the vendor technology to
the application's API; the application is not adapted to each profile's internal
implementation.

A demonstrated application defect or genuine API limitation can justify an
exception. Such a correction is a separately evaluated application or contract
change, not an implicit part of profile integration. Its impact on existing
profiles and consumers must be considered before changing the shared boundary.
This separation does not prohibit independently commissioned application
features or bugfixes.

### AccessProfilePluginLoader

`AccessProfilePluginLoader` belongs to the shared application/access layer. It
is intentionally profile-agnostic.

The generic directory package and manifest contract is defined in
`docs/contracts/access-profile-package-contract.md`.
The loaded profile and service health runtime contract is defined in
`docs/contracts/access-profile-runtime-contract.md`.

It should be responsible for:

- reading the configured access profile identity,
- scanning configured access profile plugin locations,
- reading generic profile metadata,
- matching the configured profile identity and optional profile version to an
  available profile plugin,
- detecting duplicate or ambiguous profile id/version candidates,
- performing generic loadability checks that do not require profile-specific
  knowledge,
- loading the selected access profile plugin,
- passing immutable session setup to the selected profile plugin,
- receiving an `IAccessProfileSession` instance or profile-level diagnostics.

It must not own:

- Siemens-specific composition rules,
- profile-specific interpretation of detailed configuration,
- environment capability detection for a concrete profile,
- capability selection between partial profile implementations,
- decisions about which internal technology bindings a profile needs,
- engineering project operations,
- simulation/runtime operations,
- UnitTest communication operations,
- UnitTest event interpretation,
- application workflow state,
- concrete vendor API object models.

The loader should not require code changes when a new access profile is added,
as long as the new profile implements the same generic profile plugin contract
and provides the application-facing capabilities needed by the application.

Profile selection is explicit. The loader matches the configured profile
identity to the profile metadata identity. It should not automatically choose the
"best" profile from detected candidates.

### Capability Registration And Registry

Profile discovery, capability registration, and run/session loading are separate
boundaries.

Discovery is a cheap package operation. It finds candidates and validates
generic manifest/entry-point metadata. It must not load vendor-heavy bindings or
run environment capability checks just to populate profile choices.

Capability registration is an explicit application/core operation. It uses the
current discovery result as input, may temporarily load a profile plugin, calls
the profile-owned capability resolver, validates the returned capability DTOs,
and persists the result in an application-owned registry. The temporary
registration load must be released independently from any later run/session
profile load.

The registry is the normal source for application profile selection lists and
environment usability display. Reading the registry must not trigger live
capability discovery. Manual refresh/registration is the operation that updates
the registry.

The registry contains shared capability data only:

- profile identity and source identity,
- registration state and registration timestamp,
- source fingerprint,
- access API capability descriptors,
- project source capabilities,
- runtime capabilities,
- supported UnitTest contract versions.

It must not contain Siemens API objects, loaded assembly handles, active
sessions, runtime instances, project handles, or application workflow state.

Capability registration is not a replacement for functional service validation.
It can say that a profile has a usable environment path in general. Project
opening, runtime preparation, lifecycle communication, and event-stream
validation still happen later through the loaded access profile services.

### Profile Metadata And Configuration

The generic profile metadata contract should stay small.

Required profile metadata:

- stable profile identity,
- display name,
- profile plugin version,
- supported access-profile plugin contract version.

Optional self-information can be provided for diagnostics, UI display, CLI
inspection, logs, and support cases:

- vendor or owner text,
- description,
- supported environment text,
- documentation reference,
- notes.

The metadata is not a capability-negotiation model. From the application's point
of view, a usable access profile must provide all four functional service
boundaries:

- PLC Project Service,
- PLC Control Service,
- UnitTest Lifecycle Service,
- UnitTest Event Stream Service.

If an access profile cannot provide one of these functional access boundaries for
the configured run, profile initialization or validation must fail with
diagnostics. It is not treated as a partially usable profile by the application.

The generic loader receives setup-only `AccessProfileSessionRequest` data:
profile identity/version, project source and project ownership. Operational
profile values are not part of this load request. The application passes the
current opaque configuration explicitly to later discovery/prepare operations
and uses the loaded session's required configuration service for editing and
validation.

### IAccessProfilePlugin

`IAccessProfilePlugin` owns the profile-specific composition logic.

It should be responsible for:

- declaring profile identity and display metadata,
- resolving profile environment capabilities for registration,
- validating profile-specific configuration,
- detecting and validating profile-specific runtime/API prerequisites,
- selecting and loading any internal technology bindings required by the
  profile,
- initializing those bindings,
- creating the loaded `IAccessProfileSession`,
- binding the resulting implementation objects to the application-facing service
  interfaces,
- reporting profile-specific configuration, compatibility, loadability, and
  initialization diagnostics.

For the Siemens access profile, this is where the configuration is translated into
the required TIA Openness and PLCSIM Advanced bindings. For a future profile,
the same contract could be implemented with a single local runtime/API binding
or another internal structure.

### IAccessProfileSession

`IAccessProfileSession` is the loaded and usable access profile session. It owns
the lifetime of one successfully loaded profile instance.

It should be responsible for:

- keeping profile resources alive while the profile is active,
- keeping shared API/session resources alive for internal bindings where needed,
- exposing the loaded functional access objects to the application/shared core,
- coordinating cleanup for partially or fully loaded profile resources,
- reporting cleanup and lifecycle diagnostics that belong to profile loading or
  profile lifetime.

After `IAccessProfileSession` has been successfully created, the
application/shared core should use the provided functional service interfaces
directly. `IAccessProfileSession` should not become a runtime facade that
forwards every functional operation.

## Functional Service Boundaries

The application-facing service model is split into four functional boundaries.
These boundaries describe what the applications need, not how Siemens APIs are
structured.

Functional service interfaces own the live access context for their area. They
perform access operations, observe access-related state where needed, and report
operation diagnostics or context-validity changes. They do not own the overall
application workflow or decide final run/result meaning.

The shared core owns application interpretation of access signals. It translates
access diagnostics and context-validity changes into validation state, workflow
state, lifecycle state, recovery state, result state, output state, and
application-level diagnostics.

### PLC Project Service

`IPlcProjectService` is the intended boundary for project preparation
operations required by a UnitTest run.

The term `project` is intentionally application-level. It covers the configured
engineering project input, archived project input where supported, the opened
project context, and the project/session state needed to prepare a PLC target
for execution. WPF, CLI, and shared core should not need to know whether the
underlying access profile opens an existing project, retrieves an archive,
attaches to an existing engineering session, or creates a temporary project
working context.

The service owns the live project access context after the access profile has
been loaded. Within that context it performs project preparation operations and
reports project-operation diagnostics. It does not own generic plugin loading,
vendor API installation checks, or final application workflow decisions.

It covers application-level responsibilities such as:

- project or archive validation,
- project open, archive retrieval/opening where supported, and project close,
- project compatibility and metadata needed by the applications,
- PLC/CPU target resolution,
- build or compile preparation where required,
- transfer/download to the selected runtime target,
- engineering progress and diagnostics,
- project/session cleanup.

Session responsibility is limited to what the project preparation workflow
requires:

- establish or attach to a project access context using profile-owned
  configuration,
- keep the project/session context valid for the requested project operations,
- expose project-operation state, progress, and diagnostics in project-owned
  terms,
- close or release the project/session context according to application policy,
- report cleanup failures without leaving WPF, CLI, or shared core dependent on
  vendor-specific object lifetime rules.

The service should expose stable project-owned descriptors and operation
outcomes, not concrete engineering API objects. For the Siemens access profile,
that means TIA Openness objects such as Portal sessions, projects, devices,
device items, compiler results, download providers, and callback objects remain
inside the TIA Openness binding.

It must not decide whether a complete test run should continue after a project
operation warning or failure. It reports the project operation outcome and
diagnostics; shared core and the applications decide workflow state, recovery
state, result impact, and user-facing behavior.

Project and archive handling requirements:

- accept application-level project input references from WPF or CLI
  configuration,
- distinguish supported project inputs from unsupported, missing, inaccessible,
  locked, ambiguous, or stale inputs,
- support archived project inputs where the selected access profile can open or
  retrieve them,
- report whether an input can be used as-is, must be retrieved/extracted first,
  or cannot be prepared by the selected access profile,
- open the project only through the loaded access profile and return
  application-level project state instead of vendor API handles,
- close or release opened project context explicitly and report unclear or
  failed close/cleanup outcomes.

Compatibility and project metadata requirements:

- expose enough project identity metadata for diagnostics and reproducibility,
  such as configured project reference, opened project identity, project
  version, and relevant engineering environment relation,
- expose whether the project can be used with the selected access profile and
  first Siemens target environment,
- report missing products, missing support packages, unsupported project
  versions, required user interaction, or incompatible engineering context as
  project-operation diagnostics,
- provide application-level metadata needed by later preparation steps, such as
  available PLC target descriptors or the fact that target resolution has not
  yet been performed,
- avoid exposing Siemens-specific project tree, device, software container, or
  provider objects as metadata.

Open/close behavior must be explicit. A validation-only operation may inspect
input paths, profile support, and lightweight metadata without keeping a project
context open. An operation that opens a project context must make the resulting
context state visible to shared core so later operations can either reuse it or
request cleanup. Repeated runs should not rely on hidden open project state that
is not represented in application-level state.

PLC target and CPU resolution requirements:

- accept a project-owned PLC target reference from configuration or interactive
  WPF selection,
- enumerate candidate PLC targets from the opened project context where
  interactive selection or diagnostics require it,
- resolve exactly one PLC target for a planned run before build/download and
  UnitTest execution can start,
- distinguish not available, ambiguous, wrong-type, unsupported, incompatible,
  and not-downloadable PLC targets,
- expose a stable resolved PLC target descriptor that can be used by shared core
  and by other service boundaries without exposing the engineering project tree,
- include enough target metadata for diagnostics and later model alignment, such
  as display name, project-local identity, CPU family/type where available,
  engineering project relation, and access-profile relation,
- report whether the resolved PLC target has the project-side capabilities
  needed for compile/build and transfer/download,
- keep runtime instance selection and runtime lifecycle outside
  `IPlcProjectService` while still producing the target information needed to
  connect project download intent with `IPlcControlService`.

The shared model uses `ProjectTargetRef` for target identity and
`ProjectTargetDiscoveryResult` / `ProjectTargetResolution` for discovery and
selection. These are project-owned descriptors, not Siemens devices, PLC
software objects or online-provider wrappers. Their concrete code placement is
described by [functional-service implementation](implementation/310-functional-services.md);
the [runtime contract](../contracts/access-profile-runtime-contract.md) owns
the application-facing service boundary.

Target resolution must be deterministic for CLI automation and inspectable for
WPF manual validation. If the configured reference does not resolve uniquely,
the service reports candidate and diagnostic information; shared core and the
application decide whether the user can correct the selection or whether the run
must stop during validation.

Build/compile and transfer/download requirements:

- accept a resolved PLC target descriptor and project context as explicit
  inputs for project preparation operations,
- expose whether the resolved target requires build/compile before transfer,
  supports transfer/download through the selected access profile, and has
  project-side blockers that prevent preparation,
- execute build/compile as an application-level operation with normalized
  progress, warnings, errors, and final outcome,
- execute transfer/download as an application-level operation against the
  selected PLC target and selected runtime transfer destination,
- depend on runtime readiness information supplied by `IPlcControlService`
  where transfer/download requires a prepared simulation/runtime destination,
- keep transfer/download policy at application level, while concrete download
  prompts, callbacks, block selection mechanics, protection/password handling,
  and online/offline provider details stay inside the binding,
- report whether build/compile and transfer/download completed, completed with
  warnings, failed, were rejected, timed out, were cancelled, or ended in an
  unclear state,
- expose operation evidence needed by WPF, CLI, logs, and diagnostics without
  leaking compiler-result or download-result API objects.

Progress and event requirements:

- expose project-operation progress in project-owned operation phases such as
  validation, open, target resolution, build/compile, transfer/download, and
  cleanup,
- provide enough progress information for WPF to show a manual workflow and for
  CLI to emit useful unattended logs,
- make long-running operation state observable without requiring applications to
  poll vendor API objects,
- preserve warning/error summaries and relevant evidence references for later
  diagnostics and execution summaries,
- distinguish operation progress from final application workflow state; shared
  core decides what progress and outcomes mean for the planned run.

Project operation diagnostics and error requirements:

- report project/archive input failures such as not found, inaccessible,
  locked, unsupported, ambiguous, stale, or unusable after retrieval/extraction,
- report project open failures, unsupported interaction requirements, project
  version mismatches, missing products, missing support packages, incompatible
  engineering context, permission failures, and policy/whitelist denials,
- report PLC target resolution failures such as not available, ambiguous,
  wrong-type, unsupported, incompatible, not-downloadable, or missing required
  metadata,
- report build/compile outcomes including blocking diagnostics, warnings,
  errors, cancellation, timeout, and unclear completion,
- report transfer/download outcomes including rejected configuration, unavailable
  runtime destination descriptor, transfer failure, timeout, cancellation, and
  unclear completion,
- report project/session cleanup failures without hiding the fact that a project
  context may no longer be safely reusable,
- include operation phase, severity, stable diagnostic code/category, source
  operation, user-facing summary, technical detail where useful, and evidence
  references that can be shown in WPF, emitted by CLI, or stored in logs,
- normalize vendor-specific compiler, download, permission, and project access
  details without leaking concrete API result objects or exception types as
  application contracts,
- keep diagnostics deterministic enough for CLI automation while still carrying
  enough candidate/context information for WPF troubleshooting.

Plugin discovery, plugin loadability, missing Siemens API installations, and
generic access-profile initialization failures are outside this service. Runtime
instance discovery, readiness, lifecycle, and runtime-state diagnostics belong
to `IPlcControlService`. The PLC Project Service may report that a required
runtime destination descriptor was not supplied or was rejected for a project
transfer operation, but it does not diagnose or repair the runtime lifecycle
itself.

The PLC Project Service does not power on, run, stop, or reset the simulation
runtime. It can require a runtime destination descriptor or readiness state from
`IPlcControlService` before transfer/download, but runtime lifecycle ownership
stays with the PLC Control Service.

### PLC Control Service

`IPlcControlService` is the intended boundary for simulation/runtime
operations required by a UnitTest run.

The term `runtime` is intentionally application-level. It covers the configured
simulated PLC reference, the selected or prepared simulated PLC instance, the
runtime destination descriptor needed by engineering transfer/download, and the
runtime/PLC state needed to decide whether a UnitTest run can proceed. For the
first Siemens target, the profile is expected to work against one local
simulation environment and a small number of simulated PLC instances. WPF, CLI,
and shared core should not need to know whether the underlying access profile
uses a runtime manager, registered instance, process handle, network adapter, or
simulator API object to represent that state.

The service owns the live runtime access context after the access profile has
been loaded. Within that context it performs runtime operations and reports
runtime-operation diagnostics. It does not own generic plugin loading, vendor
API installation checks, engineering project preparation, UnitTest RunControl
semantics, event-stream interpretation, or final application workflow decisions.

It covers application-level responsibilities such as:

- runtime target or instance discovery,
- runtime selection and preparation,
- runtime/network/interface readiness where required,
- power, run, stop, and reset behavior where owned by the runtime boundary,
- runtime state observation,
- runtime progress/events and diagnostics,
- explicit runtime resource release and technical runtime cleanup.

Runtime resource release intent is application-owned. Shared core derives
`KeepResource` for a runtime obtained through `UseExisting` and
`DestroyResource` for final release of a runtime obtained through
`CreateOwned`. `IPlcControlService` executes the resulting
`PlcRuntimeReleaseRequest` and reports the known resource postcondition through
`PlcRuntimeReleaseOutcome`; it does not repeat or override the ownership
decision.

Technical reconnect and fallback cleanup are separate from resource release.
Reconnect replaces technical access while retaining the selected target and its
original provisioning context. Fallback cleanup may close remaining sessions,
callbacks, workers, and handles, but it must not infer runtime destruction.
Unsuccessful release without a terminal resource outcome remains retryable by
the application owner. A partially destroyed resource is terminal and
unusable, but the release operation remains unsuccessful and diagnosed.

It owns simulation/runtime target resolution. A configured or interactively
selected simulation-instance reference is resolved by this service, not by
`IPlcProjectService`. The resulting runtime destination descriptor can be
consumed by project transfer/download operations, while creation, selection,
readiness checks, and lifecycle of the simulation/runtime remain PLC Control
Service responsibilities.

The runtime state model should be expressed in application-owned terms:

- configured simulated PLC reference,
- available simulated PLC candidates where interactive selection or diagnostics
  require it,
- selected runtime destination descriptor for transfer/download and runtime
  control,
- preparation/readiness state,
- power or availability state where the selected runtime exposes it,
- PLC operating state where the runtime boundary owns observing or changing it,
- network/interface readiness where it affects download, execution, or
  communication,
- active operation state for long-running runtime actions,
- context validity state for cases where the selected runtime can no longer be
  trusted without revalidation.

The service should distinguish application-relevant states such as not
selected, not available, ambiguous, incompatible, preparing, ready for transfer,
ready for execution, running, stopped, changing state, failed, unknown, and
invalidated. These are normalized states, not a promise to expose every
vendor-specific simulator state.

The runtime service reports state and operation outcomes. Shared core and the
applications decide whether a runtime state blocks validation, requires user
correction, allows a manual retry, aborts the current run, or changes final
result/output state.

Runtime target and instance handling requirements:

- accept an application-level simulated PLC reference from configuration or WPF
  selection,
- resolve the configured reference against the small set of available simulated
  PLC instances exposed by the selected access profile,
- enumerate available simulated PLC candidates only where interactive selection,
  validation, or diagnostics require it,
- distinguish not available, ambiguous, incompatible, inaccessible, already in
  conflicting use, and unsupported simulated PLC instances,
- create, register, or provision a simulated PLC instance only where this is
  explicitly requested by application policy; a usable access profile must
  provide the required runtime provisioning behavior for its contract,
- validate runtime settings that affect later project transfer/download and PLC
  communication, such as network/interface readiness, storage or instance
  identity where applicable, and simulator-specific prerequisites,
- expose a selected runtime destination descriptor that can be used by runtime
  control operations and consumed by `IPlcProjectService` for engineering
  transfer/download,
- keep the descriptor application-facing and stable enough for diagnostics,
  logs, and repeated validation without exposing simulator API handles,
- report whether the selected simulated PLC is ready for transfer, ready for
  execution, requires preparation, cannot be prepared, or must be revalidated.

Runtime provisioning must not silently create or replace simulated PLC instances.
Such changes require explicit application policy. WPF should be able to show
what will be used before an operation changes runtime state. CLI execution must
fail deterministically if the configured simulated PLC reference cannot be
resolved without interactive selection.

Runtime lifecycle operation requirements:

- expose explicit operations for runtime provisioning and cleanup,
- expose power or availability operations only where the runtime contract owns
  such a state for the selected profile,
- expose PLC operating-state operations such as run, stop, and reset where these
  are runtime-boundary operations,
- reject lifecycle operations that are invalid for the current runtime state
  instead of silently coercing state,
- report accepted, rejected, completed, completed with warnings, failed, timed
  out, cancelled, and unclear outcomes for runtime lifecycle operations,
- support operation cancellation where the underlying access path can stop or
  abandon a long-running runtime operation safely,
- keep UnitTest RunControl start, abort, reset, and completion semantics outside
  this service.

Runtime state observation, progress, and event requirements:

- expose current runtime and PLC operating state in normalized application terms,
- surface readiness for transfer/download separately from readiness for UnitTest
  execution where those readiness states differ,
- provide progress snapshots or progress events for long-running runtime
  operations such as preparation, power changes, run/stop/reset, network
  readiness, and cleanup,
- report runtime state changes that can affect an active workflow, including
  unexpected stop, lost runtime connection, changed instance identity,
  unavailable network/interface, or invalidated runtime context,
- make repeated or noisy low-level runtime errors observable as stable
  diagnostics and state transitions instead of uncontrolled message streams,
- avoid interpreting UnitTest result meaning from runtime state alone; final
  test result interpretation remains shared UnitTest communication behavior.

If the simulated PLC changes state unexpectedly while a UnitTest workflow is
active, `IPlcControlService` reports the runtime fact and context validity
impact. Shared core decides whether the active workflow pauses, aborts,
requires revalidation, or changes result/output state.

Runtime operation diagnostics and error requirements:

- report simulated PLC reference failures such as not found, ambiguous,
  inaccessible, incompatible, unsupported, or already in conflicting use,
- report runtime provisioning failures such as instance creation/registration
  rejected by policy, provisioning failure, storage or identity conflict,
  unavailable runtime manager state, or incomplete preparation,
- report network/interface failures that affect engineering transfer/download or
  PLC communication, including missing, unavailable, incompatible, rejected, or
  changed interface configuration,
- report power, run, stop, reset, and cleanup outcomes including rejected,
  invalid state, timeout, cancellation, failed, unclear, and completed with
  warnings,
- report runtime connection loss, changed simulated PLC identity, unexpected PLC
  stop, unavailable runtime state, and invalidated runtime context while a
  workflow is active,
- include operation phase, severity, stable diagnostic code/category, source
  operation, user-facing summary, technical detail where useful, and evidence
  references that can be shown in WPF, emitted by CLI, or stored in logs,
- normalize simulator-specific status codes, connection states, network details,
  and runtime API errors without leaking concrete runtime API handles, exception
  types, or state objects as application contracts,
- provide enough context for WPF recovery actions and deterministic CLI exit
  behavior without deciding the final workflow or result state.

Plugin discovery, plugin loadability, missing simulator API installations, and
generic access-profile initialization failures are outside this service. Project
open, compile, target resolution, and engineering transfer/download diagnostics
belong to `IPlcProjectService`. UnitTest RunControl and event-stream diagnostics
belong to their dedicated communication services, even if a runtime failure
caused those contexts to become invalid.

It should not expose PLCSIM Advanced runtime object models to WPF, CLI, or
shared core.

### UnitTest Lifecycle Service

`IUnitTestLifecycleService` is the intended boundary for PLC-side UnitTest
RunControl lifecycle behavior.

The PLC framework owns its RunControl semantics, command protocol, status
projection and compatibility metadata. Each access profile maps its owning
framework contract to this neutral lifecycle service; it must not redefine
PLC-owned semantics or treat status projection as event-stream evidence.

Reference snapshots under `docs/references/` retain the owning project's source
metadata. Refresh copies only from their owner and review changes for consumer
impact. Concrete symbolic paths, field layouts, write boundaries and version
compatibility belong to the profile's documentation, not this global boundary.

It covers application-level responsibilities such as:

- validating PLC-side RunControl contract metadata,
- observing RunControl readiness and status,
- requesting RunControl start, abort, and reset actions,
- reporting immediate command transport/API-write acceptance independently from
  later PLC reaction,
- reporting PLC-side lifecycle completion through the normalized RunControl
  status projection,
- reporting lifecycle-context validity and diagnostics.

PC-side lifecycle responsibilities are limited to contract-facing access and
evidence delivery:

- validate the PLC-side RunControl contract metadata before issuing lifecycle
  command requests,
- read the contract-defined RunControl status projection as independent coarse
  execution status, execution signals, detailed state, and result evidence,
- write only the contract-defined RunControl lifecycle request fields for start,
  abort, and reset,
- issue RunControl command requests as positive-edge requests and avoid
  continuous rewrites while a command is pending,
- report command acceptance from the bounded external API write operation only,
- use `ExecutionStatus = Idle` together with `Ready = TRUE` as the exposed
  normal start gate for `Start`,
- expose command request outcomes and unclear states to shared core and
  applications without deciding final run result meaning,
- report lifecycle-context invalidation when contract metadata, symbolic access,
  runtime state, external download, or PLC-side state changes make the current
  RunControl access context untrustworthy.

The service must not:

- expose symbolic PLC paths, PLC DB structures, numeric PLC constants, raw PLC
  field names, or access-profile-specific field layouts to WPF, CLI, or shared
  core,
- write PLC-owned status, result, metadata, event-buffer, Test Suite, Test Case,
  Preparation, Assertion, configuration, or implementation data outside the
  contract-defined request fields,
- repair PLC startup or readiness problems through undocumented writes,
- treat `Status.Execution.Result`, `Status.Execution.Completed`, or
  `ExecutionStatus.Completed` as final UnitTest result evidence,
- consume event-buffer entries, derive event-stream completeness, acknowledge
  processed events, or interpret aggregation evidence,
- decide final workflow, result, artifact, WPF, CLI, or CI/CD outcome state.

The access profile implementation owns the concrete contract mapping from
application-level lifecycle operations to PLC symbolic paths, data types,
constants, and read/write mechanics. `IUnitTestLifecycleService` exposes
application-facing lifecycle concepts such as readiness, command availability,
start/abort/reset request outcomes, lifecycle progress evidence, completion
evidence, unclear states, and diagnostics. Applications must not need PLC
constant values, symbolic paths, DB layouts, or Siemens API types to use the
service.

The access profile also owns the concrete communication cycle for lifecycle
access. It decides whether lifecycle evidence is obtained by polling, callbacks,
driver events, batched reads, retries, or another transport-specific strategy.
Shared core and applications configure policy and consume normalized lifecycle
evidence/events; they do not drive low-level PLC read/write cycles directly.

`TestRunCommandResult.Accepted = true` means the access profile completed the
external command write without an API-level failure. It does not mean that a
PLC-side lifecycle transition has already been observed. The profile publishes
later RunControl changes independently; shared-core command-reaction monitoring
uses fresh observations and its own bounded monitoring policy.

The application-facing observation preserves four independent values:

- `UnitTestExecutionStatus` for the coarse lifecycle phase,
- `UnitTestExecutionSignals` for all contract-defined independent status bits,
- `UnitTestLifecycleState` for the detailed PLC state-machine state,
- `UnitTestResultState` for the producer-side RunControl result projection.

The profile must not derive the detailed state from execution status or signals.
Generic start readiness requires coarse `Idle` plus the `Ready` signal. This is
an intentionally stricter consumer gate where the producer requires only
`Ready`; it does not assume or synthesize `Idle` from that signal. Start,
abort, and reset reaction monitoring uses execution status and the minimum
required signals; detailed state remains supplemental. Contradictory signal
combinations remain visible for diagnostics and are not normalized into another
result.

On communication stop or invalidation, the lifecycle service clears its current
observation to unknown execution status, no signals, unknown detailed state, and
not-evaluated result. Access health and diagnostics carry communication failure;
the service does not synthesize PLC-side `Error` evidence.

RunControl contract metadata validation requirements:

- validate the RunControl direct-access contract metadata before exposing
  lifecycle command availability,
- verify that the lifecycle access context matches the expected PLC-owned
  communication contract version consumed by this project,
- verify that required lifecycle command request fields and status projection
  fields are accessible through the selected access profile before command
  execution,
- validate type compatibility, string-size compatibility, and read/write access
  capability inside the access profile implementation,
- expose only application-facing compatibility state and diagnostics, such as
  compatible, incompatible, missing, unreadable, unwritable, stale, ambiguous,
  or unknown,
- include PLC-side framework/version metadata in diagnostics where useful for
  traceability, without making framework-version strings part of application
  control flow,
- invalidate the lifecycle access context when previously validated contract
  metadata changes, becomes unreadable, or no longer matches the active
  lifecycle context.

Contract metadata validation is a gate for lifecycle commands. A start, abort,
or reset request must not be issued through an unvalidated, incompatible, stale,
or unclear RunControl contract context. Shared core and applications decide how
to present or recover from validation failures.

RunControl command abstraction requirements:

- expose start, abort, and reset as asynchronous lifecycle command request
  operations,
- require a validated compatible lifecycle access context before a command
  request operation starts,
- check command availability in application-facing terms before writing a
  request,
- issue the request using the selected framework's command protocol through the
  access profile implementation,
- return the bounded transport/API-write outcome without waiting for a later
  PLC handshake or lifecycle transition; publish later observations separately,
- report final command request outcome separately from final UnitTest result
  interpretation,
- support caller-provided timeout and cancellation where practical,
- surface progress only as auxiliary lifecycle evidence for WPF, CLI logs, and
  diagnostics, not as a separate control model.

Command progress evidence can include application-facing states such as waiting
for readiness, command pending, command accepted, command reset observed,
RunControl running, RunControl completing, RunControl completed evidence,
timeout evidence, unclear state, and lifecycle diagnostics. These progress
states are normalized evidence names; applications must not depend on PLC field
names, numeric PLC constants, or access-profile-specific status structures.
They may be delivered as operation updates, events, snapshots, or another
normalized evidence stream chosen by the service contract.

Start command behavior:

- require the exposed normal start gate to be ready before requesting start,
- reject start when the lifecycle context is not ready, incompatible,
  invalidated, or another command is pending,
- after the request is accepted, expose RunControl lifecycle evidence while the
  UnitTest event-stream service delivers final events and Shared Core owns their
  result interpretation.

Abort command behavior:

- request controlled abort handling for an active or unclear run where
  application policy allows it,
- report whether the abort request was accepted, rejected, timed out, failed, or
  left the lifecycle state unclear,
- keep final abort result evidence and aggregation evidence with the event-stream
  interpretation path.

Reset command behavior:

- request return to the PLC-defined reset/startable base state where application
  policy requires it,
- return reset write acceptance separately from later readiness observations;
  Shared Core monitors the expected reaction without extending the command call,
- not treat reset as a mandatory hidden precondition for every run; PLC startup
  validity is owned by the PLC-side UnitTest application.

PLC-side lifecycle completion evidence requirements:

- expose PLC-side RunControl completion evidence as normalized lifecycle
  evidence when the contract-facing status projection reports completed
  lifecycle state,
- distinguish completion evidence from final UnitTest result evidence,
  event-log completeness, aggregation evidence availability, artifact state, and process
  exit state,
- surface unclear completion when the command was accepted but completion
  evidence cannot be observed before timeout, cancellation, context
  invalidation, or communication loss,
- make completion evidence available to shared core and applications so they can
  coordinate event-stream monitoring and recovery decisions,
- keep event-stream completion anchor interpretation with the event-stream
  service and shared communication interpretation.

Lifecycle context validity requirements:

- treat the lifecycle access context as valid only while the selected runtime,
  symbolic access path, contract metadata, and RunControl status projection
  remain consistent with the validated context,
- invalidate the context when PLC blocks are externally downloaded, contract
  metadata changes, symbolic reads/writes fail repeatedly, the runtime context
  changes, or the access profile reports that the current communication context
  can no longer be trusted,
- report invalidation as a stable lifecycle diagnostic and state change instead
  of continuing uncontrolled command retries or status reads,
- require revalidation or reconnect before further lifecycle commands are
  issued through an invalidated context,
- allow shared core and applications to decide whether invalidation pauses,
  aborts, resets, retries, or fails the active workflow.

UnitTest lifecycle operation diagnostics and error requirements:

- report RunControl contract compatibility failures such as incompatible,
  missing, unreadable, stale, ambiguous, or unknown contract metadata,
- report lifecycle access failures such as missing, unreadable, or unwritable
  lifecycle command access, unavailable status projection, type conversion
  failure, string-size mismatch, or access-profile communication failure,
- report command request failures such as not ready, command already pending,
  rejected, write failed, transport timeout, cancellation or unclear write outcome;
  later missing reactions are independent observation/monitor findings,
- report lifecycle observation failures such as status read failure, lost
  communication, completion evidence not observed, unexpected status regression,
  invalidated context, or stale lifecycle evidence,
- classify diagnostics by lifecycle operation phase such as validation,
  readiness observation, command write, later reaction observation, completion
  observation, context validation, and cleanup/reconnect preparation,
- include severity, stable diagnostic code/category, source operation,
  user-facing summary, useful technical detail, and evidence references,
- normalize contract-facing and access-profile-specific technical details without
  exposing PLC paths, PLC constants, DB layouts, Siemens API objects, exception
  types, or raw field values as application contracts,
- coalesce repeated equivalent communication failures into stable diagnostics or
  state transitions instead of emitting uncontrolled duplicate messages.

Plugin discovery, plugin loadability, missing Siemens API installations,
simulation runtime lifecycle failures, engineering project failures, and
event-stream consumption failures are outside this service. Runtime-caused
invalidations may be reported here only as lifecycle context invalidation when
they affect the current RunControl access context.

The concrete lifecycle access path is profile-owned; the selected profile maps
its runtime transport to the PLC-owned RunControl contract. The lifecycle service reports PLC-side
RunControl facts. It does not decide final test result meaning.

RunControl status completion is PLC-side lifecycle status projection. The
corresponding event-stream state-change event is final event evidence for that
lifecycle transition, but it is not the same signal.

### UnitTest Event Stream Service

`IUnitTestEventStreamService` is the intended boundary for PLC-side UnitTest
event-stream evidence delivery.

The selected PLC framework owns event meaning, lifecycle completion, sequence
and epoch semantics, loss evidence and producer/consumer release rules. The
access profile maps that framework's publication and transport to the neutral
event-stream service without transferring result interpretation into the driver.

Keep PLC-owned reference snapshots and source metadata under `docs/references/`;
refresh them from their owner rather than modifying them to fit consumer code.
Review semantic changes separately from access-path or transport changes.
Profile-owned documentation describes the applicable framework contract,
concrete mapping and qualification limits. No single vendor's FIFO layout or
write protocol is a universal requirement for all access profiles.

It covers application-level responsibilities such as:

- validating PLC-side event-stream contract metadata,
- reading event-buffer publication evidence,
- providing event ordering, sequence, ownership, completeness-related, and
  stale-data evidence,
- reporting lost, incomplete, implausible, reset, or invalid event-stream
  evidence,
- requesting processed-event release where required by the access profile,
- reporting event-stream-context validity and diagnostics.

The service acts as the PC-side technical consumer boundary toward the
PLC-owned event stream. It is responsible for exposing normalized event-stream
delivery evidence to the shared core, including newly available event data,
stream/epoch identity, sequence and ordering evidence, event-buffer emptiness
evidence, lost-event evidence, release-request state, and event-stream validity
diagnostics. The service should support an event-driven consumption model where
newly observed event-stream data is pushed to the higher layer through callbacks
or equivalent notifications. Snapshot or asynchronous operation shapes may still
be used where they fit the implementation design, but the service must not
require the application to poll low-level PLC/event-buffer data directly.

The service does not own the PLC-side event producer, the UnitTest event model,
FIFO processing, event-buffer storage, event payload semantics, lifecycle
semantics, result semantics, or aggregation semantics. It also does not decide
WPF presentation, CLI formatting, report artifacts, exit codes, or workflow
policy. Those decisions belong to shared application behavior or to the concrete
applications that consume normalized evidence from the service.

The access profile owns the concrete communication strategy behind the service.
Polling, callback/event subscriptions, batched reads, retries, cycle-gap
coordination, transport-specific buffering, and transport-specific release
mechanics are implementation details of the selected access profile. The shared
core and applications configure policy and consume normalized event-stream
evidence; they do not perform low-level PLC/event-buffer read and write cycles.

The service must preserve the single logical consumer model from the PLC
producer perspective. If a concrete access profile uses several technical
readers or an intermediate transport, the profile must still present one
logical event-stream consumer and one coherent release-progress owner to the
application-facing boundary.

The service exposes no PLC constants, symbolic paths, data block layouts,
absolute addresses, Siemens API objects, transport endpoint details, or raw PLC
field values as application contracts. Contract-facing and access-profile
details may appear only as diagnostics or evidence references where useful for
troubleshooting.

Event-stream contract metadata validation requirements:

- validate the event-stream contract metadata before interpreting event-buffer
  evidence,
- verify that the event-stream context matches the expected PLC-owned
  event-stream model contract version consumed by this project,
- expose only application-facing compatibility state and diagnostics, such as
  compatible, incompatible, not available, unreadable, stale, ambiguous, or
  unknown,
- include PLC-side framework/version metadata in diagnostics where useful for
  traceability, without making framework-version strings part of application
  control flow,
- treat unsupported event-stream contract versions as incompatible instead of
  guessing field layouts, payload semantics, sequence semantics, or completion
  rules,
- invalidate the event-stream access context when previously validated contract
  metadata changes, becomes unreadable, or no longer matches the active
  event-stream context.

Contract metadata validation is a gate for event-stream interpretation and
release-progress handling. Event entries, event-buffer status, FIFO state, and
release progress must not be interpreted through an unvalidated, incompatible,
stale, or unclear event-stream contract context. Shared core and applications
decide how to present or recover from validation failures.

Event-buffer evidence access requirements:

- expose newly available event data only after deriving the readable event range
  from producer-owned FIFO state,
- expose event-buffer empty/non-empty evidence without treating emptiness alone
  as run completion,
- expose stream/epoch evidence so shared core EventStream Processing can distinguish
  one observed producer epoch from another,
- expose sequence evidence for fetched entries, last producer-assigned
  sequence, last producer-deleted sequence, and detected gaps where the access
  profile can observe them,
- expose lost-event evidence from producer-owned status as incomplete-stream
  evidence,
- expose FIFO/configuration/index-state evidence that makes event-buffer
  consumption unreliable, deferred, or invalid,
- use the producer-published effective buffer length from the event-stream model
  instead of any application-known PLC constant,
- treat only fetched entries inside the readable range and with a produced
  sequence identity as UnitTest event evidence.

Snapshot validity requirements:

- the access profile must surface whether delivered evidence is a coherent
  snapshot, a best-effort snapshot, a retry/deferred observation, or an invalid
  observation,
- non-atomic reads must not be silently presented as fully coherent event-stream
  evidence when the access profile cannot guarantee coherence,
- stale, partial, inconsistent, or implausible observations must be exposed as
  diagnostics or validity states instead of being repaired by the application
  layer,
- event-buffer epoch reset invalidates pending event-ordering and release
  assumptions from the previous epoch,
- producer-side lost-event evidence must be surfaced as incomplete-stream
  evidence for shared interpretation even if later events can still be consumed,
- the service must not repair event-buffer state by writing producer-owned FIFO,
  status, contract metadata, or event-entry storage.

Event delivery, completion evidence, and result-evidence delivery requirements:

- expose newly available event data in producer FIFO order within each delivered
  event batch or notification,
- expose event sequence identities as delivery and plausibility evidence, not as
  UnitTest lifecycle or result semantics by themselves,
- report sequence gaps, epoch changes, lost-event evidence, and transport/access
  plausibility findings as event-stream evidence for shared interpretation,
- expose event owner, event type, state, result, severity, name, and message
  payload information only as normalized event evidence based on the PLC-owned
  event-stream model,
- expose whether the event stream has delivered the Run Control completion
  anchor defined by the event-stream contract as evidence, not as a final
  application decision,
- distinguish the Run Control completion anchor in the event stream from the
  RunControl lifecycle status projection reported by `IUnitTestLifecycleService`,
- expose event-buffer empty observation separately from completion-anchor
  evidence; both are needed by shared core EventStream Processing to evaluate whether
  event streaming for one observed run can be considered finished,
- hand newly delivered event evidence and event-stream observations to shared
  core EventStream Processing for run reconstruction, result interpretation,
  aggregation interpretation, event-log completeness evaluation, and application
  outcome interpretation.

`IUnitTestEventStreamService` does not decide final UnitTest run outcome. It
provides the event-stream evidence needed for shared core EventStream Processing
to reconstruct observed UnitTest runs, including runs that may have been
triggered PLC-side instead of by the application. Shared core EventStream
Processing combines UnitTest lifecycle evidence, event-stream completion-anchor
evidence, event-buffer empty observations, timeout policy, and optional grace
periods to decide whether all event data for an observed run has been captured.
It also decides whether result evidence is missing, whether optional aggregation
evidence is unavailable or contradictory, and how WPF, CLI, reports, and exit
behavior should reflect that evidence.

Detection and identification of an observed UnitTest run is not owned by
`IUnitTestEventStreamService`. Assigning delivered event evidence to a specific
observed run, including PLC-side-started runs, belongs to shared communication
behavior and the application workflow built on top of lifecycle and event-stream
evidence.

Processed-event release requirements:

- own communication-level release progress for event evidence that was
  successfully fetched and internally validated by the access profile,
- use event sequence identity as release-progress identity, not timestamp,
  physical buffer index, event name, or application run identity,
- release only a contiguous fetched-and-validated sequence
  from the current event-stream epoch,
- reject or defer release progress when the event-stream context, epoch,
  sequence plausibility, contract validation, snapshot validity, or
  single-consumer ownership is unclear,
- preserve the PLC producer ownership model: the service requests release but
  does not delete, clear, repair, or rewrite event-buffer storage,
- expose release request state and producer-observed release progress as
  normalized evidence, including pending, accepted, completed, rejected, timed
  out, unclear, failed, or invalidated outcomes where applicable,
- serialize or coordinate release requests so that a later request cannot
  overtake an earlier pending release request within the same logical consumer,
- invalidate pending release assumptions when the event-buffer epoch changes,
  contract metadata changes, the access context is lost, or producer evidence no
  longer matches the request context.

Release is communication progress, not application processing progress.
`IUnitTestEventStreamService` may request release once the selected access
profile has fetched event data, verified it enough for safe PC-side capture, and delivered
or queued the normalized evidence for the higher layer. The higher layer does
not need to have reconstructed a run, interpreted results, updated the UI, or
written artifacts before the communication layer can acknowledge/release the
corresponding producer events.

The event-stream intake hot path belongs to `IUnitTestEventStreamService` and
the selected access profile. Its responsibility is to:

1. read available PLC-buffer entries,
2. perform the minimal technical checks needed for safe PC-side capture,
3. copy the captured evidence into PC-owned immutable event batches,
4. expose or queue those batches through the application-facing service
   boundary,
5. request release/acknowledgement up to the highest safely captured
   `SequenceId`,
6. return to readiness for the next intake cycle.

The minimal technical checks are access/profile checks, not semantic result
interpretation. They include trusted event-stream context, produced/readable
entries, plausible sequence/FIFO evidence for release, and successful PC-side
copy of the batch.

If a future access profile uses implicit, indirect, server-owned, read-only, or
batched release semantics, the profile still has to preserve the
communication-level release-progress concept without changing the PLC-owned
event-stream model.

Event-stream context validity and delivery ownership requirements:

- maintain an event-stream access context for the currently loaded access
  profile and selected PLC/runtime context,
- report event-stream context validity as normalized state instead of relying on
  repeated low-level exceptions as the application-facing signal,
- invalidate or suspend the event-stream context when contract metadata changes,
  becomes unreadable, or no longer matches the validated event-stream contract,
- invalidate or suspend the context when the event-buffer epoch resets in a way
  that invalidates current ordering, release, or capture assumptions,
- report context invalidation when runtime communication is lost, the selected
  simulated PLC instance changes, relevant PLC blocks are externally downloaded,
  or the access profile can no longer prove that it observes the same
  event-stream producer,
- stop or quiesce event delivery when the context is invalid, unless the
  delivered notification explicitly represents invalidation or diagnostic
  evidence,
- coalesce repeated equivalent communication failures into stable context or
  diagnostic updates instead of spamming the application with duplicate event
  notifications,
- provide a clear recovery boundary, such as revalidate, reconnect, restart
  monitoring, or unload/reload access profile, without requiring WPF or CLI to
  manipulate low-level transport state.

The selected access profile owns the running communication and delivery cycle
behind `IUnitTestEventStreamService`. It decides whether event-stream data is
observed by polling, callbacks, driver events, batched reads, cycle-gap
coordination, retries, or another transport-specific strategy. The shared core
and applications subscribe to normalized event-stream evidence, configure
application policy where needed, and react to context state changes; they do not
drive the low-level read, acknowledgement, retry, or reconnect loop directly.

UnitTest event-stream operation diagnostics and error requirements:

- report event-stream contract compatibility failures such as incompatible, not
  available, unreadable, stale, ambiguous, or unknown contract metadata,
- report event-stream access failures such as missing or unreadable event-buffer
  publication access, unavailable FIFO/status/event-entry evidence, type
  conversion failure, string-size mismatch, or access-profile communication
  failure,
- report snapshot/evidence failures such as non-atomic, stale, partial,
  inconsistent, implausible, deferred, or invalid observations,
- report event-buffer model failures such as invalid FIFO configuration, invalid
  FIFO index state, epoch reset, sequence plausibility failure, sequence gap,
  lost-event evidence, or incomplete event-stream evidence,
- report event delivery failures such as notification dispatch failure, dropped
  internal event batch, invalid delivery queue state, or invalidated delivery
  context,
- report release failures such as release request rejected, release write failed,
  producer reset/progress not observed, stale release sequence, timeout,
  cancellation, unclear outcome, or invalidated release context,
- report context failures such as changed contract metadata, changed producer
  identity, changed runtime context, external PLC block download, lost runtime
  communication, disposed profile resource, or repeated communication failure,
- classify diagnostics by event-stream operation phase such as validation,
  observation, snapshot validation, event delivery, release request, release
  progress observation, context validation, and cleanup/reconnect preparation,
- include severity, stable diagnostic code/category, source operation,
  user-facing summary, useful technical detail, and evidence references,
- normalize contract-facing and access-profile-specific technical details without
  exposing PLC paths, PLC constants, DB layouts, Siemens API objects, exception
  types, or raw field values as application contracts,
- coalesce repeated equivalent communication failures into stable diagnostics or
  context state transitions instead of emitting uncontrolled duplicate event
  notifications.

Plugin discovery, plugin loadability, missing Siemens API installations,
simulation runtime lifecycle failures, engineering project failures,
RunControl lifecycle command failures, run reconstruction failures, result
interpretation failures, artifact generation failures, and UI/CLI presentation
failures are outside this service. Runtime-caused invalidations may be reported
here only as event-stream context invalidation when they affect the current
event-stream access context.

The concrete event-stream transport is profile-owned and accesses the
event-buffer and release-request fields under the owning PLC contract. Event meaning,
result evidence, aggregation evidence, and completeness interpretation remain
shared application/communication behavior based on the PLC-owned contracts.

### Access Context Validity

Each functional access boundary has a live access context that can become
invalid while the application is running. Examples include a lost engineering
session, a changed simulation/runtime instance, changed PLC blocks, changed
symbols, changed contract metadata, lost runtime communication, or a disposed
profile resource.

An access interface must be able to report that its access context is no longer
reliable. It should not report an unbounded stream of equivalent operation
failures when the underlying context is structurally invalid.

At architecture level:

- functional service interfaces detect and report operation failures and
  invalidated access contexts for their own area,
- access profiles publish normalized context trust as `AccessHealth` updates
  for `Profile`, `Project`, `PlcControl`, `UnitTestLifecycle`, and
  `UnitTestEventStream`,
- shared core decides what an invalidated context means for validation,
  monitoring, run lifecycle, recovery, and outputs,
- WPF and CLI present or emit the resulting application state and diagnostics,
- recreating `IAccessProfileSession` is a fallback recovery level, not the
  normal response to every communication or runtime problem.

Monitoring behavior must be controlled when an access context becomes invalid.
For example, if the WPF application monitors the UnitTest event stream and PLC
blocks are externally downloaded while monitoring is active, the communication
access context can become invalid. The expected architecture behavior is to
surface a stable invalidated-context diagnostic, stop or pause monitoring, and
require revalidation or reconnect according to application policy instead of
continuing an uncontrolled polling/error loop.

Repeated equivalent access failures should be coalesced at application level.
The UI and CLI should receive stable diagnostic state, optional repetition
counts/timestamps, and clear recovery state rather than repeated duplicate
messages.

`AccessHealth` is the application-facing context-trust signal. It does not
expose vendor API objects, Siemens DLL names, session handles, or profile
internal dependency graphs. A concrete profile maps its internal condition to
one or more affected service kinds plus a stable state, problem category, reason
code, message, and timestamp. If an internal failure affects multiple
application-facing boundaries, the profile publishes multiple health updates.

The profile-level health event is the single subscription point for shared
core. Individual services also expose their current health for initial snapshot
creation and direct validation, but consumers should not need to subscribe to
separate service-specific health events.

`AccessHealthState.NotEstablished` means the context has not yet been prepared
or bound. It is not an error by itself. `Healthy` means the profile currently
trusts the context. `RevalidationRequired`, `Invalidated`, and `Disposed`
represent states that shared core must interpret for gates, recovery, active
observation, and diagnostics according to workflow phase.

### Responsibility Examples

The following examples define ownership boundaries for typical failure and
state-change situations.

| Situation | Detection / Operation Owner | Application Interpretation Owner |
| --- | --- | --- |
| Configured access profile is missing or cannot be loaded far enough to call profile-specific code. | `AccessProfilePluginLoader`. | Shared core / application validation. |
| Vendor API is missing, incompatible, or not loadable during profile-specific initialization. | `IAccessProfilePlugin` / `IAccessProfileSession`. | Shared core / application validation. |
| TIA Portal cannot be started, opened, or attached through Openness during an engineering operation. | PLC Project Service. | Shared core / application workflow and diagnostics. |
| Engineering project cannot be opened, compiled, or downloaded. | PLC Project Service. | Shared core / application workflow and diagnostics. |
| Simulation/runtime instance cannot be found, prepared, powered, started, stopped, or reset. | PLC Control Service. | Shared core / application workflow, lifecycle, and recovery state. |
| Simulation PLC stops unexpectedly while the application is running. | PLC Control Service reports the observed runtime/PLC state change; the selected profile closes affected UnitTest communication. | Shared core releases lifecycle/event-stream observation. An active RunEpoch is invalidated; without an active RunEpoch, technical monitoring closes without fabricating a run result. A later observed PLC start prepares a fresh communication session. |
| PLC blocks are externally downloaded while monitoring is active. | PLC Control Service may report runtime state changes; UnitTest Lifecycle Service and UnitTest Event Stream Service report changed/invalid contract or communication contexts as applicable. | Shared core invalidates monitoring/run context as needed and requires revalidation or reconnect according to application policy. |
| RunControl DB, symbol, or contract metadata is missing, changed, unreadable, or incompatible. | UnitTest Lifecycle Service. | Shared core / application validation, lifecycle, monitoring, and diagnostics. |
| Event-buffer DB, symbol, or contract metadata is missing, changed, unreadable, or incompatible. | UnitTest Event Stream Service. | Shared core / application validation, evidence monitoring, lifecycle, result, and diagnostics. |
| RunControl command cannot be requested or remains unclear. | UnitTest Lifecycle Service. | Shared core / application lifecycle and recovery state. |
| Event-stream evidence is incomplete, implausible, or stale. | UnitTest Event Stream Service provides access evidence; shared core EventStream Processing interprets event-stream meaning and completeness. | Shared core decides lifecycle/result/output impact. |
