# PLCSIM.UnitTest CLI

The CLI is the unattended automation application for configured PLC UnitTest
runs. It uses the same shared workflow and deployed access-profile packages as
the WPF application, but it does not provide interactive target selection or
recovery dialogs.

## Before Running

Use the [Windows installation guide](../../docs/deployment/windows-installation.md)
for host runtimes, installation and per-user profile registration. Prepare the
vendor products and PLC project required by the selected profile, then follow
that profile's configuration and deployment documentation. A successful common
configuration check does not establish live target readiness.

For an installed CLI, after registration and configuration:

```powershell
.\PLCSIM.UnitTest.App.Cli.exe check-config --file .\unittest.json
```

Use the [CI starters](examples/ci/README.md) for Windows-agent provisioning and
invocation-specific result collection. For building this application from source,
see the [build foundation](../../docs/development/solution-and-build-foundation.md).

## Commands

```text
run -f|--file <configfile> -o|--output <junit-file>
    [--unittest-ready-timeout <seconds>] [--reset-on-startup-timeout]
    [--analysis-output <directory> [--analysis-on always|failure]]
check-config -f|--file <configfile>
list-profiles
register-profiles
help
```

Use `--help` or `<command> --help` for generated command help.

`register-profiles` is an explicit per-user provisioning operation. It discovers the
packages in the active profile directory, invokes profile registration, and
rebuilds the application profile registry. Run it after installing, removing,
or replacing profile or binding packages. It is not part of each UnitTest run.

`list-profiles` reads the persisted registry without loading profile
implementations or repairing registrations. `check-config` validates the common
configuration shape, checks that the persisted registry still matches the
deployed profile packages, and verifies that the selected profile is registered.
It does not load the profile, validate profile-specific runtime semantics, or
perform project or runtime setup. `run` performs the same registration preflight
before loading the profile and preparing resources. Neither command repairs a
missing, stale, or unreadable registration; run `register-profiles` explicitly.

## Full Run

```powershell
PLCSIM.UnitTest.App.Cli.exe run `
  --file C:\ci\plcsim-unittest.json `
  --output C:\ci\results\plcsim-unittest.xml
```

The unattended run workflow is intentionally deterministic:

1. Validate the configuration and registered profile selection.
2. Load and bind the selected access-profile session with application-managed
   project ownership.
3. Discover and resolve the configured project target and discover runtime
   capabilities.
4. Resolve configured runtime intent and target, then configure the shared workflow.
5. Prepare project access and create or access the selected runtime as requested.
6. Build and transfer the project.
7. Start the PLC when it is not already running and wait for the independently
   observed running state.
8. Wait for UnitTest communication and event-stream monitoring.
9. Wait for current `Idle + Ready` without sending a Reset. Event consumption
   remains active during PLC-owned startup. Apply optional recovery only as
   explicitly enabled below.
10. Start the UnitTest, wait for exportable terminal evidence, and write JUnit.
11. Run coordinated bounded cleanup, preserving existing resources for `UseExisting`
    or requesting destruction for `CreateOwned`, then unload the profile session.

The CLI resolves targets through configuration and profile discovery. It does
not prompt when a target is missing or ambiguous. The selected profile owns the
shape and meaning of `accessProfile.values`; see the profile package
documentation for required project, PLC, runtime, and communication values.

### Startup readiness

`--unittest-ready-timeout <seconds>` sets the initial readiness deadline, starting
after UnitTest communication and event-stream monitoring are active. The default
is **10 seconds**. Use positive whole seconds (maximum 4294967). This is a
CLI-only option, not a JSON configuration field or a shared Core monitor.

The CLI waits for the current communication context to report RunControl
`Idle + Ready`, with execution status `Idle` and `Running = false`. Missing
observations, PLC-side Reset in progress and an already-completed previous run
do not cause an immediate command. Timeout normally exits with `TimedOut` (31),
reports the last observed state and performs bounded cleanup without starting
the UnitTest. Explicit RunControl `ConfigError`/`Error` or workflow failure aborts
without a recovery attempt; a prior test result `Failed` alone is not such an
initialization error.

`--reset-on-startup-timeout` explicitly enables **one** recovery Reset after the
initial deadline. It is disabled by default. The CLI rechecks readiness and uses
the existing command gate before requesting Reset. Command acceptance remains
bounded by `execution.timeouts.unitTestCommand`; subsequent `Idle + Ready` must
arrive within `execution.timeouts.unitTestCommandReaction` (default 10 seconds).
Merely entering the Reset state does not satisfy that condition. Failure aborts;
there is no retry loop or automatic EventBuffer reset.

A timeout does **not** establish that the PLC is stable or that resetting it is
safe. Enable recovery only deliberately; it cannot guarantee repair of a PLC
startup/reset defect. Preceding diagnostics remain visible. Cancellation stops
the wait and follows the existing cleanup path. These options do not change WPF
behavior or profile monitoring.

### Optional evidence analysis

`--analysis-output <directory>` enables one analysis ZIP per qualifying CLI
invocation. `--analysis-on failure` is the default when export is enabled;
`always` also exports successful runs. Without an output directory, no analysis
is generated. Reporting errors are written to stderr and do not change the
primary exit code, test result, JUnit artifact, or resource cleanup.
The failure trigger includes failed tests, execution errors, timeout,
cancellation and cleanup failure. A separate `check-config` invocation does
not export analysis.

An observed RunEpoch selects Run Analysis, including an incomplete run. Before
an epoch exists, a requested UnitTest Start, Reset or PLC Start selects PLC
Lifecycle Analysis of the latest attempt. Selection does not require successful
command acceptance or a PLC reaction. Otherwise, available evidence selects
Communication Health Analysis. A failure before any trace exists is explicitly
reported as unavailable, without creating an empty package.

For an execution error, timeout or cancellation, the report also includes the
CLI termination reason and CLI diagnostics available at capture time. This is
identified separately from the evidence-only assessment: for example, an
Idle/Ready deadline expiring does not itself prove a PLC framework fault. This
addition changes neither raw activity evidence nor the selected analysis window.

The selected package is captured before session cleanup and written afterwards.
It does not include cleanup evidence. Each invocation writes to a unique
`analysis-<UTC timestamp>-<unique id>` subdirectory containing `run.zip`,
`lifecycle.zip`, or `communication-health.zip`. The ZIP uses the same documents
as WPF exports: `analysis.md`, `events.ndjson`, and `hierarchy.json` when
applicable. The CLI prints its path. Existing invocation artifacts are not
overwritten.
Use a clean or job-specific output directory when collecting artifacts:
previous analysis packages are not deleted. Cleanup failure can trigger export
of the preceding run's evidence, but its cause must be investigated in the
job/application log because capture preceded cleanup.

The trace is bounded; retained or partial evidence is not a complete recording
of the PLC. Export requires no additional PLC reads or commands and no raw-event
logging. After cancellation, writing uses an independent 10-second cancellation
deadline; filesystem calls must cooperate with cancellation. A forcibly killed
or permanently hung process cannot guarantee an artifact. Capture/formatting
uses the bounded in-memory trace. Reports can contain PLC payloads and
diagnostic details; treat them as potentially sensitive CI artifacts.

### Safety-program authorization

When the selected profile reports `PasswordRequired`, unattended execution
reads the optional credential from the common project section:

```json
{
  "project": {
    "source": "sample-project",
    "authorization": {
      "safetyPassword": "configured-ci-secret"
    }
  }
}
```

The run fails before build or transfer when a required password is missing or
when requirement discovery returns `NotSupported`. Profiles that report
`NotRequired` do not use the configured value. The CLI never accepts the
password as a command-line argument and does not include it in summaries,
output, diagnostics, evidence, or exception text. Protect the configuration
file as a secret-bearing CI input and avoid printing its contents in the job
log.

The `--output` path is required and is the authoritative JUnit target for
`run`. Its parent directory is created when possible. An existing file is
deleted during successful output preparation before workflow setup. Early
cancellation or failed output preparation can leave an existing file untouched;
file existence alone does not identify the producing invocation.
The artifact is written atomically after a complete exportable run exists.

## Configuration And Packages

Common configuration authoring is described in the packaged WPF offline help
under **Help > Help Contents > Configuration authoring**; the repository source
is [`Help/configuration.html`](../PLCSIM.UnitTest.App.Wpf/Help/configuration.html).
The normative shared JSON format is defined in
[`configuration-file-contract.md`](../../docs/contracts/configuration-file-contract.md).
Profile-specific fields are documented in `docs/configuration.md` inside the
selected deployed access-profile package.

The CLI always supplies application-managed project ownership. Runtime intent is
configured independently in schema version 2:

```json
"runtime": { "provisioningMode": "UseExisting" }
```

Target addresses stay in `accessProfile.values`. An explicit target must resolve
uniquely; an invalid target never falls back. If no target is configured, only a
profile-designated default can supply it, not an arbitrary discovery candidate.
Omitted mode defaults to `CreateOwned`, including for legacy version-1 files.
An existing-target selection with `CreateOwned` is rejected instead of cleared;
profile-specific creation parameters remain valid where supported. No additional
CLI flag or vendor-specific invocation is required. `check-config` validates the
file and registration; operational target resolution occurs during `run`.

If the profile cannot perform the configured creation, project build/transfer,
or required authorization operation, the CLI stops with the relevant diagnostic;
it does not switch to a different provisioning or project-handling mode.
`UseExisting` controls runtime provisioning and cleanup only. It still runs the
application-managed project workflow and may change the program on that target.

### Compatibility and migration

| Version boundary | Meaning |
| --- | --- |
| Configuration JSON `version` | Schema `2` supports explicit runtime intent. Legacy `1` remains readable with implicit `CreateOwned` and cannot contain `runtime`. |
| Profile API `profile.contractVersion` | The application accepts exactly `2.0.0`. Packages declaring API `1.0.0` are rejected before loading. |
| Profile `accessProfile.version` | Optional exact component version, independent of the JSON schema and API version. Omission selects the highest available profile version. |
| Application, binding, and distribution package versions | Independently released component identities pinned by the distribution. A shared version number does not establish API compatibility. |
| PLC communication contract | Owned by the selected PLC framework and mapped by its profile; application package upgrades do not automatically change it. |

When migrating, install a compatible profile/binding composition and explicitly
run `register-profiles` under the execution account. For an existing runtime,
set JSON `version` to `2` and `runtime.provisioningMode` to `UseExisting`, then
adjust profile-owned values using that installed profile's configuration guide.
`RequireExisting` is no longer an accepted provisioning name; use `UseExisting`.
Keeping a legacy version-1 file does not preserve reuse intent inferred from an
address. Do not copy a profile API version into `accessProfile.version`: that
field selects a component package, not a contract.

Profile packages are loaded from `profiles` next to the CLI executable by
default. Deployments may override the locations with:

- `PLCSIM_UNITTEST_PROFILE_DISCOVERY_DIRECTORY`
- `PLCSIM_UNITTEST_PROFILE_REGISTRY_PATH`

The CLI does not reference concrete profiles or technology bindings. Required
products, package layout, permissions, vendor authorization, and real-runtime
constraints belong to the selected profile and binding documentation below the
access-profile subtree.

## Deployment And Registration

Deploy each access-profile package as a self-contained directory below the
active profile discovery directory. A default installation therefore has this
shape:

```text
<cli-directory>/
  PLCSIM.UnitTest.App.Cli.exe
  profiles/
    <profile-package>/
      manifest.json
      docs/configuration.md
      ...
```

Technology-binding packages remain inside the owning profile package according
to that profile's package documentation. The CLI application must not acquire
compile-time references to those packages.

The discovery tree is immutable deployment input. The CLI does not create a
missing discovery directory and does not write registration evidence or caches
into profile packages. When a profile is activated, Core supplies a private
writable directory below `%LOCALAPPDATA%\PLCSIM.UnitTest\profile-data`; the
profile owns derived state below that directory. A missing discovery directory
therefore yields no discovered profiles and a discovery diagnostic until a
package is deployed.

After changing deployed packages, rebuild and inspect the persisted registry
from the same Windows account that will run the application:

```powershell
& .\PLCSIM.UnitTest.App.Cli.exe register-profiles
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& .\PLCSIM.UnitTest.App.Cli.exe list-profiles
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

Use `PLCSIM_UNITTEST_PROFILE_DISCOVERY_DIRECTORY` and
`PLCSIM_UNITTEST_PROFILE_REGISTRY_PATH` together when a deployment keeps
packages and registry outside their defaults. Registration uses the configured
package root and writes the configured registry; later commands read that same
registry. Product-specific setup may require an elevated or otherwise prepared
agent and is documented by the selected profile.

Default profile registration does not require UAC elevation, but it is a
provisioning action rather than a per-run action. Provision and register the
agent before assigning CI work to it. Perform any separate vendor authorization
required by a binding during that provisioning. CI jobs may use `list-profiles`
as a read-only sanity check of the persisted state.

## CI Usage

A pipeline should treat package deployment, vendor authorization, and
`register-profiles` as external agent provisioning. Per-test jobs can then
inspect the persisted registry, perform a lightweight preflight, and execute
the real run:

```powershell
$cli = "C:\Program Files\PLCSIM.UnitTest\PLCSIM.UnitTest.App.Cli.exe"
$config = "C:\ci\plcsim-unittest.json"
$jobResults = Join-Path "C:\ci\results" ([Guid]::NewGuid().ToString("N"))
$junit = Join-Path $jobResults "plcsim-unittest.xml"
$analysis = Join-Path $jobResults "analysis"

& $cli list-profiles
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $cli check-config --file $config
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $cli run --file $config --output $junit --analysis-output $analysis
$runExitCode = $LASTEXITCODE

# Collect $junit and ZIPs below $analysis when present, also on nonzero exit.
# Publish only this invocation's $jobResults; analysis defaults to failure only.
exit $runExitCode
```

Both `check-config` and `run` stop with exit code `20` when registration
currentness cannot be confirmed. This includes missing profile packages, stale
package fingerprints, an unavailable registry, and failures of the currentness
check. The commands never invoke vendor registration as an implicit recovery.

Successful output preparation deletes an existing output file before workflow
setup. Early cancellation, failed output preparation, or a separate failed
preflight can leave earlier artifacts untouched. In a fresh invocation-specific
directory, publish the JUnit path when it exists, including for exit code `1`,
and use the process exit code as the authoritative job outcome. File existence
in a reused directory is not proof of current-invocation provenance.
Select the job-specific collection directory before preflight and
never publish a glob spanning earlier jobs. If a job invokes the CLI more than
once, give each invocation its own result directory.

CI integrations may capture stdout and stderr for diagnostics, but must not
parse their wording as a machine contract. Warnings and errors use stderr;
workflow progress and lower-severity diagnostics use stdout.

Adaptable starter configurations for common CI systems are available under
[`examples/ci`](examples/ci/README.md). They deliberately omit profile
registration and assume a pre-provisioned Windows agent.

## Logging

Logs are written below:

```text
%LOCALAPPDATA%\PLCSIM.UnitTest\logs
```

The application log is `cli-<date>.log`. Selected profiles and workers may
write their own correlated files in the same directory according to their
logging policy.

- default: Information
- `--verbose`: Trace
- `--debug`: Debug and wins over `--verbose`
- `--raw-event-logging`: enables raw event logging only with `--debug`;
  otherwise the option is ignored with a warning

During `run`, stdout reports each long-running workflow phase, including
configuration validation, profile loading, discovery,
project/runtime preparation, transfer, PLC and UnitTest execution, artifact
export, and cleanup. Diagnostics carry an explicit severity label. Warnings and
errors are written to stderr; informational, debug, and trace diagnostics are
written to stdout.

Console output is human-facing. Automation must use the exit code and JUnit
artifact instead of parsing console messages.

## Cancellation And Cleanup

The first `Ctrl+C` requests cooperative cancellation. The active operation is
allowed to reach its defined cancellation boundary, and coordinated cleanup
still runs without the caller cancellation token. A second `Ctrl+C` terminates
the process immediately and therefore cannot guarantee cleanup.

Normal completion, test failure, infrastructure failure, timeout, and
cancellation all enter coordinated release. If cleanup itself fails, the cleanup
outcome takes precedence over the earlier run outcome.

## Exit Codes

| Code | Meaning |
| ---: | --- |
| 0 | Success. |
| 1 | UnitTest execution completed with failed test evidence. |
| 10 | Invalid command-line arguments. |
| 11 | Invalid configuration. |
| 20 | Access-profile registration cannot be confirmed as current, or the selected profile is unavailable. |
| 21 | Required profile environment or capability is unavailable. |
| 30 | Project, runtime, communication, or execution failure. |
| 31 | Operation or observation timed out. |
| 32 | Operation was cancelled. |
| 40 | Required JUnit artifact could not be prepared or written. |
| 50 | Coordinated cleanup failed. |
| 99 | Unexpected application error. |

A successful real-runtime validation requires installed and usable products,
registered profile/binding packages, valid project target references, required
permissions, and any vendor authorization required by the selected binding.
Normal unit tests and Fake-profile tests do not prove those environmental
preconditions.
