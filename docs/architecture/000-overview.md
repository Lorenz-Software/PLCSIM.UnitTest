# Architecture Overview

This document is the entry point for the `PLCSIM.UnitTest` architecture documentation. It summarizes the stable architecture intent and points to focused documents for the application layer, shared core, and access profile layer. Concrete profiles own their technology-specific design documentation.

## Purpose

`PLCSIM.UnitTest` is a two-application suite for executing PLC unit tests
against engineering and runtime environments exposed by access profiles:

- a WPF application for interactive local execution, diagnostics, configuration,
  and manual validation,
- a CLI application for scripting, CI/CD integration, unattended execution,
  diagnostics, and artifact generation.

Both applications should use the same shared application behavior where
practical. Differences between the WPF and CLI applications should be expressed
in their application layer, not by duplicating runtime, communication, result, or
plugin logic.

## Application Responsibilities

### WPF Application

The WPF application is the primary manual validation and diagnostic surface for
the current development target.

It should:

- show configured engineering, simulation/runtime, communication, and plugin
  availability,
- guide the user through validation, preparation, execution, abort/reset, result
  review, and recovery,
- expose manual control over the run lifecycle where appropriate,
- present actionable diagnostics and related evidence,
- support repeated local runs with predictable configuration and output
  behavior,
- use shared core behavior instead of reimplementing run semantics in view
  models or views.

### CLI Application

The CLI application is the automation surface for the same application behavior.

It should:

- validate planned runs deterministically,
- execute prepared runs without interactive prompts,
- emit structured diagnostics and process outcomes,
- produce CI/CD-friendly artifacts, with JUnit XML as the first test-report
  artifact format,
- support environment and plugin diagnostics,
- reuse shared core behavior and the same access/profile abstractions as the WPF
  application.

## Architecture Principles

- WPF remains the desktop UI framework for the interactive application.
- WPF and CLI are separate, equal applications over shared application behavior.
- Shared functionality belongs in a shared core where it is meaningful and
  testable.
- Application-facing contracts are owned by this project and must not mirror
  concrete vendor APIs.
- Concrete vendor dependencies stay inside versioned access-profile or plugin
  projects.
- Supported products, versions, and real-runtime qualification belong to the
  owning profiles and bindings, not to the global architecture.
- Profiles map their PLC-owned communication contracts to the shared application
  boundary. Contract snapshots are grouped by framework under `docs/references/`.
- WPF supports manual diagnostics and validation; CLI is the primary automation
  path over the same shared workflow.
- Access profiles isolate differing engineering, runtime, and communication
  technologies without application-side interpretation of vendor semantics.
- Tests without installed vendor software should be possible for shared logic,
  model behavior, diagnostics, and fake access/profile behavior where practical.

## Layer Model

The intended application structure is layered by responsibility:

| Layer | Responsibility |
| --- | --- |
| WPF Application | Interactive operation, manual workflow guidance, configuration editing, diagnostics presentation, real-runtime validation, and local result inspection. |
| CLI Application | Non-interactive execution, scripting, CI/CD integration, deterministic exit behavior, machine-readable diagnostics, and artifact generation. |
| Shared Core | Common orchestration, validation, lifecycle state, result interpretation, diagnostics classification, artifact coordination, and shared application semantics. |
| Application Access Boundary | Project-owned abstractions for external engineering, simulation/runtime, and UnitTest communication capabilities. |
| Access Profile / Plugin Layer | Concrete composition and loading of access-profile technology bindings behind application-facing abstractions. |
| External Environments | Profile-owned engineering, runtime, and communication technologies, and PLC-side UnitTest contracts. |

The WPF and CLI applications should not directly reference Siemens API object
models. Shared core should also stay independent from Siemens assemblies.
Concrete Siemens integration details belong below the application access
boundary.

## System Workflow

The system workflow is the application-wide orchestration model used by both
applications. It coordinates the functional services exposed through an access
profile without taking ownership of their concrete project, runtime, lifecycle,
or event-stream implementation.

The workflow is modeled around these design elements:

| Element | Design Role |
| --- | --- |
| Workflow Phase | Coarse execution segment used for orchestration, display, and command policy. |
| Workflow State | Current application-level state derived from phase, active operation, context validity, and service feedback. |
| Workflow Context | The trusted set of configuration, profile binding, project context, runtime target, lifecycle channel, and event-stream channel used by the current workflow. |
| Active Operation | The currently running long operation, including its owner service, cancellation boundary, and progress/diagnostic feedback. |
| Action Gate | The decision object that classifies whether a workflow action is available, pending, blocked, unsafe, or not applicable. |
| Update Boundary | Stable transition where evidence or state becomes available to result interpretation, diagnostics, output generation, cleanup, or application consumers. |

WPF and CLI consume the same workflow model. WPF turns it into an interactive
manual workflow. CLI executes it as an unattended command pipeline.

Detailed run reconstruction, result interpretation, diagnostics, output
artifacts, and implementation placement are defined separately.

### Workflow Phase Model

Each phase is represented as workflow metadata, not as a UI screen or access
profile implementation class. A phase definition should provide:

- a stable phase identifier,
- required trusted contexts,
- the functional service operation that advances the phase, if any,
- action gates that become relevant in the phase,
- phase completion evidence,
- update/output data produced by the phase.

| Phase | Required Context | Advancing Operation | Completion Evidence | Published Update / Output |
| --- | --- | --- | --- | --- |
| Configuration | None. | Capture and normalize the selected run setup from user selections or command inputs. | Required selections are available or setup diagnostics identify what is missing. | Normalized run setup. |
| Environment And Profile Validation | Normalized run setup. | Bind the selected access profile and validate required service bindings. | Access profile is bound or validation has failed. | Profile self-information, service availability, profile diagnostics. |
| Project Preparation | Bound access profile and project configuration. | Prepare a transferable PLC project state through the project service. | Project context is transferable or preparation failed. | Trusted project context and project diagnostics. |
| Runtime Provisioning | Bound access profile and runtime configuration. | Resolve, create, or attach to a simulation/runtime target through the PLC control service. | Runtime target is available as download destination or provisioning failed. | Trusted runtime target and runtime diagnostics. |
| Project Transfer | Transferable project context and provisioned runtime target. | Transfer project state to the runtime target through the project service. | Runtime target contains the transferred project state or transfer failed. | Updated project/runtime context trust. |
| Runtime Startup | Provisioned runtime target with transferred project state. | Start the PLC/runtime through the PLC control service. | Runtime is started and reachable or startup failed. | Started runtime context. |
| Unit Test Communication Validation | Started or intentionally observed runtime target. | Validate lifecycle and event-stream channels through the UnitTest services. | Communication channels are usable or validation failed. | Trusted lifecycle and event-stream communication context. |
| Execution Start And Observation | Trusted communication context. | Start a run through the lifecycle service, or enter observation for a detected PLC-side-started run. | Run start/active evidence is available or start/observation failed. | Initial run evidence. |
| Execution Monitoring | Active run evidence and trusted communication context. | Consume lifecycle and event-stream feedback. | Lifecycle completion, abort, failure, or lost trusted context. | Collected run evidence. |
| Completion Stabilization | Lifecycle completion or equivalent terminal evidence. | Keep communication open long enough to receive final event-stream data. | Stabilization window has ended or context trust was lost. | Final collected run evidence. |
| Result And Output Coordination | Final collected run evidence or structured log/result snapshot. | Update the shared log/result model and coordinate configured outputs/artifacts. | Evidence was accepted, rejected as unusable, or recorded as partial/incomplete diagnostics. | Structured log/result update and output coordination state. |
| Cleanup And Recovery | Any active or completed workflow context. | Release, reset, reconnect, or reload through the owning service/profile path. | Workflow returned to a known state or ended as failed/cancelled. | Reusable, recovered, or terminal workflow context. |

The same phase model applies to WPF and CLI. WPF exposes phases as a manual,
observable workflow. CLI executes them deterministically according to command
configuration and exits with a process outcome.

The current workflow design targets one primary prepared PLC/runtime target per
workflow run. Future scenarios may require coordinating multiple PLC/runtime
targets for integration-style tests. Multi-PLC coordination is out of scope for
the current target, but architecture terms and abstractions should avoid
unnecessary assumptions that would make later multi-target support expensive.

### Workflow State Model

The workflow exposes one application-level state object. That state is the single
source for WPF presentation and CLI command policy.

The state object should contain:

- current phase,
- current workflow state,
- active operation descriptor or no active operation,
- current context trust flags for configuration, profile, project, runtime,
  lifecycle communication, and event-stream communication,
- current action gate results,
- current diagnostics summary,
- update/output availability for run observation, result interpretation, output
  generation, or cleanup.

The workflow states are independent from WPF controls and CLI command names.

| State | Meaning |
| --- | --- |
| `NotConfigured` | No usable run setup has been selected or resolved. |
| `Configured` | Configuration exists, but validation has not confirmed that it can be used. |
| `ValidationRequired` | Existing workflow context is stale or has changed and must be validated again. |
| `ValidationBlocked` | Validation failed because required configuration, profile, environment, or service availability is missing. |
| `ReadyForPreparation` | The access profile and required service bindings are available and preparation can start. |
| `Preparing` | Project preparation, runtime provisioning, project transfer, or runtime startup is currently running. |
| `ReadyToExecute` | Project, runtime, lifecycle communication, and event-stream communication are prepared for execution or observation. |
| `Starting` | The application requested a unit test run start and is waiting for observable lifecycle feedback. |
| `Running` | A unit test run is active or being observed. |
| `CompletionPending` | Lifecycle completion has been observed, but the workflow is still allowing final communication evidence to settle. |
| `Completed` | The workflow has completed execution/observation and can hand over evidence for interpretation and outputs. |
| `Aborting` | The application requested an abort and is waiting for a defined lifecycle outcome. |
| `Resetting` | The application requested a lifecycle reset and is waiting for the communication state to become reusable. |
| `CleaningUp` | The workflow is releasing resources or returning to a defined reusable state. |
| `RecoverableFailure` | The workflow encountered a failure where revalidation, reconnect, monitoring restart, or profile reload may restore operation. |
| `TerminalFailure` | The workflow cannot continue safely without ending the current operation. |
| `Cancelled` | The user or automation policy cancelled the current workflow before normal completion. |
| `Unknown` | The application can no longer trust its current workflow context and must revalidate or recover before continuing. |

The state model is intentionally application-level. Functional services may have
their own internal states and diagnostics, but the application should only depend
on the abstract workflow states and service results/events.

### Action Availability

Action availability is modeled as an action gate result produced by the workflow,
not as WPF control state or CLI option handling. WPF presents action gate results
as user commands and guidance. CLI uses the same gate results to decide whether
to continue, fail early, cancel, or execute a recovery policy.

An action gate result should contain:

- action identifier,
- category,
- short reason code,
- human-readable explanation,
- required phase/state/context change, if blocked,
- owning service/profile boundary, if the action would delegate work,
- cancellation or recovery precondition, if unsafe.

Action availability should use the following categories:

| Category | Meaning |
| --- | --- |
| `Available` | The action can be started from the current workflow state. |
| `Pending` | The action is already running or depends on an active operation finishing first. |
| `Blocked` | The action is not possible until a known prerequisite is fixed or completed. |
| `Unsafe` | The action may corrupt workflow assumptions, lose relevant evidence, or interfere with an active operation. It must not run without an explicit recovery/cancellation path. |
| `NotApplicable` | The action has no meaning in the current state or workflow mode. |

The core workflow actions are:

| Action | Available When | Blocked Or Unsafe When |
| --- | --- | --- |
| Validate configuration/environment | Configuration exists or the workflow requires revalidation. | Blocked when no usable configuration is present; unsafe while project transfer, lifecycle control, or cleanup is running. |
| Prepare project | Validation succeeded and no active transfer/run is in progress. | Blocked by missing project context; unsafe during transfer, runtime startup, or active run monitoring. |
| Provision runtime | Validation succeeded and the target runtime configuration is known. | Blocked by unavailable runtime environment; unsafe during transfer or active run monitoring unless recovery explicitly owns the transition. |
| Transfer project | Project preparation and runtime provisioning succeeded. | Blocked until both source project and target runtime are ready; unsafe while the PLC is actively running a test unless the workflow has entered a defined recovery/cancellation path. |
| Start runtime | Project transfer succeeded and the runtime target is ready. | Blocked before transfer; unsafe if the runtime state is unknown or cleanup/recovery is active. |
| Validate unit test communication | Runtime startup succeeded or an already-running runtime is intentionally observed. | Blocked before runtime availability; unsafe while lifecycle reset/abort or communication cleanup is active. |
| Start unit test run | Unit test communication is valid and the workflow is ready to execute. | Blocked before communication validation; unsafe when another run is active or completion is still pending. |
| Observe active run | Unit test communication is valid and lifecycle/event evidence indicates an active run. | Blocked without communication; unsafe if the workflow cannot trust whether observed data belongs to the current target context. |
| Abort unit test run | A run is starting or running and lifecycle control is available. | Not applicable when no run is active; unsafe during cleanup or when the workflow context is unknown. |
| Reset unit test lifecycle | Lifecycle communication is available and no unsafe transfer/startup operation is running. | Blocked without lifecycle communication; unsafe during project transfer or while final event evidence is still settling unless recovery owns the reset. |
| Restart monitoring | Event-stream communication was interrupted but the target context is still trusted. | Blocked when communication cannot be re-established; unsafe when target context changed and revalidation is required. |
| Reconnect services | The workflow is in a recoverable or validation-required state. | Blocked by missing environment/profile availability; unsafe if reconnect would hide an unfinished abort/reset/cleanup operation. |
| Reload access profile | The current profile context is invalid, stale, or explicitly released. | Unsafe while functional service operations are active unless the workflow first cancels or cleans them up. |
| Cleanup | A run, preparation, transfer, or recovery path has ended or must be released. | Pending while cleanup is already running; unsafe only if cleanup would discard evidence that has not reached a defined log/result update or diagnostic boundary. |
| Start next run | The previous run has reached a defined run-cycle boundary and the workflow is ready for a new execution cycle. | Blocked during completion stabilization or unresolved output coordination; unsafe when prior run state is unknown. |

At concept level, the workflow does not need a full transition graph for every
state/action pair. The important rule is that action availability is derived from
the workflow state, trusted context validity, active operation, and operation
ownership boundaries.

### Blocked, Unsafe, And Pending Conditions

Blocked conditions are expected prerequisites that can be presented or reported
directly, such as missing configuration, unavailable access profile, unavailable
runtime target, failed project preparation, or missing communication setup.

Pending conditions are active operations where the workflow should wait,
observe, or cancel through a defined path before accepting another action. Long
running project preparation, runtime provisioning, project transfer, runtime
startup, lifecycle start/abort/reset, completion stabilization, and cleanup are
all pending-capable operations.

Unsafe conditions are workflow protection boundaries. They should be used when
an action could invalidate the current project/runtime context, break ownership
of an active operation, or lose run evidence. Examples include project transfer
during an active run, profile reload while service operations are active, runtime
changes while event-stream monitoring is trusted, and reset while final run
evidence is still settling.

### Recovery And Cleanup Boundaries

Recovery and cleanup are workflow-owned coordination concerns. They do not move
functional operation ownership out of the services. The workflow decides which
recovery path is allowed, then delegates concrete work to the service or access
profile that owns the affected context.

Recovery should be modeled as explicit paths:

| Recovery Path | Used When | Owner Boundary | Resulting Workflow State |
| --- | --- | --- | --- |
| Revalidate setup/profile | User selections changed, profile binding is stale, or environment availability must be checked again. | Access profile loading and validation stay below the access-profile layer. | `ReadyForPreparation`, `ValidationBlocked`, or `Unknown`. |
| Reprepare project/runtime | Project or runtime context is no longer trusted before a run starts. | Project operations stay in the project service; runtime operations stay in the PLC control service. | Prepared state, recoverable failure, or terminal failure. |
| Reconnect runtime | The prepared target and setup remain valid, but technical runtime or UnitTest communication access is stale, invalid, or intentionally refreshed. | Shared workflow owns the recovery action; the loaded access-profile session atomically replaces its technical runtime and communication resources without releasing the target. | Runtime-prepared state with communication restored according to observed PLC readiness, recoverable failure, or unknown context. |
| Restart event-stream reading | Event-stream reading stopped or failed while PLC/runtime context is still trusted. | Event-stream service owns reading/acknowledgement behavior. | Monitoring-ready state or communication failure. |
| Abort active run | A run is active and the user/automation requests interruption. | Lifecycle service owns abort communication; workflow owns resulting state handling. | Aborted, recoverable failure, or unknown context. |
| Reset UnitTest lifecycle | A completed, aborted, failed-start, or resettable recovery state should become reusable. | Lifecycle service owns reset communication. | Ready-to-start state or reset failure. |
| Stop PLC | Runtime target has a loaded PLC configuration and should be stopped manually or by policy. | PLC control service owns stop behavior; workflow owns communication/context invalidation. | Stopped runtime state, recoverable failure, or unknown context. |
| Reload access profile | Service bindings or profile context are invalid and cannot be repaired in place. | Access profile owns cleanup and rebinding of concrete service instances. | Revalidated profile state or validation blocked. |
| Cleanup session | User requested cleanup, application shuts down, CLI command ends, or terminal failure requires release. | Each functional service/profile releases its own resources. | Idle/released state or terminal cleanup failure. |

Cleanup should run in a defined order so that evidence and ownership boundaries
remain clear:

1. Stop accepting new workflow actions.
2. If a run is active, require an abort/stop-policy decision before discarding
   run context.
3. Stop event-stream reading and lifecycle observation.
4. Preserve or hand over collected run evidence before releasing communication
   state.
5. Release lifecycle and event-stream communication resources.
6. Stop PLC/runtime only when requested by the user, command policy, shutdown
   policy, or recovery path.
7. Release project/runtime/profile resources through their owning services.
8. Mark the workflow context as idle/released or terminally failed.

Cleanup may be best-effort during application shutdown, but the workflow should
still report which cleanup steps completed, failed, or were skipped because an
owner was no longer reachable.

### State Ownership Boundaries

- WPF owns view state, user interaction state, and presentation-specific command
  enablement.
- CLI owns command execution policy, process outcome handling, and unattended
  cancellation behavior.
- The shared core owns the common workflow state, phase coordination, and
  reusable orchestration rules.
- The access profile owns profile loading, service binding, and profile-specific
  communication/resource lifecycle.
- Functional services own their concrete operation state and report operation
  results, communication events, and diagnostics to the shared core.

If a functional service reports that its context is no longer usable, the shared
workflow maps that condition to `ValidationRequired`, `RecoverableFailure`, or
`Unknown` depending on whether the current context can still be trusted. Exact
action availability and recovery paths are defined separately.

### Application-Triggered And PLC-Side-Started Runs

The workflow must support both application-triggered unit test runs and observed
PLC-side-started runs.

For application-triggered runs, the workflow moves from `ReadyToExecute` through
`Starting` into `Running` based on lifecycle feedback.

For PLC-side-started runs, the workflow may enter `Running` from an observation
path when lifecycle and event-stream evidence shows that a run is active. Run
identification, event assignment, completeness checks, and result interpretation
belong to the shared run observation/result interpretation concept, not to the
workflow phase model.

Event-stream reading and run-related event processing are separate workflow
concerns. Reading may be active once the PLC is running and UnitTest
communication is connected. Run-related processing starts only when the workflow
enters an application-triggered run or an explicit observation path for a
PLC-side-started run. Processing continues through completion stabilization and
ends when collected evidence updates the shared log/result model, output
coordination reaches a defined state, or the target context is invalidated.

Run-related event processing requires an observation boundary. Event IDs classify
event meaning, but they are not sufficient to decide whether an event belongs to
the current run. Run membership, stale event handling, ambiguous evidence, and
incomplete evidence are defined by the
[event-stream processing model](400-unittest-eventstream-processing.md).

### Related Model Responsibilities

The workflow model connects these separately owned concerns without duplicating
their detailed definitions:

| Concern | Workflow Anchor |
| --- | --- |
| Run observation and result interpretation | Consumes lifecycle evidence, event-stream evidence, completion stabilization output, and structured log/result update input. |
| Diagnostics and error handling | Consumes workflow state, action gate reasons, active operation descriptors, service diagnostics, recovery path outcomes, and cleanup results. |
| Output and artifact semantics | Consumes structured log/result snapshots, terminal workflow outcome, diagnostics summary, and run setup summary. |
| Shared core implementation design | Places workflow state, action gates, operation coordination, recovery coordination, and update boundaries into testable shared behavior. |
| Config file authoring | Defines the concrete file schema, examples, override rules, and validation diagnostics for the run setup model. |

The [implementation index](implementation/000-index.md) maps these concepts to
code. Shared [runtime](../contracts/access-profile-runtime-contract.md),
[package](../contracts/access-profile-package-contract.md) and
[configuration](../contracts/configuration-file-contract.md) contracts own their
normative boundaries; architecture explanations do not establish alternate APIs.

## Architecture Documents

- `docs/architecture/100-application-layer.md` describes WPF, CLI, and application-layer behavior on top of the shared system workflow.
- `docs/architecture/300-shared-core.md` describes shared core responsibilities, communication interpretation, diagnostics, output/artifact coordination, and testability.
- `docs/architecture/400-unittest-eventstream-processing.md` describes observed-run reconstruction, event assignment, event-stream evidence completeness, and neutral result interpretation.
- `docs/architecture/200-access-profile-layer.md` describes access profiles, plugin loading, functional service boundaries, service context validity, and profile cleanup.
- Concrete access profiles own their architecture and binding documentation below their respective subtrees.

## Contract Reference Rule

PLC-owned contract snapshots under `docs/references/` are grouped by the owning
framework and are read-only references. Each framework reference records its
source revision and synchronization policy. Contract changes are made in the
PLC-owned repository and refreshed here with updated source metadata. Profile
subtrees own their consumer mappings; architecture documents consume the
references but do not redefine PLC-owned contract semantics.
