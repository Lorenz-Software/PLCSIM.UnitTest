using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.UnitTests;
using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed class EventEntryLayout
{
    internal static readonly EventStreamField[] Fields =
    [
        new("SequenceId", AdsDataTypeId.ADST_UINT32, 4),
        new("Timestamp", AdsDataTypeId.ADST_INT64, 8),
        new("OwnerScope", AdsDataTypeId.ADST_UINT8, 1),
        new("EventType", AdsDataTypeId.ADST_UINT8, 1),
        new("Severity", AdsDataTypeId.ADST_UINT8, 1),
        new("Name", AdsDataTypeId.ADST_STRING, 81),
        new("ResourceName", AdsDataTypeId.ADST_STRING, 33),
        new("State", AdsDataTypeId.ADST_INT16, 2),
        new("Result", AdsDataTypeId.ADST_INT16, 2),
        new("Message", AdsDataTypeId.ADST_STRING, 255)
    ];

    private readonly EventStreamStructureLayout layout;
    internal EventEntryLayout(EventStreamSymbol symbol, IReadOnlyList<EventStreamSymbol> fields) => layout = new(symbol, Fields, fields);
    internal EventStreamSymbol Symbol => layout.Symbol;

    internal UnitTestEventEntry Decode(ReadOnlySpan<byte> data)
    {
        layout.RequireComplete(data);
        var sequence = layout.UInt32(data, "SequenceId");
        if (sequence == 0) throw new InvalidDataException("A readable event must have a positive SequenceId.");
        var severity = layout.Field(data, "Severity")[0];
        if (severity > 5) throw new InvalidDataException($"Invalid event severity '{severity}'.");
        var nanoseconds = BinaryPrimitives.ReadInt64LittleEndian(layout.Field(data, "Timestamp"));
        // Integer conversion preserves 100 ns precision without a floating-point round trip.
        var timestamp = DateTimeOffset.UnixEpoch.AddTicks(nanoseconds / 100);
        return new(sequence, timestamp,
            (UnitTestOwnerScope)layout.Field(data, "OwnerScope")[0],
            (UnitTestEventType)layout.Field(data, "EventType")[0], (LogLevel)severity,
            layout.Text(data, "Name"), layout.Text(data, "ResourceName"),
            (UnitTestLifecycleState)layout.Int16(data, "State"),
            (UnitTestResultState)layout.Int16(data, "Result"), layout.Text(data, "Message"));
    }
}
