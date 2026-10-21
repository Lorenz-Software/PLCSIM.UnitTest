# Beckhoff TwinCAT 3 (4026) Access Profile

The `beckhoff-tc3-4026` profile connects PLCSIM.UnitTest to TwinCAT engineering,
existing local Usermode Runtime instances and the `tc3unittest` PLC framework.
It supplies the application's project, PLC control, UnitTest lifecycle and
event-stream services as one .NET 8 directory package.

## Start Here

- [Setup and WPF operation](docs/usage.md): prerequisites, target selection,
  external/no-download debugging, managed download and recovery.
- [CLI and CI execution](docs/ci-execution.md) with
  [complete configuration templates](docs/examples/README.md).
- [Configuration reference](docs/configuration.md): profile identity, runtime
  addressing and communication options.
- [Current qualification](docs/verification.md#current-qualification): tested
  packages, engineering/application workflows and remaining limits.

## Supported Workflows

| Workflow | Behavior |
| --- | --- |
| External project handling in WPF | Connect to an already loaded PLC program and run tests without implicit engineering download or system-configuration changes. |
| Application-managed engineering | Open a complete `.sln` project tree or retrieve a `.tszip` archive, select the PLC target, build and transfer through an owned XAE host. A standalone `.plcproj` is not a supported independent input. |
| Runtime access | Use an existing local Usermode instance. `UseExisting` is supported; `CreateOwned` is rejected rather than substituted. |
| UnitTest communication | Observe RunControl and consume the EventBuffer through ADS. Polling is the default; subscription is opt-in. |

CLI execution always uses application-managed project handling. Schema version 2
must explicitly select `runtime.provisioningMode: "UseExisting"`.
An omitted runtime id may use the profile-designated local standard Runtime;
a failed explicit selection never falls back to another target.

## Prerequisites And Limits

Install compatible TwinCAT 3.1 Build 4026 XAE and a local Usermode Runtime,
provide valid licenses and deploy a PLC program with the expected UnitTest
contract. [Usage prerequisites](docs/usage.md#prerequisites) and
[licensing notes](docs/licensing.md) describe the exact checks and limitations.

Runtime creation/deletion, automatic route creation, license activation,
kernel-mode qualification, Build 4024 and Safety authorization are outside this
profile's scope. Project version pinning and compatible engineering selection
remain with the project and installed TwinCAT environment; the profile does not
introduce an exact patch-version migration policy.

Managed engineering opens direct solutions in place. Supply an exclusive
workspace for CI; use external project handling for WPF debugging beside an XAE
session already using that PLC project. Independent engineering sessions can
otherwise conflict. Archives use an owned extraction workspace, not an automatic
copy of a direct solution.

The Runtime instance and its PLC application are different resources. Managed
transfer may replace the PLC application/configuration on the selected system,
but cleanup preserves the pre-existing Runtime and license configuration.
It releases owned engineering and temporary resources, not source projects or
unrelated XAE sessions.

## Distinct Control Levels

| Level | Boundary |
| --- | --- |
| TwinCAT system CONFIG/RUN | Managed configuration activation may transition/restart the explicitly selected system and affect several PLC projects. It is not a Windows reboot or blanket service restart. |
| Individual PLC STOP/RUN | Ordinary ADS control, not Reset Cold or Reset Origin. Variables and completed framework state can survive. |
| UnitTest Start/Abort/Reset | Framework commands, separate from PLC/system control. |
| EventBuffer Reset | Discards buffered event storage and changes its epoch; it is not lifecycle Reset or a general PLC-program repair. |

Command-write acceptance does not prove a PLC reaction. Known CONFIG retains
the selected target but makes PLC online commands unavailable. See
[runtime provisioning and recovery](docs/contracts/runtime-provisioning.md)
and the [consumer mapping](docs/contracts/tc3unittest-consumer-mapping.md)
for normalized state, coherent event intake and release semantics.

## Qualification

The Windows 2.0.0 distribution includes profile 1.0.0 with the late-bound COM
engineering implementation. Its release gate exercised actual CLI/WPF flows
on XAE 3.1.4026.27 and the existing Default Usermode Runtime. The reported
system-service version does not establish the Runtime patch version.
Earlier non-default-target and subscription checks have separate scopes.

The [verification guide](docs/verification.md) owns results, package provenance,
timeout evidence and qualification limits. These checks do not qualify every
4026 patch combination, arbitrary PLC misuse or indefinitely blocked COM calls.
[Performance guidance](docs/performance/README.md) separately characterizes the
measured workload; defaults are not capacity guarantees.

## Build And Test

Run from this profile directory:

```powershell
dotnet restore .\PLCSIM.UnitTest.Beckhoff.Profile.sln --locked-mode --source https://api.nuget.org/v3/index.json
dotnet test .\PLCSIM.UnitTest.Beckhoff.Profile.sln --no-restore
dotnet test .\PLCSIM.UnitTest.Beckhoff.Profile.sln -c Release --no-restore
```

Normal tests use temporary file fixtures, injected ADS/environment evidence and
a fake XAE backend. They verify the real package-loader boundary and Windows STA
host behavior without launching vendor software or accessing a PLC. They are
not native qualification.

## Implementation And Documentation

Engineering uses late-bound vendor COM through an owned XAE shell on a dedicated
STA thread; runtime access uses the reviewed ADS client library. The package
has internal project/runtime components, not separate binding plugins or profile
worker processes. Vendor objects and symbol layouts remain below the profile
boundary. See the
[implementation overview](docs/architecture/implementation/profile-composition.md)
for code owners, scheduling, resource lifetime and containment limits.

- [Registration and source inspection](docs/registration.md).
- [Project preparation and transfer contract](docs/contracts/project-deployment.md).
- [Operational logging](docs/logging.md).
- [Workload sizing](docs/performance/event-stream-workload-sizing.md).
- [Dependencies and redistribution](docs/dependencies.md).
- [Release packaging](docs/packaging.md).
- [PLC reference provenance](../../docs/references/tc3unittest/SOURCE.md).
- [Shared profile package](../../docs/contracts/access-profile-package-contract.md)
  and [runtime contract](../../docs/contracts/access-profile-runtime-contract.md).
