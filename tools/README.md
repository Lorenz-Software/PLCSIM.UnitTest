# Repository Tools

Shared build, packaging and verification scripts live here. Component-specific
tools remain with their owning access profile or installer component.

Run the documented commands from the repository root, for example:

```powershell
.\tools\test-local-release.ps1
```

Scripts derive repository-owned resources and default output locations from
their own location, not from the current shell directory. Explicit relative
input paths are still relative to the caller; use absolute paths when invoking
from another directory. Script-specific output-path conventions are documented
in the owning guide. Artifacts stay under repository `artifacts/` or `.tmp/`,
not under `tools/`.

## Build And Development

| Script | Purpose |
| --- | --- |
| [contract-artifacts.ps1](contract-artifacts.ps1) | Deliberately synchronize or verify canonical contract binaries. |
| [deploy-debug-plugins.ps1](deploy-debug-plugins.ps1) | Prepare Real or Fake application/debug compositions. |

See the [build foundation](../docs/development/solution-and-build-foundation.md)
for prerequisites, solution ownership, deployment and verification boundaries.
Contract synchronization updates committed binary inputs; it is not a routine
side effect of a normal application build.

## Local Packages And Installer

| Script | Purpose |
| --- | --- |
| [build-release-package.ps1](build-release-package.ps1) | Build one application, profile or binding package. |
| [resolve-local-release.ps1](resolve-local-release.ps1) | Resolve a pinned local package or bundle with integrity checks. |
| [build-distribution-staging.ps1](build-distribution-staging.ps1) | Validate and extract a resolved bundle into its installation tree. |
| [build-windows-installer.ps1](build-windows-installer.ps1) | Compile a staged Windows distribution using Inno Setup. |
| [new-windows-installer-composition.ps1](new-windows-installer-composition.ps1) | Generate installer components and file mappings. |
| [new-third-party-notices.ps1](new-third-party-notices.ps1) | Generate reviewed component notices from actual dependency inputs. |
| [new-bundle-third-party-notices.ps1](new-bundle-third-party-notices.ps1) | Aggregate notices from the pinned distribution packages. |
| [release-package-validation.ps1](release-package-validation.ps1) | Shared package/bundle validation functions; loaded by other scripts. |

The [package and bundle guide](../docs/deployment/release-package-manifests.md)
defines versions, pins, command sequences and local-candidate limitations.
Building or resolving a local package does not publish a release.

## Verification

| Script | Purpose |
| --- | --- |
| [test-local-release.ps1](test-local-release.ps1) | Deterministic resolver, staging and installer-composition checks. |
| [test-third-party-notices.ps1](test-third-party-notices.ps1) | Notice-generation regression checks. |
| [test-release-profile-smoke.ps1](test-release-profile-smoke.ps1) | Profile smoke-test isolation and cleanup checks. |
| [test-windows-release.ps1](test-windows-release.ps1) | Validate actual packages, staged distribution, compiled setup and isolated CLI/profile registration. |

These checks do not qualify live vendor runtimes or install/upgrade/uninstall
behavior. The optional WPF-startup check in the Windows verifier is explicit.
