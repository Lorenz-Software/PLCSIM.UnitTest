# Siemens TIA Openness V15.1 Binding

This subtree contains the independently buildable TIA Openness V15.1 binding
for the Siemens access profile.

The package consists of a `net8.0` facade and a `.NET Framework 4.8` worker.
The facade owns package validation, installed-environment probing, worker
startup, and the named-pipe session boundary. The worker loads the installed
V15.1 PublicAPI, owns Siemens API objects, opens or retrieves projects,
discovers PLC targets, compiles the selected target, and implements the
transfer command boundary. Siemens assemblies are neither copied into the
package nor referenced by the application.

The native V15.1 PublicAPI can be loaded by the `net48` worker. A separate
legacy .NET Framework installation is not required.

## Before Real Operation

Install the matching TIA Portal/PublicAPI and authorize the Openness worker for
the execution account. Deploy this binding with the Siemens profile and register
the profile separately; see [deployment and authorization](../../docs/deployment.md).
Offline builds/tests do not require that vendor installation. Follow this
binding's [troubleshooting](docs/troubleshooting.md) and
[validation scope](docs/validation.md) before claiming a real engineering path.

## Implemented Boundary

The V15.1 binding implements:

- readiness detection through the native V15.1 Openness registry and
  monolithic `Siemens.Engineering.dll` PublicAPI layout;
- direct `.ap15_1` project open;
- `.zap15_1` archive retrieval without automatic upgrade;
- PLC target discovery and exact neutral target-id selection;
- Safety-program content inspection with fail-fast unsupported classification;
- build/compile of the selected PLC software target;
- the typed local-PLCSIM transfer request and result boundary;
- bounded diagnostics, worker logging, cancellation handling, and cleanup.

The binding is selected through the Siemens profile's normal package discovery
and capability matching. The application and Siemens profile shell have no
direct V15.1 project reference.

## Validation Status

The deterministic V15.1 suite exercises package, readiness, protocol,
project/archive, discovery, build, transfer mapping,
cancellation, diagnostics, and cleanup without installed Siemens software.

Real TIA Portal V15.1 validation has confirmed:

- native registry and PublicAPI resolution for API version `15.1.0.0`;
- authorization and `net48` worker communication over the named pipe;
- direct `.ap15_1` open and `.zap15_1` retrieval;
- grouped, ungrouped, unambiguous, and explicit PLC target selection paths;
- successful compilation of a non-failsafe PLC target;
- direct-project and archive session cleanup, archive-workspace deletion, TIA
  Portal disposal, and worker exit.

Historical real validation established that native V15.1 Openness rejects
failsafe device compilation with
`Compiling a failsafe device by openness is not supported.` The current UnitTest
path for this binding is therefore limited to non-failsafe PLC software. The
current binding detects Safety content and stops before invoking compilation.

Post-build cleanup required about 18 seconds for a direct project and about
23 seconds for a retrieved archive. The facade therefore allows up to two
minutes for worker cleanup while still honoring caller cancellation and
higher-level deadlines.

Real transfer/download was not tested because the available V15.1 validation
machine did not contain a suitable PLCSIM or PLCSIM Advanced target. The
binding is real-runtime validated for project access, discovery, non-failsafe
build, and cleanup, but not for transfer.

See `docs/validation.md` for the detailed evidence boundary.

## Current Limits

- Automatic project upgrade is not supported.
- Attach to an existing TIA Portal process is not supported.
- Targets containing Safety-program content are rejected before authorization,
  build, or transfer. This behavior has deterministic coverage but has not been
  validated with an installed V15.1 Safety project. Standard programs on
  F-capable CPUs remain supported.
- Native V15.1 failsafe device compilation remains unsupported.
- UMAC, PLC access passwords, generic protected-project secrets, and credential
  storage are outside the current scope.
- Real V15.1 transfer remains unverified.
- Deterministic tests do not substitute for installed V15.1 and PLCSIM
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
dotnet build access-profiles\siemens\tia-openness\v15_1\PLCSIM.UnitTest.Siemens.TiaOpenness.V15_1.sln
dotnet test access-profiles\siemens\tia-openness\v15_1\PLCSIM.UnitTest.Siemens.TiaOpenness.V15_1.sln
```

The solution is independent of the other TIA Openness binding projects.
Building the application does not require TIA Portal V15.1 or its PublicAPI.

## Related Documentation

- TIA Openness binding contract:
  `../docs/contracts/tia-openness-binding-contract.md`
- V15.1 validation evidence and deferred checks:
  `docs/validation.md`
- V15.1 prerequisites and troubleshooting:
  `docs/troubleshooting.md`
