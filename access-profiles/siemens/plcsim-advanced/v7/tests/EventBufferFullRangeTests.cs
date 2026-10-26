using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7.Worker;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V7;

public sealed class EventBufferFullRangeTests
{
    private readonly EventBufferPollingSnapshotClassifier classifier = new(
        UnitTestContractVersions.EventStreamModel, "test");

    [Theory]
    [InlineData(1, 4, 2, new ushort[] { 1, 2 })]
    [InlineData(1, 4, 3, new ushort[] { 1, 2, 3 })]
    [InlineData(1, 4, 10, new ushort[] { 1, 2, 3 })]
    [InlineData(3, 2, 2, new ushort[] { 3, 4 })]
    [InlineData(3, 2, 3, new ushort[] { 3, 4, 1 })]
    [InlineData(3, 2, 10, new ushort[] { 3, 4, 1 })]
    public void Full_fifo_excludes_input_slot_and_respects_batch_limit(
        int output, int input, int limit, ushort[] expected)
    {
        Assert.Equal(expected, classifier.DeriveReadablePositions(Fifo(output, input), limit));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(8u)]
    public void Unoccupied_input_slot_cannot_affect_captured_events(uint inputSequence)
    {
        var fifo = Fifo(3, 2);
        var status = new PlcsimEventBufferStatusSnapshot(7, 4, 0);
        var result = classifier.Classify(fifo, status,
            new Dictionary<ushort, uint> { [3] = 5, [4] = 6, [1] = 7, [2] = inputSequence },
            fifo, status, 0, DateTimeOffset.UtcNow);

        Assert.Equal(EventBufferReadState.Valid, result.Snapshot.ReadState);
        Assert.Equal(new uint[] { 5, 6, 7 }, result.CapturedSequences.Select(item => item.SequenceId));
    }

    [Fact]
    public void Equal_indices_with_full_flag_do_not_authorize_a_whole_ring_read()
    {
        var fifo = Fifo(1, 1);
        var status = new PlcsimEventBufferStatusSnapshot(4, 0, 0);
        var result = classifier.Classify(fifo, status,
            new Dictionary<ushort, uint> { [1] = 1, [2] = 2, [3] = 3, [4] = 4 },
            fifo, status, 0, DateTimeOffset.UtcNow);

        Assert.Empty(result.ReadablePositions);
        Assert.Equal(EventBufferReadState.Inconsistent, result.Snapshot.ReadState);
        Assert.Empty(result.CapturedSequences);
    }

    [Fact]
    public void Empty_partial_and_invalid_metadata_keep_existing_range_guards()
    {
        Assert.Empty(classifier.DeriveReadablePositions(Fifo(1, 1) with { BufferEmpty = true, BufferFull = false }));
        Assert.Equal(new ushort[] { 1, 2 }, classifier.DeriveReadablePositions(Fifo(1, 3) with { BufferFull = false }));
        Assert.Empty(classifier.DeriveReadablePositions(Fifo(0, 2)));
        Assert.Empty(classifier.DeriveReadablePositions(Fifo(1, 5)));
        Assert.Empty(classifier.DeriveReadablePositions(Fifo(1, 4) with { ConfigError = true }));
        Assert.Empty(classifier.DeriveReadablePositions(Fifo(1, 4) with { IndexError = true }));
        Assert.Empty(classifier.DeriveReadablePositions(Fifo(1, 4), 0));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    public void Full_range_uses_the_published_length_without_a_fixed_capacity(int length)
    {
        var fifo = new PlcsimEventBufferFifoControlSnapshot(2, 1, (ushort)length, false, true, false, false);
        var positions = classifier.DeriveReadablePositions(fifo, 256);

        Assert.Equal(Enumerable.Range(2, Math.Min(length - 1, 256)).Select(index => (ushort)index), positions);
        Assert.DoesNotContain((ushort)1, positions);
    }

    private static PlcsimEventBufferFifoControlSnapshot Fifo(int output, int input) =>
        new((ushort)output, (ushort)input, 4, false, true, false, false);
}
