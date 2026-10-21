# Configuration File Contract

This document defines the stable UnitTest run configuration file contract used
by PLCSIM.UnitTest applications.

The configuration file is an application-facing contract. It is read by the WPF
application for manual import/load workflows and by the CLI for repeatable
automation workflows. It describes the intended UnitTest setup and common
execution policy without exposing workflow internals or vendor API object
models.

## Format

The configuration file format is JSON.

The current contract version is `2`. Version `1` remains readable as a legacy
configuration with implicit `CreateOwned` provisioning. The `runtime` section
requires version `2`; it must not be used in a version-1 file. This prevents older
consumers from ignoring an explicit reuse intent and creating a runtime instead.
CLI and WPF support both versions with the same provisioning rules.

## Ownership

The shared configuration file contract owns:

- file versioning,
- the project source entry point,
- optional project authorization values used by automation,
- access profile selection,
- runtime provisioning intent (not vendor target addressing),
- common timeout settings,
- output settings for consumers that honor file-defined output policy.

The selected access profile owns its profile-specific payload under
`accessProfile.values`.

Applications and shared core may validate the common shape, extract the profile
payload as raw JSON text, and apply common defaults. They must not interpret
profile-specific PLC, runtime, communication, vendor API, or environment
selection semantics.

The WPF application ignores file-defined `output.directory` and
`output.testResult`. WPF always writes the test result artifact when a result can
be produced and uses the artifact folder configured in WPF application settings.
The CLI full-run workflow also requires `--output <junit-file>` and uses that
explicit path as the authoritative required artifact target; it does not derive
the target from the optional `output` section. The section remains available to
other automation consumers and future CLI output-policy expansion.

The optional `project.authorization.safetyPassword` value is accepted only for
CLI automation when the selected access profile requires Safety-program
authorization before a managed build or transfer. WPF import and load workflows
discard this value and acquire any required credential interactively.
Applications must not include
the password in configuration summaries, logs, diagnostics, evidence, command
line arguments, or exported WPF state.

Project ownership is not part of this file contract. CLI configuration execution
is application-managed. WPF manual and import flows select ownership as
application session input; `Load UnitTest...` uses application-managed project
handling.

After discovery and selection, the workflow converts configured intent into a
normalized `RunSetup`. At that point the selected PLC/project reference and
runtime reference are represented as neutral `ProjectTargetRef` and
`RuntimeTargetRef` values.

## Minimal Shape

```json
{
  "version": 2,
  "project": {
    "source": "sample-project"
  },
  "accessProfile": {
    "id": "fake",
    "version": "1.0.0"
  }
}
```

`accessProfile.values` is optional. If omitted, it is treated as an empty
profile-specific JSON object.

Pre-contract shapes are not supported.

## Practical Shape

```json
{
  "version": 2,
  "runtime": {
    "provisioningMode": "UseExisting"
  },
  "project": {
    "source": "sample-project"
  },
  "accessProfile": {
    "id": "fake",
    "version": "1.0.0",
    "values": {
      "plcRef": {
        "id": "fake-project-target"
      },
      "runtimeRef": {
        "id": "fake-runtime-target"
      }
    }
  },
  "execution": {
    "timeouts": {
      "projectPrepare": "00:01:00",
      "buildTransfer": "00:10:00",
      "runtimePrepare": "00:01:00",
      "plcCommand": "00:00:10",
      "plcStartReaction": "00:00:30",
      "plcStopReaction": "00:00:30",
      "communicationPrepare": "00:00:30",
      "communicationRevalidation": "00:00:30",
      "unitTestCommand": "00:00:03",
      "unitTestCommandReaction": "00:00:10",
      "testRun": "00:02:00",
      "finalEvent": "00:00:10"
    }
  }
}
```

## Top-Level Fields

| Field | Required | Owner | Meaning |
| --- | --- | --- | --- |
| `version` | Yes | Application layer. | Configuration file contract version: `2`, or legacy `1` without a runtime section. |
| `runtime.provisioningMode` | No | Application layer. | `CreateOwned` or `UseExisting`. Omission defaults to `CreateOwned`; a target address alone never implies reuse. |
| `project.source` | Yes | Application layer. | Project source used as the UnitTest setup entry point. The concrete source string is interpreted by the selected access profile or project picker boundary. |
| `project.authorization.safetyPassword` | No | CLI application / project handling. | Password supplied when the selected profile requires Safety-program authorization before a managed build or transfer. WPF discards this persisted value. |
| `accessProfile.id` | Yes | Application layer / plugin loader. | Selected access profile id, for example `fake` or `siemens`. |
| `accessProfile.version` | No | Application layer / plugin loader. | Requested access profile version. If omitted, the loader selects the highest available version for the selected id. If present, the loader must select that exact version. |
| `accessProfile.values` | No | Selected access profile. | Profile-specific JSON object in the file. Applications and shared core pass the object to the selected profile as raw JSON text and do not interpret profile-specific semantics. |
| `execution.timeouts` | No | Application/shared workflow defaults. | Optional common timeout overrides. |
| `output.directory` | No | Automation/output policy. | Output directory for consumers that honor file-defined output policy. WPF ignores this field; the CLI full-run workflow overrides it with the required `--output` file path. |
| `output.testResult` | No | Automation/output policy. | Enables or disables the default test result artifact request for consumers that honor file-defined output policy. WPF ignores this field; the CLI full-run workflow always requires JUnit output. |

## Runtime Intent And Target Resolution

The mode is a string, read case-insensitively and written with the enum's exact
name. Unknown names, numeric values (including numeric strings), explicit null
mode values and unknown fields inside `runtime` are invalid. An omitted runtime
section or an empty runtime object means `CreateOwned`.

For `UseExisting`, an explicit profile-owned target identifier must resolve
uniquely. Failure does not fall back to a different target. Without an explicit
identifier, only the profile-designated discovery default may be used. Neither
candidate order/count nor a project-internal deployment address establishes a
default. If the profile has no default, an explicit target is required.

For `CreateOwned`, a preselected existing target is a configuration error; it is
not silently removed or reinterpreted. Profile-specific creation parameters,
such as a requested new instance name, remain intact. A profile may report that
creation is unsupported. Explicit addressing does not grant ownership of an
existing resource. Resource release retains the runtime contract's rules.

## Profile Payload

`accessProfile.values` must be a JSON object when present.

Profile payloads may contain nested objects. This allows access profiles to model
PLC references, runtime references, communication hints, or environment-specific
selectors without changing the shared top-level contract.

The shared contract does not reserve names inside `accessProfile.values`.
The selected profile defines the supported keys and validation semantics. Every
access-profile directory package documents those fields at the fixed package
path `docs/configuration.md`, as required by the access-profile package
contract.

The selected access profile receives the project source, the profile values as
raw JSON text, and the configuration load mode. It does not receive the full
root document and does not need the already-resolved `accessProfile.id` or
`accessProfile.version` for internal configuration validation.

The public Common/Profile boundary must not expose JSON-library-specific DOM
types. The profile payload is transported through a small value object such as:

```csharp
public sealed record AccessProfileConfigurationPayload(string Json);
```

The application layer creates this payload from the `accessProfile.values`
object after common-shape validation. The selected access profile parses the
JSON text with its own implementation dependency.

## Access Profile Version Selection

When more than one version of an access profile is available in the active
discovery scope, the loader applies these rules:

- `accessProfile.id` is required and selects the profile family.
- If `accessProfile.version` is omitted, the loader selects the highest available
  version for that id.
- If `accessProfile.version` is present, the loader selects exactly that version.
- If no matching version exists, loading fails with a configuration/profile-load
  diagnostic.
- If multiple candidates have the same id and same selected version, discovery is
  ambiguous and loading fails. Source kind or source priority must not silently
  override an identity collision.

Profile versions must use a comparable semantic version string, for
example `1.0.0`.

Discovery sources, package layout, manifest shape, and duplicate/source rules are
defined in `docs/contracts/access-profile-package-contract.md`.

## Timeout Fields

Timeout values use the invariant `TimeSpan` string form, for example
`"00:01:00"`.

| Field | Meaning |
| --- | --- |
| `projectPrepare` | Overall deadline for preparing project access and the configured project target. |
| `buildTransfer` | One overall deadline shared by the composed build and transfer operation. It is not reset between build and transfer. |
| `runtimePrepare` | Overall deadline for attaching or creating the selected runtime target. |
| `plcCommand` | Deadline for the bounded PLC start/stop transport or vendor API call. Success reports command acceptance only. |
| `plcStartReaction` | Observation deadline for the expected PLC state reaction after an accepted start command. |
| `plcStopReaction` | Observation deadline for the expected PLC state reaction after an accepted stop command. |
| `communicationPrepare` | Deadline for establishing UnitTest lifecycle and event-stream communication. |
| `communicationRevalidation` | Maximum stale/revalidation period for established communication. Zero disables automatic terminal timeout handling. |
| `unitTestCommand` | Deadline for the bounded UnitTest RunControl command transport/API call such as Start, Abort, and Reset. Success reports write acceptance only. |
| `unitTestCommandReaction` | Observation deadline for the expected RunControl reaction after an accepted UnitTest command. |
| `testRun` | Observation timeout for the active test run after a trusted active boundary was detected. This guards the application against missing completion evidence; the PLC-side UnitTest implementation remains responsible for its own execution timeout semantics. |
| `finalEvent` | Maximum time after terminal lifecycle evidence to wait for final event-stream data before the run is finalized. |

The configured values are application-owned overall operation or observation
boundaries. Binding-internal worker startup, pipe, protocol, process-exit, and
vendor-call containment limits are not configuration-file fields.

## Defaults

If optional fields are omitted, consumers that honor the fields apply these
defaults:

| Field | Default |
| --- | --- |
| `accessProfile.values` | `{}` |
| `execution.timeouts.projectPrepare` | `00:01:00` |
| `execution.timeouts.buildTransfer` | `00:10:00` |
| `execution.timeouts.runtimePrepare` | `00:01:00` |
| `execution.timeouts.plcCommand` | `00:00:10` |
| `execution.timeouts.plcStartReaction` | `00:00:30` |
| `execution.timeouts.plcStopReaction` | `00:00:30` |
| `execution.timeouts.communicationPrepare` | `00:00:30` |
| `execution.timeouts.communicationRevalidation` | `00:00:30` |
| `execution.timeouts.unitTestCommand` | `00:00:03` |
| `execution.timeouts.unitTestCommandReaction` | `00:00:10` |
| `execution.timeouts.testRun` | `00:02:00` |
| `execution.timeouts.finalEvent` | `00:00:10` |
| `output.directory` | `artifacts` |
| `output.testResult` | `true` |

Defaults for common fields live in application/shared options code. Defaults for
profile-specific values belong to the selected access profile. WPF artifact
folder defaults are application settings, not UnitTest configuration file
defaults.

## Validation Flow

Validation happens in layers:

CLI `check-config` stops after common-shape validation, registration-currentness
checks, and registered profile selection. It does not load the profile, validate
profile-owned values, discover targets, or test installed runtime readiness.
The operational profile and workflow stages below occur during CLI `run` or WPF
setup progression, not during that preflight.

```text
WPF / CLI
  -> parse JSON
  -> validate version and common shape
  -> apply user or command-line overrides where allowed
  -> create configuration draft or run setup intent

AccessProfilePluginLoader
  -> select access profile by accessProfile.id and optional accessProfile.version

Selected access profile
  -> validate project.source and accessProfile.values JSON for the requested load mode
  -> discover/resolve PLC and runtime references

Shared workflow
  -> consume normalized RunSetup with neutral references
```

`Import Configuration...` may accept partially usable profile values and import
them into the WPF draft/stepper. `Load UnitTest...` requires a complete
profile-valid configuration because the setup progression is automated.

Both WPF paths preserve runtime provisioning intent. A failed target resolution
stops automatic progression and leaves the selection editable; it never selects
another runtime as a fallback. Build/download selection does not imply runtime
creation. Old files intended to reuse an existing target must explicitly migrate
to version `2` with `runtime.provisioningMode` set to `UseExisting`.

## Explicitly Not Configured

The configuration file contract does not include:

- project ownership or WPF build/download selection,
- setup/runtime/cleanup workflow action sequences,
- compile/download/runtime API strategy,
- vendor API binding selection,
- retry strategy,
- administrative registration, profile load/unload, target discovery,
  release, monitoring, and total cleanup deadlines,
- locale or language,
- UI layout or application settings,
- result artifact schemas,
- direct PLC communication contract fields.

These concerns belong to access-profile implementation, application settings,
workflow policy, artifact exporters, or PLC communication contracts.
