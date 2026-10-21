# Profile Packaging

## Package Boundary

| Property | Value |
| --- | --- |
| Builder selector | `BeckhoffTc34026Profile` |
| Generic package family | `access-profiles.beckhoff-tc3-4026` |
| Profile/package version | `1.0.0` |
| Payload / metadata | `profile.zip` / `package-manifest.json` |
| Runtime profile contract | `2.0.0` |
| Installer component | `profile.beckhoff-tc3-4026`, independently selectable, selected by default |
| Installed destination | `profiles/beckhoff-tc3-4026` |

The integrated profile has no binding children and no profile worker. The
application supplies the host runtime; installed TwinCAT XAE/Usermode components
and usable licenses remain external prerequisites, checked by profile registration
and the requested operations. `runtimeRequirements` in the release manifest does
not introduce a new installer-specific TwinCAT prerequisite detector.

The ZIP includes the runtime manifest, profile assembly/dependency metadata,
matching shared contract artifacts, the locked ADS dependency closure (including
Windows runtime assets), README, profile documentation/examples, project license
and generated third-party notices. It must not include installed XAE interop,
test/fake assemblies, raw evidence, logs, PDBs, user settings or registrations.

## Build and Verify

From the repository root, with the installed engineering interop prerequisites
described in [dependencies](dependencies.md):

```powershell
.\tools\build-release-package.ps1 -Package BeckhoffTc34026Profile -Version 1.0.0
.\access-profiles\beckhoff-tc3-4026\test-release-package.ps1 `
    -PackageDirectory .\artifacts\release-packages\access-profiles.beckhoff-tc3-4026\1.0.0
.\tools\test-third-party-notices.ps1
```

The builder uses the existing `DeployPluginPackage` target and requires a locked
restore for this profile. A changed dependency graph must be reviewed rather than
silently updating `packages.lock.json` during release packaging. Required installed
Visual Studio interop is referenced, not copied or embedded. Beckhoff COM is
late-bound, without a managed wrapper reference or generated interface metadata.
The release builder rejects known installed interop DLLs in its input tree;
package tests additionally inspect the profile's assembly metadata.

The profile checker resolves the package through the ordinary registry integrity
validator using a local package root, then verifies ZIP contents, contract hashes,
the 13-package lock/assets match, all dependency attributions, Windows assets and
regenerated notices. It does not register profiles, start XAE or contact a PLC.
Use the build's matching checkout and restored assets when running it.

To exercise the real host loader against the extracted ZIP rather than the
build-output staging tree, extract it into a fresh directory with the layout
`<root>/beckhoff-tc3-4026/manifest.json`, then run the package tests:

```powershell
$env:PLCSIM_UNITTEST_BECKHOFF_PACKAGE_TEST_ROOT = [IO.Path]::GetFullPath('.tmp/package-check/profiles')
try {
    dotnet test access-profiles/beckhoff-tc3-4026/tests/PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.csproj `
        -c Release
} finally {
    Remove-Item Env:PLCSIM_UNITTEST_BECKHOFF_PACKAGE_TEST_ROOT
}
```

The override is test-only. These tests cover discovery, isolated host loading,
configuration, unconnected ADS construction, metadata and registration with
isolated application/profile data stores;
they do not activate COM or operate a Runtime. Keep the extraction isolated from
installed/debug packages. Package success does not qualify managed transfer.

Publication requires a clean source revision. For local packaging checks only,
use `-AllowDirtySource -OutputRoot .tmp/<task>/packages`. Such output is not a
release: its recorded HEAD does not describe uncommitted changes. Rebuild from
the approved clean revision before publishing. Do not replace a published package
version, edit historical bundle pins or pass an unrelated `-SourceRevision`.

## Bundle Inclusion

The profile-owned `distribution/bundle-component.json` in this subtree records
the approved component identity, version, destination and default selection.
It is a composition fragment, not a complete bundle or an integrity pin. After
verifying the exact release package, add its real package-manifest hash:

```powershell
$component = Get-Content .\access-profiles\beckhoff-tc3-4026\distribution\bundle-component.json -Raw | ConvertFrom-Json
$manifestPath = '.\artifacts\release-packages\access-profiles.beckhoff-tc3-4026\1.0.0\package-manifest.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.package.name -ne $component.package -or $manifest.package.version -ne $component.version) {
    throw 'The component and verified package must match.'
}
$component | Add-Member -NotePropertyName manifestSha256 `
    -NotePropertyValue (Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$component | ConvertTo-Json
```

Insert that object in the next coordinated bundle's `components`, alongside the
other selected profiles. It has no `parent`; it is not a Siemens binding. The
existing resolver, staging and installer-composition scripts consume the ordinary
component fields without Beckhoff-specific installer logic. Final bundle pins
must refer to the approved, compatible application/profile package set, not a
mixture with the historical V1 application. Local dirty-build hashes are never
promoted into a release bundle.

The profile checker also writes a resolved component fragment beneath `.tmp`
for local composition tests. This proves the shape/hash integration only, not
installed operation or approval to publish. Installer acceptance and live host
verification remain separate checks.

## Notices

No new license classification is introduced by packaging. The reviewed
[dependency closure](dependencies.md) is already covered by the repository's
third-party inventory. The generated file attributes all 13 packages and includes
the original Beckhoff MIT text and the canonical .NET MIT text. Unrelated upstream
notice collections are not copied. `Microsoft.VisualStudio.Interop.dll` and
native XAE COM remain non-redistributed installation prerequisites. The profile
neither requires nor distributes `TCatSysManagerLib.dll` or generated Beckhoff
interface metadata.

If a dependency version, runtime asset or interop redistribution changes, review
and update the inventory before producing another release package. Successful
notice generation is not permission to redistribute an unreviewed vendor binary.
