# Application Implementation

This document maps the current implementation of the WPF and CLI
application layers. It complements the application architecture in
`docs/architecture/100-application-layer.md` and describes how the applications
compose and consume shared core behavior.

It does not define WPF layout, CLI command syntax, or Siemens API behavior.

## Shared Application Boundary

WPF and CLI are separate applications over the same shared core behavior.

Both applications use `en-US` as the fixed application locale. Runtime language
switching is not supported.
Formatting, parsing, diagnostics text, console output, and UI text should be
implemented consistently with that fixed locale unless a later design decision
introduces localization explicitly.

Both applications:

- own their composition root,
- configure logging,
- create/load access-profile configuration and final `RunSetup`,
- call `RunWorkflowService` methods,
- consume `RunWorkflowPublication`,
- consume diagnostic snapshots and output state,
- trigger cleanup according to application policy.

Both applications must not:

- call Siemens API objects directly,
- call functional access services directly to bypass workflow state,
- interpret UnitTest event-stream semantics,
- decide final UnitTest result meaning,
- serialize artifact formats directly,
- derive command availability from diagnostic message text.

`WorkflowAction` remains the shared action identity for action gates,
diagnostics, active operation state, logging, and tests. It is not the primary
application API. WPF and CLI call concrete `RunWorkflowService` methods.

## Shared Composition

Both applications should use normal .NET dependency injection from their
composition root.

Shared registrations include:

```csharp
services.AddLogging(...);
services.AddSingleton(TimeProvider.System);

services.AddSingleton<DiagnosticSink>();
services.AddSingleton<DiagnosticServices>();
services.AddSingleton<CoreServiceContext>();

services.AddSingleton<AccessProfilePluginLoader>();
services.AddSingleton<AccessProfileSessionService>();
services.AddSingleton<AccessProfileCapabilityValidator>();
services.AddSingleton<IApplicationProfileRegistryStore, FileApplicationProfileRegistryStore>();
services.AddSingleton<IApplicationProfileRegistrationService, ApplicationProfileRegistrationService>();
services.AddSingleton<IApplicationProfileSelectionService, ApplicationProfileSelectionService>();

services.AddSingleton<RunWorkflowService>();
services.AddSingleton<ArtifactExportService>();

services.AddSingleton<IArtifactExporter, JUnitArtifactExporter>();
```

`DiagnosticSink` is application-owned. Each application run should compose one
sink instance. Shared Core owns the sink implementation; the application owns
the instance lifetime.

Applications expose the sink through `DiagnosticServices`. `DiagnosticServices`
is a role-provider backed by the same application-owned sink. Core diagnostic
producers use the writer role, presentation code uses the reader role, and
application commands that clear diagnostics use the controller role. The
role-provider must not create independent sink instances.

Shared Core services receive application-owned infrastructure through
`CoreServiceContext`. The context contains only shared infrastructure such as
`ILoggerFactory`, `TimeProvider`, and `DiagnosticServices`. It must not carry
workflow state, run setup, PLC/runtime references, WPF services, or application
commands.

Applications bind artifact export to workflow publication in the composition
root:

```csharp
workflow.PublicationEventHandler += artifactExports.OnWorkflowPublication;
```

After this binding, the export service observes workflow publications itself and
publishes `OutputStateChangedEventHandler` events for presentation. View models
and command handlers do not mediate automatic export triggering.

The concrete lifetime may differ:

- WPF may keep one `RunWorkflowService` for the interactive application session.
- CLI normally creates one workflow instance per command execution.

### File-System Ownership

Application and profile package trees are immutable deployment inputs. WPF and
CLI discover packages below their configured discovery root but do not create a
missing root or write mutable state into it.

The default per-user application data tree is:

```text
%LOCALAPPDATA%\PLCSIM.UnitTest\
  application-profile-registry.json
  profile-data\
    <isolated-profile-candidate>\
  logs\
  wpf-settings.json
  artifacts\
```

The application registry is shared application state. Each directory below
`profile-data` is private to one profile candidate and is supplied through
`AccessProfileContext`; profiles must not derive this location from their
package root. `wpf-settings.json` and the default `artifacts` directory are WPF
owned. CLI result artifacts continue to follow explicit command/configuration
paths and the caller's working-directory semantics rather than the WPF default.

## WPF Application

### Composition Root

The WPF application composes:

- shared core services,
- exporter registrations,
- WPF view models,
- WPF application settings,
- WPF logging configuration,
- file/dialog/open-file helpers where needed.

During startup WPF ensures the application-owned log directory and an empty
valid access-profile capability registry shape before the first profile list is
read. The default artifact output path is
`%LOCALAPPDATA%\PLCSIM.UnitTest\artifacts`; the exporter creates it when the
first artifact is written. A configured profile-discovery directory remains a
read-only deployment input and is never created by WPF composition.

WPF does not register Siemens technology bindings directly. Siemens-specific
composition remains inside the selected access profile plugin.

The WPF shell discovers profiles through directory packages below its configured
profile discovery directory. A normal Debug application build does not deploy
profile packages. Use the explicit internal deployment script after building
the required profiles/bindings; the manual workbench uses the same package
discovery and load boundary as deployed applications.
WPF depends on the generic access-profile contracts and `RunWorkflowService`,
not on profile-specific functional services.

WPF populates the access-profile selection from the capability registry, not
from live capability discovery. The user-facing refresh action runs capability
registration explicitly, updates the registry, and then reloads the selection
from registry state. Startup must not perform expensive profile environment
checks.

### Setup Creation

WPF creates the final `RunSetup` from user selections and, where supported,
config files. Access-profile loading and discovery can happen before the final
setup exists.

The WPF layer may validate UI-level completeness before calling workflow methods,
but shared workflow and access-profile validation remain owned by shared core and
the selected profile.

Typical flow:

```text
Project/profile selection or imported config values
  -> registered profile selection from capability registry
  -> AccessProfileConfig plus WPF-selected ProjectOwnershipMode
  -> setup-only AccessProfileSessionRequest
  -> AccessProfileSessionService.LoadAsync(sessionRequest)
  -> RunWorkflowService.BindAccessProfileSessionAsync(session)
  -> RunWorkflowService.DiscoverProjectTargetsAsync(accessProfileConfig, ...) only for ApplicationManaged ownership
  -> RunWorkflowService.DiscoverRuntimeTargetsAsync(accessProfileConfig, ...)
  -> final user selections / imported values
   -> RunSetup with selected provisioning mode (UseExisting or CreateOwned)
      and a mode-appropriate runtime target
  -> RunWorkflowService.Configure(runSetup)
```

Manual WPF setup initializes project ownership from the persisted `Build and
download project by default` application setting. The product default is
`ExternallyManaged`; the Step 1 checkbox may override it for the current session.
Imported drafts use the same application default, while `Load UnitTest...`
always selects `ApplicationManaged`. Project ownership is not read from or
written to the UnitTest configuration file.

Manual WPF setup does not preselect PLC or runtime targets. The user selects the
discovered project target and runtime target explicitly. Imported configuration
values may preselect matching target references, and `Load UnitTest...` may
perform the same progression automatically when the complete setup validates.
The application owns preselection policy; access profiles publish neutral
candidate references and may designate a default for imported `UseExisting`
intent. This does not automatically preselect a target in manual setup.

Project/source to access-profile mapping is intentionally shallow in the WPF
layer. The UI may prefill the profile id from project/source metadata where this
is available, but the selected profile owns interpretation of its profile values
and any lower-level technology binding decisions. The WPF shell populates access
profile choices from the capability registry. Capability registration uses
profile discovery internally, but normal selection reads persisted registration
state. The Fake profile is a development profile candidate, not a WPF-only
shortcut.

After the shell has loaded, WPF performs one side-effect-free registration
currentness check. A non-current result leaves the workflow available but shows
an inline Step 1 notice with an explicit registration action. The action invokes
the existing registration refresh and reloads the profile list after a
successful refresh. WPF does not load profile implementations or invoke vendor
registration merely to check startup state.

Release packaging and Windows installation are implemented separately from
Debug deployment. Their deployment inputs, compatibility checks and installation
ownership are defined in [release package manifests](../../deployment/release-package-manifests.md)
and [Windows installation](../../deployment/windows-installation.md).

### Workflow Method Composition

WPF composes meaningful user actions from concrete `RunWorkflowService` methods.

Primary WPF command mapping:

```text
Step 1 Next
  -> AccessProfileSessionService.LoadAsync with selected ProjectOwnershipMode
  -> BindAccessProfileSessionAsync
  -> DiscoverProjectTargetsAsync only for ApplicationManaged ownership
  -> DiscoverRuntimeTargetsAsync

Step 2 Next
  -> validate selected PLC/runtime targets
  -> load the profile configuration projection

Step 3 Next
  -> apply current profile payload snapshot
  -> run generic field checks and profile ValidateConfig
  -> Configure
  -> ApplicationManaged: PrepareProjectAsync
  -> PrepareRuntimeAsync
  -> ApplicationManaged: TransferProjectAsync and release live project access
  -> ExternallyManaged: accept external handling without project operations

Observed PLC ReadyForExecution
  -> PrepareUnitTestCommunicationAsync
  -> StartMonitoringAsync

Start PLC
  -> StartPlcAsync
  -> monitor the independently published PLC state reaction

Start
  -> StartUnitTestAsync

Abort
  -> AbortUnitTestAsync

Reset
  -> ResetUnitTestAsync

Stop PLC
  -> StopPlcAsync

Return to Step 2
  -> ReleaseUnitTestCommunicationAsync where needed
  -> ReleaseRuntimeAsync where needed

Return to Step 3
  -> ReleaseUnitTestCommunicationAsync where needed
  -> ReleaseRuntimeAsync where needed
  -> reproject the current application-owned profile configuration

Return to Step 1
  -> ReleaseUnitTestCommunicationAsync where needed
  -> ReleaseRuntimeAsync where needed
  -> ReleaseProjectAsync where needed
  -> UnloadAccessProfileAsync

End Session / Cleanup
  -> CleanupAsync
```

WPF may expose advanced/manual commands that call single workflow methods for
diagnostics and real-runtime validation. These commands still use action gates
and do not call functional services directly.

### Publication Consumption

WPF subscribes to `RunWorkflowService.PublicationEventHandler` and binds the
latest `RunWorkflowPublication` to view models.

WPF uses publication data for:

- workflow state and `WorkflowStatusSnapshot`,
- active operation state,
- action gate display and command enablement,
- current `UnitTestRunEpochSnapshot`,
- diagnostic snapshot entries,
- output/artifact state.

WPF projects the horizontal process badges from `WorkflowStatusSnapshot` into
WPF-local structured process status view states. These view states carry stable
status identity, display text, marker styling, and activity animation data.
Display text and marker styling are presentation outputs only. WPF command
availability is based on published `ActionGateState` values plus explicit UI
placement context, not on status display strings or badge colors.

The structured process status view states are limited to the horizontal process
badges: Runtime Setup, PLC process, UnitTest communication, Event Stream
Monitor, and Run Control. Footer/session fields, runtime setup detail fields,
diagnostics, artifact state, run result, run tree, run status, and step messages
remain separate view-model projections, not additional behavioral state models.

`MainWindowViewModel` remains the WPF command-entry and application-state
orchestration surface. It may delegate pure presentation projection to
WPF-local helpers for status badges, diagnostics list state, terminal-style
output text, artifact preview state, configuration-draft presentation, and
run-result tree rows/selection. These helpers must not own workflow semantics,
action-gate decisions, diagnostics truth, artifact serialization, or UnitTest
result interpretation. They consume shared workflow publications, diagnostic
snapshots, output state, configuration models, and stable run snapshots and
return WPF view state.

WPF consumes artifact status from `ArtifactExportService.OutputStateChangedEventHandler`.
The application composition root binds `ArtifactExportService.OnWorkflowPublication`
as an additional handler to `RunWorkflowService.PublicationEventHandler`. The
workflow publishes `WorkflowStatusSnapshot.IsExportable` and a stable run epoch
snapshot; the export service observes the `IsExportable: false -> true`
transition and performs the export. On successful export the WPF application
loads the written artifact content into the artifact preview. Export output and
diagnostics are still shared-core model types, but export execution lifetime
remains application-owned.

WPF should not subscribe directly to `IUnitTestLifecycleService`,
`IUnitTestEventStreamService`, `EventStreamProcessor`, or `TestRunMonitor`
unless implementation later proves a narrow internal view-model optimization is
required. The normal application boundary is workflow publication.

### Diagnostics, Logs, And Artifacts

WPF presents actionable diagnostics from the application-owned shared
diagnostic sink. Workflow-origin, export-origin, and WPF/application-level
diagnostics such as configuration-load, application-log, and artifact-access
problems are published into the same sink. WPF subscribes to diagnostic snapshot
changes and renders the current snapshot; it does not forward producer-specific
diagnostic events into the sink. The Diagnostics tab is diagnostic help for the
user, not an event log and not an authority for workflow state, action gates, run
evidence, or UnitTest result interpretation. Repeated emissions of the same
structured finding remain one visible entry with occurrence metadata from the
shared diagnostic sink.

WPF should provide:

- structured process/result view from `UnitTestRunEpochSnapshot`,
- terminal-style output from workflow publication and diagnostics,
- action to open the application log file,
- action to open the produced test result artifact when available,
- action to open the configured artifact output directory.

The application log file is configured by WPF logging setup. WPF does not parse
the log file to decide command availability or result meaning.

Clear Diagnostics clears the shared diagnostic sink. It removes current
workflow-origin and application-origin diagnostic entries from diagnostic
snapshots, but it does not change workflow state, action gates, run evidence,
UnitTest result interpretation, output history, or application logs.

### WPF Operation Cancellation

WPF owns one active cancellable-operation controller for long-running
registration refresh, profile/target discovery, runtime setup, and automated
`Load UnitTest...` progression. It creates the caller token, shows the operation
through the existing activity status, and exposes one status-bar command with
tooltip `Cancel current operation`.

The first cancellation request disables the cancel command and changes the
activity text to `Cancelling operation...`. WPF remains busy until the profile,
binding, and any worker request have reached their recovery boundary. It does not
report cancellation merely because the token was signalled.

PLC commands, UnitTest commands, active test-run observation, and owner cleanup
do not expose infrastructure cancellation. UnitTest Abort remains a domain
command and is not a substitute for cancelling application setup.

Cancellation keeps the current step. If the same setup remains valid and the
profile reports usable health, the existing forward action may resume the
partial setup. Otherwise functional controls remain gate-disabled while Back
and End Session provide the recovery path. WPF does not infer retryability from
the fact that the user requested cancellation.

### Shutdown And Cleanup

WPF closes directly from Step 1. Closing from Steps 2, 3, or 4, including the
Exit menu and `Alt+F4`, first asks `End Session and Exit?`. Cancel keeps the
application open. Confirmation first requests cancellation of an active
cancellable operation and waits for its recovery boundary. It then disables the
window, awaits the shared workflow/session cleanup path, and completes
application shutdown. Cleanup failures are logged but must not trap an already
confirmed exit.

WPF should request `CleanupAsync` during normal application shutdown when
workflow/profile/service resources are active.

Cleanup is workflow-owned. WPF presents cleanup progress, unsafe conditions, and
remaining diagnostics but does not release Siemens or communication resources
directly.

## CLI Application

### Composition Root

The CLI application composes:

- shared core services,
- exporter registrations,
- config file loading,
- command-line option mapping,
- CLI logging configuration,
- console output writer,
- exit-code mapper.

CLI does not register Siemens technology bindings directly. Siemens-specific
composition remains inside the selected access profile plugin.

### Setup Creation

CLI creates access-profile configuration and the final `RunSetup` from a config
file plus a small set of command-line overrides.

Typical flow:

```text
CLI options + config file
  -> common configuration check
  -> side-effect-free profile-registration currentness check
  -> persisted profile-registry match
  -> setup-only AccessProfileSessionRequest with ApplicationManaged ownership
  -> AccessProfileSessionService.LoadAsync(sessionRequest)
  -> RunWorkflowService.BindAccessProfileSessionAsync(session)
  -> profile configuration load
  -> project and runtime discovery
  -> configured project-target resolution
  -> shared runtime-target resolution for configured intent (omission: CreateOwned)
  -> profile validation and RunSetup with resolved provisioning mode/target
  -> RunWorkflowService.Configure(runSetup)
```

CLI maps invalid command-line input or config loading failure to configuration
error before starting workflow execution. For `check-config` and `run`, any
registration state other than current maps to profile-unavailable exit code
`20`; these commands never invoke registration as an implicit recovery. The
separate `register-profiles` command remains the explicit registration-only
operation.

### Full Run Execution

The CLI executes a fixed full-run method sequence. There is no generic
user-configurable workflow sequence.

Default full-run sequence:

```text
configuration check and registered-profile resolution
AccessProfileSessionService.LoadAsync
BindAccessProfileSessionAsync
load profile configuration
DiscoverProjectTargetsAsync
DiscoverRuntimeTargetsAsync
resolve configured project/runtime targets and validate provisioning intent
Configure
PrepareProjectAsync
PrepareRuntimeAsync
TransferProjectAsync
StartPlcAsync when the PLC is not already running
wait for independently observed Running state and active UnitTest communication
wait for current Idle + Ready using the CLI readiness deadline; no initial Reset
optionally request one Reset after readiness timeout, then wait for ResetReady
StartUnitTestAsync
wait for exportable terminal run evidence
export required JUnit artifact
RunSessionReleaseService coordinated cleanup
```

CLI stops at the first blocked, unsafe, failed, cancelled, or timed-out workflow
method unless the command policy explicitly describes a cleanup or recovery
path.

CLI may implement this sequence in a small application-side executor class. That
executor calls workflow methods only; it does not call functional access
services directly and does not interpret UnitTest event-stream evidence.

### Publication Consumption

CLI consumes `RunWorkflowPublication` for:

- progress output,
- active operation reporting,
- important diagnostic snapshot entries,
- final result summary,
- artifact/log path reporting,
- process outcome mapping.

CLI console output is human-facing and not a machine-readable interface. CI/CD
uses process exit code and file artifacts.

### Exit Code Mapping

CLI maps terminal workflow publication, diagnostics, output state, current run
snapshot, and coordinated-release result to a stable process exit code. The
implemented categories are success, test failure, invalid arguments,
invalid configuration, profile unavailable, environment unavailable, execution
failure, timeout, cancellation, artifact failure, cleanup failure, and unexpected
application failure. Cleanup failure has precedence over the preceding run
outcome.

Exit-code mapping remains CLI-owned. `RunWorkflowService` does not call
`Environment.Exit` and does not know process exit codes. Numeric values and
operator-facing usage are documented by the CLI project.

### Cleanup

CLI requests coordinated bounded cleanup at command end after normal completion,
test failure, setup/execution failure, timeout, or cancellation. Cleanup runs
without the caller cancellation token so the first `Ctrl+C` does not prevent
resource release.

The unattended run workflow uses configured runtime provisioning intent, defaulting
to `CreateOwned`. Shared Core derives `DestroyResource` for creation and
`KeepResource` for `UseExisting`; the
selected profile performs the explicit release request. Communication, project
access, owned runtime state, profile session, and package workers are released
while the already-written JUnit artifact is preserved. A second `Ctrl+C` is an
explicit immediate-termination fallback and cannot guarantee cleanup.

## Shared Rules

- Applications compose workflow methods; shared core owns workflow semantics.
- Applications consume action gates; they do not calculate action availability.
- Applications consume diagnostic snapshot entries as diagnostic help; they do
  not parse diagnostic messages for behavior decisions or use diagnostics as an
  event log.
- Applications consume `UnitTestRunEpochSnapshot`; they do not access mutable
  epoch state.
- Applications bind artifact export services to workflow publication; exporters
  serialize concrete formats from stable run snapshots.
- Applications own presentation and process behavior; shared core owns common
  workflow state, monitoring, event processing, diagnostics correlation, and
  output coordination.
