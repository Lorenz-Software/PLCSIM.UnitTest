# TIA Openness V20 Troubleshooting

This document covers installed-environment and operational failures owned by
the Siemens TIA Openness V20 binding.

## Prerequisites

- TIA Portal V20 and its TIA Openness PublicAPI are installed locally.
- The deployed package contains the `net8.0` facade, `net48` worker, manifest,
  and package dependencies without copied Siemens API assemblies.
- The interactive Windows user is a member of the `Siemens TIA Openness`
  Windows group.
- The user has logged off and on after group membership was granted.
- The exact deployed worker executable is authorized for TIA Openness.
- Transfer additionally requires a compatible PLCSIM Advanced target.

V20 readiness resolves the installed PublicAPI through:

```text
SOFTWARE\Siemens\Automation\Openness\20.0\PublicAPI\20.0.0.0
```

The selected key must provide the `Siemens.Engineering` assembly path and
assembly metadata. The V20 installation does not use an additional `net48`
registry-key segment. Missing evidence means the environment is unavailable;
inaccessible or contradictory evidence is an environment failure.

## Authorization And Security Errors

`Security error` or a message stating that the process owner is not a member of
`Siemens TIA Openness` is an environment authorization failure. Add the user to
the required Windows group, start a new Windows logon session, and retry with
the exact deployed worker package.

Authorization may also depend on the worker executable fingerprint. Rebuilding
or replacing the worker can require another interactive Siemens authorization
prompt. Use the Siemens-profile deployment and authorization process described
in `../../../docs/deployment.md`; do not authorize a different build output and
assume the deployed worker is covered.

`EngineeringSessionWorkerNoResponse` is a boundary symptom, not a root cause.
Use its bounded stderr evidence and the dedicated worker log to distinguish
missing managed dependencies, authorization failures, API-load failures,
protocol errors, and worker crashes.

## Safety-program Content

`SafetyProgramNotSupported` means the selected target contains Safety-program
content and managed setup was stopped deliberately before password prompting,
build, or transfer. Use a target containing only standard PLC software for this
binding. An F-capable CPU alone does not trigger this result.

The guard has deterministic coverage. Detection against an installed V20
Safety project remains unverified; preserve the worker log if native project
evidence produces a different classification.

## Managed Worker Dependencies

The `net48` worker must receive the dependency versions produced for that
worker. Do not flatten the `net8.0` facade and `net48` worker into one directory
when their managed dependencies have different assembly versions. A
`FileLoadException` for `System.Memory` or
`System.Diagnostics.DiagnosticSource` indicates a package-layout or dependency
version mismatch rather than a Siemens project failure.

## Project Open And Build

- `.ap20` is opened directly; `.zap20` is retrieved into a binding-owned
  workspace.
- Automatic project upgrade is not supported.
- Multiple supported PLC targets require an exact `plcRef.id`; otherwise target
  discovery returns an ambiguity result and lists the discovered target ids.
- Compiler warnings and errors are build results. Inspect the bounded compiler
  evidence before classifying the failure as a worker or project-open problem.

The real V20 baseline currently covers `.zap20` retrieval, target discovery,
explicit target selection, and a successful build. Direct `.ap20` open and real
compiler warning/error variants have not yet been exercised.

## Transfer

The transfer command and response mapping are covered deterministically, but a
real V20 download to PLCSIM Advanced has not been validated. A transfer failure
must therefore be investigated as new real-runtime evidence. Do not infer that
successful V20 compilation proves the download route, callbacks, or runtime
destination.

## Locks And Cleanup

The worker owns the TIA engineering session and retrieved archive workspace.
Release should close the project/session, terminate the worker, and remove only
the binding-owned retrieved-project directory. Repeated real V20 lock and
resource-lifetime validation remains open. If a project stays locked or a
worker survives release, preserve the diagnostics and worker log before manual
cleanup.

## Worker Log

The worker log family is:

```text
access-profile-<profile-id>-tia-openness-v20-worker-<date>.log
```

Look for selected PublicAPI evidence, worker startup, project/session identity,
operation correlation, the primary failure, cleanup outcome, and process exit.
Use `validation.md` to distinguish confirmed real-runtime behavior from
deterministic-only coverage.
