# Siemens TIA Openness V21 Binding

This binding supplies managed engineering for Siemens V21 projects to the
Siemens access profile: open/retrieve a project, select its PLC software target,
compile it and transfer it to a prepared local PLCSIM Advanced destination.
It is an internal binding package, not a directly selected application profile.
A .NET 8 facade owns a .NET Framework 4.8 worker; Siemens API objects stay inside
that worker process.

## Before Real Operation

Install the matching TIA Portal/PublicAPI and authorize the Openness worker for
the execution account. Deploy this binding with the Siemens profile and register
the profile separately; see [deployment and authorization](../../docs/deployment.md).
Offline builds/tests do not require that vendor installation. Follow this
binding's [troubleshooting](docs/troubleshooting.md) and
[validation scope](docs/validation.md) before claiming a real engineering path.

## Supported Behavior

The implemented V21 path supports:

- read-only environment/readiness detection from the documented V21 Openness
  registry and PublicAPI layout;
- direct `.ap21` project sessions;
- `.zap21` retrieval through `ProjectComposition.Retrieve` without automatic
  upgrade, with binding-owned workspace cleanup;
- PLC target discovery through the opened project hierarchy;
- exact neutral target-id selection;
- Safety-program content inspection with fail-fast unsupported classification;
- build/compile of the selected PLC software target;
- transfer/download to a prepared local PLCSIM Advanced runtime destination;
- bounded diagnostics, evidence, worker logging, and cleanup.

The current target policy treats project items exposing `PlcSoftware` as the
supported S7-1500 target family. PLC-family classification is binding-local;
the Siemens profile shell and application do not interpret Siemens device
types.

## Current Limits

- Attachment to an existing TIA Portal session is not supported. V21 owns and
  starts its TIA engineering session.
- Automatic project upgrade is not supported. Upgrade-required sources fail
  without changing the source project or archive.
- Targets containing Safety-program content are rejected before authorization,
  build, or transfer because the managed online transfer path cannot load the
  Safety program. Standard programs on F-capable CPUs remain supported.
- UMAC, PLC access passwords, generic protected-project secrets, and credential
  storage are not supported.
- Transfer is limited to the implemented local PLCSIM route. Non-local transfer
  routes are outside the current local transfer path.
- The repeated-lifetime test is bounded resource evidence, not a sustained
  performance soak or a universal leak-freedom claim.

## Verification
From the repository root:

```powershell
dotnet build access-profiles\siemens\tia-openness\v21\PLCSIM.UnitTest.Siemens.TiaOpenness.V21.sln
dotnet test access-profiles\siemens\tia-openness\v21\tests\PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V21.csproj
```

The deterministic V21 tests do not require installed Siemens software or TIA
firewall authorization. Real TIA Openness V21 validation remains opt-in. The
validation matrix, current real-runtime baseline, and evidence expectations are
documented in `docs/validation.md`.

Repeated session-lifetime validation is opt-in separately:

```powershell
$env:PLCSIM_UNITTEST_VALIDATE_TIA_OPENNESS_V21_LIFETIMES='1'
$env:PLCSIM_UNITTEST_TIA_OPENNESS_V21_PROJECT='D:\Projects\sample.ap21'
dotnet test access-profiles\siemens\tia-openness\v21\tests\PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V21.csproj --filter V21_binding_can_validate_real_repeated_session_lifetimes_when_enabled
```

The lifetime test performs two warm-up and 24 measured target-discovery cycles
inside one worker-backed engineering session, then opens and closes the same
project in two additional independent sessions. It records worker private memory,
handle count, and thread count, verifies worker termination after every close,
and proves project-lock release by reopening the same project. This is bounded
lifecycle evidence, not a long-running TIA performance soak.

## Internal Organization

The `net8.0` binding owns package validation, archive-workspace policy, worker
startup, named-pipe protocol validation, command dispatch, and the
worker-session handle exposed to the engineering-session facade. These concerns
are separated internally but remain one binding package and one public session
boundary.

The `net48` worker has one process entry point and one serialized session loop.
Project ownership, target discovery, build, transfer, exception mapping,
reflection access, protocol serialization, and logging are worker-internal
components. Siemens API objects never cross the named-pipe boundary. This
organization is an implementation detail: public binding contracts, manifest
shape, command names, payloads, result semantics, and session lifecycle remain
unchanged.

## Engineering And Worker Details

The current project validates package shape, Siemens-internal contract
compatibility, `.ap21`/`.zap21` request validation, a bounded V21 PublicAPI
readiness probe based on Siemens registry evidence plus `net48` split assembly
metadata, and the shell-facing engineering-session host lifecycle boundary.
The binding starts a `.NET Framework 4.8` worker process for the TIA Openness
V21 API-adjacent session lifecycle. The worker opens direct `.ap21` projects or
retrieves `.zap21` archives through the Siemens API, then performs PLC target
discovery inside the opened TIA project context. Retrieved projects use a
unique binding-owned workspace below a configured base or the per-user
temporary base. The worker also compiles the selected PLC target through the Siemens
Openness compiler API and maps compiler state, warning/error counts, bounded
compiler evidence, and compiler warnings/errors to the shell-facing build
result. Compiler information messages remain evidence only. The worker performs
transfer/download to a prepared PLCSIM Advanced runtime destination through the
TIA Openness download provider and maps bounded transfer route evidence,
download configuration choices, download messages, and final transfer state.

The binding facade communicates with the worker over the Siemens named-pipe IPC
boundary. Logging never uses that protocol. The worker writes a dedicated
`access-profile-<profile-id>-tia-openness-v21-worker-<date>.log` family below
the application-owned log directory. It inherits the access-profile minimum
level and bounded file policy, enriches entries with worker instance and process
identity, and uses an asynchronous file sink behind the normal synchronous
`ILogger` facade. Standard error is reserved for bounded fallback evidence when
logger initialization or the worker protocol fails fatally.

For the implemented local transfer path the facade sends typed transfer-route values to the worker:
`PnIe`, `PlcSim`, PLCSIM PC-interface number `1`, and no explicit PLC target
interface name. The worker maps those values to Siemens API connection names
such as `PN/IE` and `PLCSIM`, resolves the first available PLC target interface,
and reports the concrete interface used as evidence.

## Worker Error Cleanup

Terminal initialization-error replies (including authorization probes) allow
up to two seconds for normal worker exit and asynchronous log flushing before
forced termination. Caller cancellation still terminates the owned worker and
propagates cancellation. This does not change successful session handling or
the original initialization diagnostic; a hung worker can still lose tail logs.

## Related Documentation

- TIA Openness binding contract:
  `../docs/contracts/tia-openness-binding-contract.md`
- V21 opt-in validation:
  `docs/validation.md`
- V21 prerequisites and troubleshooting:
  `docs/troubleshooting.md`
