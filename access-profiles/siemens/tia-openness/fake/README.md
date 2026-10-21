# Siemens TIA Openness Fake Binding

This subtree contains the no-Siemens TIA Openness sample binding used for
deterministic Siemens shell and contract tests.

It implements the Siemens-internal TIA Openness binding contract without
referencing TIA Portal or Siemens Openness assemblies. It is not a generic
access profile and it does not prove real TIA Openness behavior.

Fake `.apf` project files are direct project-data fixtures. Fake `.zapf` files
exercise the archive-backed contract path without representing a Siemens
archive: the binding copies their JSON payload into a unique binding-owned
retrieve workspace and loads it from there. The selected `.zapf` remains the
reported project source. The workspace is removed after open failure or
cancellation and when the successful engineering session closes.
If workspace cleanup also fails, the fake preserves the primary open failure
or caller cancellation and reports the cleanup problem as bounded supplemental
evidence. Terminal close remains non-throwing for the supplemental cleanup
failure and leaves the engineering session truthfully disposed.

Both fixture forms describe project metadata and target references only.
Operation behavior such as open, target discovery, build, transfer, close,
delays, and failures is controlled by injecting `FakeTiaBehavior` into
`FakeTiaOpennessBinding` from tests or shell composition. The parameterless
binding constructor remains available for normal plugin activation and uses
default successful behavior when no compatibility behavior is present in the
project file. Tests may additionally inject an archive-cleanup failure through
`FakeTiaBehavior`; this failure is not part of the fake project-file format.

The fake engineering session also implements the optional Safety-program
authorization session boundary. Its default requirement is `NotRequired`.
Tests can inject complete requirement and authorization results through
`FakeTiaBehavior` to exercise supported, rejected, and `NotSupported` profile
paths without Siemens software. These controls are test infrastructure and are
not published as binding capabilities or fake project-file fields.

## Verification

From the repository root:

```powershell
dotnet test access-profiles\siemens\tia-openness\fake\PLCSIM.UnitTest.Siemens.TiaOpenness.Fake.sln
```

## Related Documentation

- [TIA Openness binding contract](../docs/contracts/tia-openness-binding-contract.md).
- [Siemens shell composition](../../docs/architecture/implementation/profile-composition.md).
