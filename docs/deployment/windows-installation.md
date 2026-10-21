# Windows Installation

This document describes installation and maintenance of a packaged
PLCSIM.UnitTest Windows distribution. Release-package metadata and the
reproducible installer build are documented in
[`release-package-manifests.md`](release-package-manifests.md).

## Prerequisites

The [technical project specification](../project-specification.md#running-the-windows-distribution)
defines the host runtimes and shared environment requirements.

Setup checks both host runtimes before modifying the installation and reports
missing runtimes. It does not download runtimes or vendor software. Access
profiles and technology plugins report their own vendor-product readiness after
installation.

## Install

Run `PLCSIM.UnitTest-Setup-<version>.exe` interactively. Setup installs per
machine and therefore requests elevation. The default destination is
`%ProgramFiles%\PLCSIM.UnitTest`; the directory page allows another location.

Typical setup installs every component curated by the distribution. Custom
setup allows individual access profiles and their child technology plugins to
be selected. A plugin cannot be selected without its owning profile. Component
selection controls both the runtime payload and its installed package
inventory.

The `PLCSIM.UnitTest` Start Menu group contains:

- the WPF application;
- a link to the installation directory;
- `Register Access Profiles` for the current Windows user.

The WPF desktop shortcut is optional. The CLI is installed but is not added to
`PATH` and has no direct Start Menu entry.

## Register Access Profiles

Profile packages are installed per machine, while registration and derived
profile state belong to each Windows user. Registration writes below
`%LOCALAPPDATA%\PLCSIM.UnitTest` and does not modify the installed package tree.

Interactive Setup offers **Register access profiles (valid only for the user
who started Setup)** on its final page. This action runs as the original
interactive user and does not request separate UAC elevation. Silent setup does
not register profiles.

Registration can be repeated in either of these ways:

- select **PLCSIM.UnitTest > Register Access Profiles** in the Start Menu;
- run `PLCSIM.UnitTest.App.Cli.exe register-profiles` from the installation
  directory.

WPF checks registration at startup. A missing, stale, unavailable, or
package-inconsistent registry produces a visible warning with **Register now**.
Successful refresh reloads the available profiles and removes the warning.
CLI `check-config` and `run` do not repair registration implicitly; they stop
with exit code `20`. Run `register-profiles` as a separate provisioning step.

Profile registration and vendor authorization are different operations.
Registration discovers installed packages and records their capabilities; it
does not authorize vendor workers or change vendor configuration. Follow the
deployment documentation shipped with the selected profile for any required
vendor-specific setup.

## Writable User Data

The default writable root is:

```text
%LOCALAPPDATA%\PLCSIM.UnitTest
```

It contains the per-user application profile registry, derived profile data,
settings, logs, and the default artifact output. Projects and explicitly
configured external artifact or registry paths remain outside installer
ownership.

The application, profile, plugin, and installed inventory files below the
installation directory are immutable package content. Normal application use
and registration must not change them.

## Upgrade And Repair

All distribution versions use one stable Windows product identity. Running a
newer installer performs an in-place upgrade rather than creating a parallel
product entry.

The installation directory is an installer-owned package tree. Setup removes
the previous owned tree, preserves its private uninstaller state, and installs
the newly selected package composition. This removes stale dependencies,
workers, and deselected components. Do not place persistent user files or
manually deployed extensions below the installation directory.

Per-user application data is preserved during upgrade and repair. WPF detects
whether the retained registration still matches the installed profile and
nested binding manifests. Use **Register now** after a package or component
change when the registration is reported as stale.

Re-running the same distribution version is the repair/reselection path. The
selected component set replaces the previous set as one package composition;
deselecting a component removes its installer-owned files.

## Uninstall

Uninstall removes the installer-owned application tree and Start Menu entries.
Interactive uninstall asks whether the current uninstall account's default
`%LOCALAPPDATA%\PLCSIM.UnitTest` directory should also be removed and defaults
to removal. Choose **No** to retain that account's settings, logs, artifacts,
registry, and profile data. Silent uninstall retains the directory only when
`/KEEPUSERDATA` is supplied.

The uninstaller does not enumerate other Windows user profiles and does not
follow configured external project, artifact, or registry paths. If uninstall
is run under different administrative credentials, its current-account data
path is not the original application user's path.

An empty installation directory can remain when Windows still holds a handle
during cleanup. It contains no usable installation and may be removed after the
owning process has exited. The uninstaller deliberately does not perform an
unbounded recursive cleanup outside its verified installer-owned tree.

## Troubleshooting

### WPF reports missing or stale registration

Use **Register now** or the Start Menu registration command. A package install,
removal, replacement, or installation-path change intentionally makes retained
registration stale. If refresh fails, inspect:

```text
%LOCALAPPDATA%\PLCSIM.UnitTest\logs
```

Confirm that the current user can write the application-data directory and
read the installed profile tree.

### A profile is registered but a vendor integration is unavailable

Registration records actual environment evidence; installing a plugin does not
install its vendor product. Check the selected profile's packaged deployment
and troubleshooting documentation. Perform any profile-owned authorization
step separately, then refresh registration if package or environment evidence
changed.

### Setup cannot replace or remove the application directory

Close WPF, CLI, profile workers, and tools that inspect files below the
installation directory, then retry. Setup refuses to continue when it cannot
replace a verified installer-owned tree completely.

### CLI exits with code 20

For `check-config` and `run`, exit code `20` includes missing or stale profile
registration. Provision the agent by running `register-profiles` once under the
same Windows account used by the CI job. Do not register profiles implicitly in
every test job.

## Known Limits

- The installer is offline but does not contain vendor products or vendor DLLs
  that must be resolved from an installed product.
- Generic profile registration does not perform vendor authorization.
- Silent setup skips per-user profile registration.
- Per-machine uninstall cannot remove application data for every Windows user.
- Missing-prerequisite behavior should be validated on a prepared Windows image;
  removing required runtimes from a development workstation is not part of the
  normal release check.
- The installer is unsigned and can therefore produce an unknown-
  publisher warning. Verify its published SHA-256 checksum before installation.
