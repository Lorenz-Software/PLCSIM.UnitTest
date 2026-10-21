# TIA Openness V19 Opt-In Validation

This document describes manual, opt-in validation for the Siemens TIA Openness
V19 binding. It is not part of default CI because it requires installed Siemens
software, local licenses, a suitable project, and an interactive Windows user
session.

## Prerequisites

- TIA Portal V19 with TIA Openness installed.
- The interactive user is a member of the `Siemens TIA Openness` Windows group
  and has logged on again after membership was granted.
- TIA Openness authorization prompts can be confirmed interactively.
- A direct `.ap19` project or `.zap19` archive containing a supported PLC
  software target.
- A compatible PLCSIM Advanced runtime for transfer validation.

Test projects must remain local or be dedicated test resources. Licensed,
customer-specific, or machine-specific projects must not be committed.

## Deterministic Verification

From the repository root:

```powershell
dotnet test access-profiles\siemens\tia-openness\v19\PLCSIM.UnitTest.Siemens.TiaOpenness.V19.sln
```

The V19-owned deterministic suite validates package and manifest shape,
registry and PublicAPI handling, the net48 worker protocol, project requests,
archive ownership, target selection, build and transfer mapping, cancellation,
diagnostics, and cleanup. These tests do not start TIA Portal or perform a real
download.

## Validation Matrix

| Area | Deterministic verification | Real V19 evidence | Remaining validation |
| --- | --- | --- | --- |
| Environment | Registry mapping, assembly identity, package shape, missing and inaccessible evidence | Registry API `19.0.0.0` resolved the installed V19 `Siemens.Engineering.dll`; the `net48` worker loaded it successfully | Additional V19 service-pack variants |
| Authorization | Worker startup and bounded startup-error mapping | The authorized interactive user opened projects through the deployed worker | Denied-prompt and unattended setup variants |
| Project open | `.ap19` and `.zap19` request validation and archive ownership | Direct project open and archive retrieve/open succeeded; product and project versions were 19 | Additional project structures and protected projects |
| Target discovery | Hierarchy traversal, filtering, stable ids, exact selection, ambiguity and not-found results | Two PLC targets were discovered; ambiguity was reported without a target id; explicit selection succeeded | Additional grouped hierarchies and non-PLC device mixes |
| Build | Success, warning, error, evidence, and worker-failure mapping | Selected target compiled with two expected protection warnings and zero errors | Real compiler error and cancellation variants |
| Transfer | Typed route and callback/result mapping | Not tested because PLCSIM Advanced was not installed | Complete local PLCSIM download and callback evidence |
| Cleanup | Close protocol, archive ownership, process containment | Direct and archive sessions closed; source files were unlocked; the retrieved workspace was deleted; the worker exited with code 0 | Repeated stress and failure-containment runs |

## Confirmed Real V19 Baseline

The installed V19 validation proved that the binding can:

- resolve and load the documented V19 PublicAPI from a `net48` worker;
- communicate with the `net8.0` facade through the named-pipe protocol;
- open `.ap19` projects and retrieve `.zap19` archives;
- discover multiple PLC targets and require exact selection when ambiguous;
- compile an explicitly selected target and preserve compiler diagnostics;
- release project and archive locks, delete only the owned retrieve workspace,
  dispose the TIA Portal session, and terminate the worker cleanly.

One direct-project close took about 35 seconds. This provided the evidence for
the V19-specific two-minute worker-cleanup timeout. Caller cancellation remains
authoritative and can end cleanup earlier.

## Current Safety-program Boundary

The binding now detects Safety-program content and returns `NotSupported`
before authorization, build, or transfer. A standard program on an F-capable
CPU remains eligible. This ordering and classification have deterministic test
coverage; detection has not been validated with an installed V19 Safety
project and must remain recorded as unverified real-runtime behavior.

## Deferred Validation

The following checks remain open:

- transfer/download to PLCSIM Advanced, including route and callback evidence;
- real cancellation during project open, build, and transfer;
- repeated session/resource stress and forced cleanup failure containment;
- protected projects, credentials, automatic upgrade, and attach mode, which
  are outside the current scope.

Do not convert deterministic transfer coverage into a real V19 transfer support
claim. Machine-specific paths and validation transcripts belong in issue notes
or temporary handoff artifacts, not in this document.
