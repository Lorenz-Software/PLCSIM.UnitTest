# PLCSIM Advanced V8 Troubleshooting

This document covers installed-environment and operational failures owned by
the Siemens PLCSIM Advanced V8 binding.

## Prerequisites

- PLCSIM Advanced V8 is installed and licensed locally.
- The deployed V8 package contains its `net8.0` facade, production `net48`
  worker, manifest, and production dependencies.
- The installed Runtime API registry and API-directory evidence is readable and
  resolves the required native entry points.
- `UseExisting` uses a registered local PLC instance containing the
  expected downloaded program.
- `CreateOwned` is used only when the application owns creation and final
  destruction of the temporary PLC instance.

Missing installed evidence is an unavailable environment. An inaccessible,
stale, incomplete, or unloadable selected Runtime API is an environment failure.
Do not copy Siemens Runtime API files into the application package as a repair.

## Runtime Preparation

`UseExisting` never creates, powers on, unregisters, or deletes the selected
user-owned instance. `CreateOwned` creates and powers an instance through the
Runtime API and removes it on destructive release.

Common preparation failures:

| Evidence | Meaning and action |
| --- | --- |
| `IsEmpty` / `SREC_IS_EMPTY` on Run | The PLC instance has no downloaded configuration/program. Download a compatible project before starting it. |
| `OutOfMemory` while powering on | PLCSIM Advanced could not activate the requested instance. Inspect PLCSIM resources and other active instances, then release or stop unneeded resources before retrying. |
| Runtime Manager connection lost | The manager application/API connection disappeared. End or reconnect the application runtime context after PLCSIM is usable again. |
| Unclear owned-runtime compensation | Automatic rollback could not prove that the created instance was removed. Inspect the reported instance id, name, and storage path manually before retrying. |

Do not select `CreateOwned` merely to avoid choosing an already running runtime.
The project transfer target and the runtime used for control/communication must
refer to the same simulated PLC context.

## Communication And External Downloads

RunControl and EventBuffer access is valid only while the PLC is running and a
UnitTest communication session is active. An external TIA Portal download can
make the Runtime API tag list temporarily stale.

`NotUpToDate` during RunControl/EventBuffer reads triggers same-session
revalidation. The worker discards the incomplete poll cycle, publishes
`RevalidationRequired`, and resumes only after a complete valid read cycle.
Communication, Event Stream Monitor, and Run Control remain gated while this is
in progress. Opening and canceling the TIA download dialog may be sufficient to
cause this transient state.

Software/hardware/IP change callbacks are diagnostic correlation evidence, not
the recovery boundary. If the configured revalidation timeout expires, the
communication session becomes terminal and must be re-established. A timeout
of zero disables that automatic terminal timeout but does not suppress runtime
shutdown, cancellation, or infrastructure failures.

## External Runtime Changes

- External PLC Stop closes UnitTest communication while preserving observation
  of the selected external runtime.
- External PLC Start allows communication to be established again after valid
  RunControl and EventBuffer reads.
- External Power Off closes communication and reports the observed runtime
  state; Power On/Run can recover through the normal runtime-state events.
- Closing PLCSIM Advanced or removing the selected instance invalidates the
  affected runtime context. Reconnect or end the application session after the
  external resource has been restored.

Command success only confirms that the Siemens API accepted the call. PLC and
UnitTest state changes remain independently observed; do not diagnose a delayed
state transition from command acceptance alone.

## Worker Log

The worker log family is:

```text
access-profile-<profile-id>-plcsim-advanced-v8-worker-<date>.log
```

Correlate worker process/session, runtime instance, communication session,
operation, Runtime API result, state-change events, revalidation boundaries, and
cleanup. Raw event logging is disabled by default and is effective only with
the access-profile `Debug` level. Use the opt-in real-runtime tests documented
in the V8 README for reproducible validation; default tests do not load Siemens
software.
