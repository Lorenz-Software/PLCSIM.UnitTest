# s7unittest Contract Reference Source

This directory contains a reference snapshot of selected PLC-owned contract
documents consumed by `PLCSIM.UnitTest`.

## Source Of Truth

Owning project: `s7unittest`, the Siemens PLC UnitTest framework. The source
directory is `contracts/`. A live checkout is not needed to consume this
versioned snapshot; private repository URLs and machine-local paths are omitted.

Reference branch:

```text
master
```

Reference commit:

```text
516eee6fccee21f5b3ac924656b3509edf2679b6
```

Snapshot date: `2026-10-20`.

Declared contract version in both included documents:

```text
1.0.0
```

The copied files match the clean source checkout at the recorded commit.
The revision identifies the snapshot; an unchanged declared contract version
does not imply unchanged content or establish consumer compatibility or runtime
qualification. No PLC implementation or engineering project files are copied.

## Included Files

- `contracts/event-stream-model-contract.md`
- `contracts/plcsim-advanced-communication-contract.md`

## Ownership

The `s7unittest` repository owns these contracts. This repository keeps only a
versioned reference snapshot so the PC-side implementation can be reviewed
against a known PLC communication contract state.

Do not edit these copied contract files directly in this repository. Contract
changes must be made in the `s7unittest` repository and then refreshed here as a
new reference snapshot.
