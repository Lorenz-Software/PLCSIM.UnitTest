using System.Buffers.Binary;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using TwinCAT.Ads;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class EventStreamLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodesIndependentFifoFlagsAndUnsignedStatus(bool alternate)
    {
        var fixture = new EventStreamFixture(ushort.MaxValue, alternate);
        var layout = await fixture.Resolve();
        var fifo = fixture.Data(EventStreamFixture.Root + ".FifoControl");
        foreach (var name in new[] { "IndexOut", "IndexIn", "ElementCount", "BufferLength" })
            BinaryPrimitives.WriteUInt16LittleEndian(fixture.Field(fifo, layout.Fifo.Symbol.Path, name), ushort.MaxValue);
        foreach (var name in new[] { "BufferEmpty", "BufferFull", "BufferAlmostFull", "ConfigError", "IndexError" })
            fixture.Field(fifo, layout.Fifo.Symbol.Path, name)[0] = 1;
        var value = layout.DecodeFifo(fifo);
        Assert.Equal(new EventBufferFifo(65535, 65535, true, true, true, 65535, 65535, true, true), value);
        Assert.Throws<InvalidDataException>(() => value.GetReadablePrefix(32, layout.ArrayLength));
        var status = fixture.Data(layout.Status.Symbol.Path);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Field(status, layout.Status.Symbol.Path, "SequenceId"), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Field(status, layout.Status.Symbol.Path, "DeletedSequenceId"), uint.MaxValue - 1);
        BinaryPrimitives.WriteUInt16LittleEndian(fixture.Field(status, layout.Status.Symbol.Path, "LostEventCount"), ushort.MaxValue);
        Assert.Equal(new EventBufferStatus(uint.MaxValue, uint.MaxValue - 1, ushort.MaxValue), layout.DecodeStatus(status));
    }

    [Theory]
    [InlineData("BufferEmpty")]
    [InlineData("BufferFull")]
    [InlineData("BufferAlmostFull")]
    [InlineData("ConfigError")]
    [InlineData("IndexError")]
    public async Task RejectsMalformedBoolean(string field)
    {
        var fixture = new EventStreamFixture();
        var layout = await fixture.Resolve();
        var bytes = fixture.Data(layout.Fifo.Symbol.Path);
        fixture.Field(bytes, layout.Fifo.Symbol.Path, field)[0] = 2;
        Assert.Throws<InvalidDataException>(() => layout.DecodeFifo(bytes));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", true)]
    [InlineData("1.0.0", "4026.26.1", true)]
    [InlineData("1.0.1", "1.0.0", false)]
    [InlineData("", "1.0.0", false)]
    [InlineData("1.0.0", "1.0", false)]
    [InlineData("1.0.0", "1.0.0.1", false)]
    [InlineData("1.0.0", "1..0", false)]
    [InlineData("1.0.0", "1.x.0", false)]
    public async Task ValidatesEventContractSeparatelyFromFrameworkVersion(string contract, string framework, bool valid)
    {
        var fixture = new EventStreamFixture(alternateLayout: true);
        var layout = await fixture.Resolve();
        var data = fixture.Data(layout.Contract.Symbol.Path);
        fixture.Text(data, layout.Contract.Symbol.Path, "ContractVersion", contract);
        fixture.Text(data, layout.Contract.Symbol.Path, "FrameworkVersion", framework);
        if (valid) Assert.Equal(new EventStreamContractInfo(contract, framework), layout.DecodeContract(data));
        else Assert.Throws<InvalidDataException>(() => layout.DecodeContract(data));
        Assert.DoesNotContain(fixture.Requests, path => path.Contains("RunControl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectsIncompleteMetadataAndInvalidVersionEncoding()
    {
        var fixture = new EventStreamFixture();
        var layout = await fixture.Resolve();
        Assert.Throws<InvalidDataException>(() => layout.DecodeContract(new byte[layout.Contract.Symbol.Size - 1]));
        Assert.Throws<InvalidDataException>(() => layout.DecodeStatus(new byte[layout.Status.Symbol.Size - 1]));
        Assert.Throws<InvalidDataException>(() => layout.DecodeFifo(new byte[layout.Fifo.Symbol.Size - 1]));
        var bytes = fixture.Data(layout.Contract.Symbol.Path);
        fixture.Field(bytes, layout.Contract.Symbol.Path, "ContractVersion").Fill((byte)'1');
        Assert.Throws<InvalidDataException>(() => layout.DecodeContract(bytes));
        fixture.Field(bytes, layout.Contract.Symbol.Path, "ContractVersion")[0] = 128;
        fixture.Field(bytes, layout.Contract.Symbol.Path, "ContractVersion")[1] = 0;
        Assert.Throws<InvalidDataException>(() => layout.DecodeContract(bytes));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("size")]
    [InlineData("group")]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("overlap")]
    [InlineData("wrong-path")]
    [InlineData("array-field")]
    public async Task RejectsInvalidEntryFieldMetadata(string failure)
    {
        var fixture = new EventStreamFixture();
        var key = EventStreamFixture.EventPath + ".Timestamp";
        var field = fixture.Symbols[key];
        var parent = fixture.Symbols[EventStreamFixture.EventPath];
        fixture.Symbols[key] = failure switch
        {
            "type" => field with { Type = AdsDataTypeId.ADST_REAL64 },
            "size" => field with { Size = 4 },
            "group" => field with { Group = field.Group + 1 },
            "before" => field with { Offset = parent.Offset - 1 },
            "after" => field with { Offset = parent.Offset + (uint)parent.Size - 1 },
            "overlap" => field with { Offset = parent.Offset },
            "wrong-path" => field with { Path = "Other.Timestamp" },
            _ => field with { Array = new(0, 8, 1) }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Resolve());
    }

    [Theory]
    [InlineData("missing-shape")]
    [InlineData("lower-bound")]
    [InlineData("too-small")]
    [InlineData("too-large")]
    [InlineData("type")]
    [InlineData("size")]
    [InlineData("stride")]
    [InlineData("first-offset")]
    [InlineData("last-offset")]
    [InlineData("last-size")]
    [InlineData("last-group")]
    [InlineData("address-overflow")]
    public async Task RejectsInconsistentArrayMetadata(string failure)
    {
        var fixture = new EventStreamFixture();
        var key = EventStreamFixture.Root + ".Buffer";
        var buffer = fixture.Symbols[key];
        switch (failure)
        {
            case "missing-shape": fixture.Symbols[key] = buffer with { Array = null }; break;
            case "lower-bound": fixture.Symbols[key] = buffer with { Array = buffer.Array! with { LowerBound = 0 } }; break;
            case "too-small": fixture.Symbols[key] = buffer with { Array = buffer.Array! with { Length = 1 } }; break;
            case "too-large": fixture.Symbols[key] = buffer with { Array = buffer.Array! with { Length = 65536 } }; break;
            case "type": fixture.Symbols[key] = buffer with { Type = AdsDataTypeId.ADST_UINT8 }; break;
            case "size": fixture.Symbols[key] = buffer with { Size = buffer.Size - 1 }; break;
            case "stride": fixture.Symbols[key] = buffer with { Array = buffer.Array! with { ElementSize = 401 } }; break;
            case "first-offset": fixture.Symbols[key] = buffer with { Offset = buffer.Offset - 1, Size = buffer.Size + 1 }; break;
            case "address-overflow": fixture.Symbols[key] = buffer with { Offset = uint.MaxValue - 10 }; break;
            default:
                key += "[256]";
                var last = fixture.Symbols[key];
                fixture.Symbols[key] = failure switch
                {
                    "last-offset" => last with { Offset = last.Offset - 1 },
                    "last-size" => last with { Size = last.Size - 1 },
                    _ => last with { Group = last.Group + 1 }
                };
                break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Resolve());
    }

    [Theory]
    [InlineData("FifoControl", "IndexOut", AdsDataTypeId.ADST_INT16, 2)]
    [InlineData("FifoControl", "BufferEmpty", AdsDataTypeId.ADST_UINT8, 1)]
    [InlineData("Status", "SequenceId", AdsDataTypeId.ADST_INT32, 4)]
    [InlineData("Status", "LostEventCount", AdsDataTypeId.ADST_UINT32, 4)]
    [InlineData("ContractInfo", "ContractVersion", AdsDataTypeId.ADST_STRING, 12)]
    public async Task RejectsWrongMetadataWireTypes(string owner, string name, AdsDataTypeId type, int size)
    {
        var fixture = new EventStreamFixture();
        var key = EventStreamFixture.Root + "." + owner + "." + name;
        fixture.Symbols[key] = fixture.Symbols[key] with { Type = type, Size = size };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Resolve());
    }

    [Fact]
    public async Task StopsResolutionOnCancellationOrSymbolFailureWithoutFallback()
    {
        var fixture = new EventStreamFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Resolve(cancellation.Token));
        Assert.Empty(fixture.Requests);
        using var duringRead = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EventStreamLayout.ResolveAsync(EventStreamFixture.Root,
            async (path, token) => { var symbol = await fixture.Describe(path, token); duringRead.Cancel(); return symbol; }, duringRead.Token));
        Assert.Single(fixture.Requests);
        var error = new AdsAccessException("symbol unavailable", AdsErrorCode.DeviceSymbolVersionInvalid);
        Assert.Same(error, await Assert.ThrowsAsync<AdsAccessException>(() => EventStreamLayout.ResolveAsync(EventStreamFixture.Root,
            (_, _) => throw error, default)));
    }

    [Fact]
    public async Task ConvertsWrappedPrefixToExactAdsByteRangesUsingPublishedLength()
    {
        var fixture = new EventStreamFixture(1024, true);
        var layout = await fixture.Resolve();
        var fifo = new EventBufferFifo(7, 3, false, false, false, 4, 8, false, false);
        var ranges = layout.GetReadRanges(fifo, 3);
        Assert.Equal(2, ranges.Count);
        Assert.Equal(new EventBufferReadRange(layout.Buffer.Group, layout.Buffer.Offset + (uint)(6 * layout.Entry.Symbol.Size),
            2 * layout.Entry.Symbol.Size, 2), ranges[0]);
        Assert.Equal(new EventBufferReadRange(layout.Buffer.Group, layout.Buffer.Offset, layout.Entry.Symbol.Size, 1), ranges[1]);
        var full = new EventBufferFifo(1, 1024, false, true, true, 1023, 1024, false, false);
        Assert.Equal(1023, Assert.Single(layout.GetReadRanges(full, int.MaxValue)).EventCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.DecodeRange([], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.DecodeRange([], 1024));
    }
}
