# PLCSIM Advanced V8 Binding Validation

This document owns the V8 binding-level validation scopes and procedures.
[Worker implementation](implementation.md) owns facade/process details.
This is not a new test run or qualification of
all application/distribution versions. The
[binding contract](../../docs/contracts/plcsim-advanced-binding-contract.md)
owns runtime/communication semantics; the [binding README](../README.md) owns
build entry points, and [performance](performance/README.md) owns workload
measurements and their limits.

## Implementation Scope

The concrete V8 binding package is an operational implementation of the
Siemens-internal PLCSIM binding contract. It provides:

- discoverable package metadata, readiness resolution, and bounded environment
  probing;
- local runtime discovery, existing-runtime attachment, owned-runtime creation,
  start, stop, release, storage cleanup, and unregister;
- event-driven runtime-instance and PLC-operating-state publication;
- UnitTest communication preparation and deterministic close;
- RunControl observation plus Start, Abort, and Reset command writes whose
  immediate result represents external API write acceptance only;
- polling and managed event-object-wait PLC-cycle EventBuffer observation, full
  immutable event-payload capture, contiguous release writes, and named-pipe
  delivery through the same worker path.

The PLC-cycle strategy enables default-mode sync events, calls
`RegisterOnSyncPointReachedEvent()`, and observes cycle notifications through
timed `WaitForOnSyncPointReachedEvent(uint)` calls. PLC reads and writes remain
on the serialized communication worker. Normal PLC STOP/RUN keeps the prepared
`IInstance` and registration. If the required managed members cannot be
established, preparation fails explicitly
without falling back to polling. Sustained high-throughput/backpressure stress,
deliberate real Runtime API
failure injection, and additional capabilities outside the described implementation
remain later validation work. CreateOwned compensation status, diagnostics,
evidence, and terminal-session behavior are covered deterministically without
claiming that every Siemens API failure shape was reproduced against a real
runtime. These boundaries do not turn the V8 package into
a stub and must not be represented as not-implemented results for the
operational path.

## Binding-Level Matrix Scope

This matrix is one bounded qualification scope, not the complete current test
inventory. The [native validation procedures](#external-download-and-communication-revalidation)
below also cover targeted recovery and repeated-lifetime checks; the
[Abort harness](../tests/abort/README.md) documents its successful native checks
and their limits.

The opt-in binding-level matrix described here was executed against the local PLCSIM Advanced V8
Runtime API (`8.0`) and a registered, user-owned `PlcUnderTest` instance with a
downloaded UnitTest program. TIA Portal was not part of this binding-level test;
the project/program was prepared before validation.

The matrix verified:

- `CreateOwned` registration, power-on/power-off, unregister, owned temporary
  storage cleanup, and absence of the created instance after release;
- `UseExisting` preparation and PLC start/stop without unregistering or
  deleting the user-owned instance or storage;
- UnitTest communication and event-stream contracts `1.0.0` with polling and
  managed event-object-wait PLC-cycle observation;
- three consecutive Reset/Start cycles, followed by PLC stop/start,
  communication-session reopen, and one recovery Reset/Start cycle;
- independently observed Idle/Ready projection plus RunControl `Starting` and
  `Completed` event anchors for every run;
- TestCase, TestSuite, and RunControl result/aggregation evidence;
- monotonic unique event sequence ids and populated timestamps;
- `LostEventCount = 0`, release-before-publication, and an empty-buffer
  follow-up after each completed run;
- communication cleanup without unregistering or deleting `PlcUnderTest` and
  without a remaining worker process;
- normal PLC STOP/RUN, external TIA download/revalidation transitions, and
  explicit reconnect after terminal communication invalidation without deleting
  the user-owned runtime;
- ten identical 394-event Release runs for polling at 10 ms, unthrottled
  `plcCycle`, and `plcCycle` throttled to 10 ms, all without event loss.

The tested PLC program did not emit a Full Owner
`TestSuite/StateTransition/Completed` event. It emitted the TestSuite result and
aggregation and the technical TestSuite-orchestrator completion event. This does
not prevent consumer-side result generation: the application does not require
subordinate TestSuite lifecycle evidence to classify results or complete the run
epoch. The technical orchestrator event must not be projected as TestSuite
lifecycle evidence. Run completion remains based on the RunControl completion
anchor followed by complete event-buffer consumption.

Abort was not covered by this matrix because its PLC program completes
too quickly for a deterministic Abort window. This is not a limitation of the
separate Abort qualification linked above. Sustained high-throughput or
long-running backpressure/resource behavior, deliberate real Runtime Manager
loss, and real worker/pipe crash injection remain unproven by this bounded
matrix. Deterministic tests cover the corresponding contract and containment
paths where Siemens software is not required.

## Verification Boundary Of This Matrix

Real Siemens validation is opt-in. This matrix covers PLCSIM Advanced V8 Runtime
API loadability, attached and owned
runtime-instance lifecycle, event-driven PLC state observation, symbolic PLC
access, Reset/Start RunControl writes and observation, repeated EventBuffer
payload reads, polling and managed event-object-wait PLC-cycle delivery,
release-before-publication, communication close/reopen recovery, normal PLC
STOP/RUN, external download revalidation, explicit reconnect after terminal
communication invalidation, caller-cancelled setup recovery, attached-runtime
detachment, and facade/worker transport. It does not establish Abort,
sustained event-stream performance/backpressure, resource soak behavior, or
deliberate real Runtime Manager or worker/pipe failure injection coverage.
Consult the owning README and targeted harnesses for checks outside this matrix;
do not infer either additional coverage or missing implementation from its scope.


## Deterministic Coverage And Observation Behavior

Commands in the procedures below run from the repository root. Native opt-ins
require an installed usable Runtime API and an explicitly designated test target;
they may change PLC state or create/release owned instances. Do not enable them
as part of a routine offline test run.

The V8 tests are organized by binding responsibility:

- `PlcsimAdvancedV8EnvironmentAndPackageTests` covers environment evidence,
  manifest/package shape, readiness, and deployment boundaries.
- `PlcsimAdvancedV8WorkerSessionTests` covers worker bootstrap, IPC,
  correlation, cancellation, reader termination, and process cleanup.
- `PlcsimAdvancedV8RuntimeSessionTests` covers Runtime Manager access,
  provisioning ownership, runtime state, release, revalidation, and
  compensation.
- `PlcsimAdvancedV8CommunicationSessionTests` covers UnitTest communication,
  RunControl, health, lifecycle, and communication-session cleanup.
- `PlcsimAdvancedV8EventStreamTests` covers EventBuffer monitoring,
  backpressure, snapshots, and release-before-publication.
- `PlcsimAdvancedV8RealRuntimeTests` contains only the four environment-variable
  gated checks that require an installed PLCSIM Advanced V8 runtime.

Shared stubs remain internal to the V8 test assembly. The validation worker
remains a test-owned process artifact; neither test support nor real-runtime
harness code is part of the deployed binding package.

Deterministic tests cover package shape, manifest metadata, worker bootstrap,
serialized request correlation, asynchronous event routing, protocol-to-contract
mapping, normal and forced worker cleanup, closed-session subscription release
and collectability, runtime provisioning decisions, timeout forwarding, fake
runtime behavior, cleanup-result mapping, and failed owned-runtime
compensation without loading Siemens software. Compensation coverage runs
through the test-owned net48 validation worker, the production named-pipe
protocol, facade mapper, and session host. It distinguishes proven rollback
from an unclear retained resource, verifies per-step diagnostics/evidence, and
verifies terminal session invalidation after incomplete cleanup. Public UnitTest
communication operations reject a foreign communication-session identifier
through structured results and diagnostics; they do not leak an implementation exception or dispatch the
request to the worker. Opt-in real-runtime validation covers the parts that
only the Siemens Runtime API can prove: API loadability, real runtime discovery/creation,
power-on/run/stop behavior, release, owned runtime cleanup evidence, RunControl
communication, and polling/PLC-cycle EventBuffer intake and release behavior.

Both observation strategies use the same hot path. It captures only the oldest
contiguous FIFO prefix, capped by the configured maximum and PLC-reported
EventBuffer length. A valid post-snapshot must still contain that prefix before
it is released and published. Polling uses the configured drain cadence for
known backlog and pending PLC release confirmation. PLC-cycle observation
bypasses its optional idle throttle and continues at the next sync point while
that work remains. EventBatch IPC payloads are assembled incrementally so larger
batches do not repeatedly copy the complete immutable message string.

EventBuffer monitoring-state transitions are published independently from
EventBatch delivery. BufferFull, lost-event counters, sequence watermarks, and
recovery therefore remain visible when a consistent batch cannot be published.

EventBuffer real-runtime validation covers polling and `plcCycle`. The selected
Siemens profile defaults are polling with 10 ms regular monitoring, 1 ms active
drain, and 32 events per published batch. Omitting the monitoring interval for
`plcCycle` observes every usable cycle; an explicit positive value acts as a
minimum interval between complete idle observations. Sequence ids remain
monotonic and unique, timestamps are populated, release completes before
publication, and successful intake is followed by empty-buffer confirmation.

Current Release capacity measurements, adaptive payload-read costs, operating
recommendations, diagrams, and stable source tables are owned by
[the V8 EventStream performance document](performance/README.md). That document uses the current expanded workload
and supersedes earlier development measurements. Harness reproduction details
belong to [performance harness](../tests/performance/README.md). Profile authoring and configuration
guidance remains owned by [profile configuration](../../../docs/configuration.md).

### External download and communication revalidation

Real validation with an externally managed `PlcUnderTest` established the
following V8 behavior:

| External action | Observed PLCSIM evidence | Binding behavior |
| --- | --- | --- |
| Open and cancel the TIA Portal download dialog | RunControl or EventBuffer reads may return `NotUpToDate`; no reliable callback distinguishes dialog-open from a completed download. | Discard the complete observation, publish communication health `RevalidationRequired`, retain the selected runtime and PLC state, and recover the existing session after a complete valid read cycle. |
| Complete a software download | Stale reads can precede `OnSoftwareConfigurationChanged`; the callback alone does not prove that communication is usable again. | Apply the same read-driven revalidation and restore `Healthy` only after RunControl and EventBuffer reads are both valid. |
| Complete a hardware-configuration download | Hardware/software/IP callbacks provide correlation evidence but do not form a reliable recovery boundary. Power state may change independently. | Keep callbacks diagnostic-only, follow observed PLC operating-state changes, and require complete communication validation before UnitTest actions are enabled. |
| Power the PLC instance off and on externally | Runtime operating-state events report shutdown/off and later startup/running. | Close communication on shutdown, retain external ownership, and establish a new communication session only after the running runtime has passed communication validation. |

While same-session revalidation is active, Communication, Monitor, and Run
Control are projected as `revalidating`; dependent UnitTest actions are gated.
The observation loop and selected runtime remain established. A successful
recovery produces concise start/recovered output and no routine diagnostic. A
positive revalidation timeout may make the session terminal; a timeout of zero
disables automatic terminal timeout for stale reads without masking cancellation,
runtime shutdown, or classified infrastructure failures.

The real WPF validation covered dialog cancellation, software and hardware
downloads, external Power Off/On, explicit PLC Stop/Start recovery, repeated
UnitTest execution, session cleanup, and transient-state projection. No
remaining functional issue was observed in the final manual verification.

The V8 PLC-cycle strategy enables default-mode sync events, calls
`RegisterOnSyncPointReachedEvent()`, and waits with the timed
`WaitForOnSyncPointReachedEvent(uint)` overload. A timeout is a normal
no-cycle/cancellation checkpoint; PLC reads and writes remain on the serialized
worker path. Stop/close cleanup calls `UnregisterOnSyncPointReachedEvent()` and
restores the prior default-mode setting. Normal PLC STOP/RUN keeps the same
prepared `IInstance`. If the required managed members or registration are
unavailable, communication preparation fails explicitly rather than silently
falling back to polling.

```powershell
$env:PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V8_CREATEOWNED='1'
dotnet test access-profiles\siemens\plcsim-advanced\v8\tests\PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8.csproj --filter V8_binding_can_validate_real_create_owned_lifecycle_when_enabled
```

The opt-in `CreateOwned` validation expects PLCSIM Advanced V8 to be installed
and usable locally. It validates the successful lifecycle, not deliberately
injected Siemens Runtime API failures during compensation. Such failure
injection is not currently controllable without risking an orphaned local
instance, so the exceptional path remains deterministically validated at the
worker boundary. The successful validation creates a short alphanumeric runtime
instance name and assigns the configured storage path directly to that instance
before power-on, then revalidates the prepared resource in place. The worker
process id, worker session id, runtime reference, and owned-resource
classification must remain unchanged. Final release removes the runtime registration and temporary storage
scope.

Existing-runtime start/stop validation is also opt-in:

```powershell
$env:PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V8_EXISTING_STARTSTOP='1'
dotnet test access-profiles\siemens\plcsim-advanced\v8\tests\PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8.csproj --filter V8_binding_can_validate_real_existing_runtime_start_stop_when_enabled
```

The existing-runtime validation expects a local registered PLCSIM Advanced V8
runtime instance named `PlcUnderTest`. The instance must contain a downloaded
PLC configuration/program. A powered-on but empty instance can be observed and
stopped, but PLCSIM Advanced returns `SREC_IS_EMPTY` when `Run` is requested.
The test revalidates the prepared resource while running and stopped and
requires the worker process id, worker session id, runtime reference, and
external ownership to remain unchanged. It then forces a bounded identity
mismatch, closes the invalidated session, opens a new session, and verifies that
`PlcUnderTest` is still registered. The test must not unregister or delete that
runtime; ownership remains with the user or external setup that created it.

Existing-runtime UnitTest communication validation is opt-in separately:

```powershell
$env:PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V8_COMMUNICATION='1'
dotnet test access-profiles\siemens\plcsim-advanced\v8\tests\PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8.csproj --filter V8_binding_can_validate_real_existing_runtime_unit_test_communication_when_enabled
```

The communication test uses `UseExisting`, the registered `PlcUnderTest`
instance, UnitTest communication and event-stream contracts `1.0.0`, and
configurable monitoring/drain/batch values. It validates Reset and Start command API acceptance, independently
observed Idle/Ready and event anchors, complete result/aggregation evidence,
release-before-publication, PLC stop/start, communication-session reopen, and a
recovery run. Final cleanup stops the PLC but keeps the user-owned instance and
storage. Abort lifecycle validation is owned by the separate
[`tests/abort`](../tests/abort/README.md) harness. These tests form a bounded
functional recovery matrix, not a memory or sustained-throughput soak test.

Repeated session-lifetime validation is opt-in separately:

```powershell
$env:PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V8_LIFETIMES='1'
dotnet test access-profiles\siemens\plcsim-advanced\v8\tests\PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8.csproj --filter V8_binding_can_validate_real_repeated_session_lifetimes_when_enabled
```

The lifetime test uses the user-owned `PlcUnderTest` instance for two warm-up and
24 measured communication/monitoring/close cycles. It records post-close worker
private memory, handle count, and thread count and enforces bounded growth. It
then performs three independent `CreateOwned` prepare/release/close cycles and
verifies that no worker process, runtime registration, or temporary storage scope
remains. The test restores the original running/stopped state of `PlcUnderTest`.
This is bounded lifecycle evidence, not a sustained throughput or backpressure
soak.
