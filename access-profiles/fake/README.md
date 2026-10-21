# Fake Access Profile

The Fake access profile is the vendor-independent development and regression
baseline for PLCSIM.UnitTest.

It implements the public access-profile plugin boundary without requiring TIA
Portal, PLCSIM Advanced, or Siemens API installations. The WPF application and
shared-core tests currently use it to exercise the application workflow through
the same high-level service interfaces that real profiles use.

The Fake profile is a deterministic development and test implementation. It is
not a separate architecture model and does not prove behavior of a real vendor
profile. It is also distinct from fake technology bindings used internally by
vendor profiles.

## Use In Development

Build and explicitly deploy this directory package, then register it with the
application. A normal application build does not deploy profiles. In WPF, select
a sample `.json` project from [FakeProjects](tests/Resources/FakeProjects), choose
the Fake profile and complete the normal setup/run workflow. The
[configuration reference](docs/configuration.md) describes projected test values
and the distinction between `CreateOwned` and `UseExisting` target selection.
These simulated resources never control a real PLC.

For application tests, compose the controllable Fake services or load the package
through the normal host loader. Test-only controls are not production profile
configuration or an alternative application API.

## Ownership

Fake profile-owned sources, implementation tests, documentation, project
sources, and configuration samples live below this subtree. Shared
application-level evidence and conformance utilities stay in
`tests/PLCSIM.UnitTest.Testing`.

The plugin and controllable Fake services are intentionally public for
application test composition. They are a development/test surface, not additions
to the production access-profile contract. Package integration and conformance
tests still load the profile through its manifest and public access-profile
contracts.

The package root is immutable. Fake profile registration stores its derived
`profile-registry.json` in the writable `ProfileDataDirectory` supplied by the
host. Tests inject a temporary data root and must not write profile state into
the package tree or the developer's LocalAppData.

Application integration tests can set the non-projected
`testSettings.safetyAuthorizationRequirement` value to `notRequired`,
`passwordRequired`, or `notSupported`. This is a deterministic test-injection
control for the generic project-authorization workflow. It is not an end-user
profile field and is intentionally absent from the configuration editor and
configuration contract.

## Verification

From the repository root:

```powershell
dotnet build access-profiles\fake\PLCSIM.UnitTest.Fake.Profile.sln
dotnet test access-profiles\fake\PLCSIM.UnitTest.Fake.Profile.sln

# Application workflow coverage that composes or package-loads the Fake profile
dotnet test PLCSIM.UnitTest.sln
```

## Contracts

- [Fake profile configuration contract](docs/contracts/configuration-contract.md).
- [Shared access-profile package contract](../../docs/contracts/access-profile-package-contract.md).
