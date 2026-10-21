# tc3unittest Contract Reference Source

This directory contains reference snapshots of the PLC-owned `tc3unittest`
contracts consumed by `PLCSIM.UnitTest`. They are reference documents, not PLC
implementation sources or a runtime dependency.

## Source Of Truth

- Owning project: `tc3unittest`, the TwinCAT 3 UnitTest framework.
- Source directory within that project: `contracts/`.
- Source revision: `3231ded6a6129d65aee5ebc2f98201db7d513693`.
- Snapshot date: `2026-10-20`.
- Declared contract version: `1.0.0` in each included document.

The source contract files were unchanged relative to this revision when copied.
The revision identifies the source state; the declared contract version is not
a substitute for the revision or a claim of runtime qualification. Private
repository URLs and machine-specific checkout paths are intentionally omitted.

## Included Files

Each local file preserves the corresponding source file under `contracts/`:

- [General framework contract](contracts/general-framework-contract.md)
- [Run Control contract](contracts/run-control-contract.md)
- [Event Stream contract](contracts/event-stream-contract.md)
- [Test Suite contract](contracts/test-suite-contract.md)
- [Test Case contract](contracts/test-case-contract.md)

## Ownership And Use

The `tc3unittest` project owns these contracts. This repository keeps a reviewed,
versioned snapshot so consumer development and review do not depend on a live
external checkout. The copies do not establish an independently maintained
contract variant.

Do not edit the copied contracts locally to accommodate consumer behavior.
Contract changes belong in the owning project. Consumer mappings, transport
details and application behavior belong in their respective documentation, not
in these snapshots.

## Manual Synchronization

There is no automatic synchronization during build, startup or release.

1. Select a source revision and verify that the source contract files have no
   uncommitted changes relative to it.
2. Compare all five documents with the current snapshots. Review semantic
   changes and their consumer impact before adopting the new reference state.
3. Copy the approved source documents without modifying their content. Normal
   repository line-ending normalization is permitted.
4. Update the source revision, snapshot date and declared versions here.
5. Verify the copies against the source, check local document references and
   run `git diff --check` before committing the reviewed update.

Do not treat an unchanged contract version as proof that document content is
unchanged. Do not copy PLC implementation files or engineering artifacts as
part of a contract refresh.
