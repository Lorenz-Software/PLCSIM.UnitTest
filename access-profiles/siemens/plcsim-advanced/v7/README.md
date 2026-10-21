# Siemens PLCSIM Advanced V7 Binding

This subtree contains the independently buildable PLCSIM Advanced 7.0 binding
for the Siemens access profile.

The binding implements local runtime discovery and preparation, attached and
owned runtime lifecycle, runtime-state observation, PLC start/stop control,
in-place reconnect, UnitTest communication, RunControl commands and
observation, adaptive bounded-prefix EventBuffer intake and release, stale
TagList revalidation, logging, cancellation, and bounded cleanup against the
Siemens Runtime API 7.0 contract.

For real operation, install and license the matching native PLCSIM Advanced
product, deploy the binding with the Siemens profile and register the profile.
The [prerequisites and troubleshooting guide](docs/troubleshooting.md) owns
installation/selection checks. A test program with the expected UnitTest
communication contract is required for communication validation.

## Package And Selection

The package contains a `net8.0` facade and a dedicated `net48` x64 worker. The
worker loads the Siemens mixed-mode Runtime API from the installed product; the
Siemens DLLs are not redistributed with this package.

The V7 and V8 binding packages may be deployed together. Normal Siemens-profile
registration resolves readiness for each package:

- V7 requires `API\7.0`, interface version `0x00070000`, and the native V7
  Runtime API product version `7.0.0.0`.
- The API 7 compatibility DLL delivered by PLCSIM Advanced V8 reports product
  version `7.0.0.1` and is therefore not accepted as a native V7 installation.
- A nonmatching product is published as unavailable, not as a broken package.

The application and Siemens profile contain no hard-coded V7 selection. They
use the normal manifest, readiness, capability, and binding-selection path.

## Supported Behavior And Limits

- Runtime access is local; remote Runtime API connections are not supported.
- One selected simulated PLC instance forms the active runtime target.
- `UseExisting` does not own, power, unregister, or delete the selected
  instance. `CreateOwned` owns creation and destructive release.
- Polling and PLC-cycle observation are implemented communication strategies.
  PLC-cycle observation uses `RegisterOnSyncPointReachedEvent()`, bounded
  `WaitForOnSyncPointReachedEvent(...)` calls, and
  `UnregisterOnSyncPointReachedEvent()` during cleanup. It does not fall back
  to polling automatically.
- Both strategies publish EventBuffer monitoring-state transitions independently
  from EventBatch delivery, including BufferFull, lost-event counters, sequence
  watermarks, and recovery. This path is covered by deterministic worker and
  binding tests. Real overflow/reset end-to-end validation has only been
  performed with the V8 binding and runtime.
- Runtime reconnect revalidates the existing worker and resource in place.
  Automatic worker replacement and storage rehydration are not supported.
- Command success means that the Siemens API call succeeded. PLC and UnitTest
  state changes remain independently observed.
- Abort is implemented at the API/contract boundary but was not validated with
  a long-running PLC test program.

## Worker And Logging

The facade communicates with
`PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7.Worker.exe` over the
shared Siemens named-pipe protocol. The worker owns Runtime API objects,
subscriptions, polling, EventBuffer release, and process-local cleanup.

Its log family is:

```text
access-profile-<profile-id>-plcsim-advanced-v7-worker-<date>.log
```

Logging inherits the access-profile log level and bounded file policy. Raw
UnitTest event logging is disabled by default and is effective only when it is
explicitly enabled together with the `Debug` access-profile level.

## Build And Verification

From the repository root:

```powershell
dotnet build access-profiles\siemens\plcsim-advanced\v7\PLCSIM.UnitTest.Siemens.PlcsimAdvanced.V7.sln
dotnet test access-profiles\siemens\plcsim-advanced\v7\PLCSIM.UnitTest.Siemens.PlcsimAdvanced.V7.sln
```

The default tests are deterministic and do not require Siemens software. The
test project also contains environment-variable-gated real-runtime scenarios:

- `PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V7_WORKER_SESSION`
- `PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V7_CREATEOWNED`
- `PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V7_EXISTING_STARTSTOP`
- `PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V7_COMMUNICATION`
- `PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V7_LIFETIMES`

The implementation was validated in two distinct environments:

- API-contract compatibility against the API 7 surface delivered by an
  installed PLCSIM Advanced V8 product;
- package registration, native V7 selection, and manual real-product operation
  against an installed PLCSIM Advanced V7 VM.

The compatibility validation does not substitute for the native V7 evidence;
product-version discrimination keeps both deployment cases deterministic.

## Related Documentation

- PLCSIM Advanced binding contract:
  `../docs/contracts/plcsim-advanced-binding-contract.md`
- V7 prerequisites and troubleshooting:
  `docs/troubleshooting.md`
