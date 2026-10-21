# Siemens Profile Implementation

This is the code map for the Siemens profile shell, its registration state and
its session composition. It describes the implemented ownership boundaries,
not a delivery plan or a second set of binding contracts.

## Documentation Boundaries

- [Profile architecture](../access-profile.md): project ownership, resource
  lifetime, recovery and application-facing boundaries.
- [Configuration](../../configuration.md): payload fields, defaults and typed
  editing; do not duplicate its configuration tables here.
- [Deployment](../../deployment.md): directory packages and explicit TIA
  Openness authorization.
- [TIA binding contract](../../../tia-openness/docs/contracts/tia-openness-binding-contract.md),
  [PLCSIM binding contract](../../../plcsim-advanced/docs/contracts/plcsim-advanced-binding-contract.md)
  and [worker IPC](../../contracts/worker-ipc-contract.md): internal interfaces
  and transport semantics.
- Shared [runtime](../../../../../docs/contracts/access-profile-runtime-contract.md)
  and [package](../../../../../docs/contracts/access-profile-package-contract.md)
  contracts: application-facing requirements, not Siemens API policy.

Concrete binding subtrees own product/API prerequisites, supported project
extensions, worker implementations and qualification. The profile shell
consumes their declarations; it does not maintain its own product/version or
project-extension table.

## Code Owners

All shell classes below live in
[`src/PLCSIM.UnitTest.AccessProfiles.Siemens`](../../../src/PLCSIM.UnitTest.AccessProfiles.Siemens).

| Component | Responsibility |
| --- | --- |
| `SiemensAccessProfilePlugin` | Initializes one package/host context; coordinates registration, source evaluation and session acquisition. |
| `SiemensBindingPackageDiscovery` | Reads immediate binding child directories and validates descriptor/entry-path metadata. |
| `SiemensProfileRegistrationOrchestrator` in `SiemensProfileRegistration.cs` | Loads bounded readiness probes, maps declarations to shared capabilities and returns usable candidate evidence. |
| `FileSiemensProfileRegistryStore` in `SiemensProfileRegistry.cs` | Persists the profile-private registry under the supplied writable profile-data root. |
| `SiemensSelectedBindingSelector` | Selects a route from persisted evidence and revalidates only the selected package paths. |
| `SiemensSelectedBindingLoader` / `SiemensBindingPackageLease` | Acquire initialized binding entry points in isolated package scopes; roll back partial acquisition. |
| `SiemensAccessProfileSession` | Composes configuration and four functional services and routes their health. |
| `SiemensProfileConfigurationParser` / `SiemensProfileConfigurationService` | Parse profile-owned JSON and provide side-effect-free typed edits/snapshots. |
| `SiemensTiaPlcProjectService` / `SiemensExternalPlcProjectService` | Provide managed engineering or the externally managed project boundary. |
| `SiemensPlcsimPlcControlService` | Owns runtime binding access, target preparation, PLC state and explicit resource-release mapping. |
| `SiemensPlcsimCommunicationSessionOwner` | Owns the binding communication session shared by the lifecycle and event-stream adapters. |
| `SiemensPlcsimUnitTestLifecycleService` / `SiemensPlcsimUnitTestEventStreamService` | Project normalized observations/batches through the application contract. |
| `SiemensProfileResourceScope` | Tracks acquired resources for reverse-order, best-effort cleanup. |

The shell references canonical shared and Siemens contract assemblies, not
concrete binding implementations. Binding facades and API-adjacent workers
exchange Siemens-internal neutral DTOs. Siemens API objects and worker handles
do not cross the application boundary.

## Registration And Package Evidence

The generic application loader discovers the profile package. It does not
discover Siemens bindings. `RegisterAsync` asks the shell's discovery component
to inspect immediate child directories of `<ProfileRootPath>/lib/bindings`.
Each binding descriptor is named `manifest.json`; it is distinct from the
access-profile manifest at the profile root. The descriptor schema is defined by
[`SiemensBindingModels.cs`](../../../src/PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts/SiemensBindingModels.cs).
It separates `info` (binding kind/package version/display name), `entryPoint`
(relative assembly and `typeName`) and `declaration` (API/source/runtime and
UnitTest contract support). Concrete binding manifests supply deployed
identities; a product/API version is not the binding package version.

Discovery checks descriptor shape, recognized binding kind, relative entry
assembly/type metadata and package-local files. Directories without a
descriptor are ignored. A missing binding root produces no candidates; it does
not cause a fallback scan elsewhere. Descriptor and package fingerprints become
technical evidence, not user configuration.

The orchestrator loads a candidate facade only for its readiness operation,
checks its identity against its descriptor and releases the lease afterward.
Product registry/API-path/loadability checks belong to that binding, not the
shell. Registration does not open engineering projects, create/start runtimes
or connect PLC communication. Expected missing products are unavailable
capabilities; malformed packages, failed probes or persistence errors are real
registration errors.

A usable PLCSIM binding provides the externally managed route. A usable TIA
binding is additionally needed for a matching application-managed project
source. Registration must not require TIA merely to use the external route.
Usable binding evidence is persisted at
`<ProfileDataDirectory>/profile-registry.json`; the selected package remains
immutable. Failed registration must not leave previous candidate evidence
trusted for subsequent acquisition.

Shared Core separately persists `ApplicationProfileRegistry` output. Its
profile/capability view is not the shell's candidate catalog. Concrete binding
paths, fingerprints and entry points remain profile-private state; active API
objects, sessions and runtime targets belong in neither persistent registry.

## Source Evaluation And Session Acquisition

`EvaluateProjectSource` reads profile registry identity/state and checks whether
the recorded TIA source declaration and a compatible PLCSIM candidate can serve
the supplied source. It does not start workers, probe installed products or
refresh registration. Missing/unreadable registry state is an error; a valid
registry with no matching route is unsupported.

`LoadAsync` receives setup-only `AccessProfileSessionRequest`: profile
identity, source and ownership mode. Operational `AccessProfileConfigurationPayload`
is not retained or applied during acquisition.

1. Check request/profile identity and the required managed source.
2. Read and validate persisted profile identity, state and candidate evidence.
3. For managed projects, select exactly one matching TIA source declaration and
   one compatible local PLCSIM runtime/UnitTest declaration.
4. For external projects, select PLCSIM without loading TIA. When an optional
   source is supplied, its TIA declaration/path still constrains route validity.
5. Validate selected paths, descriptor identities/declarations and fingerprints.
   Missing, ambiguous or stale evidence fails; there is no sibling-package scan,
   alternate binding guess or implicit registration.
6. Acquire the selected TIA lease when required, then the PLCSIM lease; verify
   required interfaces and compose the session. Partial failure unwinds acquired
   resources before returning failure.

Session acquisition does not open the TIA project, resolve a PLC instance,
build, download, start a PLC or start communication monitoring. Registration
and new loads are rejected while the plugin owns an active session; plugin
operations are coordinated so resources cannot be mixed across acquisitions.

## Configuration And Functional Operations

The application owns the configuration draft and final run setup. The profile
configuration service provides immutable, typed-editable snapshots without
vendor side effects. Discovery and prepare operations receive their current
configuration explicitly; discovery does not establish preparation state.
Provisioning and application deadlines are shared run intent, not Siemens
payload or binding-selection fields.

Managed project discovery opens the selected TIA source using its declared
direct/archive mode. Discovery returns neutral candidates without applying the
configured PLC id as a discovery filter. Shared selection rejects unknown ids;
preparation/final resolution still validate the selected target. Genuine
engineering failures remain operation failures, not selection failures.

The project adapter maps the selected PLCSIM runtime to the Siemens transfer
target; TIA owns compile/download mechanics. Successful transfer retains
completed setup evidence while releasing transient TIA engineering access.
Later managed operations can reopen the engineering session. Archive workspace
ownership and cleanup belong to the TIA binding, never to the selected archive
or a caller-owned base directory.

Runtime preparation applies `UseExisting` or `CreateOwned` through the selected
PLCSIM binding. The effective owned storage path is resolved during preparation
and retained for reconnect/release; parsing does not allocate it or mutate a
global PLCSIM default. PLC command results report API acceptance. Independent
state observations, including external changes, determine actual PLC readiness.

The lifecycle and event-stream adapters share one prepared binding communication
owner. They forward observations/batches rather than independently reading
Siemens symbols. The PLCSIM worker owns capture, FIFO plausibility, release
writes, trigger selection and bounded transport. Shared Core owns run epochs,
result interpretation and export eligibility. The binding contract defines the
write boundary and release-before-publication behavior; neither UI rendering
nor result aggregation is part of the PLC intake hot path.

## Recovery, Diagnostics And Release

Runtime reconnect keeps the selected runtime, provisioning ownership and PLCSIM
worker/session. It releases technical communication first and revalidates the
same target. It neither invokes TIA nor creates/releases a different target.
Shared Core prepares new communication only when the observed PLC state permits
it. Worker/pipe loss is terminal for reconnect in the current session.

Final resource release is explicit: `KeepResource` retains the existing target;
`DestroyResource` requests owned target shutdown/unregister/storage cleanup.
Technical close and known resource postcondition are reported independently.
Fallback disposal cannot infer permission to destroy a runtime.

Each adapter detaches callbacks and clears its active session before awaiting
close. The shared communication owner closes once after its last adapter
detaches; late callbacks cannot update released state. Session disposal detaches
health forwarding and is idempotent. Best-effort cleanup continues after an
individual release failure without resetting PLC UnitTest state or deleting
source projects, application artifacts or logs.

Diagnostics are published at the owner boundary, not duplicated at every layer.
Successful discovery carries mapped diagnostics in its result for workflow
publication; failed discovery emits them through the service event. Service
health expresses trust, not progress or final result meaning. Logging uses the
host policy; workers write separate bounded logs under the host log directory,
not through the protocol stream. Credentials/raw JSON and unbounded PLC dumps
are not diagnostic output. See [troubleshooting](../../troubleshooting.md) and
the binding/IPC contracts for concrete logging and failure boundaries.

## Test Boundaries

The neutral Fake profile exercises shared application behavior. Siemens fake
bindings instead exercise shell composition through the same internal package
and contract boundary as real bindings. Neither replaces vendor qualification.

Profile tests cover readiness/registry routes, descriptor/fingerprint mismatch,
selected-path loading, operational configuration, diagnostics, health, rollback,
stale callbacks and repeated cleanup. Binding-owned tests cover worker/protocol
and direct-access behavior. The [profile README](../../../README.md) and owning
binding READMEs give build/test commands and explicit real-runtime prerequisites.
