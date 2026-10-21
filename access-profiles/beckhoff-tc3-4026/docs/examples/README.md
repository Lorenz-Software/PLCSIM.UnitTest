# Configuration Examples

These are templates, not ready-to-run jobs. Replace the project paths, PLC
target ID and, where present, AMS Net ID before use. The example Net ID is not
a real discovered target or a value to derive from an IP address.

| File | Purpose |
| --- | --- |
| [managed-solution.json](managed-solution.json) | Complete schema-version-2 configuration for WPF Load UnitTest or CLI: direct solution, explicit existing local Runtime, polling defaults 10/1/32. |
| [managed-archive.json](managed-archive.json) | Complete configuration: archive, owned extraction directory, explicit UseExisting with omitted Runtime ID selecting the local standard Usermode Runtime. Communication defaults are omitted. |
| [subscription-values.json](subscription-values.json) | Profile-values fragment only, not a complete configuration. Demonstrates opt-in subscription with the same 10/1/32 settings. |

For managed jobs, `plcRef.id` is the stable identity returned by project discovery,
not a display name. The CLI requires it even for a single-target project. There
is no CLI target-list command or WPF configuration-export command in this version.
For authoring a configuration, the current profile constructs that identity as:

```text
system:<URI-escaped lowercase source-relative .tsproj path>/plc:<PLC project GUID>
```

Use `/` in the relative path before applying `Uri.EscapeDataString`. The base
is the solution directory for direct input, or the imported workspace root for
an archive. The GUID is the selected PLC project's `GUID` under
`TcSmProject/Project/Plc/Project` in the system project, not the containing
system's `ProjectGUID`. Format it lowercase with hyphens and without braces.
For example, `LibraryTests.tsproj` next to the solution and PLC GUID
`{12345678-1234-1234-1234-123456789ABC}` produce:

```text
system:librarytests.tsproj/plc:12345678-1234-1234-1234-123456789abc
```

Discovery must still match the resulting ID before execution. Recheck it when
moving a system project within the workspace or replacing a PLC project. An
archive can have a different relative layout from the original solution tree.

The managed PLC port is derived from that project; the examples intentionally
omit `runtimeRef.plcPort`. Add it only as an explicit matching constraint.
For external WPF debugging, supply the actual port (851 if omitted) and choose
external project handling; a CLI managed-run file is not an external-mode switch.

To use subscription in a managed template, change only
`accessProfile.values.unitTest.observationStrategy` to `subscription` while
retaining the managed target fields. Do not replace the entire values object
with the fragment and lose `plcRef.id`.

Both full templates use `UseExisting` deliberately. Neither requests Runtime
creation, enables boot autostart nor activates licenses. A direct source can be
modified by XAE; supply an exclusive workspace. The archive example fails if
the default Runtime is unavailable rather than choosing another instance.

Follow [CLI and CI execution](../ci-execution.md) for registration, validation,
execution, readiness and artifact handling. See [usage](../usage.md) for WPF
workflows and [configuration](../configuration.md) for every profile field.
