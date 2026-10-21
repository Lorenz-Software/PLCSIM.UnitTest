# TwinCAT Profile Implementation

This document maps the TwinCAT profile to its code and resource owners. Unlike
the Siemens profile, it does not compose a catalog of internal technology
plugins. Engineering and ADS adapters are implementation components of one
profile package, with separate lifetimes and test seams.

The shared [runtime](../../../../../docs/contracts/access-profile-runtime-contract.md)
and [package](../../../../../docs/contracts/access-profile-package-contract.md)
contracts define the host boundary. This overview does not redefine them.
Use [configuration](../../configuration.md) and [usage](../../usage.md) for user
inputs, [dependencies](../../dependencies.md) for installed engineering interop,
and [verification](../../verification.md) for qualification and test procedures.

## Code Owners

Profile source lives in
[`src/PLCSIM.UnitTest.AccessProfiles.Beckhoff`](../../../src/PLCSIM.UnitTest.AccessProfiles.Beckhoff).

| Component | Responsibility |
| --- | --- |
| `BeckhoffAccessProfilePlugin` | One initialized package context, prerequisite registration, source evaluation and one active session. |
| `Registration/BeckhoffEnvironment.cs` / `BeckhoffRegistration.cs` | Installed XAE/Usermode prerequisite inspection and application-facing capability mapping. |
| `Registration/BeckhoffProfileRegistry.cs` / `BeckhoffProjectSourceInspector.cs` | Profile-owned persisted evidence and lightweight source inspection. |
| `BeckhoffAccessProfileSession` / `BeckhoffConfigurationService` | Configuration and four-service composition, coordinated communication preparation/reconnect and disposal. |
| `BeckhoffProjectService` | Managed workspace, PLC target selection, build/transfer orchestration and explicit engineering release. |
| `Engineering/StaEngineeringAccess.cs` / `XaeComApartment.cs` | Serialize engineering operations on an owned STA with Windows COM message handling. |
| `Engineering/XaeProjectAccess*.cs` | Owned XAE/DTE context, actual-version validation, build and late-bound automation calls. |
| `Runtime/BeckhoffPlcControlService*.cs` / `LocalUsermodeRuntimeCatalog.cs` | Existing runtime selection, readiness/state observation, prepared endpoint and transfer coordination. |
| `Runtime/AdsRuntimeAccess.cs` | System-service ADS access and a replaceable PLC-session endpoint. |
| `BeckhoffUnitTestLifecycleService` / `Runtime/AdsRunControlAccess*.cs` | RunControl metadata validation, commands and independent lifecycle observations. |
| `BeckhoffUnitTestEventStreamService` / `Runtime/EventStream/` | EventBuffer validation, coherent capture/drain/release and immutable batch publication. |

Interfaces and injected factories are deterministic test seams, not separately
discovered public or internal plugin packages. ADS and engineering objects do
not escape through the shared application-facing contracts.

## Registration And Source Selection

Registration inspects installed prerequisites without activating XAE or
operating a runtime. It stores reproducible profile evidence under the supplied
`ProfileDataDirectory`; the immutable profile package is not a cache directory.
The host separately stores the returned shared registration/capability view.

`EvaluateProjectSource` reads this registry, checks registered usable source
forms and performs lightweight project inspection. It does not open the project
in XAE or register implicitly. Source inspection is owned here rather than by a
generic application extension table. Missing/corrupt registry or malformed
source data is an error, not evidence of an installed engineering session.

`LoadAsync` validates profile identity, ownership mode and active-operation
constraints and creates service/configuration objects. It does not reproduce
the Siemens selected-binding algorithm: there is no internal package catalog to
scan or load. Construction does not activate XAE, connect ADS, create a runtime
or validate a PLC application. Those prerequisites are checked at the operation
that needs them.

## Session And Configuration

Each plugin owns at most one session. The session receives setup-only
`AccessProfileSessionRequest`; operational configuration comes through current
request snapshots and the generic configuration projection. The configuration
service validates/edits profile JSON without opening engineering or runtime
resources. The host logger factory is borrowed and not disposed by the profile.

The session composes project, PLC-control, lifecycle and event-stream services
and forwards service health by its owning `AccessServiceKind`. A service-local
failure does not become a fabricated profile-wide failure or test result.

## Engineering And Transfer

Managed project operations use `ProjectWorkspace` for direct solutions or owned
archive extraction. `BeckhoffProjectService` serializes operations and obtains
engineering access lazily. Its `IEngineeringRuntimeTransfer` collaborator is the
PLC-control service, keeping engineering transfer and target readiness aligned.
Externally managed operation does not open/build/download a project.

`StaEngineeringAccess` owns the queue/thread on which `XaeProjectAccess` and COM
references are used. The context creates its own XAE host; it does not attach to
or close an arbitrary interactive user's engineering instance. COM apartment
message handling and retry behavior are confined to this boundary.

Requested engineering selection is checked against the actually loaded version;
successful COM activation alone does not establish version compatibility.
Beckhoff automation is late-bound in the owned host, with installed typed Visual
Studio interop retained as a prerequisite. No managed Beckhoff wrapper or local
redeclared COM ABI is part of the package.

Build outcomes use the existing compiler-result boundary. Static-analysis
findings do not silently establish a new build gate. Transfer targets the
explicit selected system/PLC and coordinates configuration activation, restart
readiness, forced/silent login, logoff and effective PLC endpoint verification.
There is no implicit license activation or runtime creation. The existing
[project deployment contract](../../contracts/project-deployment.md) owns these
semantics and source/workspace preservation limits.

Caller cancellation does not abandon dispatched COM work. Close waits for
accepted work to reach its completion boundary and releases owned engineering
references before archive workspace cleanup. This is cooperative cancellation,
not a promise to forcibly interrupt indefinitely blocked vendor calls.

## Runtime And Communication Ownership

PLC control selects existing local Usermode runtime targets using profile-owned
catalog/configuration rules. Unsupported `CreateOwned` preparation does not
fall through to attaching an arbitrary instance. See the
[runtime provisioning contract](../../contracts/runtime-provisioning.md).

`AdsRuntimeAccess` separates system-service access from the effective PLC port.
Runtime observation retains identity/readiness and application binding
generation; accepted start/stop calls are not synthetic PLC-state updates.
The effective endpoint established by transfer survives engineering release.

Lifecycle and EventBuffer access share the selected endpoint/generation, not a
single ADS client. Their access objects own distinct clients, symbol handles and
notification registrations. `AdsClientFactory` supplies unconnected clients;
the consuming owner connects and releases them. Generation/availability changes
invalidate access assumptions rather than silently reusing stale symbols.

The session serializes communication preparation and reconnect. Preparation
cleans up previous communication, prepares EventStream then RunControl, and
checks that the endpoint stayed current/available with the same generation.
Partial failure cleans both services; it does not leave one presented as a
complete shared communication context.

Reconnect coordinates release of communication with same-target runtime
revalidation. It is not discovery, download, runtime provisioning or PLC Start.
The public services remain the application boundary; PLC-owned lifecycle and
event evidence are mapped through the
[consumer contract](../../contracts/tc3unittest-consumer-mapping.md). Shared Core
owns run attribution, result meaning and export eligibility, not the ADS readers.

## Release And Failure Boundaries

Session disposal is idempotent and serialized against communication work. It
attempts all service closes in reverse composition order, disposes configuration,
detaches forwarding, marks the services disposed and releases the plugin's
active-session ownership. Errors are reported after best-effort cleanup rather
than preventing remaining owners from closing.

ADS owners remove notifications/symbol handles and dispose their own clients.
Engineering owners close their context and release archive extraction storage.
Existing runtime resources and user-owned engineering hosts are not destroyed
by technical session cleanup. Cleanup is not an implicit UnitTest Reset and
does not establish successful run completion.

Profile diagnostics and [logging](../../logging.md) explain owned operation and
cleanup failures; health expresses context trust. Build, transfer, ADS command
acceptance, PLC state, RunControl lifecycle and event-stream results remain
separate facts. None of the implementation structure or offline test seams
alone proves live compatibility; retain the limits in
[verification](../../verification.md).

## Engineering Adapter Details

`XaeProjectAccess` activates `TcXaeShell.DTE.17.0`, checks the new process against
already-running shells, hides its owned window and enables automation SilentMode.
Direct solutions open in place; archives use `Solution.AddFromTemplate` in owned
storage. PLC ids combine the normalized source-relative system-project path and
PLC GUID, not enumeration order or temporary archive-directory names. Before
build/authorization, the adapter reacquires the target and rejects changed tree
paths or ports. See the [project contract](../../contracts/project-deployment.md)
for the selection and source-preservation boundary.

Build uses `SolutionBuild.BuildProject(configuration, selectedSystemPath, true)`
with the active solution configuration/platform and its declared dependencies.
Completion and the failed-project count determine success. The adapter does not
read ErrorList: its entries were unreliable in hidden automation. Optional Debug
output is a best-effort Build-pane snapshot limited to 65536 characters; it can
include earlier output and never changes the build result or creates structured
application diagnostics. At higher log thresholds the pane is not read.

Transfer sets and rechecks the verified system destination, activates it and
requests `StartRestartTwinCAT`. The runtime observer must confirm fresh system
RUN and a changed nonzero completion counter before nested PLC online access
invokes `Login(FORCEDOWNLOAD | SILENT)`. Login state, owned Logoff and fresh ADS
access are separate checks. Existing bounded system/PLC waits use the runtime
observer, not a second transfer-local system poller; those waits do not interrupt
blocked COM calls. No implicit transfer retry is introduced.

Cleanup closes without saving, quits only a positively identified owned shell
and checks exit before deleting archive storage. A partial open with unknown
process ownership releases its local COM reference and reports possible manual
cleanup rather than closing an unidentified shell. Incomplete cleanup retains
ownership for retry and blocks replacing the project context.

## ADS Adapter Details

The local catalog reads `Runtimes/<name>/3.1/TcRegistry.xml`. Online identity is
checked through system port 10000 against AMS Net ID, `PC-WIN-U` device type and
configuration directory; this is target validation, not remote authentication.
Read-only registry enumeration uses HKLM index group 200 and bounded value
decoding, ending on `DeviceNotFound`; no registry write is implemented. The
vendor [protocol definitions](https://github.com/Beckhoff/ADS/blob/master/AdsLib/RegistryAccess.h)
and [enumeration implementation](https://github.com/Beckhoff/ADS/blob/master/AdsLib/RegistryAccess.cpp)
describe that access path.

PLC notifications wake state rereads. The independent system check runs at an
internal 250 ms interval with `AdsClientSettings.FastWriteThrough` and a 3000 ms
request timeout, avoiding cached FailFast errors through CONFIG/RUN. Relevant
data-service errors request a coalesced early check, at most once per interval;
they do not themselves prove CONFIG. These internal choices do not promise a
specific recovery latency or change the configurable data-monitoring interval.

RunControl reads the complete symbol-derived execution structure through one
variable handle on its serialized read/command path. In polling mode, the
monitoring delay starts after the attempt. Opt-in subscription mode waits for
notifications and performs periodic verification at an internal 100 ms interval.
Individual Start/Abort/Reset BOOL writes return transport
acceptance without readback, clearing or replay. Failed reads retain the last
real observation; fresh preparation starts Unknown. Public communication
preparation composes both data services; `PrepareRunControlAsync` remains an
internal focused-probe entry point.

`EventStreamLayout.ResolveAsync` borrows its supplied client and resolves field
offsets, one-dimensional array bounds/stride and addresses. It owns no connection
or observation loop. Decoders validate ASCII termination, integer nanosecond
conversion, signed classification values and unsigned counters rather than
assuming C# packing. The [consumer mapping](../../contracts/tc3unittest-consumer-mapping.md)
owns normalization, coherent prefix, commit and reset rules.

`AdsEventStreamAccess` owns the endpoint client/handles. Metadata groups FIFO
control, status and deletion command in one Sum Read, requiring valid results
and lengths; this is not an atomic PLC snapshot. Payload reads use one range or
two on physical wrap. `EventStreamDrain` validates the captured prefix, writes
its watermark and only then returns the immutable batch for publication.
Pending deletion refreshes metadata without synthesizing empty evidence.

In polling mode, the event-stream loop delays by `drainIntervalMs` for
backlog/pending deletion and `monitoringIntervalMs` for trusted empty or
revalidation. Opt-in subscription mode uses notification-driven waits; the
[configuration guide](../../configuration.md) owns the selectable modes.
It publishes the
first trusted empty and coalesces unchanged repeats. Callbacks run outside the
access gate. Stop joins the loop before disposal; an already dispatched bounded
watermark write can finish and publish its accepted batch. Failed writes are not
blindly replayed, and subscriber failure stops observation with a diagnostic;
neither path establishes exactly-once delivery after uncertain write acceptance.

Buffer reset is a serialized single BOOL write, not a synthesized clean state.
Recoverable access errors rebind technical handles while retaining known
metadata; explicit reconnect invalidates logical UnitTest contexts and requires
fresh preparation. Known PLC unavailability suppresses data reads while system
observation continues, without imposing a CONFIG deadline. Contract replacement
or runtime-context replacement cannot silently reuse an old communication
context. Shared Core, not the adapter, decides epoch attribution when a reset's
empty/zero window was not observed.
