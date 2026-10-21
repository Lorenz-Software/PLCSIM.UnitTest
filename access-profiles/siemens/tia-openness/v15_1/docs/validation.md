# TIA Openness V15.1 Opt-In Validation

This document describes manual, opt-in validation for the Siemens TIA Openness
V15.1 binding. It is not part of default CI because it requires installed
Siemens software, local licenses, a suitable project, and an interactive
Windows user session.

## Prerequisites

- TIA Portal V15.1 with TIA Openness installed.
- The interactive user is a member of the `Siemens TIA Openness` Windows
  group and has logged on again after membership was granted.
- TIA Openness authorization prompts can be confirmed interactively.
- A direct `.ap15_1` project or `.zap15_1` archive containing a supported
  non-failsafe PLC software target.
- A compatible local simulation target for transfer validation.

Test projects must remain local or be dedicated test resources. Licensed,
customer-specific, or machine-specific projects must not be committed.

## Deterministic Verification

From the repository root:

```powershell
dotnet test access-profiles\siemens\tia-openness\v15_1\PLCSIM.UnitTest.Siemens.TiaOpenness.V15_1.sln
```

The V15.1-owned deterministic suite validates package and manifest
shape, registry and PublicAPI handling, the net48 worker protocol, project
requests, archive ownership, target selection, build and transfer mapping,
cancellation, diagnostics, and cleanup. These tests do not start TIA Portal or
perform a real download.

## Validation Matrix

| Area | Deterministic verification | Real V15.1 evidence | Remaining validation |
| --- | --- | --- | --- |
| Environment | Registry mapping, assembly identity, package shape, missing and inaccessible evidence | Native registry API `15.1.0.0` resolved the installed V15.1 `Siemens.Engineering.dll`; the `net48` worker loaded it successfully | Additional V15.1 update variants |
| Authorization | Worker startup and bounded startup-error mapping | The authorized interactive user opened projects through the deployed production worker | Denied-prompt and unattended setup variants |
| Project open | `.ap15_1` and `.zap15_1` request validation and archive ownership | Direct project open and archive retrieve/open succeeded; product and project versions were 15.1 | Protected projects and additional project structures |
| Target discovery | Hierarchy traversal, filtering, stable ids, exact selection, ambiguity and not-found results | Grouped and ungrouped targets were discovered; ambiguity and explicit selection behaved correctly | Additional non-PLC device mixes |
| Build | Success, warning, error, evidence, and worker-failure mapping | A non-failsafe target compiled successfully with zero warnings and errors; native V15.1 explicitly rejected failsafe compilation | Real cancellation and additional compiler-error variants |
| Transfer | Typed route and callback/result mapping | Not tested because no suitable simulation target was installed | Complete local simulation download and callback evidence |
| Cleanup | Close protocol, archive ownership, process containment | Direct and archive sessions closed; retrieved workspace deletion and worker exit completed; post-build cleanup took about 18-23 seconds | Repeated stress and forced cleanup failure containment |

## Confirmed Real V15.1 Baseline

The installed V15.1 validation proved that the binding can:

- resolve and load the native V15.1 PublicAPI from a `net48` worker;
- communicate with the `net8.0` facade through the named-pipe protocol;
- open `.ap15_1` projects and retrieve `.zap15_1` archives;
- discover PLC targets and require exact selection when ambiguous;
- compile a selected non-failsafe target and preserve compiler evidence;
- release project and archive locks, delete only the owned retrieve workspace,
  dispose the TIA Portal session, and terminate the worker cleanly.

After compilation, V15.1 required about 18 seconds to close a direct project
and about 23 seconds to close a retrieved archive. This provides the evidence
for the V15.1-specific two-minute worker-cleanup timeout. Caller cancellation
remains authoritative and can end cleanup earlier.

## Native V15.1 Limitation

TIA Openness V15.1 reports failsafe device compilation as unsupported. This is
a native API limitation rather than a binding failure. The current support
claim is limited to non-failsafe PLC software.

The V15.1 API also does not expose the later project property used by newer TIA
versions to enable simulation during block compilation. Projects and blocks
used for simulation must therefore already be configured and eligible for
simulation.

## Current Safety-program Boundary

The binding now detects Safety-program content and returns `NotSupported`
before authorization, build, or transfer. A standard program on an F-capable
CPU remains eligible. The native compiler limitation above remains historical
real evidence. The current detection ordering and classification have
deterministic test coverage; detection has not been validated with an installed
V15.1 Safety project and must remain recorded as unverified real-runtime
behavior.

## Deferred Validation

The following checks remain open:

- transfer/download to a compatible local simulation target;
- real cancellation during project open, build, and transfer;
- repeated session/resource stress and forced cleanup failure containment;
- protected projects, credentials, automatic upgrade, and attach mode, which
  are outside the current scope.

Do not convert deterministic transfer coverage into a real V15.1 transfer
support claim. Machine-specific paths and validation transcripts belong in
issue notes or temporary handoff artifacts, not in this document.
