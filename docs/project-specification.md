# Technical Project Specification

This document owns the shared technical baseline: system requirements,
technology stack and repository structure. The [root README](../README.md)
introduces the project and current targets; operational procedures and detailed
integration constraints belong to their owning guides.

## System Requirements

### Running The Windows Distribution

| Area | Requirement |
| --- | --- |
| Host platform | Windows, x64. |
| Application runtime | .NET 8 Desktop Runtime, x64. |
| Worker runtime | .NET Framework 4.8; required by the packaged installer and used by Siemens workers. |
| Vendor environment | The engineering/runtime products, licenses and authorization required by the selected access profile and bindings; installed separately. |
| PLC program | A compatible PLC UnitTest framework and test project implementing the communication contract consumed by the profile. |
| Profile provisioning | Compatible directory packages and current per-user profile registration. |

These are the common prerequisites of the Windows distribution, not a support
claim for every Windows/vendor combination. Concrete product versions and
qualification remain with the selected profile and binding. The
[installation guide](deployment/windows-installation.md) owns Setup behavior,
component selection, registration, upgrade and maintenance.

### Building From Source

- Windows and an SDK supporting the projects' declared .NET 8 targets.
- PowerShell for the shared scripts under [tools](../tools/README.md).
- Access to the configured NuGet sources when dependencies are not cached.
- Committed canonical contract binaries under `lib/contracts/`; a normal
  application build consumes them without rebuilding or replacing them.
- Additional targeting packs and installed vendor products only when required
  by the profile, binding or worker being built. Siemens .NET Framework workers
  require the matching .NET Framework targeting support.
- A compatible **Inno Setup 6** compiler for Windows installer builds.

WPF is Windows-only. CLI, shared core, exporters and the Fake profile require no
vendor installation for development. The Siemens profile shell can also be
tested with fake bindings; building one concrete vendor binding does not imply
installing every supported vendor version.

Build/test commands, solution selection, contract synchronization and local
deployment are defined in the [build foundation](development/solution-and-build-foundation.md).
Local package assembly and installer commands are defined in the
[package guide](deployment/release-package-manifests.md).

## Technology Stack

| Component | Technical basis |
| --- | --- |
| Desktop application | C#, .NET 8, Windows Presentation Foundation (WPF). |
| Automation application | C#, .NET 8 CLI with defined process exit codes. |
| Shared workflow and results | .NET 8 services for run coordination, event reconstruction, diagnostics and output processing. |
| Integration boundary | Access-profile directory packages, JSON manifests and application-facing service contracts. |
| Vendor workers | Profile-owned implementations, including .NET Framework 4.8 workers where vendor API compatibility requires them. |
| Test reports | JUnit XML generated from reconstructed PLC test evidence. |
| Configuration | JSON application configuration plus profile-owned values. |
| Windows distribution | Versioned package ZIPs/manifests, hash-pinned bundles and an Inno Setup installer. |

WPF supports application-managed engineering and externally prepared PLC
programs. CLI execution uses application-managed project preparation/transfer.
Concrete runtime provisioning and operation capabilities depend on the profile;
the two applications do not promise complete feature parity.

The [architecture overview](architecture/000-overview.md) explains the workflow
and ownership model. Normative runtime, package and configuration interfaces
are defined in [contracts](contracts/), with vendor-specific details below the
owning access-profile subtree.

## Repository Structure

| Location | Responsibility |
| --- | --- |
| `src/` | Shared contract owners, workflow, WPF/CLI applications and exporters. |
| `access-profiles/` | Independent profiles, internal bindings, workers and their owned tests, resources and documentation. |
| `tests/` | Application/shared tests and common test utilities. |
| `docs/` | Shared architecture, contracts, technical, development and deployment guides. |
| `lib/contracts/` | Canonical binary contracts consumed by normal builds. |
| `distribution/` | Bundle definitions and reviewed dependency-license inputs. |
| `installer/` | Installer definition, assets and registration launcher. |
| `tools/` | Shared build, package and verification scripts. |

Root solution files provide common, application and integration build views;
their exact ownership is described in the
[build foundation](development/solution-and-build-foundation.md#solution-ownership).
Profile and binding solutions remain with their components.

`Directory.Build.props` and `Directory.Build.targets` remain at the repository
root so MSBuild can discover the shared defaults and targets from project
directories. They establish repository/contract paths, common C# defaults,
contract deployment and plugin package validation/deployment.

Generated build output belongs under project-local `bin/` and `obj/`, package
output under root `artifacts/`, and local scratch data under `.tmp/`. None of
these are product source inputs. The checked-in contract binaries are an
intentional exception to ordinary generated-output handling.
