# Siemens PLCSIM Advanced V8 Binding

This package supplies local PLCSIM Advanced 8.0 runtime access and UnitTest
communication to the Siemens access profile. It is an internal technology
binding, not an access profile that applications select directly.

## Supported Behavior And Limits

- Discover and select one local simulated PLC instance; attach through
  `UseExisting` or create/release an owned instance through `CreateOwned`.
- Observe runtime state, request PLC start/stop, prepare UnitTest communication,
  and write RunControl Start/Abort/Reset requests.
- Consume and release bounded contiguous EventBuffer prefixes using polling or
  managed `plcCycle` waits. Unsupported sync-point setup fails explicitly;
  there is no automatic fallback to polling.
- Preserve external instance ownership through communication revalidation and
  explicit runtime reconnect.
- Target the supported S7-1500 family. Remote Runtime API connections are not
  supported.

Write acceptance is separate from observed PLC reaction and test results.
`UseExisting` never grants permission to unregister/delete the user's instance.
Failed owned-runtime preparation can leave a resource requiring manual recovery
when rollback cannot be proven; details belong to the
[worker implementation](docs/implementation.md#failed-owned-runtime-preparation).

## Prerequisites And Deployment

Real operation requires installed, licensed PLCSIM Advanced V8 and the matching
Runtime API. The package contains a .NET 8 facade and .NET Framework 4.8 x64
worker; Siemens API DLLs are loaded from the installed product, not redistributed.
Use [prerequisites and troubleshooting](docs/troubleshooting.md) for discovery
failures and runtime setup.

Deploy the binding below the Siemens profile's `lib/bindings` directory, then
register the profile for the execution account. The profile selects the matching
package from registration evidence; WPF/CLI do not select this assembly directly.
See [Siemens deployment](../../docs/deployment.md).

## Verification

From the repository root:

```powershell
dotnet build access-profiles\siemens\plcsim-advanced\v8\PLCSIM.UnitTest.Siemens.PlcsimAdvanced.V8.sln
dotnet test access-profiles\siemens\plcsim-advanced\v8\PLCSIM.UnitTest.Siemens.PlcsimAdvanced.V8.sln
```

The default tests use a test-owned validation worker and require no Siemens
installation. They cover package shape, IPC, runtime/communication sessions,
error containment and FIFO processing without certifying native API behavior.
The validation worker is not deployed in the production package.

Building the binding project directly excludes that test artifact:

```powershell
dotnet build access-profiles\siemens\plcsim-advanced\v8\src\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.csproj --configuration Release
```

For a local application test, explicitly compose the desired built packages:

```powershell
.\tools\deploy-debug-plugins.ps1 `
  -Configuration Debug `
  -Composition Real `
  -TiaOpennessVersions v21 `
  -PlcsimAdvancedVersions v8
```

This example replaces the WPF/CLI profile roots with that composition. A normal
application build does not redeploy packages. `DeployPluginPackage` includes
the facade, production worker, manifest and production dependencies only.

## Validation And Performance

Bounded native checks cover attached/owned lifecycle, polling and PLC-cycle
communication, repeated runs, Abort, recovery and session lifetime. Their exact
fixtures, opt-in commands and limits are in
[binding validation](docs/validation.md) and the
[Abort harness](tests/abort/README.md). These operations can start/stop or
create/release test resources; inspect the owning procedure before enabling
them. They are not normal offline tests.

No universal leak-freedom, sustained-throughput or real failure-injection
guarantee follows from the bounded checks. The
[performance guide](docs/performance/README.md) owns current measurements and
recommendations; [performance harness instructions](tests/performance/README.md)
own reproduction. Authoring defaults and profile fields belong to
[Siemens configuration](../../docs/configuration.md).

## Implementation And Contracts

- [Facade/worker ownership, compensation and logging](docs/implementation.md).
- [Production versus validation worker](tests/worker/README.md).
- [PLCSIM binding contract](../docs/contracts/plcsim-advanced-binding-contract.md).
- [Workload sizing](../docs/performance/event-stream-workload-sizing.md).
- [Shared runtime contract](../../../../docs/contracts/access-profile-runtime-contract.md).
