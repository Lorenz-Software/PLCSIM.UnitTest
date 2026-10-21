# Access Profile Package Contract

This document defines how PLCSIM.UnitTest discovers access profile candidates.
It covers only generic profile package metadata and loader behavior. Profile
specific configuration remains in the selected access profile contract.

The supported application/access-profile API contract is `2.0.0`.
Directory packages must declare that exact value in `profile.contractVersion`.
Other versions, including `1.0.0`, are rejected before loading; there is no
legacy adapter. Install a profile package built for the supported contract.
This version is independent of the profile package version, manifest schema
version, run configuration file version and PLC communication contracts.

Contract 2.0.0 renames `RuntimeProvisioningMode.RequireExisting` to
`UseExisting` without an alias. Its numeric value remains 0; `CreateOwned`
remains 1. Source and name-based consumers must migrate. Provisioning and
resource cleanup behavior are unchanged by this rename.

## Discovery Sources

The active discovery scope may contain these source kinds:

| Source Kind | Meaning |
| --- | --- |
| `BuiltIn` | In-process access profile plugins registered by the host application. |
| `DirectoryPackage` | Access profile packages found below the configured profile discovery directory. |

`BuiltIn` profiles are normal candidates. They are not a WPF shortcut and must be
reported through the same discovery result as directory package candidates.

The profile discovery directory is application configuration, not UnitTest run
configuration. It is an immutable package input owned by deployment. WPF, CLI,
Core, and loaded profiles must not create it or write derived state below it.
A missing configured discovery directory produces no candidates and may be
reported as a discovery diagnostic. Installing or deploying a profile creates
and populates the directory.

The UnitTest configuration file selects profiles by `accessProfile.id` and
optional `accessProfile.version`; it does not define plugin/package paths.

## Directory Package Layout

A directory package is an immediate child directory of the profile discovery
directory and contains a `manifest.json` file at its root.

```text
<profile-discovery-directory>/
  <package-directory>/
    manifest.json
    docs/
      configuration.md
    <profile assembly and private dependencies>
```

Every directory package must include `docs/configuration.md`. The fixed path is
part of the package contract and is therefore not repeated in the manifest.
The document is owned by the access profile and describes every
profile-specific field below `accessProfile.values`, including:

- value type;
- whether the value is required or optional;
- default behavior when the value is omitted;
- allowed values where the profile constrains them;
- dependencies on other profile fields or session choices;
- representative configuration examples.

This requirement applies to access-profile packages only. Profile-internal
binding packages do not duplicate the profile configuration documentation.
Access-profile builds and package-contract checks must fail when the required
document is absent. Package deployment must preserve the `docs` subdirectory.

Discovery scans:

- immediate child directories of the profile discovery directory that contain
  `manifest.json`.

Discovery does not treat the discovery directory itself as a package directory
and does not recursively scan arbitrary nested directories in v1.

## Manifest Format

The manifest file is `manifest.json`.

```json
{
  "schemaVersion": 1,
  "profile": {
    "id": "siemens",
    "version": "1.0.0",
    "displayName": "Siemens Access Profile",
    "description": "Siemens TIA Portal and PLCSIM Advanced access profile.",
    "contractVersion": "2.0.0"
  },
  "entryPoint": {
    "assembly": "PLCSIM.UnitTest.AccessProfiles.Siemens.dll",
    "type": "PLCSIM.UnitTest.AccessProfiles.Siemens.SiemensAccessProfilePlugin"
  }
}
```

Fields:

| Field | Required | Meaning |
| --- | --- | --- |
| `schemaVersion` | Yes | Manifest schema version. First supported value is `1`. |
| `profile.id` | Yes | Stable access profile family id used by configuration files, for example `fake` or `siemens`. |
| `profile.version` | Yes | Comparable semantic version of this access profile candidate. |
| `profile.displayName` | Yes | User-facing name for selection and diagnostics. |
| `profile.description` | No | Short user-facing description. |
| `profile.contractVersion` | Yes | Version of the PLCSIM.UnitTest access profile contract expected by the package. |
| `entryPoint.assembly` | Yes | Assembly file path relative to the package directory. |
| `entryPoint.type` | Yes | Public concrete type implementing `IAccessProfilePlugin`. |

The loader validates only generic manifest shape, candidate identity, contract
compatibility, and loadability. It does not interpret profile-specific
configuration or vendor technology requirements.

## Candidate Identity

A discovered profile candidate is identified by:

- source kind,
- source path or built-in source id,
- `profile.id`,
- `profile.version`.

`profile.id` comparison is ordinal ignore-case. `profile.version` comparison
uses semantic version ordering. Version strings must be normal semantic versions
for v1, for example `1.0.0`.

## Source Priority And Duplicates

Source priority is used only for deterministic ordering after duplicate
validation has succeeded. It is not an override rule.

Priority:

1. `BuiltIn`
2. `DirectoryPackage`

Duplicate rules:

- Two candidates with the same normalized `profile.id` and the same
  `profile.version` are ambiguous, even if they come from different source
  kinds.
- Ambiguous candidates are not auto-resolved by priority.
- Loading fails with a diagnostic when the requested id/version resolves to an
  ambiguous candidate set.

Selection rules:

- If `accessProfile.version` is present, the loader selects exactly that version.
- If `accessProfile.version` is omitted, the loader selects the highest valid
  semantic version for the requested id after excluding invalid candidates.
- If the selected id/version has more than one candidate, loading fails as
  ambiguous.
- If no selected version exists, loading fails as not found.

This keeps accidental shadowing visible. A later trusted extension model may add
explicit override policy, but v1 does not silently prefer one package over
another when identity collides.

## Discovery Without Heavy Bindings

Directory discovery should read the manifest before loading profile assemblies.
Discovery must not load vendor-heavy API bindings only to populate profile
selection lists.

Directory package assembly loading is a load-boundary step after a candidate has
been selected. Generic assembly/type load failures are loader diagnostics.
Profile-specific dependency and environment failures after the plugin is created
belong to the selected access profile.

## Isolated Directory-Package Loading

A selected directory package is loaded from its already discovered package root.
The application must not require the profile implementation assembly or its
private dependencies beside the application executable.

The generic loader creates one collectible package load scope for one entry-point
activation attempt. The scope:

- resolves the manifest entry assembly below the validated package root;
- rejects entry assemblies, managed dependencies, or native dependencies that
  resolve outside that package root;
- resolves private managed and native dependencies from the owning package;
- reuses explicitly supplied host-loaded public contract assemblies so contract,
  interface, and DTO type identity remains stable;
- rejects a package dependency whose requested shared-assembly identity conflicts
  with the host-loaded identity;
- validates that the manifest entry type is public, concrete, and implements
  `IAccessProfilePlugin`;
- unloads its collectible assembly-load context when its owner disposes it.

`PLCSIM.UnitTest.Common.Application` owns only the domain-neutral package-loading
primitive. Core owns access-profile candidate selection, manifest semantics,
diagnostics, plugin initialization, and access-profile scope lifetime. A profile
that composes internal binding packages owns the corresponding binding loader,
selection rules, diagnostics, and binding scope lifetime.

Temporary registration and project-source evaluation each own and dispose their
plugin instance and package scope after the bounded operation. A successful
`LoadAsync` transfers the plugin and package scope into the loaded session
lifetime. Disposal order is loaded profile session, profile plugin, then package
scope. Releasing a package scope requests collectible unload; physical file
release follows normal .NET collectible-context garbage-collection semantics.

Built-in test composition may use a host-owned plugin instance without a package
scope. Directory packages always use the isolated package path.

## Profile Registration And Registries

Discovery and capability registration are separate operations.

Discovery is cheap and technical. It finds profile candidates and validates the
generic package boundary. It does not decide whether a profile is usable in the
current machine environment.

Profile registration is an explicit application/core operation. It may load a
profile plugin temporarily and call `IAccessProfilePlugin.RegisterAsync` so the
profile can inspect its own environment and technology bindings. The loaded
registration instance must be released after the registration operation; normal
run/session loading remains a separate lifetime.

The application registry is application-owned persistent state. It stores only
generic registration state that WPF/CLI/Core need for selection and setup. The
default registry file name is:

```text
application-profile-registry.json
```

The registry belongs to application-local data, not to a UnitTest run
configuration file and not to an access profile package directory. Applications
should ensure the registry file exists at startup with an empty registry shape
when no registrations have been performed yet.

The application registry stores the latest resolved registration result per
profile candidate identity:

- `profile.id`,
- `profile.version`,
- source kind,
- source path or built-in source id.

The registry also stores a source fingerprint. The fingerprint helps detect
package changes, but it is not part of the candidate key.

Registration states in the application registry:

| State | Meaning |
| --- | --- |
| `Registered` | The profile candidate completed registration and produced usable application-facing capabilities for the current environment. |
| `Unavailable` | The profile candidate is valid, but its expected environment is not currently usable. This is normal state, not a diagnostic by itself. |
| `Error` | Capability resolution or capability validation failed due to a real problem. |

Operation statuses for refresh operations:

| Status | Meaning |
| --- | --- |
| `Completed` | The requested registration operation completed without registration errors. |
| `CompletedWithErrors` | The operation completed, but one or more profile candidates failed, were missing, or produced invalid capability data. |
| `Error` | The registration operation itself failed before a reliable registry update could be produced. |

`RefreshAll` rebuilds the application registry from the currently discovered candidates.
Candidates no longer discovered are removed from the registry. It should continue
when an individual profile reports `Unavailable` or `Error`.

`RefreshProfile` updates one profile candidate. If the requested candidate is no
longer discovered, the stale registry entry for that exact candidate is removed
and the operation completes with errors.

Corrupt registry files are a real application problem. The application/core
should preserve the corrupt file for troubleshooting where practical, reset the
active registry to an empty valid shape, and publish a diagnostic.

Profiles may additionally own profile-internal registry/cache files below the
writable `ProfileDataDirectory` supplied through `AccessProfileContext`. That
state is not the application registry and is not part of the generic package
manifest. A profile-owned registry stores vendor/profile-specific evidence
needed for cheap project-source evaluation and targeted load-time binding
selection. The generic application/core layer treats it as profile-owned
implementation detail.

A profile-owned registry is operational evidence, not a diagnostic history,
scan report, or administration log. Profiles must not persist invalid, ignored,
or failed package candidates as loadable or selected evidence. Detailed
registration findings belong to technical logs and diagnostics. Expected
environment absence remains capability availability state.

The profile-owned registry convention is:

```text
<ProfileDataDirectory>\profile-registry.json
```

Core creates `ProfileDataDirectory` before plugin initialization. If a profile
needs its registry for cheap project-source evaluation and the registry is
missing, unreadable, or corrupt, `EvaluateProjectSource` returns `Error`.
Package-local legacy registries are ignored and are not migrated implicitly.

Expected unavailable environment capabilities, such as a missing installed
vendor product for an otherwise valid profile package, should be represented as
capability availability state. Diagnostics are reserved for real problems such
as invalid manifests, load failures, registration exceptions, invalid capability
data, registry corruption, persistence failures, or dispose failures.

## Project-Source Selection

Manual project-source selection uses the application registry only to find
registered candidate profiles. It then asks each candidate profile through
`IAccessProfilePlugin.EvaluateProjectSource(string projectSource)` whether the
selected source is supported.

`EvaluateProjectSource` must be lightweight. It may inspect the selected source
path and profile-owned registration state, but it must not run discovery,
registration, vendor API probes, project open, runtime creation, worker startup,
or monitoring. It returns:

| Result | Meaning |
| --- | --- |
| `Supported` | The profile-owned registration state can handle the selected project source. |
| `Unsupported` | The selected project source is valid input but does not match the profile's registered capabilities. |
| `Error` | The profile cannot answer reliably, for example because required profile-owned registry state is missing, unreadable, corrupt, or internally inconsistent. |

Configuration-file loading with explicit `accessProfile.id` and optional
`accessProfile.version` does not use project-source filtering as a gate. It
selects the requested profile through the loader and lets `ValidateConfig` and
`LoadAsync` perform the normal profile-owned validation and loading.

## Diagnostics

The loader owns diagnostics for:

- configured profile directory missing or inaccessible,
- package directory without a readable manifest when it was otherwise considered
  a package candidate,
- invalid manifest JSON,
- unsupported manifest schema version,
- missing or invalid generic manifest fields,
- unsupported or incompatible access profile contract version,
- duplicate or ambiguous `profile.id` + `profile.version`,
- requested profile id or version not found,
- entry assembly missing,
- entry type missing or not implementing `IAccessProfilePlugin`,
- entry-point activation failure,
- package path escape or missing private dependency,
- conflicting shared contract assembly identity,
- generic assembly/type load failures before profile-specific initialization.

Diagnostics should include the source kind and source path where practical. They
must not expose vendor API object details as application contracts.

## Built-In Profiles

Built-in profiles do not need a physical manifest file or packaged
`docs/configuration.md`. The host-provided `IAccessProfilePlugin.Info` is
treated as the built-in manifest equivalent. A built-in profile that is also
distributed as a directory package must include the documentation in that
package.

The WPF/Fake development baseline uses the Fake profile as a directory package
with a physical `manifest.json`. Test hosts may still register in-process
profiles directly when a test needs controlled service instances, but that is
test composition and not the application package model.

Built-in profiles must still follow the same identity rules:

- stable `ProfileId`,
- semantic `Version`,
- display name,
- description.

The Fake profile package uses:

| Field | Value |
| --- | --- |
| `profile.id` | `fake` |
| `profile.version` | `1.0.0` |
