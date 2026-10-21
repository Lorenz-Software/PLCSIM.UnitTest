# PLCSIM Advanced V8 performance source data

This directory owns the stable real-runtime source data used to document the
performance characteristics of the current PLCSIM Advanced V8 plugin.

It is not a development-history archive. Measurements of superseded scalar or
full-snapshot readers, intermediate Debug builds, obsolete UnitTest workloads,
and corrected harness behavior are intentionally excluded.

## Current data sets

| File | Rows | Scope |
| --- | ---: | --- |
| `real-runtime-capacity-runs.csv` | 440 | Individual runs of the current adaptive V8 EventStream reader. |
| `real-runtime-capacity-summary.csv` | 44 | One aggregate row per measured PLC-cycle, observation-strategy, and monitoring-interval combination. |
| `event-payload-read-samples.csv` | 110 | Controlled and natural adaptive payload-read samples. |
| `event-payload-read-summary.csv` | 11 | Published aggregate payload-read points and fit membership. |

The run-level table is the durable evidence source. The summary table is
derived from it and is intended for documentation, diagrams, and comparison.

## Measurement boundary

The matrix uses the current V8 reader with these fixed conditions:

- compiler-optimized Release binaries;
- raw EventStream logging disabled;
- `drainIntervalMs=1`;
- `maxEventsPerBatch=32`;
- EventBuffer length 256;
- PLC-side UnitTest communication contract and framework version `1.0.0`;
- workload identifier `expanded-unit-test-v1`;
- stable workload fingerprint
  `9b25b35f8b24566653ed7b657677fe5fd854d405cb8991887486641dce2f522a`;
- 10 Test Suites, 51 Test Cases, and 479 observed events per complete run;
- no concurrent TIA Portal online observation and no concurrent WPF or CLI
  communication.

Configured PLC minimum cycle times are 1, 5, 10, and 15 ms. Each block contains
these points:

- `polling` at 5, 10, 25, 50, and 100 ms;
- `plcCycle` without a configured monitoring interval and at explicit 5, 10,
  25, 50, and 100 ms minimum intervals.

Every point contains ten complete normal runs. Stop/start recovery, Abort, and
command-responsiveness scenarios are excluded. Every retained run completed
with a PLC-side `Passed` result and zero lost events.

The `sourceRevision` column records the committed repository HEAD visible to
the harness during measurement. The performance-tooling changes were still in
the same uncommitted worktree; the repository commit that first contains these
tables is therefore the complete source-state boundary.

## Statistics

The summary is calculated only from the ten retained runs at each point:

- mean is the arithmetic mean;
- median and p95 use the nearest-rank method;
- with ten samples, nearest-rank p95 equals the observed maximum;
- backlog is the maximum producer backlog observed within a run;
- lost events are the maximum PLC-reported lost-event count observed within a
  run;
- worker CPU and private-memory deltas are process observations, not isolated
  per-API-call costs.

## Interpretation boundary

The measured UnitTest program is calibration evidence, not a universal
capacity profile. Permanent guidance must derive expected event volume and
burst concentration from Suite count, TestCase count, assertion/finding volume,
orchestration mode, PLC cycle time, monitoring interval, drain interval, batch
size, and EventBuffer length. The matrix then bounds and plausibility-checks
that general model for the measured V8 environment.

Claims about raw EventStream logging, concurrent TIA Portal observation,
different EventBuffer lengths, different batch sizes, or substantially
different workloads require current real-runtime evidence before they are
added to the permanent V8 guidance.

## Payload-read boundary

The payload-read samples use compiler-optimized Release binaries, a configured
1 ms PLC minimum cycle, unthrottled `plcCycle`, a 1 ms drain interval, and raw
EventStream logging disabled. Ten controlled samples were recorded at each
requested size and ten naturally occurring samples at 32 events.

The operating-range regression uses controlled samples from 1 through 192
captured events. The original full-boundary gate requested 256 positions and
consistently captured 255 events in the measured fixture, configured with an
EventBuffer length of 256, the framework default with 255 usable FIFO entries.
The stable summary therefore records that observation
as a 255-event boundary and excludes it from the operating-range fit. This is
not a fixed consumer capacity or a requirement for other PLC projects. The
individual sample table retains the original requested target so the measurement
is not rewritten after collection.
