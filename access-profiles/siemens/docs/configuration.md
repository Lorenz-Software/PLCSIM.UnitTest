# Siemens Access Profile Configuration

This document describes the Siemens access profile fields below
`accessProfile.values`. The surrounding configuration file follows the shared
PLCSIM.UnitTest configuration-file contract.

Runtime provisioning mode is session setup, not a Siemens profile configuration
field. Schema version 2 stores it at `runtime.provisioningMode`; omission means
`CreateOwned`, including legacy schema-version-1 files. A target address alone
never implies `UseExisting`. Operation timeouts and runtime release policy are also owned by the
application/shared workflow configuration and must not be placed below
`accessProfile.values`.

## Target References

| JSON path | Type | Required | Default | Meaning |
| --- | --- | --- | --- | --- |
| `plcRef.id` | string | Required when project handling selects a PLC | None | Stable PLC target id published by Siemens project discovery. Interactive applications may write this value from target selection. |
| `runtimeRef.id` | string | Required for `UseExisting` unless supplied by runtime-target selection | None | Stable PLCSIM runtime target id. Conflicts with `CreateOwned`. |

Siemens does not designate a discovery default, even when only one instance
exists. `UseExisting` therefore needs an explicit target that resolves uniquely.
Invalid or unavailable selections never fall back to another instance.
For `CreateOwned`, omit `runtimeRef.id` and provide the creation parameters below;
they are retained rather than cleared by CLI target resolution.

## Runtime

| Field id | JSON path | Type | Required | Default | Applicability and constraints |
| --- | --- | --- | --- | --- | --- |
| `siemens.runtime.instanceName` | `runtimeRef.instanceName` | string | Yes | None | Projected and required only for `CreateOwned`; must not be empty. |
| `siemens.runtime.storagePath` | `runtimeRef.storagePath` | string | No | Session-specific directory below `%TEMP%\\PLCSIM.UnitTest\\runtimes` | Used only for `CreateOwned`. Environment variables are expanded and the value must be a valid path. |
| Not projected | `runtimeRef.cpuType` | string | No | `s7-1500` | Used only for `CreateOwned`. V1 accepts `s7-1500` and the compatibility spelling `s71500`; both map to S7-1500. |

For `UseExisting`, create-owned-only values are ignored. The application
supplies the provisioning mode separately as session intent.

## UnitTest Communication

| Field id | JSON path | Type | Required | Default | Constraints |
| --- | --- | --- | --- | --- | --- |
| `siemens.unitTest.runControl` | `unitTest.runControl` | string | Yes | `DB_UnitTestRunControl` | Symbolic RunControl data-block name; must not be empty or contain control characters. |
| `siemens.unitTest.eventBuffer` | `unitTest.eventBuffer` | string | Yes | `DB_UnitTestEventBuffer` | Symbolic EventBuffer data-block name; must not be empty or contain control characters. |
| `siemens.unitTest.observationStrategy` | `unitTest.observationStrategy` | string | No | `polling` | `polling` or `plcCycle`. The selected PLCSIM binding remains authoritative for support. There is no automatic fallback. |
| `siemens.unitTest.monitoringIntervalMs` | `unitTest.monitoringIntervalMs` | integer | No | Strategy-specific | Positive 32-bit integer in milliseconds when present. Omitted `polling` resolves to `10` ms. Omitted `plcCycle` observes every PLC cycle. An explicit `plcCycle` value is a minimum interval between complete observations. |
| `siemens.unitTest.drainIntervalMs` | `unitTest.drainIntervalMs` | integer | No | `1` | Positive 32-bit integer in milliseconds. Polling uses it while EventBuffer backlog or a pending PLC-side release is known. With `plcCycle`, known backlog bypasses the monitoring throttle and continues at the next available sync point. |
| `siemens.unitTest.maxEventsPerBatch` | `unitTest.maxEventsPerBatch` | integer | No | `32` | Positive 32-bit integer. Maximum oldest contiguous FIFO prefix captured and published per batch. The effective value is capped by the PLC-reported EventBuffer length. |

### Observation Guidance

`monitoringIntervalMs` and `drainIntervalMs` are waits after completed
observation steps, not fixed sampling periods. Runtime API processing time is
added to the selected wait. Polling uses the monitoring cadence while no backlog
is known. After a valid snapshot reports remaining work, or while a PLC-side
release is pending, it uses the drain cadence until the buffer is caught up.

The `10` ms monitoring default limits the initial backlog for burst-heavy or
less serial UnitTest programs. The `1` ms drain default minimizes delay around
the PLC release roundtrip. The `32`-event batch default bounds the native
Runtime API read window before each validated prefix can be released.

- `20` ms monitoring is a valid override when lower idle observation activity
  is preferred and the workload has sufficient EventBuffer reserve.
- `50` ms and higher reduce idle observation work but allow a larger initial
  backlog. Use them only for workloads whose event production is understood.
Measured batch-size and monitoring-interval trade-offs depend on the selected
binding, PLC program and runtime environment. Use the
[workload-sizing guide](../plcsim-advanced/docs/performance/event-stream-workload-sizing.md)
and the selected binding's performance guide for its recorded workload, buffer
settings and measured limits; do not generalize one fixture's rates to another.

`pollIntervalMs` remains a silent deprecated compatibility alias for
`monitoringIntervalMs`. A configuration must not specify both names.

For `plcCycle`, supporting PLCSIM bindings register Siemens sync-point
observation and use bounded sync-point waits to wake their dedicated
communication worker. PLC reads, EventBuffer parsing, release writes, and
publication stay on the normal worker path. Omitting
`monitoringIntervalMs` permits one complete observation at every usable sync
point. Supplying a positive value suppresses complete idle observations until
that minimum interval has elapsed. Known EventBuffer backlog and pending release
work still continue at the next sync point, so throttling does not defer active
draining to the idle interval.

Polling remains the profile default. Unthrottled cycle observation can increase
empty-read work; a monitoring throttle reduces idle activity but is not a
capacity guarantee. Validate unusually burst-heavy programs against the selected
binding and runtime environment before changing strategy or cadence.

Raw EventStream logging does not require different communication intervals, but it
should remain limited to diagnostic runs because it can increase worker CPU and
log volume. The PLC framework defaults to an EventBuffer length of 256,
providing a usable FIFO capacity of 255 events. The length can be changed in
the PLC project; it is not fixed by the consumer. The worker derives the
effective batch limit from the PLC-reported
length and evaluates the published FIFO metadata, including `BufferFull` and
event-loss evidence, rather than assuming a fixed 255- or 256-event capacity. No
interval or batch size guarantees a maximum sustainable event rate; validate
unusually burst-heavy test programs against their selected PLCSIM binding and
runtime environment.

## TIA Project Archives

| Field id | JSON path | Type | Required | Default | Applicability and constraints |
| --- | --- | --- | --- | --- | --- |
| `siemens.tia.archiveWorkDirectory` | `tia.archiveWorkDirectory` | string | No | Binding-managed temporary workspace | Projected only when the selected project source is an archive supported by the TIA binding. Environment variables are expanded and the value must be a valid path. |

Direct TIA project files ignore the archive workspace.

## Example: Existing Runtime

This is an `accessProfile.values` fragment. In a CLI job, use schema version 2
and add `"runtime": { "provisioningMode": "UseExisting" }` at the application
configuration root, beside `project` and `accessProfile`.

```json
{
  "plcRef": {
    "id": "PLC_1"
  },
  "runtimeRef": {
    "id": "PlcUnderTest"
  },
  "unitTest": {
    "runControl": "DB_UnitTestRunControl",
    "eventBuffer": "DB_UnitTestEventBuffer",
    "observationStrategy": "polling",
    "monitoringIntervalMs": 10,
    "drainIntervalMs": 1,
    "maxEventsPerBatch": 32
  }
}
```

## Example: Create Owned Runtime

This values fragment works with omitted mode (schema version 1 or 2), or with
explicit `"runtime": { "provisioningMode": "CreateOwned" }` in schema version 2.

```json
{
  "plcRef": {
    "id": "PLC_1"
  },
  "runtimeRef": {
    "instanceName": "UnitTestRuntime",
    "storagePath": "%TEMP%\\PLCSIM.UnitTest\\runtimes\\UnitTestRuntime",
    "cpuType": "s7-1500"
  },
  "unitTest": {
    "runControl": "DB_UnitTestRunControl",
    "eventBuffer": "DB_UnitTestEventBuffer",
    "observationStrategy": "polling",
    "monitoringIntervalMs": 10,
    "drainIntervalMs": 1,
    "maxEventsPerBatch": 32
  }
}
```

## Example: PLC-Cycle Observation

Omit `monitoringIntervalMs` to observe every usable PLC cycle. This requires a
selected PLCSIM binding that supports `plcCycle`; unsupported bindings fail
communication preparation instead of falling back to polling.

```json
{
  "plcRef": {
    "id": "PLC_1"
  },
  "runtimeRef": {
    "id": "PlcUnderTest"
  },
  "unitTest": {
    "runControl": "DB_UnitTestRunControl",
    "eventBuffer": "DB_UnitTestEventBuffer",
    "observationStrategy": "plcCycle",
    "drainIntervalMs": 1,
    "maxEventsPerBatch": 32
  }
}
```

Add `"monitoringIntervalMs": 10` to make 10 ms the minimum interval between
idle complete observations while retaining sync-point alignment and immediate
backlog draining.
