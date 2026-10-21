# Siemens Access Profile

The Siemens access profile is the Siemens-specific integration layer for TIA
Portal / TIA Openness engineering work and PLCSIM Advanced runtime / UnitTest
communication.

This subtree contains the Siemens profile shell, Siemens-internal binding
contract assembly, profile-owned tests, profile-owned solution files, and
Siemens-internal technology binding families.

## Use The Profile

Choose this profile in WPF or identify it in a CLI configuration, then select the
project/PLC and runtime targets. [Configuration](docs/configuration.md) owns all
profile values and runtime-mode requirements. The application does not need
Siemens API types or choose internal binding assemblies.

Real operation needs an installed, usable PLCSIM Advanced product and a
compatible runtime binding. Managed build/download additionally needs matching
TIA Portal/Openness and an authorized engineering worker. The selected PLC
program must publish the expected UnitTest contract. Install/deploy packages and
register capabilities before operation; registration is not proof of live PLC
readiness or permission to modify an unrelated runtime.

## Supported Behavior And Limits

The profile supports externally managed project handling through a usable
PLCSIM binding and application-managed direct or archive project handling when
a matching TIA Openness binding is also available. One selected PLC target and
one selected simulated runtime target form the active UnitTest context.

Project-target discovery returns supported candidates independently of the
configured `plcRef.id`; the application validates that selection against the
candidate list. No supported targets is a successful empty discovery, not a
worker failure. The profile adapter separates the binding's unselected-target
result from actual discovery failures. Preparation and final target resolution
still validate the configured PLC reference before build or transfer.

Attachment to an existing TIA Portal engineering session is not supported. The
application-managed path creates and owns its engineering session; externally
managed project handling does not implicitly attach to an interactive TIA
Portal process. Concrete project formats, installed product versions, API
restrictions, transfer routes, observation strategies, and real-runtime
validation limits are owned and documented by the selected binding package
rather than duplicated here.

## Deployment

Deploy the profile and required technology binding packages independently. Real
TIA Openness workers must be authorized from an interactive Windows session
before unattended CLI use, and per-user application profile registration remains
a separate provisioning step. The complete version-neutral handoff is described
in `docs/deployment.md`.

## Ownership

The profile shell owns Siemens profile configuration, binding discovery,
registration, profile-owned registry state, selected binding composition,
application-facing service adapters, diagnostics, health fan-out, cleanup, and
resource lifetime across the composed Siemens services.

The deployed Siemens package root is immutable and remains the source for
binding discovery and package loading. Registration stores derived Siemens
binding/environment evidence in `profile-registry.json` below the writable
`ProfileDataDirectory` supplied by the host, never below the package root.

The profile shell must not directly reference concrete TIA Openness or PLCSIM
Advanced version projects. It composes selected bindings through the
Siemens-internal contracts.

Siemens-internal technology bindings live below the Siemens subtree and are
documented by their versions-neutral technology README files.

## Verification

From the repository root:

```powershell
dotnet build access-profiles\siemens\PLCSIM.UnitTest.Siemens.Contracts.sln
dotnet test access-profiles\siemens\PLCSIM.UnitTest.Siemens.Profile.sln
```

Concrete binding build and test commands belong to the README in the owning
binding subtree.

## Opt-In Real Profile Validation

The default profile tests use fake bindings and do not require Siemens software.
The existing opt-in integration test loads the deployed Siemens profile package
and production bindings, then repeats externally managed and application-managed
session lifetimes:

```powershell
$env:PLCSIM_UNITTEST_VALIDATE_SIEMENS_PROFILE_LIFETIMES='1'
$env:PLCSIM_UNITTEST_SIEMENS_REAL_PROFILE_ROOT='D:\deploy\profiles'
$env:PLCSIM_UNITTEST_SIEMENS_PROJECT='D:\Projects\sample.ap21'
$env:PLCSIM_UNITTEST_SIEMENS_RUNTIME_INSTANCE='PlcUnderTest'
dotnet test access-profiles\siemens\tests\PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.csproj --filter Siemens_profile_can_validate_real_repeated_managed_and_external_session_lifetimes_when_enabled
```

The deployed root, authorized TIA worker, installed products, project, and
user-owned PLCSIM instance must already be prepared. The test supplies bounded
cross-binding lifetime evidence; it does not replace the deeper opt-in
validation owned by each concrete binding.

## Related Documentation

- Siemens profile configuration authoring:
  `docs/configuration.md`
- Siemens deployment and TIA Openness authorization:
  `docs/deployment.md`
- Siemens profile troubleshooting and log routing:
  `docs/troubleshooting.md`
- Architecture structure:
  `../../docs/architecture/implementation/090-structure.md`
- Siemens profile architecture and resource lifetime:
  [Profile architecture](docs/architecture/access-profile.md)
- Siemens implementation and code owners:
  [Profile implementation](docs/architecture/implementation/profile-composition.md)
- Shared access-profile package contract:
  `../../docs/contracts/access-profile-package-contract.md`
- Siemens binding-family contracts:
  owning technology subtree `docs/` directories
