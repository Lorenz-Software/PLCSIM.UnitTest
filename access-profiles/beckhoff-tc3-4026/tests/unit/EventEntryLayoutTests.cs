using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class EventEntryLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsesSymbolOffsetsAndRetainsAggregationWithoutAddingRequirements(bool alternate)
    {
        var fixture = new EventStreamFixture(alternateLayout: alternate);
        var layout = await fixture.Resolve();
        var bytes = fixture.Event(uint.MaxValue);
        const string message = "{\"total\":1,\"runtime\":4294967295,\"extra\":3}";
        fixture.Text(bytes, EventStreamFixture.EventPath, "Message", message);
        fixture.Field(bytes, EventStreamFixture.EventPath, "EventType")[0] = 50;
        var entry = layout.Entry.Decode(bytes);
        Assert.Equal(uint.MaxValue, entry.SequenceId);
        Assert.Equal(UnitTestOwnerScope.TestCase, entry.OwnerScope);
        Assert.Equal(UnitTestEventType.Aggregation, entry.EventType);
        Assert.Equal(LogLevel.Information, entry.Severity);
        Assert.Equal("UnitTest.Suite.Case", entry.Name);
        Assert.Equal("Resource", entry.ResourceName);
        Assert.Equal(UnitTestLifecycleState.Completed, entry.State);
        Assert.Equal(UnitTestResultState.Passed, entry.Result);
        Assert.Equal(message, entry.Message);
        Array.Fill(bytes, (byte)0);
        Assert.Equal(message, entry.Message);
    }

    [Theory]
    [InlineData(-10, -10)]
    [InlineData(0, 0)]
    [InlineData(21, 10)]
    [InlineData(56, 20)]
    [InlineData(89, 30)]
    [InlineData(short.MinValue, short.MaxValue)]
    public async Task PreservesSignedLifecycleAndResultValuesWithoutSemanticRewriting(short state, short result)
    {
        var fixture = new EventStreamFixture();
        var layout = await fixture.Resolve();
        var bytes = fixture.Event();
        BinaryPrimitives.WriteInt16LittleEndian(fixture.Field(bytes, EventStreamFixture.EventPath, "State"), state);
        BinaryPrimitives.WriteInt16LittleEndian(fixture.Field(bytes, EventStreamFixture.EventPath, "Result"), result);
        var entry = layout.Entry.Decode(bytes);
        Assert.Equal(state, (short)entry.State);
        Assert.Equal(result, (short)entry.Result);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(5, 10, 1)]
    [InlineData(10, 20, 2)]
    [InlineData(15, 30, 3)]
    [InlineData(20, 40, 4)]
    [InlineData(25, 50, 5)]
    [InlineData(30, 60, 2)]
    [InlineData(40, 10, 3)]
    [InlineData(50, 10, 4)]
    [InlineData(255, 255, 2)]
    public async Task PreservesClassificationCodesAndMapsAllSeverityLevels(byte scope, byte type, byte severity)
    {
        var fixture = new EventStreamFixture();
        var layout = await fixture.Resolve();
        var bytes = fixture.Event();
        fixture.Field(bytes, EventStreamFixture.EventPath, "OwnerScope")[0] = scope;
        fixture.Field(bytes, EventStreamFixture.EventPath, "EventType")[0] = type;
        fixture.Field(bytes, EventStreamFixture.EventPath, "Severity")[0] = severity;
        var entry = layout.Entry.Decode(bytes);
        Assert.Equal(scope, (byte)entry.OwnerScope);
        Assert.Equal(type, (byte)entry.EventType);
        Assert.Equal((LogLevel)severity, entry.Severity);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(99L, 0L)]
    [InlineData(-99L, 0L)]
    [InlineData(199L, 1L)]
    [InlineData(-199L, -1L)]
    [InlineData(1720000000123456799L, 17200000001234567L)]
    [InlineData(long.MaxValue, 92233720368547758L)]
    [InlineData(long.MinValue, -92233720368547758L)]
    public async Task ConvertsUnixNanosecondsWithIntegerPrecision(long nanoseconds, long ticks)
    {
        var fixture = new EventStreamFixture();
        var layout = await fixture.Resolve();
        var entry = layout.Entry.Decode(fixture.Event(timestamp: nanoseconds));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks(ticks), entry.Timestamp);
        Assert.Equal(TimeSpan.Zero, entry.Timestamp.Offset);
    }

    [Theory]
    [InlineData("Name", 80)]
    [InlineData("ResourceName", 32)]
    [InlineData("Message", 254)]
    public async Task ReadsFullStringCapacityAndStopsAtFirstNull(string name, int length)
    {
        var fixture = new EventStreamFixture();
        var layout = await fixture.Resolve();
        var bytes = fixture.Event();
        fixture.Text(bytes, EventStreamFixture.EventPath, name, new string('X', length));
        var entry = layout.Entry.Decode(bytes);
        Assert.Equal(length, Text(entry, name).Length);
        fixture.Field(bytes, EventStreamFixture.EventPath, name)[1] = 0;
        fixture.Field(bytes, EventStreamFixture.EventPath, name)[2] = 255;
        Assert.Equal("X", Text(layout.Entry.Decode(bytes), name));
    }

    [Theory]
    [InlineData("Name", false)]
    [InlineData("Name", true)]
    [InlineData("ResourceName", false)]
    [InlineData("ResourceName", true)]
    [InlineData("Message", false)]
    [InlineData("Message", true)]
    public async Task RejectsNonAsciiOrUnterminatedStrings(string name, bool unterminated)
    {
        var fixture = new EventStreamFixture();
        var layout = await fixture.Resolve();
        var bytes = fixture.Event();
        if (unterminated) fixture.Field(bytes, EventStreamFixture.EventPath, name).Fill((byte)'X');
        else fixture.Field(bytes, EventStreamFixture.EventPath, name)[0] = 128;
        Assert.Throws<InvalidDataException>(() => layout.Entry.Decode(bytes));
    }

    [Fact]
    public async Task RejectsInvalidSeverityZeroSequenceAndWrongEntryLength()
    {
        var fixture = new EventStreamFixture();
        var layout = await fixture.Resolve();
        Assert.Throws<InvalidDataException>(() => layout.Entry.Decode(fixture.Event(0)));
        var bytes = fixture.Event();
        fixture.Field(bytes, EventStreamFixture.EventPath, "Severity")[0] = 6;
        Assert.Throws<InvalidDataException>(() => layout.Entry.Decode(bytes));
        Assert.Throws<InvalidDataException>(() => layout.Entry.Decode(new byte[layout.Entry.Symbol.Size - 1]));
        Assert.Throws<InvalidDataException>(() => layout.Entry.Decode(new byte[layout.Entry.Symbol.Size + 1]));
    }

    [Fact]
    public async Task DecodesWholeRangeInStorageOrderWithoutTimestampSortingOrGapRepair()
    {
        var fixture = new EventStreamFixture();
        var layout = await fixture.Resolve();
        var data = fixture.Event(17, 199).Concat(fixture.Event(19, 100)).ToArray();
        var events = layout.DecodeRange(data, 2);
        Assert.Equal(new uint[] { 17, 19 }, events.Select(entry => entry.SequenceId));
        Assert.Equal(events[0].Timestamp, events[1].Timestamp);
        Assert.Throws<InvalidDataException>(() => layout.DecodeRange(data[..^1], 2));
        Assert.Throws<InvalidDataException>(() => layout.DecodeRange(data, 1));
        Assert.Throws<InvalidDataException>(() => layout.DecodeRange(fixture.Event(17).Concat(fixture.Event(0)).ToArray(), 2));
        Assert.Empty(layout.DecodeRange([], 0));
    }

    private static string Text(UnitTestEventEntry entry, string field) => field switch
    {
        "Name" => entry.Name,
        "ResourceName" => entry.ResourceName,
        _ => entry.Message
    };
}
