# Functional Service Implementation Map

## Common Functional Service Contracts

The four functional services are application-oriented abstractions. They model
what the applications and shared core need, not the Siemens APIs below them.

The [runtime contract](../../contracts/access-profile-runtime-contract.md)
defines service semantics. Exact interfaces, including scoped release and
communication operations, live in
[AccessProfileServiceContracts.cs](../../../src/PLCSIM.UnitTest.Common.Application/Services/AccessProfileServiceContracts.cs).
Adapters consume these canonical contracts rather than a copied API sketch.

Direction:

- commands and externally controlled workflow operations are asynchronous,
- plugin creation, config validation, current state, and current observations
  are synchronous,
- operations return structured results and diagnostics,
- event-stream intake is push/event based,
- diagnostics and state changes are pushed through profile/service events,
- `ServiceState` is the coarse lifecycle/progress state of a functional service,
- `AccessHealth` is the normalized application-facing trust state of the
  service context; it is the surface used by shared workflow for access-health
  gates, context invalidation, recovery decisions, and status snapshots,
- `AccessHealthState.NotEstablished` is valid before the corresponding context
  has been prepared and does not block actions by itself; blocking starts when a
  previously relevant context reports `RevalidationRequired`, `Invalidated`, or
  `Disposed`,
- access profiles publish service health changes through the loaded
  `IAccessProfileSession.HealthChangedEventHandler` so the shared core has one
  subscription point for profile and service context trust,
- discovery operations return application-facing candidates and references; they
  must not expose vendor API objects or handles,
- discovery operations may prepare enough read context to find selectable
  project/PLC/runtime candidates, but they do not build, transfer, start PLC, or
  touch UnitTest lifecycle/event-stream communication,
- discovery requests carry the application-owned configuration snapshot needed
  for that discovery call; services must not retain it as the operational
  configuration for a later prepare call,
- project, runtime, and communication preparation each receive the current
  application-owned configuration snapshot explicitly,
- release operations are scoped to the service lifecycle level and are used by
  workflow back-navigation,
- `CleanupAsync` remains a convenience/full-service cleanup path and may
  delegate to the scoped release operation internally,
- communication-level acknowledgement/release is owned by
  `IUnitTestEventStreamService`,
- destructive EventBuffer reset is an explicit command on
  `IUnitTestEventStreamService`; successful command completion means only that
  the underlying write was accepted, while the resulting clean buffer state is
  established by later event-stream observations,
- PLC/runtime reset is not part of `IPlcControlService` unless a later design
  defines a concrete runtime-boundary operation for it; connection/resource
  release is modeled as `CleanupAsync`, and UnitTest RunControl reset belongs to
  `IUnitTestLifecycleService`,
- no PLC constants, data block layouts, Siemens handles, or vendor API objects
  are exposed through these contracts.

Functional service request and result types:

Exact request/result types live in
[FunctionalServiceModels.cs](../../../src/PLCSIM.UnitTest.Common.Application/Services/FunctionalServiceModels.cs),
target identities/contexts in
[TargetReferences.cs](../../../src/PLCSIM.UnitTest.Common.Application/References/TargetReferences.cs),
and PLC/lifecycle state in
[PlcStateModels.cs](../../../src/PLCSIM.UnitTest.Common.Application/Runtime/PlcStateModels.cs).
These code owners and the runtime contract define fields, defaults and
command/release semantics; this document maps their use.

`IPlcControlService` and its owning access profile are authoritative for
`PlcState`. `InstanceState` describes the lifecycle and availability of the
runtime instance hosting the PLC. `OperatingState` describes the independently
observed PLC operating state. Runtime boot/shutdown must not be projected as PLC
start/stop, and workflow preparation must not be encoded as a PLC operating
state.

`Starting` and `Stopping` are optional PLC transition observations; profiles
may publish direct `Stopped -> Running` or `Running -> Stopped` transitions
when the underlying API exposes only stable states. `Frozen` and `Held` remain
distinct model states so gates and diagnostics can handle them explicitly.
`ReadyForTransfer` and `ReadyForExecution` are profile-owned capability
projections and are not reconstructed by consumers from enum names. Start/Stop
operation results never synthesize `PlcState`.

The detailed event-stream monitoring state is independent from coarse service
health. Buffer saturation or observed event loss does not by itself invalidate
otherwise functioning runtime communication. Implementations publish changes
through `MonitoringStateChangedEventHandler`, including buffer saturation,
producer/deletion progress, and the cumulative PLC-side lost-event counter.

Run-epoch snapshots retain an epoch-relative lost-event count and expose
`HasLostEvents`. The application establishes the epoch baseline; it must not
copy a cumulative PLC-side counter into a new epoch without accounting for that
baseline. Any loss attributed to an epoch makes its evidence permanently
incomplete and prevents a successful artifact from being published.

RunControl observation keeps four independent evidence axes:

- `ExecutionStatus` is the coarse lifecycle projection used for generic gates
  and command-reaction monitoring.
- `ExecutionSignals` is the flags-based set of independent PLC signals.
- `State` is the detailed PLC RunControl state-machine value and remains
  supplemental evidence.
- `Result` is the PLC-owned RunControl result projection and does not replace
  event-stream result evidence.

The `Ready` convenience property is derived from `ExecutionSignals`; it is not
stored independently. Normal start readiness requires `ExecutionStatus.Idle`
and `Ready`. Command results report immediate external API write acceptance
only. Later PLC reaction is delivered through
`RunControlObservationChanged` and monitored independently.

Reference and discovery type rules:

- `ProjectTargetRef` is the application-facing reference to a project-side PLC,
  CPU, or build/download target.
- `RuntimeTargetRef` is the application-facing reference to a runtime-side PLC
  simulation target or destination.
- `PlcProjectContext` represents a prepared or opened project context owned by
  the access profile.
- `PlcRuntimeContext` represents a prepared runtime context owned by the access
  profile.
- `Id` values are the stable references used for selection, validation, and
  final setup binding.
- `DisplayName` is intended for UI/CLI display.
- `TechnicalName` is optional diagnostic/display context. It must not be the
  primary identity for workflow decisions.
- These types must not contain Siemens API objects, runtime handles, COM
  objects, process references, or other vendor-owned object models.
- Discovery results return neutral candidates and diagnostics. Runtime discovery
  may additionally publish an explicit profile-designated `DefaultTarget`.
  The host may use that default for `UseExisting` only when no target identifier
  was supplied; discovery count/order is not an implicit default and an invalid
  explicit identifier never falls back. Manual preselection remains application
  policy, not a consequence of having one candidate.
- `TargetDiscoveryState.NotAvailable` means discovery cannot currently be
  executed because required preconditions are not present.
- `TargetDiscoveryState.NoCandidates` means discovery completed but found no
  selectable targets.
- `TargetDiscoveryState.CandidatesAvailable` means discovery completed and
  returned at least one selectable target.
- `TargetDiscoveryState.Failed` means discovery failed; diagnostics contain the
  relevant detail.
- `ProjectTargetResolution` and `RuntimeTargetResolution` remain preparation /
  binding results for a selected target context. They should not be used as the
  primary discovery result if no final selection exists yet.
