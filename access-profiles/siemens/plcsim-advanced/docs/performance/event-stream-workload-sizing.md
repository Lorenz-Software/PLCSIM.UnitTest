# EventStream workload sizing and orchestrator selection

This guide provides a version-neutral model for estimating UnitTest EventStream
volume and burst concentration. It describes PLC-side event production. A
concrete PLCSIM Advanced binding must apply the model to its own measured
transport capacity, EventBuffer layout, observation strategy, and configuration.

- [Estimate volume and concentration separately](#estimate-volume-and-concentration-separately)
- [Choose the PLC orchestrator](#choose-the-plc-orchestrator)
- [Recognize communication pressure](#recognize-communication-pressure)
- [Tune only when needed](#tune-only-when-needed)
- [Validate the intended workload](#validate-the-intended-workload)

## Estimate volume and concentration separately

### Event volume

The communication contract requires three final evidence events for every
observed full-owner lifecycle:

- one final `Result` event;
- one final `Aggregation` event;
- one `StateTransition` event into `UnitTest.State.Completed`.

Full owners are the Run Control, Test Suites, and Test Cases. This gives the
portable contract floor:

```text
E_contract_floor = 3 * (1 + S + C)
```

`S` is the number of executed Test Suites and `C` is the total number of
executed Test Cases. The floor is useful for validating completeness, but it is
not a realistic normal-run estimate because lifecycle and trigger diagnostics
also enter the EventBuffer.

The standard framework lifecycle emits seven normal-path events for every full
owner:

| Event contribution | Events per owner |
| --- | ---: |
| Start trigger | 1 |
| `Starting`, `Running`, `Completing`, and `Completed` transitions | 4 |
| Final result | 1 |
| Final aggregation | 1 |
| **Total** | **7** |

The generic helper topology also emits four technical state transitions for the
Suite Orchestrator below Run Control and four for the Test Case Orchestrator in
each Suite. The resulting normal-path estimate is:

```text
E_standard = 7 * (1 + S + C) + 4 * (1 + S) + P + F + D
```

The variable terms are:

- `P`: additional lifecycle transitions, most notably Test Cases that enter
  `Preparing`;
- `F`: assertion `Failed` or `Error` finding events;
- `D`: framework, informational, timeout, configuration, abort/error-path, or
  project-specific diagnostic events.

Passing assertions do not emit one event per assertion. They contribute to the
Test Case aggregation counters. A failed or erroneous assertion emits a
one-shot finding in its assertion lifecycle and therefore adds one event to
`F`. Tests that deliberately exercise failing assertions can consequently
produce substantially more EventStream traffic than an all-passing run with
the same number of assertions.

The standard formula describes the maintained framework helpers. A direct or
custom owner implementation may produce a different diagnostic envelope while
remaining contract-compatible. The contract floor remains the lower bound for
complete owner evidence.

#### Example

Five Suites with ten Test Cases each produce this successful normal-path
estimate before preparation transitions, findings, and diagnostics:

```text
S = 5
C = 50
E_standard = 7 * 56 + 4 * 6 = 416 events
```

Five passing assertions in every Test Case do not increase the estimate. If all
250 assertions instead produce one failed/error finding, the estimate becomes
approximately 666 events plus any additional diagnostics.

### Burst concentration

Total run volume is not buffer demand. A run can contain more events than the
EventBuffer can hold at once when the consumer releases events throughout the
run. Capacity is governed by the largest net backlog:

```text
Q_peak = max(events produced - events successfully released)
```

The relevant observation window includes the time until the next observation,
payload reads, release confirmation, and any immediate drain iterations.
Events produced during that window accumulate even when the complete run lasts
much longer.

A normal full-owner finalization forms a compact cluster of approximately four
events: transition into `Completing`, final Result, final Aggregation, and
transition into `Completed`. Near-synchronous child completion therefore gives
this useful analytical estimate:

```text
B_completion ~= 4 * N_concurrent_children + F_window + D_window
```

`F_window` and `D_window` are findings and diagnostics emitted in the same
communication window. Start transitions and technical orchestrator transitions
add smaller boundary clusters. This formula estimates concentration; it is not
a transport guarantee or an exact PLC-cycle count.

Concurrency must be evaluated at both hierarchy levels. The active Test Case
count is the sum of active Test Cases in all active Suites. Parallel Suite and
Test Case orchestration can therefore accumulate bursts across the hierarchy.

## Choose the PLC orchestrator

Choose the orchestration model from both resource ownership and EventStream
headroom. Resource conflicts always take precedence: communication capacity is
not a reason to run dependent tests concurrently.

| Situation | Recommended starting point | Reason |
| --- | --- | --- |
| New or unmeasured UnitTest program | Serial | Establishes the lowest-burst reference and makes communication limits easier to identify. |
| Shared DUT state or exclusive Preparation | Serial | Prevents test interference independently of communication load. |
| Independent groups with known concurrency requirements | Slotted parallel | Makes concurrent width explicit and allows high-burst groups to be separated. |
| All children independent and worst-case EventStream headroom validated | Parallel | Minimizes execution time but permits the strongest synchronized bursts. |
| Existing parallel program with occasional backlog peaks | Slotted parallel with smaller or reorganized slots | Reduces temporal concentration without serializing the complete run. |
| Existing program with persistent backlog throughout execution | First tune observation/drain behavior, then reduce concurrency | Persistent pressure is not only a finalization-burst problem. |

### Serial

Serial orchestration starts one child after the previous child completes. It
normally preserves total event volume but minimizes temporal concentration.
Use it as the baseline when event density is unknown, resources are shared, or
communication headroom is limited.

### Slotted parallel

Slotted-parallel orchestration runs all children in the active slot together
and starts the next slot only after the current slot completes. Slot width is
therefore an explicit burst-control parameter. Size a slot from the number of
concurrent children, their possible completion clusters, and the findings they
can emit in the same observation window.

Slots should primarily express valid resource ownership and execution order.
Reducing a slot only to mask an unmeasured communication bottleneck can hide a
capacity problem that will return as the test program grows.

### Parallel

Parallel orchestration starts all registered children together. It has the
highest burst risk because child triggers, findings, and completions can cluster.
Use it only when the test resources are independent and communication headroom
has been validated with the actual workload. Total Test Case count alone is not
sufficient evidence because internal timing determines how strongly events
align.

## Recognize communication pressure

Use EventBuffer backlog and loss evidence, not only the final UnitTest result.
A completed `Passed` run does not prove that the transport has sufficient
headroom for a larger or differently timed run.

### Healthy behavior

- `LostEventCount` remains zero and `BufferFull` is never observed.
- Backlog rises during lifecycle boundaries but falls again while the run is
  still active.
- The buffer returns to empty after completion.
- Maximum backlog remains reasonably stable across repeated identical runs.
- The remaining free capacity is materially larger than one normal published
  batch.

The final point is a practical robustness check, not a contract guarantee. A
run that leaves less than one configured maximum batch free can fail after a
small timing change even though the measured run completed.

### Excessive average load

Average-load pressure exists when the producer creates events at least as fast
as the binding can read and release them over an extended period:

```text
average event production rate >= sustained release rate
```

Typical indicators are:

- backlog remains non-zero between lifecycle boundaries;
- backlog grows over several otherwise ordinary Test Cases or Suites;
- the consumer remains in active drain operation and rarely observes an empty
  buffer;
- slower monitoring or competing Runtime API activity makes the growth clearly
  worse;
- changing one isolated completion burst does not remove the trend.

Serial orchestration can reduce average production rate by preventing
concurrent children, but it does not reduce the total event count. If a serial
run still accumulates backlog continuously, first inspect binding observation,
payload-read, and release settings as well as competing communication. Also
check for unnecessary framework/project diagnostics and deliberately generated
finding traffic.

### Excessive burst load

Burst pressure exists when the buffer is normally empty or lightly occupied but
backlog jumps sharply around starts, assertion failures, slot boundaries, or
near-synchronous completion:

```text
events produced in one service window > free buffer capacity + events released
```

Typical indicators are:

- low average backlog but a high maximum backlog;
- repeated peaks at the same Suite, slot, or run-finalization boundary;
- `BufferFull` or increasing `LostEventCount` appears abruptly rather than
  after sustained growth;
- a serial run is stable while a parallel run of the same children is not.

This is primarily an orchestration problem. Convert full parallel execution to
slotted parallel, reduce the affected slot width, or place fast Test Cases that
finish together into different slots. Serializing the complete run is the
safest fallback but is usually unnecessary when only one group creates the
peak.

## Tune only when needed

When communication pressure is observed, change one dimension at a time and
repeat enough identical runs to capture timing variation.

1. **Confirm valid evidence.** Treat any `LostEventCount > 0` as an incomplete
   event log. Reset the EventBuffer through the supported application workflow
   before using the next run as capacity evidence.
2. **Remove avoidable competing load.** Disable raw event logging unless it is
   required for diagnosis and close concurrent TIA Portal, WPF, CLI, or other
   Runtime API observation paths where the binding documentation identifies
   them as relevant.
3. **Reduce observation latency.** Use the concrete binding's normal/recommended
   monitoring interval before changing PLC orchestration. A shorter interval is
   the simplest response when backlog begins before the first read.
4. **Keep active draining responsive.** Verify that known backlog uses the
   binding's drain cadence rather than waiting for the idle monitoring interval.
   Do not increase batch size blindly: a larger payload read can retain the
   readable prefix for longer on APIs with expensive per-event access.
5. **Reduce burst concurrency.** Replace Parallel with Slotted Parallel and use
   the smallest slot width that still provides the required execution overlap.
   Separate children with short, similarly timed lifecycles or many possible
   findings.
6. **Use Serial for the limiting scope.** Serialize only the Test Case or Suite
   level that creates the pressure. Serializing Test Cases inside one Suite does
   not require serializing independent Suites when their combined bursts remain
   bounded.
7. **Reduce event production only when semantically valid.** Remove redundant
   project diagnostics, not required lifecycle/result/aggregation evidence.
   Assertion findings and framework errors must remain observable.
8. **Revalidate the worst credible path.** Include failed assertions,
   Preparation transitions, and synchronized short tests. An all-passing path
   can have lower event density than the error path that matters operationally.

If loss remains with serial execution and the binding's recommended settings,
the workload is outside the demonstrated transport envelope. Splitting the
UnitTest program into separately started runs can reduce one run's pressure,
but it is an architectural workaround and should follow transport diagnosis,
not replace it.

## Validate the intended workload

1. Count the executed Suites and Test Cases and calculate the successful
   normal-path estimate.
2. Add one event for every assertion finding expected in the evaluated path.
3. Add preparation transitions and a margin for diagnostics, timeouts, and
   deliberately exercised error paths.
4. Determine the maximum active Suites and active Test Cases for the selected
   orchestration modes and estimate completion clusters.
5. Compare the estimated bursts with the concrete binding's measured backlog,
   drain behavior, and EventBuffer capacity.
6. Start with Serial when no representative measurement exists. Increase
   concurrency through explicit slots and finally Parallel only while measured
   headroom remains acceptable.
7. Validate the complete program on the intended runtime. Require zero lost
   events and retain headroom for run-to-run timing variation.

Do not convert one binding version's measurements into a general guarantee.
Runtime API latency, PLC cycle time, EventBuffer size, monitoring interval,
batch size, logging, host load, and concurrent engineering communication can
all change the operating envelope.
