# CI Starter Configurations

These files are adaptable starting points, not supported production pipeline
integrations. They cover GitLab CI, Jenkins, GitHub Actions, Azure DevOps
Pipelines, and TeamCity.

Every example assumes a self-managed Windows agent on which the application,
required products, access-profile packages, bindings, permissions, and vendor
authorization have already been provisioned.

## Provision A Windows Agent

Provisioning is separate from an individual UnitTest job:

1. Install a fixed PLCSIM.UnitTest distribution and the required profile and
   binding components. The default CLI path is
   `C:\Program Files\PLCSIM.UnitTest\PLCSIM.UnitTest.App.Cli.exe`.
2. Install and license the required vendor products and configure permissions
   according to the selected profile's deployment documentation.
3. Complete any profile-owned vendor authorization. When authorization is tied
   to a worker binary, repeat it after replacing that binding.
4. Run `register-profiles` once as the same Windows user that runs the CI agent.
   Default registration writes below that user's `%LOCALAPPDATA%` and does not
   require UAC elevation. Silent Setup does not perform this per-user step.
5. Run `list-profiles` and `check-config` under the agent identity before making
   the agent available for jobs.

Repeat the relevant authorization and registration checks after application,
profile, or binding upgrades. Do not run `register-profiles` in every UnitTest
job. It changes persistent provisioning state and does not perform vendor
authorization.

## Execute Jobs

Use a dedicated self-managed Windows agent and allow only one runtime
job to use that agent at a time. The GitLab CI, GitHub Actions, Jenkins, and
TeamCity starters include project-level serialization. Configure the Azure
agent pool with one usable parallel slot or protect the selected agent through
an equivalent exclusive-resource mechanism.

The runner must execute the shown commands in PowerShell. The examples inspect
the registry, check the configuration, run the UnitTest, and publish JUnit only
when the CLI produced it. Exit code `1` means a completed run with failed test
evidence and can still have a valid JUnit file. Infrastructure failures may
produce no JUnit file. Keep the CLI exit code as the authoritative job result;
console text is not a machine contract.

`check-config` validates common JSON shape, registration currentness, and
registered profile selection. It does not load the profile or validate its
operational payload and target references. Those checks happen during `run`.
A successful preflight does not prove that the project, vendor products, or
runtime can be prepared.

### Configuration and startup

Use configuration schema `2` for new files. Set
`runtime.provisioningMode` explicitly to `UseExisting` when reusing a runtime;
omission defaults to `CreateOwned`, including in legacy schema-1 files. Schema
`1` cannot contain a runtime section. An explicit existing target must resolve
uniquely; invalid targets never fall back. Without an explicit target, only a
profile-designated default is eligible. Unsupported creation or project
operations fail rather than switching modes. Runtime reuse still performs the
CLI's application-managed project build and transfer. Profile-owned addresses,
creation parameters, project source kinds, and restrictions belong in the
selected package's `docs/configuration.md`.

The CLI waits for observed `Idle + Ready` before requesting UnitTest Start.
The initial readiness timeout is 10 seconds after communication and monitoring
are active. `--unittest-ready-timeout <seconds>` changes that deadline.
`--reset-on-startup-timeout` opts into one gated Reset attempt after timeout;
the starters leave it disabled. Explicit initialization/workflow errors are
not retried. Neither option resets the EventBuffer automatically. Timeout
normally exits with code `31`; cleanup failure takes precedence with code `50`.
These switches are CLI options, not JSON fields. See the application CLI
reference for command/reaction deadlines and full exit-code semantics.

### Isolate results before preflight

Each starter selects an output directory for the current build/job attempt and
publishes only that directory, including after a nonzero exit:

| Starter | File | Result directory identifier |
| --- | --- | --- |
| GitLab CI | [gitlab-ci.yml](gitlab-ci.yml) | `CI_JOB_ID` (a retry receives a new job id) |
| GitHub Actions | [github-actions.yml](github-actions.yml) | `github.run_id` and `github.run_attempt` |
| Jenkins | [Jenkinsfile](Jenkinsfile) | `BUILD_NUMBER` within the job workspace |
| Azure Pipelines | [azure-pipelines.yml](azure-pipelines.yml) | `Build.BuildId` and `System.JobAttempt` |
| TeamCity | [teamcity.kts](teamcity.kts) | `teamcity.build.id` within the checkout directory |

Choose these paths before `list-profiles` and `check-config`. A failed preflight
does not execute `run`, so it cannot remove a preceding JUnit file. The CLI also
never deletes earlier analysis packages. Do not broaden publication patterns to
all build directories or restore prior results into the current directory.
If adapting a starter to run several configurations or jobs in one build,
include their job/configuration identity too. Give each repeated CLI invocation
its own directory. Keep output paths and publication patterns aligned.

### Optional evidence artifacts

The starters enable `--analysis-output <directory>` on `run`. Its default
trigger is `failure`: a successful invocation creates no analysis ZIP. Add
`--analysis-on always` to investigate successful runs too, or remove
`--analysis-output` to disable analysis export. These are CLI options, not
configuration-file fields. Use a CLI distribution that supports these options;
check `run --help` when updating an existing agent.

Exactly one analysis is selected from the captured evidence:

| Evidence available | Export |
| --- | --- |
| RunEpoch, including an incomplete run | Run Analysis (`run.zip`) |
| No RunEpoch, but PLC Start, Reset or UnitTest Start was requested | PLC Lifecycle Analysis of the latest attempt (`lifecycle.zip`) |
| Earlier setup/communication evidence only | Communication Health Analysis (`communication-health.zip`) |
| No retained evidence/context for analysis | Explicit unavailability on stderr; no empty report |

Lifecycle selection does not require command acceptance or a PLC response.
Analysis is captured before cleanup and written afterwards. Cleanup failure
also qualifies for the default failure export, but the captured report does
not contain cleanup evidence; use the job log for that failure.

Each exported package has a unique path below the configured directory:

```text
artifacts/plcsim-unittest/<job-id>/analysis/analysis-<UTC timestamp>-<unique id>/run.zip
```

The filename instead becomes `lifecycle.zip` or `communication-health.zip`
according to scope. Inside are `analysis.md`, `events.ndjson`, and
`hierarchy.json` where applicable, captured consistently for the same analysis.
Start with the Markdown summary and keep the whole ZIP for a PLC-project
investigation. Do not parse console wording to locate it; collect the configured
directory recursively as the examples do, including when the CLI exits nonzero.

Use a clean or job-specific artifact directory, especially on reused agents.
The CLI never removes analysis packages from earlier invocations. Preflight
failure in a separate `check-config` step never reaches `run` and creates no
analysis. Missing ZIPs are also expected on success with the default trigger,
when evidence is unavailable, or when export fails. Missing JUnit and missing
analysis therefore do not mean the same thing.

Analysis is best effort and does not change the primary exit code or JUnit.
No extra PLC reads, commands, or raw-event logging are required. The in-memory
trace is bounded, so older evidence may be missing and reports may be partial.
The ZIP write has a separate cooperative 10-second deadline even after run
cancellation. A forced process/agent termination or noncooperative hang cannot
guarantee an artifact.

PLC payloads, names, paths, assertion values and diagnostics may be sensitive.
Restrict artifact access and configure an appropriate retention period in the
CI system. Reports are not credential/configuration dumps, but are not a
sanitized public log either. Review them before sharing externally.

### Logs and cleanup

Successful CLI output preparation removes an old output file before workflow
setup. Early cancellation, preparation failure, or failed separate preflight can
leave an old file untouched. Collect only the fresh invocation-specific result
directory; file existence in a reused directory does not establish provenance.
CI systems already retain the command output. For a
failed infrastructure investigation, logs below
`%LOCALAPPDATA%\PLCSIM.UnitTest\logs` may additionally be copied into a
restricted, short-lived artifact. Do not publish the complete shared log
directory from a multi-purpose account without reviewing unrelated content.

Prefer an immutable project source supported by the selected profile. A direct
engineering project may be changed or locked by external engineering work.
Keep the UnitTest configuration as a protected CI input when it contains a
credential. Do not print the configuration or pass credentials as command-line
arguments.

The CLI performs bounded coordinated cleanup. A forced process termination,
runner shutdown, or second cancellation can bypass that cleanup and may require
manual inspection of the agent before another job starts.

Adapt the CLI, configuration, and output paths plus runner labels, pools, and
agent capabilities before use. The selected profile and binding documentation
remains authoritative for product versions, authorization, configuration, and
real-runtime limitations.
