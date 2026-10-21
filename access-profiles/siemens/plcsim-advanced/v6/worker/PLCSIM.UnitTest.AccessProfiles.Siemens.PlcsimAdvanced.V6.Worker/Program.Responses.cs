using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6.Worker
{
    internal static partial class Program
    {
        private static void WriteReady(StreamWriter writer, string sessionId)
        {
            WriteLine(writer, CreateMessage("Ready") + "|sessionId=" + Escape(sessionId));
        }

        private static void WriteError(StreamWriter writer, string code, string message)
        {
            WriteLine(
                writer,
                CreateMessage("Error")
                + "|code=" + Escape(code)
                + "|message=" + Escape(message ?? string.Empty));
        }

        private static void WriteRunControlCommandResult(StreamWriter writer, RunControlCommandResult result)
        {
            WriteLine(
                writer,
                CreateMessage("RunControlCommandRequested")
                + "|status=" + Escape(result.Status)
                + "|command=" + Escape(result.Command)
                + "|accepted=" + Escape(result.Accepted ? "true" : "false"));
        }

        private static void WriteEventBufferResetResult(StreamWriter writer, EventBufferResetResult result)
        {
            WriteLine(
                writer,
                CreateMessage("EventBufferResetRequested")
                + "|status=" + Escape(result.Status)
                + "|accepted=" + Escape(result.Accepted ? "true" : "false"));
        }


        private static void WriteRunControlObserved(
            StreamWriter writer,
            RunControlObservationResult result,
            string communicationSessionId,
            string source)
        {
            WriteLine(
                writer,
                CreateMessage("RunControlObserved")
                + "|source=" + Escape(source)
                + "|status=" + Escape(result.Status)
                + "|communicationSessionId=" + Escape(communicationSessionId)
                + "|executionStatus=" + Escape(((int)result.ExecutionStatus).ToString(CultureInfo.InvariantCulture))
                + "|executionSignals=" + Escape(((ushort)result.ExecutionSignals).ToString(CultureInfo.InvariantCulture))
                + "|state=" + Escape(((int)result.State).ToString(CultureInfo.InvariantCulture))
                + "|result=" + Escape(((int)result.Result).ToString(CultureInfo.InvariantCulture)));
        }

        private static void WriteEventStreamMonitoringResult(
            StreamWriter writer,
            string messageType,
            EventStreamMonitoringResult result,
            ILogger logger,
            bool rawEventLoggingEnabled)
        {
            if (result.CapturedBatch is not null)
            {
                WriteEventBatchReceived(writer, result.CapturedBatch, logger, rawEventLoggingEnabled);
            }

            if (result.CapturedBatch is null && result.LostEventCount > 0)
            {
                logger.LogWarning(
                    "EventStream reports lost PLC events. CommunicationSessionId={CommunicationSessionId} LostEventCount={LostEventCount} ReadState={ReadState}",
                    result.CommunicationSessionId,
                    result.LostEventCount,
                    result.LastReadState);
            }

            WriteLine(
                writer,
                CreateMessage(messageType)
                + "|status=" + Escape(result.Status)
                + "|code=" + Escape(result.Code)
                + "|message=" + Escape(result.Message)
                + "|communicationSessionId=" + Escape(result.CommunicationSessionId)
                + "|isMonitoring=" + Escape(result.IsMonitoring ? "true" : "false")
                + "|lastReadState=" + Escape(result.LastReadState)
                + "|batchReleaseState=" + Escape(result.BatchReleaseState)
                + "|bufferObservedEmpty=" + Escape(result.BufferObservedEmpty ? "true" : "false")
                + "|lostEventCount=" + Escape(result.LostEventCount.ToString())
                + "|bufferFull=" + Escape(result.BufferFull ? "true" : "false")
                + "|producerSequenceId=" + Escape(result.ProducerSequenceId.ToString())
                + "|deletedSequenceId=" + Escape(result.DeletedSequenceId.ToString()));
        }

        private static void WriteEventStreamMonitoringStateChanged(
            StreamWriter writer,
            EventStreamMonitoringResult result)
        {
            WriteLine(
                writer,
                CreateMessage("EventStreamMonitoringStateChanged")
                + "|communicationSessionId=" + Escape(result.CommunicationSessionId)
                + "|isMonitoring=" + Escape(result.IsMonitoring ? "true" : "false")
                + "|lastReadState=" + Escape(result.LastReadState)
                + "|batchReleaseState=" + Escape(result.BatchReleaseState)
                + "|bufferObservedEmpty=" + Escape(result.BufferObservedEmpty ? "true" : "false")
                + "|lostEventCount=" + Escape(result.LostEventCount.ToString())
                + "|bufferFull=" + Escape(result.BufferFull ? "true" : "false")
                + "|producerSequenceId=" + Escape(result.ProducerSequenceId.ToString())
                + "|deletedSequenceId=" + Escape(result.DeletedSequenceId.ToString()));
        }

        private static void WriteEventBatchReceived(
            StreamWriter writer,
            EventStreamCapturedBatch batch,
            ILogger logger,
            bool rawEventLoggingEnabled)
        {
            LogEventStreamBatch(logger, batch, rawEventLoggingEnabled);

            var message = new StringBuilder(CreateMessage("EventBatchReceived"))
                .Append("|communicationSessionId=").Append(Escape(batch.CommunicationSessionId))
                .Append("|producerSequenceId=").Append(Escape(batch.ProducerSequenceId.ToString()))
                .Append("|deletedSequenceId=").Append(Escape(batch.DeletedSequenceId.ToString()))
                .Append("|lostEventCount=").Append(Escape(batch.LostEventCount.ToString()))
                .Append("|bufferObservedEmpty=").Append(Escape(batch.BufferObservedEmpty ? "true" : "false"))
                .Append("|readState=").Append(Escape(batch.ReadState))
                .Append("|eventCount=").Append(Escape(batch.CapturedEntries.Count.ToString(CultureInfo.InvariantCulture)));

            for (var index = 0; index < batch.CapturedEntries.Count; index++)
            {
                var captured = batch.CapturedEntries[index];
                var prefix = "|event" + index;
                message.Append(prefix).Append(".bufferIndex=").Append(Escape(captured.BufferIndex.ToString(CultureInfo.InvariantCulture)));
                message.Append(prefix).Append(".sequenceId=").Append(Escape(captured.SequenceId.ToString(CultureInfo.InvariantCulture)));
                message.Append(prefix).Append(".timestampNanoseconds=").Append(Escape(captured.PlcTimestampNanoseconds.ToString(CultureInfo.InvariantCulture)));
                message.Append(prefix).Append(".ownerScope=").Append(Escape(captured.OwnerScope.ToString(CultureInfo.InvariantCulture)));
                message.Append(prefix).Append(".eventType=").Append(Escape(captured.EventType.ToString(CultureInfo.InvariantCulture)));
                message.Append(prefix).Append(".severity=").Append(Escape(captured.Severity.ToString(CultureInfo.InvariantCulture)));
                message.Append(prefix).Append(".name=").Append(Escape(captured.Name));
                message.Append(prefix).Append(".resourceName=").Append(Escape(captured.ResourceName));
                message.Append(prefix).Append(".state=").Append(Escape(captured.State.ToString(CultureInfo.InvariantCulture)));
                message.Append(prefix).Append(".result=").Append(Escape(captured.Result.ToString(CultureInfo.InvariantCulture)));
                message.Append(prefix).Append(".message=").Append(Escape(captured.Message));
            }

            WriteLine(writer, message.ToString());
        }

        private static void LogEventStreamBatch(
            ILogger logger,
            EventStreamCapturedBatch batch,
            bool rawEventLoggingEnabled)
        {
            if (batch.CapturedEntries.Count == 0)
            {
                return;
            }

            var firstSequenceId = batch.CapturedEntries[0].SequenceId;
            var lastSequenceId = batch.CapturedEntries[batch.CapturedEntries.Count - 1].SequenceId;
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "EventStream batch captured. CommunicationSessionId={CommunicationSessionId} EventCount={EventCount} FirstSequenceId={FirstSequenceId} LastSequenceId={LastSequenceId} ProducerSequenceId={ProducerSequenceId} DeletedSequenceId={DeletedSequenceId} LostEventCount={LostEventCount} ReadState={ReadState}",
                    batch.CommunicationSessionId,
                    batch.CapturedEntries.Count,
                    firstSequenceId,
                    lastSequenceId,
                    batch.ProducerSequenceId,
                    batch.DeletedSequenceId,
                    batch.LostEventCount,
                    batch.ReadState);
            }

            if (batch.LostEventCount > 0)
            {
                logger.LogWarning(
                    "EventStream batch may be incomplete because the PLC reported lost events. CommunicationSessionId={CommunicationSessionId} EventCount={EventCount} FirstSequenceId={FirstSequenceId} LastSequenceId={LastSequenceId} LostEventCount={LostEventCount}",
                    batch.CommunicationSessionId,
                    batch.CapturedEntries.Count,
                    firstSequenceId,
                    lastSequenceId,
                    batch.LostEventCount);
            }

            if (!rawEventLoggingEnabled || !logger.IsEnabled(LogLevel.Debug))
            {
                return;
            }

            foreach (var captured in batch.CapturedEntries)
            {
                logger.LogDebug(
                    "Raw UnitTest event. CommunicationSessionId={CommunicationSessionId} BufferIndex={BufferIndex} SequenceId={SequenceId} PlcTimestampNanoseconds={PlcTimestampNanoseconds} OwnerScope={OwnerScope} EventType={EventType} Severity={Severity} Name={Name} ResourceName={ResourceName} State={State} Result={Result} EventMessage={EventMessage}",
                    batch.CommunicationSessionId,
                    captured.BufferIndex,
                    captured.SequenceId,
                    captured.PlcTimestampNanoseconds,
                    captured.OwnerScope,
                    captured.EventType,
                    captured.Severity,
                    NormalizeRawEventLogValue(captured.Name),
                    NormalizeRawEventLogValue(captured.ResourceName),
                    captured.State,
                    captured.Result,
                    NormalizeRawEventLogValue(captured.Message));
            }
        }

        private static string NormalizeRawEventLogValue(string value)
        {
            const int maxLength = 512;
            var singleLine = (value ?? string.Empty)
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
            return singleLine.Length <= maxLength
                ? singleLine
                : singleLine.Substring(0, maxLength) + "...";
        }

        private static void WriteEventStreamMonitoringFaulted(StreamWriter writer, EventStreamMonitoringFault fault)
        {
            WriteLine(
                writer,
                CreateMessage("EventStreamMonitoringFaulted")
                + "|status=" + Escape(fault.Status)
                + "|communicationSessionId=" + Escape(fault.CommunicationSessionId)
                + "|code=" + Escape(fault.Code)
                + "|message=" + Escape(fault.Message));
        }

        private static void WriteCommunicationHealthChanged(
            StreamWriter writer,
            string communicationSessionId,
            PlcsimCommunicationHealthState health,
            string message)
        {
            WriteLine(
                writer,
                CreateMessage("CommunicationHealthChanged")
                + "|communicationSessionId=" + Escape(communicationSessionId)
                + "|state=" + Escape(health.ToString())
                + "|message=" + Escape(message));
        }

        private static void WriteRuntimePrepared(StreamWriter writer, string status, RuntimeTarget target)
        {
            WriteRuntimeTargetMessage(writer, "RuntimePrepared", status, target);
        }

        private static void WriteRuntimeTargetMessage(StreamWriter writer, string type, string status, RuntimeTarget target)
        {
            WriteLine(
                writer,
                CreateMessage(type)
                + "|status=" + Escape(status)
                + "|id=" + Escape(target.Id)
                + "|instanceName=" + Escape(target.InstanceName)
                + "|productVersion=" + Escape("6.0")
                + "|accessApiVersion=" + Escape("6.0")
                + "|locality=" + Escape("Local")
                + "|state=" + Escape(target.State)
                + "|isUsable=" + Escape("true")
                + "|isOwned=" + Escape(target.IsOwned ? "true" : "false"));
        }

        private static void WriteRuntimePrepareError(StreamWriter writer, RuntimePrepareResult result)
        {
            if (result.Compensation is null)
            {
                WriteError(writer, result.Status, result.Code, result.Message);
                return;
            }

            var compensation = result.Compensation;
            WriteLine(
                writer,
                CreateMessage("Error")
                + "|status=" + Escape(result.Status)
                + "|code=" + Escape(result.Code)
                + "|message=" + Escape(result.Message ?? string.Empty)
                + "|compensationAttempted=true"
                + "|compensationComplete=" + Escape(compensation.Complete ? "true" : "false")
                + "|runtimeId=" + Escape(compensation.RuntimeId)
                + "|instanceName=" + Escape(compensation.InstanceName)
                + "|storagePath=" + Escape(compensation.StoragePath)
                + "|powerOffStatus=" + Escape(GetCompensationStepStatus(compensation.PowerOff))
                + "|powerOffMessage=" + Escape(compensation.PowerOff.Message)
                + "|unregisterStatus=" + Escape(GetCompensationStepStatus(compensation.Unregister))
                + "|unregisterMessage=" + Escape(compensation.Unregister.Message));
        }

        private static string GetCompensationStepStatus(RuntimePrepareCompensationStep step)
        {
            return !step.Attempted
                ? "NotAttempted"
                : step.Succeeded ? "Succeeded" : "Failed";
        }

        private static void WriteError(StreamWriter writer, string status, string code, string message)
        {
            WriteLine(
                writer,
                CreateMessage("Error")
                + "|status=" + Escape(status)
                + "|code=" + Escape(code)
                + "|message=" + Escape(message ?? string.Empty));
        }

        private static void WriteLine(StreamWriter writer, string line)
        {
            WorkerProtocol.WriteLine(writer, line);
        }

        private static string CreateMessage(string type)
        {
            return WorkerProtocol.CreateMessage(type);
        }

        private static string Escape(string value)
        {
            return WorkerProtocol.Escape(value);
        }

    }
}
