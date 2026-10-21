# TIA Openness V17 Opt-In Validation

This document describes manual, opt-in validation for the Siemens TIA Openness
V17 binding. It is not part of default CI because it requires installed Siemens
software, local licenses, a suitable project, and an interactive Windows user
session.

## Prerequisites

- TIA Portal V17 with TIA Openness installed.
- The interactive user is a member of the `Siemens TIA Openness` Windows group
  and has logged on again after membership was granted.
- TIA Openness authorization prompts can be confirmed interactively.
- A direct `.ap17` project or `.zap17` archive containing a supported PLC
  software target.
- A compatible PLCSIM Advanced runtime for transfer validation.

Test projects must remain local or be dedicated test resources. Licensed,
customer-specific, or machine-specific projects must not be committed.

## Deterministic Verification

From the repository root:

```powershell
dotnet test access-profiles\siemens\tia-openness\v17\PLCSIM.UnitTest.Siemens.TiaOpenness.V17.sln
```

The V17-owned deterministic suite validates package and manifest shape,
registry and PublicAPI handling, the net48 worker protocol, project requests,
archive ownership, target selection, build and transfer mapping, cancellation,
diagnostics, and cleanup. These tests do not start TIA Portal or perform a real
download.

## Validation Matrix

| Area | Deterministic verification | Real V17 evidence | Remaining validation |
| --- | --- | --- | --- |
| Environment | Registry mapping, assembly identity, package shape, missing and inaccessible evidence | Registry API `17.0.0.0` resolved the installed V17 `Siemens.Engineering.dll`; the `net48` worker loaded it successfully | Additional V17 service-pack variants |
| Authorization | Worker startup and bounded startup-error mapping | The authorized interactive user opened projects through the deployed worker | Denied-prompt and unattended setup variants |
| Project open | `.ap17` and `.zap17` request validation and archive ownership | Direct project open and archive retrieve/open succeeded; product and project versions were 17 | Additional project structures and protected projects |
| Target discovery | Hierarchy traversal, filtering, stable ids, exact selection, ambiguity and not-found results | A grouped PLC target was discovered and selected through its stable encoded target id | Multiple-target ambiguity and additional non-PLC device mixes |
| Build | Success, warning, error, evidence, and worker-failure mapping | Dispatch reached the native V17 `CompileProvider`; Siemens then required login to the password-protected Safety program | Completed non-Safety build, Safety authorization, compiler warnings, and cancellation |
| Transfer | Typed route and callback/result mapping | Not tested because PLCSIM Advanced was not installed | Complete local PLCSIM download and callback evidence |
| Cleanup | Close protocol, archive ownership, process containment | Direct and archive sessions closed in about 2.5 to 5.4 seconds; the direct project was unlocked and could be opened normally afterward | Repeated stress and failure-containment runs |

## Confirmed Real V17 Baseline

The installed V17 validation proved that the binding can:

- resolve and load the native V17 PublicAPI from a `net48` worker;
- communicate with the `net8.0` facade through the named-pipe protocol;
- open `.ap17` projects and retrieve `.zap17` archives;
- discover a PLC target in a grouped project hierarchy and preserve its stable
  neutral target id;
- dispatch compilation to the native V17 compiler provider and preserve the
  resulting Safety authorization failure;
- close direct and archive sessions cleanly and release the direct-project
  lock.

The measured cleanup durations were 2471 ms, 3195 ms, and 5357 ms. A temporary
post-run directory lock disappeared without a remaining validation, worker, or
TIA process and was consistent with transient system scanning rather than a
binding resource leak.

## Safety Boundary

The available validation project contains a password-protected Safety program.
TIA Openness V17 rejected compilation until that program is logged into through
the Siemens Safety API. This result confirms compiler dispatch but does not
confirm a completed build. Safety authorization and secret handling require a
separate design and validation path. They must not be inferred from ordinary
Openness process authorization.

The primary current UnitTest path remains non-Safety PLC software.

## Current Safety-program Boundary

The binding now detects Safety-program content and returns `NotSupported`
before authorization, build, or transfer. A standard program on an F-capable
CPU remains eligible. The compiler-dispatch evidence above predates this guard.
The current ordering and classification have deterministic test coverage;
detection has not been validated with an installed V17 Safety project and must
remain recorded as unverified real-runtime behavior.

## Deferred Validation

The following checks remain open:

- a completed build using a non-Safety V17 UnitTest project;
- transfer/download to PLCSIM Advanced, including route and callback evidence;
- real cancellation during project open, build, and transfer;
- repeated session/resource stress and forced cleanup failure containment;
- Safety-program authorization and protected-project credential handling;
- automatic upgrade and attach mode, which are outside the current scope.

Do not convert deterministic transfer coverage into a real V17 transfer support
claim. Machine-specific paths and validation transcripts belong in issue notes
or temporary handoff artifacts, not in this document.
