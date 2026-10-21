# Profile Dependencies

## Installed Engineering Interop

Engineering uses late-bound Beckhoff COM in the owned DTE/XAE host and the
official `Microsoft.VisualStudio.Interop.dll` supplied by TcXaeShell for typed
Visual Studio operations. The remaining assembly reference has `Private=false`
and `EmbedInteropTypes=false`: it is neither copied into the profile package
nor embedded as local COM declarations. No `TCatSysManagerLib.dll` reference,
TLB import toolchain or generated/embedded Beckhoff interface metadata is needed.

The default build path is:

- `%ProgramW6432%\Beckhoff\TcXaeShell\Common7\IDE\PublicAssemblies\Microsoft.VisualStudio.Interop.dll`

For another installed build layout, set `XaeInteropAssemblyPath`. A missing file
fails the build with an explicit prerequisite error. Registration and engineering
loading both inspect `PublicAssemblies/Microsoft.VisualStudio.Interop.dll`
relative to the detected registered shell and validate its assembly identity.
Missing interop makes the shell unavailable; invalid or unreadable metadata is
an error. Engineering loads the assembly into the profile's load context before
using its types and rejects a conflicting already-loaded identity. The build
override does not override runtime installation discovery. Profile loading and
offline test doubles do not activate XAE or load installed interop assemblies.

The installed native XAE/PLC engineering remains necessary; late binding is not
an installation-free substitute. The installed products' licensing remains
applicable. Native Automation Interface and Visual Studio interop are classified
as non-redistributed prerequisites in the dependency inventory; no additional
license text is copied into the package for them. Review rights and notices
before shipping vendor binaries or generated/embedded vendor metadata. ADS MIT
terms do not supply that permission.

The integration follows Beckhoff's
[Automation Interface reference setup](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242715915.html)
and uses the official
[EnvDTE80 ToolWindows interface](https://learn.microsoft.com/en-us/dotnet/api/envdte80.toolwindows?view=visualstudiosdk-2022).
Managed adapters provide offline test seams; they do not redefine the COM ABI.

## Reviewed Baseline

Review date: 2026-10-04. The selected implementation baseline is the official
[`Beckhoff.TwinCAT.Ads` 7.0.389 package](https://www.nuget.org/packages/Beckhoff.TwinCAT.Ads/7.0.389)
for `net8.0`. The profile project now references this exact version and retains
the resolved closure in `packages.lock.json`. Do not substitute a different ADS
DLL found in another installed product. Live qualification and its limits are
recorded in [package verification](verification.md#runtime-and-event-integrity).

An isolated restore against nuget.org with an exact root version and a generated
lock file resolved the following closure. A second restore in locked mode
succeeded. NuGet auditing, including transitive dependencies, reported no known
vulnerabilities at the review date; this is not a security guarantee.

| Package | Resolved version | Declared license |
| --- | --- | --- |
| Beckhoff.TwinCAT.Ads | 7.0.389 | MIT, packaged `License.md` |
| Beckhoff.TwinCAT.Ads.Abstractions | 7.0.389 | MIT, packaged `License.md` |
| Beckhoff.TwinCAT.Ads.ConfigurationProviders | 7.0.389 | MIT, packaged `License.md` |
| Beckhoff.TwinCAT.Ads.Server | 7.0.389 | MIT, packaged `License.md` |
| Microsoft.Extensions.Configuration | 8.0.0 | MIT |
| Microsoft.Extensions.Configuration.Abstractions | 8.0.0 | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT |
| Microsoft.Extensions.Primitives | 8.0.0 | MIT |
| System.ComponentModel.Composition | 8.0.0 | MIT |
| System.Diagnostics.EventLog | 8.0.1 | MIT |
| System.Reactive | 6.1.0 | MIT |
| System.ServiceProcess.ServiceController | 8.0.1 | MIT |

All but the first entry are transitive. The server support assembly belongs to
the client dependency graph; its presence does not require the profile to host
an ADS server or install its own router. The installed TwinCAT environment
remains a prerequisite.

The restore selected `net8.0` library assets, except for the compatible `net6.0`
asset of `System.Reactive`. EventLog and ServiceController also provide Windows
runtime-specific assets, including `System.Diagnostics.EventLog.Messages.dll`.
The final Windows publish output must retain the selected assets; copying only
the top-level ADS DLL is insufficient. Changing target framework, runtime
identifier or additional profile references can change the resolved closure.

## Redistribution Obligations

The reviewed licenses permit redistribution with their copyright and permission
notices. This managed-library review does not license TwinCAT XAE/XAR or provide
PLC execution licenses; see [runtime licensing](licensing.md).

- **Beckhoff:** all four packages contain identical MIT `License.md` files with
  the copyright `2024 Beckhoff Automation GmbH & Co. KG`. Preserve that original
  text; do not replace its year with the different nuspec copyright metadata.
  One shared copy can serve the four explicitly attributed package entries.
  No separate NOTICE file was found in these packages.
- **Microsoft:** all eight packages declare MIT and contain an identical
  `LICENSE.TXT`, byte-identical to the existing canonical
  `distribution/licenses/texts/dotnet-mit.txt`. Reuse that text and retain each
  package's copyright metadata. Their `THIRD-PARTY-NOTICES.TXT` files are
  runtime-wide collections, not a list of dependencies used by every library.
  Review of the selected assets and source project inclusions identified no
  additional applicable text for this bounded closure. Do not copy the complete
  collections indiscriminately.
- **System.Reactive:** the package declares MIT and the .NET Foundation and
  Contributors copyright, but contains no separate license text. The
  [upstream license at its package source revision](https://github.com/dotnet/reactive/blob/f4da16f15a3cde97f178396ea6e3489cc893651f/LICENSE)
  matches the existing canonical .NET MIT text after line-ending normalization.
  Reuse that text with explicit component attribution. No additional NOTICE
  file was found in that source revision.

The Microsoft scope covers configuration, logging/DI abstractions, primitives,
composition and Windows event-log/service wrappers. It does not redistribute
CoreCLR, compression libraries, globalization data or the native libraries
enumerated in the runtime-wide notices. The EventLog message resource belongs
to the reviewed EventLog component. Build-only analyzers are not runtime
payload. Different deployment modes or additional assets require a fresh review.

Supporting Microsoft sources are the
[library-package licensing guidance](https://github.com/dotnet/core/blob/main/license-information.md)
and source project inclusions at the revisions recorded in the restored nuspecs:
[8.0.0 libraries](https://github.com/dotnet/runtime/tree/5535e31a712343a63f5d7d796cd874e563e5ac14/src/libraries),
[logging update](https://github.com/dotnet/runtime/tree/eba546b0f0d448e0176a2222548fd7a2fbf464c0/src/libraries),
and [DI/EventLog/ServiceController updates](https://github.com/dotnet/runtime/tree/81cabf2857a01351e5ab578947c7403a5b128ad1/src/libraries).
This is a release-content review, not a legal warranty or a blanket approval of
future packages.

## Production Integration Checks

Maintain these checks when changing the production reference or packaging:

1. Pin the reviewed ADS version and compare the actual resolved closure with
   this matrix. Do not assume pinning only the root package fixes all transitive
   versions in a larger project.
2. Update `distribution/licenses/third-party-inventory.json` for all new package
   versions, add the original Beckhoff MIT text, and select the shared .NET MIT
   text for the remaining packages. Record explicit additional-text decisions
   through the existing notice tooling. Do not add unused dependencies to the
   production inventory merely to record this preliminary review.
3. Run `tools/test-third-party-notices.ps1` and verify the actual profile package's
   generated notices and payload. Installed XAE/COM interop dependencies require
   their own deployment classification; this ADS review does not authorize
   redistribution of arbitrary installed vendor binaries.
4. Test package loading and ADS use through the existing host load context.
   `ILoggerFactory` is shared with the host. Both the inspected host assembly
   and Logging.Abstractions 8.0.3 have assembly identity version `8.0.0.0` and
   public-key token `adb9793829ddae60`, so there is no identity mismatch in that
   check. This does not prove every ADS logging call is compatible with the
   host-supplied implementation. Do not change host dependencies implicitly.
5. Repeat essential reads, writes, notifications and recovery with ADS V7 on
   .NET 8. Earlier ADS 4.x probes prove API-path feasibility, not qualification
   of this package version. Engineering on the selected in-process STA path
   likewise needs its own production-stack verification.

## Verified Package Integration

The profile's production restore matches all 13 entries above and succeeds in
locked mode. The dependency inventory includes these versions and the original
Beckhoff MIT text. The notice-tool self-tests and notice generation against the
Release publish payload pass. The portable publish payload retains the Windows
runtime-specific assets rather than copying only top-level DLLs.

Offline package tests use the existing application loader and its host-shared
Logging.Abstractions 8.0.0, while the package retains its resolved 8.0.3 dependency.
ADS client construction, logging-factory use, AMS Net ID parsing and client
disposal succeed in that load context without connecting to a target. This
verifies the exercised load path, not every ADS API or recovery path. That
dependency-integration check did not change application dependencies or shared
contracts. Subsequent production-stack and application evidence is recorded in
[current qualification](verification.md#current-qualification); package loading
alone is not a substitute for those live checks.
