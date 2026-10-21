# Siemens Profile Deployment

This document describes the deployment and setup handoff for the Siemens access
profile and its independently deployed technology bindings. The application
does not know concrete Siemens binding versions or vendor authorization
details.

## Package Layout

A deployed application has a profile discovery directory next to the executable
unless the location is overridden by application configuration. The Siemens
profile package is one directory below that root:

```text
profiles/
  siemens/
    manifest.json
    setup-siemens-openness.ps1
    lib/
      bindings/
        <binding-package>/
          manifest.json
          <binding binaries>
```

Each profile and binding package must be built and deployed independently. A
binding package is selected by its directory containing `manifest.json`; binding
IDs and product versions are read from the manifest and are not setup-script
parameters.

The installed profile and binding tree is immutable application content.
Registration and normal execution do not create or update files below it. The
application supplies the Siemens profile with a separate per-user writable data
directory for derived registration evidence. Removing or replacing packages is
a deployment operation; registration then regenerates the profile-owned state
for the selected package source.

## Release Versions

The Siemens profile and each technology binding are independently versioned
release packages. The profile's `profile.version` and a binding's
`info.bindingVersion` identify their component releases, not a Siemens product
or API version. The binding's declaration retains its separate product/API
targets. Shared contract compatibility is validated independently of these
release versions.

The release package builder requires the requested package version to match
the runtime manifest. A version in a local manifest or build does not imply
that the package has been published. A distribution pins the exact profile and
binding package versions it includes; an application or installer version
change does not by itself require changing those component versions.

See the [release package contract](../../../docs/deployment/release-package-manifests.md)
for package identities, dependency declarations, and publication rules.

## Deployment Sequence

Use this order for a new or rebuilt real TIA Openness binding:

1. Deploy the Siemens access-profile package.
2. Deploy the required Siemens technology binding packages below
   `siemens\lib\bindings`.
3. From an interactive Windows user session, run the Siemens-owned TIA Openness
   authorization setup for the new or changed binding worker.
4. From the Windows account that will run the application, execute the
   application's `register-profiles` command to rebuild that account's
   application profile registry from the deployed package manifests. Generic
   profile registration does not require elevation.
5. Run the unattended CLI command with the registered profile and the intended
   configuration.

Authorization and profile registration are separate provisioning operations.
`register-profiles` runs in the application user's context, does not require
elevation for the default per-user registry, does not open TIA Portal, and does
not authorize workers. TIA Openness authorization remains an interactive
vendor setup step. Prepare, authorize, and register the Windows agent before
assigning unattended automation work to it.

## TIA Openness Authorization Setup

Run the script from the deployed Siemens profile directory. To authorize one
newly deployed binding without touching other bindings:

```powershell
.\setup-siemens-openness.ps1 -p .\lib\bindings\<binding-package>
```

`-Path` is the long form. Supply an explicit array to process multiple selected
packages:

```powershell
.\setup-siemens-openness.ps1 -Path @(
    '.\lib\bindings\<first-binding-package>',
    '.\lib\bindings\<second-binding-package>'
)
```

Use `-All` only when every deployed real TIA Openness binding advertising the
authorization probe must be processed:

```powershell
.\setup-siemens-openness.ps1 -All
```

Without `-Path` or `-All`, the script presents an interactive numbered
selection. Fake bindings and packages that do not advertise the authorization
probe are not offered. Use `-h`, `-Help`, or PowerShell `Get-Help` for command
help.

The selected binding starts its own unchanged worker and performs a minimal,
project-free TIA Portal open/close. Confirm the Siemens authorization prompt and
persist the approval when unattended use is intended. The setup script does not
elevate itself and does not edit the Siemens AllowList registry directly.

The authorization belongs to the exact worker binary fingerprint. Rebuilding or
replacing the worker requires running setup again for that deployed package.
After successful setup, repeat the probe if needed: the unchanged worker should
return `Authorized` without another prompt and must leave no worker or TIA
Portal process behind.

## Setup Exit Codes

| Code | Meaning |
| ---: | --- |
| 0 | Every selected worker is authorized. |
| 10 | Invalid or conflicting script arguments. |
| 20 | Invalid selection or binding package. |
| 21 | Selected binding environment is unavailable. |
| 30 | Authorization was denied or the probe failed. |
| 31 | Authorization probe timed out. |
| 50 | Probe cleanup failed. |
| 99 | Unexpected setup failure. |

All explicitly selected packages are processed. For multiple packages, the
script returns the most severe observed exit code and prints each package
result separately.

## Registration And CLI Execution

After authorization, rebuild the application registry from the application
directory:

```powershell
.\PLCSIM.UnitTest.App.Cli.exe register-profiles
```

An installed WPF application checks this per-user registration during startup.
When it is missing or stale, use the displayed **Register now** action. The
installer also provides **PLCSIM.UnitTest > Register Access Profiles** as a
repeatable command in the Start Menu. Both paths operate in the current user's
normal context.

Then execute the configured unattended run:

```powershell
.\PLCSIM.UnitTest.App.Cli.exe run `
  --file C:\ci\plcsim-unittest.json `
  --output C:\ci\results\plcsim-unittest.xml
```

Profile discovery and registry locations may be overridden with the application
environment variables documented by the CLI. Setup, registration, and the run
must resolve the intended deployment roots consistently.
