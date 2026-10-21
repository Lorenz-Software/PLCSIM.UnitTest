# Release Package And Bundle Manifests

This document defines the version and integrity metadata used to publish
PLCSIM.UnitTest components and compose a Windows distribution. It does not
replace the runtime manifests used to discover access profiles or their
technology bindings.

## Manifest Boundaries

Each independently released component has two artifacts in one package version.
The local build and resolution tools use this layout without a registry:

```text
<payload>.zip
package-manifest.json
```

The payload ZIP contains the component-owned runtime files. Access-profile and
plugin archives retain their existing runtime `manifest.json` at the package
root. `package-manifest.json` describes the release artifact itself and remains
outside the ZIP so a distribution build can validate an artifact before
extracting it.

Extracted application, profile, and plugin payloads are immutable installation
content. Runtime registration state, caches, settings, logs, and result
artifacts are not package contents and are written only to their application-
or profile-owned data locations. The installer creates package directories only
for selected components; WPF and CLI do not create a missing profile-discovery
tree.

A Windows distribution is defined by a separate `bundle-manifest.json`. It
pins exact component package versions and package-manifest hashes. The bundle
manifest owns the destination of each component within the installed tree;
individual packages do not choose installation paths.

Versioned bundle definitions are repository inputs below
`distribution/bundles/` and use the name `installer-<distribution-version>.json`.
They are the reviewed bill of materials for a release. New profiles or plugins
are added by publishing their independent package and adding an explicit pin to
the next bundle definition; the build does not select packages automatically
from a directory or remote service.
Bundle resolution and installer composition are currently explicit release
commands. No CI orchestration is provided for this command sequence.

## Package Identities

Package names use lowercase dot-separated ownership segments in a flat
namespace; dots express logical hierarchy without
exposing repository layout.

Package identities follow these ownership patterns:

| Component kind | Generic package name | Payload |
| --- | --- | --- |
| Application | `plcsim-unittest` | `application.zip` |
| Access profile | `access-profiles.<profile>` | `profile.zip` |
| Technology plugin | `plugins.<vendor>.<technology>.<target-version>` | `plugin.zip` |

A target version that cannot be represented as a dot segment uses the runtime
package's established token. The reviewed bundle definitions are the
authoritative inventory of concrete profile and plugin packages included in a
Windows distribution.

## Version Semantics

Application, profile, plugin, and Windows distribution versions are independent
semantic versions. A package version identifies a release of code owned by that
component; it is not the Siemens product or API version.

For profile and plugin packages, the release package version must equal the
package version in the component's runtime manifest:

- `profile.version` for an access profile;
- `info.bindingVersion` for a Siemens technology binding.

Siemens product and API targets remain in the binding declaration and package
identity. For example, a V21 binding may have package
version and `info.bindingVersion` `1.0.0`, while its declaration continues to
advertise product and API version `21`.

The Windows distribution version changes whenever installer behavior or its
pinned component composition changes. It does not need to equal any component
version.

WPF and CLI belong to one application package and declare the same application
`Version` in their project files. Keep both declarations aligned when preparing
an application release. The package builder passes its requested `-Version` to
both application builds and records it in `package-manifest.json`. The WPF About
dialog displays the assembly's major/minor/patch version; CLI `--version`
reports the informational version, which may additionally include a source
revision. Neither display represents the installer or an access-profile version.

## Package Manifest

`package-manifest.json` uses this schema:

```json
{
  "schemaVersion": 1,
  "package": {
    "name": "plugins.vendor.technology.target",
    "version": "1.0.0",
    "kind": "plugin",
    "displayName": "Vendor Technology Plugin"
  },
  "artifact": {
    "file": "plugin.zip",
    "sha256": "<lowercase SHA-256>",
    "size": 123456
  },
  "source": {
    "revision": "<full Git commit SHA>"
  },
  "requires": [
    "access-profiles.vendor"
  ],
  "runtimeRequirements": [
    {
      "id": "net-framework",
      "version": "4.8"
    }
  ]
}
```

Fields are defined as follows:

| Field | Required | Meaning |
| --- | --- | --- |
| `schemaVersion` | Yes | Package-manifest schema version. The first supported value is `1`. |
| `package.name` | Yes | Exact component package identity. |
| `package.version` | Yes | Normal semantic version of this component release. |
| `package.kind` | Yes | One of `application`, `profile`, or `plugin`. |
| `package.displayName` | Yes | Stable human-readable component name. |
| `artifact.file` | Yes | Fixed payload filename within the package version. |
| `artifact.sha256` | Yes | Lowercase SHA-256 of the exact payload bytes. |
| `artifact.size` | Yes | Payload size in bytes. |
| `source.revision` | Yes | Full Git commit SHA of the component source used for this build. |
| `requires` | Yes | Package identities required for composition; empty when there are none. |
| `runtimeRequirements` | Yes | Host runtime prerequisites owned by the package; empty when there are none. |

The manifest contains no repository URL, build-machine path, pipeline URL,
author, or timestamp. This avoids publishing development-environment details
and keeps equivalent package metadata reproducible. `source.revision` is
traceability evidence only; it is not a version or compatibility constraint.

The application runtime requirement is represented as:

```json
{
  "id": "dotnet-desktop",
  "version": "8.0",
  "architecture": "x64"
}
```

Dependencies are intentionally package identities rather than version ranges.
The distribution selects their exact versions. Shared contract assembly
compatibility continues to use the existing exact assembly/hash validation at
the runtime package boundary.

## Bundle Manifest

The bundle manifest is the immutable input for distribution staging:

```json
{
  "schemaVersion": 1,
  "distribution": {
    "name": "plcsim-unittest.windows",
    "version": "0.1.0"
  },
  "application": {
    "package": "plcsim-unittest",
    "version": "0.1.0",
    "manifestSha256": "<lowercase SHA-256>",
    "destination": "."
  },
  "components": [
    {
      "id": "profile.vendor",
      "package": "access-profiles.vendor",
      "version": "1.0.0",
      "manifestSha256": "<lowercase SHA-256>",
      "destination": "profiles/vendor",
      "selectedByDefault": true
    },
    {
      "id": "plugin.vendor.technology.target",
      "package": "plugins.vendor.technology.target",
      "version": "1.0.0",
      "manifestSha256": "<lowercase SHA-256>",
      "destination": "profiles/vendor/lib/bindings/technology-target",
      "parent": "profile.vendor",
      "selectedByDefault": true
    }
  ]
}
```

`manifestSha256` hashes the exact `package-manifest.json` bytes. Together with
the payload hash inside that manifest, it prevents a package version from being
silently replaced during later distribution builds. `parent` defines installer
component dependency and layout ownership. Destinations must be relative,
normalized paths below the distribution root and must not overlap another
package-owned root except through the declared parent relationship.

The staging process must reject:

- unknown schema versions or package kinds;
- package name or version mismatches;
- package-manifest or payload hash/size mismatches;
- missing dependencies or parent components;
- absolute, parent-traversing, or colliding destinations;
- profile/plugin release versions that differ from their runtime manifests;
- package contents outside the allowlist defined by the owning package build.

## Package Publication Identity

Component and distribution source tags use these identity conventions:

```text
application/v<version>
access-profiles/<profile>/v<version>
plugins/<vendor>/<technology>/<target-version>/v<version>
distribution/windows/v<distribution-version>
```

The completed local installer output has this layout (publication is a separate,
explicitly approved step):

```text
plcsim-unittest.windows/<distribution-version>/
  PLCSIM.UnitTest-Setup-<distribution-version>.exe
  bundle-manifest.json
  SHA256SUMS
```

The distribution version supplies Inno Setup `AppVersion`, the installer file
name, and a four-part Windows file version by appending `.0` to the numeric
major/minor/patch portion. The installer retains the complete semantic version
in its textual product version, including any prerelease suffix. Published
package versions are immutable inputs: publication and
resolution automation must fail when an artifact with the same package name and
version already exists instead of replacing it.

## Local Package Build

Build one component package at a time from the repository root. The package
selector identifies the owned application, profile, or plugin project:

```powershell
.\tools\build-release-package.ps1 -Package Application -Version 0.1.0
.\tools\build-release-package.ps1 -Package <package-selector> -Version <version>
```

Use `Get-Help .\tools\build-release-package.ps1 -Full` for the selectors supported by
the current source tree. Concrete package versions belong in the corresponding
bundle definition, not in this document.

Outputs are written to
`artifacts/release-packages/<package-name>/<version>/`. The build requires a
clean Git working tree and records its full commit SHA. For local verification
of uncommitted packaging changes, `-AllowDirtySource` explicitly relaxes only
the clean-tree check; it does not relax content, version, or integrity checks.

Bundle hashes derived from these local packages pin only that local candidate.
Before publication, rebuild from the qualified clean component revisions and
update the bundle pins from the resulting immutable package manifests. Do not
publish an uncommitted candidate as though its recorded HEAD revision fully
described its source.

Application publishing is framework-dependent and RID-specific for `win-x64`.
WPF and CLI outputs are merged only when same-path files have identical
content. Profile and plugin packages use the existing production
`DeployPluginPackage` output as input to an isolated release staging directory.
The release-package script limits its explicit restore operations to
`nuget.org` so machine-wide package sources cannot change the package build.
This packaging-only NuGet dependency source does not alter normal repository
development. Component resolution below reads local files only and does not
contact a package registry or GitHub.
Debug symbols are omitted. Test, Fake, validation, performance-measurement,
temporary, cache, log, and known vendor artifacts are rejected. ZIP entries are
sorted and receive a fixed timestamp so identical payload bytes produce an
identical archive.

## Local Package And Bundle Resolution

No published release, registry, token, or GitHub connection is needed. Build
packages into a local store with the layout:

```text
artifacts/release-packages/<package-name>/<version>/package-manifest.json
artifacts/release-packages/<package-name>/<version>/<payload>.zip
```

Resolve one exact package, supplying the expected SHA-256 of its manifest:

```powershell
.\tools\resolve-local-release.ps1 `
  -PackageName plugins.vendor.technology.target `
  -Version 1.0.0 `
  -ManifestSha256 <sha256> `
  -LocalPackageRoot artifacts\release-packages `
  -DestinationRoot artifacts\resolved-packages
```

The output contains `<package-name>/<version>/` with the two validated files.
Resolve every pin in a reviewed bundle definition with:

```powershell
.\tools\resolve-local-release.ps1 `
  -BundleManifest .\distribution\bundles\installer-0.1.0.json `
  -LocalPackageRoot artifacts\release-packages `
  -DestinationRoot artifacts\resolved-bundle
```

The resolver uses exact package versions and manifest hashes. It verifies the
manifest schema, package identity, source revision format, payload filename,
size and SHA-256. For bundles it also checks unique identities, component
hierarchy, destinations and package dependencies. Resolution and distribution
staging share those validators. ZIP entry validation remains in distribution
staging, before extraction.

All packages are copied to private staging and revalidated before the complete
new destination is exposed. Existing destinations are rejected without being
overwritten. Sources and destinations must not overlap; linked input/output
paths are rejected. Bundle output contains the original `bundle-manifest.json`
and `packages/<package-name>/<version>/` for the existing staging command.

### Local Candidates And Bundle Pins

A bundle definition pins previously built artifacts, not just version labels.
The committed bundle hashes do not automatically describe a fresh build from
this checkout. Missing packages and stale hashes are errors; the resolver never
selects a newer package or refreshes pins silently.

For local verification, make an untracked candidate definition under `.tmp/`
from the intended bundle layout, and set each `manifestSha256` to the hash of
its actual local `package-manifest.json`. Keep versions aligned with the built
packages and preserve the intended destinations and parent relationships.
Review the candidate before resolving it. Such a candidate is not an approved
release definition, especially when built using `-AllowDirtySource`.

The default release package build requires a clean tree. `-AllowDirtySource`
is for local checks only; its recorded HEAD does not describe uncommitted
changes. Before publication, build from the exact qualified public commit and
review the final bundle pins and release version separately. This local
workflow performs no upload and does not require a GitHub release.

Run `./tools/test-local-release.ps1` for deterministic package/bundle integrity,
failure-path, distribution-staging and installer-composition checks using
synthetic packages. Those checks do not qualify application binaries or vendor
runtimes; perform real package and installer checks separately.

## Distribution Staging

Compose a resolved bundle into its deterministic installation tree:

```powershell
.\tools\build-distribution-staging.ps1 `
  -ResolvedBundleRoot artifacts\resolved-bundle
```

The default output is
`artifacts/distribution-staging/<distribution-name>/<distribution-version>/`.
The command fails when that immutable output already exists. It independently
revalidates every pinned package manifest and payload before extraction.

Application files own the distribution root. Profile and plugin files are
mapped to their normalized bundle destinations. Nested component destinations
are permitted only for a declared direct parent relationship; duplicate target
files are always rejected. Plugin package dependencies must be present in the
bundle and must include the package of their declared parent component.

ZIP entries are extracted individually after rejecting absolute paths, parent
traversal, duplicate case-insensitive entries, symbolic links, forbidden
development files, and vendor assemblies. The final tree must contain exactly
the files owned by the validated package entries plus the inventory below
`distribution/`. The completed tree becomes visible only after all checks pass.

`distribution/bundle-manifest.json` is a byte-for-byte copy of the validated
bundle manifest and records the complete composition available in the Windows
distribution. Each validated release manifest is staged separately at
`distribution/packages/<package-name>/package-manifest.json`. The installer
always installs the application package inventory and conditionally installs
profile and plugin package inventories with their corresponding installer
components. The presence of a package manifest in an installed tree therefore
identifies an actually installed package without dynamically generating JSON
during setup.

## Windows Installer Build

Build the complete offline Windows installer from a validated distribution
staging tree:

```powershell
.\tools\build-windows-installer.ps1 `
  -DistributionRoot artifacts\distribution-staging\plcsim-unittest.windows\0.1.0
```

The build requires Inno Setup 6 and defaults to its normal 32-bit Program Files
location. The immutable output is written below
`artifacts/windows-installer/plcsim-unittest.windows/<version>/` and contains
the versioned setup executable, the exact bundle manifest used to compile it,
and `SHA256SUMS` for both files. The setup payload is entirely local and
performs no target-machine package downloads.

The Inno definition provides Typical and Custom installation types. The build
generates component and file declarations from the validated staged bundle and
its package inventories. `selectedByDefault` controls Typical selection,
`parent` controls the component hierarchy, `destination` controls payload
placement, and the package `displayName` supplies the visible component label.
Custom installation permits individual selection while preserving declared
parent relationships. Package inventory manifests use the same generated
component conditions as their payloads.

The generated Inno fragments are private build intermediates and are not
published. Component identifiers are converted to stable Inno identifiers;
collisions, unsafe or overlapping destinations, missing parents, invalid
default selection, unsupported package kinds, and inconsistent package
inventories fail before compilation. Consequently, adding a representable
profile or plugin requires a package release and bundle change, but no edit to
the Inno source or installer build script.

Setup installs per machine to `%ProgramFiles%\PLCSIM.UnitTest` by default while
retaining the standard directory selection page. It creates one
`PLCSIM.UnitTest` Start Menu group for the WPF application, installation
directory, and `Register Access Profiles` maintenance command. The desktop
shortcut is optional.

Interactive setup offers access-profile registration on its final page. The
same generic registration command remains available in the Start Menu. Both
paths run the installed CLI `register-profiles` command in the invoking user's
normal context. The final-page action uses the original interactive user rather
than Setup's elevated process. Silent setup never runs registration.
Registration dynamically evaluates the selected installed profile packages;
the installer launcher contains no profile or plugin-specific behavior.

Profile-owned setup tools remain part of their selected package and are
documented by that profile. The installer does not create plugin-specific
shortcuts, run Siemens authorization, or manipulate vendor configuration.

### Upgrade Ownership

The application directory is an installer-owned package tree. When setup finds
the installed `distribution/bundle-manifest.json` marker, it removes the prior
tree before installing the newly selected package composition. Inno keeps its
uninstaller state separately below `.uninstall` during this replacement. This
whole-tree replacement removes stale application dependencies, workers, and
deselected profile/plugin packages without introducing per-file ownership
metadata.

Do not manually deploy persistent extensions or user files into the application
directory. Such files are part of the installer-owned tree and are removed by
an upgrade. User projects, configured artifact locations, and profile registry
locations outside the default application-data directory are not part of that
tree and are not touched.

### Uninstall Data

Interactive uninstall asks whether `%LOCALAPPDATA%\PLCSIM.UnitTest` for the
current Windows account should be removed and defaults to removal. Choosing
`No` retains those settings, logs, caches, and the default application profile
registry. Silent uninstall also removes the directory by default; pass
`/KEEPUSERDATA` to the uninstaller to retain it.

Only that fixed current-account default directory is considered. The
uninstaller never follows configured project, artifact, profile registry, or
other external paths and does not enumerate other Windows user profiles. If
uninstall runs under different administrative credentials, the current-account
path belongs to those credentials; data owned by the original application user
must then be handled from that user's context.

Setup fails before installation with an explicit message when .NET 8 Desktop
Runtime x64 or .NET Framework 4.8 is missing. It does not download
prerequisites or vendor software.

Operator-facing installation, registration, upgrade, uninstall, and
troubleshooting guidance is maintained in
[`windows-installation.md`](windows-installation.md).

## Distribution Integrity And Signing

Do not reuse a published component or distribution version with different bytes.
A plugin-only change requires a new distribution version, while unchanged
component versions can be reused through their exact manifest pins. Component
build, bundle resolution, staging and installer compilation are separate local
commands; publishing to a particular host is not required for local composition.

The installer is unsigned. Generated SHA-256 checksums permit integrity
comparison but do not establish publisher authenticity or replace code signing.
Keep the exact bundle manifest and checksums alongside the installer.

## License And Notice Ownership

The repository root `LICENSE` is the authoritative PLCSIM.UnitTest license.
`distribution/licenses/third-party-inventory.json` classifies every direct and
transitive NuGet dependency used by production release projects and references
the reviewed license texts below `distribution/licenses/texts/`.

`tools/build-release-package.ps1` validates each restored release dependency against
that inventory, matches shipped dependency DLLs against the restored packages,
and generates package-specific `LICENSE.txt` and
`THIRD-PARTY-NOTICES.txt` files. `tools/build-distribution-staging.ps1` generates an
aggregate installer notice from the pinned packages' own notices that also
identifies Inno Setup. Generated notice
files are release outputs and are not maintained as duplicate repository
sources.

When adding or updating a production dependency:

1. verify its license and any upstream `NOTICE` requirements from the package
   and authoritative project source, recording the license's `includeText`
   decision and the package's explicit `additionalTextFiles` list;
2. update the inventory and canonical license text when necessary;
3. run `./tools/test-third-party-notices.ps1`;
4. build every affected release package; and
5. run Windows release verification to confirm package and installer notices.

Siemens APIs and installed .NET runtimes are external prerequisites. The small
.NET executable host embedded in framework-dependent application executables
needs to be considered separately from the installed runtime. Selecting a
package's license does not automatically select all upstream NOTICE files.
Additional texts are included only through the package inventory. Review scope
and original sources are documented in `distribution/licenses/README.md`.

## Release Verification

Run `./tools/test-release-profile-smoke.ps1` after changing profile-data verification
or cleanup. Its isolated fixtures cover multiple profile identities, partial
registration, pre-existing state and cleanup containment without vendor access.

Verify one resolved bundle, its deterministic distribution tree, and the
compiled installer publication together:

```powershell
.\tools\test-windows-release.ps1 `
  -ResolvedBundleRoot artifacts\resolved-bundle `
  -DistributionRoot artifacts\distribution-staging\plcsim-unittest.windows\0.1.0 `
  -InstallerRoot artifacts\windows-installer\plcsim-unittest.windows\0.1.0
```

The verification independently checks pinned manifest and payload hashes,
package-to-distribution file ownership, installed package inventories,
forbidden artifacts, the bundle-derived Inno component hierarchy, installer
publication checksums, and installer version metadata. It then creates an
isolated copy of the full installation layout, verifies CLI help, registers all
packaged access profiles into temporary application data, and checks that the
persisted registry points to their installed profile roots.

Add `-RunWpfStartup` on an interactive Windows desktop to start the packaged WPF
application, verify that it remains alive after initialization, and close it
again. The verification does not execute setup or uninstall and does not touch
the user's application profile registry. Fresh install, component selection,
upgrade, repair, and uninstall behavior remain explicit installer smoke tests.
