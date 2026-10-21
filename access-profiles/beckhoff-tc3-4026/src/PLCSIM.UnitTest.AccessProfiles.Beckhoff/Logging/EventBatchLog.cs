using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Logging;

internal sealed class EventBatchLog(ILogger logger, TimeProvider timeProvider, bool rawEnabled)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    internal void Record(UnitTestEventBatch batch, string netId, int port)
    {
        try
        {
            if (batch.Events.Count == 0 || !logger.IsEnabled(LogLevel.Debug)) return;
            logger.LogDebug("EventBatch accepted for handover: Target={NetId}:{Port}, CommunicationSession={Session}, Count={Count}, Sequence={First}..{Last}, Producer={Producer}, Deleted={Deleted}, Lost={Lost}, Empty={Empty}, ReadState={ReadState}",
                netId, port, batch.SessionId.Value, batch.Events.Count, batch.Events[0].SequenceId,
                batch.Events[^1].SequenceId, batch.ProducerSequenceId, batch.DeletedSequenceId,
                batch.LostEventCount, batch.BufferObservedEmpty, batch.ReadState);
            if (!rawEnabled) return;
            var pcObservedAt = timeProvider.GetUtcNow();
            foreach (var entry in batch.Events)
            {
                var json = JsonSerializer.Serialize(new
                {
                    pcObservedAt, netId, plcPort = port, communicationSessionId = batch.SessionId.Value,
                    entry.SequenceId, plcEventTimestamp = entry.Timestamp, entry.OwnerScope,
                    entry.EventType, entry.Severity, entry.Name, entry.ResourceName, entry.State,
                    entry.Result, entry.Message
                }, JsonOptions);
                logger.LogDebug("RawPlcEvent {Payload}", json);
            }
        }
        catch { /* Logging/formatting cannot prevent the already accepted batch from reaching the host. */ }
    }
}
