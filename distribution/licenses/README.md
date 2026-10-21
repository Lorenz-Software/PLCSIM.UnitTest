# Release License Sources

`third-party-inventory.json` records reviewed production dependency versions.
The release scripts generate notices from this inventory and the actual package
payload. Generated `THIRD-PARTY-NOTICES.txt` files are not committed.

Canonical texts in `texts/` are upstream originals:

- `dotnet-mit.txt`: `Microsoft.Extensions.Logging` 8.0.0, `LICENSE.TXT`.
- `apache-2.0.txt`: https://www.apache.org/licenses/LICENSE-2.0.txt,
  used by the listed Serilog packages.
- `beckhoff-mit.txt`: the Beckhoff MIT text recorded as `MIT-Beckhoff` in
  `third-party-inventory.json`, used by the reviewed Beckhoff ADS packages.

Each license has an explicit `includeText` decision and reason. Each package
version has an explicit `additionalTextFiles` list, including an empty list when
no additional text is selected. The generator does not discover and copy files
by name. Package copyright metadata remains associated with its component.

Microsoft and Beckhoff MIT texts and Apache 2.0 are included as selected by
the inventory. Inno Setup's binary-distribution
conditions require preserving its embedded notices, not adding a separate full
license copy. The original compiler and its embedded notices are not modified.

## Reviewed Scope

The inventory also covers the Beckhoff ADS components selected for the TwinCAT
profile. Their component-specific copyright metadata and the separate canonical
Beckhoff MIT text remain associated with those dependencies; the Microsoft
review below is not their licensing source. This clarification adds no new
dependency classification or notice obligation.

The selected Microsoft library packages declare MIT. Their copyright metadata
is retained per component. No additional attribution requirement was identified
for the distributed managed libraries; the .NET runtime-wide NOTICE collections
are not copied wholesale. In particular:

- `NETStandard.Library` and `Microsoft.NETCore.Platforms` supply build/reference
  assets, not selected runtime DLLs.
- The backport libraries provide managed buffers, memory, vectors, tuples and
  task helpers, not CoreCLR, native compression, globalization or graphics.
- `DiagnosticSource` includes `HexConverter`, but the separately attributed
  SIMD hex parser is guarded by `SYSTEM_PRIVATE_CORELIB` and is not compiled
  into this library.
- Framework-dependent application EXEs embed the Windows .NET apphost. It is
  included in the MIT attribution; the separately installed runtime is not
  redistributed. This review does not cover self-contained or single-file
  runtime distribution.

Sources: the exact restored package metadata and license files,
[Microsoft's .NET licensing guidance](https://github.com/dotnet/core/blob/main/license-information.md),
[Windows binary licensing](https://github.com/dotnet/core/blob/main/license-information-windows.md),
the [apphost source](https://github.com/dotnet/runtime/tree/v8.0.0/src/native/corehost/apphost),
[HexConverter compilation guards](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/Common/src/System/HexConverter.cs),
and the [CoreFX library source families](https://github.com/dotnet/corefx/tree/v2.1.0/src).
This is a bounded release-content review, not a legal warranty or a blanket
classification of future .NET distributions.

For an update, review the new package's license and NOTICE files, update this
inventory, explicit inclusion decisions and affected original texts, then
rebuild the affected release packages. A new or changed dependency version,
SDK major version or deployment mode must not bypass this review.
The installer combines the notices already delivered by its pinned packages;
independent profile/plugin releases remain responsible for their own notices.

Run `./tools/test-third-party-notices.ps1` for deterministic generator regression
checks, followed by the package and Windows release verification scripts for
the actual distribution.
