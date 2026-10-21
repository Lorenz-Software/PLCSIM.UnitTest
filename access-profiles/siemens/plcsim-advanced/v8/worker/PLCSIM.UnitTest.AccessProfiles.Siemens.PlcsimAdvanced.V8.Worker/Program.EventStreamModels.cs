using System;
using System.Collections.Generic;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker
{
    internal static partial class Program
    {
        private sealed class EventStreamSnapshotReadResult
        {
            private EventStreamSnapshotReadResult(
                string status,
                EventBufferPollingSnapshotClassification? classification,
                IReadOnlyList<EventBufferCapturedEntry> capturedEntries,
                bool hasKnownRemainingEvents,
#if EVENTSTREAM_PERFORMANCE_MEASUREMENT
                bool payloadReadDeferred,
#endif
                string code,
                string message)
            {
                Status = status;
                Classification = classification;
                CapturedEntries = capturedEntries;
                HasKnownRemainingEvents = hasKnownRemainingEvents;
#if EVENTSTREAM_PERFORMANCE_MEASUREMENT
                PayloadReadDeferred = payloadReadDeferred;
#endif
                Code = code;
                Message = message;
            }

            public string Status { get; }

            public EventBufferPollingSnapshotClassification? Classification { get; }

            public IReadOnlyList<EventBufferCapturedEntry> CapturedEntries { get; }

            public bool HasKnownRemainingEvents { get; }

#if EVENTSTREAM_PERFORMANCE_MEASUREMENT
            public bool PayloadReadDeferred { get; }
#endif

            public string Code { get; }

            public string Message { get; }

            public bool Succeeded => string.Equals(Status, "Succeeded", StringComparison.OrdinalIgnoreCase);

            public static EventStreamSnapshotReadResult Success(
                EventBufferPollingSnapshotClassification classification,
                IReadOnlyList<EventBufferCapturedEntry> capturedEntries,
                bool hasKnownRemainingEvents)
            {
                return new EventStreamSnapshotReadResult(
                    "Succeeded",
                    classification,
                    capturedEntries,
                    hasKnownRemainingEvents,
#if EVENTSTREAM_PERFORMANCE_MEASUREMENT
                    false,
#endif
                    string.Empty,
                    string.Empty);
            }

#if EVENTSTREAM_PERFORMANCE_MEASUREMENT
            public static EventStreamSnapshotReadResult Deferred(bool hasKnownRemainingEvents)
            {
                return new EventStreamSnapshotReadResult(
                    "Succeeded",
                    null,
                    Array.Empty<EventBufferCapturedEntry>(),
                    hasKnownRemainingEvents,
                    true,
                    string.Empty,
                    string.Empty);
            }
#endif

            public static EventStreamSnapshotReadResult Failed(string code, string message)
            {
                return Failed("Failed", code, message);
            }

            public static EventStreamSnapshotReadResult Failed(string status, string code, string message)
            {
                return new EventStreamSnapshotReadResult(
                    status,
                    null,
                    Array.Empty<EventBufferCapturedEntry>(),
                    false,
#if EVENTSTREAM_PERFORMANCE_MEASUREMENT
                    false,
#endif
                    code,
                    message);
            }
        }

        private sealed class EventBufferEntriesReadResult
        {
            private EventBufferEntriesReadResult(
                bool succeeded,
                IReadOnlyList<EventBufferCapturedEntry> entries,
                string code,
                string message)
            {
                Succeeded = succeeded;
                Entries = entries;
                Code = code;
                Message = message;
            }

            public bool Succeeded { get; }

            public IReadOnlyList<EventBufferCapturedEntry> Entries { get; }

            public string Code { get; }

            public string Message { get; }

            public static EventBufferEntriesReadResult Success(IReadOnlyList<EventBufferCapturedEntry> entries)
            {
                return new EventBufferEntriesReadResult(true, entries, string.Empty, string.Empty);
            }

            public static EventBufferEntriesReadResult Failed(string code, string message)
            {
                return new EventBufferEntriesReadResult(
                    false,
                    Array.Empty<EventBufferCapturedEntry>(),
                    code,
                    message);
            }
        }

        private sealed class EventBufferMetadataReadResult
        {
            private EventBufferMetadataReadResult(
                bool succeeded,
                PlcsimEventBufferFifoControlSnapshot? fifoControl,
                PlcsimEventBufferStatusSnapshot? status,
                string code,
                string message)
            {
                Succeeded = succeeded;
                FifoControl = fifoControl;
                Status = status;
                Code = code;
                Message = message;
            }

            public bool Succeeded { get; }

            public PlcsimEventBufferFifoControlSnapshot? FifoControl { get; }

            public PlcsimEventBufferStatusSnapshot? Status { get; }

            public string Code { get; }

            public string Message { get; }

            public static EventBufferMetadataReadResult Success(
                PlcsimEventBufferFifoControlSnapshot fifoControl,
                PlcsimEventBufferStatusSnapshot status)
            {
                return new EventBufferMetadataReadResult(
                    true,
                    fifoControl,
                    status,
                    string.Empty,
                    string.Empty);
            }

            public static EventBufferMetadataReadResult Failed(string code, string message)
            {
                return new EventBufferMetadataReadResult(false, null, null, code, message);
            }
        }

        private sealed class EventBufferEntryReadResult
        {
            private EventBufferEntryReadResult(
                bool succeeded,
                EventBufferCapturedEntry? entry,
                string code,
                string message)
            {
                Succeeded = succeeded;
                Entry = entry;
                Code = code;
                Message = message;
            }

            public bool Succeeded { get; }

            public EventBufferCapturedEntry? Entry { get; }

            public string Code { get; }

            public string Message { get; }

            public static EventBufferEntryReadResult Success(EventBufferCapturedEntry entry)
            {
                return new EventBufferEntryReadResult(true, entry, string.Empty, string.Empty);
            }

            public static EventBufferEntryReadResult Failed(string code, string message)
            {
                return new EventBufferEntryReadResult(false, null, code, message);
            }
        }

        private sealed class EventBufferFifoReadResult
        {
            private EventBufferFifoReadResult(bool succeeded, PlcsimEventBufferFifoControlSnapshot? fifoControl, string code, string message)
            {
                Succeeded = succeeded;
                FifoControl = fifoControl;
                Code = code;
                Message = message;
            }

            public bool Succeeded { get; }

            public PlcsimEventBufferFifoControlSnapshot? FifoControl { get; }

            public string Code { get; }

            public string Message { get; }

            public static EventBufferFifoReadResult Success(PlcsimEventBufferFifoControlSnapshot fifoControl)
            {
                return new EventBufferFifoReadResult(true, fifoControl, string.Empty, string.Empty);
            }

            public static EventBufferFifoReadResult Failed(string code, string message)
            {
                return new EventBufferFifoReadResult(false, null, code, message);
            }
        }

        private sealed class EventBufferStatusReadResult
        {
            private EventBufferStatusReadResult(bool succeeded, PlcsimEventBufferStatusSnapshot? status, string code, string message)
            {
                Succeeded = succeeded;
                Status = status;
                Code = code;
                Message = message;
            }

            public bool Succeeded { get; }

            public PlcsimEventBufferStatusSnapshot? Status { get; }

            public string Code { get; }

            public string Message { get; }

            public static EventBufferStatusReadResult Success(PlcsimEventBufferStatusSnapshot status)
            {
                return new EventBufferStatusReadResult(true, status, string.Empty, string.Empty);
            }

            public static EventBufferStatusReadResult Failed(string code, string message)
            {
                return new EventBufferStatusReadResult(false, null, code, message);
            }
        }

        private sealed class ObservationWakeupResult
        {
            private ObservationWakeupResult(
                bool stopRequested,
                bool observe,
                string code,
                string message)
            {
                StopRequested = stopRequested;
                Observe = observe;
                Code = code;
                Message = message;
            }

            public bool StopRequested { get; }

            public bool Observe { get; }

            public bool Faulted => !string.IsNullOrWhiteSpace(Code);

            public string Code { get; }

            public string Message { get; }

            public static ObservationWakeupResult Stop()
            {
                return new ObservationWakeupResult(
                    stopRequested: true,
                    observe: false,
                    string.Empty,
                    string.Empty);
            }

            public static ObservationWakeupResult ObserveNow()
            {
                return new ObservationWakeupResult(
                    stopRequested: false,
                    observe: true,
                    string.Empty,
                    string.Empty);
            }

            public static ObservationWakeupResult NoEvent()
            {
                return new ObservationWakeupResult(
                    stopRequested: false,
                    observe: false,
                    string.Empty,
                    string.Empty);
            }

            public static ObservationWakeupResult FromFault(string code, string message)
            {
                return new ObservationWakeupResult(
                    stopRequested: false,
                    observe: false,
                    code,
                    message);
            }
        }
    }
}
