# Implementation Structure

## Project Structure And Dependency Direction

The implementation separates contract, workflow, application, profile and test
owners. The solution/build entry points are documented in
[the build foundation](../../development/solution-and-build-foundation.md).

### Projects

The project responsibility groups are:

```text
PLCSIM.UnitTest.Common
PLCSIM.UnitTest.Common.Application
PLCSIM.UnitTest.Core

PLCSIM.UnitTest.App.Wpf
PLCSIM.UnitTest.App.Cli

access-profiles/
  <profile>/
    src/
    tests/
    docs/
    resources/

PLCSIM.UnitTest.Exporters.JUnit

PLCSIM.UnitTest.Testing
PLCSIM.UnitTest.Tests.Core
PLCSIM.UnitTest.Tests.Exporters
```

The tree groups responsibilities rather than enumerating every child project.
Concrete profile/binding solutions and tests are documented by their owning
subtree; they are not migration tasks for the root solutions.

Access-profile source code is organized vertically below `access-profiles/`
because profile and technology binding owners are built independently from the
main application. Common,
Core, application, and exporter projects remain directly below `src/`.

### Project Responsibilities

| Project | Responsibility |
| --- | --- |
| `PLCSIM.UnitTest.Common` | Minimal framework-neutral contracts and DTOs that can be consumed by `net8` projects and API-adjacent `.NET Framework 4.8` workers. This includes PLC-side UnitTest contract values, contract-near event-stream payload DTOs, and required neutral IDs. |
| `PLCSIM.UnitTest.Common.Application` | Application/core/profile-facing contracts and read models that may remain host-facing: diagnostics, operation results/progress, access-profile plugin and service contracts, health, run configuration, target references/contexts, artifact/export contracts, output state, and run-result read models. It may also contain small domain-neutral host-runtime primitives such as isolated plugin-package loading when they are shared by Core and profile-owned loaders. |
| `PLCSIM.UnitTest.Core` | Shared workflow behavior, test-run monitoring, event-stream processing, neutral result interpretation, diagnostic collection/correlation, action gates, output state handling, and read-only run epoch publication. |
| `PLCSIM.UnitTest.App.Wpf` | WPF composition root, UI, view models, manual workflow commands, WPF logging setup, and user-facing presentation. |
| `PLCSIM.UnitTest.App.Cli` | CLI composition root, config file loading, command policy, unattended execution, exit-code mapping, console output, and CLI logging setup. |
| Access profile subtree | Profile package implementation, profile-specific configuration, profile-owned tests, resources, local documentation, and optional technology-specific internal bindings. |
| Siemens-internal binding subtree | Technology binding implementation behind the Siemens profile boundary. Concrete binding versions are documented by their owning subtree, not by this global structure document. |
| `PLCSIM.UnitTest.Exporters.JUnit` | JUnit XML artifact exporter from neutral `UnitTestRunEpoch` projections. |
| `PLCSIM.UnitTest.Testing` | Reusable non-runtime test helpers, sample event batches, run epoch builders, and deterministic test time/evidence utilities. The Fake access profile itself lives in `PLCSIM.UnitTest.AccessProfiles.Fake` so application and core tests can exercise the same profile package boundary as real profiles. |
| `PLCSIM.UnitTest.Tests.*` | Focused test projects by implementation area. |

### Access Profile Source Tree

The source tree for access profiles is:

```text
access-profiles/
  README.md
  <profile>/
    README.md
    src/
    tests/
    docs/
    resources/
    <technology-binding-family>/
      README.md
      <binding-implementation>/
        README.md
        src/
        tests/
        docs/
        resources/
```

This is the repository source layout only. Runtime discovery uses deployed
profile packages below the configured profile discovery directory, not these
source paths.

Documentation reference direction follows the subtree ownership rule: child
documentation may reference parent/global documentation, but parent/global
documentation must not enumerate variable child implementations. Concrete
solutions, tests, resources, and implementation-specific notes belong to the
owning subtree README or local documentation.

Example deployment layout:

```text
profiles/
  fake/
    manifest.json
    PLCSIM.UnitTest.AccessProfiles.Fake.dll
  siemens/
    manifest.json
    PLCSIM.UnitTest.AccessProfiles.Siemens.dll
    lib/
      contracts/
        PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.dll
      bindings/
        <binding-package>/
```

### Common Contract Boundaries

The common contract surface is intentionally split into a neutral layer and an
application-facing layer.

`PLCSIM.UnitTest.Common` is the neutral cross-framework layer. It targets
`netstandard2.0` and may be referenced by `net8` projects and API-adjacent
`.NET Framework 4.8` workers. It may contain:

- PLC-side UnitTest contract enums and values,
- contract-near event-stream payload DTOs,
- neutral IDs required by worker/facade transport, such as
  `CommunicationSessionId`,
- a minimal UTC time source contract and default implementation for
  cross-framework worker code,
- small neutral DTO helpers only when they are needed across the framework
  boundary.

`PLCSIM.UnitTest.Common.Application` is the host/application contract layer. It
targets the application runtime and references neutral `Common`. It may contain:

- `IAccessProfilePlugin`,
- `IAccessProfileSession`,
- `IPlcProjectService`,
- `IPlcControlService`,
- `IUnitTestLifecycleService`,
- `IUnitTestEventStreamService`,
- access profile metadata, package, capability, and configuration models,
- application-facing project, PLC target, runtime target, communication
  session, event batch, run epoch, and output/artifact models,
- operation outcome and progress models,
- diagnostics model and diagnostic snapshots,
- artifact/export abstractions such as `IArtifactExporter`,
- read models consumed by WPF, CLI, exporters, Core, Fake, and tests,
- small application-runtime primitives shared across host/profile boundaries,
  currently the domain-neutral isolated plugin-package load scope.

Neither common project may contain:

- workflow orchestration,
- run-cycle state machines,
- event-stream interpretation behavior,
- output/export execution behavior,
- WPF, CLI, Siemens, PLCSIM Advanced, or TIA Openness dependencies,
- generic utility collections without a clear architecture role.

If a type is only needed by `Core` implementation internals, it stays in
`Core`. If a type is exchanged with applications, access profiles, exporters, or
tests as an application-facing concept, it belongs in
`Common.Application`. If a type must cross the API-adjacent worker boundary, it
belongs in neutral `Common` only when it is not host/application specific.

### Dependency Direction

The intended dependency direction is:

```text
App.Wpf ───────────────┐
App.Cli ───────────────┼──> Core ───> Common.Application ───> Common
                       │
                       ├──> Common.Application ───> Common
                       └──> Exporters.JUnit ───> Common.Application ───> Common

AccessProfiles.Siemens ───────────────> Common.Application
AccessProfiles.Siemens ───────────────> Common
AccessProfiles.Siemens ───────────────> AccessProfiles.Siemens.Contracts

Siemens technology bindings ──────────> Common
Siemens technology bindings ──────────> AccessProfiles.Siemens.Contracts

Testing / Tests ──────────────────────> projects under test
```

Rules:

- WPF and CLI may reference `Core`, `Common`, and selected exporter projects.
- WPF, CLI, Core, Fake, and application-facing exporters use
  `Common.Application` for host/profile contracts and read models.
- WPF and CLI do not reference Siemens binding projects directly during normal
  execution; Siemens bindings are loaded through the access profile/plugin
  composition path.
- `Core` references `Common.Application` and neutral `Common` only. It does not
  reference WPF, CLI, Siemens profile projects, Siemens bindings, or concrete
  exporter implementations.
- The Siemens access profile references `Common.Application`, `Common`, and
  `AccessProfiles.Siemens.Contracts`. It does not reference concrete Siemens
  version binding projects directly.
- Concrete Siemens version binding projects reference `Common` and
  `AccessProfiles.Siemens.Contracts`. They do not reference
  `Common.Application`, the Siemens access profile, `Core`, WPF, or CLI unless a
  specific binding is explicitly promoted to an application-facing component.
- Exporter projects reference `Common.Application` and serialize
  `UnitTestRunEpoch` projections. They do not depend on `Core` implementation
  classes.
- Siemens binding projects reference `Common` and Siemens assemblies as needed.
  They must not expose Siemens API objects as application contracts.

### Package Assembly API Boundaries

An access-profile or profile-owned binding package exposes only its intentional
package API:

- the manifest-selected package entry point;
- contracts and DTOs from the owning contract assemblies;
- explicitly supported declarative configuration models of a development or
  test package.

Concrete sessions, services, parsers, registries, stores, selectors, loaders,
resource scopes, and worker implementation types are package internals. They
must not remain public only so a test in another project can instantiate them.
The generic Fake access profile is the deliberate exception for application test
composition: its plugin, controllable services, and declarative scenario models
form a public development/test surface so Core and WPF tests can drive precise
contract-level behavior. That surface is not a production access-profile
contract and must not be consumed by production composition. Fake Siemens
binding packages expose only their manifest-selected binding entry point plus
intentional declarative scenario models.

Tests follow the same ownership boundaries as production code:

- application and shared-core unit tests may directly compose the generic Fake
  access profile when they require precise control over service behavior;
- Fake-profile implementation tests remain in the Fake profile subtree;
- application package integration and conformance tests consume the Fake access
  profile through its deployed package and public access-profile contracts;
- profile implementation tests remain in the owning profile subtree;
- profile package integration tests use the public profile API and deployed
  Fake binding packages;
- binding implementation and vendor-runtime tests remain in the owning binding
  subtree and do not reference the profile implementation or shared core.

`InternalsVisibleTo` may be used only from an implementation assembly to its
own local test assembly. It must not cross application, profile, or binding
subproject boundaries.

Package-level integration and conformance assertions work with manifests,
paths, assembly names, public contracts, and deployed artifacts. Owning test
solutions may use build-only project references to produce the package artifacts
that they stage, but tests must not inject or inspect another package's internal
implementation objects. Explicit package build and deployment steps produce the
package consumed at runtime.

## Solution Boundaries

The repository uses multiple solution files because the applications, shared
contracts, Siemens profile, and versioned Siemens API bindings have different
build prerequisites.

| Solution | Purpose |
| --- | --- |
| `PLCSIM.UnitTest.Common.sln` | Build the stable neutral and application/profile contract assemblies. |
| `PLCSIM.UnitTest.App.sln` | Develop WPF, CLI, shared core, exporters, and application-side tests against deployed contract binaries. |
| `PLCSIM.UnitTest.sln` | Repository-level main application/shared integration view. It keeps the generic Fake profile because the current application/test baseline uses it, but it is not the catch-all solution for Siemens profile or Siemens binding projects. |
| Access-profile subtree solutions | Develop and verify profile- or binding-owned projects from the owning subtree. Concrete solution paths are documented by the owning subtree README. |

Solution filters are not the primary boundary for this project. They can be
added later as Visual Studio convenience views, but they must not replace the
separate build/development boundaries described above.

## Contract Binary Boundary

`PLCSIM.UnitTest.Common`, `PLCSIM.UnitTest.Common.Application`, and
`PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts` are treated as stable binary
contract boundaries.

Application, shared-core, profile, and binding projects reference the deployed
contract binaries from:

```text
lib/contracts/
  PLCSIM.UnitTest.Common.dll
  PLCSIM.UnitTest.Common.pdb
  PLCSIM.UnitTest.Common.Application.dll
  PLCSIM.UnitTest.Common.Application.pdb
  PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.dll
  PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.pdb
```

The `lib/contracts` directory is versioned with the repository. Contract
binaries are updated only by the explicit repository synchronization helper,
not implicitly when application or plugin projects are built. This keeps plugin
loading stable and prevents apps and plugins from accidentally using different
builds of the same contract assembly.

Canonical contract synchronization and verification are explicit operations:

```text
.\tools\contract-artifacts.ps1 -Mode Sync
.\tools\contract-artifacts.ps1 -Mode Verify
```

The helper builds contract owners in dependency order. Synchronization deploys
their Release artifacts; verification compares Release outputs with
`lib/contracts` by hash. Debug package deployment performs synchronization first
and rejects package-local contract copies that differ from the canonical
artifacts. Detailed commands and composition behavior are documented in
`docs/development/solution-and-build-foundation.md`.

Plugin-package deployment is also explicit, but targets a caller-selected
profile discovery/package root instead of a versioned `lib/plugins` directory:

```text
dotnet msbuild <plugin-project>.csproj /t:DeployPluginPackage /p:Configuration=<configuration> /p:PluginDeploymentRoot=<profile-root> /p:PluginPackageSubDir=<package-path>
```

The owning project is built independently and its complete target output is
copied into the selected package directory. Application builds do not build or
copy profile/binding implementations. Normal Debug/Release builds do not update
`lib/contracts` or a runtime profile discovery directory unless an explicit
deploy target or deployment helper is requested.

## Platform Target Direction

The initial foundation does not force a global `x86` or `x64` platform target.
Projects stay platform-neutral unless a concrete access profile or technology
binding proves that a fixed process architecture is required.

Reasoning:

- existing baseline plugins were managed assemblies and not hard
  32-bit-required,
- PLCSIM Advanced provides version-dependent API assemblies with explicit
  architecture variants,
- TIA Openness assemblies must be validated per concrete target version and
  binding implementation,
- future access profiles may have different architecture constraints.

If a binding later requires a fixed process architecture, that constraint must
be documented in the owning binding/profile design and validated by build/load
tests. The shared contracts should remain architecture-neutral as long as
possible.

### Config Loading And Validation Placement

Configuration handling is split by ownership:

- WPF creates or reads run setup from user selections/config files.
- CLI loads config files and applies command-line overrides.
- `Core` validates common run setup structure and shared prerequisites.
- The loaded access profile's configuration service validates profile-specific
  values through `ValidateConfig(runtimeProvisioningMode)` before final setup.
- Concrete parsing and validation remain profile-internal; applications do not
  select a vendor validator class.

The application and shared core transport profile-specific config payloads but
do not interpret Siemens-specific fields.

### Artifact Export Placement

Artifact export stays event-driven and consumes stable run epoch projections.

The shared workflow publishes exportability from run completion and complete
run evidence. It does not build artifact plans and does not own export execution
state. A small application/composition level `ArtifactExportService` is bound
as an additional workflow publication handler, observes `IsExportable:
false -> true`, and executes export through registered `IArtifactExporter`
implementations. This service is an export observer/execution helper, not a
large workflow coordinator.

`IArtifactExporter` and artifact request/result/state models belong in
`Common.Application`. `PLCSIM.UnitTest.Exporters.JUnit` implements the JUnit
exporter.

WPF and CLI configure artifact output options and consume output
state/diagnostics. They do not directly encode JUnit XML behavior and do not
parse exported artifacts to make workflow decisions.

### Logging Integration Placement

Logging is standard host infrastructure, not a separate shared-core subsystem.

Implementation rules:

- WPF and CLI configure the concrete logging pipeline.
- Applications use `Microsoft.Extensions.Logging` abstractions with
  Serilog as configured provider.
- Shared core, access profiles, Siemens bindings, exporters, and tests depend
  on `ILogger<T>` or `ILoggerFactory`, not on Serilog APIs.
- Application log file naming, rolling, retention, and sink configuration stay
  in the application host configuration.
- The host owns independent `ApplicationLogLevel` and
  `AccessProfileLogLevel` policies. Both default to `Information`.
- The access-profile loader requests a profile-id-specific logger factory from
  the host. Each profile writes to a dedicated
  `access-profile-<profile-id>-<date>.log` family without creating its own
  Serilog sink or retention policy.
- Access-profile log entries should carry enough structured context to identify
  profile version, service area, operation, correlation, and selected
  technology binding where relevant.
- Profile-internal bindings and workers inherit `AccessProfileLogLevel` by
  default. Their existence and optional overrides remain profile/host
  composition details and are not WPF application settings.
- Raw-event logging is opt-in and effective only at access-profile level
  `Debug`. When disabled, event payloads must not be formatted merely for
  logging.
- Diagnostics remain structured application state. Logging records technical
  execution detail and may include diagnostics, but it does not replace the
  diagnostic model.

### Dependency Injection Placement

Dependency injection is owned by the WPF and CLI composition roots. It is not a
project-wide service locator and it is not part of `Common`.

Application composition roots build the application-level service graph:

```text
PLCSIM.UnitTest.App.Wpf
  -> logging
  -> configuration/UI services
  -> shared core services
  -> exporter registration
  -> access profile plugin loader

PLCSIM.UnitTest.App.Cli
  -> logging
  -> command/config loading
  -> shared core services
  -> exporter registration
  -> access profile plugin loader
```

Typical application-level registrations:

```csharp
services.AddLogging(...);
services.AddSingleton(TimeProvider.System);

services.AddSingleton<DiagnosticSink>();
services.AddSingleton<AccessProfilePluginLoader>();
services.AddSingleton<AccessProfileSessionService>();

services.AddSingleton<RunWorkflowService>();
services.AddSingleton<ArtifactExportService>();

services.AddSingleton<IArtifactExporter, JUnitArtifactExporter>();
```

The concrete lifetime may differ between WPF and CLI. WPF may keep one workflow
instance for the interactive application session. CLI normally creates one
workflow instance for the command execution.

The application composition root binds artifact export to workflow publication:

```csharp
workflow.PublicationEventHandler += artifactExports.OnWorkflowPublication;
```

Shared core services receive dependencies through constructor injection.
Examples:

```text
RunWorkflowService
  <- EventStreamProcessor
  <- TestRunMonitor
  <- DiagnosticSink
  <- ILogger<RunWorkflowService>

AccessProfileSessionService
  <- AccessProfilePluginLoader
  <- AccessProfileDiscoveryOptions

ArtifactExportService
  <- IArtifactExporter[]
  <- DiagnosticSink
  <- TimeProvider
  <- ILogger<ArtifactExportService>

TestRunMonitor
  <- TimeProvider
  <- ILogger<TestRunMonitor>
```

Core services must not resolve arbitrary dependencies from `IServiceProvider`.
Short-lived internal state objects may still be created directly by their owning
component.

### Time Placement

Implementation rules:

- Use `TimeProvider` as the application/shared-core time abstraction.
- `PLCSIM.UnitTest.Common` provides the minimal `ITimeSource` contract and
  `SystemTimeSource` implementation for cross-framework worker code that cannot
  depend on `TimeProvider`.
- Do not introduce custom timer, delay, scheduler, or timeout-policy wrappers
  unless a concrete implementation problem proves that `TimeProvider` and
  `ITimeSource` are insufficient.
- Production composition registers `TimeProvider.System`.
- Tests can use `FakeTimeProvider` where deterministic time control is needed.
- `TestRunMonitor` owns test-run timeout policy, timer scheduling, elapsed-time
  properties, and timeout diagnostics.
- `RunWorkflowService` consumes `TestRunMonitor` state/events. It does not poll
  the monitor and does not pass periodic time ticks into it.

### Access Profile DI Boundary

`AccessProfilePluginLoader` is the stateless boundary between application DI and
plugin composition. `AccessProfileSessionService` owns the loaded profile
session lifetime for an application workflow/session.

The host applications register the loader and application/core services. They do
not register Siemens technology bindings, Siemens API sessions, or Siemens API
object models in the application-level container.

Plugin loading should use a small profile context instead of passing the full
host `IServiceProvider` into plugins:

```csharp
public sealed record AccessProfileContext(
    ILoggerFactory LoggerFactory,
    TimeProvider TimeProvider,
    string ProfileRootPath,
    string ProfileDataDirectory,
    OperationalLoggingOptions Logging);
```

Expected plugin load flow:

```text
Application / AccessProfileSessionService
  -> AccessProfilePluginLoader selects a discovered package root
  -> PluginPackageLoadScope loads the manifest entry point and package-local dependencies
  -> AccessProfilePluginLease owns plugin plus package scope
  -> IAccessProfilePlugin.Initialize(accessProfileContext)
  -> IAccessProfilePlugin.LoadAsync(sessionRequest, cancellationToken)
  -> package-owned IAccessProfileSession
  -> RunWorkflowService.BindAccessProfileSessionAsync(...)
  -> session dispose releases profile session, plugin, then package scope
```

The profile context may grow only for stable infrastructure dependencies that
plugins are explicitly allowed to use. It must not become a generic service
locator.

### Siemens Profile Internal Composition

The Siemens access profile owns Siemens-specific composition.

Application/WPF/CLI/Core code must not directly construct or register:

- TIA Openness binding implementations,
- PLCSIM Advanced binding implementations,
- Siemens API sessions or handles,
- Siemens API object model types.

The Siemens profile may construct its internal services manually or use a
plugin-internal DI container if the implementation complexity justifies it.
That internal container is owned and disposed by the Siemens
`IAccessProfileSession`.

Expected Siemens internal composition:

```text
SiemensAccessProfile
  -> Siemens config validator
  -> Siemens environment detector
  -> profile-owned package scope for selected TIA Openness binding
  -> profile-owned package scope for selected PLCSIM Advanced binding
  -> Siemens PLC project service
  -> Siemens PLC control service
  -> Siemens UnitTest lifecycle service
  -> Siemens UnitTest event-stream service
```

Only `IAccessProfileSession` and the four application-facing functional services
cross back into shared core:

```text
IPlcProjectService
IPlcControlService
IUnitTestLifecycleService
IUnitTestEventStreamService
```

### Lifetime Guidance

Application-level lifetime:

- `ILoggerFactory`,
- `TimeProvider`,
- `DiagnosticSink`,
- `AccessProfilePluginLoader`,
- `AccessProfileSessionService`,
- exporter registry or registered exporter implementations.

Workflow/session lifetime:

- `RunWorkflowService`,
- `EventStreamProcessor`,
- `TestRunMonitor`.

Application workflow/session helper lifetime:

- `ArtifactExportService`.

Access-profile lifetime:

- loaded `IAccessProfileSession`,
- functional service implementations,
- Siemens profile-internal services,
- Siemens technology bindings and their package scopes,
- external API sessions, handles, callbacks, and communication resources.

Access-profile resources are released through
`IAccessProfileSession.DisposeAsync()` and workflow cleanup. Directory-package
sessions release the profile session, plugin instance, and collectible package
scope in that order. Profile-internal binding scopes are owned below the profile
session and are released before the outer profile package scope. Plugin-internal
containers, if used, are disposed as part of that access-profile disposal path.

### DI Boundaries

Rules:

- `Common` contains neutral contracts/models. `Common.Application` contains
  application-facing contracts/models plus narrowly shared technical primitives;
  neither project participates in DI.
- WPF and CLI own their composition roots.
- Core uses constructor injection and does not use a service locator.
- Access profile plugins receive an explicit profile context, not the full host
  container.
- Siemens API dependencies stay below the access-profile boundary.
- Exporters are registered in the application composition root and consume
  neutral `UnitTestRunEpochSnapshot` data.
