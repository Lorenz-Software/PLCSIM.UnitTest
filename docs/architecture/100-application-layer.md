# Application Layer Architecture

This document describes how the WPF and CLI application layers translate the
system-wide application responsibilities and workflow into concrete interaction
and execution behavior.

The system-wide application responsibilities and workflow are defined in
`docs/architecture/000-overview.md`.

## Configuration Model

The application layer should use one shared run setup model for WPF and CLI.
WPF creates or updates this model through user selections. CLI creates it from a
configuration file plus a small set of optional command-line overrides. The
shared workflow consumes the resulting run setup and does not need to know
whether it came from WPF selections, a file, or CLI overrides.

The conceptual composition flow is:

```text
WPF user selections
CLI config file + optional overrides
        -> AccessProfileConfiguration
        -> profile loading and target discovery
        -> RunSetup
        -> Workflow preparation
```

WPF reads the same configuration file as CLI. Import fills an editable setup
draft; Load UnitTest advances a complete setup to the operation surface. The
profile projects its typed editable fields without exposing vendor semantics
to the application. This is not a general configuration-file editor.

The CLI should prefer a configuration file for non-trivial setups. Command-line
arguments should stay focused on selecting the config file, selecting execution
mode, overriding a small number of common values, and controlling process-level
behavior. This avoids making the CLI argument model depend on every current or
future access-profile-specific option.

The configuration model should stay minimal and target-oriented. It should
describe what the user wants to run and which target/output should be used. It
should not expose internal workflow strategy, vendor API strategy, retry logic,
binding selection logic, compile/download modes, or low-level communication
tuning unless a concrete user-facing decision cannot be made safely by the
application or access profile.

The [configuration-file contract](../contracts/configuration-file-contract.md)
defines the concrete shape and schema. User authoring belongs in shipped Help
and profile-owned configuration documentation, not a future design task here.

### Run Setup Content

At application-layer level, the run setup should contain these configuration
groups:

| Group | Purpose | WPF Source | CLI Source |
| --- | --- | --- | --- |
| Access profile selection | Selects which access profile should provide project, runtime, lifecycle, and event-stream services. | Manual selection or config file. | Config file, optional common override. |
| Project selection | Identifies the engineering project or project source used for preparation and transfer. | Manual selection or config file. | Config file. |
| PLC target selection | Identifies the intended PLC/CPU target when it cannot be derived unambiguously. | Manual selection or config file. | Config file. |
| Runtime target selection | Identifies the intended simulation/runtime target when the access profile cannot choose a safe default. | Manual selection or config file. | Config file. |
| UnitTest execution mode | Selects application-started run or PLC-side observation. Reset and abort remain runtime actions, not static configuration strategy. | Manual command choice. | CLI command mode; not part of the v1 configuration file contract. |
| Output preferences | Selects output directory and requested artifact categories without defining artifact schemas here. | Application settings or optional manual UI field; not UnitTest configuration file. | Config file, optional output override. |
| Cleanup policy | Defines high-level end-of-session behavior, such as whether the PLC/runtime should be stopped on cleanup when this is not implied by the command/session. | Manual choice or shutdown behavior. | Command policy; not part of the v1 configuration file contract. |
| Operation limits | Defines coarse global timeout/cancellation limits where unattended execution needs deterministic bounds. | Defaults, advanced settings, or config file. | Config file, optional common overrides. |
| Diagnostics preference | Defines coarse diagnostic verbosity or machine-readable reporting preference. | UI settings. | CLI option or command policy; not part of the v1 configuration file contract. |
| Access-profile-specific target configuration | Holds minimal profile-owned target identifiers that the application layer transports but does not interpret as vendor API object models. | Config file or profile-specific selection surface. | Config file. |

The application layer may validate that required groups exist and are internally
well-formed. It should not validate vendor-specific semantics that belong to the
selected access profile.

### Access Profile Configuration Boundary

The run setup should contain a profile-specific configuration section identified
by the selected access profile. The application layer owns selection and
transport of this section. The selected access profile owns interpretation and
validation of its profile-specific values.

For the Siemens access profile, profile-specific configuration should remain
minimal and target-oriented. It should identify the intended engineering project,
PLC target, simulation/runtime target, and only those UnitTest communication
details that cannot be derived safely. Siemens API binding, download, compile,
communication, and recovery strategies belong inside the Siemens access profile
unless they become unavoidable user-facing choices.

### Config File Contract

The shared configuration file contract is defined in
`docs/contracts/configuration-file-contract.md`. Access-profile-specific payloads
are defined by the owning access-profile subtree documentation.

WPF uses application settings for artifact folder selection and always requests
the test result artifact when a result can be produced. WPF does not apply
`output.directory` or `output.testResult` from UnitTest configuration files.

## Layer Responsibilities

### WPF Layer

The WPF layer turns shared workflow state, action availability, diagnostics, and
run evidence into an interactive user workflow.

It owns:

- view state and user interaction state,
- presentation-specific command visibility and placement context,
- manual workflow guidance and feedback,
- configuration editing surfaces,
- local diagnostic and result presentation.

It should not own:

- shared workflow phase/state rules,
- functional service behavior,
- Siemens API object models,
- final run/result semantics,
- output artifact semantics.

WPF command availability must be based on workflow action gates plus explicit UI
placement context only. UI placement context includes concerns such as the active
step or whether a command is visible in the current surface. Functional safety
conditions, lifecycle readiness, PLC state, communication state, monitoring
state, and run-control availability belong to shared workflow gates, not to WPF
status text or badge state.

## Output And Artifact Inventory

Outputs and artifacts are produced from shared-core state and models:

- run setup and output policy,
- run-cycle state,
- action availability and recovery state,
- diagnostics,
- structured UnitTest log/result model,
- output coordination state.

Human-facing WPF and console text are presentation outputs. Machine-readable
artifacts must come from shared models and exporters, not from parsing console
or WPF text.

### Initial Output Set

| Output / Artifact | Producer | Primary consumer | Kind | Purpose |
| --- | --- | --- | --- | --- |
| WPF structured result view | WPF from shared UnitTest log/result model. | Engineer. | Human-facing on-screen output. | Show a process-oriented tree of RunControl, suites, test cases, and assertion findings with status per level. |
| WPF log access | WPF action opening the application log file. | Engineer. | Human-facing diagnostic access. | Provide detailed diagnostics through the system editor rather than duplicate a deep log viewer. |
| CLI console output | CLI from shared workflow state, diagnostics, and result summary. | Engineer / automation operator. | Human-facing console output. | Show progress and final summary; not a machine-readable contract. |
| CLI exit code | CLI from terminal process category. | Scripts / CI/CD. | Machine-readable process result. | Signal success, test failure, configuration/environment/execution/communication error, cancellation, or timeout. |
| Test result artifact | Output exporter from neutral UnitTest result model. | CI/CD / external test-report consumers. | Machine-readable file artifact. | Publish test results in a standard report format. JUnit XML is the first exporter. |
| Application log file | Application/shared logging. | Engineer / support / CI diagnostics. | Human-readable or structured log file. | Record workflow, environment/config highlights, validation, runtime, communication, recovery, output paths, and concise execution summary information. |
| Communication evidence export | Explicit diagnostic exporter. | Engineer / developer troubleshooting contract or communication problems. | Optional diagnostic file artifact. | Preserve selected lifecycle/event-stream evidence when configured; not part of normal result reporting. |

### Ownership Rules

- Shared core owns the semantics of run-cycle state, diagnostics, and UnitTest
  log/result model updates.
- WPF owns on-screen presentation and manual export/open-log actions.
- CLI owns console output, command flow, and process exit-code mapping.
- Exporters own file serialization for concrete artifact formats.
- Access profiles do not own output artifacts. They provide evidence,
  operation outcomes, and diagnostics consumed by shared core.
- CI/CD consumes exit code and file artifacts. It must not depend on parsing
  console text.

### Diagnostic Support State

Diagnostics are application-facing support state. They explain observed
conditions and preserve troubleshooting context, but they are not authoritative
for workflow action gates, UnitTest result interpretation, artifact state,
output history, or CLI exit-code selection.

Applications consume the shared diagnostic snapshot as the current set of
coalesced diagnostic entries. Repeated equivalent emissions should update the
same current entry with last-seen and occurrence metadata. Chronological history
belongs to the application log, terminal-style output, run evidence, or artifact
state depending on the source.

`Clear Diagnostics` clears the shared diagnostic snapshot. It must not change:

- workflow state or action availability,
- active or previous UnitTest run evidence,
- result interpretation,
- output/artifact state,
- terminal output history,
- application log files.

### Test Result Artifact Exporters

The test result artifact is an artifact type, not a fixed internal model.

JUnit XML is the first supported test-result exporter because it is widely
consumed by CI/CD systems. The architecture should allow additional exporters
later, such as other test-report XML formats or a project-specific structured
format, without changing UnitTest result interpretation or WPF/CLI run-cycle
semantics.

The neutral UnitTest result model remains the source for all test-result
exporters. Exporter format selection is an output policy/configuration concern.

### Application Log File

The application log file is the conventional detailed diagnostic output for both
applications.

It should include:

- relevant environment and configuration highlights,
- validation and preparation diagnostics,
- runtime, lifecycle, event-stream, and recovery diagnostics,
- timeout/cancellation information,
- output/artifact paths and writer diagnostics,
- a concise execution summary when a run ends.

The WPF application should provide direct access to the log file. The CLI should
report the log path in its console summary when a log file is produced.

Output writing must not block event-stream intake or PLC-buffer release. When a
log or artifact writer needs longer processing, it consumes a structured
snapshot/model and reports completion or failure through output diagnostics.

## Artifact Location And Naming

Artifact behavior should be deterministic for CLI automation and predictable for
manual WPF troubleshooting. The application layer defines the output policy; the
concrete writer/exporter applies it for each produced file.

### Output Root

The output root is selected by output policy:

- CLI uses the configured output directory or a command default.
- WPF uses a user-selected export/log location or an application default for
  session logs.
- If no file artifact is requested, the application may still keep the
  structured log/result model in memory.

File artifacts for one run should be grouped under one run output directory
when more than one file is produced. The run output directory should be derived
from stable run context, such as timestamp, run name, command mode, or a local
run identifier, according to implementation policy.

### Standard Artifact Names

Result artifacts use predictable names, while explicitly requested analysis
packages have their own application-owned naming:

| Artifact | Suggested default name | Notes |
| --- | --- | --- |
| Test result artifact, JUnit XML exporter | `junit.xml` | Concrete exporter may allow another configured file name. |
| Evidence analysis | Application-owned ZIP name | Optional diagnostic output containing `analysis.md`, ordered `events.ndjson` and `hierarchy.json` when applicable. See the CLI README for per-invocation paths. |

Additional exporters may define their own extension and default file name, but
they should still be selected through output policy and written under the same
run output directory unless the user explicitly configured another path.

### Overwrite And Collision Policy

Default behavior for generated output files is last-run-wins:

- WPF and CLI overwrite generated output artifacts by default.
- There is no automatic `junit-1.xml`, `junit-2.xml`, or similar suffix
  strategy in the default policy.
- Within one output directory, each artifact type should have one primary file
  unless the exporter format requires multiple files.
- Users or CI jobs that need to preserve old artifacts should select a different
  output directory or copy artifacts before the next run.
- Parallel CI jobs must use separate workspaces or output directories. Shared
  use of one local simulation/runtime target by parallel jobs is not supported
  by this workflow model.

Required result output uses deterministic replacement rather than confirmation
prompts or automatic numbered copies. Explicit CLI evidence analysis instead
creates a unique invocation directory as described by the
[CLI command documentation](../../src/PLCSIM.UnitTest.App.Cli/README.md#optional-evidence-analysis).

### Application Logging

Application logging is separate from run output artifacts.

The code should use `Microsoft.Extensions.Logging` abstractions, especially
`ILogger<T>` and `ILoggerFactory`, across WPF, CLI, shared core, access profiles,
and functional services. Concrete logging implementation and file-retention
policy belong to the application composition root.

Serilog is the concrete logging provider. It
should be configured by the WPF and CLI applications to write separate
application logs, for example:

- WPF application log path,
- CLI application log path,
- dedicated access-profile technical log path,
- daily rolling files,
- bounded retention such as five days,
- console logging for CLI where appropriate.

Plugins and shared core should not depend on Serilog APIs directly. The host
application passes `ILoggerFactory` instances and an application-owned
operational logging policy through composition.

The host exposes two independent minimum levels:
`ApplicationLogLevel` for application/shared-core logging and
`AccessProfileLogLevel` for profile-specific technical logging. Both default
to `Information`. Each loaded profile receives a dedicated file identified by
its normalized profile id, for example `access-profile-siemens-<date>.log`.
The profile logger remains host-owned; profiles do not create that sink or own
its retention.

The access-profile policy also contains an opt-in raw-event flag. It is effective
only when `AccessProfileLogLevel` is `Debug`; all other levels force the flag
off. Profile-internal components and external workers inherit the access-profile
level by default. A host or profile-specific composition may supply a narrower
override without exposing worker concepts in general application settings.

The general application log should remain focused on application workflow,
diagnostics, gates, output/artifact state, and bounded operation summaries.
Access-profile logs are the technical evidence trail for profile-specific
environment detection, API binding, communication, callbacks, event-stream
capture, cleanup, and recovery behavior.

WPF should provide an action to open the relevant application log file. CLI
should report relevant log/output paths in its console summary where useful.

## Output And Artifact Semantics

### WPF Human-Facing Outputs

The WPF application should provide these human-facing outputs:

| Output | Purpose | Notes |
| --- | --- | --- |
| Progress / action state | Show current workflow phase, active operation, run-cycle state, action availability, and recovery options. | Derived from shared-core run-cycle and action-availability state. |
| Structured result tree | Show RunControl, suites, test cases, and assertion findings with status per level. | Primary result inspection view for manual debugging. |
| Terminal-style output panel | Show text-oriented workflow progress, phase transitions, operation messages, concise result lines, important diagnostics, and output paths. | Similar in spirit to CLI output, but human-facing and not a machine-readable contract. |
| Open application log file | Open the current WPF application log for detailed diagnostics. | The application log is managed by logging configuration. |
| Open test result artifact | Open the produced test result artifact when one was written successfully. | Uses the currently configured exporter output, such as `junit.xml`, not a hard-coded format assumption. |
| Test result artifact preview | Show the content of the produced test-result artifact after it was written successfully. | The preview reads the generated artifact file. It must not render a second independently generated artifact model. |

The WPF layer shows actionable status and diagnostics in context and
provides log-file access for details.

### CLI Human-Facing Outputs

The CLI should provide human-facing console output:

- progress and phase messages,
- concise final result summary,
- important diagnostics,
- produced artifact paths,
- application log path where useful.

Console output is not a machine-readable interface. Scripts and CI/CD should use
exit codes and file artifacts.

### Test Result Artifact

The test result artifact is the machine-readable test-report output.

- Source: neutral UnitTest result model.
- First exporter: JUnit XML.
- Default file name for the first exporter: `junit.xml`.
- Default write policy: overwrite existing generated output file.
- Consumer: CI/CD and external test-report tooling.

The exporter format must be replaceable or extendable later. WPF, CLI, shared
core, and result interpretation must not depend on JUnit XML as the internal
result model.

WPF should provide an action to open the produced test result artifact when it
exists. CLI should report the artifact path in its final console output when the
artifact was requested.

The WPF artifact preview is an application-internal view of the produced file.
It is populated by reading the generated artifact
after a successful write. The output panel shows artifact coordination status,
format, state, and paths, but not the artifact payload itself.

### CI/CD Test Report Semantics

The CI/CD test report artifact represents interpreted test results. It is not
the authoritative process outcome for the CLI command and it is not a complete
workflow diagnostic report.

CI/CD integration should use two independent signals:

- CLI process exit code for command/job success or failure.
- Test result artifact for detailed test-result display and comparison.

The first CI/CD test report exporter is JUnit XML. The exporter reads the
neutral UnitTest result model and writes only meaningful test-result data. It
must not parse console output and must not invent test cases to satisfy a
specific CI UI.

JUnit result mapping for concrete test cases is:

| Internal result | JUnit representation | Notes |
| --- | --- | --- |
| Passed | Successful `testcase` without `failure`, `error`, or `skipped`. | Assertion findings with failed/error severity must prevent a passed final testcase result. |
| Failed | `failure` on the owning `testcase`. | Used for expected UnitTest assertion/test failures. |
| Error | `error` on the owning `testcase`. | Used for unexpected testcase execution problems. |
| NotEvaluated | `error` on the owning `testcase`. | Indicates no reliable successful testcase result exists. |
| Incomplete | `error` on the owning `testcase`. | Indicates an unfinished or incomplete testcase result. |
| Ambiguous | `error` on the owning `testcase`. | Indicates conflicting evidence. |

Skipped test reporting is not inferred. If PLC-side configurable skipping is
added later, it should introduce an explicit PLC/internal result state and a
separate exporter mapping to `skipped`.

Failures before the RunControl `Starting` event anchor has opened a run epoch
are workflow errors, not test results. In this case, the CLI reports a non-zero
exit code and diagnostics through console output and application logs. The JUnit artifact is
not written when no meaningful test-result model exists.

Run-level or suite-level errors after the run epoch boundary remain workflow or
aggregate result problems unless they can be assigned to concrete test cases.
They fail the CLI command through its exit-code mapping and are recorded in
diagnostics/log output. The JUnit exporter writes the concrete testcase results
that exist. It does not create synthetic testcases for run-level or suite-level
errors by default.

If a future CI target has a proven compatibility requirement that cannot display
important run-level errors without testcase elements, that behavior should be an
explicit exporter compatibility mode, not the default result semantics.

### Communication Evidence Export

Communication evidence export is optional and diagnostic-only.

It may contain selected normalized lifecycle and event-stream evidence useful
for contract, access-profile, or communication troubleshooting. It is not the
primary result artifact and is not produced by default.

Writing communication evidence must not block PLC-buffer intake or release. The
exporter should consume already captured PC-side evidence or snapshots.

### Information Without A Separate Standard Artifact

The application does not create these as separate standard artifacts:

- execution summary artifact,
- validation report artifact,
- environment snapshot artifact,
- planned run configuration snapshot artifact,
- separate WPF diagnostics artifact.

Relevant information from these areas can be written to the application log,
terminal-style output, CLI console summary or explicitly requested diagnostic
analysis packages. These remain separate from the required test-result artifact.

### Output Coordination State

Each requested artifact should report an output state:

- `NotRequested`
- `Pending`
- `Written`
- `Skipped`
- `Failed`

The state should include the target path when known and diagnostics when writing
was skipped or failed. CLI can use this state for final summary and process
outcome mapping. WPF can use it to show export status and open produced files.

### WPF Manual Workflow Design

The WPF layer is a manual workflow shell around the shared workflow state object.
It does not calculate phase transitions or functional action availability itself.
It binds to the shared workflow model and turns that model into visible status,
commands, diagnostics, and evidence views.

The WPF layer should consume these workflow objects:

| Workflow Object | WPF Use |
| --- | --- |
| Workflow status snapshot | Primary status, current step context, resource readiness, PLC/communication/monitoring/run-control/run-observation status, and active operation context. |
| Action gate results | Command availability, blocked/unsafe explanations, and next-action guidance. |
| Diagnostics summary/details | User-facing diagnostics without vendor API object exposure. |
| Run evidence/log update status | Result/review availability and cleanup protection. |

The workflow status snapshot should stay focused and presentation-neutral. It is
not a container for diagnostic details, output text, run tree details, artifact
payloads, UI strings, colors, animations, or access-profile internals. If a
status detail does not belong in the focused snapshot, it should remain in a
separate stable workflow publication field, diagnostics/output, or run evidence
rather than being interpreted ad hoc by WPF.

The WPF layer should expose the workflow as stepwise commands. Each command maps
to one workflow action identifier and is enabled, blocked, pending, unsafe, or
not applicable through the action gate result plus explicit UI placement context.
The WPF layer may group commands for usability, but grouped commands still
execute the same workflow actions in order and stop at the first blocked, unsafe,
failed, or cancelled operation.

The primary WPF workflow should be optimized for a small set of meaningful user
actions. Validation and fast dependent steps are workflow gates behind those
actions; they are visible through status and diagnostics, but they are not
mandatory separate user commands in the normal flow.

| Order | Primary User Action | Internal Workflow Composition | User-Visible Result |
| --- | --- | --- | --- |
| 1 | Select configuration. | Capture the user's run selections, such as access profile choice, project selection, runtime target options, UnitTest options, and output preferences. | The selected run setup is visible, incomplete selections are highlighted, and dependent actions are gated. |
| 2 | Prepare. | Bind and validate the selected access profile, validate setup, open/prepare project, provision simulation PLC, and transfer project as one composed workflow. | The simulation PLC is prepared with the transferred project but not necessarily running, or the workflow stops at the failing gate with diagnostics and recovery guidance. |
| 3 | Start PLC. | Start the simulation PLC and connect UnitTest lifecycle/event-stream communication after the PLC is running. | The PLC is running and UnitTest communication is connected, or startup/communication diagnostics are shown. |
| 4 | Start. | Validate readiness, request lifecycle start, and begin event-stream observation for the UnitTest run. | The run becomes active, completes with result evidence, is rejected, or start diagnostics are shown. |
| 5 | Abort. | Request lifecycle abort for the active run and continue observing until a defined abort outcome is reached. | The run is aborted or abort diagnostics are shown. |
| 6 | Reset. | Request lifecycle reset and return to a reusable UnitTest communication state. | The target is ready for another run or reset diagnostics are shown. |
| 7 | Stop PLC. | Stop the simulation PLC when the runtime is prepared and has a PLC configuration loaded, then update communication/context trust accordingly. | The PLC is stopped and communication monitoring is no longer active, or stop diagnostics are shown. |
| 8 | Cleanup. | Release workflow/profile/service resources according to action gates. | The application returns to a known idle/reusable state. |

Cleanup is both a user-triggered action and an application shutdown action. When
the WPF application closes, it triggers the same coordinated release used by End
Session. The application first blocks new UI actions and requests cancellation of
its active UI operation. Shared Core independently blocks new workflow operations,
cancels and boundedly awaits its active operation, cleans the workflow context,
and unloads the profile session. Application service-provider and logging disposal
happen only after this resource release has been attempted.

The WPF layer does not reproduce profile, binding, or worker cleanup. It presents
release progress and diagnostics while `RunSessionReleaseService`, profile
ownership, and binding-local worker ownership execute their respective phases.
Stepper Back releases only the context invalidated by that navigation; End Session
and application exit release the complete workflow/profile session.

The WPF layer should derive command availability from workflow action gates. The
default command availability is:

The command availability below is defined for the current target design with one
primary prepared PLC/runtime target per workflow run. Future multi-PLC scenarios
should extend the target model rather than reinterpret these commands as global
singletons.

| WPF Command | Available When | Blocked/Pending/Unsafe When |
| --- | --- | --- |
| Select configuration | No workflow operation is pending, or the current context has been cleaned up/released. | Pending while prepare, start PLC, start, abort, reset, stop PLC, or cleanup is running. Unsafe while a run is active or unsaved run evidence has not reached a defined log/result update or cleanup boundary unless the user enters a defined cleanup/reconfiguration path. |
| Prepare | A complete run setup has been selected and no active run is pending. | Blocked by incomplete selections, unavailable access profile/environment, or unavailable project/runtime prerequisites. Unsafe while a run is active, while PLC is running with trusted communication, or while cleanup is pending. |
| Start PLC | Prepare completed successfully and the simulation PLC has a transferred project state. | Blocked before prepare/transfer. Pending while prepare or stop PLC is running. Unsafe if the runtime context is unknown or cleanup is active. |
| Start | PLC is running, UnitTest lifecycle communication is connected, event-stream reading is active, RunControl ready evidence is positive, and no conflicting command is pending. | Blocked before Start PLC/communication connection. Pending while start/abort/reset is running. Unsafe while completion evidence is still settling or context trust is unresolved. The command does not create an epoch; the RunControl `Starting` event anchor does. |
| Abort | A UnitTest run is starting or running and lifecycle communication is trusted. | Not applicable when no run is active. Blocked when lifecycle communication is unavailable. Unsafe when the target context is unknown or cleanup is active. |
| Reset | Runtime/PLC is running and lifecycle communication is reachable enough to request reset. | Blocked only when runtime/PLC is not running, lifecycle command access is unavailable, or cleanup/profile unload owns the context. Reset during an active or finishing run is a recovery/interruption action and prevents a clean completed result for that run. |
| Stop PLC | The runtime is prepared and has a PLC configuration loaded, regardless of whether the PLC is currently running a UnitTest workflow. | Pending while stop PLC is already running. Stopping during an active run or pending lifecycle operation is handled as a workflow interruption/recovery case and must update lifecycle, event-stream, and context-trust state accordingly. |
| Cleanup | No cleanup operation is already running. | Pending while cleanup is running. Unsafe when an active operation must first be cancelled/aborted or when cleanup would discard evidence that has not reached a defined log/result update or diagnostic boundary. |

The WPF layer may offer explicit validation and single-step commands in an
advanced/manual validation mode. Those commands are diagnostic tools for
development and troubleshooting, not the default user path.

| Advanced Action | Internal Workflow Action |
| --- | --- |
| Validate selected setup. | Bind and validate the selected access profile, validate service bindings, and validate required external environment without continuing into project preparation or runtime provisioning. |
| Open project. | Open or attach the engineering project through the project service. |
| Build/prepare project. | Compile/check/prepare the project state required for transfer. |
| Create or attach simulation PLC. | Resolve, create, or attach the configured runtime target. |
| Download project. | Transfer the prepared project state to the selected runtime target. |
| Start simulation PLC. | Start the runtime target after transfer. |
| Stop simulation PLC. | Stop the runtime target when this is safe for the current workflow state. |
| Connect UnitTest communication. | Validate lifecycle and event-stream communication against the running target. |
| Reconnect runtime. | Replace technical runtime and UnitTest communication resources while retaining the prepared runtime target and setup. |
| Reload access profile. | Rebuild service bindings through the access profile boundary. |

For observing a PLC-side-started run, the user executes the same preparation
actions through `Start PLC`, then chooses to observe instead of starting a run.
Monitoring, completion stabilization, and result/log updates are automatic
consequences of the observation action.

UnitTest event-stream reading starts only after the PLC is running and UnitTest
communication has been connected. It must be active before an application-started
UnitTest run is requested, so early run events are not missed. Event-stream
reading stops during cleanup, when the PLC is stopped, or when the workflow
marks the current target context as no longer trusted.

Run-related event-stream processing starts with `Start` for an
application-triggered run or with `Observe PLC-side run` for an externally
started run. Reading may already be active after `Start PLC`, but events are not
assigned to a run/result context until the workflow enters run start or
observation. Processing continues through completion stabilization and ends when
the collected evidence has reached a defined log/result update or output
coordination boundary, or when the workflow invalidates the target context.

Event IDs are not sufficient for assigning events to the current run. WPF shows
stale, ambiguous, or incomplete event evidence as diagnostics supplied by shared
run observation/result interpretation behavior; it does not decide final run
membership in the application layer.

Additional actions support observation and recovery:

| User Action | Available During | Workflow Meaning |
| --- | --- | --- |
| Observe PLC-side run | Prepared and communication-connected target. | Start observation without requesting lifecycle start. |
| Reconnect runtime | Prepared target retained while runtime or UnitTest communication access is stale, invalid, or intentionally refreshed. | Replace technical runtime and communication resources without discovery, project handling, reprovisioning, target release, or PLC control. |
| Reload access profile | Recoverable failure or invalid profile/service context. | Rebuild service bindings through the access profile boundary. |

Manual real-runtime validation is represented by explicit stop points between
major workflow phases. The user can inspect phase state, context trust, and
diagnostics after validation, project preparation, runtime provisioning, project
transfer, runtime startup, communication validation, run start/observation,
completion stabilization, and cleanup.

The WPF layer should never discard run evidence directly. Cleanup commands are
routed through the workflow so that cleanup can be blocked or marked unsafe until
the available evidence has reached a defined log/result update, diagnostic, or
output coordination boundary.

Shortcuts for common paths are allowed as UI-level command compositions. They do
not bypass workflow validation, action gates, recovery boundaries, or functional
service ownership.

WPF recovery behavior should stay user-directed where a decision can affect the
real runtime state. If PLC stop, abort, reset, profile reload, or cleanup would
change a running/prepared target, WPF should present the action as an explicit
available command or shutdown decision instead of silently changing the target,
except where the user already triggered cleanup or application shutdown.

### CLI Layer

The CLI layer turns the same shared workflow into unattended execution behavior.

It owns:

- command configuration and validation entry points,
- non-interactive execution policy,
- cancellation and timeout policy as configured by the command,
- process outcome mapping,
- machine-readable command feedback.

It should not own:

- shared workflow phase/state rules,
- functional service behavior,
- Siemens API object models,
- final run/result semantics,
- output artifact schemas.

### CLI Unattended Workflow Design

The CLI layer is an unattended workflow driver around the same shared workflow
state object used by WPF. It does not implement a separate execution model. A CLI
command creates a workflow execution request, provides command policy, executes
workflow actions in a deterministic sequence, and maps the final workflow/result
state to process feedback.

The CLI layer should provide these inputs to the shared workflow:

| CLI Input | Workflow Use |
| --- | --- |
| Command configuration | Selects configuration source, access profile, project input, runtime target, execution mode, and output targets. |
| Execution policy | Defines whether the command should validate only, prepare only, execute a full run, observe an already running PLC-side run, or perform cleanup/recovery. |
| Timeout/cancellation policy | Provides maximum durations and cancellation behavior for long-running workflow operations. |
| Unsafe-action policy | Defines whether unsafe actions fail immediately or can be executed only through an explicit command mode. |
| Output policy | Selects requested output/artifact categories without defining their schemas in the CLI layer. |

The CLI command should consume these workflow outputs:

| Workflow Output | CLI Use |
| --- | --- |
| Workflow phase/state | Progress reporting and final command state. |
| Action gate results | Early failure when a required action is blocked, unsafe, or not applicable for the requested command mode. |
| Active operation descriptor | Progress, timeout handling, and cancellation handling. |
| Diagnostics summary/details | Human-readable and machine-readable command diagnostics. |
| Result/output update status | Determines whether requested artifacts can be generated and whether command execution is complete. |
| Terminal workflow outcome | Process exit category and final command summary. |

The default full-run command sequence should be:

1. Resolve command run setup.
2. Bind and validate environment and selected access profile.
3. Prepare the project.
4. Provision the runtime target.
5. Transfer the project.
6. Start the runtime.
7. Validate UnitTest communication.
8. Start or observe the unit test run according to command mode.
9. Monitor execution.
10. Stabilize completion.
11. Hand over evidence for result interpretation and outputs.
12. Cleanup according to command policy.

The CLI layer should stop at the first blocked, unsafe, failed, cancelled, or
timed-out workflow action unless the selected command mode explicitly describes
a recovery or cleanup path. Recovery command modes still use workflow action
gates and do not call functional services directly.

At command end, CLI should always request workflow cleanup according to command
policy. The default command policy should release communication/profile
resources and preserve requested output artifacts. Stopping the PLC/runtime is a
command-policy decision, not an unconditional side effect, so diagnostic and
CI/CD runs can choose whether the prepared simulation state should be left
available or released.

The CLI layer should map terminal outcomes to stable positive process exit
codes instead of exposing raw service exceptions. CI/CD systems primarily depend
on `0` versus non-zero. The differentiated codes are for scripts, diagnostics,
logs, and later support tooling.

The CLI-owned [exit-code table](../../src/PLCSIM.UnitTest.App.Cli/README.md#exit-codes)
defines numeric values. Keep that mapping in one place rather than duplicating
it in global architecture documentation. Categories distinguish test failures,
configuration/environment failures, execution/communication failures, timeout,
cancellation, artifact failure, cleanup failure and unexpected internal errors.

`CliOutcomeResolver` owns precedence when several outcomes coexist. In
particular, cleanup failure takes precedence over the earlier run outcome;
artifact or test results must not hide failed resource release.

Communication failures are reported through the closest process category:

- environment error when required communication services or bindings are not
  available at setup time,
- workflow error when lifecycle/event-stream communication fails during the run
  and prevents trusted completion,
- timeout when the configured communication/run observation limit expires.

The CLI should always print a concise final summary with the process category,
exit code, important diagnostics, and produced artifact/log paths where known.
Machine consumers should use the process exit code and file artifacts, not parse
the console text.

CI/CD integration is handled primarily through stable diagnostics, terminal
outcome categories, and output/artifact coordination. CI/CD job templates and
concrete artifact schemas are defined separately.

Structured diagnostic DTOs are internal application/service data, not a promise
of machine-readable console output. CI integrations consume the documented
process exit codes and the separately documented JUnit/analysis file formats;
stdout/stderr wording is not a parsing contract.

## Application Workflow And Orchestration

The system workflow is defined in `docs/architecture/000-overview.md`. The
application layer uses that workflow differently for WPF and CLI:

- WPF presents workflow phases, state, action availability, and diagnostics as a
  manual, observable user workflow.
- CLI executes the same workflow without interaction and maps workflow state,
  action availability, cancellation, and failures to deterministic process
  behavior.

The application layer should not duplicate functional service behavior or vendor
API details. It consumes shared workflow state and service results from the
shared core and translates them into application-specific interaction and
execution behavior.
