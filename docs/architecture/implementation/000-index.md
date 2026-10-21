# Shared Core Implementation

This document maps the architecture concepts to the current implementation of
`PLCSIM.UnitTest`. Architecture documents describe responsibilities and
rationale; shared contracts define the normative interface and persistence
boundaries. These implementation documents identify code owners and data flows
without defining a second contract or a delivery sequence.

The detailed design is split by concern:

- [Implementation Structure](090-structure.md): projects,
  dependency direction, placement rules, configuration ownership, and artifact
  placement.
- [Application Implementation](110-application-implementation.md): WPF and CLI
  composition roots, workflow method usage, publication consumption, cleanup,
  diagnostics, and application boundaries.
- [Configuration Implementation](120-configuration.md): implementation usage of
  the configuration file contract, access-profile config boundary,
  execution/output settings, timeout names, defaults, and validation flow.
- [Access Profile Implementation](210-access-profile.md): access
  profile plugin loading, profile contracts, profile lifecycle, service
  consistency, profile-level diagnostics, and the package/manifest contract
  reference.
- [Functional Service Implementation](310-functional-services.md): concrete
  application-facing contracts for PLC project, PLC control, UnitTest lifecycle,
  and UnitTest event-stream services.
- [Shared Workflow Design](320-shared-workflow.md): workflow orchestration,
  action gates, application publication, WPF/CLI flow, diagnostics handover, and
  output handover.
- [UnitTest Run Epoch Model](410-unittest-run-epoch.md): event batches,
  contract-near event entries, event-stream processing, run epoch ownership,
  run tree, findings, framework events, and aggregation model.
- [Artifact Export Design](510-artifact-export.md): artifact export service,
  exporter contract, export execution/results, output state, and exporter
  diagnostic reporting.
- [Testability Strategy](700-testability.md): fake/sample access profiles,
  sample event evidence, non-runtime shared-core tests, application test
  boundaries, and Siemens-specific integration test separation.

The split keeps each document focused while preserving the same ownership
boundary: WPF and CLI consume shared behavior, the shared core owns workflow and
UnitTest interpretation, access profiles own concrete communication and internal
service consistency, and exporters consume stable `UnitTestRunEpoch`
projections.
