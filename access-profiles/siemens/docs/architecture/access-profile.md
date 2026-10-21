# Siemens Access Profile Architecture

This document describes the Siemens-specific access profile design and its internal binding responsibilities.

## Purpose

The Siemens access profile provides the concrete profile implementation for the selected Siemens toolchain while preserving the application-facing service boundaries defined by the access profile layer.

The [implementation overview](implementation/profile-composition.md) maps these
boundaries to the current code. Supported products, versions and live-validation
limits belong to the concrete binding subtrees, not an initial delivery plan.

## Design Boundary

The Siemens access profile must keep Siemens API object models, version-specific dependencies, installation detection details, and concrete communication mechanics inside Siemens-specific implementation projects. WPF, CLI, and shared core consume only application-facing access-profile contracts and functional services.

The Siemens profile composes separate technology bindings for engineering/project access and simulation/runtime/UnitTest communication where that separation is required by the Siemens APIs and installed product model.

## Project Handling And Resource Lifetime

The Siemens access-profile session receives the application-selected project
ownership mode:

- `ApplicationManaged` selects both the required TIA Openness binding and the
  PLCSIM Advanced binding. Project discovery opens a bounded TIA engineering
  session. Successful build/download records durable transfer evidence before
  the profile closes the TIA project session and worker. The selected binding
  package, PLCSIM runtime/PLC context, communication, and access-profile session
  remain independently owned.
- `ExternallyManaged` selects the PLCSIM Advanced binding without opening a TIA
  project or starting the TIA engineering-session worker. An optional project
  source may constrain profile/binding selection but is not opened, built, or
  transferred. Runtime and PLC-instance preparation remain profile-owned.

Profile registration is `Registered` when at least one usable PLCSIM Advanced
binding provides the required local runtime and UnitTest contracts. This makes
the external-project path available without requiring an installed or usable
TIA Openness binding. Application-managed project handling additionally
requires a usable TIA Openness candidate matching the selected project source;
that requirement is enforced by load-time route selection. Published TIA access
API and project-source capabilities remain unavailable when no usable TIA
binding exists.

Load-time selection consumes only the persisted profile-owned registry and the
selected package descriptors. It rejects structurally incomplete registry data,
ambiguous candidates, stale package fingerprints, and descriptor identity,
entry-point, or capability declarations that differ from registration
evidence. It does not implicitly refresh registration or rescan sibling binding
packages.

One Siemens profile plugin instance is initialized exactly once for one profile
root and host context. Registration, project-source evaluation, and profile
session loading are mutually exclusive top-level operations. Registration and
additional loads are rejected while that plugin owns an active profile session;
the session releases this ownership when it is disposed. Plugin disposal is
rejected while a top-level operation or owned session is still active, so
context, logger, binding-loader, and package ownership cannot be mixed across
lifetimes.

Selected binding acquisition is transactional. The profile acquires the TIA
binding first when managed project handling is requested and then the PLCSIM
binding. Ownership transfers to a profile session only after all selected entry
points were initialized and their required contracts were verified. Any
initialization, inspection, contract, or session-composition failure rolls the
partial acquisition back in reverse order. A binding entry point created before
its initialization fails is disposed before its collectible package load scope
is released.

A later managed project operation may reopen the TIA engineering session and
resolve the configured project and PLC target again. Live TIA project access is
therefore a transient resource, not the authority for completed setup/transfer
evidence.

PLCSIM Advanced remains the authority for the observed runtime-instance and PLC
operating state. A published state with `ReadyForExecution` permits shared core
to prepare UnitTest communication whether the PLC was started by the application,
was already running when attached, or was started externally. Siemens command
results report API request acceptance and do not replace those observations.

Runtime reconnect releases the shared PLCSIM UnitTest communication session and
asks the existing PLCSIM runtime session and worker to revalidate technical API
access for the already selected PLC instance. It preserves the worker and
binding session, effective target, resource identity, provisioning mode, profile
configuration, and target cleanup ownership. It does not invoke TIA Openness,
reopen a project, build, download, create or register a PLC instance,
release/delete the target, replace the worker, or start/stop the PLC. The public
lifecycle and event-stream adapters remain stable while their shared technical
communication owner is recreated when needed.

Caller cancellation is honored before communication teardown. Once teardown
starts, the Siemens profile uses a bounded internal reconnect deadline and
returns success or failure. Successful revalidation publishes the observed PLC
state; Shared Core then starts UnitTest communication and monitoring only when
that state is ready for execution. A stopped PLC remains attached with
communication inactive. Failed revalidation keeps target ownership and supports
owner-driven release; worker or transport loss is terminal for reconnect in the
current loaded session.

Final runtime release is explicitly authorized by Shared Core. For an external
runtime, the Siemens profile receives `KeepResource` and closes only worker,
session, callback, and API access. For an application-owned runtime, it receives
`DestroyResource` and asks the selected PLCSIM binding to shut down, unregister,
and clean the owned storage before technical close. In-place reconnect does not
change that retained release responsibility.

The Siemens profile returns the known resource postcondition independently from
technical close success. A completed destroy therefore remains
`ResourceDestroyed` if a later worker close fails. Unsupported destruction or
an unknown failure remains unsuccessful and must not be presented as successful
cleanup. Profile-session fallback cleanup closes technical access only and does
not derive destruction from Siemens configuration.

One profile-internal owner holds each prepared PLCSIM UnitTest communication
session shared by the lifecycle and event-stream adapters. The adapters own only
their subscriptions and projected state. Releasing either adapter detaches it
and resets its state; the shared owner closes the binding session exactly once
after the last adapter detaches, independent of release order. Monitoring-stop
or adapter cleanup failure does not leave the other adapter as an implicit
session owner. Callbacks from the released session are ignored.

TIA project and PLCSIM runtime adapters follow the same lifetime boundary for
their own binding sessions: detach callbacks and clear the active reference
before awaiting close, reject callbacks whose sender is no longer current, reset
their projected context, and report a successful explicit release as
`NotEstablished`. Profile-session disposal first detaches all service-health
forwarding and is idempotent for concurrent or repeated callers. Cleanup-time
service callbacks therefore cannot escape through an already released profile
session. A failure isolated to one functional service remains reported under
that service kind and does not rewrite profile health or unrelated service
health.

## Verification Boundaries

Deterministic profile-shell tests verify registration routes, registry-backed
selection and revalidation, plugin-operation exclusion, transactional binding
rollback, shared communication ownership, stale-callback rejection, diagnostic
publication ownership, package-scope release, and repeated session disposal
without loading Siemens software.

Opt-in real profile validation loads the deployed Siemens directory package
through the generic access-profile loader. It repeats both an externally managed
session and an application-managed session against the production binding
packages. The validation confirms worker termination, project-lock release,
release of package entry assemblies, and bounded post-warm-up worker process,
thread, handle, and private-memory observations. The profile-level test uses an
existing user-owned PLCSIM instance and must not unregister or delete it.
Owned-runtime creation, unregister, and temporary-storage cleanup are verified
at the PLCSIM Advanced binding boundary. Together these checks provide bounded
lifecycle evidence; they do not claim exact garbage-collection timing,
long-running leak freedom, or sustained performance and backpressure behavior.

## Profile Configuration

The generic application-layer run setup may contain a Siemens-specific profile
configuration section. The application layer selects and transports that
section; the Siemens access profile owns its interpretation, editing, defaults,
and validation. Runtime provisioning mode is separate application-owned run
intent and is not inferred from the Siemens payload.

The Siemens profile configuration should be minimal and target-oriented. It
should identify the intended Siemens target and leave internal technology,
binding, download, communication, and recovery decisions to the Siemens access
profile wherever a safe default is possible.

The profile configuration covers these responsibilities:

| Configuration Area | Purpose |
| --- | --- |
| Engineering project source | Uses the shared top-level `project.source` field. The Siemens payload must not duplicate the project path. |
| PLC reference | Identifies the PLC/device/CPU target inside the project when it is not unambiguous. |
| Runtime reference | Identifies the local PLCSIM Advanced runtime target name/identifier when the access profile should attach to or reuse a specific runtime. |
| UnitTest communication target | Provides lifecycle/event-stream binding hints only if the Siemens profile cannot derive them safely from the project and PLC-side contract. |

The configuration should not expose Siemens API binding strategy, explicit TIA
Openness strategy, explicit PLCSIM Advanced strategy, compile modes, download
modes, application operation deadlines, low-level IPC timing, or internal
recovery strategy unless implementation proves that a safe automatic decision
is not possible. UnitTest observation strategy, monitoring/drain cadence, and
maximum published EventBuffer batch size are profile-owned intent because they
alter the supported communication behavior.

The Siemens access profile should detect installed Siemens APIs, select
compatible technology bindings, derive project/runtime/UnitTest communication
details where possible, and return diagnostics when a required target decision
is missing or ambiguous.

The concrete payload is defined by [Siemens configuration](../configuration.md)
and the surrounding shared configuration-file contract. The application transports Siemens
payload values as opaque JSON; the Siemens profile owns their interpretation.

The loaded Siemens session exposes the required generic profile-configuration
boundary. It projects these currently supported semantic fields without exposing
Siemens JSON paths to WPF or shared core:

| Field | Applicability |
| --- | --- |
| Runtime instance name | Required for `CreateOwned`. |
| Runtime storage path | Optional for `CreateOwned`. |
| UnitTest RunControl root | Required. |
| UnitTest EventBuffer root | Required. |
| Communication observation strategy | Required; allowed values are supplied by the profile. |
| Monitoring interval | Required invariant positive integer milliseconds. |
| Drain interval | Positive integer milliseconds with a profile-owned default. |
| Maximum events per batch | Positive integer capped at runtime by the PLC-reported EventBuffer length. |

Projection and editing are lightweight and have no vendor/runtime side effects.
The session-owned configuration service holds the current application-neutral
snapshot. Successful typed edits update that snapshot while preserving unknown
and inactive Siemens values; failed edits leave it unchanged. The application
copies the immutable snapshot into discovery and final run setup and never
passes the service instance to shared workflow. The profile's context-aware
validation remains authoritative before setup proceeds; binding support that
cannot be determined without activating an operation is still reported by that
later binding operation.

## Related Contracts

The engineering and runtime/communication boundaries are defined in the
[TIA Openness binding contract](../../tia-openness/docs/contracts/tia-openness-binding-contract.md)
and the [PLCSIM Advanced binding contract](../../plcsim-advanced/docs/contracts/plcsim-advanced-binding-contract.md).
Concrete API prerequisites and validation limits belong to each binding's documentation.
