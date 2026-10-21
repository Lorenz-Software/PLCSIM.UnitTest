# TIA Openness V21 Troubleshooting

This document covers installed-environment and operational failures owned by
the Siemens TIA Openness V21 binding.

## Prerequisites

- TIA Portal V21 and its TIA Openness PublicAPI are installed locally.
- The deployed V21 package contains its `net8.0` facade, `net48` worker,
  manifest, and package dependencies without copied Siemens assemblies.
- The interactive Windows user is permitted to use TIA Openness.
- The exact deployed worker executable has been accepted through the Siemens
  authorization prompt before unattended use.
- Managed transfer additionally has a compatible prepared PLCSIM target.

Readiness uses installed Siemens registry/PublicAPI evidence. Missing installed
evidence is an unavailable environment. Inaccessible or contradictory selected
evidence is an environment failure and should be investigated from diagnostics
and the worker log rather than bypassed with a hard-coded API path.

## Authorization And Security Errors

Run the Siemens-profile-owned setup script from an interactive Windows session
as described in `../../../docs/deployment.md`. Authorization is tied to the
exact worker binary fingerprint. Rebuilding or replacing that executable
requires authorization again.

`Security error: This process is not permitted to connect to TIA-Portal` means
the worker was not authorized. `EngineeringSessionWorkerNoResponse` means the
worker exited or failed to answer; append the bounded stderr evidence and check
the dedicated worker log before classifying it as authorization, startup,
protocol, or API-load failure.

If the Siemens prompt repeats:

1. Stop the application and ensure no old V21 worker remains.
2. Run the setup script against the same deployed package the application will
   load, not a build output or an older debug deployment.
3. Allow enough time to confirm the interactive prompt before the probe
   deadline.
4. Repeat the unchanged probe. Success without another prompt proves the
   deployed worker is authorized.

Do not manually copy an allow-list entry between worker builds. Do not assume a
successful prompt for one executable authorizes a differently built worker.

## Safety-program Content

`SafetyProgramNotSupported` means the selected target contains Safety-program
content and managed setup was stopped deliberately before password prompting,
build, or transfer. Use a target containing only standard PLC software for this
binding. An F-capable CPU alone does not trigger this result.

This is distinct from TIA Openness executable authorization, UMAC, PLC access
protection, and an incorrect Safety password. Real V21 investigation confirmed
that offline Safety authorization and compilation do not make the generic
managed online transfer path capable of loading the Safety program.

## Project Open, Build, And Transfer

- `.ap21` is opened directly; `.zap21` is retrieved into a binding-owned
  workspace. Automatic project upgrade is not supported.
- An unsupported, missing, ambiguous, or non-PLC project target fails before
  build or transfer.
- Build errors are compiler results. Inspect bounded compiler evidence rather
  than treating them as worker startup failures.
- Transfer requires the prepared local PLCSIM route selected by the Siemens
  profile. Download route or callback failures are transfer failures, not proof
  that project compilation failed.
- `DownloadMessagesTruncated` means transfer evidence exceeded its bounded log
  limit. It does not by itself mean the transfer failed; use the final transfer
  result and worker log.

A canceled build or transfer remains retryable while the engineering session
stays healthy. A session explicitly reported as invalidated must be released and
opened again through the application workflow.

## Locks And Cleanup

Direct projects remain locked only while the binding owns the engineering
session. Retrieved archive workspaces are binding-owned and removed during
release. If a project remains locked or a worker remains after End Session,
record the application diagnostic and worker log, terminate the application
session, and verify worker cleanup before reopening the project.

## Worker Log

The worker log family is:

```text
access-profile-<profile-id>-tia-openness-v21-worker-<date>.log
```

Look for worker startup, selected PublicAPI evidence, project/session identity,
operation correlation, the primary failure, cleanup outcome, and process exit.
Use `docs/validation.md` for opt-in real-runtime validation rather than turning
normal CI into a Siemens-dependent test path.
