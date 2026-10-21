# Diagnostics Model Architecture

This document defines the shared diagnostic model contract and application
consumption rules for `PLCSIM.UnitTest`.

Diagnostics explain why the application, shared core, access profile, functional
services, run observation, result interpretation, or output handling reached a
specific state. They do not define the workflow, result, artifact, JUnit, or
exit-code semantics themselves.

Diagnostics are support state for users, operators, tests, and troubleshooting.
They are not the authority for workflow gates, UnitTest run result,
artifact/output state, or CLI exit-code selection. Those decisions come from the
accepted workflow, action-gate, run-epoch, result, output, and process outcome
models. Diagnostics explain those decisions and preserve useful context.

## Diagnostic Ownership

Diagnostics are produced at the layer that owns the failing or noteworthy
condition and are consumed by higher layers without leaking implementation
details.

| Owner | Diagnostic Responsibility |
| --- | --- |
| Access profile loader | Reports profile discovery, loadability, manifest, dependency, and profile initialization problems. |
| Access profile | Reports profile-level validation, service binding, compatibility, and profile-specific target interpretation problems. |
| Functional services | Report operation-specific diagnostics for project, PLC control, UnitTest lifecycle, and UnitTest event-stream access. |
| Shared core | Correlates diagnostics with workflow state, action gates, run observation, result interpretation, output coordination, and process outcome categories. |
| WPF application | Presents user-actionable diagnostics, detailed context, recovery options, and log/artifact access for manual operation. |
| CLI application | Emits concise diagnostics, process category, exit code, and produced log/artifact paths for unattended execution. |
| Output writers/exporters | Report output coordination diagnostics for requested artifacts, including skipped, written, or failed outputs. |

Diagnostics should move upward as stable application-facing facts. Lower layers
may include technical detail and evidence references, but they must not require
WPF, CLI, or shared core consumers to understand Siemens API object models,
vendor exception types, raw PLC constants, file handles, or implementation-only
state.

## Diagnostic Principles

Diagnostics should be stable enough for UI presentation, CLI automation, logs,
and tests.

- Diagnostics describe an observed condition, not a recovery decision.
- Recovery decisions belong to workflow/action gates or user/application policy.
- The layer that detects a condition creates the diagnostic closest to the
  source.
- The shared core may aggregate, correlate, deduplicate, and map diagnostics to
  accepted application states.
- WPF and CLI consume diagnostics; they do not reinterpret vendor-specific
  exceptions or functional-service internals.
- A diagnostic should be useful without requiring the application log, but the
  log may contain deeper technical detail.
- Repeated equivalent low-level failures should be coalesced into stable
  diagnostic state instead of causing uncontrolled message streams.
- Diagnostic categories and codes should be deterministic enough for tests and
  support tooling, while messages may remain human-oriented.
- Diagnostics may refer to workflow, run, evidence, result, or output state, but
  they must not redefine those semantics.

## Emissions, Current Snapshot, And History

The diagnostic model distinguishes between a diagnostic emission and the current
diagnostic snapshot.

A diagnostic emission is one observation from a producer at one point in time.
Examples include one failed project operation, one event-stream framework event,
one artifact-open failure, or one cleanup failure.

The shared diagnostic sink turns emissions into a current coalesced diagnostic
snapshot. The snapshot is what WPF and CLI normally present as current
diagnostic help. It is not a chronological event log.

Chronological history belongs to other surfaces:

- the terminal-style output stream may show concise user-facing event lines,
- application logs record diagnostic emissions and technical runtime context,
- event-stream and run-epoch evidence carry UnitTest run evidence,
- output/artifact state carries artifact generation status.

Repeated equivalent diagnostic emissions should update one diagnostic entry in
the current snapshot. The entry keeps `FirstSeenAt`, `LastSeenAt`, and
`OccurrenceCount` so the user can recognize repeated/current problems without
being shown indistinguishable duplicate rows.

Clearing diagnostics clears the shared current diagnostic snapshot. It does not
erase application logs, terminal output history, event-stream/run evidence,
artifact state, workflow state, or action-gate state.

## Diagnostic Reporting Versus Logging

Diagnostic reporting and logging are related but not interchangeable.

Diagnostic reporting is the structured, application-facing model used by WPF,
CLI, shared core, tests, output coordination, and later support tooling. It
contains stable facts such as category, log level, source, phase, correlation,
application-facing message, technical detail, and evidence references.
Diagnostics may be included in published state and process summaries, but
workflow gates, run result, artifact state, and exit-code decisions are not
derived from diagnostic message text or from the mere presence of diagnostics.

Logging is the persisted technical runtime record. Logs may contain diagnostic
entries, exception details, timestamps, retries, timing, internal decisions,
trace/debug information, and additional technical context. Logs are useful for
support and troubleshooting, but application behavior must not depend on parsing
log text.

The boundary rules are:

- diagnostics define application-facing diagnostic semantics,
- logs record diagnostics and additional technical runtime context,
- WPF and CLI consume diagnostic models and workflow state, not log text,
- WPF and CLI use diagnostic snapshots as support information, not as the
  authoritative source for workflow or result decisions,
- output/result/exit-code behavior is not derived from log parsing,
- log formatting and retention are application configuration concerns,
- diagnostic category/code stability is more important than log message wording
  stability.

## Minimum Diagnostic Model

Every diagnostic entry should follow one common minimum shape so WPF, CLI,
shared core, tests, and output coordination can consume diagnostics
consistently.

This is a conceptual minimum model, not a final C# DTO definition. Specific
implementations may add fields, wrappers, generated ids, timestamps, or
transport-specific metadata as long as the application-facing minimum remains
available.

| Field | Required | Purpose |
| --- | --- | --- |
| `Id` | No | Optional owner-side diagnostic identifier when the producer can provide one without central coordination. Consumers must not require plugins/services to request ids from a central owner before reporting diagnostics. |
| `Code` | Yes | Stable diagnostic code for tests, filtering, documentation, and support. |
| `Category` | Yes | Coarse diagnostic category used for application state and process outcome mapping. |
| `Level` | Yes | `Microsoft.Extensions.Logging.LogLevel` value describing application-facing diagnostic impact. |
| `Source` | Yes | Producer layer/component that detected or created the diagnostic. |
| `Phase` | Yes | Workflow, operation, or output phase where the diagnostic belongs. |
| `Message` | Yes | Short user-facing or operator-facing summary. |
| `TechnicalDetail` | No | Technical detail for logs, troubleshooting, and support. |
| `Correlation` | Yes | Identifiers that connect the diagnostic to workflow/run/service/output context. |
| `EvidenceReference` | No | Reference to event, lifecycle observation, operation result, artifact path, exception, or evidence snapshot. |
| `Timestamp` | Yes | Time when the diagnostic was created. |

The coalesced snapshot wraps diagnostics in `DiagnosticEntry` values. An entry
has a stable fingerprint, the latest diagnostic emission as `Current`, the time
it was first seen, the time it was last seen, and an occurrence count. UI and CLI
presentation should use the entry metadata when showing current diagnostics.

The envelope should be small enough for normal application flow but complete
enough that callers do not need to inspect exceptions, vendor objects, or logs
to decide how to present and classify the condition.

Suggested recovery actions are not part of the minimum diagnostic model.
Recovery availability and safety belong to workflow/action gates and application
policy. A diagnostic may explain why an action is blocked or unsafe, but it does
not define which recovery action must be executed.

### Diagnostic Code

Diagnostic codes should be stable identifiers, not localized messages. They
should be scoped by diagnostic source or domain so new codes can be added
without renaming existing ones.

Examples:

- `Profile.Load.DependencyMissing`
- `Project.Open.Failed`
- `Runtime.Start.Timeout`
- `UnitTestLifecycle.Ready.NotAvailable`
- `EventStream.Context.Invalidated`
- `Result.Evidence.Ambiguous`
- `Output.Artifact.WriteFailed`

Codes do not need to encode every data value. Variable details belong in
technical details, evidence references, or structured context fields.

## Categories And Log Level

Diagnostic categories are intentionally coarse. They should describe the
application-facing problem area, not the exact implementation exception.

| Category | Meaning |
| --- | --- |
| `Configuration` | The run setup, CLI arguments, config file, or user selection is invalid, incomplete, or inconsistent. |
| `Environment` | Required software, access profile, dependency, project source, runtime target, permission, or service binding is unavailable or unusable. |
| `Workflow` | Workflow/action-gate/run-cycle execution reached an invalid, blocked, cancelled, interrupted, or structurally failed state. |
| `Communication` | Lifecycle or event-stream communication is unavailable, invalidated, inconsistent, lossy, or unclear during operation. |
| `Result` | Result interpretation found incomplete, ambiguous, inconsistent, or failed UnitTest evidence. |
| `Output` | Requested log/artifact/output coordination state is skipped, blocked, or failed. |
| `Cleanup` | Cleanup, release, unload, or shutdown handling failed, was unsafe, or left manual recovery work. |
| `Internal` | Unexpected application or implementation failure outside the known categories. |

Diagnostics should use `Microsoft.Extensions.Logging.LogLevel` instead of a
project-specific severity enum. The level describes application-facing
diagnostic impact. It should not be treated as a complete replacement for
category, source, phase, correlation, or accepted workflow/result/output state.

| Log Level | Diagnostic Reporting Meaning |
| --- | --- |
| `Information` | Informational condition that is useful at application level and does not block the workflow. Use sparingly; routine flow belongs to logging. |
| `Warning` | Degraded, unusual, stale, skipped, or recoverable condition that does not by itself make the current outcome failed. |
| `Error` | Operation, result, output, or workflow problem that prevents the requested action or a clean successful outcome. |
| `Critical` | Unrecoverable or unexpected condition that requires command/application termination or profile/session rebuild. |

`Trace` and `Debug` are logging levels and should normally not be used for
application-facing diagnostic reporting. `None` is not a valid diagnostic
reporting level. Application behavior should not depend only on log level. State
mapping should use category, phase, source, correlation, and accepted
workflow/result/output state as well.

## Source And Phase

`Source` identifies who produced or owns the diagnostic. It should stay stable
at architecture level and should not expose concrete vendor class names.

| Source | Typical Producer |
| --- | --- |
| `AccessProfileLoader` | Profile discovery, loadability, manifest, and initialization. |
| `AccessProfile` | Profile-level compatibility, service binding, and target interpretation. |
| `PlcProjectService` | Engineering project open, prepare, compile/check, and transfer operations. |
| `PlcControlService` | Runtime target discovery, provisioning, start, stop, reset, and runtime state. |
| `UnitTestLifecycleService` | RunControl readiness, start, abort, reset, lifecycle observations, and lifecycle context validity. |
| `UnitTestEventStreamService` | Event-stream context, event delivery, acknowledgement/release, ordering, and stream validity. |
| `SharedWorkflow` | Workflow phase/action-gate decisions, orchestration, stabilization, and cleanup coordination. |
| `RunObservation` | Epoch membership, stale evidence, ambiguous evidence, and completion observation. |
| `ResultInterpretation` | UnitTest log/result model interpretation and aggregation consistency. |
| `OutputCoordinator` | Artifact requests, output state, writer/exporter coordination, and output diagnostics. |
| `WpfApplication` | WPF-only presentation or manual interaction diagnostics. |
| `CliApplication` | CLI-only command setup, cancellation, and command-level diagnostics. |

`Phase` identifies where the condition belongs in the workflow or operation
lifecycle. It should be specific enough for filtering and presentation, but not
so specific that each implementation detail needs a new phase.

Common phases:

- `Configuration`
- `ProfileValidation`
- `ProjectPreparation`
- `RuntimeProvisioning`
- `ProjectTransfer`
- `RuntimeStartup`
- `CommunicationValidation`
- `RunStart`
- `ExecutionMonitoring`
- `CompletionStabilization`
- `ResultInterpretation`
- `OutputCoordination`
- `Cleanup`
- `ApplicationShutdown`

Service-specific operation phases may refine these common phases inside the
diagnostic context, but the common phase should still be present for WPF/CLI
presentation and process mapping.

## Correlation And Evidence References

Diagnostics should be correlated to the smallest stable context that explains
their impact.

Correlation may include:

- application session id,
- workflow/run request id,
- active operation id,
- access profile id,
- service/source id,
- project context id,
- runtime target id,
- UnitTest run epoch id,
- lifecycle command/observation id,
- event-stream context id,
- evidence snapshot id,
- output/artifact request id.

Correlation fields should be optional individually but collected under one
structured correlation object. A diagnostic must contain enough correlation to
decide whether it belongs to the active workflow/run/output state or only to
historical context.

Evidence references connect diagnostics to supporting facts without embedding
large payloads directly into the diagnostic entry. They may point to:

- lifecycle observation,
- event-stream entry or range,
- event-stream snapshot,
- operation result,
- output path,
- exception reference stored in logs,
- communication evidence export entry,
- result model node.

Evidence references are diagnostic aids. They do not replace the normalized
workflow, result, or output state.

## Conceptual Model Sketch

The following sketch illustrates the intended model shape. It is not a final C#
API, does not define namespaces, and should not be treated as an implementation
contract. Implementation design may adjust names, split records, use value
objects, or add transport-specific metadata as long as the accepted diagnostic
semantics remain available.

```csharp
public sealed record Diagnostic(
    string Code,
    DiagnosticCategory Category,
    Microsoft.Extensions.Logging.LogLevel Level,
    DiagnosticSource Source,
    DiagnosticPhase Phase,
    string Message,
    DiagnosticCorrelation Correlation,
    DateTimeOffset Timestamp,
    string? Id = null,
    string? TechnicalDetail = null,
    DiagnosticEvidenceReference? EvidenceReference = null);

public sealed record DiagnosticFingerprint(
    string Code,
    DiagnosticCategory Category,
    DiagnosticSource Source,
    DiagnosticPhase Phase,
    DiagnosticCorrelationScope CorrelationScope);

public sealed record DiagnosticEntry(
    DiagnosticFingerprint Fingerprint,
    Diagnostic Current,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int OccurrenceCount);

public sealed record DiagnosticSnapshot(
    IReadOnlyList<DiagnosticEntry> Entries,
    DateTimeOffset UpdatedAt);

public enum DiagnosticCategory
{
    Configuration,
    Environment,
    Workflow,
    Communication,
    Result,
    Output,
    Cleanup,
    Internal
}

public enum DiagnosticSource
{
    AccessProfileLoader,
    AccessProfile,
    PlcProjectService,
    PlcControlService,
    UnitTestLifecycleService,
    UnitTestEventStreamService,
    SharedWorkflow,
    RunObservation,
    ResultInterpretation,
    OutputCoordinator,
    WpfApplication,
    CliApplication
}

public sealed record DiagnosticCorrelation(
    string? SessionId = null,
    string? WorkflowRunId = null,
    string? OperationId = null,
    string? AccessProfileId = null,
    string? ServiceId = null,
    string? ProjectContextId = null,
    string? RuntimeTargetId = null,
    string? UnitTestRunEpochId = null,
    string? LifecycleObservationId = null,
    string? EventStreamContextId = null,
    string? EvidenceSnapshotId = null,
    string? OutputRequestId = null);

public sealed record DiagnosticCorrelationScope(
    string? SessionId = null,
    string? WorkflowRunId = null,
    string? OperationId = null,
    string? AccessProfileId = null,
    string? ServiceId = null,
    string? ProjectContextId = null,
    string? RuntimeTargetId = null,
    string? UnitTestRunEpochId = null,
    string? LifecycleObservationId = null,
    string? EventStreamContextId = null,
    string? EvidenceSnapshotId = null,
    string? OutputRequestId = null);

public sealed record DiagnosticEvidenceReference(
    string Kind,
    string Reference);
```

Diagnostic fingerprint identity:

- `Code`, `Category`, `Source`, and `Phase` are always identity fields.
- Stable structured correlation values are identity fields when they describe
  the affected access profile, service, prepared project context, or runtime
  target.
- Volatile correlation values such as application session, workflow run,
  operation, UnitTest run epoch, lifecycle observation, event-stream context,
  evidence snapshot, and output request remain diagnostic detail context, but
  they do not split coalesced diagnostic entries.
- `Message`, `TechnicalDetail`, `EvidenceReference`, `Timestamp`, and optional
  emission `Id` are occurrence data, not identity fields.
- Producers that need broader or narrower coalescing should make that explicit
  through structured correlation, not through message text.

## User-Facing And Technical Detail

Diagnostics should separate the text that is useful for a user/operator from
technical detail that is useful for troubleshooting.

The minimum model contains:

- `Message` for a short application-facing summary,
- `TechnicalDetail` for optional troubleshooting detail,
- `EvidenceReference` for optional links to supporting evidence,
- `Category`, `Level`, `Source`, `Phase`, and `Correlation` for structured
  state mapping and filtering.

`Message` should be understandable and concrete for the expected user/operator.
It should not be artificially abstracted away from the actual access profile or
PLC context. Vendor terms, Siemens API names, PLC-side names, and PLC constant
values may be used when they make the diagnostic clearer. Prefer textual
meanings and domain names over raw numeric values, or include both when the raw
value is useful for troubleshooting.

The message should still avoid unnecessary implementation noise such as stack
traces, internal object handles, or raw exception dumps. It should state what
failed or what condition was observed.

Examples:

- "The Siemens access profile cannot load the TIA Openness API."
- "The configured PLCSIM Advanced instance is not available."
- "UnitTest lifecycle communication is no longer trusted."
- "The UnitTest event stream contains ambiguous evidence for the current run."
- "The requested test result artifact could not be written."

`TechnicalDetail` may include concise structured technical context such as API
name, operation name, target identifier, resolved path, symbol name, native
error code/message, retry outcome, or access-profile-specific context. It is
primarily for troubleshooting views, support, and developer diagnostics.

Full stack traces, raw exception dumps, and large low-level payloads belong to
logging, not to the diagnostic model. A diagnostic may reference log or evidence
locations where deeper troubleshooting information is available.

Technical detail is optional in the minimum model, but it should be supplied
when the message alone would either become too vague or too long. Technical
detail must not be required for normal WPF command availability, CLI exit-code
selection, output coordination, or result interpretation. Those decisions use
structured diagnostic fields and accepted workflow/result/output state.

Diagnostics should not contain free-text recovery instructions as the primary
recovery model. Recovery options are derived from workflow/action gates and
application policy. A diagnostic can explain why an action is blocked, unsafe,
or failed; the application decides which recovery commands are available.

WPF may show the `Message` directly and make technical detail available through
expanded detail, terminal-style output, log access, or evidence/artifact links.
CLI may print the `Message` and process category in its normal output and write
technical detail to the application log or verbose output mode.

When technical detail contains sensitive local paths, project names, or
environment details, the application may decide how much of it is shown in
normal UI/console output. The diagnostic model should preserve enough detail for
logs and troubleshooting unless a concrete privacy/security reason requires
redaction.

## Diagnostic Mapping Overview

Diagnostics support state mapping; they do not replace the accepted state
models. The shared core maps diagnostics together with workflow state, result
state, output coordination state, cancellation/timeout policy, and context trust.

The category gives the first coarse mapping direction:

| Category | Typical State Impact | Typical CLI Process Category |
| --- | --- | --- |
| `Configuration` | Run setup is incomplete, invalid, or cannot be normalized; preparation does not start or stops at configuration gate. | Configuration Error |
| `Environment` | Required access profile, external installation, service binding, project source, runtime target, or permission is unavailable. | Environment Error |
| `Workflow` | Action gate is blocked/unsafe, workflow operation failed, run-cycle state is invalid, run is interrupted, or no trusted final result can be produced. | Workflow Error, Cancelled, or Timeout depending on policy and terminal cause. |
| `Communication` | Lifecycle or event-stream context is unavailable, invalidated, lossy, stale, or ambiguous. | Environment Error when detected during setup; Workflow Error or Timeout during execution. |
| `Result` | Result interpretation reports failed, error, incomplete, ambiguous, or inconsistent UnitTest evidence. | Test Failure for trusted concrete failed/error testcase results; Workflow Error for incomplete/ambiguous/untrusted evidence. |
| `Output` | Requested output/artifact was skipped, blocked, pending too long, or failed. | Artifact Error when required output fails after a trusted execution/result state exists. |
| `Cleanup` | Cleanup or resource release failed, was unsafe, or left manual recovery work. | Workflow Error, Cancelled, Timeout, or Success with warning depending on command policy and whether requested work already completed. |
| `Internal` | Unexpected application failure outside known categories. | Unexpected Internal Error |

Log level influences presentation and aggregation, but it is not a complete state
mapping rule by itself:

- `Information` should not change workflow, result, output, or process outcome by
  itself.
- `Warning` may make WPF/CLI show degraded state or recovery guidance, but does
  not automatically fail the command.
- `Error` normally blocks an operation, prevents a clean successful outcome, or
  marks requested output as failed.
- `Critical` normally invalidates the active context, requires session/profile
  rebuild or application termination, and maps to an internal or workflow error
  unless a more specific category is available.

Mapping should prefer accepted state over diagnostic text:

- workflow/action-gate state decides whether an action is available, blocked,
  pending, unsafe, or not applicable,
- run observation and result interpretation decide whether a run is trusted,
  complete, failed, error, incomplete, ambiguous, or inconsistent,
- output coordination decides whether an artifact is not requested, pending,
  written, skipped, or failed,
- CLI process outcome decides the final exit code.

Diagnostics explain those decisions and provide stable category/source/phase/code
information for presentation, logs, tests, and support.

When several diagnostics exist, aggregation should keep all relevant entries but
choose the terminal process category from the accepted precedence rules in the
application-layer architecture. Diagnostic log level can help choose what to show
first, but it should not override a more specific workflow/result/output state.

## Application Consumption

Diagnostics are consumed differently by shared core, WPF, and CLI. All three
consume the same diagnostic model but use it for different presentation and
execution needs.

### Shared Core Consumption

The shared core uses diagnostics as structured input for application state,
coordination, and testing.

Shared core responsibilities:

- collect diagnostics from access profile loader, access profile, functional
  services, run observation, result interpretation, and output coordination,
- correlate diagnostics to workflow context, active operation, run epoch,
  service context, evidence, and output requests,
- deduplicate or aggregate repeated equivalent diagnostics where needed,
- map diagnostics together with accepted workflow/result/output state to action
  gate explanations, process outcome candidates, and output coordination state,
- expose diagnostics to WPF and CLI without leaking vendor exception objects or
  concrete API handles,
- preserve enough technical context and evidence references for logs,
  troubleshooting, and tests.

The shared core should not parse log text and should not infer behavior from
free-text diagnostic messages. It should use structured fields such as category,
level, source, phase, code, correlation, and accepted workflow/result/output
state.

### WPF Consumption

WPF is the primary manual diagnostic surface. It should present diagnostics in a
way that supports local operation, troubleshooting, and real-runtime validation.

WPF should use diagnostics for:

- phase/status messages,
- command availability explanations,
- blocked/unsafe action explanations,
- recovery context,
- result and event evidence warnings/errors,
- output/artifact status,
- terminal-style output,
- log and artifact access.

WPF should show the most relevant `Message` values near the affected workflow
area. It may expose category, level, source, phase, technical detail, and
evidence references in expanded details, terminal-style output, or log access.

WPF should not require the user to read logs for normal operation. Logs are for
deeper troubleshooting. If WPF cannot present enough detail inline, it should
provide a clear path to the application log or produced diagnostic artifact.

WPF must not decide recovery availability from diagnostic text. Recovery buttons
and command states come from workflow/action gates. Diagnostics explain why a
command is blocked, unsafe, failed, or recommended for attention.

### CLI Consumption

CLI consumes diagnostics for unattended execution, CI/CD feedback, and concise
operator output.

CLI should use diagnostics for:

- final command summary,
- process category and exit-code explanation,
- concise failure/error output,
- optional verbose output,
- application log context,
- produced artifact/log path reporting.

CLI normal output should stay concise. It should include the process category,
exit code, most relevant diagnostic messages, and produced artifact/log paths
where known. Detailed technical context should go to the application log or
verbose output mode.

Machine consumers should use exit code and file artifacts, not parse CLI
diagnostic text. Diagnostic codes and categories may still be useful for tests,
support tooling, and optional structured CLI output in the future.

CLI must not derive exit codes from message text. It uses the accepted process
outcome mapping together with structured diagnostics and workflow/result/output
state.

## Boundary To Accepted Semantics

Diagnostics explain and annotate already accepted states:

- workflow phases, action gates, recovery boundaries, and run-cycle behavior are
  defined in the workflow and run-cycle architecture documents,
- run observation, epoch handling, result interpretation, and
  ambiguous/incomplete evidence are defined in the event-stream processing and
  run-cycle architecture documents,
- output/artifact, JUnit exporter, naming/overwrite, and output coordination
  semantics are defined in the application-layer architecture,
- CLI exit-code categories and CI/CD signal behavior are defined in the
  application-layer architecture.

This document only defines how diagnostic information is carried, correlated,
presented, and persisted across those accepted concepts.
