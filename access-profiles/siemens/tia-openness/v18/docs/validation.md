# TIA Openness V18 Opt-In Validation

This document describes manual, opt-in validation for the Siemens TIA Openness
V18 binding. It is not part of default CI because it requires installed Siemens
software, local licenses, a suitable project, and an interactive Windows user
session.

## Prerequisites

- TIA Portal V18 with TIA Openness installed.
- The interactive user is a member of the `Siemens TIA Openness` Windows group
  and has logged on again after membership was granted.
- TIA Openness authorization prompts can be confirmed interactively.
- A direct `.ap18` project or `.zap18` archive containing a supported PLC
  software target.
- A compatible PLCSIM Advanced runtime for transfer validation.

Test projects must remain local or be dedicated test resources. Licensed,
customer-specific, or machine-specific projects must not be committed.

## Deterministic Verification

From the repository root:

```powershell
dotnet test access-profiles\siemens\tia-openness\v18\PLCSIM.UnitTest.Siemens.TiaOpenness.V18.sln
```

The V18-owned deterministic suite validates package and manifest shape,
registry and PublicAPI handling, the net48 worker protocol, project requests,
archive ownership, target selection, build and transfer mapping, cancellation,
diagnostics, and cleanup. These tests do not start TIA Portal or perform a real
download.

## Validation Matrix

| Area | Deterministic verification | Real V18 evidence | Remaining validation |
| --- | --- | --- | --- |
| Environment | Registry mapping, assembly identity, package shape, missing and inaccessible evidence | Registry API `18.0.0.0` resolved the installed V18 `Siemens.Engineering.dll`; the `net48` worker loaded it successfully | Additional V18 service-pack variants |
| Authorization | Worker startup and bounded startup-error mapping | The authorized interactive user opened projects through the deployed worker | Denied-prompt and unattended setup variants |
| Project open | `.ap18` and `.zap18` request validation and archive ownership | Direct project open and archive retrieve/open succeeded; product and project versions were 18 | Additional project structures and protected projects |
| Target discovery | Hierarchy traversal, filtering, stable ids, exact selection, ambiguity and not-found results | A grouped PLC target was discovered and selected through its stable encoded target id | Multiple-target ambiguity and additional non-PLC device mixes on real V18 projects |
| Build | Success, warning, error, evidence, and worker-failure mapping | The selected target compiled with three expected warnings and zero errors; standard and Safety compiler evidence was preserved | Real compiler error and cancellation variants |
| Transfer | Typed route and callback/result mapping | Not tested because PLCSIM Advanced was not installed | Complete local PLCSIM download and callback evidence |
| Cleanup | Close protocol, archive ownership, process containment | Direct and archive sessions closed in about 2.2 to 3.4 seconds; the direct project was unlocked and could be opened normally afterward | Repeated stress and failure-containment runs |

## Confirmed Real V18 Baseline

The installed V18 validation proved that the binding can:

- resolve and load the native V18 PublicAPI from a `net48` worker;
- communicate with the `net8.0` facade through the named-pipe protocol;
- open `.ap18` projects and retrieve `.zap18` archives;
- discover a PLC target in a grouped project hierarchy and preserve its stable
  neutral target id;
- compile the explicitly selected target while preserving standard and Safety
  compiler diagnostics;
- close direct and archive sessions cleanly and release the direct-project
  lock.

The measured cleanup durations were 2248 ms, 2362 ms, and 3417 ms. These
measurements establish the V18-specific baseline without borrowing timeout
evidence from another TIA version.

## Current Safety-program Boundary

The binding now detects Safety-program content and returns `NotSupported`
before authorization, build, or transfer. A standard program on an F-capable
CPU remains eligible. Earlier compiler evidence remains historical evidence
from before this guard. The current ordering and classification have
deterministic test coverage; detection has not been validated with an installed
V18 Safety project and must remain recorded as unverified real-runtime
behavior.

## Deferred Validation

The following checks remain open:

- transfer/download to PLCSIM Advanced, including route and callback evidence;
- real cancellation during project open, build, and transfer;
- repeated session/resource stress and forced cleanup failure containment;
- protected projects, credentials, automatic upgrade, and attach mode, which
  are outside the current scope.

Do not convert deterministic transfer coverage into a real V18 transfer support
claim. Machine-specific paths and validation transcripts belong in issue notes
or temporary handoff artifacts, not in this document.
