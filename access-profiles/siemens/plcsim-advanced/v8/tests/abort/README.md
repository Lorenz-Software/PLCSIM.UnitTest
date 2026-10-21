# Abort lifecycle real-runtime harness

This harness validates a clean RunControl Abort and a complete same-session follow-up run against
an existing PLCSIM Advanced V8 `PlcUnderTest` instance. It is separate from EventStream capacity
measurement and does not modify the production worker or communication contracts.

The primary sequence uses one communication session and one EventBuffer epoch:

1. Observe an empty, loss-free EventBuffer and RunControl `Idle + Ready`.
2. Execute one complete calibration run to measure the active RunControl duration.
3. Start a run and wait for a new `Running` observation.
4. Request Abort at a seeded random point in an early, middle, or late timing window.
5. Require RunControl Abort Result, Aggregation, RunControl completion, complete evidence for every
   observed Full Owner lifecycle, and an empty loss-free EventBuffer.
6. Request RunControl Reset and wait for a new `Idle + Ready` observation and an empty buffer.
7. Execute a complete `Passed` follow-up run without resetting the EventBuffer.

The matrix requires five successful Abort sequences per timing window. A trial where normal
completion wins before Abort is recorded as `NaturalCompletionWon` and repeated. Seed, target and
actual Abort delay, sequence boundaries, owner/event counts, loss, and follow-up evidence are
written to JSON. The PLC is stopped during cleanup only when the harness started it.

## Supported workflow and evidence boundary

Real-runtime validation confirms the following V8 workflow against communication contract
`1.0.0`:

- Abort is requested only after a new `Running` observation.
- Abort completion requires terminal RunControl result, aggregation, completion, and subordinate
  owner evidence. RunControl status alone is not completion evidence.
- RunControl Reset is requested only after Abort completion. A new post-command `Idle + Ready`
  observation and an empty, loss-free EventBuffer define the boundary for the next Start.
- The immediate follow-up run uses the same communication session and EventBuffer epoch. The
  harness does not reset the EventBuffer between Abort and that run.
- The PLC program remains responsible for resetting its internal sequencer, orchestrator, and test
  fixture state before it exposes `Idle + Ready`. The PC communication path does not repair PLC
  program state.

Targeted owner-level Abort scenarios and the generic seeded timing-window matrix both completed
with loss-free evidence and a successful immediate follow-up run after the tested PLC program had
correct Reset handling. Earlier failures caused by retained PLC fixture state were reproducible in
the received assertion evidence and did not indicate an application, profile, or binding defect.

This validation does not cover Reset while Abort is still active, arbitrary non-conforming PLC
programs, or a follow-up run after event loss. Event loss prevents proof of complete lifecycle
evidence and must not be interpreted as a successful Abort handover.

Prepare and download the UnitTest PLC program, leave the existing `PlcUnderTest` active, disconnect
WPF, CLI, TIA online observation, and other PLCSIM clients, then run:

Run from this harness directory, not the repository root:

```powershell
.\run-abort-lifecycle-validation.ps1 `
  -ObservationStrategy Polling `
  -MonitoringIntervalMs 10 `
  -DrainIntervalMs 1 `
  -MaxEventsPerBatch 32 `
  -RawEventLogging `
  -Seed 122 `
  -OutputPath .tmp\abort-lifecycle-validation.json `
  -PlcProgramPrepared `
  -ExclusiveCommunicationConfirmed
```

Use `-ObservationStrategy PlcCycle -UnthrottledPlcCycle` to validate every signaled PLC cycle.
Use `-RawEventLogging` only for diagnostic reproduction; the JSON evidence records the worker log
directory and the complete failed lifecycle event set.
An initially non-empty or lossy EventBuffer is a failed precondition. Reset it explicitly before
starting the harness; the harness never hides prior loss by resetting the EventBuffer itself.
