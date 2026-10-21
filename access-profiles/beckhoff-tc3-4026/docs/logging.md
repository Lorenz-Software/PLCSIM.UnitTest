# Operational Logging

The profile uses the application's logger factory and log directory. WPF and CLI
write `access-profile-beckhoff-tc3-4026-YYYYMMDD.log`; the default directory is
`%LOCALAPPDATA%\PLCSIM.UnitTest\logs`. Daily rotation, size limits and retention
remain application-owned. There is no separate Beckhoff worker log.

## Levels

| Profile level | Profile-owned content | ADS library minimum level |
| --- | --- | --- |
| Information | Preparation, operation results, relevant state changes, cleanup | Warning |
| Debug | Commands, engineering steps, accepted nonempty batches, diagnostic context | Information |
| Warning | Warnings and errors | Warning |
| Error / Critical | Corresponding severity and higher | Corresponding severity |
| None | Disabled | Disabled |

ADS Debug/Trace output is excluded, including when a host supplies profile Trace.
In that case the ADS minimum remains Information. ADS records are prefixed `ADS:`;
the library's category and event ID are preserved in the logging call. The
profile does not own or dispose the application's logger factory. Raw-event
logging never enables ADS packet or memory dumps.

Debug logs distinguish command writes accepted by ADS from observed PLC execution.
Unchanged empty polls are not logged. Existing profile diagnostics retain their
deduplication and are not expanded into per-poll UI messages.

## Raw PLC Events

Enable the existing profile Debug level and Raw Event Logging option. In the CLI,
use `--debug --raw-event-logging`. Logging options follow the existing application
lifetime; this profile does not add live settings reload or a new parameter.

Each accepted nonempty batch has a compact Debug summary with its target,
communication session, event count, sequence range and observed buffer counters.
With raw logging enabled, `RawPlcEvent` records additionally contain a JSON payload
for each decoded event: sequence, PLC timestamp, owner scope, event type, severity,
name, resource, lifecycle state, result and message. JSON escaping keeps embedded
newlines and quotes inside the payload rather than creating apparent log entries.
The records are embedded in the ordinary operational log, not a standalone NDJSON
export.

`pcObservedAt` is the PC time when the accepted batch is logged, after its watermark
write and before host handover. `plcEventTimestamp` is the normalized event time
supplied by the PLC. They are different clock domains and must not be subtracted
to infer transport latency. The profile records communication-session identity,
not a RunEpoch assignment. Startup/reset events are included regardless of later
classification in the Core.

Rejected or withheld captures are not logged as accepted raw events. Watermark
write failures retain technical diagnostics. No additional PLC read is performed
for logging, and capture/write/handover order is unchanged. Disabled raw logging
does not serialize event payloads. Enabled logging adds synchronous formatting and
sink work; no zero-overhead or lossless-log guarantee is made.

## Evidence and Privacy

Log sinks are best-effort: a logging or formatting failure must not prevent an
accepted batch from reaching the host. A missing log record is not proof of a
missing PLC event. Activity Trace and the existing analysis exports remain the
analysis path; file logs provide additional technical context.

Raw event names/messages and technical logs can contain user PLC data and local
project paths. Review logs before sharing them, enable raw logging only when
needed, and retain only the relevant files. Logging does not read additional PLC
memory, credentials or license contents.

Deterministic tests cover filtering, raw payload preservation, disabled paths,
unchanged empty polls, repeated read failures, rejected captures and handover with
failing log sinks. They do not establish real-runtime performance.

## Focused Cost Check

A local Release-mode check used the production logging sources with the actual
CLI profile file logger (shared file sink, normal OS file caching), without ADS
or a PLC connection. Two process runs each measured seven rounds per case.
The table shows the range of the two run medians of per-round mean batch times;
these are not latency percentiles or maximum write times. Batch construction,
PLC reads, host handover and disk durability are not included.

| Events per batch | Debug summary only | Debug with raw events |
| ---: | ---: | ---: |
| 1 | 0.035-0.036 ms | 0.058-0.062 ms |
| 32 | 0.023-0.028 ms | 0.385-0.512 ms |
| 255 | 0.019-0.025 ms | 2.955-4.258 ms |

These payloads used a 64-character message. For 32 events with a synthetic
2,048-character message, raw logging took 0.485-0.648 ms per batch. That larger
message is a formatting stress input, not a claim about the PLC contract's string
limits. The disabled Debug path measured below 0.001 ms per call with no managed
allocation on the measured thread and no output. At 32 events, the Debug summary
allocated about 2.6 KiB per batch; raw logging allocated about 63 KiB with short
messages and 187 KiB with the stress payload. Raw output was about 16 KB or 79 KB
per batch respectively. Every expected file record was counted after disposal.

Raw logging therefore has a measurable synchronous cost, particularly for large
batches. Keep it off for ordinary execution and throughput measurements. These
figures characterize logging on one development machine, not ADS throughput,
PLC-cycle limits, or a guarantee under disk contention.

## Vendor Output Volume

A historical log inspection found 58 Information-or-higher records (about 7 KB)
during a 63-second healthy prepared-access window, including connection setup.
A 141-second managed-workflow window contained 402 such records (about 59 KB).
In contrast, an unavailable-port reconnect loop produced roughly 84 records/s
(about 10 KB/s), peaking at 88 records/s. ADS Information can therefore still be
verbose during repeated connection changes. Profile Information suppresses those
vendor Information records; profile Debug intentionally retains them.

These are level-based counts from an earlier mixed profile/vendor log, not a
fresh live test of the filter or exact ADS-only counts. The unavailable-port case
predated the external-mode missing-port preparation check. The agreed level
mapping is retained; no message-text suppression hides vendor failures.
