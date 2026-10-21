using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Testing.Evidence;

/// <summary>
/// Loads embedded fake event-stream evidence samples.
/// </summary>
public static class FakeEvidenceLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public static FakeEvidenceSet Load(string sampleName)
    {
        var resourceName = GetResourceName(sampleName);
        using var stream = typeof(FakeEvidenceLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded evidence sample '{sampleName}' was not found.");

        var document = JsonSerializer.Deserialize<FakeEvidenceDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Embedded evidence sample '{sampleName}' is empty.");

        return new FakeEvidenceSet(
            document.Name,
            document.Description,
            document.Batches.Select(ToBatch).ToArray());
    }

    public static IReadOnlyList<string> GetAvailableSamples()
    {
        const string marker = ".Evidence.";
        return typeof(FakeEvidenceLoader).Assembly
            .GetManifestResourceNames()
            .Where(name => name.Contains(marker, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(name => name[(name.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..^".json".Length])
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetResourceName(string sampleName)
    {
        var assemblyName = typeof(FakeEvidenceLoader).Assembly.GetName().Name;
        return $"{assemblyName}.Evidence.{sampleName}.json";
    }

    private static UnitTestEventBatch ToBatch(FakeEvidenceBatch batch)
    {
        return new UnitTestEventBatch(
            new CommunicationSessionId(batch.SessionId),
            batch.Events.Select(ToEntry).ToArray(),
            batch.ProducerSequenceId,
            batch.DeletedSequenceId,
            batch.LostEventCount,
            batch.BufferObservedEmpty,
            batch.ReadState);
    }

    private static UnitTestEventEntry ToEntry(FakeEvidenceEvent entry)
    {
        return new UnitTestEventEntry(
            entry.SequenceId,
            entry.Timestamp,
            entry.OwnerScope,
            entry.EventType,
            entry.Severity,
            entry.Name,
            entry.ResourceName,
            entry.State,
            entry.Result,
            entry.Message);
    }

    private sealed record FakeEvidenceDocument(
        string Name,
        string Description,
        IReadOnlyList<FakeEvidenceBatch> Batches);

    private sealed record FakeEvidenceBatch(
        string SessionId,
        uint ProducerSequenceId,
        uint DeletedSequenceId,
        ushort LostEventCount,
        bool BufferObservedEmpty,
        EventBufferReadState ReadState,
        IReadOnlyList<FakeEvidenceEvent> Events);

    private sealed record FakeEvidenceEvent(
        uint SequenceId,
        DateTimeOffset Timestamp,
        UnitTestOwnerScope OwnerScope,
        UnitTestEventType EventType,
        LogLevel Severity,
        string Name,
        string ResourceName,
        UnitTestLifecycleState State,
        UnitTestResultState Result,
        string Message);
}
