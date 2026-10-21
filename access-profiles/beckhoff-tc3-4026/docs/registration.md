# Registration And Source Inspection

Registration inspects local installation evidence and writes only to the
host-supplied profile data directory. It does not activate COM, start XAE, connect
ADS, enumerate live PLC applications, change system mode or validate licenses.

## Prerequisite Evidence

- Read TwinCAT `InstallDir` from the 32-bit and 64-bit views of
  `HKLM\SOFTWARE\Beckhoff\TwinCAT3\3.1`.
- Inspect `Components\Base\Build_4026.*\TwinCAT System Manager.x64.dll`
  below those roots and retain its product version. Multiple installed 4026
  patches are recorded, not reduced to an assumed preferred version.
- Inspect `Runtimes\bin\TcSystemServiceUm.exe` for the 4026 Usermode component.
- Resolve the x64 COM registration of `TcXaeShell.DTE.17.0` to its executable
  and verify that the file exists. Do not execute the registered command.
- Inspect the shell's `PublicAssemblies/Microsoft.VisualStudio.Interop.dll`
  and validate its assembly identity without loading or activating it. A shell
  lacking this file is not published as usable. Invalid or unreadable metadata
  is an error. Engineering loading uses the same prerequisite check.

Missing components are unavailable, not probe errors. Access failures and invalid
version/registration metadata produce `Error`. Other TwinCAT build families are
not accepted as a 4026 fallback. Installation evidence alone does not establish
that a specific Runtime instance is reachable or licensed.

The XAE ProgID is documented in Beckhoff's
[Visual Studio host selection](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242746251.html).
Resolving `CLSID`/`LocalServer32`/`ServerExecutable` follows the documented
[Windows COM registration](https://learn.microsoft.com/en-us/windows/win32/com/localserver32).
The precise `TwinCAT3\3.1\InstallDir` key and both registry views are also used
by the vendor-shipped `SDK\Build\MsBuild\TcPublish\TcPublish.targets` and
`TcSign\TcSign.targets`. Public deployment documentation sometimes abbreviates
the key without `\3.1`; the installed vendor tooling confirms the actual key.

Beckhoff documents the [Usermode installation directory](https://infosys.beckhoff.com/content/1033/tc170x_tc3_usermode_runtime/16309326731.html).
The exact engineering component subdirectory/DLL search pattern is an installation
layout assumption verified on 3.1.4026.26, not a published discovery-API stability
guarantee for every future patch. Missing expected files remain unavailable;
do not infer successful installation or live readiness from a registry root alone.

## Capabilities

The shared capability DTOs distinguish installed API prerequisites from functional
project and runtime support. Detected components are published as `twincat.xae`
and `twincat.usermode` API entries with their actual product versions.

With the 4026 XAE shell, its Visual Studio interop, engineering components and
Usermode components present, registration returns `Registered`.
It publishes usable `.sln` and `.tszip` project
capabilities, the local Usermode runtime capability and UnitTest contract `1.0.0`.
The current late-bound engineering implementation has offline coverage and
real-runtime workflow qualification with the recorded XAE 3.1.4026.27 and local
Usermode Runtime environment. This does not establish the Runtime patch version.
The exact tested workflows, distribution checks, and remaining limits are owned
by the [verification guide](verification.md); registration itself is not a live
workflow qualification.

If any prerequisite is missing, registration returns `Unavailable`, keeps the
installed API evidence, marks execution capabilities unavailable and advertises
no usable UnitTest contract. Runtime-only installations are not part of this
profile's qualified installation scope. Registration does not connect to a
Runtime or certify its current application, system mode or license validity.
No `TCatSysManagerLib.dll` or imported type-library artifact is a prerequisite.

## Stored Registration

`<ProfileDataDirectory>/profile-registry.json` contains the schema version,
profile id/version, package root, registration timestamp, capability result and
installation evidence. Writes use a temporary file followed by replacement, not
an in-place update. Cancellation before replacement preserves the previous file.
The destination must be outside the installed package tree.

Source evaluation reads that snapshot without rerunning environment discovery or
creating missing registration data. Missing/corrupt data, incompatible schema,
or mismatching package identity/root return `Error` and require registration.
An unavailable capability returns `Unsupported`; neither case starts engineering.
Registration cannot overlap an active session or another registration on the same
plugin instance. Disposal waits for an outstanding registration to finish.

Package freshness remains the host registration service's responsibility. After
changing installed TwinCAT components, explicitly refresh registration; source
selection does not continuously revalidate vendor files. Runtime preparation
still checks the selected target independently of registration evidence.

## Lightweight Source Inspection

The metadata inspector reads a solution's referenced `.tsproj` files or the
`.tsproj` entries inside a `.tszip` archive. It verifies the TwinCAT system-project
structure and the `3.1.4026` version family without extracting the archive or
opening XAE. It does not change project pinning or demand an exact patch match.

A source without a TwinCAT system project, or outside the 4026 family, is
unsupported. Malformed metadata, missing referenced system projects and read
failures are errors. XML external entities are disabled; oversized metadata and
ambiguous duplicate archive entries are rejected. This is not a complete project
dependency, PLC-target or transfer validation. Those checks belong to preparation.

The inspector is tested independently. Public source evaluation additionally
requires usable registered project capabilities.
