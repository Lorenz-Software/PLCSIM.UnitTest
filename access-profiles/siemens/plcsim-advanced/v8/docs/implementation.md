# PLCSIM Advanced V8 Worker Implementation

This document describes the V8 facade/worker implementation, ownership and
logging. The [binding README](../README.md) provides setup/build entry points;
[binding validation](validation.md) separates deterministic and native evidence.
These internal components do not add public profile or binding APIs.

## Facade worker-session structure

The net8 facade keeps the binding-facing worker session behind one internal
coordinator while assigning the technical responsibilities to four internal
components:

- `PlcsimAdvancedV8WorkerBootstrap` resolves only the worker executable deployed
  beside the binding assembly, starts it, establishes the named pipe, validates
  the initial handshake, and cleans up a partially opened connection.
- `PlcsimAdvancedV8WorkerTransport` owns the worker process, pipe, reader,
  writer, serialized command gate, request correlation, response queue, reader
  lifetime, normal close, and forced termination.
- `PlcsimAdvancedV8WorkerEventRouter` owns subscriptions, asynchronous event
  publication, terminal runtime-state replay, and connection-loss deduplication.
- `PlcsimAdvancedV8WorkerMessageMapper` is the single protocol-to-contract
  conversion boundary for command results, diagnostics, evidence, runtime and
  RunControl payloads, EventBuffer batches, and asynchronous worker events.

`PlcsimAdvancedV8WorkerSessionHandle` remains the binding-facing coordinator. It
owns operation sequencing, cancellation decisions, and runtime-availability
orchestration, but does not own process bootstrap, pipe lifetime, event
publication, or protocol-to-contract construction. No component adds a public
implementation surface or changes the named-pipe protocol.

Ownership transfers from bootstrap to the transport exactly once after a valid
worker handshake. Closing or terminating the session disposes the transport and
therefore cancels the reader, completes response intake, closes the pipe, and
ends the worker process. Facade communication and runtime sessions unsubscribe
from worker events on close; terminal connection-loss publication remains
available for late runtime-state subscribers without retaining communication
handlers.

### Production and validation worker boundary

The released binding package contains only
`PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker.exe`. This
production worker accepts the Runtime API directory required for real PLCSIM
Advanced operation. It has no validation-behavior API, command-line switch, or
runtime-selectable validation scenario.

Deterministic process and IPC tests use the test-owned net48 validation worker
below `tests/worker/ValidationWorker`. It implements controlled runtime,
RunControl, EventStream, timeout, malformed-response, cancellation, and cleanup
scenarios without loading the Siemens Runtime API. The validation worker is
built by the V8 test project and is never copied by `DeployPluginPackage`.

Both workers compile the internal protocol, logging, and EventBuffer snapshot
helpers from `worker/shared`. The validation worker additionally compiles the
production dispatch/session sources and enables its test handlers at compile
time through `VALIDATION_WORKER`. This source-level seam does not add a public
application, profile, or binding contract and does not make validation behavior
available in the compiled production artifact.

### Failed owned-runtime preparation

After `RegisterInstance`, failures while assigning `StoragePath` or powering on
the owned instance trigger worker-owned compensation. The worker attempts
`PowerOff` and then `UnregisterInstance` and returns the original preparation
failure together with bounded instance identity and both compensation-step
results.

A successful unregister proves rollback and returns `Failed` without exposing a
runtime. A failed power-off remains a separate warning when unregister still
proves removal. A failed or unprovable unregister returns `Unclear`, exposes the
instance id, name, and storage path as recovery evidence, and terminates the V8
worker session because its resource state is no longer trustworthy. The caller
must open a new runtime session and manually inspect/remove the evidenced PLCSIM
instance before retrying. Normal worker close releases technical API/IPC
resources only; it does not hide the unclear state behind another destructive
cleanup attempt.

## Operational Logging

The binding facade communicates with the worker over the Siemens named-pipe IPC
boundary. Logging is independent of that protocol. The worker writes a
dedicated
`access-profile-<profile-id>-plcsim-advanced-v8-worker-<date>.log` family below
the application-owned log directory. It inherits the access-profile minimum
level and bounded file policy and uses an asynchronous file sink behind the
normal synchronous `ILogger` facade.

Entries identify the access profile, binding and worker instance, process,
runtime session, communication session, PLC instance, and available operation
correlation. Normal log levels contain bounded lifecycle and operation
summaries. Poll iterations and individual EventBuffer entries are not logged at
normal levels. Raw UnitTest event logging is disabled by default and is
effective only when explicitly enabled together with the `Debug` access-profile
log level. Disabling it avoids formatting event payloads on the EventStream hot
path.

Worker files use daily and size-based rolling according to the
application-owned policy. The current application policy limits each file to
10 MiB and retains at most five files per worker log family. Standard error is
reserved for bounded fallback evidence when logger initialization fails or the
worker terminates through an unexpected fatal path; it is not a regular logging
or protocol channel.
