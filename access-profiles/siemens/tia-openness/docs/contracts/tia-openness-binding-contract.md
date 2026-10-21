# Siemens TIA Openness Binding Contract

This document defines the durable Siemens-internal contract between the Siemens
access profile shell and TIA Openness engineering bindings.

The contract is not an application-facing Common/Core contract. It belongs to
the Siemens access profile family and is intended to be implemented by concrete
TIA Openness version bindings.

## Purpose

The TIA Openness binding contract isolates engineering-project access from the
generic application and shared core.

It provides the Siemens profile shell with a stable boundary for:

- TIA Openness availability, loadability, and readiness evidence;
- TIA engineering session creation and cleanup;
- project source opening and archive retrieval hooks;
- PLC target discovery and resolution;
- build/check preparation;
- transfer/download to a prepared runtime destination;
- TIA operation progress, diagnostics, health, invalidation, and cleanup;
- facade/worker separation when TIA Openness requires a .NET Framework API
  adjacent worker.

The contract must keep TIA Portal object models, Siemens API handles, callback
objects, result objects, remoting/proxy objects, registry implementation details,
and concrete Siemens exception types inside the TIA binding implementation.

## Contract Location

The code contract lives in:

```text
access-profiles/siemens/src/PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts
```

This assembly is the shared Siemens-internal contract assembly for the Siemens
profile shell and Siemens technology binding packages. It must remain usable by
the shell-facing .NET implementation and by API-adjacent .NET Framework worker
implementations. TIA Openness assemblies must not be referenced from this
contract assembly.

The current shell-facing boundary starts a TIA engineering session explicitly:

```csharp
public interface ITiaOpennessEngineeringBinding : ISiemensTechnologyBinding
{
    Task<TiaEngineeringSessionOpenResult> OpenProjectAsync(
        TiaProjectOpenRequest request,
        CancellationToken cancellationToken);
}
```

Session-bound operations are exposed through `ITiaEngineeringSession`. Concrete
TIA operation DTOs are part of this Siemens contract assembly, not Common/Core
and not a concrete TIA V21 implementation project. Through
`ISiemensTechnologyBinding`, the entry point also implements
`ResolveReadiness(SiemensBindingReadinessRequest)`. The readiness call is the
registration-time boundary for TIA-specific installed-product, PublicAPI,
target-framework, assembly loadability, and worker/session probe evidence.

An authorization-capable real binding may additionally implement the optional
setup boundary:

```csharp
public interface ITiaOpennessAuthorizationProbeBinding
{
    Task<TiaAuthorizationProbeResult> ProbeAuthorizationAsync(
        TiaAuthorizationProbeRequest request,
        CancellationToken cancellationToken);
}
```

This interface is not part of normal project handling and is not required from
fake or otherwise non-authorizable bindings. A package advertises support through
the optional `setup.tiaOpennessAuthorizationProbe` manifest capability. Tooling
must require both the manifest capability and the optional interface before
dispatching a probe. A binding must publish the capability only when its concrete
probe is implemented.

The probe result distinguishes `Authorized`, `Denied`, `Unavailable`,
`TimedOut`, `Failed`, and `CleanupFailed`. Only `Authorized` is success.
The operation opens no project and returns only bounded diagnostics and evidence.
Worker/session creation and cleanup remain owned by the concrete binding.

## Ownership

### Siemens Profile Shell

The Siemens profile shell owns:

- Siemens profile configuration parsing and validation;
- Siemens-internal binding package discovery and selection;
- profile-owned registry/cache handling;
- composition of TIA and PLCSIM bindings into one loaded access profile session;
- mapping Siemens-internal binding outcomes to application-facing services;
- profile-level health, diagnostics, and cleanup coordination.

The shell must not own TIA Portal project/session handles or call TIA Openness
APIs directly.

### TIA Openness Binding Facade

The TIA binding facade owns the shell-facing implementation of
`ITiaOpennessEngineeringBinding`.

It owns:

- facade lifetime and worker supervision;
- request correlation;
- cancellation and timeout translation;
- validation of worker responses;
- normalization of TIA worker results into Siemens-internal DTOs;
- operation-scoped progress and diagnostics;
- TIA project-service health updates;
- facade-visible cleanup.

The facade must not leak TIA API objects or worker-local handles to the Siemens
profile shell.

### TIA Openness Worker

The worker is API-adjacent. It owns:

- TIA Openness assembly resolution;
- TIA Portal process/session handles;
- project handles;
- project open/retrieve operations;
- hardware tree traversal;
- compiler and download provider objects;
- download configuration callbacks;
- notification and system-dialog handling where required;
- Siemens exception capture and mapping input;
- TIA resource cleanup.

The worker may be a .NET Framework host when required by the selected Siemens
Openness API. Production facade/worker IPC follows the Siemens worker IPC
contract. The worker process owns the named-pipe server endpoint; the
shell-facing binding facade starts the worker and connects as the named-pipe
client. Standard output and standard error are diagnostic channels only and must
not carry the worker protocol.

TIA Openness worker commands are binding-specific messages on top of the
Siemens worker IPC envelope. Worker-backed TIA bindings use:

- `ProbeTargets` from facade to worker, followed by zero or more `Target`
  messages and one `EndTargets` message;
- `BuildTarget` from facade to worker with the selected neutral target id,
  followed by bounded `BuildEvidence` messages, optional `BuildDiagnostic`
  messages for compiler warnings/errors, and one `BuildResult` message;
- `TransferTarget` from facade to worker with the selected neutral target id
  and typed PLCSIM runtime transfer target, followed by bounded
  `TransferEvidence` messages, optional `TransferDiagnostic` messages for
  download warnings/errors, and one `TransferResult` message.

`ProbeTargets`, `BuildTarget`, and `TransferTarget` are serialized session
commands. If caller cancellation is requested after dispatch, the facade keeps
ownership of the command and drains its terminal result under a
binding-internal hard containment limit. Successful drain preserves the open
engineering session and returns caller cancellation; cancellation is not
rollback. Worker exit, pipe/protocol/correlation loss, explicit worker-side
session invalidation, or containment expiry invalidates the session.

Compiler information messages are evidence, not diagnostics. Compiler warnings
and errors are diagnostics because they explain a requested build operation
that completed with warnings or failed. Download information messages are
evidence; download warnings and errors are diagnostics because they explain a
requested transfer operation that completed with warnings or failed.

## Boundary Rules

The facade/worker boundary is DTO-only.

The following must not cross into the Siemens shell, Common, Core, WPF, CLI, or
generic access-profile contracts:

- TIA Portal sessions or process objects;
- TIA projects;
- devices, device items, hardware objects, software containers, PLC software
  objects, and hardware identifiers;
- compiler providers, compiler result objects, and compiler message objects;
- download providers, download result objects, connection configuration objects,
  delegates, callbacks, and callback state objects;
- Siemens exception objects;
- registry handles or raw registry result objects;
- remoting/proxy objects;
- raw Siemens API handles.

The boundary may expose:

- stable neutral identifiers;
- operation status and outcome DTOs;
- bounded technical evidence summaries;
- diagnostic data already mapped to application-facing categories;
- progress snapshots;
- health states;
- cancellation, timeout, invalidation, and unclear-outcome indicators.

## Binding Declaration

TIA bindings declare their package identity and supported product/API/project
source capabilities through the existing Siemens binding declaration model:

```csharp
public sealed record SiemensBindingInfo(
    string BindingKind,
    string BindingVersion,
    string DisplayName);

public sealed record SiemensBindingDeclaration(
    IReadOnlyList<SiemensAccessApiDeclaration> AccessApis,
    IReadOnlyList<SiemensProjectSourceDeclaration> ProjectSources,
    IReadOnlyList<SiemensRuntimeDeclaration> Runtimes,
    IReadOnlyList<string> UnitTestCommunicationContractVersions,
    IReadOnlyList<string> UnitTestEventStreamContractVersions);

public sealed record SiemensProjectSourceDeclaration(
    string Kind,
    string Extension,
    string Version,
    TiaProjectOpenMode OpenMode);
```

`OpenMode` is binding-owned metadata. The Siemens profile selects the matching
project-source declaration and forwards its mode in `TiaProjectOpenRequest`;
it must not infer archive handling or a concrete TIA version from a filename
extension.

For a TIA Openness binding:

- `BindingKind` is `tia-openness`;
- `AccessApis` declares supported TIA product/access API combinations;
- `ProjectSources` declares supported project source forms and their open modes,
  for example `.ap21` as `DirectProject` and `.zap21` as
  `RetrievedArchive`, both with project-source kind `tia-project`;
- `Runtimes` is empty because runtime ownership belongs to the PLCSIM binding;
- UnitTest communication contract lists are empty because UnitTest runtime
  communication belongs to the PLCSIM binding.

Product version and access API version remain separate facts. The TIA binding
implementation decides which concrete Siemens API it uses internally. The
application-facing configuration selects the Siemens access profile and project
source; it does not expose TIA API paths or binding class names.

## Availability And Readiness

The TIA binding owns detailed TIA availability, loadability, and readiness
evidence. The Siemens shell consumes normalized `SiemensBindingReadinessResult`
values and selected evidence; it must not duplicate TIA registry, PublicAPI,
assembly, or worker/session probe rules.

Evidence should cover:

- declared binding identity and supported product/API versions;
- installed TIA Portal and Openness product evidence;
- Siemens-documented registry evidence;
- PublicAPI path and target framework layout;
- required assembly and XML files;
- product version versus access API version;
- process architecture and framework requirements;
- resolver and assembly loadability;
- worker start/loadability;
- bounded session creation or attach probe evidence where appropriate;
- Openness permission, trust, or whitelist evidence where it can be detected
  without opening user projects;
- feature evidence for direct project open, archive retrieve, attach existing
  session, build/check, target discovery, and transfer/download.

Missing optional capabilities or absent expected products are capability facts.
They are not diagnostics by themselves unless they block a selected/requested
operation.

Diagnostics are reserved for real problems such as access denied, corrupted
metadata, selected binding load failure, missing required files for a selected
binding, resolver/assembly load crashes, worker probe crashes, stale selected
evidence, and permission/trust failure during a requested operation.

Registration-time probes must be bounded and cleanup-safe. They must not open
user projects, retrieve archives, compile, download, create runtimes, connect
UnitTest communication, or leave TIA sessions/workers alive.

## Session Contract

Opening a TIA engineering session is an explicit operation after the Siemens
profile session has loaded.

A TIA engineering session owns:

- the selected project source context;
- TIA Portal process/session state;
- one opened project context when a project is open;
- session-bound target evidence;
- TIA operation cancellation and cleanup state.

If session or project opening fails, no partially opened project context may be
returned. The binding must leave the facade/worker in a cleanup-safe state.

The shell closes a successful session through `ITiaEngineeringSession.CloseAsync`.
The method is the functional end of the TIA engineering session and its opened
project context. Technical disposal or worker process cleanup remains an
implementation detail of the binding.

Session handles are not durable configuration. A neutral target id may be stored
or logged, but TIA object handles must be revalidated against the currently open
session before compile or transfer operations.

## Project Source Handling

The Siemens V1 binding supports these source modes:

- direct `.ap21` project open;
- `.zap21` archive retrieval into a binding-owned work directory;
- a later attach mode for an existing TIA Portal session in explicit
  WPF/debugging workflows.

For archive retrieval, `TiaProjectOpenRequest.ArchiveWorkDirectory` is an
optional base directory, not the final extraction directory. If it is absent,
the binding uses the normal per-user temporary base below
`%TEMP%\PLCSIM.UnitTest\projects`. The binding creates a unique child directory
for every archive-backed engineering session and passes that child to the
Siemens `ProjectComposition.Retrieve` operation. Configuration parsing and
validation must not allocate this child directory or generate a session
identity.

The selected archive remains caller-owned and immutable. The binding owns only
the unique retrieved-project child directory and must never delete or rewrite
the archive source. The project identity reported to the profile remains the
selected archive path; the extracted direct-project path is binding-internal
evidence.

`Retrieve` returns the opened TIA project. The binding must use that returned
project directly and must not invoke a second project-open operation. V1 does
not use `RetrieveWithUpgrade`.

Archive-session cleanup follows this order:

1. stop accepting new project operations;
2. close/dispose the retrieved TIA project;
3. dispose the owned TIA Portal session and worker resources;
4. delete only the unique binding-owned child directory.

The same cleanup applies after successful use, partial retrieval failure,
cancellation, timeout, Back, End Session, and application shutdown. Cleanup is
idempotent. A cleanup failure is reported as diagnostic evidence and must not
hide the primary operation failure. A partial retrieval must never produce a
usable engineering session.

Automatic project upgrade is not part of the default flow. Upgrade-required
sources are reported as unsupported, incompatible, or failed unless a later
explicit upgrade mode is designed.

UMAC, PLC access protection, and generic protected-project handling are not part
of V1. Safety-program authorization is a separate optional project-session
boundary. A binding may inspect the selected target and report `NotRequired`,
`PasswordRequired`, or a terminal unsupported operation without exposing
Siemens authorization objects beyond the binding contract.

The current TIA Openness binding policy rejects targets containing Safety
program content before credential prompting, build, or transfer because the
managed online transfer path does not support loading that content. Detection
must use project content, not F-capable CPU classification: a standard program
on an F-capable target remains supported. Future bindings may return
`PasswordRequired` only when they can support the complete authorized build and
transfer workflow.

Credentials use `SecureString` at the binding boundary and protected local IPC
when a worker must receive them. Plaintext credentials must not appear in
arguments, logs, diagnostics, evidence, exception text, or protocol traces.

## Target Discovery

TIA target discovery traverses the opened project hardware tree and returns
neutral target candidates.

The binding identifies usable PLC targets from TIA project evidence that exposes
`Siemens.Engineering.SW.PlcSoftware` through the device item software container.
CPU classification is useful supporting evidence, but it is not sufficient by
itself because other project items can be CPU-like or device-like without being
a PLC execution target for this application. The binding must keep `Device`,
`DeviceItem`, `HardwareObject`, `SoftwareContainer`, `PlcSoftware`,
`HwIdentifier`, and similar TIA API objects internal.

Target traversal must include both ungrouped and grouped Siemens project
devices:

- direct project devices / the ungrouped device scope;
- `Project.DeviceGroups`;
- recursive `DeviceUserGroup.Groups`;
- `DeviceGroup.Devices`;
- recursive `DeviceItem.DeviceItems`.

`TiaProjectTargetRef.Id` is the canonical `plcRef.id` value. It is not a
display name, CPU order number, CPU type, or raw TIA object handle. For Siemens
TIA targets the canonical id is a slash-separated list of typed, URI-escaped
segments:

```text
scope:ungrouped/device:<device-name>/item:<position>:<device-item-name>
group:<group-name>/group:<sub-group-name>/device:<device-name>/item:<position>:<device-item-name>
```

Segment values are escaped independently before joining so a slash inside a
Siemens device or group name cannot be confused with the canonical path
separator. `DisplayName`, `TechnicalPath`, `CpuFamily`, and `CpuType` are target
metadata for presentation, diagnostics, and evidence. They must not drive
selection logic.

Target resolution rules:

- if `plcRef.id` is present, it must match exactly one supported target;
- zero matches is not found;
- multiple matches is ambiguous;
- no fuzzy matching against display names, CPU names, order numbers, or type
  identifiers drives control flow;
- if `plcRef.id` is absent and exactly one supported target exists, the binding
  may return that target as the unambiguous selection;
- unsupported candidates may be returned as diagnostic/evidence data but must
  not become selectable execution targets.

The first Siemens target supports S7-1500 PLC targets only.

## Build And Check

Build/check is a TIA engineering responsibility.

The facade/session exposes a neutral operation for a resolved PLC target inside
the opened project context of a TIA engineering session. Internally, the binding
may use Siemens compiler APIs such as `ICompilable`, compiler providers,
compiler results, and compiler messages. Those objects remain internal.

Build outcomes must distinguish:

- success;
- success with warnings;
- failed;
- cancelled;
- timed out;
- invalidated or stale target;
- unclear final outcome.

Warnings do not automatically block transfer unless the binding can prove they
make the later download/runtime path unsafe. Blocking compiler errors prevent
transfer.

The public result contains a summary, diagnostics, and bounded evidence
references only. It must not expose raw compiler objects or unbounded compiler
logs as structured contract data.

For worker-based bindings, the facade sends the selected neutral target id to
the worker. The worker resolves the corresponding TIA project object inside the
opened project session and performs compilation through Siemens Openness
compiler APIs. The facade maps the worker result to `TiaBuildResult`.

## Transfer And Download

Transfer/download is a TIA engineering responsibility. Runtime discovery,
preparation, lifecycle, and communication remain PLCSIM binding
responsibilities.

The TIA facade consumes the selected application-facing runtime target/reference
or a Siemens-internal neutral destination derived from it. The TIA binding must
not create, select, or control PLCSIM runtime lifecycle.

The first Siemens path targets PLCSIM Advanced explicitly. Siemens download
details such as target selection, connection configuration, pre/post
configuration delegates, provider objects, result objects, callback objects, and
prompt handling remain inside the TIA binding/worker.

For Siemens profile V1 the profile shell provides a typed, Siemens-internal
transfer target descriptor instead of user-facing free-text Siemens route
names. The profile-level transfer policy is:

- connection mode `PnIe`;
- PC interface `PlcSim`;
- PLCSIM PC-interface number `1`;
- first available PLC target interface unless a later explicit advanced
  configuration is designed.

The concrete TIA Openness binding maps those typed values to Siemens API
connection names such as `PN/IE` and `PLCSIM` only at the binding/worker
boundary. The worker resolves the Siemens `ConnectionConfiguration` mode, PC
interface, and target interface from the opened TIA project/session context and
returns bounded evidence for the route actually used. Evidence may therefore
contain Siemens names such as `1 X1`, but the first V1 slice does not require
users to configure the PLC target interface by name.

Transfer outcomes must distinguish:

- success;
- success with warnings;
- failed;
- rejected by download configuration or TIA;
- missing, stale, or unsupported project target;
- missing, stale, or incompatible runtime destination;
- unsupported prompt or credential requirement;
- cancelled;
- timed out;
- invalidated context;
- unclear final state.

Unsupported interactive prompts or credential demands are reported as neutral
operation failures/diagnostics. The TIA binding must not surface Siemens
callback objects to WPF, CLI, Core, or the Siemens shell.

## Progress

TIA progress is operation-scoped and neutral.

The binding may publish bounded progress for:

- session creation;
- project open/retrieve;
- target discovery;
- build/check;
- transfer/download;
- cancellation;
- timeout handling;
- cleanup.

Progress is not health and not diagnostics. It must not expose Siemens
notifications, callback objects, compiler result objects, download result
objects, or API event-handler objects.

## Diagnostics

TIA diagnostics are emitted for problems owned by the TIA project-service
boundary:

- project open/retrieve failure;
- missing products or support packages when they block the requested operation;
- access, permission, trust, or whitelist failure;
- target missing, stale, ambiguous, or unsupported;
- compile/download errors or relevant warnings;
- unsupported prompt or credential requirement;
- TIA session or worker loss;
- selected binding failure during operation;
- cancellation, timeout, unclear outcome, or invalidation when they affect the
  requested operation;
- cleanup failure.

The TIA binding maps Siemens exception data, compiler/download messages,
notifications, and callback outcomes to application-facing diagnostic
categories, messages, and bounded evidence references.

A layer must not republish diagnostics already emitted by a lower layer unless
it adds a new problem at its own boundary.

## Health And Invalidation

TIA project-service health maps to `AccessServiceKind.Project`.

Health states:

| State | Meaning |
| --- | --- |
| `NotEstablished` | No trusted TIA engineering session with opened project context exists yet. |
| `Healthy` | The open project and selected target context are currently trusted. |
| `RevalidationRequired` | Source, target, or session evidence may be stale but can be explicitly rechecked. |
| `Invalidated` | The TIA engineering session, opened project context, or target context can no longer be trusted. |
| `Disposed` | The TIA project service/session was unloaded. |

Invalidation is service-owned and functional, not UI-owned.

Examples:

- project source changed;
- project was closed or lost;
- TIA session or worker died;
- selected target can no longer be resolved;
- selected binding evidence is stale during operation;
- a non-recoverable Siemens error affects the current project context;
- cancellation or timeout whose terminal worker result could not be drained
  leaves the final project/download state unclear.

The binding reports health and operation outcomes. Shared workflow decides gates,
active workflow consequences, diagnostics display, output behavior, and artifact
consequences.

## Cleanup And Recovery

Cleanup is bounded best-effort:

1. stop accepting new TIA project operations;
2. drain or technically contain in-flight operations;
3. close opened project handles;
4. dispose owned TIA session, worker, and facade resources;
5. publish final health;
6. report cleanup diagnostics without hiding the original failure.

Cleanup must not:

- delete project sources;
- delete application artifacts, logs, or configuration;
- delete runtime storage;
- reset or repair PLCSIM state;
- reset PLC-side UnitTest state;
- hide failed runs by performing implicit repair actions.

Recovery is explicit. The TIA binding must not silently refresh registration,
select another binding, reopen a different project, auto-upgrade a project,
auto-answer unsupported prompts, or repair external state. Recovery is explicit
registration, reload, setup, revalidation, or retry driven by the application
workflow.

## Concrete Binding Ownership

Each concrete TIA binding owns its package manifest, supported project-source
declarations, implementation status, version-specific Siemens API integration,
tests, and validation documentation. Those details belong to the concrete
binding subtree and are not duplicated in this parent contract.

A production binding manifest declares package identity and capability shape.
Release capability additionally requires deterministic contract tests and
opt-in validation against the selected Siemens product/API environment.

Fake or sample bindings implement this contract without Siemens dependencies.
They own their fixture formats and injected behavior below their own subtree.
They prove shell composition and contract semantics only; they do not prove
real Siemens API loading, project behavior, permissions, callbacks, IPC, or
cleanup.

## Verification Expectations

Pure tests should verify:

- TIA contract DTOs do not expose Siemens API types;
- concrete bindings can be packaged and loaded through the Siemens binding
  package path;
- shell composition works without installed Siemens software;
- success, warning, failure, cancellation, timeout, invalidated, and unclear
  operation outcomes are representable;
- diagnostics are mapped without Siemens API object leaks;
- cleanup disposes sample resources and reports cleanup failures;
- no production path relies on exact diagnostic display text.

Real Siemens validation remains opt-in and must verify actual TIA Openness API
behavior, real project open, real compile/download, permissions/whitelisting,
system-dialog/callback behavior, worker hosting, and final transport behavior.
