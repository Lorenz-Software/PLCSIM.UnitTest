# Fake Access Profile Configuration Contract

This document defines the profile-specific configuration payload for the Fake
access profile used by PLCSIM.UnitTest tests and development workflows. The Fake
profile is packaged as a Siemens-independent access-profile plugin and can be
discovered through the normal access-profile package boundary.

The payload is stored under `accessProfile.values` in the shared configuration
file contract.

## Access Profile Id

The stable access profile id for this profile is `fake`. The current stable fake
profile version is `1.0.0`.

```json
{
  "accessProfile": {
    "id": "fake",
    "version": "1.0.0"
  }
}
```

## Payload Shape

```json
{
  "accessProfile": {
    "id": "fake",
    "version": "1.0.0",
    "values": {
      "plcRef": {
        "id": "fake-project-target"
      },
      "runtimeRef": {
        "id": "fake-runtime-target"
      }
    }
  }
}
```

## Fields

| Field | Required For Execution Setup | Meaning |
| --- | --- | --- |
| `plcRef.id` | Yes | Identifier of the fake PLC reference inside the fake project target discovery result. |
| `runtimeRef.id` | Mode-dependent | Existing Fake runtime id for `UseExisting`; omission uses the designated `fake-runtime-target` default. Must be absent for `CreateOwned`. |

For manual import workflows, missing reference fields may still be accepted as a
draft when the user can complete the missing selection in the WPF stepper. Direct
execution setup requires the project reference and mode-appropriate runtime
selection. Schema version 2 stores provisioning intent at
`runtime.provisioningMode` outside the profile payload; omission means
`CreateOwned`. The payload example above therefore requires explicit
`"runtime": { "provisioningMode": "UseExisting" }` at the shared file root.
For `CreateOwned`, remove `runtimeRef`. Invalid explicit runtime ids never fall
back to the default. Loading the setup-only profile session does not itself
prepare these operational targets.

## Stable Standard References

The standard fake services publish these stable reference identifiers:

| Reference | Value |
| --- | --- |
| `accessProfile.id` | `fake` |
| `accessProfile.version` | `1.0.0` |
| `plcRef.id` | `fake-project-target` |
| `runtimeRef.id` | `fake-runtime-target` |

The fake project source used by the shared configuration file can be:

```json
{
  "project": {
    "source": "..\\FakeProjects\\default-fake-project.json"
  }
}
```

The repository contains development sample project sources under
`access-profiles/fake/tests/Resources/FakeProjects/`. They are
Siemens-independent project anchors used by the Fake profile for manual WPF
selection and saved configuration files. The Fake profile currently treats
`.json` project sources as supported during lightweight project-source
selection.

## Validation Semantics

The Fake access profile validates that configured references can be matched
against the fake discovery candidates. Matching is done against the candidate
identifier values, not display names.

The application layer passes the fake profile the project source and
`accessProfile.values`. It must not interpret `plcRef` or `runtimeRef`; it only
transports the payload to the selected access profile.

## Generic Configuration Projection

The loaded Fake profile exposes the required generic profile-configuration
boundary so application and shared-contract tests can exercise every supported
primitive value shape without Siemens software. It projects:

| Field Id | Type | Default / Allowed Values |
| --- | --- | --- |
| `fake.test.name` | String | `Fake run` |
| `fake.test.iterations` | Integer | `1`; positive integer required |
| `fake.test.factor` | Float | `1`; finite invariant `System.Single` required |
| `fake.test.enabled` | Boolean | `true` |
| `fake.test.mode` | String with allowed values | `standard`; `standard` or `diagnostic` |

Projected values are stored below `accessProfile.values.testSettings`. Applying
one value preserves the stable target references and any unknown payload
content. This surface is deterministic test support; it does not change the
Fake profile's project or runtime selection semantics.
