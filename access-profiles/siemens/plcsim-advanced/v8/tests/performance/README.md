# EventStream performance harness

The V8 test-worker projects and their compiler symbols are documented in
[`../worker/README.md`](../worker/README.md). Test workers are separately named and are not
deployment artifacts.

Published measurements, diagrams, recommendations, limitations, and stable
source tables are owned by the
[V8 EventStream performance document](../../docs/performance/README.md). The
harness documentation describes reproduction and measurement boundaries; it is
not the published interpretation surface.

The V8 validation worker contains a synthetic performance mode that executes the production
`ReadEventBufferPollingSnapshot` path against a deterministic Runtime API test double. It does
not require PLCSIM Advanced.

Run the following commands from this performance-harness directory, in Release
configuration:

```powershell
dotnet build ..\worker\ValidationWorker\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.ValidationWorker.csproj -c Release

..\worker\ValidationWorker\bin\Release\net48\PLCSIM.UnitTest.PlcsimAdvanced.V8.ValidationWorker.exe `
  --performance `
  --workload all `
  --monitoring-interval-ms 10 `
  --drain-interval-ms 2 `
  --max-events-per-batch 32 `
  --warmup 5 `
  --iterations 30 `
  --output .tmp\v8-performance\eventstream-50ms.json
```

Add `--raw-event-logging` to measure Debug-level raw-event message formatting. The
synthetic logger formats and consumes each message without writing it to disk, so the result
isolates worker-side logging cost from file-system and log-sink latency.

Available snapshot workloads are `empty`, `events-16`, `events-64`, `events-128`,
`events-192`, `events-240`, `events-256`, `overflow-384`, and `overflow-512`.
`drain-256` drains a full synthetic EventBuffer through bounded FIFO-prefix batches.
`adaptive-drain` selects `empty` and `drain-256` together so the fixed metadata cost and
bounded-drain cost can be compared in one invocation.
Complete-run workloads are `run-466-5000ms`, `run-466-1000ms`, `run-466-200ms`,
`run-716-1000ms`, and `run-716-200ms`. The complete-run batch plan is derived from the
selected monitoring interval. `--poll-interval-ms` remains accepted as the legacy harness
alias for `--monitoring-interval-ms`.

The JSON result reports event counts, poll counts, Runtime API read-call counts,
numeric batch/string scalar call counts, read state, snapshot-read median/p95/max, maximum-poll p95, IPC
serialization median/p95, encoded IPC payload bytes, and median managed bytes allocated by the
measured thread. Adaptive-drain results additionally report release API calls, release-operation
cost, bounded-batch count, modeled release wait, total drain time, throughput, and one modeled
command opportunity after each released batch. IPC measurements use the production EventBatch message writer with a reused
in-memory stream; they measure message construction and encoding, not named-pipe blocking.
Overflow workloads cap captured entries at the contract buffer capacity and report the remainder
as lost events.

The release wait is modeled as one configured drain interval per released batch. The measured
release-operation cost covers the production pending-read/write path against the deterministic
test double; it is not a measured PLC release roundtrip. This synthetic worker hot-path benchmark
does not model native Runtime API latency or replace real-runtime validation.

## Real-runtime capacity measurement

The block driver records both supported observation strategies against an existing `PlcUnderTest`
instance. Polling uses explicit monitoring intervals `5`, `10`, `25`, `50`, and `100 ms`.
PLC-cycle observation is measured once without `monitoringIntervalMs` and again at each of the five
explicit intervals. Drain interval and maximum batch size remain fixed at `1 ms` and `32` events.
The PLC may initially be stopped. Each capacity point starts it, executes ten complete normal runs
without a stop/start recovery cycle, and leaves the PLC stopped. Published complete runs must report
a PLC-side `Passed` RunControl result; the harness does not hide an invalid first run as an
unreported warm-up. Stop/start recovery remains covered by the functional real-runtime validation
mode and is intentionally excluded from the performance sample.

Before each block, configure and download the requested PLC minimum cycle time. Keep TIA Portal
offline and do not run the WPF or CLI application against the runtime during the measurement.
The configured PLC minimum cycle is recorded as an explicit operator-supplied label; the harness
does not claim to measure the effective PLC cycle time.

```powershell
.\run-capacity-matrix-block.ps1 `
  -ConfiguredPlcMinimumCycleTimeMs 5 `
  -WorkloadId expanded-unit-test-v1 `
  -EventBufferLength 256 `
  -OutputDirectory .tmp\plcsim-v8-capacity\plc-005ms `
  -ExpectedWorkloadFingerprint <fingerprint-from-first-block> `
  -PlcProgramPrepared `
  -ExclusiveCommunicationConfirmed
```

Omit `-ExpectedWorkloadFingerprint` for the first PLC-cycle block. The script prints and records the
resulting structural fingerprint; pass it to every subsequent block. Use a new or empty output
directory for every block so evidence from an earlier attempt cannot enter the combined result.

The script builds the test project once in Release configuration. Before the matrix it runs one
non-measured `polling` reference at `5/1/32` and records observed contract versions, distinct Suite
and TestCase names, event count, event-class distribution, and the operator-confirmed EventBuffer
length. It writes one point JSON and one TRX file per attempt, then combines the reference, point
evidence, host, workload fingerprint, and measurement conditions in
`plc-NNNms-capacity-block.json`. A point is repeated once if any run reports event loss or a maximum
backlog that leaves fewer than `32` free EventBuffer slots.
Point evidence includes:

- source revision, build configuration, runtime platform, and communication settings;
- workload ID, measurement role, intended run count, and observed contract/framework versions;
- whether an initial EventBuffer reset was required;
- clean-start producer/deleted sequence IDs and lost-event count for every run;
- outcome, duration, batches, events, maximum batch size, maximum backlog, lost events, worker CPU,
  worker private-memory delta, RunControl result, sequence boundaries, and event breakdown for every run;

The harness contains no Abort, command-responsiveness, or recovery scenario. Every measured cycle
is a complete normal UnitTest run.
Before each run, the harness observes RunControl and uses an existing `Idle + Ready` state directly.
It sends a RunControl reset only when the normal application start gate is not already satisfied.

Capacity mode records event loss as a measured outcome instead of failing the test and resets a
previously lost EventBuffer before starting the measurement. Debug worker logs additionally report
non-empty snapshot duration and the actual numeric-batch and string-scalar Runtime API read-call
counts. The snapshot metric excludes the preceding pending-release read and the following release
write.

Without the capacity flag, the same test remains a strict correctness validation and fails when
event loss or incomplete completion evidence is observed.

## Observation-strategy comparison

The comparison driver measures the same Release build and PLC workload in three configurations:

- polling with a `10 ms` monitoring interval;
- PLC-cycle observation without a monitoring interval, which observes every signaled PLC cycle;
- PLC-cycle observation with an explicit `10 ms` minimum monitoring interval.

Drain interval, maximum batch size, raw-event logging, run count, and PLC program remain fixed at
`1 ms`, `32` events, disabled, ten runs, and the operator-confirmed program respectively.

```powershell
.\run-observation-strategy-comparison.ps1 `
  -ConfiguredPlcMinimumCycleTimeMs 1 `
  -WorkloadId expanded-unit-test-v1 `
  -OutputDirectory .tmp\plcsim-v8-observation-comparison `
  -PlcProgramPrepared `
  -ExclusiveCommunicationConfirmed
```

Each scenario JSON records observation wake-ups, observations, no-event wake-ups, successful and
empty reads, batch and event counts, maximum backlog and loss, run duration, worker CPU time, and
worker private-memory deltas. `observation-strategy-comparison.json` combines the three points with
their common measurement conditions. The evidence is intended to support a later usage/default
decision; the harness itself does not select or change a default.

## Real-runtime adaptive payload-read cost

The payload-read matrix measures the current production V8 adaptive reader with Release binaries,
a user-confirmed 1 ms configured PLC minimum cycle, unthrottled PLC-cycle observation, a 1 ms drain
interval, and raw EventStream logging disabled. Controlled measurements defer payload consumption
while continuing to inspect EventBuffer metadata on each signaled PLC cycle. Once sufficient
backlog exists, one normal production-reader batch is limited to the requested target size.

```powershell
.\run-event-payload-read-matrix.ps1 `
  -OutputDirectory .tmp\plcsim-v8-payload-read `
  -SamplesPerPoint 10 `
  -PlcProgramPrepared `
  -ExclusiveCommunicationConfirmed
```

Controlled targets are `1`, `2`, `4`, `8`, `16`, `32`, `64`, `128`, `192`, and `255` events.
The linear operating-range regression uses the actually captured `1` through `192` event samples.
The 255-event point is retained as a separate saturation-boundary observation
for the measured fixture using the framework's default EventBuffer length of
256 (255 usable FIFO entries); it does
not determine the operating-range fit. These controlled targets describe this
benchmark, not a fixed consumer capacity or a required PLC-project buffer length.
The driver also records natural 32-event batches without the consumption gate. Every sample starts
and stops the existing `PlcUnderTest` runtime so PLC startup supplies a clean EventStream epoch.
The output separates complete snapshot duration, pre/post metadata duration, and payload-entry read
duration. Its linear regression uses only controlled entry-read samples. This is a real-runtime
payload-cost microbenchmark, not a normal-run capacity result; high target sizes may intentionally
fill the EventBuffer and report lost events while the measured payload call is in progress.

Use `-TargetEventCounts 32 -SamplesPerPoint 2` for a short controlled/natural smoke comparison.
`-SkipNaturalReference` may be used for focused controlled-point repetitions.
