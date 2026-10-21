# Documentation Guide

The [project overview](../README.md) introduces PLCSIM.UnitTest. This guide
directs readers to the document that owns each operational or technical topic.

## Using The Application

| Topic | Authoritative entry point |
| --- | --- |
| Current PLC targets, required companion frameworks and publication status | [Current PLC targets](../README.md#current-plc-targets); PLC-side setup belongs to the linked framework projects |
| Installation, host runtimes, component selection and per-user registration | [Windows installation](deployment/windows-installation.md) |
| Interactive session setup, test execution, settings and diagnostics | The application's **Help > Help Contents**; [offline help source](../src/PLCSIM.UnitTest.App.Wpf/Help/index.html) |
| Commands, configuration preflight, automation, artifacts and exit codes | [CLI guide](../src/PLCSIM.UnitTest.App.Cli/README.md) |
| Windows CI-agent provisioning and adaptable pipeline examples | [CI starters](../src/PLCSIM.UnitTest.App.Cli/examples/ci/README.md) |
| Vendor prerequisites, supported workflows, profile values and qualification | The selected access profile's README and its owned guides under [access-profiles](../access-profiles/) |

HTML help is shipped with the desktop application. When browsing this source
repository on GitHub, HTML links show source files; use the installed help or
open a local copy in a browser to read the rendered help.

Application guides own common behavior. A profile's `docs/configuration.md`
owns its fields and target restrictions; its usage, deployment and verification
guides own vendor-specific preparation and evidence. A shared example is not a
substitute for those requirements.

## Building And Extending

| Topic | Authoritative entry point |
| --- | --- |
| System requirements, technology stack and repository structure | [Technical project specification](project-specification.md) |
| Solution ownership, build/test procedures, contracts and local deployment | [Build foundation](development/solution-and-build-foundation.md) |
| Shared script entry points and their responsibilities | [Repository tools](../tools/README.md) |
| Shared workflow and component responsibilities | [Architecture overview](architecture/000-overview.md) |
| Access-profile ownership and integration boundaries | [Profile overview](../access-profiles/README.md) and [profile architecture](architecture/200-access-profile-layer.md) |
| Runtime, package and common configuration interfaces | [Runtime contract](contracts/access-profile-runtime-contract.md), [package contract](contracts/access-profile-package-contract.md), [configuration contract](contracts/configuration-file-contract.md) |
| Local packages, exact bundle pins, distribution staging and installer builds | [Package and bundle guide](deployment/release-package-manifests.md) |
| Reviewed dependencies and redistribution notices | [Release license sources](../distribution/licenses/README.md) |

## Keeping Information In Its Owning Document

- The root README introduces the project and links here; it does not duplicate
  installation procedures, command references or build instructions.
- This guide provides navigation. It does not maintain a second support matrix
  or a list of changing vendor versions.
- The project specification owns the shared technical baseline. Build and
  installation guides link to it and own their procedures and checks.
- Application, profile and binding READMEs introduce their component and point
  to its detailed guides. Concrete commands and version constraints stay with
  the component that owns them.
- Architecture documents explain shared models and ownership. Contract documents
  define normative interfaces; framework-owned references retain their source
  metadata and synchronization rules.
- Package assembly and publication decisions belong to the package guide, while
  end-user installation and maintenance belong to the installation guide.
