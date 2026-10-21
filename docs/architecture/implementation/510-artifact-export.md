# Artifact Export Implementation

## `ArtifactExportService`

`ArtifactExportService` is an application-composed export observer and execution
service. The workflow publishes `WorkflowStatusSnapshot.IsExportable` together
with a stable `UnitTestRunEpochSnapshot`; the application composition root binds
the export service as an additional handler to workflow publication. The export
service observes the publication stream and triggers automatic export on the
`IsExportable: false -> true` transition.

Code owner:

The implementation and publication event live in
[ArtifactExportService.cs](../../../src/PLCSIM.UnitTest.Core/Output/ArtifactExportService.cs).
Application composition roots attach the observer; exporters do not drive
workflow or PLC operations.

Required behavior:

1. Export consumes a stable run epoch snapshot, not the mutable active run
   epoch.
2. Automatic export is triggered by the export service when the workflow
   publication changes from not exportable to exportable and its epoch has not
   already been automatically exported.
3. The service resolves the matching `IArtifactExporter` by `ArtifactFormat`.
4. Each request updates `OutputState` from pending to written, skipped, or
   failed.
5. Required artifact failure is returned in `OutputState` and reported as a
   diagnostic. CLI process-result mapping is handled by the CLI application
   layer.
6. Optional artifact failure is still diagnostic evidence, but does not by
   itself define the UnitTest result.
7. Exporters receive only `ExportRequest` and `UnitTestRunEpochSnapshot`.
8. Exporter and export-coordination diagnostics are written to the shared
   `DiagnosticSink`.
9. Exporters do not call access profile services, do not read PLC data, and do
   not interpret Siemens-specific details.
10. Artifact output options are owned by the export service. The default test
    result artifact is written as `junit.xml` below the configured output
    directory and overwrites the previous file instead of creating per-run file
    spam.
11. JUnit XML is implemented as one exporter behind `IArtifactExporter`.
12. Terminal-style text output is generated from the same neutral run epoch
    model and attached to `OutputState`.
13. The service emits concise artifact status lines with format, state, and
    output path.
14. `OutputState.TerminalText` is raw presentation text. It is a current output
    snapshot owned by the output/export service, not an append-only application
    log. UI applications add local display timestamps when rendering it.
15. Explicit `ExportAsync` calls are separate from the automatic-publication
    trigger. Epoch deduplication of that trigger does not prohibit an explicit
    export request.

This service must stay narrow. It is not a workflow coordinator, does not
subscribe to PLC/run monitoring itself, and does not define result meaning. It
observes only workflow publications. JUnit XML behavior remains inside the
JUnit exporter.

## Common Output And Export Models

Artifact export is based on a stable read-only run epoch projection.

Exact exporter/request/result definitions live in
[ArtifactExportContracts.cs](../../../src/PLCSIM.UnitTest.Common.Application/Artifacts/ArtifactExportContracts.cs),
and output read models in
[OutputState.cs](../../../src/PLCSIM.UnitTest.Common.Application/Output/OutputState.cs).
Application architecture owns result/artifact meaning. This document maps
execution and publication to code instead of copying DTO declarations.

Ownership:

- JUnit XML is one exporter implementation,
- exporters consume `ExportRequest` and `UnitTestRunEpochSnapshot`,
- exporter diagnostics are emitted through the exporter contract and written by
  `ArtifactExportService` to the shared `DiagnosticSink`,
- exporters do not call functional services and do not interpret Siemens data,
- `OutputState.TerminalText` remains timestamp-free in shared core; timestamped
  terminal presentation is owned by the consuming application.

## JUnit XML Exporter

The JUnit exporter writes a conservative CI-compatible dialect. It uses
standard JUnit-style elements and attributes only:

- root `testsuites` with `name`, `tests`, `failures`, `errors`, `skipped`, and
  `time`,
- child `testsuite` elements with `name`, `tests`, `failures`, `errors`,
  `skipped`, and `time`,
- child `testcase` elements with `classname`, `name`, and `time`,
- optional `failure` or `error` child elements with `message` and text detail.

The exporter must not add project-specific metadata such as `runEpochId` as XML
attributes. If such metadata is needed later, it must be added through an
explicit compatibility decision for the target consumer.

Result mapping follows the application-layer artifact semantics:

- passed concrete test cases are written as successful `testcase` elements,
- failed concrete test cases are written with `failure`,
- errored, not-evaluated, unknown, incomplete, ambiguous, conflicting, aborted,
  or framework-error concrete test cases are written with `error`,
- run-level or suite-level structural problems are diagnostics/output failures
  unless they can be assigned to concrete test cases,
- the default exporter does not create synthetic test cases for run-level or
  suite-level problems.
- JUnit XML is testcase-oriented. A RunControl abort observed after concrete
  test cases already reported passed evidence does not retroactively convert
  those concrete test cases to failed or errored JUnit entries. The abort remains
  visible in the neutral run epoch/result tree and diagnostics/output context.

Runtime values in the XML come from run evidence, not from application wall-clock
measurement. Testcase `time` is derived from testcase aggregation evidence when
available. Suite `time` is derived from suite aggregation evidence when
available and otherwise falls back to the sum of exported testcase times. The
root `testsuites` time is the sum of exported suite times.
