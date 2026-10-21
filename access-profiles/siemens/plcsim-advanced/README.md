# Siemens PLCSIM Advanced Bindings

This subtree contains Siemens-internal PLCSIM Advanced binding projects for the
Siemens access profile.

PLCSIM Advanced bindings are internal technology bindings, not generic
access-profile plugins. They are expected to own runtime preparation/control,
UnitTest lifecycle communication, event-stream intake, release/backpressure,
health, diagnostics, and Siemens API cleanup below the Siemens profile shell.

## Use A Binding

Deploy a compatible binding package with the Siemens profile and register that
profile for the execution account. The profile selects bindings from their
manifest and installed-environment evidence; applications select the profile,
not a Runtime API DLL. Real bindings load the vendor API from an installed,
licensed PLCSIM Advanced product, while deterministic fake bindings need no
vendor installation.

For supported products, prerequisites, build commands, observation strategies
and qualification limits, read the selected binding's README. Package presence
alone does not prove readiness, and compatibility API coverage does not replace
native product qualification.

## Ownership

This subtree owns PLCSIM Advanced runtime and UnitTest communication bindings
for the Siemens profile. Product-version bindings are independent internal
subprojects. Each concrete binding owns its README, solution, source, tests,
resources, and local implementation notes.

Adding another PLCSIM Advanced binding should not require updating parent
README files unless the ownership model or contract changes.

## Related Documentation

- [PLCSIM Advanced binding contract](docs/contracts/plcsim-advanced-binding-contract.md).
- [EventStream workload sizing and orchestrator guidance](docs/performance/event-stream-workload-sizing.md)
- [Access profile runtime contract](../../../docs/contracts/access-profile-runtime-contract.md).
