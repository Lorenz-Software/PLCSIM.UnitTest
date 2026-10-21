# Project Preparation And Deployment

This is the profile-local implementation contract for managed TwinCAT 4026
engineering through the existing `IPlcProjectService`. It defines required
behavior; implementation and live evidence are recorded separately in
[verification](../verification.md#current-qualification). It does not extend
the application or shared service contracts.

## Sources And Selection

Open a supplied complete `.sln` project tree in place. Do not create a defensive
copy. CI provides an exclusively used workspace; concurrent editing, builds or
downloads from another XAE using that project are not coordinated by the profile.
External/no-download debugging does not open an engineering project.

Import `.tszip` input through the vendor archive mechanism into a unique owned
working subdirectory. Respect the configured archive parent and remove only the
owned subdirectory during cleanup, never the original archive or unrelated files.
See the [archive workspace configuration](../configuration.md#archive-workspace).
A standalone `.plcproj` without system configuration is outside the initial scope.

Discovery enumerates PLC projects across the source's TwinCAT system projects.
Multiple candidates are a selection task, not a discovery failure. Discovery
does not activate a configuration or download. Reuse a compatible owned
engineering context between discovery and preparation; do not reopen it merely
because the caller advances to preparation.

Target identifiers distinguish the containing system project and PLC project.
They must not depend on enumeration order, display names alone or a temporary
archive extraction root. Resolve the explicit selection, or the sole unambiguous
candidate, before build or transfer. Retain neutral target evidence independently
of COM references so engineering release does not erase completed setup evidence.

## Engineering Host

Use an owned hidden XAE shell through a dedicated STA in the .NET 8 profile host.
Do not attach to a user's XAE, use the WPF UI thread for COM work or introduce a
profile worker. Serialize engineering operations on that STA and reacquire
subordinate COM references for operations rather than assume they remain current
across state changes.

Bind Beckhoff Automation Interface operations through late-bound COM dispatch
in that DTE host. Retain typed Visual Studio interop for shell operations where
appropriate. Do not require a preinstalled `TCatSysManagerLib.dll`, generate or
embed Beckhoff interface metadata, or substitute the standalone System Manager
for the DTE workflow. The installed compatible XAE/PLC engineering and Visual
Studio interop remain prerequisites; this is not installation-free engineering.
Registration and loading must validate the same actual dependencies, and the
package must not contain Beckhoff wrapper binaries or generated interface metadata.
This defines the target binding; implementation and qualification status remain
separate from this contract.

Enable global automation `SilentMode` in the owned XAE, in addition to the
transfer Login flags below. This reduces interactive vendor dialogs; it is not
proof that all failures are non-interactive or that an operation succeeded.
Preserve compiler/vendor diagnostics and verify actual outcomes. Qualify these
paths on the production .NET 8 stack rather than assume earlier Framework probe
behavior is sufficient.

## Build And Authorization

Build the selected TwinCAT system project and its required dependencies, not
unrelated system projects in the solution. Use the supplied build configuration
and platform. Report incompatibility with the intended Usermode environment
instead of silently rewriting project settings. Normal build output in the
supplied workspace is expected; project access is not a read-only operation.

Build success follows the completed DTE compiler build and its failed-project
count. Additional static-analysis findings are separate diagnostic evidence,
not an additional build gate. Compiler success is not a guarantee that static
analysis passed or that no warnings exist. The optional bounded output snapshot
can include earlier messages and is not parsed into build-result semantics.

Leave version pinning and compatible engineering selection to the project and
installed environment within the 4026 scope. Do not introduce an exact-patch
gate, pinning rewrite or implicit 4024 fallback.

Observe and validate the actual loaded engineering version after opening the
project, before build or target operations. An unsupported or unverifiable
effective version is an error, not permission to continue with an assumed
version. A normally returning Remote Manager version assignment does not prove
that the requested version was selected. If an exact version is explicitly
requested, verify that request against the effective version and reject a
mismatch. This does not introduce an exact-version configuration option or make
the project's saved version an exact patch requirement. Installed prerequisites,
the loaded engineering version and the target Runtime version are distinct
evidence; record them without inferring one from another.

For a standard PLC target, Safety-program authorization is `NotRequired`.
TwinSAFE authorization and transfer are not supported by this baseline. An
identified unsupported requirement must return `NotSupported` before transfer,
not be represented as `NotRequired` or a successful authorization. The presence
of a separate TwinSAFE project in the same system is not itself such a requirement
and does not block the selected standard PLC. Only a required unsupported Safety
operation is rejected; this profile does not perform TwinSAFE deployment. Do not request
a password for an authorization path that is not implemented. License activation
remains an external prerequisite, with failures reported through diagnostics;
see the [licensing reference](../licensing.md).

## Transfer Boundary

Use the verified prepared Runtime identity and the selected PLC project's port
as defined by [managed and external targeting](../configuration.md#targeting-with-and-without-download).
Do not use the developer's saved Net ID as the deployment destination. Select
the effective destination in the owned XAE context without explicitly saving
that change back to the supplied source project. Reject explicit port conflicts
before transfer; an omitted port is not an override to `851`.

Configuration activation applies to the selected system configuration. Its
necessary CONFIG-to-RUN transition or restart can affect sibling PLC applications
within that Runtime. Selecting one PLC project does not isolate those system-wide
effects. It does not permit Windows reboot, blanket service restart or operations
on another Runtime. Build and ordinary discovery do not imply activation.

After configuration activation and any necessary selected-system transition,
download only the selected PLC application using `Login(FORCEDOWNLOAD | SILENT)`.
Coordinate the pre-login wait through the runtime-owned
[system-readiness observation](runtime-provisioning.md#session-ownership-and-system-readiness).
Do not detach that observer during activation/transfer or substitute the first
RUN read, a stale pre-restart observation, or local client connectivity for the
confirmed restart/readiness condition. Timeout or cancellation prevents Login;
the engineering adapter remains independent of concrete ADS session objects.
The path must support an existing Runtime without a PLC application port yet.
Bind the effective PLC port without replacing the system session or observer.
Suspend PLC application observation during activation/download, not system
observation. Resume it after Login and require fresh actual PLC access before
reporting success. Failure or cooperative cancellation also resumes ordinary
observation and retains the effective endpoint. Previously obtained data endpoints
are invalidated when transfer rebinds their descriptor.
Do not call `GenerateBootProject`, change boot-autostart settings or add Reset
Origin as a transfer step. Do not create a Runtime or activate its licenses.

A normally returning Login call alone does not establish successful transfer.
Check login outcome and actual access to the selected PLC endpoint, and report
vendor errors through the existing diagnostics. PLC Start remains a separate
application-controlled operation; transfer does not issue a UnitTest command.
External/no-download mode performs none of these engineering operations.

## Cancellation And Release

Accept cancellation before queued work executes and at supported operation
boundaries. Merely cancelling an await does not stop an in-flight COM call.
Drain or contain dispatched work before claiming the engineering context is
released. Do not report timeout isolation that the in-process implementation
cannot provide or silently introduce a worker or automatic transfer retry.

Release only owned COM/XAE resources and archive working storage. Preserve source
projects, other XAE sessions, existing Runtime instances and their licenses.
After successful transfer, project release does not tear down runtime access or
discard the effective PLC endpoint needed for control, communication and reconnect.
Report incomplete cleanup truthfully instead of announcing successful release.

## Verification

Ordinary automated tests use doubles and temporary file fixtures; they require
neither an installed XAE nor a live PLC. Cover source/import ownership, multiple
system/PLC candidates, stable selection, authorization, build/transfer failures,
diagnostics, cancellation boundaries and cleanup.

Qualify direct and archive engineering on .NET 8 against an explicitly authorized
existing local Usermode Runtime. Include first application download, replacement
of changed application content, a source naming a different Runtime, non-default
PLC-port propagation and external/no-download isolation. Verify owned-host cleanup
on the exercised success/failure/cancellation paths and record any permanently
blocked vendor-call containment limits explicitly; do not infer them from a
cancelled caller task. Real-runtime harnesses and raw evidence remain temporary.

Qualify automated configuration activation/restart separately from download on
an already prepared Runtime. Exercise the normal target-selection, activation
and system-RUN sequence through the production transfer coordinator, including
its fresh restart observation, rather than substitute manual preparation.
Verify download, login/logoff, fresh PLC endpoint access and the absence of a
PLC Start request. Do not add explicit boot-project generation or boot-autostart
changes to the transfer sequence. The profile does not implement a separate
activation-copy suppression control; a `CopyBootFiles` XML readback is not an
additional activation prerequisite. Project/vendor-controlled file effects must
not be confused with an explicit `GenerateBootProject` operation. No total
Boot-directory immutability or persistent-application-state preservation
guarantee follows from this contract.
