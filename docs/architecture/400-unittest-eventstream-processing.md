# UnitTest EventStream Processing Architecture

This document defines how `PLCSIM.UnitTest` processes UnitTest event-stream
evidence into observed runs and neutral result meaning. It focuses on the shared
core behavior above the functional communication services.

The PLC-owned event-stream contract remains the source of truth for event model
semantics. This document describes how the application consumes evidence from
that contract; it does not redefine PLC-owned constants, data blocks, or producer
behavior.

## Purpose

UnitTest EventStream Processing turns event-stream evidence assigned to a
monitored test-run epoch into:

- observed UnitTest run evidence,
- event assignment to observed runs,
- stale, ambiguous, lost, or incomplete evidence classification,
- capture completeness state,
- neutral run, suite, and test-case result meaning,
- structured input for diagnostics and output/artifact generation.

EventStream Processing is shared behavior. WPF and CLI consume the same observed
run and result meaning. Access profiles and functional services only provide
evidence.

## Layer Placement And Scope

EventStream Processing belongs to the shared core. It is application-facing
shared behavior above `IUnitTestLifecycleService` and
`IUnitTestEventStreamService`.

The access profile layer does not decide event-to-run assignment, capture
completeness, evidence consistency, or final UnitTest result meaning. It only
delivers normalized lifecycle evidence, event-stream evidence, communication
state, acknowledgement/release outcomes, and diagnostics.

EventStream Processing is also separate from TestRun Monitoring:

- `TestRunMonitor` owns the active monitoring cycle for a UnitTest run,
  including when monitoring starts, continues, stops, times out, aborts, resets,
  or is invalidated.
- EventStream Processing detects the exact RunControl event anchors that open
  and close a run epoch and materializes the assigned evidence as
  `UnitTestRunEpoch`.
- `TestRunMonitor` consumes the resulting epoch snapshots and independently
  monitors command reactions, active-run duration, final-evidence waiting,
  reset, timeout, and invalidation. It does not create an epoch from polled
  lifecycle projections.
- EventStream Processing does not start/stop the PLC, issue RunControl
  commands, manage user actions, own timeout policy, or own recovery workflows.

## Ownership Boundaries

| Area | Owner | Responsibility |
| --- | --- | --- |
| PLC-owned event production | PLC-side UnitTest contract. | Produce event-stream data and lifecycle communication data according to the PLC-owned contracts. |
| Lifecycle communication access | `IUnitTestLifecycleService`. | Provide RunControl command outcomes, readiness/progress/status observations, PLC-side lifecycle completion evidence, lifecycle context validity, and diagnostics. |
| Event-stream evidence access | `IUnitTestEventStreamService`. | Validate event-stream access context, read event-buffer evidence, expose ordering/epoch/completeness evidence, handle release/acknowledgement where required, and report event-stream diagnostics. |
| EventStream Processing | Shared core. | Maintain the active `UnitTestRunEpoch` event stream and owner tree, classify stale/ambiguous/incomplete event evidence, evaluate event evidence quality, and derive neutral result meaning. |
| WPF application | WPF layer. | Present observed run state, action availability, diagnostics, and result meaning for manual operation. |
| CLI application | CLI layer. | Drive unattended execution, report observed run/result state, and hand neutral results to output/artifact generation and process outcome mapping. |
| Output/artifact generation | Output/artifact concern. | Convert neutral result/evidence models into concrete files and report formats. |

## Terminology

| Term | Meaning |
| --- | --- |
| Lifecycle evidence | Application-facing facts from `IUnitTestLifecycleService`: command transport outcomes plus independently published coarse execution status, execution signals, detailed RunControl state, and producer result. This evidence supports readiness, reaction monitoring, plausibility, and diagnostics but does not replace event-stream result evidence. |
| Event-stream evidence | Application-facing facts from `IUnitTestEventStreamService`, including delivered event entries, ordering/sequence information, epoch/context information, completion-anchor evidence, event-buffer empty observations, lost-event evidence, and stream diagnostics. |
| Observed run | A run instance monitored by `TestRunMonitor` and materialized as `UnitTestRunEpoch` by EventStream Processing from assigned event-stream evidence. |
| Application-triggered run | An observed run where the application requested lifecycle start. The lifecycle service owns the bounded transport/API-write call; Shared Core monitors later PLC reaction independently. The monitored run evidence after the event-stream active boundary follows the same model as external observation. |
| PLC-side-started run | An observed run that was started outside the application and is detected through lifecycle and event-stream evidence. No lifecycle start command is monitored by the workflow. |
| Observation boundary | The exact RunControl start anchor in the delivered event stream: `OwnerScope = RunControl`, `EventType = StateTransition`, and `State = Starting`. |
| Observation baseline | The communication-session sequence baseline established before event processing starts. It filters stale transport evidence but does not create a run epoch. |
| Run epoch | The inclusive event-sequence interval from the exact RunControl `Starting` anchor through the corresponding RunControl `Completed` anchor. EventStream Processing owns this assignment. |
| Run membership | The decision that an event-stream entry belongs to one observed run. Event IDs classify event meaning, but they do not decide membership by themselves. |
| Stale evidence | Event-stream evidence that predates the observation boundary or belongs to a previous trusted context/epoch. |
| Ambiguous evidence | Evidence whose run membership or ordering cannot be determined with enough confidence. |
| Incomplete evidence | Evidence that is insufficient for a complete result interpretation, for example because the stream is missing expected completion or result evidence. |
| Capture completeness | The decision that all relevant event-stream evidence for an observed run has been captured or that capture cannot be considered complete. |
| Neutral result model | Shared run, suite, and test-case result meaning before WPF presentation, CLI exit-code mapping, or artifact formatting. |

## Hierarchical Owner Names

The PLC-owned event-stream contract defines event `Name` values for UnitTest
owners as dot-separated hierarchical owner paths. The event stream does not
provide a separate parent id. EventStream Processing derives the result
hierarchy from `OwnerScope` and `Name`.

Full Owner names use fixed segment counts:

| OwnerScope | Required name form | Parent |
| --- | --- | --- |
| `RunControl` | `<runControlName>` | none |
| `TestSuite` | `<runControlName>.<suiteName>` | `<runControlName>` |
| `TestCase` | `<runControlName>.<suiteName>.<testCaseName>` | `<runControlName>.<suiteName>` |

Assertion result names use:

```text
<runControlName>.<suiteName>.<testCaseName>.<assertionName>
```

For assertion result events, the parent test case is always the first three path
segments. Everything after the third dot belongs to the assertion-local name.
This allows assertion-local diagnostic names to contain additional dots without
changing the test-case parent relationship.

Run Control, Test Suite, and Test Case local name segments must follow the
PLC-owned contract restrictions. They may contain only ASCII letters, ASCII
digits, `_`, and `-`; they must not contain `.`, whitespace, control characters,
or be empty. Produced event names must not be truncated.

Framework and orchestrator scopes are not part of the result hierarchy.
Orchestrator diagnostics use the reserved `<parentName>.Orchestrator` form and
remain technical diagnostics, not Full Owner lifecycle evidence.

Example:

| OwnerScope | Name | Interpretation |
| --- | --- | --- |
| `RunControl` | `MainRun` | RunControl path `MainRun`. |
| `TestSuite` | `MainRun.DriveStartup` | Suite `DriveStartup` belongs to RunControl `MainRun`. |
| `TestCase` | `MainRun.DriveStartup.CheckReady` | Test case `CheckReady` belongs to suite `MainRun.DriveStartup`. |
| `Assertion` | `MainRun.DriveStartup.CheckReady.Speed.WithinTolerance` | Assertion finding `Speed.WithinTolerance` belongs to test case `MainRun.DriveStartup.CheckReady`. |

If a `TestSuite`, `TestCase`, or assertion result name does not match the
contract-defined hierarchy for its `OwnerScope`, its result evidence can still
be retained for diagnostics, but hierarchy reconstruction and aggregation
validation for that evidence are incomplete or inconsistent.

Assertion results are part of the internal log/result hierarchy but not Full
Owner lifecycles. They do not require lifecycle completion or aggregation
evidence. A failed or error assertion result means that the parent test case
cannot be interpreted as passed. If assertion result evidence contradicts the
parent test-case result evidence, the test case has inconsistent result
evidence and must not become a clean passed result.

## Core Design Rules

- Event IDs classify event semantics; they are not sufficient for run
  identification or event-to-run assignment.
- `IUnitTestEventStreamService` delivers evidence and stream diagnostics; it
  does not decide observed run membership, capture completeness, or final result
  meaning.
- `IUnitTestLifecycleService` delivers lifecycle facts; its completion evidence
  is not final result evidence by itself.
- EventStream Processing consumes the active observation context and
  event-stream evidence. `TestRunMonitor` combines lifecycle evidence,
  EventStream Processing results, timeout policy, and context validity.
- Incomplete, stale, conflicting, lost, or unreliable evidence must not be
  interpreted as a successful complete run.
- Result meaning is shared before outputs, artifacts, WPF presentation, CLI
  process outcomes, or CI/CD report formats are applied.

## Observation Flows

EventStream Processing supports two observation flows:

- application-triggered run observation,
- PLC-side-started run observation.

Both flows produce an observed run candidate. They differ in how the observation
boundary is established and how much confidence the application has about the run
start.

### Application-Triggered Run Observation

Application-triggered observation starts when the workflow executes the UnitTest
`Start` action after the PLC is running and UnitTest lifecycle/event-stream
communication is trusted.

The intended sequence is:

1. Event-stream reading is active and has a trusted access context.
2. `TestRunMonitor` establishes an observation baseline before the workflow
   issues the lifecycle start request.
3. `IUnitTestLifecycleService` issues the start request and reports command
   progress/outcome evidence.
4. The workflow monitors the lifecycle reaction to the accepted start command
   independently from run evidence assignment.
5. `EventStreamProcessor` opens the run epoch when it receives the exact
   RunControl `Starting` state-transition anchor. The anchor itself is the first
   event assigned to the epoch.
6. The corresponding RunControl `Completed` state-transition anchor and an
   empty-buffer observation are latched independently. Both are required for
   normal capture completion.
7. The access-profile boundary must publish the first trusted empty-buffer
   observation after non-empty evidence even though that observation contains
   no event entries. Repeated unchanged empty observations may be coalesced.

If the start request is rejected, times out, remains unclear, or lifecycle
context becomes invalid before a trusted active boundary can be observed, no
clean monitored run epoch is opened. The workflow reports failed-start,
execution-error, timeout, incomplete, or unknown outcome according to later
result-state rules.

Application-triggered observation is stronger before the active boundary because
the workflow controls and monitors the start request. After the active boundary,
the monitored run evidence follows the same EventStream Processing model as an
externally observed run. Even here, event IDs alone do not prove run membership.
Membership still depends on the observation baseline, stream ordering/epoch
evidence, lifecycle evidence, and context validity.

### PLC-Side-Started Run Observation

PLC-side-started observation starts when the application is connected to a
prepared, running PLC and the user or command mode chooses observation instead of
requesting lifecycle start.

The intended sequence is:

1. Event-stream reading is active and has a trusted access context.
2. `TestRunMonitor` establishes an observation baseline at the moment
   observation starts.
3. `EventStreamProcessor` continuously evaluates delivered event-stream
   evidence for the exact RunControl `Starting` anchor.
4. That anchor opens the observed run epoch without an application Start
   command, so PLC-side-started runs follow the same assignment rule.
5. A lifecycle `Running` projection without the start anchor may satisfy command
   reaction monitoring, but it must not create or repair a run epoch.
6. Capture completeness and result meaning are evaluated with the same evidence
   categories used for application-triggered runs.

PLC-side-started observation uses the same exact event anchor but has no prior
command-reaction context. If observation starts after the RunControl `Starting`
anchor was already released, later lifecycle projections or sequence ids do not
reconstruct the missing epoch boundary. The evidence must remain outside a
complete run epoch rather than being reported as successful.

### Observation Flow Boundaries

Observation flows stop accepting evidence for the current observed run when one
of these conditions occurs:

- capture completes and evidence updates the shared log/result model,
- the workflow aborts or resets the run and no further evidence can be trusted
  for that observed run,
- the PLC is stopped,
- event-stream or lifecycle context becomes invalid,
- the access profile is reloaded,
- cleanup releases the communication context,
- timeout policy ends observation without enough evidence for completion.

Stopping observation does not necessarily stop event-stream reading forever.
Reading may be restarted later through recovery or a new prepared context, but
the next observed run must get a new observation boundary and baseline.

## Observation Boundary And Event Assignment

EventStream Processing owns run-epoch assignment from normative event-stream
anchors. The start boundary is exactly one delivered event with:

- `OwnerScope = RunControl`,
- `EventType = StateTransition`,
- `State = Starting`.

This event opens the epoch and is included as its first event. The corresponding
RunControl `StateTransition` to `Completed` is included as the final event of
the epoch. Their `SequenceId` values delimit the inclusive event range only
after the semantic anchors have been identified. A sequence id, lifecycle
projection, trigger event, or successful Start command cannot open an epoch by
itself.

EventStream Processing consumes PC-side captured event batches. A captured batch
means the event evidence was copied from the PLC-side event buffer into PC-owned
memory and can be processed independently from PLC-buffer release. Release or
acknowledgement is communication progress; it does not assign evidence to a run.

The processor therefore applies these rules:

1. Maintain continuous observation for the active communication session.
2. Ignore stale sequence ids at or below the communication-session baseline.
3. Route valid framework, trigger, initialization, reset, idle, and other
   non-run evidence outside an epoch immediately without adding it to run
   results.
4. Diagnose result/aggregation, RunControl completion, or active subordinate
   lifecycle evidence that appears without an active epoch.
5. Open or replace the active epoch only on the exact RunControl start anchor.
   A second start anchor before completion invalidates the previous epoch as
   overlapping evidence.
6. Assign subsequent ordered events through the exact RunControl completion
   anchor to the active epoch.
7. Reject later run-owned events from that epoch range because the completion
   anchor is contractually the final RunControl event.
8. Latch the completion anchor and empty-buffer observation independently.
   Normal completion requires both facts, irrespective of which is observed
   first.

Application-triggered and PLC-side-started runs use the same epoch rule. An
application Start command only adds independent command-reaction monitoring. It
does not grant epoch ownership and it does not alter event assignment.

If observation begins after the start anchor was already released, no complete
epoch can be reconstructed from later lifecycle projections. Such events remain
outside an epoch and the application reports incomplete or implausible evidence
instead of manufacturing a successful run.

### Assignment Rules

EventStream Processing should classify delivered event evidence relative to the
active observed run:

| Classification | Rule |
| --- | --- |
| Candidate event | Event evidence is after the observation baseline, belongs to the same trusted event-stream context/epoch, and has plausible ordering. |
| Assigned event | Candidate event can be associated with the observed run through baseline position, ordering/sequence evidence, lifecycle correlation, and event semantics. |
| Stale event | Event evidence is before the observation baseline or belongs to an older context/epoch. |
| Ambiguous event | Event evidence cannot be safely classified as assigned or stale because ordering, epoch, lifecycle correlation, or context trust is unclear. |
| Conflicting event | Event evidence contradicts trusted lifecycle state, ordering evidence, or other assigned event evidence. |
| Lost evidence indicator | Stream evidence reports gaps, lost-event state, overwritten entries, or another loss signal that prevents a complete confidence claim. |

Event IDs and event payload semantics are used after an event is a candidate for
the observed run. They classify meaning, such as lifecycle transition, suite
result, test-case result, diagnostic entry, or aggregation evidence. They do not
override boundary, ordering, epoch, or context-trust evidence.

Within one trusted event-stream context/epoch, event IDs are the ordering rule
for delivered event evidence. This means: if event `42` was already seen for the
current context, event `43` is newer than event `42`. Event IDs do not define the
run by themselves. They only help decide whether an event is before or after the
baseline and whether evidence was delivered in a plausible order.

An event ID reset is only valid when the stream context/epoch was reset as well,
for example after a trusted lifecycle reset or communication context reset. If
event IDs go backwards, duplicate unexpectedly, jump in a way that indicates
lost evidence, or reset without a trusted reset boundary, the affected evidence
is not trustworthy for a complete success result.

An event belongs to the current run only when all of these checks are true:

- it comes from the same trusted event-stream context,
- it is newer than the baseline captured for this run,
- its event ID order is plausible for that context,
- it fits the RunControl active interval,
- it is not stale, ambiguous, conflicting, or lost evidence.

### Stale And Ambiguous Evidence Handling

Stale evidence must not be interpreted as part of the current observed run. It
may be retained as diagnostics so the user or CLI can explain why the stream was
not clean at observation start.

Ambiguous evidence must not be silently assigned to the current run. If
ambiguous evidence is relevant to result or completion interpretation, the run
must become incomplete, ambiguous, or unknown according to the later result-state
rules. It must not become complete success.

Conflicting evidence must be treated as a stronger problem than ordinary stale
evidence. It indicates that the observed run context, lifecycle state, or stream
ordering cannot be trusted without recovery or a new observation boundary.

Lost evidence indicators invalidate complete-result confidence unless the
follow-up completeness rules can prove that the lost evidence is outside the
observed run boundary and cannot affect the current result.

### Boundary Reset Conditions

A new observation boundary is required when:

- the user or CLI starts a new UnitTest run,
- the user or CLI starts observing a PLC-side-started run,
- UnitTest lifecycle reset succeeds,
- event-stream context is reconnected after invalidation,
- event-buffer epoch changes,
- PLC runtime is stopped and started again,
- project transfer/download changes the running PLC program,
- access profile is reloaded,
- cleanup releases the previous communication context.

Existing observed-run evidence must not be merged across boundary reset
conditions unless a later design explicitly defines a safe merge rule. The
default behavior is to treat the next run/observation as a new observed run with
its own baseline.

## Capture Completeness

Capture completeness decides whether the shared core has enough event-stream
evidence to interpret one observed run. It is evaluated after event streaming
for the observed RunControl start is finished or timed out.

Lifecycle completion and result interpretation are related, but they are not the
same decision:

- RunControl lifecycle completion says that the PLC-side RunControl reached its
  terminal completed state.
- Event-stream capture completeness says whether the application observed the
  required event evidence for the run.
- Result interpretation says what the captured evidence means for run, suite,
  test-case, and assertion results.

### Completion Gate

Event streaming for one observed run can be evaluated only after one of these
conditions is reached:

- the RunControl completion anchor was observed in the event stream and the
  event buffer was observed empty,
- the observation timed out,
- an active run observation context was invalidated by reset, PLC stop, project
  download, access-profile reload, communication loss, or cleanup.

When no RunEpoch is active, PLC stop or communication release ends the technical
observation without fabricating an invalidated run result.

Before this gate is reached, missing owner evidence is still pending evidence.
After this gate is reached, missing owner evidence becomes incomplete evidence.

### Owner Evidence Completeness

For each observed Full Owner lifecycle, EventStream Processing checks the event
evidence expected by the PLC-owned contract:

| OwnerScope | Required evidence for complete lifecycle/result evidence |
| --- | --- |
| `RunControl` | One RunControl `StateTransition` to `Completed` and one RunControl `Result`. |
| `TestSuite` | One TestSuite `StateTransition` to `Completed` and one TestSuite `Result`. |
| `TestCase` | One TestCase `StateTransition` to `Completed` and one TestCase `Result`. |

Framework and orchestrator scopes are diagnostics. They do not create Full Owner
lifecycle completeness requirements.

Assertion result events are handled as result findings below the matching test
case. They do not require lifecycle completion or aggregation evidence.

Aggregation evidence is intentionally not required for lifecycle/result
completion. Missing aggregation evidence is a separate aggregation-evidence
condition. It can reduce diagnostics or reporting detail, but it must not by
itself make a completed owner lifecycle incomplete.

### Hierarchy And Result Consistency

The internal run log hierarchy is reconstructed from the local hierarchical
owner-name agreement:

```text
RunControl
  TestSuite
    TestCase
      Assertion finding
```

The shared core derives result meaning from the captured hierarchy:

- A failed or error assertion result prevents the parent test case from being
  interpreted as passed.
- A failed or error test-case result prevents the parent suite from being
  interpreted as passed.
- A failed or error suite result prevents the run from being interpreted as
  passed.
- If captured child evidence contradicts parent result evidence, the affected
  owner has inconsistent result evidence.

The run model therefore distinguishes two result views:

- the PLC-reported result is the value carried by the owner's retained Result
  event,
- the effective PC result includes retained child findings, hierarchy
  propagation, evidence completeness, and terminal PC-side observation state.

The application does not relabel a PLC-reported `Passed` result when child
evidence contradicts it. It preserves that reported value and raises the
effective result according to the hierarchy priority. This makes PLC framework
or integration defects visible instead of hiding them behind a normalized
parent value.

Global Run incompleteness does not imply that every owner is locally
incomplete. Missing terminal evidence is assigned to the owner for which the
event is absent and propagated only through that owner's parent chain. Owners
with complete retained evidence remain locally complete even when event loss or
another branch makes confidence in the complete Run incomplete.

The application must not infer missing expected children from aggregation
counters alone. Aggregation can validate captured evidence, but it does not
create concrete owner identities that were not observed in the event stream.

### Aggregation Evidence And Validation

Aggregation events are optional validation and reporting evidence for the
reconstructed hierarchy and derived result meaning. They are useful for compact
log formatting, reporting totals, and detecting contradictions, but they are not
the primary completion signal.

| Situation | Interpretation |
| --- | --- |
| Aggregation matches the captured owner hierarchy and derived result meaning. | Aggregation evidence is consistent. |
| Aggregation is missing for an observed Full Owner after the completion gate. | Aggregation evidence for that owner is unavailable or incomplete, but lifecycle/result completion is not invalidated by this alone. |
| Aggregation reports counts or result totals that contradict the captured hierarchy. | Result evidence is inconsistent; the affected owner must not become a clean passed result. |
| Aggregation reports more children or failures than the captured hierarchy can explain. | Evidence is incomplete or inconsistent; the run must not become a clean passed result. |

Missing aggregation evidence is tracked separately from lifecycle completion and
result evidence. The lifecycle event log can have its completion anchor and
complete result evidence while aggregation evidence for one or more owners is
unavailable.

### Stream Integrity Evidence

Transport and buffer integrity evidence supports completeness evaluation, but it
does not replace the owner-evidence rules.

- Event IDs and sequence data are used for ordering and plausibility within one
  trusted event-stream context.
- Event ID gaps are not by themselves the semantic parent/child completeness
  model.
- `LostEventCount > 0` after event streaming is finished marks the delivered
  lifecycle event log as incomplete according to the PLC-owned contract.
- A reset, epoch change, reconnect, or context invalidation inside the observed
  run prevents a complete confidence claim unless a later design defines a safe
  recovery rule.

### Capture And Evidence State Separation

EventStream Processing must keep observation completion, evidence quality, and
UnitTest result meaning separate. Capture state says whether observation has
finished. It does not say whether the UnitTest passed.

Capture state:

| State | Meaning |
| --- | --- |
| `Pending` | Observation is still active and the completion gate has not been reached. |
| `Finished` | Completion gate reached and event-stream stabilization finished. |
| `TimedOut` | Monitoring time expired before the completion gate could be satisfied. |
| `Invalidated` | Observation context was invalidated before capture could finish. |
| `Aborted` | The run or observation was intentionally aborted before normal capture completion. |

Evidence completeness:

| State | Meaning |
| --- | --- |
| `Complete` | Required lifecycle, result, hierarchy, and stream-integrity evidence is present for the evaluated scope. |
| `Partial` | Some result or diagnostic evidence is available, but the evaluated scope is not fully complete. |
| `Incomplete` | Required evidence is missing after the completion gate, timeout, abort, or invalidation made further waiting meaningless. |
| `Unknown` | Evidence completeness cannot be evaluated yet. |

Evidence consistency:

| State | Meaning |
| --- | --- |
| `Consistent` | Captured evidence does not contradict itself. |
| `Inconsistent` | Captured evidence contradicts itself, for example assertion result versus test-case result or derived result versus aggregation. |
| `Ambiguous` | Captured evidence cannot be assigned or interpreted with enough confidence. |
| `Unknown` | Evidence consistency cannot be evaluated yet. |

Result interpretation remains a separate concern. A run, suite, or test case may
only become a clean passed result when capture is `Finished`, evidence
completeness is `Complete`, evidence consistency is `Consistent`, and the
interpreted UnitTest result is `Passed`.

Partial result and diagnostic evidence may still be shown in WPF, returned by
CLI diagnostics, or written to diagnostic artifacts. It must not be mapped to a
successful complete run.

## Neutral Result Model

The neutral result model is the shared result and log structure created by
EventStream Processing before WPF presentation, CLI process-outcome mapping, or
artifact formatting. It must represent UnitTest result meaning without depending
on JUnit XML, UI state, CLI exit codes, Siemens API objects, or raw PLC
constants.

The model separates:

- observation/capture state,
- evidence completeness,
- evidence consistency,
- interpreted UnitTest result,
- diagnostic evidence,
- optional aggregation/reporting evidence.

### Result Hierarchy

The neutral result hierarchy follows the local hierarchical owner-name
agreement:

```text
ObservedRunResult
  SuiteResult
    TestCaseResult
      AssertionFinding
```

`RunControl` is represented by the observed run result. `TestSuite` and
`TestCase` owners become suite and test-case result nodes. Assertion result
events become assertion findings below the matching test case when the
hierarchical name can be resolved.

Framework, informational, trigger, and orchestrator events are diagnostic
entries. They can be attached to the nearest resolvable result node or retained
as run-level diagnostics when no safer attachment exists. They do not create
result hierarchy nodes by themselves.

### Observed Run Result

An observed run result represents one observed RunControl run epoch.

It should contain:

- observation identity used by the application, not a PLC raw identifier,
- capture state,
- evidence completeness,
- evidence consistency,
- interpreted run result,
- suite results,
- unresolved findings or diagnostics,
- timing/runtime information when available,
- optional aggregation data,
- source diagnostics such as timeout, invalidation, stream loss, stale evidence,
  or aggregation inconsistency.

### Suite Result

A suite result represents one observed `TestSuite` owner.

It should contain:

- hierarchical suite name,
- local display name derived from the hierarchical name,
- capture/evidence state for that suite,
- interpreted suite result,
- test-case results,
- suite diagnostics,
- optional suite aggregation data.

A suite must not be interpreted as passed when any child test case is failed,
error, incomplete, ambiguous, or inconsistent.

### Test Case Result

A test-case result represents one observed `TestCase` owner.

It should contain:

- hierarchical test-case name,
- local display name derived from the hierarchical name,
- capture/evidence state for that test case,
- interpreted test-case result,
- assertion findings,
- diagnostic log entries,
- timing/runtime information when available,
- optional test-case aggregation data.

The normative test-case result comes from the `TestCase` result evidence. A test
case must not be interpreted as passed when a failed or error assertion finding
is attached to it.

### Assertion Findings

Assertion result events are modeled as assertion findings, not as complete
assertion result nodes.

Assertions are problem-oriented evidence:

- The PLC-side framework emits assertion result events only for failed or error
  assertions.
- A missing assertion event does not mean missing assertion evidence.
- A completed test case without assertion findings can be passed.
- A failed or error test case without assertion findings is valid and can be
  caused by test-case logic, preparation/setup behavior, framework/runtime
  behavior, explicit failure paths, or abort/error handling before an assertion
  is emitted.
- A passed test case with a failed or error assertion finding is inconsistent.

An assertion finding should contain:

- hierarchical assertion name,
- local display name derived from the hierarchical name,
- result value,
- message text,
- severity or diagnostic level when relevant,
- source event reference for diagnostics.

### Result Interpretation Rules

Result interpretation derives clean result meaning only after capture and
evidence state have been evaluated.

| Rule | Consequence |
| --- | --- |
| Capture is not `Finished`. | The scope cannot be a clean passed result. |
| Evidence completeness is not `Complete`. | The scope cannot be a clean passed result. |
| Evidence consistency is not `Consistent`. | The scope cannot be a clean passed result. |
| Assertion finding is failed or error. | The parent test case cannot be passed. |
| Test case is failed, error, incomplete, ambiguous, or inconsistent. | The parent suite cannot be passed. |
| Suite is failed, error, incomplete, ambiguous, or inconsistent. | The observed run cannot be passed. |
| Aggregation is missing. | Reporting detail is reduced, but lifecycle/result completion is not invalidated by this alone. |
| Aggregation contradicts derived hierarchy or result meaning. | The affected scope is inconsistent and cannot be a clean passed result. |

### Output Independence

The neutral result model is not the output artifact model.

- WPF maps the model to manual observation, detail views, and diagnostics.
- CLI maps the model to process outcome, console summary, and diagnostics.
- Artifact generation maps the model to concrete report formats such as JUnit
  XML.
- Diagnostic artifacts may include source event references and raw evidence
  excerpts where useful.

Output formats may flatten, group, or enrich the neutral model, but they must
not redefine capture state, evidence completeness, evidence consistency, or
UnitTest result meaning.

## Representative Evidence Cases

The following cases define representative evidence constellations for later
shared-core tests. They are conceptual test cases for EventStream Processing,
not implementation test code.

| Case | Input evidence | Expected capture state | Expected evidence completeness | Expected evidence consistency | Expected result interpretation | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| Clean passed run | RunControl, Suite, and TestCase completion and result evidence are present; no failed/error assertion finding exists; aggregation is missing or consistent. | `Finished` | `Complete` | `Consistent` | `Passed` | Missing aggregation alone does not prevent a clean passed result. |
| Assertion failure | TestCase result is failed; failed assertion finding exists below the test case; Suite and Run result evidence reflect failure. | `Finished` | `Complete` | `Consistent` | `Failed` | Assertion finding explains the failed test case. |
| TestCase failure without assertion finding | TestCase result is failed or error; no assertion finding exists. | `Finished` | `Complete` | `Consistent` | `Failed` or `Error` | Valid case. The failure may originate from test-case logic, preparation, framework/runtime behavior, or abort/error handling. |
| Contradicting assertion | TestCase result is passed; failed or error assertion finding exists below the test case. | `Finished` | `Complete` | `Inconsistent` | Not clean passed | Failed/error assertion evidence prevents parent test case from being interpreted as passed. |
| Missing result evidence | Owner completion evidence is present, but required owner result evidence is missing after the completion gate. | `Finished` | `Incomplete` | `Consistent` or `Unknown` | Not clean passed | Completion alone is not result evidence. |
| Missing completion evidence | Result evidence is present, but required Full Owner completion evidence is missing after finish or timeout. | `Finished` or `TimedOut` | `Incomplete` | `Consistent` or `Unknown` | Not clean passed | Missing lifecycle completion makes lifecycle evidence incomplete. |
| Missing aggregation | Completion and result evidence are complete; aggregation is missing. | `Finished` | `Complete` | `Consistent` | Derived from result evidence | Aggregation evidence is unavailable/incomplete, but lifecycle/result completion is not invalidated by this alone. |
| Aggregation contradiction | Captured hierarchy and derived result say failed, but aggregation says passed, or aggregation counts cannot match captured evidence. | `Finished` | `Complete` or `Partial` | `Inconsistent` | Not clean passed | Aggregation cannot override captured result evidence. |
| Stale buffered events | Event buffer contains events before the run baseline; new run starts afterwards. | `Finished` or `Pending` | Depends on current run evidence | Depends on current run evidence | Old events not result-relevant | Stale events may be retained as diagnostics and released/acknowledged by the event-stream service. |
| PLC-side-started run | Event stream delivers the exact RunControl `Starting` anchor without application `Start`; events through the corresponding `Completed` anchor form the inclusive epoch. | `Pending` then `Finished` | Depends on captured owner evidence | Depends on anchor and stream integrity | Derived from captured run | Same epoch-assignment rule as application-triggered start; only command-reaction context is absent. |
| Timeout before completion gate | Completion anchor or buffer-empty observation is not reached within monitoring time. | `TimedOut` | `Incomplete` or `Unknown` | `Unknown` or `Ambiguous` | Not clean passed | Partial evidence may still be useful diagnostics. |
| Invalidated observation | PLC stop, project download, reset, reconnect, access-profile reload, communication loss, or cleanup invalidates context while a RunEpoch is active. | `Invalidated` | `Incomplete` or `Unknown` | `Unknown` or `Ambiguous` | Not clean passed | A new observation needs a new boundary and baseline. Without an active RunEpoch, the same context loss closes technical observation without creating a run result. |
| Lost event evidence | Event streaming finishes with `LostEventCount > 0`. | `Finished` | `Incomplete` | `Unknown` or `Ambiguous` | Not clean passed | PLC-owned contract defines the delivered lifecycle event log as incomplete. |
| Unresolved hierarchy | TestCase or assertion finding name cannot be matched to the expected hierarchical parent. | `Finished` | `Partial` or `Incomplete` | `Ambiguous` or `Inconsistent` | Not clean passed for affected scope | Evidence may be retained diagnostically, but hierarchy-based validation cannot be clean. |

These cases intentionally avoid artifact-format expectations. JUnit XML,
console output, WPF presentation, and diagnostic file mapping are downstream
concerns that consume the neutral result model.
