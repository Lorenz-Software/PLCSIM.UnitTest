# Access Profile Implementation

## `AccessProfilePluginLoader`

`AccessProfilePluginLoader` discovers, loads, and selects access profile plugins
from built-in registrations and configured package locations. The loader is
stateless with respect to loaded profile lifetime. `AccessProfileSessionService`
owns the currently loaded profile session and unloads it for the application
workflow/session.

The generic package and manifest contract is defined in
`docs/contracts/access-profile-package-contract.md`.
The loaded profile and service health runtime contract is defined in
`docs/contracts/access-profile-runtime-contract.md`.

Code entry points:

Code entry points are in [Core/AccessProfiles](../../../src/PLCSIM.UnitTest.Core/AccessProfiles):
`AccessProfilePluginLoader` owns discovery/resolution and package leases;
`AccessProfileDataDirectoryResolver` supplies isolated writable profile roots;
`ApplicationProfileRegistrationService` and `FileApplicationProfileRegistryStore`
own application registration/persistence. Exact option, candidate and registry
models live beside these implementations, not in a separate API sketch here.

Required behavior:

1. Discovery reads only generic access-profile metadata where possible.
2. Discovery must not load vendor-heavy technology bindings just to populate
   profile lists.
3. `AccessProfileSessionService.LoadAsync` selects exactly one candidate by
   `AccessProfileSessionRequest.ProfileId` and optional
   `AccessProfileSessionRequest.ProfileVersion`.
4. Generic loader validation covers profile identity, requested profile version,
   source identity, duplicate candidates, contract compatibility, plugin
   loadability, and `IAccessProfilePlugin` creation.
5. The selected plugin is initialized with `AccessProfileContext` before
   registration, evaluation, validation, or loading is used.
6. The loader passes setup-only intent to the selected plugin; acquisition
   validation of source/ownership and internal dependencies belongs to that
   plugin, not to a generic interpretation of operational values.
7. `IAccessProfilePlugin.LoadAsync` creates the session. Operational JSON is
   subsequently loaded and validated through its configuration service before
   progression to final run setup. The lightweight plugin `ValidateConfig`
   contract is separate; it is not an operational-payload step in this loader.
8. A successful load result must contain an `IAccessProfileSession` that
   provides all four functional services.
9. `AccessProfileSessionService.UnloadAsync` disposes the loaded profile session,
   its owned plugin lease, and its package scope, then clears session-owned state.
   It does not delete plugin files or reset application configuration.
10. Loader diagnostics are returned in result objects and written to the
   application-owned diagnostic sink through the Core diagnostic writer. The
   loader does not expose a producer-specific diagnostic event and does not
   throw for expected plugin/configuration failures.
11. Unexpected programming errors may still throw and are handled by the host
    application boundary.

Runtime reconnect is a loaded-session operation. The application calls
`IAccessProfileSession.ReconnectRuntimeAsync` with the effective configuration,
provisioning mode, prepared runtime context, and effective runtime target. The
profile releases dependent UnitTest communication and revalidates technical
runtime/API access within the existing worker and runtime session while keeping
its published functional service facades stable. It must preserve the selected
target, resource identity, target ownership, and worker/session boundary and
must not route reconnect through project or provisioning operations.

The contract permits caller cancellation only before dependent communication
teardown begins. After teardown starts, the profile must finish a bounded
in-place revalidation or containment path and return success or failure. A
successful result carries the revalidated runtime context. A failed result must
leave truthful service health and preserve the target owner's later release or
cleanup path. Worker or transport loss is terminal for the current loaded
session; automatic worker replacement is not implicit reconnect behavior. The
default interface implementation reports unsupported reconnect as a structured
failure, so profiles are not forced to fabricate reconnect behavior.

Directory-package loading behavior:

1. The discovery directory and every selected package root are read-only
   deployment inputs. Application composition and discovery do not create a
   missing discovery directory.
2. Core creates a `PluginPackageLoadScope` from the selected candidate package
   root and manifest entry assembly. The reusable primitive lives in
   `Common.Application` but does not know access-profile manifests, selection,
   registration, diagnostics, or sessions.
3. The scope resolves private managed/native dependencies only from the package
   and reuses explicitly shared host contract assemblies after strict assembly
   identity validation.
4. `AccessProfilePluginLease` owns the activated plugin and package scope.
   Registration and project-source evaluation dispose their temporary lease.
5. A successful session load wraps the returned profile session together with
   its plugin lease. Disposal releases the profile session first, then the plugin,
   then the collectible package scope, while preserving all cleanup failures.
6. Directory-package implementation assemblies are not copied beside the
   application executable and are not application project references.
7. Built-in test composition remains host-owned and does not receive a package
   scope; it is not the deployed application package path.

Capability registration behavior:

1. The application/core registration service owns the persistent
   `application-profile-registry.json` file and ensures an empty valid registry
   shape exists at application startup.
2. A corrupt registry file is quarantined where practical, replaced with an
   empty valid registry, and reported as a diagnostic.
3. The registration service uses current package discovery as input. It does not
   replace package discovery.
4. `RefreshAllProfilesAsync` rebuilds the registry from current
   candidates and removes stale entries.
5. `RefreshProfileAsync` updates one exact candidate key and removes a stale
   entry when that candidate is no longer discovered.
6. Registration may temporarily resolve/load a profile plugin to call
   `IAccessProfilePlugin.RegisterAsync`. The temporary load is independent from
   `AccessProfileSessionService` and must not leave a loaded run/session
   profile alive.
7. The registration service validates returned shared capability DTOs before
   persisting them.
8. Expected unavailable capabilities are stored as registration/capability
   state. Diagnostics are used for real problems such as invalid capability
   data, load failures, registry corruption, persistence failures, or unexpected
   exceptions.
9. The registration service also owns a side-effect-free currentness check. It
   compares discovered candidate identities and source fingerprints with the
   persisted registry and reports `Current`, `RefreshRequired`,
   `NoProfilesDiscovered`, or `Failed`.
10. The currentness check does not activate profile packages, call
    `IAccessProfilePlugin.RegisterAsync`, or modify the registry. Applications
    decide whether to present an explicit refresh action or fail their preflight.

WPF and CLI should read profile usability from the registry. They should not
trigger live profile capability checks as a side effect of opening a selection
list.

`AccessProfileContext` is the plugin-facing context. It must not become the
general shared Core service context. The application/Core composition may use a
broader Core-owned context internally, but the plugin-facing context stays
limited to stable infrastructure, immutable selected package-root information,
and one host-provided writable profile-data directory.

Core derives the profile-data directory below the configured per-user data root
from profile id, version, source kind, and normalized source path. A SHA-256
source key isolates independently deployed copies without publishing the source
path as a directory name. Core creates this directory before plugin
initialization. Profiles write reproducible private registry/cache state only
there and never below `ProfileRootPath`.

Access-profile plugins and profile services do not receive the application
diagnostic sink or diagnostic role-provider. They publish diagnostics through
the existing access-profile events and result objects; Core/application code
decides when those diagnostics are written into the application-owned
diagnostic sink.

Access-profile plugins and services receive a profile-specific
`ILoggerFactory` and an application-owned operational logging policy through
`AccessProfileContext`. They log through `ILogger<T>` or `ILoggerFactory`
only; they do not create the profile file sink, depend on Serilog APIs, or own
profile-log retention. The host routes every profile id to a dedicated
`access-profile-<profile-id>-<date>.log` family.

The operational policy carries the profile minimum level and the effective
raw-event logging flag. The latter is true only when explicitly enabled while
the profile level is `Debug`. Profile-internal components and workers inherit
that policy unless their owning host/profile composition supplies an explicit
override.

Access-profile log entries should be structured enough to distinguish:

- profile id/version,
- service area (`Profile`, `Project`, `PlcControl`, `UnitTestLifecycle`, or
  `UnitTestEventStream`),
- operation name and correlation id where available,
- selected technology binding identity where the profile composes additional
  implementation-specific bindings,
- bounded result, duration, cancellation/timeout, health, and cleanup evidence.

The host creates one log family per normalized profile id. Profile-specific
implementations may be noisy internally, so hot-path technical detail must stay
behind the configured access-profile log level and should use counts, ranges,
counters, or summaries instead of unbounded per-event logging.

Source and duplicate behavior:

- built-in profiles and directory package profiles are both discovery
  candidates,
- a duplicate is the same normalized profile id and the same profile version,
  not merely the same profile id,
- duplicates are ambiguous even when they come from different source kinds,
- source priority is not used to silently override an ambiguous id/version,
- when no version is requested, the loader selects the highest valid semantic
  version for the requested profile id,
- when a version is requested, the loader selects exactly that version.

The loader does not interpret Siemens-specific configuration and does not load
technology bindings directly. Internal technology binding selection remains
owned by the selected access profile.

## Common Access Profile Contracts

`Common.Application` owns the access profile contract because applications, shared core,
plugins, and tests all need the same language for profile loading and service
binding.

`Common.Application` owns the canonical application-facing code:
- [AccessProfileContracts.cs](../../../src/PLCSIM.UnitTest.Common.Application/AccessProfiles/AccessProfileContracts.cs):
  plugin/session lifecycle, immutable acquisition and operational snapshots.
- [AccessProfileConfigurationContracts.cs](../../../src/PLCSIM.UnitTest.Common.Application/AccessProfiles/AccessProfileConfigurationContracts.cs):
  typed configuration projection/editing, including empty field projections.
- [AccessHealthModels.cs](../../../src/PLCSIM.UnitTest.Common.Application/Services/AccessHealthModels.cs):
  profile/service trust updates.

The [runtime contract](../../contracts/access-profile-runtime-contract.md)
defines lifecycle, consistency and configuration semantics. This implementation
map does not maintain parallel interface/enum declarations.

Direction:

- an initialized plugin instance is bound to one immutable package root and one
  writable profile-data directory before registration, evaluation, validation,
  or loading is used,
- explicit profile registration returns shared capability DTOs for application
  registry persistence and may update profile-owned registry/cache state only
  below `ProfileDataDirectory`, but it does not create a loaded workflow/session
  profile,
- a loaded access profile session must provide all four functional services,
- profile self-information is available for diagnostics and UI/CLI feedback,
- profile-specific config is transported through `AccessProfileConfig.Values` as
  profile-owned raw JSON text,
- `StateChangedEventHandler` remains a coarse profile lifecycle signal,
- `AccessHealth` is the application-facing context-trust signal used for
  workflow gates, invalidation, recovery decisions, and status publication,
- `IAccessProfileSession.HealthChangedEventHandler` is the single event surface for
  profile and functional-service health changes; individual updates are
  identified by `AccessServiceKind`,
- access profile loading is independent from final `RunSetup` configuration,
- `AccessProfileSessionRequest` carries immutable session setup only: profile
  identity/version, project source, and project ownership; it does not carry
  profile-owned operational values,
- the generic loader uses profile id/version for candidate selection and passes
  source/ownership to the selected profile without interpreting profile-owned
  values,
- the application retains the current `AccessProfileConfig` snapshot and passes
  it explicitly to discovery and prepare request boundaries,
- project/PLC/runtime discovery is provided through application-facing service
  contracts after the profile is loaded and before final setup binding,
- discovery may open/read profile-owned context when required by the underlying
  technology, but it must not build, transfer, start a PLC, or use UnitTest
  lifecycle/event-stream communication,
- UnitTest communication preparation is performed after the PLC is running and
  before lifecycle or event-stream operations are used,
- validation of profile-specific fields remains inside the profile,
- every loaded session exposes the lightweight
  `IAccessProfileConfigurationService`; profiles without configurable values
  provide an empty field projection,
- the profile-owned configuration service holds the current editing snapshot;
  successful typed add/set/remove/load operations replace that snapshot, failed
  operations leave it unchanged, and unrelated, inactive, and non-projected
  payload values are preserved,
- the application reads the immutable `AccessProfileConfig` snapshot from the
  service and passes that value, never the service instance, into discovery and
  final run setup,
- the generic editor may validate primitive syntax, required values, and allowed
  values locally, while profile `ValidateConfig(runtimeProvisioningMode)`
  remains authoritative for the complete configuration and progression gate,
- `PrepareUnitTestCommunicationAsync` is a profile-level gate, not a fifth
  functional service; it validates the UnitTest communication contract after the
  PLC is running and before lifecycle/event-stream operations are allowed,
- the access profile owns internal consistency across its services; when a fatal
  internal problem invalidates shared profile resources, the profile moves all
  affected services into a consistent invalidated state and notifies the shared
  core,
- plugin-internal implementation details such as Siemens DLLs, sessions, or API
  handles are not exposed through `AccessHealth`; profiles map those details to
  one or more affected `AccessServiceKind` values plus stable reason/problem
  data,
- the access profile performs local containment such as stopping monitoring,
  invalidating sessions, closing internal handles, and reporting stable
  diagnostics,
- the shared core decides workflow impact, action gates, run epoch state,
  recovery path, and output behavior from stable profile/service state.

## Access Profile Setup And Release Boundaries

The loaded access profile must support these shared workflow boundaries:

| Boundary | Meaning | Profile Responsibility |
| --- | --- | --- |
| Load profile | Create a session from setup-only `AccessProfileSessionRequest`. | Validate acquisition identity/source/ownership, compose internal dependencies and expose required services without retaining operational payload values. |
| Discovery | Provide application-facing candidates for project/PLC/runtime selection. | Hide vendor object models and return stable references/projections only. |
| Final setup binding | Accept final selected setup through functional service operations. | Use selected references/config values to prepare concrete project/runtime resources. |
| UnitTest communication | Validate UnitTest contract and create a communication session after PLC start. | Keep lifecycle/event-stream services internally consistent for the session. |
| Scoped release | Release only the requested lifecycle level where possible. | Release communication, runtime, project, or full profile resources without forcing unnecessary profile unload. |
| Profile unload | Dispose the loaded profile. | Release all remaining internal resources and move services to a disposed/invalidated state. |

Application/Core owns each overall operation deadline and caller-cancellation
classification. Profile services consume the propagated token, publish truthful
state/health, and return structured results. Profile-internal bindings own
technical transport, worker, protocol, and vendor-call containment.

Caller cancellation after dispatch is not rollback. A profile/binding that can
drain a known terminal response keeps a healthy session reusable and preserves
ownership of any retained side effect. Failed drain, correlation loss, transport
loss, or hard containment expiry invalidates the affected boundary. Release and
cleanup are bounded owner operations and are not interrupted by the WPF cancel
command.

`IAccessProfileSession.DisposeAsync` remains the final full-profile release mechanism.
It is not the only release path used by shared workflow back-navigation.
