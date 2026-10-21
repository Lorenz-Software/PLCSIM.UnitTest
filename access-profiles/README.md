# Access Profiles

Access profiles are the boundary between the PLCSIM.UnitTest application/core
workflow and concrete project/runtime technologies.

For application use, choose a deployed profile and start with its README,
`docs/configuration.md` and operating guide. The profile's manifest identifies
the package; registration records available capabilities for the execution
account. A package being discoverable is not proof of live target readiness.

For profile development, this directory owns the integration boundary, not PLC
test definitions or application workflow policy. Shared interfaces stay vendor
neutral; profile-specific prerequisites, supported source formats, runtime
ownership and native qualification belong to the profile itself.

An access profile owns:

- profile-specific configuration validation;
- project and runtime service composition;
- profile package metadata and registration behavior;
- profile-owned diagnostics, health, cleanup, and recovery behavior;
- profile-owned tests, sample project sources, and configuration samples.

Technology-specific bindings that are only meaningful for one profile stay
below that profile subtree. They are treated as independent internal
development paths with their own focused solutions and tests, but they are not
generic access-profile plugins unless their contract explicitly says so.

## Development Boundaries

- A profile's source directory is `access-profiles/<profile.id>/`, matching its
  manifest identity rather than only its vendor name.
- Do not append the profile's release version to its source directory name;
  that version is declared in the manifest. A technology version belongs in
  the directory name only when it is part of a distinct profile identity.
- A profile subtree owns its own README, source, tests, local documentation, and
  resources.
- A profile may own technology-specific internal bindings when those bindings
  are not generic access-profile plugins.
- Version-specific binding folders are implementation paths under a
  versions-neutral technology subtree. Adding another binding version should not
  require updating repository-level README files unless the development model
  changes.
- Parent documentation must describe stable rules and ownership only. Concrete
  implementations, solutions, resources, and verification commands belong to
  the owning subtree.

These are repository structure rules, not additional loader requirements.
Installed package identity and version remain defined by the manifest, not by
the package directory name. See the
[access-profile package contract](../docs/contracts/access-profile-package-contract.md).

## Configuration Documentation

Every directory-package access profile owns a `docs/configuration.md` document
inside its deployed package. The fixed document describes all fields below
`accessProfile.values`; parent documentation and applications do not duplicate
those profile-specific field definitions. Profile-internal binding packages do
not provide a second copy.

## Verification

Run profile-specific build and test commands from the owning subtree README or
the [build foundation](../docs/development/solution-and-build-foundation.md).
Normal application builds do not deploy profiles. Use explicit internal
deployment for a local workbench, and do not equate deterministic tests with
real engineering or runtime qualification.
