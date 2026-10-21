# Siemens Worker IPC Contract

This contract defines the Siemens-owned IPC boundary between a shell-facing
`net8` Siemens technology binding facade and an API-adjacent worker process.

The contract is currently owned by the Siemens access profile because the first
use cases are TIA Openness and PLCSIM Advanced bindings. The technical pattern
is intentionally generic enough to be promoted later if another access profile
needs the same worker boundary.

## Scope

The contract covers:

- process-local facade/worker communication;
- Windows named-pipe transport ownership;
- the common message envelope;
- worker startup, connect, initial response, command correlation, post-dispatch
  cancellation, close, timeout, and broken-pipe behavior;
- the role of standard output and standard error.

The contract does not define binding-specific commands or payloads. TIA
Openness and PLCSIM Advanced binding contracts define their own command and
payload semantics on top of this IPC envelope.

## Transport

Production Siemens API-adjacent workers communicate with their binding facade
through Windows named pipes.

The worker process owns the named-pipe server endpoint. The `net8` binding
facade starts the worker process, passes a unique pipe name to it, and connects
as the named-pipe client.

Pipe names must be unique per worker session. They must not encode user secrets
or application credentials.

The facade owns worker process lifetime from the shell-facing side. The worker
owns Siemens API objects and worker-local resources below the pipe boundary.

## Worker Location

Worker binaries are binding-package-local implementation details. The Siemens
profile shell discovers and loads the binding facade through the binding package
manifest; it does not discover, start, or control worker executables directly.

A binding package that requires a worker must ship the worker binary with the
binding facade. The concrete binding implementation owns the relative lookup
rule for its worker. For production packages, the primary lookup rule should be
relative to the loaded binding assembly or package root, typically beside the
binding facade DLL and `manifest.json`.

Repository or build-output fallback paths may exist for local development and
tests, but they must not be required by deployed binding packages.

## Standard Streams

Standard output and standard error are not protocol channels.

Standard error may be captured as technical fallback evidence when the worker
cannot report a structured protocol response, for example:

- worker crash before pipe connect;
- worker crash before initial `Ready` or `Error`;
- unhandled framework or Siemens assembly load failure;
- unexpected fatal failure outside the controlled protocol path.

Application-facing decisions must not depend on standard error. Authoritative
operation results and operation evidence must be reported through the named-pipe
protocol.

## Message Envelope

The current protocol marker is:

```text
SIEMENS-WORKER/1
```

Messages are encoded as one line of UTF-8 text:

```text
SIEMENS-WORKER/1|type=<message-type>|key=<escaped-value>|...
```

Rules:

- the first segment is the protocol marker;
- each following segment is `key=value`;
- `type` is required;
- values are URI-escaped;
- unknown keys must be ignored unless a binding-specific contract marks them
  required;
- an unsupported protocol marker must be treated as a protocol error.

This line-based envelope is the Slice 1 format. If larger payloads or binary
event batches become necessary, they must be added as an explicit versioned
extension or a new protocol version.

## Common Message Types

The common envelope reserves these message types:

| Type | Direction | Purpose |
| --- | --- | --- |
| `Ready` | Worker to facade | Worker initialized the requested session or operation boundary. |
| `Error` | Worker to facade | Worker failed before or during a protocol operation. |
| `Close` | Facade to worker | Request deterministic worker/session cleanup and process exit. |

Binding-specific contracts own additional message types. For example, TIA
Openness may define target discovery/build/transfer messages, while PLCSIM
Advanced may define runtime and event-buffer messages.

## Startup And Initial Response

The facade starts the worker process and passes:

- the unique pipe name;
- binding-specific startup arguments needed to initialize the worker;
- no application secrets unless the owning binding contract explicitly allows
  them.

The worker creates the named-pipe server endpoint and waits for the facade
client connection.

After pipe connection, the worker must send exactly one initial `Ready` or
`Error` response for the requested startup/session boundary.

The facade must bound:

- worker process start;
- pipe connect;
- initial response read.

If the worker exits before pipe connect or before the initial response, the
facade reports a worker-start/no-response failure and may attach captured
standard-error content as fallback evidence.

## Command Handling

Commands are binding-specific messages sent from facade to worker after the
initial `Ready` response.

The facade must validate worker responses before mapping them to Siemens
binding DTOs. Siemens API objects, native handles, process objects, pipe objects,
and worker-local state must not cross the facade/worker boundary.

Malformed, incomplete, unsupported, or mismatched responses must produce a
structured binding failure. They must not be silently ignored.

## Request Ownership And Post-Dispatch Cancellation

Each binding-specific command protocol must provide enough correlation to prove
that a terminal response belongs to the active request. A facade serializes
commands unless its binding contract explicitly defines safe parallel request
ownership.

Before a request is dispatched, caller cancellation may stop the operation
without changing worker-session health. After a request may have crossed the
pipe boundary, cancellation does not remove facade ownership of that request and
does not imply rollback.

The facade must continue consuming the request protocol until one of these
boundaries is reached:

- the correlated terminal response is drained;
- the pipe or worker is lost;
- response correlation or protocol validation fails;
- a binding-internal hard containment limit expires.

A successfully drained response keeps a healthy worker session reusable. The
facade reports the caller operation as cancelled, while operation-specific code
retains or compensates any known side effect. A failed drain or hard technical
failure terminates and invalidates the worker session so a late response cannot
be mistaken for a later command.

The hard containment limit is a binding implementation detail. It protects IPC
and process ownership when the application has stopped waiting; it is not an
application-configurable operation deadline and must not silently replace the
Shared Core overall deadline.

## Close And Cleanup

The facade sends `Close` when it owns an initialized worker session and the
session should be released.

The worker must release project/runtime/API resources before process exit.

The facade must dispose pipe, reader/writer, and process resources
deterministically. The close deadline covers acquisition of the binding's
serialized command boundary, delivery and completion of `Close`, and worker
process exit; it must not begin only after an unbounded wait for an earlier
command.

If close cannot be delivered because the pipe is already broken or the worker
already exited, the facade reports or logs the cleanup state according to the
owning binding contract. Close and owner cleanup are bounded independently from
normal caller cancellation. After that graceful-close deadline, the facade may
terminate only the worker process that the binding itself started and owns. It
must never terminate an external TIA Portal process, PLCSIM Advanced application,
or user-owned simulated PLC resource as an IPC-cleanup fallback.

## PLCSIM Advanced Event-Stream Note

PLCSIM Advanced event-stream intake is performance-sensitive. The same named
pipe and envelope contract applies, but event-stream payloads must be batched
inside the worker before crossing the pipe boundary.

If the Slice 1 line-based envelope is not sufficient for PLCSIM event-stream
throughput or backpressure, the PLCSIM binding must introduce an explicit
versioned extension or a dedicated data-channel rule in its own contract.
