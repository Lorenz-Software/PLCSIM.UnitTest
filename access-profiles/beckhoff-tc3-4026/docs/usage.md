# Using the TwinCAT Profile

## Prerequisites

- Windows x64 with TwinCAT 3 build 4026 XAE, its registered XAE Shell, the
  shell's installed `Microsoft.VisualStudio.Interop.dll` and local Usermode
  Runtime components. The distribution 2.0.0 CLI/WPF release gate used installed
  XAE 3.1.4026.27 and the existing local Default Usermode Runtime. Earlier
  4026.26 checks retain their distinct scope. This does not qualify every patch
  combination, 4024,
  kernel-mode or remote systems. See [verification](verification.md).
- A compatible PLCSIM.UnitTest host and the `beckhoff-tc3-4026` profile package.
  Profile 1.0.0 requires shared access-profile contract 2.0.0. The host's .NET
  prerequisites still apply; installing the profile does not install TwinCAT.
- An existing local Usermode Runtime with usable project licenses. Activate or
  renew them in XAE. No Runtime is created and no trial is activated by the
  application. See [licensing](licensing.md) for unattended CI limitations.
- A `tc3unittest` program exposing communication contract 1.0.0. Default symbols
  are `GVL_UnitTest.RunControl` and `GVL_UnitTest.EventStream`; change the profile
  configuration if the project publishes them elsewhere.

Register installed profiles under the Windows account running WPF or CLI. In
WPF use **Register now** when registration is missing/stale; for CI use
`PLCSIM.UnitTest.App.Cli.exe register-profiles` during runner provisioning.
Refresh registration after profile or TwinCAT installation changes. Registration
checks installed prerequisites, not online availability or current licenses.
Beckhoff engineering uses COM without a `TCatSysManagerLib.dll` prerequisite.
Project/vendor-compatible selection within 4026 is retained; after opening,
the actual engineering version is checked rather than inferred from registration.

## Select the Target

The AMS Net ID identifies the TwinCAT system; the PLC ADS port identifies a PLC
application in that system. Port 852 is not an alternate route to port 851.
Use the actual Net ID, not a value inferred from the machine's IP address.

Discovery reads local Usermode configurations below
`%ProgramData%\Beckhoff\TwinCAT\3.1\Runtimes\<instance>\3.1`.
The internal standard instance `UmRT_Default` is displayed as **Local Default
Runtime**. This is a lightweight local inventory, not a network scan or a list
of all ADS routes. An explicit Net ID can address a local instance outside that
inventory; preparation still verifies its online identity against its local
configuration. Remote targets and router aliases are not supported substitutes.
ADS routing must already allow access; the profile does not create routes.

For configuration files explicitly select `runtime.provisioningMode:
"UseExisting"`. With `runtimeRef.id`, exactly that target must resolve. Without
it, the local standard Usermode Runtime is selected, not the first discovered
instance. Missing defaults or invalid explicit targets fail without fallback.
Omitting the provisioning mode means `CreateOwned`, which is not supported.

## WPF Without Download

1. Select the Beckhoff profile and external project handling.
2. Select the existing Runtime and set its PLC port. The default is 851; choose
   the actual port if the application is loaded elsewhere.
3. Prepare Runtime access. An existing stopped PLC is valid. If the system is
   in RUN but the port is missing, load the application in XAE or correct the
   port, then retry. CONFIG may leave setup prepared but PLC controls unavailable.
4. Start the PLC if stopped. Wait for communication and UnitTest Idle/Ready,
   then start the test run.
5. For another run, use UnitTest Reset after a stable completion and wait for
   Idle/Ready. End Session releases access without deleting the Runtime.

This path does not open XAE, download, activate configuration or switch the
system to RUN. It is the intended debugging path alongside your own XAE.
Do not run another EventBuffer consumer against the same PLC application.

## WPF With Download

Use **Load UnitTest** with an adapted complete [configuration example](examples/README.md),
or select managed project handling and the project, PLC and existing Runtime
interactively. Supply a complete `.sln` project tree or a `.tszip` archive.
The PLC port comes from the selected project; a conflicting explicit port fails.

The profile opens a hidden, application-owned XAE, builds the selected system,
activates its configuration, waits for confirmed system restart readiness and
downloads the selected PLC application. Configuration activation can affect
other PLC applications on that same system. Use an exclusively available target.
Start the PLC when setup is ready; do not assume configuration activation means
the selected application is running or UnitTest is ready.

Direct solutions are opened in place and XAE may persist the selected target.
Use an exclusive workspace, not a project simultaneously edited in another XAE.
Archives use an owned extraction directory which is cleaned up after use.
The profile does not call boot-project generation or enable boot autostart.
It does not provide a separate native activation-copy suppression control.
Current online compile-info and persistent-data backups can change during
download; preservation of the entire Boot directory or persistent state is not
promised. A boot application already installed by another tool can start during
a system restart; account for that when preparing the target.

## Lifecycle and Recovery

| Action/state | Meaning and appropriate response |
| --- | --- |
| PLC Stop / Start | Ordinary ADS STOP/RUN, not Reset Cold or Reset Origin. Variables and the completed UnitTest state can survive. Reset the framework normally before another run if it is not Idle/Ready. |
| UnitTest Reset | Framework command, separate from PLC/system restart. Command acceptance is not completion; wait for observed Idle/Ready. Do not reset an unsettled run or an Abort still in progress. |
| System CONFIG or connection loss | PLC access becomes unavailable. System observation continues; the profile reconnects PLC access when system RUN and the application endpoint are confirmed. It does not download a missing application automatically in external mode. |
| Reconnect Runtime | Rebuild/revalidate communication access to the selected target. No implicit system restart, PLC reset, application download or license activation. |
| EventBuffer Reset | Destructive evidence/epoch reset, not a repair for a blocked PLC test fixture. Use only with a stable test state and an explicit intention to discard buffer evidence; wait for the clean observation. |
| End Session | Release owned access and engineering resources. Preserve the existing Runtime and licenses; do not infer that the PLC was stopped or its state restored. |

## Troubleshooting

| Symptom | Check or next action |
| --- | --- |
| Profile missing or unavailable | Refresh registration; inspect the prerequisite diagnostic and [registration details](registration.md). Check the installed XAE/Usermode components and runner account. |
| Runtime not listed | Check the local inventory location and actual Net ID. Use an explicit local target if appropriate; do not create a new Runtime merely to recover discovery. |
| `TargetPortNotFound` on initial external preparation | Check Net ID, port and whether an application is loaded. A system in RUN can have no PLC application. Managed first download is a different path. |
| ADS warnings during managed restart | Temporary system-port loss followed by recovery can be expected. Judge completion from setup/readiness and the final result, not a single transient warning. Persistent failure requires the profile log. |
| `NotSupported` during provisioning | Use explicit `UseExisting`; this profile does not implement `CreateOwned`. A target address alone does not select the mode. |
| Build/download rejected | Inspect the operation diagnostic and verify the project configuration/platform, compatible installation, licenses and required Safety operations in XAE. Compiler output is optional Debug context, not guaranteed structured diagnostics. |
| Reset timeout or missing Idle/Ready | Export PLC Lifecycle Analysis and inspect the PLC fixture. Do not repeat resets while unstable or assume reconnect repairs PLC state. CLI readiness failure is not an instruction to force a reset. |
| Run failed or evidence incomplete | Use Run Analysis. Preserve assertion details and distinguish PLC failures from missing/lost events. Incomplete evidence is not a healthy completed run. |
| Backlog, loss or degraded EventBuffer | Use Communication Health Analysis and the [workload-sizing guide](performance/event-stream-workload-sizing.md). Reduce burst concurrency or tune the measured interval/batch tradeoff before treating a buffer reset as a solution. |

The profile uses the host's log directory, not a separate worker log. See
[logging](logging.md) for file names, levels and the cost of raw logging. Analysis
exports are bounded observations, not a complete lifetime recording or a proof
of PLC causality. Keep PC and PLC timestamps separate when interpreting them.

For automation see [CLI and CI execution](ci-execution.md); for detailed fields
see [configuration](configuration.md). Keep the default polling 10 ms / drain
1 ms / batch 32 unless measurements justify a change. Opt-in subscription and
its measured tradeoffs are covered by the [performance guide](performance/README.md).
