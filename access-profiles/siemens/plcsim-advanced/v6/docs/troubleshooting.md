# PLCSIM Advanced V6 Troubleshooting

This document covers installed-environment and operational failures owned by
the Siemens PLCSIM Advanced V6 binding.

## Prerequisites And Registration

- PLCSIM Advanced V6 is installed and licensed locally.
- The deployed package contains the V6 facade, production worker, manifest, and
  production dependencies.
- The installed Runtime API registry path is readable and contains `API\6.0`.
- Siemens Runtime API files come from the installed product and are not copied
  into the application or binding package.

After deploying or replacing binding packages, refresh profile registrations.
The registered capabilities must report PLCSIM Advanced 6.0 as `Usable` on a
native V6 installation. If V6 and V8 packages are deployed together on V8, V6
is expected to be `Unavailable` and V8 `Usable`.

The profile log must identify the selected binding as:

```text
PlcSimBinding=plcsim-advanced/6.0.0
```

The worker log must show the V6 worker executable. A V7 or V8 worker indicates
that registration evidence selected another installed product version.

## Product Evidence Mismatch

Native V6 and the compatibility API delivered by V8 both use `API\6.0` and
interface version `0x00060000`. The Runtime API DLL product version distinguishes
them:

| Installed API evidence | Binding result |
| --- | --- |
| Product version `6.0.0.0` | Native V6 candidate is usable. |
| Product version `6.0.0.1` | V8-delivered API 6 compatibility surface; native V6 candidate is unavailable. |
| Missing, unreadable, or unloadable DLL | Environment is unavailable or failed according to the concrete evidence failure. |

Do not repair a mismatch by copying a DLL from another PLCSIM installation.
Facade, worker, and installed Runtime API must remain a coherent environment.

## Runtime And Communication

`UseExisting` expects a registered instance with a downloaded PLC program.
An empty instance returns `IsEmpty` when Run is requested. `CreateOwned` creates
and powers a temporary instance and removes it only when destructive release is
requested by the caller.

RunControl and EventBuffer access requires a running PLC and an active UnitTest
communication session. `NotUpToDate` reads trigger same-session TagList
revalidation. The worker discards incomplete poll cycles and restores healthy
communication only after RunControl and EventBuffer reads are both valid.

External Stop, Power Off, Runtime Manager loss, or instance removal are reported
through observed runtime state and health. Command acceptance is not used as a
synthetic state transition. Reconnect reuses the selected worker/resource where
the runtime identity remains valid; otherwise end the session and establish a
new runtime context.

## Logs

Application and profile logs are below the application-owned log directory. The
V6 worker log family is:

```text
access-profile-<profile-id>-plcsim-advanced-v6-worker-<date>.log
```

Use the profile log to verify package selection and the worker log to correlate
process/session startup, runtime identity, API operations, state events,
revalidation, communication, and cleanup. Standard error is fallback crash
evidence, not the normal logging or IPC channel.
