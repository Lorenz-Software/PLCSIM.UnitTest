# Retained performance data

The CSV files retain compact evidence for the 2026-10-08 production polling
matrix and the separate 2026-10-09 strategy comparison, not raw PLC payloads,
logs or temporary runtime harnesses.

- `run-measurements.csv`: 90 measured successful runs, ten per configuration.
- `configuration-summary.csv`: nine configurations, duration/resource statistics,
  observed backlog, capture cost and actual read sizes.
- `read-size-summary.csv`: 50 actual-size bins from 8517 nonempty captures.
- `strategy-summary.csv`: matched polling/subscription comparison, ten measured
  runs per strategy, with separate binary identity below.

Warm-ups, two recovery controls and the batch-1 timeout are excluded from these
success statistics. The timeout is described in the [overview](../README.md).

## Identity

Source baseline: `c09c27e`, with the profile-local production Sum Read change
included in the measured build. The baseline commit alone does not identify
that change; the binary hashes below identify the exact measurement inputs.

| Artifact | SHA-256 |
| --- | --- |
| Core | `73838337B6FDB55E00FD0C815D8AC37DC26E516A0308B922AA4999C3DC53FC1F` |
| Beckhoff profile | `0E38B56B5F713F002427DFB42181B274203765B6791E772E652B717E1D20065A` |

Release, ADS .NET 7.0.389, installed XAE 3.1.4026.26, local Usermode runtime
reporting 3.1.4026, PLC port 851. Host: 16 logical processors. Effective task
cycle value 10000 corresponds to 1 ms; usable buffer capacity 255. SelfTests
contained 12 serial Suites and 122 Cases; healthy runs had 1563 run-owned events.
No cryptographic PLC source fingerprint was recorded. These workload checks
support comparability within the campaign, not identity with a later program.

## Subscription comparison identity

`strategy-summary.csv` is a separate matched production comparison dated
2026-10-09, not an extension of the earlier polling sweep population. Both
strategies used these same binaries:

| Artifact | SHA-256 |
| --- | --- |
| Core | `EA405281BA960250F54F5005B7045F4875D3160D2E2AADAA67568803E52A16A5` |
| Beckhoff profile | `0DB7EB94BB999A03DF743A559256E52855C8F5BD2AFEB3B1717C194FFCF3CA73` |

Release, ADS 7.0.389, local Usermode, 1 ms task, 255 usable positions,
10/1/32, same 122-case/1563-run-event workload. Ten measured runs per strategy;
one warm-up each and separate recovery runs excluded. No PLC source fingerprint
was recorded. Callback counts come from passive hooks on the production clients,
not independent subscriptions. Logical pre-read counts are not physical ADS-read
counts. Capture P95 pools captures within runs; CPU/allocation include the host,
Core, SDK and measurement adapters. Raw payloads and harnesses are not committed.

## Metric definitions

`monitoringMs`, `drainMs` and `batchLimit` are configured values. `maxEvents`
is the largest actual payload, not the configured maximum. `maxBacklog` is the
maximum observed count. `meanRunMaxBacklog` averages each run's maximum.
SD uses the sample denominator n-1; P95 uses nearest rank. A single sample
has no sample SD. Read-size statistics pool configurations; samples within a
run are correlated.

Run duration, payload and capture use PC monotonic time. Capture spans pre-read
metadata through watermark-write completion; payload includes decode. Neither
separates PLC execution from all transport overhead. CPU is whole-process CPU
time; allocation is cumulative managed bytes, not retained memory. Logging
was Information/raw off with in-memory capture and serialization outside the
measured windows.

## Regenerate diagrams

From the repository root, run:

```powershell
./access-profiles/beckhoff-tc3-4026/tests/performance/render-diagrams.ps1
```

The script uses only these CSV summaries and makes no PLC/network access.
It reproduces the backlog and size/cost diagrams, not raw-sample regression or
a new measurement campaign. No fitted line is published because the measured
linear relationship was too weak to support a predictive model.
