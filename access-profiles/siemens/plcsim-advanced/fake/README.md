# Fake PLCSIM Advanced Binding

This is the deterministic, no-Siemens runtime/communication binding for the
Siemens profile. It implements `IPlcsimAdvancedRuntimeBinding` and
`IPlcsimUnitTestCommunicationBinding` inside the normal binding package boundary.
It is not the application's generic Fake access profile and does not load the
real PLCSIM Runtime API.

## What It Exercises

The in-process fake supplies runtime discovery/preparation, existing/owned
resource behavior, start/stop/release, communication preparation, RunControl
writes and later observations, and EventBuffer batches/release state. Controlled
scenarios cover success, rejection, failure, cancellation and ambiguous evidence.

The parameterless manifest entry point uses default successful behavior.
Binding-owned tests inject internal `FakePlcsimBehavior` and a `TimeProvider`
to exercise delayed reactions, health invalidation, pending release, loss and
inconsistent snapshots. These are test controls, not user configuration fields
or application-facing contracts.

## Package And Composition

Deploy the package with the Siemens profile below
`lib/bindings/<package>/manifest.json`. Registration and selection use the same
manifest/readiness path as production bindings. The declaration advertises fake
API/runtime evidence and compatible fake project input; it does not impersonate
an installed Siemens product.

Applications and Shared Core consume the profile's neutral services, not fake
scenario internals. Siemens shell tests may stage/register/load the package;
direct construction and internal behavior injection remain test composition.

## Build And Test

From the repository root, without Siemens software:

```powershell
dotnet build access-profiles\siemens\plcsim-advanced\fake\PLCSIM.UnitTest.Siemens.PlcsimAdvanced.Fake.sln
dotnet test access-profiles\siemens\plcsim-advanced\fake\tests\PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.Fake.csproj
```

[FakePlcsimAdvancedBinding.cs](src/PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.Fake/FakePlcsimAdvancedBinding.cs)
owns the implementation and internal scenario types;
[binding tests](tests/FakePlcsimAdvancedBindingTests.cs) show concrete composition
and assertions.

## Verification Boundary

This package verifies deterministic contract-level behavior and Siemens shell
composition. It does not qualify native API loading, real PLC reads/writes,
notification timing, PLC-cycle synchronization or production IPC performance.
It is not a worker-process simulator: process/pipe crash, malformed-response and
transport-containment checks belong to the real version binding's test-owned
validation worker.

See the [PLCSIM binding contract](../docs/contracts/plcsim-advanced-binding-contract.md)
for release/evidence semantics and the
[Siemens implementation overview](../../docs/architecture/implementation/profile-composition.md)
for composition and ownership.
