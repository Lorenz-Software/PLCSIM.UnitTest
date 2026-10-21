# Configuration Implementation

This document describes how the applications and shared workflow implement the
configuration file contract.

The stable file shape is defined in
`docs/contracts/configuration-file-contract.md`. Profile-specific payloads are
defined by the owning access-profile subtree documentation.

This implementation document must not become a second schema definition. It
describes responsibilities, mapping, defaults, validation flow, and runtime use.

## Implementation Boundary

Configuration loading has four distinct representations:

| Representation | Owner | Purpose |
| --- | --- | --- |
| Configuration file | User, WPF import/load, CLI. | Serialized run intent following the configuration file contract. |
| Configuration draft | WPF / CLI application layer. | Parsed and partially validated values before all profile discovery and target resolution has completed. |
| `AccessProfileSessionRequest` | Application / profile loader boundary. | Immutable profile-session setup containing identity/version, project source, and project ownership, but no operational profile payload. |
| `RunSetup` | Application layer / shared workflow boundary. | Normalized current run snapshot consumed by shared workflow after common defaults, provisioning intent, and neutral references are available. |

The application layer owns parsing, common-shape validation, defaulting, command
line or UI overrides, and conversion into workflow inputs. The selected access
profile owns validation and interpretation of its `accessProfile.values` payload.

The shared workflow consumes normalized concepts. It should not parse JSON, read
configuration files, or interpret profile-specific payload semantics.

## Contract Mapping

The common contract maps into application/shared concepts as follows:

| Contract Field | Implementation Target |
| --- | --- |
| `version` | Checked before further parsing/validation. Unsupported versions produce a configuration diagnostic. |
| `project.source` | Stored as the project source entry point for WPF draft state or CLI run intent. The selected profile may later interpret the concrete source string. |
| `accessProfile.id` | Converted to `AccessProfileConfig.ProfileId` and used by the plugin loader. |
| `accessProfile.version` | Passed to the profile loader as an optional exact version selector. If omitted, the loader selects the highest available version for the profile id. |
| `accessProfile.values` | Transported as profile-owned raw JSON payload. It is passed to the selected profile without application/shared semantic interpretation. |
| `runtime.provisioningMode` | Converted to `RunSetup.RuntimeProvisioningMode`; omission means `CreateOwned`. |
| `execution.timeouts` | Converted to `ExecutionPolicy` / `RunOperationTimeoutPolicy` after applying defaults. |
| `output` | Converted to `OutputPolicy` by CLI/automation consumers after applying defaults. WPF import/load ignores file-defined output values. |

PLC and runtime selection hints remain profile-specific while they are encoded in
`accessProfile.values`. After discovery/resolution they become neutral
`ProjectTargetRef` and `RuntimeTargetRef` values in the final `RunSetup`. Runtime
provisioning mode is application-owned intent, persisted in schema version 2:
`UseExisting` requires a selected runtime target, while `CreateOwned` lets
runtime preparation return the effective target.

Both hosts validate the configuration draft before discovery without requiring an
already resolved existing target. Final `RunSetup` validation still requires that
target for `UseExisting`. The shared Core `RuntimeTargetResolver` preserves an explicit profile target identifier
and resolves it uniquely; otherwise it uses only `RuntimeTargetDiscoveryResult.DefaultTarget`.
It never infers reuse from an address, clears an explicit target, or substitutes
a first/sole candidate for a missing default. CLI and WPF use this pure resolver;
host-specific error handling, manual correction and workflow progression remain
outside it. WPF carries the mode through both import and automatic load, and
validates resolution before target setters can clear contradictory input.

The WPF creation choice is independent of discovered candidate count. It denotes
provisioning intent, not a discovered runtime or a claim of creation support.
The profile may reject preparation as unsupported, stopping before transfer.

## Parsing And Defaults

The application uses the platform JSON parser directly at the
application boundary to validate the common file shape and extract
`accessProfile.values`. Parsing must preserve nested `accessProfile.values`
objects because profile payloads are not limited to string dictionaries.

`accessProfile.values` is transported to the selected profile as the
library-neutral raw JSON wrapper
`AccessProfileConfigurationPayload(string Json)`. The application validates only
that the value is absent or a JSON object, then serializes that object as JSON
text for the selected profile. It does not interpret profile-specific keys.

Common defaults are applied only after the file has a supported version and a
valid common shape. The common defaults are defined by the configuration
file contract and should live in shared/application options code, not in access
profiles.

Access profiles own defaults inside their profile payload. The application may
provide an empty profile payload when `accessProfile.values` is omitted.

The profile loader receives `AccessProfileSessionRequest`, containing selected
profile id/version, `project.source`, and project ownership. The application
retains `AccessProfileConfig`, including the raw profile values JSON payload, as
the current draft/run snapshot. Discovery and prepare operations receive that
snapshot explicitly. The profile-specific parser interprets only
`project.source` and `accessProfile.values`; it does not receive or interpret
the root JSON document.

## Validation Flow

Validation happens in layers:

```text
WPF / CLI
  -> parse JSON
  -> validate supported version and common shape
  -> apply allowed UI or command-line overrides
  -> create configuration draft or run setup intent

AccessProfilePluginLoader
  -> select and load profile from setup-only AccessProfileSessionRequest

Selected access profile
  -> validate the current project.source and accessProfile.values snapshot
  -> discover/resolve PLC and runtime references without establishing later prepare configuration

Optional profile configuration editor
  -> load the current opaque profile payload into the profile-owned service
  -> project run-intent-applicable semantic fields
  -> edit fields through typed profile operations
  -> read the replacement application-neutral snapshot
  -> validate the complete current snapshot through the profile

Application / shared workflow boundary
  -> create final RunSetup with runtime provisioning mode and neutral references
  -> validate common RunSetup structure
  -> configure workflow
```

Common validation diagnostics should identify the file field that failed where
practical. Profile validation diagnostics should identify the relevant
profile-owned field or selection hint where practical.

## Configuration Traceability

The shared workflow logs the accepted final `RunSetup` at configuration time.
It includes neutral profile/target/provisioning fields, payload length, and a
non-sensitive SHA-256 fingerprint of the opaque profile payload. Profile id and
project source are logged as separate structured fields. The raw opaque payload
is never written to the log.

Profiles log the effective values they own immediately before the corresponding
binding operation. The Siemens profile does this for project open, runtime
preparation, and UnitTest communication preparation. Each entry carries the
same configuration fingerprint so application, profile, binding, and worker
records can be correlated without exposing raw profile JSON. Worker logs record
the values actually received over IPC; they do not establish defaults that
override an already resolved profile configuration.

## WPF Usage

WPF uses the configuration contract for two menu entry points:

| Entry Point | Implementation Behavior |
| --- | --- |
| `Import Configuration...` | Parses the file, validates common shape, loads the selected access profile where needed, and imports values as a draft. Project ownership is initialized from the persisted WPF default and remains editable before the user continues manually through the stepper. |
| `Load UnitTest...` | Parses the file, validates common shape and profile payload, forces application-managed project ownership, performs the required profile/discovery/resolution progression, and lands in the operation step when the setup is complete. |

The UnitTest configuration file never supplies project ownership. The WPF
application setting `Build and download project by default` has product default
`false` and initializes new manual/import sessions only. A session-local Step 1
change does not rewrite that setting.

`Import Configuration...` imports all common and profile values that are usable
for the draft. Missing or incomplete profile details can be completed manually by
the user in the generic profile configuration step through the loaded profile's
configuration service.

`Load UnitTest...` requires a complete profile-valid configuration because the
setup progression is automated up to the operation/test-run surface.

After target selection, WPF presents an explicit profile configuration step.
The application renders controls from semantic field values only: text input for
strings and numeric values, a checkbox for booleans, and a selection control when
allowed values are supplied. It does not parse profile JSON paths or accept
profile-defined WPF controls.

The loaded profile configuration service owns a mutable editing snapshot for the
current WPF session. Each successful typed edit updates that snapshot, after
which WPF reads the replacement `AccessProfileConfig`, reprojects the fields, and
revalidates. Failed edits leave the previous snapshot unchanged. Immediate
generic validation covers only required values, invariant primitive conversion,
and optional allowed values. The profile's context-aware `ValidateConfig`
result gates progression to runtime and project preparation. The immutable
snapshot, not the service instance, is passed into discovery and final
`RunSetup`. Profiles without configurable values provide an empty field
projection; their configuration service still validates the current snapshot.

Neither entry point may silently discard an active session or draft. Replacement
is confirmation-gated by the WPF workflow.

WPF presentation state should keep imported values visible enough for the user
to understand that the current stepper values came from a configuration file.

WPF ignores `output.directory` and `output.testResult` from the configuration
file. It always requests the test result artifact when a result can be produced
and writes artifacts to the folder configured in WPF application settings. If the
operation step later exposes an output field, that field is initialized from
application settings, not from the UnitTest configuration file.

## CLI Usage

CLI uses the same contract as its primary repeatable setup input.

The CLI layer should load the configuration file, apply the small set of allowed
command-line overrides, bind the selected access profile, resolve required
targets, and then configure the shared workflow. CLI should fail early with a
configuration exit category when the file is invalid, incomplete for the chosen
command mode, or unsupported.

CLI must not add command-line options for every profile-specific setting.
Profile-specific target and environment selectors belong in the configuration
file payload.

## Timeout Ownership

Run-specific timeout values are common configuration fields, but their
enforcement is owned by the component responsible for the complete operation or
observed boundary:

| Timeout | Implementation Owner |
| --- | --- |
| `projectPrepare` | Shared workflow overall project-prepare operation. |
| `buildTransfer` | Shared workflow composed build/transfer operation; one budget spans both calls. |
| `runtimePrepare` | Shared workflow overall runtime-prepare operation. |
| `plcCommand` | Shared workflow overall PLC command-call boundary. A successful result means API acceptance only. |
| `plcStartReaction` | Shared workflow observation boundary after an accepted PLC start command. |
| `plcStopReaction` | Shared workflow observation boundary after an accepted PLC stop command. |
| `communicationPrepare` | Shared workflow overall communication-prepare operation. |
| `communicationRevalidation` | Profile/binding communication revalidation period supplied by the application run policy; zero disables automatic terminal timeout handling. |
| `unitTestCommand` | Shared workflow overall UnitTest RunControl command-call boundary. A successful result means write acceptance only. |
| `unitTestCommandReaction` | Shared workflow observation boundary after an accepted UnitTest command. |
| `testRun` | `TestRunMonitor`; observation timeout after a trusted active run boundary was detected. |
| `finalEvent` | `TestRunMonitor`; stabilization timeout after terminal lifecycle evidence. |

Shared Core creates a linked cancellation scope from the caller token and the
selected overall deadline. Caller cancellation maps to `Cancelled`; expiry of
the Shared Core deadline maps to `TimedOut`. A lower layer that returns
`Cancelled` or `TimedOut` without either condition being active has violated the
operation boundary and is normalized to a failure.

Registration refresh, profile load/unload, target discovery, project/runtime
release, monitoring, and total cleanup use
`ApplicationOperationTimeoutPolicy`. They are application settings rather than
configuration-file fields. Binding-internal pipe, worker startup, protocol,
process-exit, and vendor containment limits remain implementation details and
must not replace the application-owned overall deadline.

The application-level test-run timeout is an observation timeout. The PLC-side
UnitTest implementation remains responsible for its own execution timeout
semantics.

## Non-Goals

Configuration implementation must not introduce:

- workflow action sequences in the file model,
- profile-specific interpretation in application/shared core,
- Siemens API binding strategy in the common contract,
- UI settings inside the UnitTest run configuration,
- exporter format internals inside the common config parser.

These concerns stay with workflow policy, access-profile implementation,
application settings, or concrete exporters.
