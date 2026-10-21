# Runtime Provisioning And Release

This defines the Beckhoff profile's agreed mapping of the existing application
API. It is an implementation contract; the scope of implementation and live
evidence is recorded in [verification](../verification.md#current-qualification).
No new shared DTO or release outcome is introduced by this mapping.

## Internal Strategy

Use an internal `RuntimeProvisioningStrategy` with these intended values:

| Strategy | Meaning |
| --- | --- |
| `ReuseExisting` | Default. Use a pre-existing local Runtime; do not create or destroy one. |
| `CreateDedicated` | Reserved for actual Runtime provisioning. Actual creation and destruction are currently `NotSupported`. |

The strategy is an implementation setting, not an `accessProfile.values` field,
application preference or independent create/destroy toggle. Capture the effective
strategy and actual resource ownership when preparing the context; release must
not reinterpret it through a later setting change. Merely requesting
`CreateOwned` is not proof that the profile created a vendor Runtime.

## Existing Runtime Mapping

Under `ReuseExisting`, handle application requests as follows:

| Request | Profile behavior | Successful result |
| --- | --- | --- |
| `UseExisting` | Resolve the explicitly selected existing target and open technical access. | Prepared context and actual target. |
| `CreateOwned` | Reject before opening ADS access, regardless of the internal strategy. | `NotSupported`; no existing Runtime is substituted. |
| `KeepResource` | Close profile-owned technical access and preserve the existing Runtime. | `Succeeded` with `ResourceKept`. |
| `DestroyResource` | Complete release of the reused context and preserve the existing Runtime. | `Succeeded` with `ResourceKept`. |

There is no CreateOwned-to-existing-Runtime compatibility mapping. Select
`UseExisting` explicitly in the application configuration. Resource preservation
during cleanup is not a failed or partial attempt to destroy a Runtime and must
not emit an unsupported-cleanup warning for expected preservation.
Report technical cleanup failures truthfully;
never turn them into success just because no Runtime destruction was intended.

`ResourceKept` describes the actual resource postcondition. It must not be
replaced with `ResourceDestroyed` to suggest that a pre-existing instance was
removed. This profile mapping does not alter the provisioning/release behavior
of other profiles.

Discovery designates the local standard Runtime through `DefaultTarget`. The
host uses it only for `UseExisting` without an explicit target, then supplies the
resolved target to preparation. A missing default leaves `DefaultTarget` null;
explicitly selected candidates remain usable. Preparation does not infer a
target from a missing selection. The selected default must be identifiable rather than the first enumeration
entry or a hard-coded development AMS Net ID. Missing or ambiguous selection
fails with a useful diagnostic; it must not fall back to an unrelated target or
create an instance. Resolve the existing local default Usermode configuration
and verify its actual Runtime identity as defined in the
[configuration reference](../configuration.md#cli-and-the-existing-local-default).
Use the documented local Usermode configuration directories as discovery
evidence, not proof of a reachable Runtime. Resolve the default from
`UmRT_Default` and verify its effective AMS identity and local association before
allowing writes. A successful state read at an address alone does not establish
that it belongs to the intended local Usermode instance. Do not guess an adjusted
Net ID after a collision or substitute another discovered candidate.

Explicit local targets remain usable when broad discovery is incomplete,
provided their identity and local association can be verified. Missing or
contradictory identity evidence fails resolution with a diagnostic. The qualified
environment and remaining limits are recorded in the profile verification document.

An existing TwinCAT system may have no PLC application port until managed first
download creates the application; that does not mean the Runtime instance itself
is absent.

Managed deployment uses the prepared Runtime's verified AMS identity, not the
destination saved in the engineering source project. Resolve the PLC application
port from the selected project and reject an explicit conflicting port override.
Retain that effective endpoint for PLC control, UnitTest communication and
reconnect, including after owned engineering access has been released. External
access instead uses the configured PLC port (default `851`) without opening an
engineering project. See [targeting with and without download](../configuration.md#targeting-with-and-without-download).

## System Mode And PLC Availability

Project a positively observed TwinCAT CONFIG mode as unavailable PLC online
access, not as a disappeared Runtime or an ordinary startable PLC STOP state.
Use the existing application-facing state fields:

| Field | Known CONFIG mode |
| --- | --- |
| `InstanceState` | `Available` while the selected TwinCAT system remains reachable. |
| `OperatingState` | `Unavailable`, even though CONFIG stops PLC execution. |
| `ReadyForExecution` | `false`. |
| `ReadyForTransfer` | Reflect the actual managed-transfer prerequisites; CONFIG alone is not a transfer failure. |

Keep profile, project and PLC-control health usable when the corresponding
technical context remains trustworthy. Do not invalidate the whole PLC-control
service merely to disable Start/Stop: its health also gates managed transfer.
The unavailable PLC operating state already blocks those controls. Actual access
errors still require truthful health and availability updates; do not infer
CONFIG solely from an ADS failure or a missing PLC application port.

This mapping uses existing WPF/Core behavior without a new state enum, control,
or public system-mode setting. The operational step and selected target remain
in place. UnitTest commands are unavailable, and the Core releases existing
UnitTest communication when execution readiness is lost. An active run
observation is invalidated; an already completed result is retained.

Keep system-state observation independent of UnitTest communication release.
After observing RUN again, re-establish the selected PLC access and publish its
actual operating state. A loaded, stopped PLC enables Start PLC; a running PLC
allows the existing automatic communication preparation and monitoring path.
Revalidate symbols and the communication contract rather than reuse invalid
handles. Do not resume the interrupted run or issue a new UnitTest start.

Known CONFIG may persist without an arbitrary recovery deadline turning it into
a terminal connection error. Report the specific reason, for example:
`PLC access is unavailable because the TwinCAT system is in CONFIG mode.`
The generic WPF availability display does not need to expose a CONFIG enum.

Discovery, ordinary access preparation, reconnect and Start/Stop PLC do not
implicitly switch the system to RUN. Managed configuration activation retains
its separately defined permission to transition or restart the selected system.
CONFIG alone must not prevent that transfer path or force the user to repeat
the WPF setup steps.

## State Observation

Observe the selected TwinCAT system separately from its PLC application. Use
the system endpoint at ADS port 10000 and the configured PLC application port;
do not substitute XAE online-state properties or the local router's state for
observations of those endpoints.

| Observation | InstanceState | OperatingState | ReadyForExecution |
| --- | --- | --- | --- |
| System CONFIG | Available | Unavailable | false |
| System RUN, PLC STOP | Available | Stopped | false |
| System RUN, PLC RUN | Available | Running | true when runtime access prerequisites are satisfied |
| System reachable, selected PLC application absent | Available | Unavailable | false |

Without reliable current access evidence, do not claim that execution is ready.
Represent uncertainty or unavailability through the existing state and health
models; a read failure is not an observed PLC STOP or system CONFIG state.

Use ADS state notifications for PLC STOP/RUN changes, together with an initial
state read during preparation and a state observation after an explicit PLC
command. Independently monitor system CONFIG/RUN by periodic state reads for
the lifetime of the prepared runtime context, including while the PLC is stopped
or absent and while UnitTest communication is released. The system interval is
an internal implementation setting, not the UnitTest monitoring interval or an
additional public configuration option.

ADS V7 documents `AdsClient.AdsStateChanged` for notification-capable ports such
as 851, but not system port 10000. `RouterStateChanged` concerns the local AMS
router independently of the selected target address; it is not a substitute
for the selected Usermode system's CONFIG/RUN state. See Beckhoff's
[ADS state event](https://infosys.beckhoff.com/content/1033/tc3_ads.net/9407905547.html)
and [router state event](https://infosys.beckhoff.com/content/1033/tc3_ads.net/9407909643.html)
references. Notification registration and re-establishment require qualification
with the installed ADS V7 dependency and Usermode Runtime.

A suitable ADS error from a data poll should bring the system-state check
forward instead of waiting for its regular interval. The error triggers that
check; it does not itself prove CONFIG. Publish confirmed unavailability promptly
through the existing state/health model. Repeated failures must not create
concurrent or unbounded immediate checks, duplicate health transitions, or a
diagnostic for every poll. Keep the initial failure, meaningful changes and
recovery visible. Qualify the applicable error codes rather than treating every
read error as a system-state change. Regular monitoring remains responsible for
independent state detection and recovery observation.

## Session Ownership And System Readiness

The runtime component owns separate ADS sessions for the selected system service
and PLC application endpoints. Keep connection health, system mode/start
completion, and PLC application state distinct. A standalone client's
`IsConnected` is not evidence that the target is ready; a session connection
transition alone is not confirmation of system RUN or a loaded PLC application.

The runtime observation context publishes immutable system observations with a
context identity, increasing local observation sequence, PC observation time,
actual system state and the system-service restart completion counter. Failed
reads and connection loss replace the confirmed value with an unavailable
observation; they do not carry an old RUN forward. Publication rejects a read
started before a newer invalidation. PLC application read/registration failures
do not by themselves erase a successfully confirmed system observation.

The wait implementation neither acquires runtime operation locks nor holds the
ADS IO lock. An exclusive transfer retains its operation gate to prevent competing
commands/release, while the observation loop runs independently of that gate.
A wait requires evidence newer than its baseline in
the same context. Restart waits additionally require a changed nonzero completion
counter; counter wrap is supported, not treated as a strictly increasing value.
Ordinary RUN waits do not require a restart. Timeout/cancellation terminates only
the caller's wait, not ongoing system monitoring. Context release wakes and fails
pending waits; observations from a replacement context cannot satisfy them.

Keep the system observer alive throughout runtime preparation, managed transfer,
CONFIG intervals and temporary communication loss until the prepared context is
released or replaced. PLC/UnitTest access may be invalidated independently. The
profile-internal transfer coordinator consumes this observer; the engineering
adapter does not own a competing ADS state loop. Existing application-facing
services and the integrated profile boundary remain unchanged.

After a requested system restart, require a fresh successful system-service
observation through the connected session, RUN, and a changed nonzero restart
completion counter relative to the pre-operation baseline. A read failure or
replaced context must not preserve an old RUN as currently confirmed. Ordinary
external access without a requested restart needs fresh current RUN evidence,
not an invented requirement to observe a restart first.

Retain the vendor session recovery behavior; do not replace it with a fixed sleep
or shorten it to optimize transfer duration. Readiness waits must be bounded and
cancellable; an unsuccessful wait must not dispatch Login. These operation
deadlines do not terminate continuous monitoring merely because the operator
leaves the system in CONFIG.

PLC application absence must not block the system-readiness gate for first
download. Conversely, confirmed system RUN does not imply that the selected PLC
application exists. External access must remain unavailable until that application
is actually accessible; it must not perform an implicit download.

On system-session `Lost`, or a successfully observed system state other than RUN,
explicitly disconnect the PLC session. Retain the system session and observation
loop. Loss callbacks only latch the interruption and wake serialized observation;
they do not call vendor connection operations. Reject PLC access until a fresh
successful system RUN observation is available. A loss during that observation
invalidates it. Disconnect once per interruption, then connect the same PLC
session after confirmation. PLC STOP while the system remains RUN is not a
reason to disconnect.

An isolated PLC-session `Lost` also revokes PLC access and wakes serialized
observation, but does not invalidate the system observation or imply CONFIG.
Recover only the affected PLC connection through Disconnect/Connect, with a
fresh system RUN check before PLC access. Suppress loss callbacks caused by
the explicit disconnect itself; obsolete session callbacks cannot invalidate
a replacement context.

Known system or PLC-session loss immediately blocks new UnitTest data access,
including through previously issued endpoints. Re-enable it only after fresh
system and PLC readiness verification for the current interruption. An older
in-flight observation cannot restore access. Batches already released for PC
processing are not discarded by this gate.

Register the PLC state handler initially only when the application endpoint is
available. Preserve the successfully registered handler across Disconnect/Connect;
do not unregister/register on each mode transition. An absent application or a
failed initial registration must not be treated as a working subscription. Final
context cleanup removes the handler and disposes both sessions, even if cleanup
reports a failure. Keep obsolete callbacks isolated from replacement contexts.
Repeated connection attempts on an unavailable PLC port must not become repeated
application diagnostics. Explicit application reconnect rebuilds the technical
context while retaining the selected endpoint; automatic mode recovery does not
change the target or issue PLC commands/downloads.

This is the intended integration model. The qualification scope and distinction
between temporary probes and production integration are recorded in the profile's
[verification document](../verification.md).

## Reconnect And Technical Release

Reconnect retains the selected AMS identity, PLC application port, provisioning
strategy and resource ownership. Rebuild the dependent technical ADS access and
revalidate the same target; do not rediscover a replacement, change system mode,
start the PLC, download or issue a UnitTest command. Re-obtain invalidated
symbols and registrations rather than replay a failed write.

A verified, reachable system in CONFIG is a valid reconnect result with PLC
access unavailable. Reconnect does not wait for an operator to select RUN.
Technical access deadlines must not become a deadline for leaving CONFIG.

Technical release stops owned observation work and removes subscriptions before
disposing the corresponding clients. Late callbacks or reads must not publish
into a disposed or replaced context. Preserve the Runtime process, licenses,
PLC program and system mode. This is distinct from releasing only UnitTest
communication, which must leave runtime system observation active.

## Unsupported Actual Provisioning

`CreateOwned` is `NotSupported` under either internal strategy. With
`CreateDedicated`, actual `DestroyResource` is also `NotSupported` until a
supported implementation is explicitly introduced. Do not create an unlicensed instance or attempt destructive work as
a fallback. `UseExisting` access and its `KeepResource` cleanup remain
non-provisioning operations.

Never infer permission to destroy a pre-existing Runtime from this strategy.
Future actual provisioning must retain concrete ownership evidence and provide
its matching cleanup; this document does not approve that future implementation.

## Verification

Test selection and cleanup through the application contract:

- Successful prepare returns the real target and does not create an instance.
- `CreateOwned` never opens ADS access or reuses an existing instance.
- Both release intents preserve the instance and licenses and close owned access.
- `Succeeded` plus `ResourceKept` completes Core cleanup and does not produce a
  CLI cleanup-failure exit code.
- Missing/ambiguous defaults, failed initialization and technical cleanup failure
  remain visible; no false success or automatic alternate target is allowed.
- Reserved actual-provisioning operations return `NotSupported` without effects.
- Reconnect keeps the prepared target, strategy and ownership evidence.

Verify managed target selection with a source project naming a different Runtime:

- Activation and download target only the verified prepared Runtime, never the
  developer's saved Net ID or an incidental discovery candidate.
- A selected project port other than `851` is used consistently for download,
  PLC control, communication and reconnect after engineering release.
- An explicit conflicting port fails before transfer; omission is not an override.
- External/no-download access does not open or retarget an engineering project.

Qualify the system-mode mapping against both existing consumer paths:

- An external session can reach the operational step while the selected system
  is in CONFIG, with PLC and UnitTest commands blocked.
- RUN to CONFIG during debugging retains the selected target and blocks commands;
  an active run is invalidated, but a completed result is preserved.
- CONFIG to RUN with a stopped PLC enables only the applicable PLC controls;
  with a running PLC, communication validation and monitoring resume automatically.
- A prolonged CONFIG interval does not cause terminal recovery failure, and
  reconnect during CONFIG neither switches system mode nor downloads a project.
- Managed transfer remains possible from CONFIG, including first download with
  no existing PLC application. Missing prerequisites and real access failures are
  not hidden by the CONFIG mapping.
- PLC notifications are restored after access re-establishment; selected-system
  observation continues independently of PLC-port availability.
- Relevant data-read errors can trigger an earlier system check, without false
  CONFIG attribution, repeated equivalent diagnostics or competing check loops.
- Cancellation, reconnect and release prevent obsolete observations from being
  published into a new context and preserve the operator-provided Runtime.

Normal automated coverage uses test doubles. Live verification requires an
explicitly authorized target and must not remove an operator-provided Runtime.
