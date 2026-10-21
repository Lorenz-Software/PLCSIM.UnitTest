using System.Buffers.Binary;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using TwinCAT.Ads;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class EventStreamSumReadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodesSymbolDerivedLayoutsAndPendingCommand(bool alternate)
    {
        var fixture = new EventStreamFixture(alternateLayout: alternate);
        var layout = await fixture.Resolve();
        var fifo = fixture.Data(layout.Fifo.Symbol.Path);
        var status = fixture.Data(layout.Status.Symbol.Path);
        BinaryPrimitives.WriteUInt16LittleEndian(fixture.Field(fifo, layout.Fifo.Symbol.Path, "ElementCount"), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Field(status, layout.Status.Symbol.Path, "SequenceId"), 123);
        var pending = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pending, uint.MaxValue);
        var result = AdsEventStreamAccess.DecodeMetadata(layout, AdsErrorCode.NoError,
            new AdsErrorCode[3], [fifo, status, pending]);
        Assert.Equal(new EventBufferObservation(layout.DecodeFifo(fifo), layout.DecodeStatus(status), uint.MaxValue), result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RejectsOverallAndIndividualFailuresBeforeDecoding(int failedItem)
    {
        var layout = await new EventStreamFixture().Resolve();
        var errors = new AdsErrorCode[3];
        if (failedItem >= 0) errors[failedItem] = AdsErrorCode.TargetPortNotFound;
        Assert.Throws<AdsAccessException>(() => AdsEventStreamAccess.DecodeMetadata(layout,
            failedItem < 0 ? AdsErrorCode.TargetPortNotFound : AdsErrorCode.NoError,
            errors, [ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task RejectsMissingOrUnexpectedItemCounts(int count)
    {
        var layout = await new EventStreamFixture().Resolve();
        Assert.Throws<InvalidDataException>(() => AdsEventStreamAccess.DecodeMetadata(layout,
            AdsErrorCode.NoError, count < 0 ? null : new AdsErrorCode[count], new ReadOnlyMemory<byte>[3]));
        Assert.Throws<InvalidDataException>(() => AdsEventStreamAccess.DecodeMetadata(layout,
            AdsErrorCode.NoError, new AdsErrorCode[3], count < 0 ? null : new ReadOnlyMemory<byte>[count]));
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(0, 1)]
    [InlineData(1, -1)]
    [InlineData(1, 1)]
    [InlineData(2, -1)]
    [InlineData(2, 1)]
    public async Task RejectsTruncatedAndOversizedBlocks(int item, int delta)
    {
        var layout = await new EventStreamFixture().Resolve();
        ReadOnlyMemory<byte>[] blocks = [new byte[layout.Fifo.Symbol.Size], new byte[layout.Status.Symbol.Size], new byte[4]];
        blocks[item] = new byte[blocks[item].Length + delta];
        Assert.Throws<InvalidDataException>(() => AdsEventStreamAccess.DecodeMetadata(layout,
            AdsErrorCode.NoError, new AdsErrorCode[3], blocks));
    }
}
