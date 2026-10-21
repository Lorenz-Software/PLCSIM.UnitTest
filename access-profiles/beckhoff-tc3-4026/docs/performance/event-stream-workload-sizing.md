# EventStream workload sizing and orchestrator selection

Use this guide to recognize communication pressure and select practical
remedies. It complements the [measured performance envelope](README.md), not
a fixed limit on the number of Suites or Test Cases.

- [Estimate volume and concentration separately](#estimate-volume-and-concentration-separately)
- [Choose the PLC orchestrator](#choose-the-plc-orchestrator)
- [Recognize communication pressure](#recognize-communication-pressure)
- [Tune only when needed](#tune-only-when-needed)
- [Validate the intended workload](#validate-the-intended-workload)

## Estimate volume and concentration separately

Event volume consists of RunControl events, each Suite's lifecycle and
orchestration events, each Test Case's lifecycle and assertion events, plus
preparation and framework diagnostics. Count the emitted evidence for each
representative path rather than treating every assertion or case as one event.
Failure and recovery paths may produce different totals.

For planning, add these contributions across the intended hierarchy, calibrated
against a complete trace from that program. The reference workload generated
1563 run-owned events for 12 Suites and 122 Cases; this is not an events-per-case
conversion factor. Do not import an event-count formula from another framework.

A buffer need not hold a whole run. What matters is how much evidence arrives
before the consumer can read and the PLC can acknowledge deletion. During a
window, backlog changes approximately by newly published events minus events
actually deleted. PC acceptance alone does not prove PLC deletion. Average
events/second can hide a short burst that occupies most of the buffer.

## Choose the PLC orchestrator

| Orchestrator | Expected concentration | Practical use |
| --- | --- | --- |
| Serial | Usually the smallest overlap between child lifecycles | Conservative starting point for unknown workloads or shared resources |
| Slotted parallel | Children in one slot run together; slot size bounds concurrency | Increase concurrency gradually while measuring each slot's burst |
| Parallel | All children may progress together | Independent work with verified burst headroom and suitable PLC resources |

This applies at both Suite and Test Case levels. Parallel Suites containing
parallel Cases can align many start/completion events. Serial execution can
still burst within one Case, for example from several assertions in one cycle.
Slots with equal child counts can behave differently because of internal timing.
These are qualitative recommendations, not measured parallel speedup claims.

Buffer-aware orchestrators may wait for free space instead of overflowing.
That preserves evidence, but can lengthen the run or eventually reach a PLC
timeout. With custom sequencing, do not assume the same pressure protection.

## Recognize communication pressure

| Observation | Interpretation to investigate | First practical action |
| --- | --- | --- |
| Brief high backlog followed by full drainage | Concentrated burst, not necessarily sustained overload | Keep headroom; stagger starts or reduce a parallel slot if needed |
| Backlog remains high and run duration increases | Consumer/deletion throughput may throttle PLC progress | Avoid tiny batches; compare with 10/1/32 before changing PLC logic |
| Lower backlog but unchanged run duration | More communication headroom, likely PLC scheduling/timing limit | Keep defaults unless the extra headroom is needed |
| Producer advances while deleted counter stops | Delayed release processing or communication problem, not proof of either | Inspect health/diagnostics and verify cyclic EventBuffer request handling |
| Lost-event count increases | Evidence is incomplete | Investigate the loss; do not treat green individual results as a complete run |
| PLC framework timeout with zero loss and high backlog | Pressure-induced waiting is possible | Check timeout owner and backlog history; do not simply increase timeouts |

Backlog alone cannot identify a programming defect or its exact cause. Also
check PLC result, final empty evidence and producer/deleted progress. A healthy
monitor does not repair an already incomplete run.

## Tune only when needed

1. Start with polling **10 ms / 1 ms / 32 events** and a healthy complete run.
2. Record whole-run duration, observed backlog, actual batch sizes, loss and
   final deletion. Repeat with an unchanged workload; distinguish normal
   variation from an improvement.
3. For short bursts, test monitoring 5 ms or batch 64 separately. Neither is
   guaranteed to shorten the run. Keep the better setting only when the target
   workload benefits without sacrificing resource use or correctness.
4. Avoid shrinking batches merely to make individual reads look cheaper. In the
   reference matrix, batch 8 greatly lengthened execution and batch 1 reached
   a PLC timeout without losing events.
5. If pressure persists, reduce simultaneous Suite/Case activity: parallel to
   bounded slots, smaller slots, or serial where concurrency is not needed.
   Preserve intended test semantics and resource independence.
6. Investigate stopped release progress or missing evidence before tuning.
   Increasing a timeout or buffer size can hide the symptom without restoring
   reliable communication. Reset only from the appropriate stable lifecycle state.

Application-specific tuning belongs to the user. More Suites/Cases do not
automatically require different settings; their timing, event density and
orchestration determine the communication load.

### When to try subscription

For a supported local target, compare opt-in `subscription` at the same 10/1/32
before changing additional parameters. It is useful when idle RunControl reads
or burst backlog matter. The matched reference reduced RunControl reads and
backlog, but produced more/smaller batches and higher cumulative allocation;
the complete run was not materially faster. Keep polling as the default and
measure the user's workload before choosing. `monitoringIntervalMs` then controls
ADS sampling, not a PC wait. Subscription does not change PLC orchestrator
semantics, replace final-empty verification or solve stopped release processing.

## Validate the intended workload

1. Record the Suite/Case hierarchy and representative successful and failing
   event paths; use a complete trace to calibrate the event-volume estimate.
2. Identify concurrency at both hierarchy levels and possible synchronized
   bursts. Check buffer-aware waiting as well as lost events.
3. Validate on the intended runtime using fixed logging, task period and
   configuration. Change one setting at a time and repeat complete runs.
4. Require complete evidence, zero loss and confirmed final drainage; compare
   backlog and duration together to detect PLC throttling without overflow.
5. Recheck after material workload or environment changes. The measured
   SelfTests program is a calibration point, not a Suite/Case capacity limit.
