# TIA Openness V18 Troubleshooting

This document covers installed-environment and operational failures owned by
the Siemens TIA Openness V18 binding.

## Prerequisites

- TIA Portal V18 and its TIA Openness PublicAPI are installed locally.
- The package contains the `net8.0` facade, `net48` worker, manifest, and worker
  dependencies without copied Siemens API assemblies.
- The interactive user is a member of the `Siemens TIA Openness` Windows group
  and has logged on again after membership was granted.
- The exact deployed worker executable is authorized for TIA Openness.
- Transfer additionally requires a compatible PLCSIM Advanced target.

V18 readiness resolves:

```text
SOFTWARE\Siemens\Automation\Openness\18.0\PublicAPI\18.0.0.0
```

The selected key must provide the `Siemens.Engineering` assembly path and
consistent assembly metadata.

## Authorization And Worker Startup

A Siemens security error or missing `Siemens TIA Openness` group membership is
an environment authorization failure. Correct the membership, start a new
Windows logon session, and authorize the exact deployed worker executable.
Replacing or rebuilding the worker can require a new authorization prompt.

`EngineeringSessionWorkerNoResponse` is a boundary symptom. Inspect its stderr
evidence and the V18 worker log to distinguish authorization, missing managed
dependencies, PublicAPI loading, protocol failures, and worker crashes.

Do not flatten the net8 facade and net48 worker dependencies into one shared
directory. A `FileLoadException` for a managed support assembly indicates a
package-layout or dependency-version problem rather than a TIA project error.

## Safety-program Content

`SafetyProgramNotSupported` means the selected target contains Safety-program
content and managed setup was stopped deliberately before password prompting,
build, or transfer. Use a target containing only standard PLC software for this
binding. An F-capable CPU alone does not trigger this result.

The guard has deterministic coverage. Detection against an installed V18
Safety project remains unverified; preserve the worker log if native project
evidence produces a different classification.

## Project Open, Discovery, And Build

- `.ap18` is opened directly; `.zap18` is retrieved into a binding-owned
  temporary workspace.
- Automatic project upgrade is not supported.
- Multiple supported PLC targets require an exact `plcRef.id`. An ambiguity
  result lists the discovered target ids and does not invalidate the session.
- Compiler warnings and errors are build results. Inspect compiler evidence
  before classifying them as worker or project-open failures.

## Cleanup Duration

Real V18 direct and archive validation measured cleanup between about 2.2 and
3.4 seconds. Preserve the worker log when cleanup approaches or exceeds the
configured boundary; these measurements are the V18-specific baseline rather
than inherited evidence from another version.

If cleanup fails, identify whether
execution stopped at project close, TIA Portal disposal, or archive-workspace
deletion. Do not shorten the timeout based only on faster archive runs.

Successful cleanup logs include:

```text
TIA project closed.
TIA Portal session disposed.
Deleting owned TIA project archive workspace.
Worker command completed. CommandType=Close
TIA Openness V18 worker exiting. ExitCode=0
```

## Transfer

Transfer request and response mapping are covered deterministically, but a real
V18 download to PLCSIM Advanced has not been validated. Treat the first real
transfer as new integration evidence; successful project open and compilation
do not prove the download route or callbacks.

## Worker Log

The worker log family is:

```text
access-profile-<profile-id>-tia-openness-v18-worker-<date>.log
```

Look for PublicAPI evidence, worker startup, project/session identity,
operation duration, the primary failure, cleanup outcome, and process exit.
Use `validation.md` to distinguish real-runtime evidence from deterministic-only
coverage.
