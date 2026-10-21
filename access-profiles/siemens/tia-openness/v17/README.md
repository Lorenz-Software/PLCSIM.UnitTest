# Siemens TIA Openness V17 Binding

This subtree contains the independently buildable TIA Openness V17 binding for
the Siemens access profile.

The package consists of a `net8.0` facade and a `.NET Framework 4.8` worker.
The facade owns package validation, installed-environment probing, worker
startup, and the named-pipe session boundary. The worker loads the installed
V17 PublicAPI, owns Siemens API objects, opens or retrieves projects, discovers
PLC targets, compiles the selected target, and implements the transfer command
boundary. Siemens assemblies are neither copied into the package nor referenced
by the application.

The V17 PublicAPI can be loaded by the `net48` worker. A separate `net47`
worker or .NET Framework 4.7 installation is not required.

## Before Real Operation

Install the matching TIA Portal/PublicAPI and authorize the Openness worker for
the execution account. Deploy this binding with the Siemens profile and register
the profile separately; see [deployment and authorization](../../docs/deployment.md).
Offline builds/tests do not require that vendor installation. Follow this
binding's [troubleshooting](docs/troubleshooting.md) and
[validation scope](docs/validation.md) before claiming a real engineering path.

## Implemented Boundary

The V17 binding implements:

- readiness detection through the installed V17 Openness registry and
  monolithic `Siemens.Engineering.dll` PublicAPI layout;
- direct `.ap17` project open;
- `.zap17` archive retrieval without automatic upgrade;
- PLC target discovery and exact neutral target-id selection;
- Safety-program content inspection with fail-fast unsupported classification;
- build/compile dispatch for the selected PLC software target;
- the typed local-PLCSIM transfer request and result boundary;
- bounded diagnostics, worker logging, cancellation handling, and cleanup.

The binding is selected through the Siemens profile's normal package discovery
and capability matching. The application and Siemens profile shell have no
direct V17 project reference.

## Validation Status

The deterministic V17 suite exercises package, readiness, protocol,
project/archive, discovery, build, transfer mapping,
cancellation, and cleanup boundaries without installed Siemens software.

Real TIA Portal V17 validation has confirmed:

- installed registry and PublicAPI resolution for API version `17.0.0.0`;
- authorization and `net48` worker communication over the named pipe;
- direct `.ap17` open and `.zap17` retrieval;
- grouped PLC target discovery and exact neutral target-id selection;
- dispatch to the native V17 compiler provider;
- direct-project lock release and clean engineering-session shutdown.

The historical validation project contains a password-protected Safety program.
Before the current fail-fast guard was introduced, native V17 compilation
reached the Siemens Safety authorization boundary and stopped because no login
had been established. That result remains compiler-dispatch evidence; current
managed project handling rejects detected Safety content before compilation.

Measured engineering-session cleanup completed between about 2.5 and 5.4
seconds for the validated direct and archive runs.

Real transfer/download was not tested because the available V17 validation
machine did not contain PLCSIM Advanced. The binding is real-runtime validated
for project access, discovery, native build dispatch, and cleanup, but not for
a completed non-Safety build or transfer.

See `docs/validation.md` for the detailed evidence boundary.

## Current Limits

- Automatic project upgrade is not supported.
- Attach to an existing TIA Portal process is not supported.
- Targets containing Safety-program content are rejected before authorization,
  build, or transfer. This behavior has deterministic coverage but has not been
  validated with an installed V17 Safety project. Standard programs on
  F-capable CPUs remain supported.
- UMAC, PLC access passwords, generic protected-project secrets, and credential
  storage are outside the current binding scope.
- Real V17 transfer through PLCSIM Advanced remains unverified.
- A completed real build of a non-Safety V17 UnitTest project remains
  unverified.
- Deterministic tests do not substitute for installed V17 and PLCSIM Advanced
  integration tests.

## Worker Error Cleanup

Terminal initialization-error replies (including authorization probes) allow
up to two seconds for normal worker exit and asynchronous log flushing before
forced termination. Caller cancellation still terminates the owned worker and
propagates cancellation. This does not change successful session handling or
the original initialization diagnostic; a hung worker can still lose tail logs.

## Verification
From the repository root:

```powershell
dotnet build access-profiles\siemens\tia-openness\v17\PLCSIM.UnitTest.Siemens.TiaOpenness.V17.sln
dotnet test access-profiles\siemens\tia-openness\v17\PLCSIM.UnitTest.Siemens.TiaOpenness.V17.sln
```

The solution is independent of the other TIA Openness binding projects.
Building the application does not require TIA Portal V17 or its PublicAPI.

## Related Documentation

- TIA Openness binding contract:
  `../docs/contracts/tia-openness-binding-contract.md`
- V17 validation evidence and deferred checks:
  `docs/validation.md`
- V17 prerequisites and troubleshooting:
  `docs/troubleshooting.md`
