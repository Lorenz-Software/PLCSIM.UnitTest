# Siemens TIA Openness Bindings

This subtree contains Siemens-internal TIA Openness binding projects for the
Siemens access profile.

TIA Openness bindings are internal technology bindings, not generic
access-profile plugins. They expose Siemens-internal engineering contracts to
the Siemens profile shell and keep TIA Portal object models, Siemens API
handles, worker implementation details, and concrete Siemens exceptions below
the binding boundary.

## Use A Binding

A real binding opens or retrieves a project, discovers PLC targets, compiles the
selected target and implements its engineering transfer boundary. Deploy it with
the Siemens profile, then authorize the worker for the Windows execution account
and register the profile. Registration and vendor authorization are separate;
see [Siemens deployment](../docs/deployment.md).

The concrete README owns accepted project/archive formats, required installed
PublicAPI, build/test commands and native qualification limits. Fake bindings
exercise the same contract without vendor software. An implemented transfer
operation or passing deterministic suite is not proof of real transfer support
for every product version.

## Ownership

This subtree owns TIA Openness engineering bindings for the Siemens profile.
Sample bindings and product-version bindings are independent internal
subprojects. Each concrete binding owns its README, solution, source, tests,
resources, and local implementation notes.

Adding another TIA Openness binding should not require updating parent README
files unless the ownership model or contract changes.

## Verification

Use the README in the owning concrete binding subtree.

## Contract

- [TIA Openness binding contract](docs/contracts/tia-openness-binding-contract.md).
