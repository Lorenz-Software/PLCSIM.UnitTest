# TIA Openness V15.1 Troubleshooting

This document covers installed-environment and operational failures owned by
the Siemens TIA Openness V15.1 binding.

## Prerequisites

- TIA Portal V15.1 and its native TIA Openness PublicAPI are installed locally.
- The package contains the `net8.0` facade, `net48` worker, manifest, and
  worker dependencies without copied Siemens API assemblies.
- The interactive user is a member of the `Siemens TIA Openness` Windows
  group and has logged on again after membership was granted.
- The exact deployed worker executable is authorized for TIA Openness.
- Transfer additionally requires a compatible local simulation target.

V15.1 readiness resolves:

```text
SOFTWARE\Siemens\Automation\Openness\15.1\PublicAPI\15.1.0.0
```

The selected key must provide the `Siemens.Engineering` assembly path and
consistent assembly metadata for version `15.1.0.0`.

## Authorization And Worker Startup

A Siemens security error or missing `Siemens TIA Openness` group membership
is an environment authorization failure. Correct the membership, start a new
Windows logon session, and authorize the exact deployed worker executable.
Replacing or rebuilding the worker can require a new authorization prompt.

`EngineeringSessionWorkerNoResponse` is a boundary symptom. Inspect its
stderr evidence and the V15.1 worker log to distinguish authorization, missing
managed dependencies, PublicAPI loading, protocol failures, and worker crashes.

Do not flatten the net8 facade and net48 worker dependencies into one shared
directory. A `FileLoadException` for a managed support assembly indicates a
package-layout or dependency-version problem rather than a TIA project error.

## Safety-program Content

`SafetyProgramNotSupported` means the selected target contains Safety-program
content and managed setup was stopped deliberately before password prompting,
build, or transfer. Use a target containing only standard PLC software for this
binding. An F-capable CPU alone does not trigger this result.

The guard has deterministic coverage. Detection against an installed V15.1
Safety project remains unverified; preserve the worker log if native project
evidence produces a different classification.

## Project Open And Discovery

- `.ap15_1` is opened directly; `.zap15_1` is retrieved into a
  binding-owned temporary workspace.
- Automatic project upgrade is not supported.
- Multiple supported PLC targets require an exact `plcRef.id`. An ambiguity
  result lists the discovered target ids and does not invalidate the session.

## Build

Compiler warnings and errors are build results. Inspect compiler evidence
before classifying them as worker or project-open failures.

Historical native validation established that V15.1 Openness cannot compile a
failsafe device and reports:

```text
Compiling a failsafe device by openness is not supported.
```

The current managed workflow should reject this project during Safety-content
inspection before the compiler is invoked. Safety login or credential handling
does not make this native V15.1 operation available.

V15.1 does not expose the later project property for enabling simulation during
block compilation. If the worker log reports that this property is unavailable,
ensure the TIA project settings and selected blocks are already suitable for
simulation. A Siemens `NonRecoverableException` during `Compile()` can be a
project/compiler failure; preserve the complete worker log and reproduce the
compile in TIA Portal before classifying it as an IPC failure.

## Cleanup Duration

TIA V15.1 can require more than 15 seconds to close a project after compilation.
Validated cleanup took about 18 seconds for a direct project and about 23
seconds for a retrieved archive. The V15.1 facade therefore uses a two-minute
internal worker-cleanup timeout. Caller cancellation and higher-level deadlines
remain effective.

If cleanup exceeds that boundary, preserve the worker log and identify whether
execution stopped at project close, TIA Portal disposal, or archive-workspace
deletion.

Successful cleanup logs include:

```text
TIA project closed.
TIA Portal session disposed.
Deleting owned TIA project archive workspace.
Worker command completed. CommandType=Close
TIA Openness V15.1 worker exiting. ExitCode=0
```

## Transfer

Transfer request and response mapping are covered deterministically, but a real
V15.1 download has not been validated. Treat the first real transfer as new
integration evidence; successful project open and compilation do not prove the
download route or callbacks.

## Worker Log

The worker log family is:

```text
access-profile-<profile-id>-tia-openness-v15_1-worker-<date>.log
```

Look for PublicAPI evidence, worker startup, project/session identity, operation
duration, the primary failure, cleanup outcome, and process exit. Use
`validation.md` to distinguish real-runtime evidence from deterministic-only
coverage.
