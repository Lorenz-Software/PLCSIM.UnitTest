using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker
{
    internal static partial class Program
    {
        private static bool TryHandleValidationCommand(
            StreamWriter writer,
            WorkerProtocolMessage message,
            string validationScenario,
            ILogger logger,
            bool rawEventLoggingEnabled)
        {
            if (string.Equals(message.Type, "DiscoverRuntimeTargets", StringComparison.OrdinalIgnoreCase))
            {
                return TryHandleValidationDiscoveryBehavior(writer, validationScenario);
            }

            if (string.Equals(message.Type, "PrepareRuntime", StringComparison.OrdinalIgnoreCase))
            {
                return TryHandleValidationPrepareBehavior(writer, validationScenario, message.Values);
            }

            if (string.Equals(message.Type, "ObserveRuntime", StringComparison.OrdinalIgnoreCase))
            {
                return TryHandleValidationObservationBehavior(writer, validationScenario);
            }

            if (string.Equals(message.Type, "RevalidateRuntime", StringComparison.OrdinalIgnoreCase))
            {
                return TryHandleValidationRuntimeRevalidationBehavior(writer, validationScenario, message.Values);
            }

            if (string.Equals(message.Type, "StartRuntime", StringComparison.OrdinalIgnoreCase))
            {
                return TryHandleValidationControlBehavior(writer, validationScenario, "StartRuntime", "Run");
            }

            if (string.Equals(message.Type, "StopRuntime", StringComparison.OrdinalIgnoreCase))
            {
                return TryHandleValidationControlBehavior(writer, validationScenario, "StopRuntime", "Stop");
            }

            if (string.Equals(message.Type, "ReleaseRuntime", StringComparison.OrdinalIgnoreCase))
            {
                return TryHandleValidationReleaseBehavior(writer, validationScenario, message.Values);
            }

            if (string.Equals(message.Type, "PrepareCommunication", StringComparison.OrdinalIgnoreCase))
            {
                return TryHandleValidationCommunicationPrepareBehavior(writer, validationScenario, message.Values);
            }

            if (string.Equals(message.Type, "CloseCommunication", StringComparison.OrdinalIgnoreCase))
            {
                return TryHandleValidationCommunicationCloseBehavior(writer, validationScenario, message.Values);
            }

            if (string.Equals(message.Type, "ObserveRunControl", StringComparison.OrdinalIgnoreCase))
            {
                return TryHandleValidationRunControlObservationBehavior(writer, validationScenario);
            }

            if (string.Equals(message.Type, "RequestRunControlCommand", StringComparison.OrdinalIgnoreCase))
            {
                return TryHandleValidationRunControlCommandBehavior(writer, validationScenario, message.Values);
            }

            if (string.Equals(message.Type, "RequestEventBufferReset", StringComparison.OrdinalIgnoreCase))
            {
                return TryHandleValidationEventBufferResetBehavior(writer, validationScenario);
            }

            if (string.Equals(message.Type, "StartEventStreamMonitoring", StringComparison.OrdinalIgnoreCase))
            {
                return TryHandleValidationEventStreamMonitoringBehavior(
                    writer,
                    validationScenario,
                    message.Values,
                    "EventStreamMonitoringStarted",
                    true,
                    logger,
                    rawEventLoggingEnabled);
            }

            if (string.Equals(message.Type, "StopEventStreamMonitoring", StringComparison.OrdinalIgnoreCase))
            {
                return TryHandleValidationEventStreamMonitoringBehavior(
                    writer,
                    validationScenario,
                    message.Values,
                    "EventStreamMonitoringStopped",
                    false,
                    logger,
                    rawEventLoggingEnabled);
            }

            return false;
        }

        private static bool TryHandleValidationDiscoveryBehavior(
            StreamWriter writer,
            string validationBehavior)
        {
            if (string.IsNullOrWhiteSpace(validationBehavior))
            {
                return false;
            }

            if (string.Equals(validationBehavior, "malformed-discovery", StringComparison.OrdinalIgnoreCase))
            {
                WriteLine(writer, "MALFORMED");
                return true;
            }

            if (string.Equals(validationBehavior, "unexpected-discovery", StringComparison.OrdinalIgnoreCase))
            {
                WriteLine(writer, CreateMessage("Unexpected"));
                return true;
            }

            if (string.Equals(validationBehavior, "mismatched-discovery", StringComparison.OrdinalIgnoreCase))
            {
                WriteLine(
                    writer,
                    CreateMessage("EndRuntimeTargets")
                    + "|status=" + Escape("Succeeded")
                    + "|requestId=" + Escape("different-request"));
                return true;
            }

            if (string.Equals(validationBehavior, "discovery-error", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(writer, "PlcsimAdvanced.V8.Worker.ValidationDiscoveryError", "Validation discovery error.");
                return true;
            }

            if (string.Equals(validationBehavior, "disconnect-during-discovery", StringComparison.OrdinalIgnoreCase))
            {
                Environment.Exit(4);
                return true;
            }

            if (string.Equals(validationBehavior, "slow-discovery", StringComparison.OrdinalIgnoreCase))
            {
                System.Threading.Thread.Sleep(TimeSpan.FromMinutes(5));
                return true;
            }

            if (string.Equals(validationBehavior, "delayed-discovery", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(250));
                WriteRuntimeTargetMessage(
                    writer,
                    "RuntimeTarget",
                    "Succeeded",
                    new RuntimeTarget("0", "PlcUnderTest", "Stop", false));
                WriteLine(writer, CreateMessage("EndRuntimeTargets") + "|status=" + Escape("Succeeded"));
                return true;
            }

            return false;
        }

        private static bool TryHandleValidationReleaseBehavior(
            StreamWriter writer,
            string validationBehavior,
            IReadOnlyDictionary<string, string> values)
        {
            if (string.IsNullOrWhiteSpace(validationBehavior))
            {
                return false;
            }

            if (string.Equals(validationBehavior, "release-owned", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "delayed-prepare-owned", StringComparison.OrdinalIgnoreCase))
            {
                WriteLine(
                    writer,
                    CreateMessage("RuntimeReleased")
                    + "|status=" + Escape("Succeeded")
                    + "|released=" + Escape("true")
                    + "|cleanupPreference=" + Escape(values.TryGetValue("cleanupPreference", out var cleanup) ? cleanup : "Release")
                    + "|storageCleaned=" + Escape("true"));
                return true;
            }

            if (string.Equals(validationBehavior, "release-attached", StringComparison.OrdinalIgnoreCase))
            {
                WriteLine(
                    writer,
                    CreateMessage("RuntimeReleased")
                    + "|status=" + Escape("Succeeded")
                    + "|released=" + Escape("true")
                    + "|cleanupPreference=" + Escape(values.TryGetValue("cleanupPreference", out var cleanup) ? cleanup : "Release")
                    + "|storageCleaned=" + Escape("false"));
                return true;
            }

            if (string.Equals(validationBehavior, "release-error", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(writer, "Failed", "PlcsimAdvanced.V8.Worker.ValidationReleaseError", "Validation runtime release error.");
                return true;
            }

            return false;
        }

        private static bool TryHandleValidationCommunicationPrepareBehavior(
            StreamWriter writer,
            string validationBehavior,
            IReadOnlyDictionary<string, string> values)
        {
            if (string.IsNullOrWhiteSpace(validationBehavior))
            {
                return false;
            }

            if (string.Equals(validationBehavior, "communication-prepare", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "delayed-communication-operations", StringComparison.OrdinalIgnoreCase)
                || validationBehavior.StartsWith("eventstream-snapshot-", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "eventstream-monitoring-state-without-batch", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "eventstream-monitoring-faulted-event", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(validationBehavior, "delayed-communication-operations", StringComparison.OrdinalIgnoreCase))
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(250));
                }

                WriteLine(
                    writer,
                    CreateMessage("CommunicationPrepared")
                    + "|status=" + Escape("Succeeded")
                    + "|communicationSessionId=" + Escape(values.TryGetValue("runtimeSessionId", out var sessionId)
                        ? sessionId + "-communication"
                        : Guid.NewGuid().ToString("N"))
                    + "|runControlContractVersion=" + Escape(values.TryGetValue("expectedRunControlContractVersion", out var contractVersion)
                        ? contractVersion
                        : string.Empty)
                    + "|runControlFrameworkVersion=" + Escape("validation")
                    + "|eventStreamContractVersion=" + Escape(values.TryGetValue("expectedEventStreamContractVersion", out var eventStreamContractVersion)
                        ? eventStreamContractVersion
                        : string.Empty)
                    + "|eventStreamFrameworkVersion=" + Escape("validation"));
                return true;
            }

            if (string.Equals(validationBehavior, "communication-runtime-stopped", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(
                    writer,
                    "Unavailable",
                    "PlcsimAdvanced.V8.Worker.RuntimeNotRunning",
                    "PLCSIM Advanced runtime must be running before UnitTest communication can be prepared.");
                return true;
            }

            if (string.Equals(validationBehavior, "communication-plccycle-unavailable", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(
                    writer,
                    "Unavailable",
                    "PlcsimAdvanced.V8.Worker.PlcCycleObservationUnavailable",
                    "PLCSIM Advanced V8 sync-point observation could not be established for the configured communication strategy.");
                return true;
            }

            return false;
        }

        private static bool TryHandleValidationEventStreamMonitoringBehavior(
            StreamWriter writer,
            string validationBehavior,
            IReadOnlyDictionary<string, string> values,
            string messageType,
            bool isMonitoring,
            ILogger logger,
            bool rawEventLoggingEnabled)
        {
            if (string.IsNullOrWhiteSpace(validationBehavior))
            {
                return false;
            }

            if (string.Equals(validationBehavior, "communication-prepare", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "delayed-communication-operations", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "eventstream-monitoring", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "eventstream-monitoring-state-without-batch", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "eventstream-stop-runtime-stopped", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(validationBehavior, "delayed-communication-operations", StringComparison.OrdinalIgnoreCase))
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(250));
                }

                var communicationSessionId = values.TryGetValue("communicationSessionId", out var sessionId)
                    ? sessionId
                    : string.Empty;
                if (isMonitoring
                    && string.Equals(validationBehavior, "eventstream-monitoring-state-without-batch", StringComparison.OrdinalIgnoreCase)
                    && TryCreateValidationEventStreamSnapshot(
                        "eventstream-snapshot-lost-events",
                        out var stateSnapshot,
                        out _))
                {
                    WriteEventStreamMonitoringStateChanged(
                        writer,
                        EventStreamMonitoringResult.CreateBufferObservationResult(
                            communicationSessionId,
                            isMonitoring: true,
                            stateSnapshot,
                            "Idle",
                            capturedBatch: null));
                }

                WriteEventStreamMonitoringResult(
                    writer,
                    messageType,
                    EventStreamMonitoringResult.CreateMonitoringResult(
                        communicationSessionId,
                        isMonitoring),
                    logger,
                    rawEventLoggingEnabled);
                return true;
            }

            if (string.Equals(validationBehavior, "eventstream-monitoring-faulted-event", StringComparison.OrdinalIgnoreCase))
            {
                var communicationSessionId = values.TryGetValue("communicationSessionId", out var sessionId)
                    ? sessionId
                    : string.Empty;
                logger.LogError(
                    "EventStream monitoring faulted. CommunicationSessionId={CommunicationSessionId} Code={Code}",
                    communicationSessionId,
                    "PlcsimAdvanced.V8.Worker.ValidationEventStreamMonitoringFaulted");
                WriteEventStreamMonitoringFaulted(
                    writer,
                    EventStreamMonitoringFault.Failed(
                        communicationSessionId,
                        "PlcsimAdvanced.V8.Worker.ValidationEventStreamMonitoringFaulted",
                        "Validation EventStream monitoring fault event."));
                WriteEventStreamMonitoringResult(
                    writer,
                    messageType,
                    EventStreamMonitoringResult.CreateMonitoringResult(
                        values.TryGetValue("communicationSessionId", out sessionId) ? sessionId : string.Empty,
                        isMonitoring),
                    logger,
                    rawEventLoggingEnabled);
                return true;
            }

            var releaseFailed = string.Equals(
                validationBehavior,
                "eventstream-snapshot-release-failed",
                StringComparison.OrdinalIgnoreCase);
            var releasePending = string.Equals(
                validationBehavior,
                "eventstream-snapshot-release-pending",
                StringComparison.OrdinalIgnoreCase);
            var snapshotBehavior = releaseFailed || releasePending
                ? "eventstream-snapshot-partial"
                : validationBehavior;
            if (TryCreateValidationEventStreamSnapshot(
                snapshotBehavior,
                out var snapshot,
                out var capturedEntries))
            {
                var communicationSessionId = values.TryGetValue("communicationSessionId", out var sessionId)
                    ? sessionId
                    : string.Empty;
                var candidateBatch = EventStreamCapturedBatch.FromClassification(
                    communicationSessionId,
                    snapshot,
                    capturedEntries);
                var batchReleaseState = !releaseFailed && !releasePending
                    && candidateBatch is not null && candidateBatch.CapturedEntries.Count > 0
                        ? "Completed"
                        : "Idle";
                var capturedBatch = releaseFailed || releasePending
                    ? null
                    : candidateBatch;
                WriteEventStreamMonitoringResult(
                    writer,
                    messageType,
                    EventStreamMonitoringResult.CreateBufferObservationResult(
                        communicationSessionId,
                        isMonitoring,
                        snapshot,
                        batchReleaseState,
                        capturedBatch),
                    logger,
                    rawEventLoggingEnabled);
                return true;
            }

            if (string.Equals(validationBehavior, "eventstream-monitoring-error", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(writer, "Failed", "PlcsimAdvanced.V8.Worker.ValidationEventStreamMonitoringError", "Validation EventStream monitoring error.");
                return true;
            }

            return false;
        }

        private static bool TryCreateValidationEventStreamSnapshot(
            string validationBehavior,
            out EventBufferPollingSnapshotClassification snapshot,
            out IReadOnlyList<EventBufferCapturedEntry> capturedEntries)
        {
            snapshot = default!;
            capturedEntries = Array.Empty<EventBufferCapturedEntry>();
            if (!validationBehavior.StartsWith("eventstream-snapshot-", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var classifier = new EventBufferPollingSnapshotClassifier(UnitTestContractVersions.EventStreamModel, "validation");
            var preStatus = new PlcsimEventBufferStatusSnapshot(SequenceId: 4, DeletedSequenceId: 0, LostEventCount: 0);
            var postStatus = preStatus;
            PlcsimEventBufferFifoControlSnapshot preFifo;
            PlcsimEventBufferFifoControlSnapshot postFifo;
            var sequenceIdsByBufferIndex = new Dictionary<ushort, uint>();

            if (string.Equals(validationBehavior, "eventstream-snapshot-empty", StringComparison.OrdinalIgnoreCase))
            {
                preFifo = new PlcsimEventBufferFifoControlSnapshot(1, 1, 4, BufferEmpty: true, BufferFull: false, ConfigError: false, IndexError: false);
                postFifo = preFifo;
            }
            else if (string.Equals(validationBehavior, "eventstream-snapshot-partial", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "eventstream-snapshot-escaped-payload", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "eventstream-snapshot-unknown-values", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "eventstream-snapshot-partial-entry-read", StringComparison.OrdinalIgnoreCase))
            {
                preFifo = new PlcsimEventBufferFifoControlSnapshot(1, 3, 4, BufferEmpty: false, BufferFull: false, ConfigError: false, IndexError: false);
                postFifo = preFifo;
                sequenceIdsByBufferIndex[1] = 1;
                if (!string.Equals(validationBehavior, "eventstream-snapshot-partial-entry-read", StringComparison.OrdinalIgnoreCase))
                {
                    sequenceIdsByBufferIndex[2] = 2;
                }
            }
            else if (string.Equals(validationBehavior, "eventstream-snapshot-wraparound", StringComparison.OrdinalIgnoreCase))
            {
                preFifo = new PlcsimEventBufferFifoControlSnapshot(3, 2, 4, BufferEmpty: false, BufferFull: false, ConfigError: false, IndexError: false);
                postFifo = preFifo;
                sequenceIdsByBufferIndex[3] = 1;
                sequenceIdsByBufferIndex[4] = 2;
                sequenceIdsByBufferIndex[1] = 3;
            }
            else if (string.Equals(validationBehavior, "eventstream-snapshot-full", StringComparison.OrdinalIgnoreCase))
            {
                preFifo = new PlcsimEventBufferFifoControlSnapshot(2, 1, 4, BufferEmpty: false, BufferFull: true, ConfigError: false, IndexError: false);
                postFifo = preFifo;
                sequenceIdsByBufferIndex[2] = 1;
                sequenceIdsByBufferIndex[3] = 2;
                sequenceIdsByBufferIndex[4] = 3;
                sequenceIdsByBufferIndex[1] = 0;
            }
            else if (string.Equals(validationBehavior, "eventstream-snapshot-index-in-advanced", StringComparison.OrdinalIgnoreCase))
            {
                preFifo = new PlcsimEventBufferFifoControlSnapshot(1, 3, 4, BufferEmpty: false, BufferFull: false, ConfigError: false, IndexError: false);
                postFifo = new PlcsimEventBufferFifoControlSnapshot(1, 4, 4, BufferEmpty: false, BufferFull: false, ConfigError: false, IndexError: false);
                sequenceIdsByBufferIndex[1] = 1;
                sequenceIdsByBufferIndex[2] = 2;
            }
            else if (string.Equals(validationBehavior, "eventstream-snapshot-index-out-changed", StringComparison.OrdinalIgnoreCase))
            {
                preFifo = new PlcsimEventBufferFifoControlSnapshot(1, 3, 4, BufferEmpty: false, BufferFull: false, ConfigError: false, IndexError: false);
                postFifo = new PlcsimEventBufferFifoControlSnapshot(2, 3, 4, BufferEmpty: false, BufferFull: false, ConfigError: false, IndexError: false);
                sequenceIdsByBufferIndex[1] = 1;
                sequenceIdsByBufferIndex[2] = 2;
            }
            else if (string.Equals(validationBehavior, "eventstream-snapshot-sequence-zero", StringComparison.OrdinalIgnoreCase))
            {
                preFifo = new PlcsimEventBufferFifoControlSnapshot(1, 3, 4, BufferEmpty: false, BufferFull: false, ConfigError: false, IndexError: false);
                postFifo = preFifo;
                sequenceIdsByBufferIndex[1] = 1;
                sequenceIdsByBufferIndex[2] = 0;
            }
            else if (string.Equals(validationBehavior, "eventstream-snapshot-sequence-regression", StringComparison.OrdinalIgnoreCase))
            {
                preFifo = new PlcsimEventBufferFifoControlSnapshot(1, 3, 4, BufferEmpty: false, BufferFull: false, ConfigError: false, IndexError: false);
                postFifo = preFifo;
                sequenceIdsByBufferIndex[1] = 2;
                sequenceIdsByBufferIndex[2] = 1;
            }
            else if (string.Equals(validationBehavior, "eventstream-snapshot-lost-events", StringComparison.OrdinalIgnoreCase))
            {
                preStatus = new PlcsimEventBufferStatusSnapshot(SequenceId: 4, DeletedSequenceId: 0, LostEventCount: 2);
                postStatus = preStatus;
                preFifo = new PlcsimEventBufferFifoControlSnapshot(1, 3, 4, BufferEmpty: false, BufferFull: false, ConfigError: false, IndexError: false);
                postFifo = preFifo;
                sequenceIdsByBufferIndex[1] = 1;
                sequenceIdsByBufferIndex[2] = 2;
            }
            else if (string.Equals(validationBehavior, "eventstream-snapshot-config-error", StringComparison.OrdinalIgnoreCase))
            {
                preFifo = new PlcsimEventBufferFifoControlSnapshot(1, 3, 4, BufferEmpty: false, BufferFull: false, ConfigError: true, IndexError: false);
                postFifo = preFifo;
            }
            else
            {
                return false;
            }

            snapshot = classifier.Classify(preFifo, preStatus, sequenceIdsByBufferIndex, postFifo, postStatus, DateTimeOffset.Now);
            capturedEntries = CreateValidationCapturedEntries(
                validationBehavior,
                snapshot.CapturedSequences);
            return true;
        }

        private static IReadOnlyList<EventBufferCapturedEntry> CreateValidationCapturedEntries(
            string validationBehavior,
            IReadOnlyList<EventBufferCapturedSequence> capturedSequences)
        {
            var unknownValues = string.Equals(
                validationBehavior,
                "eventstream-snapshot-unknown-values",
                StringComparison.OrdinalIgnoreCase);
            var escapedPayload = string.Equals(
                validationBehavior,
                "eventstream-snapshot-escaped-payload",
                StringComparison.OrdinalIgnoreCase);
            var entries = new List<EventBufferCapturedEntry>(capturedSequences.Count);
            for (var index = 0; index < capturedSequences.Count; index++)
            {
                var captured = capturedSequences[index];
                var isFirst = index == 0;
                var isLast = index == capturedSequences.Count - 1;
                entries.Add(new EventBufferCapturedEntry(
                    captured.BufferIndex,
                    captured.SequenceId,
                    1700000000000000000L + (captured.SequenceId * 1000000L),
                    ownerScope: unknownValues ? (byte)255 : (byte)10,
                    eventType: unknownValues ? (byte)255 : (byte)30,
                    severity: unknownValues ? (byte)255 : (byte)2,
                    name: "ValidationRunControl",
                    resourceName: escapedPayload ? "Resource | A=B % complete" : "ValidationResource",
                    state: unknownValues ? (short)1234 : isFirst ? (short)11 : isLast ? (short)89 : (short)50,
                    result: unknownValues ? (short)1234 : isLast ? (short)10 : (short)0,
                    message: escapedPayload
                        ? "Validation | payload=A=B % complete."
                        : isLast ? "Validation run completed." : "Validation run started."));
            }

            return entries.AsReadOnly();
        }

        private static bool TryHandleValidationCommunicationCloseBehavior(
            StreamWriter writer,
            string validationBehavior,
            IReadOnlyDictionary<string, string> values)
        {
            if (string.IsNullOrWhiteSpace(validationBehavior))
            {
                return false;
            }

            if (string.Equals(validationBehavior, "communication-close", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "communication-prepare", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "delayed-communication-operations", StringComparison.OrdinalIgnoreCase)
                || validationBehavior.StartsWith("eventstream-snapshot-", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "eventstream-monitoring-state-without-batch", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "eventstream-monitoring-faulted-event", StringComparison.OrdinalIgnoreCase))
            {
                WriteLine(
                    writer,
                    CreateMessage("CommunicationClosed")
                    + "|status=" + Escape("Succeeded")
                    + "|communicationSessionId=" + Escape(values.TryGetValue("communicationSessionId", out var sessionId)
                        ? sessionId
                        : string.Empty));
                return true;
            }

            if (string.Equals(validationBehavior, "communication-close-error", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(
                    writer,
                    "Failed",
                    "PlcsimAdvanced.V8.Worker.ValidationCommunicationCloseFailed",
                    "Validation UnitTest communication close failed.");
                return true;
            }

            return false;
        }

        private static bool TryHandleValidationRunControlObservationBehavior(
            StreamWriter writer,
            string validationBehavior)
        {
            if (string.IsNullOrWhiteSpace(validationBehavior))
            {
                return false;
            }

            if (string.Equals(validationBehavior, "runcontrol-observe-ready", StringComparison.OrdinalIgnoreCase))
            {
                WriteValidationRunControlObserved(writer, UnitTestLifecycleState.Idle, UnitTestExecutionStatus.Idle, UnitTestResultState.NotEvaluated, UnitTestExecutionSignals.Ready);
                return true;
            }

            if (string.Equals(validationBehavior, "runcontrol-observe-running", StringComparison.OrdinalIgnoreCase))
            {
                WriteValidationRunControlObserved(writer, UnitTestLifecycleState.Running, UnitTestExecutionStatus.Running, UnitTestResultState.NotEvaluated, UnitTestExecutionSignals.Running);
                return true;
            }

            if (string.Equals(validationBehavior, "runcontrol-observe-completed", StringComparison.OrdinalIgnoreCase))
            {
                WriteValidationRunControlObserved(writer, UnitTestLifecycleState.Completed, UnitTestExecutionStatus.Completed, UnitTestResultState.NotEvaluated, UnitTestExecutionSignals.Completed);
                return true;
            }

            if (string.Equals(validationBehavior, "runcontrol-observe-all-signals", StringComparison.OrdinalIgnoreCase))
            {
                WriteValidationRunControlObserved(
                    writer,
                    UnitTestLifecycleState.Preparing,
                    UnitTestExecutionStatus.Starting,
                    UnitTestResultState.NotEvaluated,
                    UnitTestExecutionSignals.Uninitialized
                    | UnitTestExecutionSignals.ConfigError
                    | UnitTestExecutionSignals.Ready
                    | UnitTestExecutionSignals.Running
                    | UnitTestExecutionSignals.Completed
                    | UnitTestExecutionSignals.Aborted
                    | UnitTestExecutionSignals.Inactive
                    | UnitTestExecutionSignals.Error);
                return true;
            }

            if (string.Equals(validationBehavior, "runcontrol-observe-config-error", StringComparison.OrdinalIgnoreCase))
            {
                WriteValidationRunControlObserved(writer, UnitTestLifecycleState.Error, UnitTestExecutionStatus.Error, UnitTestResultState.Error, UnitTestExecutionSignals.ConfigError);
                return true;
            }

            if (string.Equals(validationBehavior, "runcontrol-observe-error", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(writer, "Failed", "PlcsimAdvanced.V8.Worker.ValidationRunControlObservationError", "Validation RunControl observation error.");
                return true;
            }

            return false;
        }

        private static bool TryHandleValidationRunControlCommandBehavior(
            StreamWriter writer,
            string validationBehavior,
            IReadOnlyDictionary<string, string> values)
        {
            if (string.IsNullOrWhiteSpace(validationBehavior))
            {
                return false;
            }

            if (string.Equals(validationBehavior, "runcontrol-command-start", StringComparison.OrdinalIgnoreCase))
            {
                WriteValidationRunControlCommandRequested(writer, values);
                return true;
            }

            if (string.Equals(validationBehavior, "runcontrol-command-abort", StringComparison.OrdinalIgnoreCase))
            {
                WriteValidationRunControlCommandRequested(writer, values);
                return true;
            }

            if (string.Equals(validationBehavior, "runcontrol-command-reset", StringComparison.OrdinalIgnoreCase))
            {
                WriteValidationRunControlCommandRequested(writer, values);
                return true;
            }

            if (string.Equals(validationBehavior, "runcontrol-command-rejected", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(writer, "Rejected", "PlcsimAdvanced.V8.Worker.ValidationRunControlCommandRejected", "Validation RunControl command rejected.");
                return true;
            }

            if (string.Equals(validationBehavior, "slow-runcontrol-command", StringComparison.OrdinalIgnoreCase))
            {
                System.Threading.Thread.Sleep(TimeSpan.FromMinutes(5));
                return true;
            }

            if (string.Equals(validationBehavior, "delayed-communication-operations", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(250));
                WriteValidationRunControlCommandRequested(writer, values);
                return true;
            }

            return false;
        }

        private static void WriteValidationRunControlObserved(
            StreamWriter writer,
            UnitTestLifecycleState state,
            UnitTestExecutionStatus executionStatus,
            UnitTestResultState result,
            UnitTestExecutionSignals executionSignals)
        {
            WriteLine(
                writer,
                CreateMessage("RunControlObserved")
                + "|status=" + Escape("Succeeded")
                + "|executionStatus=" + Escape(((int)executionStatus).ToString(System.Globalization.CultureInfo.InvariantCulture))
                + "|executionSignals=" + Escape(((ushort)executionSignals).ToString(System.Globalization.CultureInfo.InvariantCulture))
                + "|state=" + Escape(((int)state).ToString(System.Globalization.CultureInfo.InvariantCulture))
                + "|result=" + Escape(((int)result).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        private static void WriteValidationRunControlCommandRequested(
            StreamWriter writer,
            IReadOnlyDictionary<string, string> values)
        {
            var command = values.TryGetValue("command", out var commandValue) ? commandValue : string.Empty;
            WriteLine(
                writer,
                CreateMessage("RunControlCommandRequested")
                + "|status=" + Escape("Succeeded")
                + "|command=" + Escape(command)
                + "|accepted=" + Escape("true"));
        }

        private static bool TryHandleValidationEventBufferResetBehavior(
            StreamWriter writer,
            string validationBehavior)
        {
            if (string.Equals(validationBehavior, "event-buffer-reset-accepted", StringComparison.OrdinalIgnoreCase))
            {
                WriteLine(
                    writer,
                    CreateMessage("EventBufferResetRequested")
                    + "|status=" + Escape("Succeeded")
                    + "|accepted=" + Escape("true"));
                return true;
            }

            if (string.Equals(validationBehavior, "event-buffer-reset-rejected", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(
                    writer,
                    "Rejected",
                    "PlcsimAdvanced.V8.Worker.ValidationEventBufferResetRejected",
                    "Validation event-buffer reset command rejected.");
                return true;
            }

            return false;
        }

        private static bool TryHandleValidationObservationBehavior(
            StreamWriter writer,
            string validationBehavior)
        {
            if (string.IsNullOrWhiteSpace(validationBehavior))
            {
                return false;
            }

            if (string.Equals(validationBehavior, "runtime-observe-stop", StringComparison.OrdinalIgnoreCase))
            {
                WriteRuntimeTargetMessage(writer, "RuntimeObserved", "Succeeded", new RuntimeTarget("0", "PlcUnderTest", "Stop", false));
                return true;
            }

            if (string.Equals(validationBehavior, "delayed-runtime-operations", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(250));
                WriteRuntimeTargetMessage(writer, "RuntimeObserved", "Succeeded", new RuntimeTarget("0", "PlcUnderTest", "Stop", false));
                return true;
            }

            if (string.Equals(validationBehavior, "runcontrol-runtime-stopped", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "eventstream-stop-runtime-stopped", StringComparison.OrdinalIgnoreCase))
            {
                WriteRuntimeTargetMessage(writer, "RuntimeObserved", "Succeeded", new RuntimeTarget("0", "PlcUnderTest", "Stop", false));
                return true;
            }

            if (validationBehavior.StartsWith("runcontrol-", StringComparison.OrdinalIgnoreCase))
            {
                WriteRuntimeTargetMessage(writer, "RuntimeObserved", "Succeeded", new RuntimeTarget("0", "PlcUnderTest", "Run", false));
                return true;
            }

            if (string.Equals(validationBehavior, "communication-prepare", StringComparison.OrdinalIgnoreCase)
                || string.Equals(validationBehavior, "delayed-communication-operations", StringComparison.OrdinalIgnoreCase)
                || validationBehavior.StartsWith("event-buffer-reset-", StringComparison.OrdinalIgnoreCase)
                || validationBehavior.StartsWith("eventstream-", StringComparison.OrdinalIgnoreCase))
            {
                WriteRuntimeTargetMessage(writer, "RuntimeObserved", "Succeeded", new RuntimeTarget("0", "PlcUnderTest", "Run", false));
                return true;
            }

            if (string.Equals(validationBehavior, "runtime-observe-unavailable", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(writer, "Unavailable", "PlcsimAdvanced.V8.Worker.RuntimeNotPrepared", "No PLCSIM Advanced runtime is prepared.");
                return true;
            }

            return false;
        }

        private static bool TryHandleValidationRuntimeRevalidationBehavior(
            StreamWriter writer,
            string validationBehavior,
            IReadOnlyDictionary<string, string> values)
        {
            var state = string.Equals(validationBehavior, "runtime-observe-run", StringComparison.OrdinalIgnoreCase)
                ? "Run"
                : "Stop";
            if (!string.Equals(validationBehavior, "runtime-observe-stop", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(validationBehavior, "runtime-observe-run", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            values.TryGetValue("expectedRuntimeId", out var expectedRuntimeId);
            values.TryGetValue("expectedInstanceName", out var expectedInstanceName);
            if ((!string.IsNullOrWhiteSpace(expectedRuntimeId)
                    && !string.Equals(expectedRuntimeId, "0", StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(expectedInstanceName)
                    && !string.Equals(expectedInstanceName, "PlcUnderTest", StringComparison.Ordinal)))
            {
                WriteError(
                    writer,
                    "Rejected",
                    "PlcsimAdvanced.V8.Worker.RuntimeIdentityMismatch",
                    "The prepared PLCSIM Advanced runtime does not match the expected runtime identity.");
                return true;
            }

            WriteRuntimeTargetMessage(
                writer,
                "RuntimeRevalidated",
                "Succeeded",
                new RuntimeTarget("0", "PlcUnderTest", state, false));
            return true;
        }

        private static bool TryHandleValidationControlBehavior(
            StreamWriter writer,
            string validationBehavior,
            string command,
            string successState)
        {
            if (string.IsNullOrWhiteSpace(validationBehavior))
            {
                return false;
            }

            if (string.Equals(validationBehavior, "runtime-control", StringComparison.OrdinalIgnoreCase))
            {
                WriteLine(
                    writer,
                    CreateMessage("RuntimeControlled")
                    + "|status=" + Escape("Succeeded")
                    + "|state=" + Escape(successState));
                return true;
            }

            if (string.Equals(validationBehavior, "delayed-runtime-operations", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(250));
                WriteLine(
                    writer,
                    CreateMessage("RuntimeControlled")
                    + "|status=" + Escape("Succeeded")
                    + "|state=" + Escape(successState));
                return true;
            }

            if (string.Equals(validationBehavior, "runtime-state-before-control", StringComparison.OrdinalIgnoreCase))
            {
                WriteValidationRuntimeStateChanged(writer, successState);
                WriteLine(
                    writer,
                    CreateMessage("RuntimeControlled")
                    + "|status=" + Escape("Succeeded")
                    + "|state=" + Escape(successState));
                return true;
            }

            if (string.Equals(validationBehavior, $"{command}-error", StringComparison.OrdinalIgnoreCase))
            {
                WriteLine(
                    writer,
                    CreateMessage("Error")
                    + "|status=" + Escape("Failed")
                    + "|state=" + Escape("Unknown")
                    + "|code=" + Escape($"PlcsimAdvanced.V8.Worker.Validation{command}Error")
                    + "|message=" + Escape($"Validation {command} error."));
                return true;
            }

            if (string.Equals(validationBehavior, "runtime-control-unavailable", StringComparison.OrdinalIgnoreCase))
            {
                WriteLine(
                    writer,
                    CreateMessage("Error")
                    + "|status=" + Escape("Unavailable")
                    + "|state=" + Escape("Unknown")
                    + "|code=" + Escape("PlcsimAdvanced.V8.Worker.RuntimeNotPrepared")
                    + "|message=" + Escape("No PLCSIM Advanced runtime is prepared."));
                return true;
            }

            if (string.Equals(validationBehavior, "runtime-control-empty", StringComparison.OrdinalIgnoreCase)
                && string.Equals(command, "StartRuntime", StringComparison.OrdinalIgnoreCase))
            {
                WriteLine(
                    writer,
                    CreateMessage("Error")
                    + "|status=" + Escape("Failed")
                    + "|state=" + Escape("Stop")
                    + "|code=" + Escape("PlcsimAdvanced.V8.Worker.RuntimeProgramMissing")
                    + "|message=" + Escape(
                        "The simulated PLC instance contains no downloaded STEP 7 program. "
                        + "Download the project to the selected instance before starting the PLC."));
                return true;
            }

            return false;
        }

        private static void WriteValidationRuntimeStateChanged(StreamWriter writer, string currentState)
        {
            WriteLine(
                writer,
                CreateMessage("RuntimeStateChanged")
                + "|runtimeSessionId=" + Escape("validation-worker-session")
                + "|kind=" + Escape("OperatingStateChanged")
                + "|runtimeId=" + Escape("validation-runtime")
                + "|instanceName=" + Escape("ValidationRuntime")
                + "|previousState=" + Escape("Unknown")
                + "|currentState=" + Escape(currentState)
                + "|observedAt=" + Escape(DateTimeOffset.Now.ToString("O"))
                + "|code=" + Escape("OK")
                + "|message=" + Escape("Validation runtime-state event."));
        }

        private static bool TryHandleValidationPrepareBehavior(
            StreamWriter writer,
            string validationBehavior,
            IReadOnlyDictionary<string, string> values)
        {
            if (string.IsNullOrWhiteSpace(validationBehavior))
            {
                return false;
            }

            if (string.Equals(validationBehavior, "prepare-existing", StringComparison.OrdinalIgnoreCase))
            {
                WriteRuntimePrepared(writer, "Succeeded", new RuntimeTarget(
                    values.TryGetValue("id", out var id) && !string.IsNullOrWhiteSpace(id) ? id : "0",
                    values.TryGetValue("instanceName", out var instanceName) && !string.IsNullOrWhiteSpace(instanceName) ? instanceName : "PlcUnderTest",
                    "Stop",
                    false));
                return true;
            }

            if (string.Equals(validationBehavior, "prepare-owned", StringComparison.OrdinalIgnoreCase))
            {
                WriteRuntimePrepared(writer, "Succeeded", new RuntimeTarget(
                    "created-runtime",
                    values.TryGetValue("instanceName", out var instanceName) && !string.IsNullOrWhiteSpace(instanceName)
                        ? instanceName
                        : "PLCSIM.UnitTest.Created",
                    "Off",
                    true));
                return true;
            }

            if (string.Equals(validationBehavior, "prepare-storage-failure-clean-rollback", StringComparison.OrdinalIgnoreCase))
            {
                WriteValidationRuntimePrepareCompensationError(
                    writer,
                    values,
                    "Failed",
                    "PlcsimAdvanced.V8.Worker.StoragePathFailed",
                    "Validation storage path failure.",
                    powerOffSucceeded: true,
                    unregisterSucceeded: true);
                return true;
            }

            if (string.Equals(validationBehavior, "prepare-poweron-failure-poweroff-failed", StringComparison.OrdinalIgnoreCase))
            {
                WriteValidationRuntimePrepareCompensationError(
                    writer,
                    values,
                    "Failed",
                    "PlcsimAdvanced.V8.Worker.PowerOnFailed",
                    "Validation power-on failure.",
                    powerOffSucceeded: false,
                    unregisterSucceeded: true);
                return true;
            }

            if (string.Equals(validationBehavior, "prepare-poweron-failure-unregister-failed", StringComparison.OrdinalIgnoreCase))
            {
                WriteValidationRuntimePrepareCompensationError(
                    writer,
                    values,
                    "Unclear",
                    "PlcsimAdvanced.V8.Worker.PowerOnFailed",
                    "Validation power-on failure.",
                    powerOffSucceeded: true,
                    unregisterSucceeded: false);
                return true;
            }

            if (string.Equals(validationBehavior, "prepare-storage-failure-compensation-failed", StringComparison.OrdinalIgnoreCase))
            {
                WriteValidationRuntimePrepareCompensationError(
                    writer,
                    values,
                    "Unclear",
                    "PlcsimAdvanced.V8.Worker.StoragePathFailed",
                    "Validation storage path failure.",
                    powerOffSucceeded: false,
                    unregisterSucceeded: false);
                return true;
            }

            if (string.Equals(validationBehavior, "delayed-prepare-owned", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(250));
                WriteRuntimePrepared(writer, "Succeeded", new RuntimeTarget(
                    "created-runtime",
                    values.TryGetValue("instanceName", out var instanceName) && !string.IsNullOrWhiteSpace(instanceName)
                        ? instanceName
                        : "PLCSIM.UnitTest.Created",
                    "Off",
                    true));
                return true;
            }

            if (string.Equals(validationBehavior, "prepare-unavailable", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(writer, "Unavailable", "PlcsimAdvanced.V8.Worker.RuntimeTargetUnavailable", "Requested validation runtime target is unavailable.");
                return true;
            }

            if (string.Equals(validationBehavior, "prepare-ambiguous", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(writer, "Rejected", "PlcsimAdvanced.V8.Worker.RuntimeTargetAmbiguous", "Validation runtime target selection is ambiguous.");
                return true;
            }

            return false;
        }

        private static void WriteValidationRuntimePrepareCompensationError(
            StreamWriter writer,
            IReadOnlyDictionary<string, string> values,
            string status,
            string code,
            string message,
            bool powerOffSucceeded,
            bool unregisterSucceeded)
        {
            var instanceName = values.TryGetValue("instanceName", out var requestedInstanceName)
                ? requestedInstanceName
                : "ValidationOwnedRuntime";
            var storagePath = values.TryGetValue("storagePath", out var requestedStoragePath)
                ? requestedStoragePath
                : string.Empty;
            var compensation = new RuntimePrepareCompensation(
                "validation-created-runtime",
                instanceName,
                storagePath,
                new RuntimePrepareCompensationStep(
                    true,
                    powerOffSucceeded,
                    powerOffSucceeded ? "Validation PowerOff succeeded." : "Validation PowerOff failed."),
                new RuntimePrepareCompensationStep(
                    true,
                    unregisterSucceeded,
                    unregisterSucceeded ? "Validation UnregisterInstance succeeded." : "Validation UnregisterInstance failed."));

            WriteRuntimePrepareError(
                writer,
                RuntimePrepareResult.Failed(status, code, message, compensation));
        }
    }
}
