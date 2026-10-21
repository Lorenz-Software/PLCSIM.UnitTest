# TIA Openness V21 Opt-In Validation

This document describes manual, opt-in validation for the Siemens TIA Openness
V21 binding. It is not part of default CI because it requires installed Siemens
software, local licenses, a suitable project, and an interactive Windows user
session.

## Prerequisites

- TIA Portal V21 with TIA Openness installed.
- The user account is allowed to use TIA Openness on the machine.
- TIA Openness firewall/trust prompts can be confirmed interactively.
- A direct `.ap21` project and, for archive validation, its `.zap21` archive;
  each source must contain at least one S7-1500 PLC target.
- For transfer validation, a compatible PLCSIM Advanced runtime instance is
  already prepared by the runtime side of the Siemens profile.

The validation project must stay local or be provided as a dedicated test
resource. Large, licensed, customer-specific, or machine-specific TIA projects
must not be committed into this repository.

## Deterministic Verification

The V21-owned test project is the default no-Siemens verification boundary:

```powershell
dotnet test access-profiles\siemens\tia-openness\v21\tests\PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V21.csproj
```

These tests use controlled evidence sources and worker/session doubles. They do
not read the developer machine registry, load TIA Openness, open a TIA project,
or require firewall authorization.

The binding-owned test project groups tests by stable responsibility:

- environment, package, manifest, and readiness;
- worker process host, startup, logging, cancellation, and cleanup;
- strict worker protocol parsing and malformed-response handling;
- project-session ownership and archive-workspace ownership;
- target discovery and selection;
- build, transfer, cancellation containment, and session health;
- session disposal, subscriber release, and managed collectability;
- explicitly opt-in real-runtime lifetime validation.

Shared test support is limited to neutral V21 target data, default operational
logging, the recording environment probe, and the recording engineering-session
handle used across multiple responsibility fixtures. Manifest lookup, worker-log
polling, runtime-target construction, archive failure setup, GC forcing, and
real-process resource measurement remain local to their owning fixtures.

| Area | Deterministic verification | Real TIA verification |
| --- | --- | --- |
| Package and readiness | Manifest shape, documented registry mapping, missing evidence versus registry-access error, Register/Open rule equivalence | Installed V21 registry/PublicAPI evidence resolves to the selected package and starts its worker |
| Worker boundary | Startup arguments, strict response schema/type validation, bounded fallback evidence, cancellation, invalidation, close failure mapping, and facade-to-dispatcher-to-session-handle delegation | `net48` worker starts, connects over the named pipe, loads V21 assemblies, serves repeated commands through one serialized session loop, and terminates after close |
| Project session | Direct/archive mode validation, missing and unsupported source rejection, open result mapping, partial-open cleanup, archive workspace ownership, and worker-internal project-owner delegation | A direct `.ap21` project opens normally; a `.zap21` archive is retrieved through the Siemens API; both remain locked only while owned and are released after close |
| Target discovery | Exact id matching, ambiguity/not-found handling, non-PLC filtering, neutral path preservation | Ungrouped and grouped PLC targets are traversed and produce stable typed, URI-escaped ids |
| Build | Success/warning/error result and evidence mapping, worker failure containment | The selected PLC software target compiles and real compiler warnings/errors are classified correctly |
| Transfer | Typed-route validation, result/evidence mapping, rejection before worker dispatch | Download reaches the selected PLCSIM target through `PN/IE` and `PLCSIM`; actual route and callback decisions are recorded |
| Cleanup | Idempotent close, disposed health, event-subscriber release, archive child-directory cleanup, managed session/handle collectability, close failure containment, and observable fallback-cleanup failures without replacing the primary open/cancellation result | Project and TIA resources are released, no worker remains, direct projects can be reopened, and retrieved archive workspaces are removed without changing the archive or configured base |
| Repeated lifetime | Deterministic ownership and collectability boundaries | Repeated target discovery remains within worker resource guardrails; repeated open/close leaves no worker or project lock |

## Firewall And Trust Prompt

TIA Openness may ask the user to allow the calling executable. This is expected
when the worker executable is new or has changed after a rebuild. If the prompt
is denied, validation fails as an environment authorization problem, not as
proof that the binding logic is broken.

The prompt belongs to the real-runtime validation flow. Automated no-Siemens
tests must not depend on it.

After deploying or rebuilding the worker, use the Siemens-profile-owned
`setup-siemens-openness.ps1` script against this binding package from an
interactive Windows user session. The script invokes this binding's minimal,
project-free authorization probe and closes the worker again. Once the
machine-wide Openness allow-list entry has been accepted for that exact deployed
worker binary, repeat the probe and confirm that it succeeds without another
prompt. A later worker rebuild changes the fingerprint and requires setup again.
The version-neutral package layout, command variants, exit codes, registration
order, and unattended CLI handoff are documented in
`../../../docs/deployment.md`.

## Real TIA Validation Procedure

The V21 binding must be validated in this order:

1. Build the V21 binding solution without copying Siemens API assemblies into
   the repository.
2. Register the Siemens access profile and confirm that V21 readiness resolves
   from the installed registry/PublicAPI evidence.
3. Open a worker-backed engineering session for a direct `.ap21` project. If a
   firewall/trust prompt appears, record whether it was accepted or denied and
   verify the resulting diagnostic classification.
4. Discover targets in both ungrouped and grouped device hierarchies. Confirm
   that only PLC-software targets are selectable and that `plcRef.id` uses
   stable typed, URI-escaped path segments.
5. Build the selected target. Record final status, warning/error counts,
   bounded evidence, and diagnostics.
6. Transfer to a prepared local PLCSIM Advanced runtime. Record the selected
   runtime identity, typed route input, resolved Siemens route evidence,
   callback decisions, and final transfer result.
7. Close the project/session. Confirm that the worker exits within the bounded
   cleanup period, no TIA project lock remains, and the worker log contains the
   correlated startup, operation, and shutdown outcomes.
8. Run the opt-in repeated-lifetime test with
   `PLCSIM_UNITTEST_VALIDATE_TIA_OPENNESS_V21_LIFETIMES=1` and the local project
   path in `PLCSIM_UNITTEST_TIA_OPENNESS_V21_PROJECT`. Confirm bounded resources
   across repeated discovery and successful reopening after every close.
9. Cancel a build or transfer after worker dispatch. Confirm that the correlated
   terminal response is drained, the same worker/session remains reusable, and
   retry completes without reopening the project. Then use End Session and
   confirm normal project unlock and worker exit.
10. Select the `.zap21` archive through registered project-source capabilities.
    Confirm retrieval, target discovery, build, transfer, project release,
    worker exit, and deletion of only the unique retrieved-project child
    directory. Confirm that the archive and configured workspace base remain.

Expected transfer route evidence for the implemented local transfer path:

- connection mode evidence resolves to `PN/IE`;
- PC interface evidence resolves to `PLCSIM`;
- PLCSIM PC-interface number is `1`;
- the concrete PLC target interface, for example `1 X1`, is evidence only and
  is not user-facing configuration.

## Current Real-Runtime Baseline

The executable V21 path has been exercised with installed TIA Portal V21 and
direct and archived forms of an S7-1500 project for:

- worker-backed engineering-session and project open;
- neutral PLC target discovery and selection;
- compilation of the selected PLC software target;
- successful transfer to a prepared PLCSIM Advanced runtime through `PN/IE`
  and `PLCSIM`;
- route evidence including PLCSIM PC-interface number `1` and a resolved PLC
  target interface;
- project/session close and worker release;
- caller cancellation during a dispatched build, terminal-response drain,
  retry in the same worker/project session, and subsequent successful transfer;
- two warm-up plus 24 measured target-discovery cycles in one session with
  stable thread count and bounded handle/private-memory observations;
- three independent open/close cycles of the same project without a surviving
  worker process or retained project lock.
- `.zap21` retrieval through the worker, target discovery, build, successful
  transfer, project/session close, worker exit, and owned workspace cleanup.

This baseline proves the successful Slice-1 path, the refactored worker/session
ownership boundaries, and bounded repeated-session cleanup. It does not replace
a long-running performance soak or release validation of denied firewall/trust
access, restricted Windows user rights, real compiler warning/error variants,
hard containment timeout, worker/process loss, unsupported download prompts,
protected projects, stale targets, or cleanup failures. Those remain explicit
release-hardening risks rather than hidden assumptions of the internal
refactoring.

## Expected Failure Classification

### Safety-program boundary

Real V21 investigation confirmed that offline Safety-program authorization and
compilation can succeed while the generic managed online download rejects the
Safety program. The production binding therefore inspects the selected target
and returns a binding-owned terminal `NotSupported` result before prompting for
a password, compiling, or transferring when Safety content is present. An
F-capable CPU with only a standard program is not rejected.

Deterministic tests cover detection and operation ordering. Real V21 evidence
covers a Safety archive where authorization and compilation succeeded and the
Siemens download provider rejected the transfer. This evidence does not claim
that other transfer routes, future TIA versions, or other access profiles have
the same limitation.

Validation should record failures as one of these categories:

- environment unavailable, for missing TIA V21 / PublicAPI / worker files;
- authorization problem, for Openness trust/firewall/user-right failures;
- project problem, for missing, locked, unsupported, corrupted, or
  upgrade-required project sources;
- target problem, for missing, ambiguous, non-S7-1500, or stale PLC targets;
- build problem, for compiler warnings/errors that affect the requested build;
- transfer problem, for download-provider, route, prompt, password, target
  upgrade, or runtime destination failures;
- worker problem, for named-pipe, worker crash, timeout, or cleanup failures.

## Evidence To Record

For each real-runtime validation run, record the following evidence in the
validation record for that run:

- TIA Portal product version and PublicAPI path;
- project source path or sanitized project identity;
- selected `plcRef.id`;
- selected runtime target identity;
- build status, warning count, and error count;
- transfer status and route evidence;
- diagnostics emitted by the binding;
- the worker-log path and worker instance/process identity;
- whether startup, session open/failure, and shutdown outcomes are present in
  the worker log without protocol traffic or sensitive payloads;
- whether an Openness firewall/trust prompt appeared;
- whether worker cleanup completed without a remaining worker process;
- for repeated-lifetime validation, the post-warm-up private-memory, handle, and
  thread observations and whether the project reopened after each close.

Durable repository documentation should describe the validation method. It
should not store machine-specific validation results.
