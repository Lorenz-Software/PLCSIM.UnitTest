# Testability Strategy

This document maps the current shared-core testing boundaries. Deterministic
tests do not require vendor engineering software or a real PLC runtime.

The test strategy should verify shared semantics with fake access profiles and
sample communication evidence. Profile/binding-owned deterministic tests remain
separate from opt-in tests that require installed vendor software. Their owning
subtrees define those additional dependencies and qualification limits.

## Test Layers

| Layer | Primary Purpose | Runtime Requirement |
| --- | --- | --- |
| Common contract/model tests | Validate DTO defaults, serialization, enum mapping, result objects, and artifact model behavior. | No Siemens software. |
| Shared-core service tests | Validate workflow state, action gates, monitoring, event-stream processing, diagnostics, result interpretation, and artifact export coordination. | No Siemens software. |
| Fake access profile integration tests | Validate that WPF/CLI-facing shared workflow behavior works through the same access-profile contracts used by real profiles. | No Siemens software. |
| Siemens access profile tests | Validate detection, binding selection, profile config validation, package composition, and service behavior. | Deterministic profile tests use Fake bindings without Siemens software; real-runtime checks are opt-in. |
| WPF/CLI application tests | Validate application composition, command/menu binding, CLI exit mapping, config parsing, and output wiring. | No Siemens software when using fake profile; Siemens software only for real-runtime validation. |

The default automated test path should use fake/sample profiles. Tests that
require Siemens installations must be opt-in and clearly separated.

## Fake Access Profile

The fake access profile should implement the same public access-profile contract
as a real profile. It must not use Siemens assemblies, Siemens registry state,
or Siemens installation folders.

Package placement:

```text
PLCSIM.UnitTest.AccessProfiles.Fake
  -> FakeAccessProfilePlugin
  -> FakeAccessProfile
  -> FakePlcProjectService
  -> FakePlcControlService
  -> FakeUnitTestLifecycleService
  -> FakeUnitTestEventStreamService
```

The fake profile is a Siemens-independent access profile package, not WPF-only
test code. Application and shared-core tests may either load it through a
temporary directory package with `manifest.json` or instantiate the fake plugin
directly when the test needs precise control over fake services. Direct
instantiation is test composition; production application composition should use
profile discovery.

The fake profile models the application-facing behavior of a real access
profile, not Siemens-specific implementation details. Tests do not require a
real PLC, runtime, Siemens installation, worker process, or IPC transport. They
must, however, establish the same abstract preconditions as a real profile. In
particular, UnitTest communication can only be prepared while the fake
runtime/PLC state is `Running`.

The following behavior boundaries must remain aligned with real access-profile
behavior:

- a successful Start/Abort/Reset command result means that the command was
  accepted by the fake external API boundary; it does not imply that the
  requested lifecycle reaction has already occurred;
- RunControl lifecycle changes are published as separate observations and may
  be delayed, omitted, or externally triggered by a test scenario;
- external runtime/PLC stop and health invalidation affect communication
  sessions, workflow gates, and active observation through the normal
  application-facing events;
- event-stream delivery, release, invalidation, cancellation, and cleanup follow
  the same abstract workflow boundaries used by real profiles.

The fake profile does not model Siemens binding selection, Siemens API timing,
worker/IPC behavior, PLC-cycle synchronization, or sustained event-stream
throughput. Those properties require Siemens fake-binding integration tests or
explicit real-runtime validation.

The fake profile should support these scenarios:

| Scenario | Purpose |
| --- | --- |
| Valid setup | Shared workflow can bind all four functional services and reach prepared state. |
| Missing profile config | Config validation errors are surfaced without Siemens-specific failures. |
| Project preparation failure | `IPlcProjectService` diagnostics and workflow gates are validated. |
| Runtime preparation failure | `IPlcControlService` diagnostics and action availability are validated. |
| PLC start/stop state changes | WPF/CLI-visible workflow publication and action gates are validated. |
| UnitTest start accepted | Command acceptance is reported independently from the later simulated RunControl observation. |
| UnitTest command rejected | Command result, diagnostics, and recovery state are validated. |
| Event stream batches | `EventStreamProcessor` receives accepted batches exactly as it would from a real profile. |
| Runtime health invalidation | Runtime trust loss invalidates active observation where applicable and blocks runtime-dependent actions. |
| Lifecycle health invalidation | RunControl communication trust loss invalidates active observation and blocks UnitTest commands. |
| Event-stream health invalidation | Event-stream communication trust loss invalidates active observation/evidence and blocks monitoring/evidence paths. |
| Project health invalidation | Project context loss blocks project-dependent paths without retroactively invalidating a running observation when runtime/lifecycle/event-stream trust remains healthy. |
| Profile health invalidation | Profile-level trust loss invalidates active observation and blocks normal workflow actions while release/cleanup remains possible. |

The fake profile should be deterministic by default. Tests may configure delayed
events or failures explicitly, but random timing should not be used in unit
tests.

## Sample Evidence

Sample evidence is contract-near event-stream data used by tests. It should be
small, readable, and reusable.

Shared JSON samples and their loader live in `PLCSIM.UnitTest.Testing`; focused
processor/workflow tests also construct evidence directly when the behavior
under test needs a specific event sequence. This does not require a separate
fixture file for every scenario.

Current shared sample placement:

```text
tests/
  PLCSIM.UnitTest.Testing/
    Evidence/
      FakeEvidenceLoader.cs
      FakeEvidenceSet.cs
      normal-run.json
      failed-assertion-run.json
```

Sample evidence should model `UnitTestEventBatch` and `UnitTestEventEntry`
values, not Siemens API objects. Each sample should describe:

- initial lifecycle context,
- event batches in producer/sequence order,
- expected run epoch state,
- expected result state,
- expected findings,
- expected diagnostics,
- expected artifact export readiness.

Evidence scenarios exercised by shared tests include:

| Sample | Expected Focus |
| --- | --- |
| Normal run | New epoch detection, tree creation, passed result, final event handling. |
| Failed assertion run | Assertion failure mapping to testcase/run result and JUnit output readiness. |
| Framework error run | Framework event indexing, diagnostics, invalid or error result classification. |
| Stale events before run | Ignoring/rejecting old evidence before the trusted active boundary. |
| Reset during run | Current epoch invalidation and no clean completed result. |
| Abort during run | Abort outcome, partial evidence, and output policy behavior. |
| Missing final event | `finalEvent` timeout path and incomplete evidence classification. |

For shared-core workflow tests, each sample should assert the published
`RunWorkflowPublication.CurrentRun` snapshot where applicable. This verifies
that applications consume processor/epoch snapshots instead of reconstructing
result meaning from monitor-only state.

## Shared-Core Tests

Shared-core tests should focus on behavior that must be identical for WPF and
CLI.

Test responsibilities:

| Test Group | Subject |
| --- | --- |
| Workflow gates | Published `ActionGateState[]` for configuration, prepare, start PLC, start, abort, reset, stop PLC, and cleanup. |
| Workflow publication | `RunWorkflowPublication` updates after service state changes, diagnostics, monitor updates, and output updates. |
| Access health | `AccessHealthMonitor`, `WorkflowStatusSnapshot.AccessHealth`, gate blocking, and active-observation invalidation for profile/project/runtime/lifecycle/event-stream health changes. |
| Operation boundaries | Caller cancellation versus Shared Core deadline expiry, unexpected lower-layer cancellation, combined build/transfer budget, partial-setup retry, and owner cleanup after individual failures. |
| Monitor | `TestRunMonitor` command-reaction monitoring, processor-snapshot consumption, timeout handling with `TimeProvider`, terminal state detection, and invalidation. |
| Event processing | `EventStreamProcessor.StartObservation`, anchor-driven `ProcessBatch`, `CompleteCurrentEpoch`, `StopObservation`, `CreateSnapshot`, and `Reset`. |
| Result interpretation | `UnitTestRunEpoch` tree updates, result propagation, assertion findings, framework events, aggregation consistency, and evidence state. |
| Artifact export | `ArtifactExportService` request validation, exporter selection, JUnit exporter invocation, output state, and export diagnostics. |
| Diagnostics | Diagnostic severity/source/code propagation from fake services, shared workflow, monitor, processor, exporters, and WPF/application-level producers; shared sink coalescing and clear behavior. |
| Config | JSON parsing, defaults, timeout mapping, `accessProfile.values` handover, and invalid config classification. |

Tests should assert structured outputs, not log text. Logs may be captured for
diagnostic verification only where the behavior under test is explicitly
logging-related.

## Time And Async Testing

Shared-core timing behavior should use `TimeProvider`.

Direction:

- production composition uses `TimeProvider.System`,
- tests use `FakeTimeProvider` or equivalent controlled time provider,
- `TestRunMonitor` owns timer scheduling and timeout interpretation,
- tests advance virtual time explicitly,
- workflow tests should not rely on `Task.Delay` or wall-clock sleeps,
- event-stream tests should push batches through fake services or directly into
  `EventStreamProcessor`.

If a method is asynchronous because it represents an external operation, tests
should complete it through controllable fake task sources or deterministic fake
service responses.

## Application Tests

WPF and CLI tests should consume the shared workflow rather than duplicate
shared-core behavior.

CLI test direction:

- parse config files,
- bind fake access profile,
- execute validate/prepare/full-run paths,
- verify exit-code mapping,
- verify output paths and artifact requests,
- verify diagnostics are printed/logged according to CLI policy.

WPF test direction:

- verify view models bind to `RunWorkflowPublication`,
- verify commands call `RunWorkflowService` methods rather than functional
  services directly,
- verify action availability follows workflow gates,
- verify the activity cancel command is visible only for cancellable operations,
  becomes disabled after the first request, and remains busy until recovery
  completes,
- verify cancelled discovery/setup can recover through retry, Back, End Session,
  and application exit without losing retained-resource ownership,
- verify diagnostics, terminal-style output, result tree, and artifact-open
  actions consume shared publications and output state.

WPF layout and menu structure are defined in the [WPF UI design](../700-wpf-ui-design.md).
This document only defines how the resulting application behavior should be
testable.

## Siemens-Specific Tests

Siemens access profile tests should not be part of the default unit-test path.

They should be split into:

- pure tests for Siemens config parsing and profile-internal decision logic,
- deterministic facade/worker tests for pre-dispatch cancellation,
  post-dispatch terminal-response draining, reusable sessions, hard containment
  failure, retained side effects, and owner cleanup,
- environment detection tests that can be skipped when Siemens software is not
  installed,
- real-runtime validation against TIA Portal and PLCSIM Advanced where manually
  triggered or explicitly enabled.

The Siemens profile should expose testable internal boundaries where practical,
but the application/shared-core tests must not depend on Siemens assemblies or
installed Siemens versions.

## Test Profiles And Test Doubles

The Fake access profile is a vendor-independent development and regression
profile package. It lives in its own subtree and uses the same access-profile package,
discovery, configuration, service, health, diagnostic, and cleanup boundaries as
real profiles.

| Component | Owner | Purpose |
| --- | --- | --- |
| `FakeAccessProfilePlugin` | Fake access profile package. | Discovery, profile-selection, and package-boundary tests. |
| `FakeAccessProfile` | Fake access profile package. | Provides deterministic functional service instances through normal access-profile contracts. |
| `FakePlcProjectService` | Fake access profile package. | Project prepare/build/transfer success and failure paths. |
| `FakePlcControlService` | Fake access profile package. | Runtime prepare/start/stop state paths. |
| `FakeUnitTestLifecycleService` | Fake access profile package. | Start/abort/reset command outcomes and lifecycle events. |
| `FakeUnitTestEventStreamService` | Fake access profile package. | Pushes configured event batches and communication faults. |
| `RecordingArtifactExporter` / `ThrowingArtifactExporter` | Core `ArtifactExportServiceTests`. | Export requests and failure/output-state behavior through local test doubles. |
| `HoldingArtifactExporter` | WPF stepper smoke tests. | Controlled asynchronous export completion. |

Production workflow, WPF, CLI, and shared-core code must not contain Fake-only
behavioral shortcuts. They may depend on access-profile contracts and discover
or load the Fake profile through the same package/session boundary used for real
profiles.

## Access-Profile Contract Conformance

A reusable profile-neutral conformance harness verifies behavior only through
the public access-profile package and service contracts. The same harness is
executed against the packaged Fake access profile and against concrete profile
packages composed with their deployable Fake bindings.

The common conformance scope covers:

- registration and capability publication;
- profile loading and disposal;
- configuration projection and validation;
- session and functional-service availability;
- cancellation and operation-result mapping;
- health publication and invalidation;
- cleanup, repeated cleanup, and disposal;
- normalized rejection of operations whose prerequisites are not satisfied.

The harness does not define profile-specific capabilities or inspect concrete
profile and binding implementation classes. Profile-specific behavior remains
covered by tests in the owning profile subtree.

The generic Fake access profile is the application-owned controllable test
double. Core and WPF unit tests may compose it directly when they need precise
control over service outcomes and events; its own implementation tests live in
the Fake profile subtree. Application package integration and conformance tests
consume it as a deployed package rather than inspecting the loaded
implementation. Profile integration tests consume Fake bindings as deployed
packages. Binding tests
validate their own entry point, manifest, contracts, worker behavior, and vendor
integration without referencing the owning profile implementation.

Implementation details may be exposed to the owning local test assembly through
`InternalsVisibleTo`. Such access must not cross application, profile, or binding
subproject boundaries. Public controllable types of the generic Fake profile are
an intentional application-test composition surface, not permission to expose
equivalent implementation types from production profiles or bindings.

Validation-only worker behavior must be owned by a binding-local harness or
validation worker. Production worker dispatch and protocol surfaces must not grow
test-only commands or behavior switches; temporary seams are removed through
binding-owned hardening before release qualification.

## Verification Rule

An implementation issue that changes shared workflow, event-stream processing,
monitoring, diagnostics, or artifact export should add or update tests at the
lowest layer that can verify the behavior without Siemens software.

Real Siemens runtime validation remains necessary, but it should validate the
Siemens access profile and integration path rather than replace shared-core
automated tests.
