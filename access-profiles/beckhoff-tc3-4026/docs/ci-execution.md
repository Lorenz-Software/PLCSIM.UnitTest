# CLI and CI Execution

## Provisioning

Use a Windows runner with TwinCAT 3 build 4026 XAE and an existing licensed local
Usermode Runtime. Run registration and jobs under the same Windows account.
There is no automatic trial-license activation or per-job Runtime creation.
Keep exclusive access to the selected system while activating configuration,
downloading and executing tests. Do not run parallel XAE downloads or another
EventBuffer consumer against that system.

Use a disposable checkout for direct `.sln` input: XAE opens it in place and may
save the selected target in the project. A `.tszip` source is imported into a
separate workspace. See [configuration](configuration.md) for both formats.

## Runtime Selection

Use schema version 2 with `runtime.provisioningMode: "UseExisting"`. Supply
`accessProfile.values.runtimeRef.id` to select an existing local Runtime
explicitly. The CLI retains that selection and fails if it cannot be uniquely
resolved; no alternate target is substituted. Without an id, the profile
designates the local standard Usermode Runtime. If it is unavailable, the job
fails. Cleanup preserves the existing Runtime and its licenses.

Omitting the mode still means `CreateOwned`, which this profile does not
support. An address alone does not change the mode. Migrate legacy version-1
jobs to version 2 with explicit `UseExisting`. Do not run a job against a system
reserved for other work.

The PLC application port is derived from the selected project, not necessarily
851. An explicit conflicting `runtimeRef.plcPort` is rejected. Provide `plcRef.id`
using the stable ID returned by project discovery; the CLI requires this selection
even when only one project target exists. Do not substitute the display name.

## Commands

After installing or updating the profile, register it under the runner account:

```powershell
& $cli register-profiles
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

`$cli` denotes the deployed `PLCSIM.UnitTest.App.Cli.exe`. Registration is a
provisioning action, not a required step before every job. Missing or stale
registration causes preflight to fail rather than silently refreshing it.

For each job, adapt one of the complete [configuration examples](examples/README.md).
They cover a direct solution with an explicit Runtime and an archive using the
local default Runtime. Replace paths and discovery identities before execution.
Use a unique artifact directory per job; do not reuse a previous job's JUnit
or analysis output as evidence for the current job.

```powershell
$jobResults = Join-Path .\artifacts ([Guid]::NewGuid().ToString('N'))
$junit = Join-Path $jobResults 'junit.xml'
$analysis = Join-Path $jobResults 'analysis'
& $cli check-config --file .\unittest.json
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
New-Item -ItemType Directory -Path $jobResults -Force | Out-Null
& $cli run --file .\unittest.json --output $junit `
    --analysis-output $analysis
$runExitCode = $LASTEXITCODE
# Collect only this invocation's $jobResults, also on failure; preserve the CLI result.
exit $runExitCode
```

`check-config` checks the common file shape and profile registration. It does not
validate all profile-specific fields or establish online license/Runtime readiness.
Build, activation, download and execution errors are evaluated during `run`.
Success returns 0; failed test evidence returns 1. Other nonzero codes identify
configuration, infrastructure, timeout, cancellation or artifact failures under
the common CLI contract. Do not replace the exit code by parsing console text.

### Startup Readiness

The managed CLI path builds/downloads and starts the PLC when required, then
waits for observed UnitTest Idle/Ready. It does not send an unconditional startup
Reset. `--unittest-ready-timeout <seconds>` overrides the initial 10-second
readiness deadline; it is a CLI option, not a TwinCAT profile field or system
restart timeout. A simple PLC STOP/RUN can retain a completed framework state.

`--reset-on-startup-timeout` explicitly permits one Reset after that initial
deadline when the command is allowed and no initialization Error/ConfigError
has been observed. The CLI then requires Idle/Ready, not merely a reaction to
the command; the post-reset wait uses `execution.timeouts.unitTestCommandReaction`.
If readiness still fails, the job fails. This option is not a default repair for
a blocked startup/reset fixture and does not authorize resetting an unstable run.

### Failure Artifacts

`--analysis-output` enables a best-effort analysis ZIP on failure by default;
add `--analysis-on always` to request one for successful runs too. It exports
one focused analysis: Run when an epoch exists, otherwise PLC Lifecycle for a
requested lifecycle command, otherwise Communication Health. A failure before
trace availability may have no package. The report includes the CLI outcome;
PLC evidence can be healthy even when another part of the job failed.

Analysis uses the existing bounded trace, without extra PLC reads or requiring
raw logging. It is captured before cleanup; later cleanup failure can trigger
export but is not part of that captured trace. Preserve console output and the
CLI exit code even if an analysis package or JUnit file is unavailable. Configure
the CI system to upload artifacts on failed jobs as well. The shared
[CI examples](../../../src/PLCSIM.UnitTest.App.Cli/examples/ci/README.md) describe
pipeline integration; serialize TwinCAT jobs by target system, not merely by PLC
port, because configuration activation affects the containing system.

When needed, retain a reviewed excerpt of
`%LOCALAPPDATA%\PLCSIM.UnitTest\logs`. Use `--debug` with
`--raw-event-logging` only for targeted investigations; logs may contain source
paths and PLC data. Do not publish unrelated shared log history.

## Verification Boundary

The distribution 2.0.0 release gate exercised the exact candidate's unmodified
CLI against the existing Default Usermode Runtime on installed XAE 3.1.4026.27:
archive-based managed execution passed 193 cases with JUnit and cleanup. Invalid
targets were rejected before transfer/PLC Start; a real assertion failure returned
exit 1 and one JUnit failure. No target-selection/provisioning substitution was
used to claim an actual CLI result.

Earlier non-default 4026.26 tests passed the 122-case SelfTests workload; they
retain their separate runtime/engineering scope. Neither set establishes
unattended Windows-service operation or arbitrary runtime/version support.
Actual WPF execution and installed registration/startup checks are separate
scopes in the [qualification record](verification.md#current-qualification).
