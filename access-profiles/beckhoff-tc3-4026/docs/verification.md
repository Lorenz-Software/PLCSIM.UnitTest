# Package Verification

Verification has distinct scopes: deterministic tests, native service checks,
actual CLI/WPF execution, and installer checks. A successful package load does
not establish live access. The [implementation overview](architecture/implementation/profile-composition.md)
maps code and resource owners; the profile contracts remain normative.

## Current Qualification

Windows distribution 2.0.0 includes profile 1.0.0 with ADS 7.0.389 and the
late-bound Beckhoff COM engineering implementation. The release gate exercised
installed XAE 3.1.4026.27 and the existing local Default Usermode Runtime, PLC
port 851. The system-service device version reports 3.1.4026, not a Runtime
patch version; do not infer a Runtime patch matching XAE from that value.
The [profile manifest](../src/PLCSIM.UnitTest.AccessProfiles.Beckhoff/manifest.json)
and reviewed [bundle definitions](../../../distribution/bundles) identify package
composition; versions alone do not establish qualification.

The released Beckhoff component records source revision
`dea5306e4079437d7a9371078af905510990d608`; distribution composition is pinned
in `e0bcf4a6e64bb0d21e2bbdf5541f86e6b9b02b67`. These are provenance identifiers,
not a claim that subsequent source changes inherit the same qualification.

| Scope | Observed result and limit |
| --- | --- |
| Native CLI | The exact candidate's unmodified CLI completed archive-based managed execution with 193 passed cases, JUnit and cleanup. Invalid targets were rejected before transfer/PLC Start. A deliberately failing assertion produced one JUnit failure and exit 1; the normal program subsequently passed 193/193. |
| Native WPF | Managed and external/no-download paths completed 193-case runs and JUnit export. Managed Abort, settled Reset and recovery succeeded; repeated epochs did not duplicate exports. This does not certify every UI feature or arbitrary PLC misuse. |
| Offline release gate | 3138 tests in 19 suites passed without skips across the coordinated candidate; the final Siemens selection correction additionally passed 944 focused Siemens/CLI/Core regressions. These are repository-wide checks, not Beckhoff-only counts or additional native target scenarios. |
| Package identity | Integrity, exact contract hashes, dependency/notice checks and Windows runtime assets were checked. The release was published from clean component revisions; downloaded components reproduced the qualified distribution tree. |
| Installer | Actual full installation, Beckhoff-only reselection, same-version repair and upgrade from the published V1 installer were checked. Installed non-administrator WPF registration and CLI registration/configuration checks passed. These installed checks did not repeat the native engineering/run matrix. |
| Acceptance limits | User acceptance was bounded, not a claim of every manual scenario. Missing-.NET prerequisite rejection was not retested: prerequisites were installed, checks were unchanged, and no separate VM was available. Cross-account behavior and uninstall deletion of user data were not qualified by those checks. |

Release-gate application runs, feasibility probes, substituted CLI harnesses and
dirty-source component checks are different scopes. Only the former establish
the combined released CLI/WPF qualification described here. Repeat the relevant
checks on the selected source/package when qualifying a different version;
temporary raw outputs are not a retained public evidence package.

### Engineering Service Qualification

The packaged late-bound implementation was additionally checked through the
public host and profile services on XAE 3.1.4026.27:

- Direct solution and vendor archive preparation, explicit PLC selection,
  successful builds, genuine compiler failure and ambiguous-selection rejection.
- Selected-system build isolation and declared dependencies: independent valid
  system A succeeded; invalid B and A depending on invalid B failed.
- Normal transfer through verified target selection, configuration activation,
  restart readiness, forced/silent Login, Logoff and fresh ADS endpoint access.
- First application creation from ADS Invalid and replacement with different
  compiled content. Independent marker reads remained correct after engineering
  release; no manual runtime preparation or Login retry was required.
- Owned engineering contexts exited and archive working storage was removed;
  pre-existing user engineering remained open and fixture files could be read
  exclusively afterward.

Static analysis reported errors while the DTE compiler result reported zero
failed projects. Compiler success is not passing static analysis; the existing
[compiler-result boundary](contracts/project-deployment.md#build-and-authorization)
is unchanged.

The exercised preparation, discovery, build and transfer paths fit the existing
60/180/600-second operation budgets and 30-second readiness observations. This
is environment-specific timeout evidence, not an engineering benchmark or a
runtime performance-matrix update. Dispatched COM calls still require
cooperative completion; a permanently blocked vendor call is not qualified.

The transfer checks requested no PLC Start, explicit boot-project generation,
boot-autostart manipulation, license activation or route change. They did not
inspect or guard boot files and therefore establish no boot-file immutability
or persistent-state preservation guarantee.

## Additional Bounded Qualification

These retained scopes explain runtime and transport behavior beyond the release
gate. They are not a second current-release acceptance matrix.

### Runtime And Event Integrity

Earlier integrated ADS V7/.NET 8 service checks used a selected non-default local
4026.26 Usermode instance. They covered identity checks, PLC STOP/RUN,
missing/reappearing application ports, reconnect and short/long CONFIG phases
(including 2- and 45-second holds). A missing application remained unavailable;
some CONFIG fixtures required explicit application reload. This is not automatic
restoration or permission for external mode to download implicitly.

A one-case Smoke fixture completed 22 service/Core-workflow runs, including
one-event batches, settled explicit EventBuffer reset, STOP/RUN and reconnect.
Exports were healthy and complete with no event loss or duplicate run delivery.
This harness bound the production session to Core; it was not an actual CLI or
WPF invocation and did not qualify burst capacity or ring wrap under live load.

The expanded 122-case SelfTests workload passed full runs and three completed
Abort/Reset/follow-up sequences. Full runs contained 1563 unique contiguous
run-owned events with zero loss and complete evidence; Abort runs completed
with Abort evidence and no JUnit under the existing policy. A separate
unmodified schema-version-2 CLI run on the non-default instance passed all 122
cases. These earlier application results used the previous engineering binding;
they are not substituted for the current release-gate results above.

Deterministic full-ring tests drain 1020 events over four waves at batch limits
1, 32 and 256. Late child evidence is checked after a PLC suite timeout but
before final completion, not after a PC observation timeout. Missing Result
evidence is not repaired merely by eventual completion. Fault injection,
wrapping, partial reads, failed writes and missed reset-zero windows are
offline checks, not live ADS fault or capacity measurements.

### Subscription Qualification

Production polling+Sum and subscription+Sum were compared against the same
122-case SelfTests workload at monitoring/drain/batch settings 10 ms / 1 ms / 32.
Each series used one warm-up and ten measured runs. Including recovery probes
and the polling CONFIG control, all 37 runs completed with 1563 contiguous unique
run-owned events, complete/exportable evidence and zero loss or duplicate
delivery within a run. Native subscription callbacks resumed after recovery;
fallback reads alone were not counted as notification proof.
The [performance comparison](performance/README.md#polling-versus-subscription)
owns measurements and their limits; engineering changes do not requalify that
performance matrix.

These checks are not a diagnostic-free startup claim. During CONFIG, obsolete
access cleanup returned ADS MailBoxError with both strategies; clients were
disposed and access recovered. Both strategies also observed PLC startup
reset-timeout events before settled Reset and healthy follow-ups. The observed
system-access delay and PLC reset timeout do not establish causation.

A separate managed WPF check with the fixture's pre-existing boot application
and autostart removed by operator-approved fixture preparation completed with
PLC STOP, then explicit PLC Start, 122 passing tests, JUnit and no startup
reset-timeout diagnostics. This is a bounded fixture result, not a production
boot-file cleanup feature or a guarantee for arbitrary startup state.
Transient ADS restart and SDK unregister warnings remain visible.

## Remaining Boundaries

- Build 4024, kernel-mode runtimes, remote-system operation, arbitrary patch
  combinations, runtime creation/deletion, license activation and Safety are
  not established by these results.
- External Online Change and arbitrary vendor/network failure injection remain
  separate from managed-download qualification. Symbol-generation changes and
  isolated PLC-session loss have deterministic coverage.
- External EventBuffer reset followed immediately by production may hide the
  empty/zero window needed for Core epoch attribution. The transport must not
  fabricate that observation, discard captured events or rewrite sequence ids.
- Healthy live Abort runs do not qualify a mixed failed-child/parent-Abort PLC
  case. Shared result-precedence behavior has deterministic coverage.
- XAE can persist the selected TargetNetId into a direct project even when
  closing without saving. Direct managed sources must be exclusively owned
  workspaces; preservation of permanent development projects and concurrent
  editing are not qualified.
- Vendor license dialogs can prevent readiness and reject normal XAE cleanup.
  A successful rerun after operator license repair does not erase that failure.
  See [licensing diagnostics](licensing.md#status-and-diagnostics).
- Cancellation/timeout is not containment of permanently blocked COM work.
  No forced vendor-process termination is qualified.
- Default values are not capacity or guaranteed recovery-latency claims. Normal
  recovery uses actual terminal completion, fresh Idle/Ready and buffer drainage;
  elapsed quiet time alone does not authorize another command.

## Offline Coverage

Run the profile-local solution's tests in Debug and Release. The package tests
load the staged profile through the real host loader; the unit tests use local
file fixtures and injected installation evidence. Executing these tests requires
neither a PLC nor XAE activation. Building the profile requires the installed
engineering interop metadata described in [dependencies](dependencies.md#installed-engineering-interop).

The coverage includes:

- Manifest discovery, host-shared contract and logging compatibility, and an
  unconnected ADS client created/disposed inside the profile load context.
- Configuration defaults, omission versus explicit port, managed/external field
  projection, typed mutations, invalid boundaries and transactional import.
- Complete configuration roundtrip, preservation of unrelated values and no
  archive-workspace creation during configuration validation.
- Missing prerequisites, corrupt/stale profile registration, persistence errors,
  cancellation and exclusion of concurrent registration/session operations.
- Lightweight solution/archive inspection, malformed metadata and build-family
  boundaries without engineering activation.
- Managed project discovery/selection, stable system-plus-PLC identities, explicit
  port conflicts and context reuse; external mode never creates engineering access.
- Windows STA execution with a fake XAE backend, queue ordering, cancellation
  without abandoning dispatched work, cleanup retry and archive ownership.
- Selected-system builds with the unchanged solution configuration/platform,
  completion/failure-count evaluation, vendor errors and optional bounded Debug
  output that cannot change build outcomes or become application diagnostics.
- Standard PLC authorization, required unsupported Safety operations, changed
  target evidence, rejected stale contexts and build cancellation before release.
- Managed activation/download ordering, verified Runtime identity, selected PLC
  port retention after project release/reconnect, missing initial PLC application,
  login outcome, ADS state evidence, cancellation and owned logoff/cleanup failures.
- Failed session composition, subsequent reuse and complete disposal without
  disposing host-owned logging.
- Host registration persistence and capability validation, unavailable-profile
  exclusion from source selection, package immutability and missing entry DLL.
- Local Usermode configuration decoding, default versus explicit selection,
  conflicting identities, malformed XML/ADS identity data and router-alias rejection.
- Runtime reuse with both release intents, unsupported dedicated provisioning,
  managed omitted port, PLC commands and independent CONFIG/RUN observations.
- PLC notification wakeups, missing PLC ports, bounded early system checks,
  diagnostic deduplication, context-preserving reconnect and read cancellation
  before client disposal. No test creates a real ADS connection.
- Symbol-derived RunControl field layout, signed state/result normalization,
  independent BOOL flags and separate contract/framework version validation.
- Individual lifecycle writes, no command replay/readback, stale-context
  rejection, last-observation retention, read/write serialization, cancellation,
  revalidation, diagnostic deduplication and cleanup failure reporting.
- Router-registration exceptions, failed preparation callbacks and unexpected
  observer termination cannot leave an apparently usable RunControl context.
- EventBuffer symbol-derived layouts, array bounds/stride, alternate field
  positions/padding, malformed fields and independent contract-version validation.
- Unsigned FIFO/status counters, ASCII termination/limits, signed lifecycle and
  result values, integer Unix-nanosecond timestamps and unchanged event messages.
- Bounded FIFO prefixes, wrapping, full/empty consistency, N-1 capacity through
  the UINT range, incomplete range rejection and cancelled/failed symbol resolution.
- Reset write acceptance without readback, replay or synthetic empty evidence;
  full/loss transition diagnostics while valid consumption continues.
- EventBuffer revalidation after symbol/port errors, prolonged unavailability,
  incompatible replacement contracts and real reset observations with/without an
  intervening empty-zero window. The latter does not establish Core epoch detection.
- Public communication preparation, compensation on partial failure/cancellation,
  dependent-service cleanup before reconnect, fresh contexts after re-preparation
  and session disposal concurrent with preparation.
- Profile drain output through the real shared EventStreamProcessor: split final
  batches, minimal aggregation without optional runtime counters, late child
  assertions after parent failure, full/lost status, out-of-epoch reset evidence,
  pending deletion and characterization of reset with/without a visible zero window.
  The Core reference is test-only; production dependencies are unchanged.

Package immutability checks compare file names and SHA-256 hashes before and
after registration. This is test-only verification, not a profile security,
signing or encryption feature and not an additional production dependency.

## Local Deployment

The repository's generic `DeployPluginPackage` MSBuild target can stage this
independent package. From the repository root:

```powershell
$project = 'access-profiles/beckhoff-tc3-4026/src/PLCSIM.UnitTest.AccessProfiles.Beckhoff/PLCSIM.UnitTest.AccessProfiles.Beckhoff.csproj'
$destination = [IO.Path]::GetFullPath('.tmp/beckhoff-package-check/profiles')
dotnet msbuild $project /t:DeployPluginPackage /p:Configuration=Debug "/p:PluginDeploymentRoot=$destination" /p:PluginPackageSubDir=beckhoff-tc3-4026
```

Restore before deployment. Use a new isolated destination for verification; this
copy target does not remove obsolete output files. Preserve the complete package,
including dependency metadata and Windows runtime assets, not just the profile
and ADS DLLs. Do not synchronize or rebuild shared contract artifacts merely to
deploy this profile.

Both applications support `PLCSIM_UNITTEST_PROFILE_DISCOVERY_DIRECTORY` and
`PLCSIM_UNITTEST_PROFILE_REGISTRY_PATH` overrides. The latter isolates only the
application registry, not all profile data or logs. A fully isolated automated
host probe must also supply separate `AccessProfileDataOptions` and log paths.
The Siemens-specific composition in `tools/deploy-debug-plugins.ps1` does not deploy
this profile; using it is not a Beckhoff deployment check.

## Repeatable Native Checks

Use a prepared, licensed machine and explicit authorization for the exact source
workspace and runtime. Follow [usage](usage.md), [CLI/CI execution](ci-execution.md)
and the [configuration templates](examples/README.md). Use disposable managed
sources; do not attach to or close an unrelated user's engineering context.

1. Record the exact package/source identity, installed XAE version, runtime
   identity/available version evidence, PLC port, workload and starting state.
   Keep profile/application data and logs isolated from development sessions.
2. For managed access, exercise direct/archive preparation, multiple/invalid
   selection, compiler success/failure and transfer to the selected runtime.
   Observe actual restart readiness, first/replacement application content and
   effective port retention after engineering release/reconnect.
3. For external access, use an already loaded application without engineering
   activation/download. Check missing application, STOP/RUN, reconnect and
   authorized CONFIG/RUN recovery separately.
4. Verify complete runs, a genuine assertion failure, settled Abort/Reset
   recovery, JUnit policy, analysis completeness, event uniqueness/loss and
   final buffer drainage through the intended actual CLI/WPF entry points.
   Distinguish public service harnesses from application tests.
5. Confirm owned context/storage cleanup, existing runtime/license preservation
   and preservation of unrelated engineering. Report cleanup diagnostics and
   original failed outcomes; do not hide them through retries.
6. Run the [package checks](packaging.md#build-and-verify) and matching
   [distribution verification](../../../docs/deployment/release-package-manifests.md#release-verification).
   Installer registration/startup checks and native execution are different
   scopes; repeat affected scopes when package bytes change.

No native checks are run merely by reading this document or executing the
ordinary offline suite. Performance qualification uses the dedicated documented
workloads, not engineering timings or functional smoke runs.
