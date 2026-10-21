# Siemens TIA Openness V20 Binding

This subtree contains the independently buildable TIA Openness V20 binding for
the Siemens access profile.

The package consists of a `net8.0` facade and a `.NET Framework 4.8` worker.
The facade owns package validation, installed-environment probing, worker
startup, and the named-pipe session boundary. The worker loads the installed
V20 PublicAPI, owns Siemens API objects, opens or retrieves projects, discovers
PLC targets, compiles the selected target, and implements the transfer command
boundary. Siemens assemblies are not copied into the package or referenced by
the application.

The binding is selected through the Siemens profile's normal binding discovery
and capability matching. The application and Siemens profile shell do not
contain a direct V20 project reference.

## Before Real Operation

Install the matching TIA Portal/PublicAPI and authorize the Openness worker for
the execution account. Deploy this binding with the Siemens profile and register
the profile separately; see [deployment and authorization](../../docs/deployment.md).
Offline builds/tests do not require that vendor installation. Follow this
binding's [troubleshooting](docs/troubleshooting.md) and
[validation scope](docs/validation.md) before claiming a real engineering path.

## Implemented Boundary

The V20 binding implements:

- readiness detection through the installed V20 Openness registry and
  monolithic `Siemens.Engineering.dll` PublicAPI layout;
- direct `.ap20` project requests;
- `.zap20` archive retrieval without automatic upgrade;
- PLC target discovery and exact neutral target-id selection;
- Safety-program content inspection with fail-fast unsupported classification;
- build/compile of the selected PLC software target;
- the existing typed local-PLCSIM transfer request and result boundary;
- bounded diagnostics, worker logging, cancellation handling, and cleanup.

These are implemented capabilities, not equal claims of real-runtime coverage.
The current real V20 validation is intentionally limited.

## Validation Status

The deterministic V20 suite exercises package, readiness, worker protocol,
project/archive, discovery, build, transfer mapping, cancellation, and cleanup
boundaries without installed Siemens software.

Real TIA Portal V20 validation has confirmed:

- installed registry/PublicAPI resolution;
- authorization enforcement through the `Siemens TIA Openness` Windows group;
- `net48` worker startup and named-pipe communication;
- `.zap20` retrieval and project open;
- discovery and explicit selection from multiple PLC targets;
- successful compilation with zero warnings and zero errors;
- engineering-session close.

Real transfer/download was not tested because the V20 validation environment
did not contain PLCSIM Advanced. Direct `.ap20` open, real cancellation/retry,
repeated lifetime/resource checks, and explicit project-lock verification also
remain unperformed. The binding must therefore be treated as partially
real-runtime validated rather than release-proven V20 support.

The detailed evidence boundary and deferred checklist are documented in
`docs/validation.md`.

## Current Limits

- Automatic project upgrade is not supported.
- Attach to an existing TIA Portal process is not supported.
- Targets containing Safety-program content are rejected before authorization,
  build, or transfer. This behavior has deterministic coverage but has not been
  validated with an installed V20 Safety project. Standard programs on
  F-capable CPUs remain supported.
- UMAC, PLC access passwords, generic protected-project secrets, and credential
  storage are outside the current scope.
- Only the existing local PLCSIM transfer route is represented by the binding
  contract; its real V20 execution remains unverified.
- Deterministic tests do not substitute for installed V20 and PLCSIM Advanced
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
dotnet build access-profiles\siemens\tia-openness\v20\PLCSIM.UnitTest.Siemens.TiaOpenness.V20.sln
dotnet test access-profiles\siemens\tia-openness\v20\PLCSIM.UnitTest.Siemens.TiaOpenness.V20.sln
```

The V20 solution is independent of the V21 binding project. Building the
application does not require TIA Portal V20 or its PublicAPI assemblies.

## Related Documentation

- TIA Openness binding contract:
  `../docs/contracts/tia-openness-binding-contract.md`
- V20 opt-in validation and current evidence:
  `docs/validation.md`
- V20 prerequisites and troubleshooting:
  `docs/troubleshooting.md`
