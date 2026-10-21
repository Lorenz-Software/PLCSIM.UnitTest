# Beckhoff TwinCAT 3 (4026) Access Profile Configuration

This reference defines profile-owned fields below `accessProfile.values`; the
surrounding JSON file uses the existing PLCSIM.UnitTest configuration-file
contract. For installation, WPF workflows and troubleshooting see [usage](usage.md);
for complete managed-job templates see [examples](examples/README.md).
The [qualification record](verification.md#current-qualification) distinguishes
the distribution 2.0.0 CLI/WPF gate on XAE 3.1.4026.27 and local Usermode from
earlier 4026.26 service, application and subscription checks.
Safety authorization remains unsupported. Registration checks prerequisites;
it does not certify live access or replace installed-release verification.

## Identity And Scope

| Property | Value |
| --- | --- |
| Profile id | `beckhoff-tc3-4026` |
| Display name | `Beckhoff TwinCAT 3 (4026) Access Profile` |
| Profile/package version | `1.0.0` |
| Release package family | `access-profiles.beckhoff-tc3-4026` |

The profile version is independent of the TwinCAT build and ADS library version.
Supported scope is installed TwinCAT 3.1 Build 4026 XAE and an existing local
Usermode Runtime. Project/environment version selection remains authoritative
within that scope; no additional exact-patch gate or 4024 fallback is introduced.

Project ownership, provisioning intent, operation timeouts and release policy
remain application/session inputs. Do not duplicate them as profile settings.
There are no public system-mode, runtime-creation, license-activation, worker or
binding-selection options, and no separate global profile settings store.

## Project Sources

Application-managed engineering accepts a complete `.sln` project tree or a
`.tszip` archive. A standalone `.plcproj` without system configuration is not an
independent supported input. `project.source` remains a common configuration
field, outside `accessProfile.values`.

A supplied solution is opened in place, without a defensive project copy. CI
must provide an exclusively used workspace. Concurrent XAE editing or downloads
against that same project are not coordinated by the profile. External WPF
debugging uses the already loaded PLC application without opening, building or
downloading a project. Archives require an extracted/imported working project
as described below. Discovery, build, authorization, transfer and owned XAE
lifetime follow the [project deployment contract](contracts/project-deployment.md).

## Target References

| JSON path | Type | Requirement/default | Meaning |
| --- | --- | --- | --- |
| `plcRef.id` | string | Managed handling requires an unambiguous project target. Interactive discovery may resolve a single candidate; CLI jobs require the explicit id even then. Not required for external/no-download access. | Stable PLC target id from project discovery, distinguishing the containing system project and PLC project rather than relying on a display name alone. |
| `runtimeRef.id` | string | Explicit target when supplied. With `UseExisting` and no target selection, resolve the local standard Usermode Runtime; fail if unavailable. No hard-coded AMS default. | Local Runtime AMS Net ID, not its display name. |
| `runtimeRef.plcPort` | integer | Optional for external/no-download access; omission resolves to `851`. For managed transfer, derive the port from the selected PLC project. | PLC application ADS port. An explicitly configured port conflicting with the managed project fails before transfer. |

Keep the AMS target identity separate from the PLC application port. Validate
the Net ID syntax, local-target association and the selected PLC port; do not
silently replace an invalid or conflicting target with another one. Preserve
the distinction between an omitted port and an explicit override: the external
default of 851 must not become a conflicting managed-transfer override.

Explicit local targets can be presented even when broad enumeration is
incomplete, with actual availability checked during preparation. Discovery does
not create routes or perform an automatic network search. A missing PLC
application port is not proof that the containing Runtime is missing; managed
first download may create the application.

For external/no-download access, initial preparation fails if the system is
observed in RUN but the selected PLC port returns `TargetPortNotFound`. The
diagnostic identifies the Net ID and port; check the port configuration and
load the application in XAE before retrying. An existing stopped PLC remains
valid for preparation. System CONFIG and loss of a PLC port after preparation
retain the existing recovery behavior rather than failing this initial check.

### Targeting With And Without Download

Targeting follows project ownership and runtime provisioning intent, not the
identity of the WPF or CLI host. The AMS Net ID selects the Runtime system;
the PLC ADS port selects an application within that system.

| Typical use | Effective Runtime AMS Net ID | Effective PLC application port |
| --- | --- | --- |
| External WPF debugging, without download | Explicitly selected existing local Runtime. | `runtimeRef.plcPort`, or `851` when omitted; no engineering project is opened to determine it. |
| Managed WPF download | Explicitly selected existing local Runtime (`UseExisting`). | Port of the selected PLC project; an explicit conflicting `runtimeRef.plcPort` fails before transfer. |
| Managed CLI/CI execution | Explicit `UseExisting` target, or the profile-designated local standard Usermode Runtime when the target is omitted. | Port of the selected PLC project, with the same explicit-conflict check. |

The target Net ID saved in a TwinCAT source project is not authoritative for
managed deployment. A checked-in project may still name its developer's Runtime.
Use the prepared Runtime's verified identity for configuration activation and
PLC download, selecting that destination in the owned XAE context without
explicitly saving the changed destination back to the supplied source project.
Do not contact the source project's saved target as a fallback.

XAE may nevertheless persist that destination during its operations. This was
observed and accepted for disposable application-managed test workspaces; the
profile does not restore source files afterward. Do not assume direct project
access leaves a permanent development workspace unchanged.

For example, a project saved for Runtime `192.0.2.10.1.1` with its selected PLC
at port `852` can be deployed to a CI machine's verified local default Runtime
`192.0.2.20.1.1`. The effective PLC endpoint is `192.0.2.20.1.1:852`, not the
developer's Net ID and not an assumed port `851`.

This destination selection is not a general conversion of machine-specific
project configuration. The supplied project must suit the intended Usermode
environment; unrelated hardware mappings are not automatically rewritten.

### CLI And The Existing Local Default

Set application-level `runtime.provisioningMode` to `UseExisting` in schema
version 2. An explicit `accessProfile.values.runtimeRef.id` must resolve uniquely;
it is retained and an invalid target never falls back to the default. Without
an explicit id, discovery designates the existing local standard Usermode
configuration; preparation verifies the selected Runtime's actual identity.
Neither the first enumeration entry, a development AMS Net ID nor an assumed
local-router identity is a substitute for that resolution.

If the default is missing, ambiguous or unavailable, report the problem; do not
create an instance or use another candidate. An omitted provisioning mode means
`CreateOwned`, even if an address is present. This profile rejects `CreateOwned`
as `NotSupported`; schema-version-1 jobs must be migrated to version 2 and
explicit `UseExisting`. The XAE project's stored target is never a fallback.

Retain the actual selected target and ownership evidence for reconnect and
cleanup. Even an accidental `DestroyResource` request completes technical
cleanup with `Succeeded` and `ResourceKept`. Runtime instances and their licenses
are preserved. See the [runtime provisioning contract](contracts/runtime-provisioning.md).

## UnitTest Communication

| JSON path | Type | Required | Default | Meaning/constraints |
| --- | --- | --- | --- | --- |
| `unitTest.runControl` | string | No | `GVL_UnitTest.RunControl` | ADS RunControl root; an explicit value must be nonempty. Contract-owned subfields are derived from this root. |
| `unitTest.eventBuffer` | string | No | `GVL_UnitTest.EventStream` | ADS EventBuffer root; an explicit value must be nonempty. Contract layout/version validation remains required. |
| `unitTest.observationStrategy` | string | No | `polling` | Supports `polling` and opt-in `subscription` for EventBuffer and RunControl. Other strategies are rejected, never substituted. |
| `unitTest.monitoringIntervalMs` | integer | No | `10` | Positive 32-bit integer. Polling: idle wait between attempts. Subscription: ADS OnChange sampling interval, not a PC-side wait. |
| `unitTest.drainIntervalMs` | integer | No | `1` | Positive 32-bit integer. Polling: wait while backlog/deletion is outstanding. Subscription: fallback wait for pending deletion progress; notifications can wake sooner. |
| `unitTest.maxEventsPerBatch` | integer | No | `32` | Positive 32-bit integer, maximum number of events captured per batch. Effective size is bounded by the safely readable FIFO prefix and PLC-reported usable capacity. |

Polling intervals are waits, not guaranteed wall-clock sampling periods; read
and processing time is additional. Subscription sampling is configured on ADS,
but delivery and processing are not hard real-time guarantees. The 10/1/32
defaults remain a conservative starting configuration. ADS notifications are
not equivalent to a PLC-cycle callback. See the strategy details below and the
[measured comparison](performance/README.md#polling-versus-subscription).

The TwinCAT FIFO's usable capacity is `BufferLength - 1`, not a hard-coded 256.
Do not count its unused ring position as a readable event. Changing a symbol
root does not waive the PLC communication contract. See the
[PLC consumer mapping](contracts/tc3unittest-consumer-mapping.md).

### Subscription Strategy Contract

Both observers are implemented, covered by offline tests and qualified through
production-service/Core-workflow probes on local 4026.26 Usermode. Repeated runs,
external PLC STOP/RUN, CONFIG/RUN and reconnect resumed native callbacks and
complete event delivery. This does not imply every lifecycle transition is free
of diagnostics; see the [qualification findings](verification.md#subscription-qualification).

`unitTest.observationStrategy = "subscription"` applies to both EventBuffer
metadata and RunControl Execution observation. Polling remains the default.
Unsupported strategies fail validation; there is no silent fallback. Strategy
selection and allowed values remain profile-owned, using the existing application
configuration projection and JSON envelope. No shared strategy enum or application
interface change is required by this extension.

| Setting | Subscription meaning | Default |
| --- | --- | ---: |
| `monitoringIntervalMs` | Positive 32-bit ADS `OnChange` sampling interval, passed as `NotificationSettings.CycleTime` when registering both observers. Not a PC-side wait or processing throttle. | `10` ms |
| `drainIntervalMs` | Positive 32-bit fallback interval for an explicit progress read while EventBuffer release confirmation is outstanding. Usable notifications can wake processing sooner. | `1` ms |
| `maxEventsPerBatch` | Existing bounded FIFO-prefix limit, unchanged. | `32` events |

Subscriptions remain registered for their valid access lifetime; the interval
does not cause repeated registration. Process usable notifications without an
additional monitoring delay. Known readable backlog continues without requiring
another producer increment once previous release is confirmed. A metadata
change with no readable events updates status without a payload read.

Each observer performs a control read after 100 ms without a usable observation.
This is an internal verification interval, not an additional public field or a
hard real-time guarantee. Reads and scheduling can add latency. Bootstrap,
necessary post-command status confirmation, payload consistency checks and
final-empty confirmation remain independent explicit reads. Existing safety
checks are not replaced by notification timing or by absence of notifications.

The EventBuffer consumer subscribes to the contiguous, symbol-resolved FIFO/status
span and separately observes the deletion command as a wakeup only. Notification
payloads can supply a nonempty pre-read candidate. Payload post-validation, the
last command check before writing and empty confirmation remain explicit reads.
An accepted watermark blocks reuse of that prefix until observed deletion progress
covers it; command zero alone is not acknowledgement. A regressing notification
sequence is latched for revalidation rather than hidden by later callbacks.
The existing single consumer writes the watermark before publishing its batch;
it does not wait for PLC deletion before publication.

RunControl subscribes to the complete symbol-resolved Execution structure. Unlike
EventBuffer candidates, these notifications only request a fresh explicit read;
the existing decoder and observation interface publish that read, not callback
payloads. Execution has no sequence counter that orders a delayed callback against
an explicit read. This avoids publishing an older notification after a newer
bootstrap or fallback observation. Pending wakeups coalesce; a notification during
a read requests another read. Transient transitions can be missed, as with polling;
notifications are not a replacement for the event history.

Start/Abort/Reset remain single command writes. An accepted command requests an
asynchronous fresh status read without waiting for a notification. That first
successful post-command read is published even if unchanged; cached pre-command
status is never republished as confirmation. Command acceptance is still only
transport acceptance, not proof of PLC processing. Failure of the later read does
not replay the write. Unchanged ordinary observations remain deduplicated.

Callbacks capture data and signal serialized processing, without invoking the
workflow directly. Retire stale-generation callbacks on context changes and
re-register with fresh symbols and bootstrap after recovery. Delayed samples
must not overwrite newer confirmed state; relevant reset/fault observations
must not disappear through work coalescing. OnChange observations do not provide
a guaranteed history of every PLC transition; the EventBuffer remains the
source of run-event evidence. This contract does not change PLC event semantics,
result evaluation, release/publication ordering or system-state monitoring.

### Subscription Lifetime and Recovery

Registrations belong to one technical ADS binding, not to its numerical handles.
Both observers resolve symbols, register and bootstrap again after rebinding.
The Runtime endpoint retains a binding generation for observed CONFIG/non-RUN,
PLC STOP/unavailability and system/PLC session loss. Returning to RUN does not
erase that generation change: even an observer that missed the unavailable phase
must retire its old binding. The existing availability checks still require
verified system RUN and a running PLC before data access. This does not detect
state transitions that neither Runtime monitoring nor the ADS session observed.

Readers reject results across a binding-generation change. Commands cannot use
an already obsolete binding. An EventBuffer write already accepted by ADS still
delivers its batch, including during cancellation; recovery does not replay it.
Within the same Runtime generation, an ADS-only rebind preserves the accepted
watermark until deletion progress or a real bootstrap reset is observed. A new
Runtime generation or communication context does not inherit that watermark.

Explicit reconnect releases dependent communication before replacing Runtime
access. Release/close cancels and joins observers, retires callback generations
and attempts notification/handle cleanup. Stopping only EventStream monitoring
retains its prepared access until communication release. Healthy fallback reads
are not proof of notification delivery; live recovery qualification must verify
callbacks after re-registration, as well as continued reads.

The strategy does not change router/session, TwinCAT-system or PLC-state
monitoring. Those remain independent, including the internal 250 ms system
check. External changes are observed regardless of who issued a command.
Notifications and control reads observe current state, not every short-lived
transition; disconnected access must recover before fresh state can be supplied.

Use the updated profile package for subscription configuration. No common DLL
interface or configuration-envelope change is required, and existing polling
configurations keep their semantics. Older packages that reject `subscription`
must be updated; do not silently substitute polling or another strategy.

## Archive Workspace

| JSON path | Type | Required | Default | Applicability |
| --- | --- | --- | --- | --- |
| `twincat.archiveWorkDirectory` | string | No | Profile-managed temporary working storage | Parent directory for a separate working subdirectory per `.tszip` extraction/import. |

Project the field in the WPF configuration editor only for archive sources.
Direct `.sln` handling ignores it. A configured path must be valid, and failures
to create or use the workspace must be reported during preparation.

Always create a separate profile-owned working subdirectory. Do not extract
over existing user files or treat the configured parent as disposable. Cleanup
removes only the working directory owned by the operation, not the original
archive, source solution, parent directory or unrelated contents. Configuration
validation alone must not create the workspace or start XAE.

## Runtime Availability

Positively observed CONFIG mode is mapped to `InstanceState=Available` while
the system remains reachable, `OperatingState=Unavailable` and
`ReadyForExecution=false`. Existing controls are gated without losing runtime
selection or forcing the WPF stepper back to setup. Managed transfer is not
blocked merely because the system is in CONFIG. Ordinary preparation,
reconnect and PLC Start/Stop do not implicitly switch system mode.

System-state observation remains independent of UnitTest communication. After
RUN and actual PLC availability are observed, restore access and use the
existing communication-validation/monitoring path. See
[system mode and PLC availability](contracts/runtime-provisioning.md#system-mode-and-plc-availability)
for the complete mapping and qualification cases.

## Registration And Validation

Registration checks installed prerequisites and package capabilities without
starting XAE, activating configuration or requiring a running PLC application.
Profile-owned registration/cache data uses the supplied writable profile data
directory, never the installed package tree. Registration does not certify
future Runtime availability or license validity.

Lightweight source evaluation identifies supported project/archive structure
without engineering startup or archive extraction. Invalid field types, ranges
or conflicting target values fail validation. Partial interactive import can
remain unresolved until target selection; automated execution requires
unambiguous targets. Missing prerequisites are unavailable, corrupt evidence or
access failures are errors, and unsupported operations report `NotSupported`.
Do not publish a capability as implemented or qualified solely because its
configuration is accepted.

Registration returns `Registered` with usable project/runtime capabilities and
UnitTest contract `1.0.0` when the required 4026 XAE shell, engineering and Usermode
components are installed. Missing prerequisites yield `Unavailable`; installed
API evidence is retained and source selection does not offer execution.
See [registration and source inspection](registration.md) for persistence,
failure handling and metadata checks.

## Example: External Runtime Values

This fragment is `accessProfile.values` for external/no-download use. Replace
the example Net ID with the explicitly selected local Usermode Runtime identity;
it is not a default or an IP address plus an assumed suffix.

```json
{
  "runtimeRef": {
    "id": "192.0.2.10.1.1",
    "plcPort": 851
  },
  "unitTest": {
    "runControl": "GVL_UnitTest.RunControl",
    "eventBuffer": "GVL_UnitTest.EventStream",
    "observationStrategy": "polling",
    "monitoringIntervalMs": 10,
    "drainIntervalMs": 1,
    "maxEventsPerBatch": 32
  }
}
```

### Opt-in Subscription Values

The packaged [subscription values example](examples/subscription-values.json)
is an `accessProfile.values` fragment, not a complete application configuration.
Replace its example Net ID with your selected local Runtime. It keeps 10/1/32
and changes only `unitTest.observationStrategy` to `subscription`; this selects
both observers. WPF exposes the choice through the profile configuration fields.
CLI transports the same values but retains its existing target-selection rules.
There is no new application-wide setting or global strategy selector.

## Example: Managed Archive Configuration

This common configuration selects the profile and an archive. Replace the
project-target placeholder with an id returned by discovery. CLI uses the
existing local default Runtime, derives its PLC port from the selected project
and uses the communication defaults omitted here.

```json
{
  "version": 2,
  "runtime": {
    "provisioningMode": "UseExisting"
  },
  "project": {
    "source": "C:\\CI\\workspace\\LibraryTests.tszip"
  },
  "accessProfile": {
    "id": "beckhoff-tc3-4026",
    "version": "1.0.0",
    "values": {
      "plcRef": {
        "id": "<PLC target id from discovery>"
      },
      "twincat": {
        "archiveWorkDirectory": "C:\\CI\\work\\twincat"
      }
    }
  }
}
```

For a direct solution, supply its `.sln` path and omit `twincat`. It is opened
in place, not extracted or copied. Common timeout and output settings continue
to follow the application contract; they are not profile fields.

For measured interval/batch tradeoffs and the unchanged 10/1/32 defaults, see
[EventStream performance](performance/README.md). The
[workload-sizing guide](performance/event-stream-workload-sizing.md) explains
burst pressure and practical orchestrator/configuration choices.
