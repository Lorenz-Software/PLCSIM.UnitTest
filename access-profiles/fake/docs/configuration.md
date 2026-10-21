# Fake Access Profile Configuration

This document describes the Fake access profile fields below
`accessProfile.values`. The surrounding configuration file follows the shared
PLCSIM.UnitTest configuration-file contract.

The Fake profile is intended for application development and deterministic
tests. Its configuration surface exercises the generic profile configuration
editor without vendor software.

## Target References

| JSON path | Type | Required | Default | Meaning |
| --- | --- | --- | --- | --- |
| `plcRef.id` | string | Required for execution | None | Selected Fake project/PLC target id. Interactive applications may write this value from target selection. |
| `runtimeRef.id` | string | Only for `UseExisting`; may be omitted to use the designated default | `fake-runtime-target` for `UseExisting` | Explicit existing target; must resolve uniquely. Must be omitted for `CreateOwned`. |

Runtime provisioning is application intent at `runtime.provisioningMode` in
schema version 2, not a field below `accessProfile.values`. Omission means
`CreateOwned`, including legacy schema-version-1 files. `CreateOwned` takes no
existing runtime id; `UseExisting` may use Fake's designated default only when
no id was supplied. An invalid explicit id never falls back.

## Test Settings

All test settings are projected as required fields by the profile configuration
service. When an explicit value is omitted, the listed default is used.

| Field id | JSON path | Type | Default | Constraints |
| --- | --- | --- | --- | --- |
| `fake.test.name` | `testSettings.name` | string | `Fake run` | Must not be empty or whitespace. |
| `fake.test.iterations` | `testSettings.iterations` | integer | `1` | Must be greater than zero. |
| `fake.test.factor` | `testSettings.factor` | float | `1` | Must be a finite floating-point value. |
| `fake.test.enabled` | `testSettings.enabled` | boolean | `true` | `true` or `false`. |
| `fake.test.mode` | `testSettings.mode` | string | `standard` | `standard` or `diagnostic`. |

## Example

The following is an `accessProfile.values` fragment for explicit `UseExisting`
intent. Set `"runtime": { "provisioningMode": "UseExisting" }` at the shared
schema-version-2 configuration root, alongside `project` and `accessProfile`.
Omitting `runtimeRef` from this fragment selects Fake's designated default in
that mode. For `CreateOwned` (also the omitted-mode default), omit `runtimeRef`
instead; the runtime is created during preparation.

```json
{
  "plcRef": {
    "id": "fake-project-target"
  },
  "runtimeRef": {
    "id": "fake-runtime-target"
  },
  "testSettings": {
    "name": "Application test run",
    "iterations": 2,
    "factor": 1.25,
    "enabled": true,
    "mode": "standard"
  }
}
```
