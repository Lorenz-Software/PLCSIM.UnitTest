# TIA Openness V20 Opt-In Validation

This document describes manual, opt-in validation for the Siemens TIA Openness
V20 binding. It is not part of default CI because it requires installed Siemens
software, local licenses, a suitable project, and an interactive Windows user
session.

## Prerequisites

- TIA Portal V20 with TIA Openness installed.
- The interactive Windows user is a member of the `Siemens TIA Openness`
  Windows group and has logged on again after group membership was granted.
- TIA Openness authorization prompts can be confirmed interactively.
- A direct `.ap20` project or a `.zap20` archive containing at least one
  supported PLC software target.
- A compatible PLCSIM Advanced runtime for transfer validation.

Test projects must stay local or be provided as dedicated test resources.
Licensed, customer-specific, or machine-specific TIA projects must not be
committed to this repository.

## Deterministic Verification

From the repository root:

```powershell
dotnet test access-profiles\siemens\tia-openness\v20\PLCSIM.UnitTest.Siemens.TiaOpenness.V20.sln
```

The V20-owned deterministic suite validates:

- package, manifest, registry, PublicAPI, and readiness handling;
- the `net48` worker process host and named-pipe protocol boundary;
- direct-project and archive request validation and archive workspace ownership;
- PLC target discovery, stable target ids, selection, and ambiguity handling;
- build and transfer request/result mapping;
- cancellation, invalidation, cleanup, and worker error evidence;
- compatibility with the monolithic V20 `Siemens.Engineering.dll` API surface.

These tests use controlled evidence sources and worker/session doubles. They do
not open TIA Portal, read the developer machine registry, require Siemens
authorization, or prove a real download to PLCSIM Advanced.

## Validation Matrix

| Area | Deterministic verification | Real V20 evidence | Remaining real-runtime validation |
| --- | --- | --- | --- |
| Environment | Registry mapping, assembly identity, package shape, missing and inaccessible evidence | `SOFTWARE\Siemens\Automation\Openness\20.0\PublicAPI\20.0.0.0` resolved `Siemens.Engineering.dll` version `20.0.0.0` from the installed V20 PublicAPI directory | Service-pack and update variants beyond the observed installation |
| Authorization | Worker startup and bounded startup-error mapping | Missing Windows-group membership was classified from worker evidence; project access succeeded after membership and a new logon | Denied authorization prompt and unattended authorization setup |
| Project open | `.ap20` and `.zap20` request validation, archive retrieval ownership, cleanup paths | A `.zap20` archive was retrieved and opened by the worker; product and project versions were reported as 20 | Direct `.ap20` open, explicit project-lock checks, and repeated archive cleanup |
| Target discovery | Hierarchy traversal, PLC filtering, stable typed ids, exact selection, ambiguity and not-found results | Two PLC targets were discovered; ambiguity without `plcRef.id` was rejected; explicit id selection succeeded | Additional grouped project hierarchies and non-PLC device mixes |
| Build | Success, warning, error, evidence, and worker-failure mapping | The selected PLC target compiled successfully with zero warnings and zero errors | Real warning/error variants and cancellation during compilation |
| Transfer | Typed route validation, callback/result mapping, bounded evidence | Not validated because the V20 validation machine has no PLCSIM Advanced installation | Complete download through the local `PN/IE` and `PLCSIM` route, including callbacks and final target evidence |
| Cleanup | Close protocol, process-host cleanup, archive ownership boundaries | The validated engineering session closed cleanly after discovery and build | Repeated open/close, worker-process observation, project-lock release, and failure containment |

## Confirmed Real V20 Baseline

The following behavior has been exercised against an installed TIA Portal V20
PublicAPI rather than inferred from V21 or from deterministic doubles:

- readiness resolved the documented V20 registry entry and monolithic
  `Siemens.Engineering.dll` installation;
- the `net48` worker started and communicated with the `net8.0` facade over the
  named-pipe protocol;
- missing `Siemens TIA Openness` group membership produced actionable bounded
  worker evidence;
- a `.zap20` archive was retrieved and opened without project upgrade;
- multiple PLC targets were discovered and an ambiguous request was rejected;
- an explicitly selected PLC target was compiled successfully;
- the engineering session closed after validation.

This baseline proves project access, discovery, selection, and compilation for
the tested V20 environment. It does not prove transfer support or complete V1
runtime integration.

## Current Safety-program Boundary

The binding now detects Safety-program content and returns `NotSupported`
before authorization, build, or transfer. A standard program on an F-capable
CPU remains eligible. This ordering and classification have deterministic test
coverage; detection has not been validated with an installed V20 Safety
project and must remain recorded as unverified real-runtime behavior.

## Deferred Validation

The following checks remain deliberately open until a suitable V20 environment
or a release-hardening need is available:

- transfer/download to PLCSIM Advanced and its route/callback evidence;
- direct `.ap20` project open;
- real cancellation and retry after worker dispatch;
- repeated session lifetime, resource, worker-exit, and project-lock checks;
- real compiler warning/error classifications;
- protected projects, credentials, automatic upgrade, and attach to an
  existing TIA Portal process, which are outside the current scope.

Do not convert these open checks into support claims. A successful deterministic
test only proves the binding-side contract and mapping for that path.

## Evidence To Record

For each later real-runtime validation, record in the requesting issue or
handoff note:

- TIA Portal product version, PublicAPI registry key, and assembly path;
- source kind and sanitized project identity;
- selected `plcRef.id`;
- build status, warning count, error count, and diagnostics;
- for transfer, the runtime target and resolved route evidence;
- worker-log path and whether startup, operation, cleanup, and shutdown are
  present;
- whether an authorization prompt appeared;
- whether the project lock and worker process were released.

Durable repository documentation describes the method and verified boundary.
Machine-specific paths and individual validation transcripts belong in issue
notes or temporary handoff artifacts.
