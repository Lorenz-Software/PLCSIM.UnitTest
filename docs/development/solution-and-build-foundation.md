# Solution And Build Foundation

This document describes solution ownership, build and test procedures,
binary contract flow, package deployment, and verification boundaries of the
current repository.

## Technical Baseline

The [project specification](../project-specification.md) owns the system
requirements, technology stack and repository layout. Its
[build requirements](../project-specification.md#building-from-source) distinguish
application/shared work from vendor-specific prerequisites.

The commands below assume the repository root as the working directory. Concrete
profiles and bindings own their additional vendor prerequisites and exact commands.

## Solution Ownership

| Solution | Use |
| --- | --- |
| `PLCSIM.UnitTest.Common.sln` | Build the stable neutral and application/profile contract owners. |
| `PLCSIM.UnitTest.App.sln` | Develop WPF, CLI, shared core, exporters, and application-side tests against canonical contract binaries. |
| `PLCSIM.UnitTest.sln` | Application integration view including the generic Fake profile and application tests. It deliberately excludes the Siemens profile and concrete Siemens bindings. |
| Access-profile subtree solutions | Build and test one independently owned profile or binding path. Concrete commands and prerequisites belong to the owning subtree README. |

The root solutions are not a release bundle containing every plugin. A new
access profile or binding does not need to be added to a root solution for the
application to discover its deployed package.

## Canonical Contract Binaries

Application, shared-core, profile, and binding projects consume canonical
contract binaries from:

```text
lib/contracts/
```

The current artifact set is:

```text
PLCSIM.UnitTest.Common.dll
PLCSIM.UnitTest.Common.pdb
PLCSIM.UnitTest.Common.Application.dll
PLCSIM.UnitTest.Common.Application.pdb
PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.dll
PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.pdb
```

Normal application/profile/binding builds do not rebuild or overwrite these
files. This keeps application, profile, and binding development independent and
prevents a project reference from silently changing a public binary boundary.

A normal checkout uses the committed canonical artifacts. When contract source
changes intentionally, update and verify the complete artifact set:

```powershell
.\tools\contract-artifacts.ps1 -Mode Sync
.\tools\contract-artifacts.ps1 -Mode Verify
```

`Sync` builds the Common, Common.Application, and Siemens contract owners in
dependency order and deploys their Release DLL/PDB files. `Verify` rebuilds the
same owners without deployment and compares Release output with
`lib/contracts` by SHA-256. The underlying `DeployContractArtifact` target is
restricted to the known contract projects and Release configuration.

After a contract change, dependent projects should be rebuilt against the
synchronized artifacts. Canonical binary changes are reviewed and committed
with their source changes.

Only the three canonical contract projects disable
`IncludeSourceRevisionInInformationalVersion` and `EnableSourceLink` in
`Directory.Build.props`. Otherwise, a new Git commit changes the informational
version or source URLs embedded in the debug symbols even when contract source
is unchanged, invalidating the byte-for-byte comparison. Assembly versions and
public interfaces are unaffected. PDB files and debugging with local source are
retained; automatic remote source retrieval through Source Link is unavailable
for these assemblies. Release package manifests retain `source.revision` for
provenance.

Contract Release builds also enable deterministic compilation and map the repository
source root to `/_/` using `PathMap`. Contract C# sources use LF line endings,
enforced on checkout by `.gitattributes`; the contract script rejects CRLF input
before building or deploying any artifacts. Both paths and exact source bytes
affect portable PDBs and therefore the DLL/PDB comparison. Automatic embedding of
untracked sources is disabled for these projects so Git tracking/ignore state
does not change the artifact. With the same SDK,
dependencies and build settings, contract artifacts can be reproduced in another
checkout location. Different SDK/compiler versions or operating systems are not
covered by this guarantee.

The path mapping applies only to Release builds of the three contract projects;
Debug builds keep their local source paths. For debugging a mapped Release PDB,
the debugger may need an explicit local source mapping. See Microsoft's
[PathMap compiler documentation](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/advanced#pathmap).
If `Verify` fails, inspect source changes, line endings and the toolchain before
deliberately synchronizing the canonical artifacts; a failed hash check alone
does not establish an API change.

## Application Development

A normal application-side build does not require production access-profile
implementations or Siemens installations. Application tests may compose the
Fake profile as their contract test double:

```powershell
dotnet build PLCSIM.UnitTest.App.sln -c Debug
dotnet test PLCSIM.UnitTest.sln -c Debug
```

The application integration solution includes the generic Fake profile so the
public workflow can be exercised without vendor software. Profile-specific
integration and concrete binding tests remain in their owning subtree solutions.

For a broader application-side check:

```powershell
dotnet build PLCSIM.UnitTest.Common.sln -c Debug
.\tools\contract-artifacts.ps1 -Mode Verify
dotnet build PLCSIM.UnitTest.App.sln -c Debug
dotnet build PLCSIM.UnitTest.App.sln -c Release
dotnet build PLCSIM.UnitTest.sln -c Debug
dotnet test PLCSIM.UnitTest.sln -c Debug
```

## Independent Profile And Binding Development

Each profile and concrete binding is independently buildable through the
solution in its own subtree. Production projects reference canonical contract
binaries rather than application or concrete sibling implementation projects.
Profile-owned test solutions may compose fake bindings for integration evidence.

Use the owning README for the exact command. The stable rule is:

1. verify or synchronize required contract artifacts;
2. build the owning subtree solution or project;
3. run its deterministic tests;
4. deploy its package only when application-level or manual validation is
   required;
5. run opt-in real-runtime validation only on a prepared machine.

The Siemens profile shell can be developed and tested with fake internal
bindings. A concrete Siemens binding can be developed independently of other
binding versions. Building a real binding may require only the Siemens product
version owned by that binding, as documented locally.

## Plugin Package Deployment

Application projects do not reference access-profile or binding implementation
projects. Each plugin project exposes `DeployPluginPackage`:

```powershell
dotnet msbuild <plugin-project>.csproj `
  /t:DeployPluginPackage `
  /p:Configuration=Release `
  /p:PluginDeploymentRoot=<profile-discovery-root> `
  /p:PluginPackageSubDir=<package-relative-path>
```

Both destination properties are required. The target copies the complete build
output recursively while retaining package-relative subdirectories. This
includes the manifest, private dependencies, package-local workers, and
access-profile `docs/configuration.md`. Projects marked with
`IsAccessProfilePackage=true` fail package validation when that required
configuration document is missing. Binding packages are not subject to the
profile-document requirement.

The deploy target does not remove stale destination files. A caller that needs
exact replacement must remove the old package directory first.

## WPF Debug Compositions

The repository helper builds WPF and CLI separately, synchronizes canonical
contracts, replaces the selected local package trees, and verifies deployed
contract hashes:

```powershell
# Real Siemens bindings plus the Siemens profile
.\tools\deploy-debug-plugins.ps1 -Configuration Debug -Composition Real

# Fake profile plus Siemens profile with fake internal bindings
.\tools\deploy-debug-plugins.ps1 -Configuration Debug -Composition Fake
```

The Real composition is the default. It may require the locally supported
Siemens installations needed to build the concrete real bindings. The Fake
composition is the portable application/profile development path.

The resulting WPF roots are:

```text
src/PLCSIM.UnitTest.App.Wpf/bin/Debug/net8.0-windows/profiles
src/PLCSIM.UnitTest.App.Wpf/bin/Debug/net8.0-windows/profiles-fake
```

Equivalent package roots are created below the CLI output.

Start the application from the matching build output after deployment. The
standard discovery directory is `profiles` beside the executable. A normal
application build preserves deployed packages but does not deploy or refresh
them automatically.

For a Fake composition, select `profiles-fake` explicitly and use a separate
application-profile registry. For example, in a dedicated PowerShell session
started at the repository root:

```powershell
New-Item -ItemType Directory -Path .tmp -Force | Out-Null
$env:PLCSIM_UNITTEST_PROFILE_DISCOVERY_DIRECTORY = (Resolve-Path 'src/PLCSIM.UnitTest.App.Wpf/bin/Debug/net8.0-windows/profiles-fake').Path
$env:PLCSIM_UNITTEST_PROFILE_REGISTRY_PATH = Join-Path (Get-Location).Path '.tmp/wpf-fake-profile-registry.json'
& .\src\PLCSIM.UnitTest.App.Wpf\bin\Debug\net8.0-windows\PLCSIM.UnitTest.App.Wpf.exe
```

Use the CLI's own deployed profile root when checking CLI output. Do not reuse
these Fake overrides for a Real session. After adding or changing packages,
refresh profile registrations inside the application before manual testing.

Neither composition copies plugin implementation assemblies beside the
application executable. Profiles and bindings remain directory packages.

## Verification Boundaries

### Pipeline-safe checks

Unless an owning README says otherwise, the following evidence is deterministic
and does not prove a Siemens installation:

- application, shared-core, exporter, and Fake-profile unit tests;
- profile tests using fake bindings;
- binding tests using test-owned workers or deterministic API metadata;
- package layout and public contract conformance tests;
- canonical contract hash verification;
- package loading, unload, cleanup, and process-lifetime tests that use controlled
  test artifacts.

These checks are appropriate for a normal development pipeline.

### Opt-in real-runtime checks

Opt-in tests or native qualification procedures may load vendor engineering or
runtime APIs and can require installed/licensed products, prepared
projects or runtime instances, authorization, and interactive user action.
Their setup, state requirements, filters, cleanup behavior, and evidence are
documented in the owning profile verification or binding validation documents.

Do not infer real vendor API behavior from a successful normal test run. Do not
enable real-runtime tests on a generic pipeline agent.

### Manual WPF/CLI validation

A manual validation additionally depends on:

- current deployed packages and profile registrations;
- the intended Real or Fake package root;
- valid project and runtime targets;
- any vendor authorization needed by the selected binding;
- the runtime state described by the owning validation procedure.

State clearly whether a result came from deterministic tests, an opt-in binding
test, or a manual real-runtime run.

## Build Artifacts And Temporary Data

Normal project output stays below project-local `bin/` and `obj/`
directories. Repository scratch data belongs below `.tmp/` and is not a
project artifact. Canonical contract binaries live only below `lib/contracts/`.
Directory packages are assembled in an explicit deployment root and are not
copied back into source directories.
