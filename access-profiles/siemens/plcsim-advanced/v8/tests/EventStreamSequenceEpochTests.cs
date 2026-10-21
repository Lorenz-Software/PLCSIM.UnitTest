using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8;

public sealed class EventStreamSequenceEpochTests
{
    [Fact]
    public void Worker_accepts_a_stable_prefix_without_cross_batch_epoch_state()
    {
        var classifier = new EventBufferPollingSnapshotClassifier(
            UnitTestContractVersions.EventStreamModel,
            "test");
        var full = Fifo(bufferEmpty: false, bufferFull: true, bufferLength: 4);
        var classification = classifier.Classify(
            full,
            Status(sequenceId: 7, deletedSequenceId: 0, lostEventCount: 3),
            new Dictionary<ushort, uint>
            {
                [1] = 1,
                [2] = 2,
                [3] = 3,
                [4] = 4
            },
            full,
            Status(sequenceId: 7, deletedSequenceId: 0, lostEventCount: 3),
            DateTimeOffset.UtcNow);

        Assert.Equal(EventBufferReadState.Valid, classification.Snapshot.ReadState);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, classification.CapturedSequences.Select(item => item.SequenceId));
        Assert.True(classification.Snapshot.FifoControl.BufferFull);
        Assert.Equal((ushort)3, classification.Snapshot.Status.LostEventCount);
    }

    [Fact]
    public void Sequence_regression_within_the_current_prefix_is_inconsistent()
    {
        var classifier = new EventBufferPollingSnapshotClassifier(
            UnitTestContractVersions.EventStreamModel,
            "test");
        var fifo = new PlcsimEventBufferFifoControlSnapshot(
            IndexOut: 1,
            IndexIn: 3,
            BufferLength: 4,
            BufferEmpty: false,
            BufferFull: false,
            ConfigError: false,
            IndexError: false);
        var classification = classifier.Classify(
            fifo,
            Status(sequenceId: 2, deletedSequenceId: 0, lostEventCount: 0),
            new Dictionary<ushort, uint>
            {
                [1] = 2,
                [2] = 1
            },
            fifo,
            Status(sequenceId: 2, deletedSequenceId: 0, lostEventCount: 0),
            DateTimeOffset.UtcNow);

        Assert.Equal(EventBufferReadState.Inconsistent, classification.Snapshot.ReadState);
        Assert.Empty(classification.CapturedSequences);
    }

    [Fact]
    public void Stable_prefix_of_non_atomic_full_buffer_read_remains_releasable()
    {
        var classifier = new EventBufferPollingSnapshotClassifier(
            UnitTestContractVersions.EventStreamModel,
            "test");
        var full = Fifo(bufferEmpty: false, bufferFull: true, bufferLength: 4);

        var classification = classifier.Classify(
            full,
            Status(sequenceId: 8, deletedSequenceId: 4, lostEventCount: 1),
            new ushort[] { 1, 2, 3 },
            new Dictionary<ushort, uint>
            {
                [1] = 5,
                [2] = 6,
                [3] = 7
            },
            full,
            Status(sequenceId: 8, deletedSequenceId: 4, lostEventCount: 1),
            DateTimeOffset.UtcNow);

        Assert.Equal(EventBufferReadState.Valid, classification.Snapshot.ReadState);
        Assert.Equal(new uint[] { 5, 6, 7 }, classification.CapturedSequences.Select(item => item.SequenceId));
    }

    [Fact]
    public void Readable_positions_are_limited_to_the_oldest_contiguous_fifo_prefix()
    {
        var classifier = new EventBufferPollingSnapshotClassifier(
            UnitTestContractVersions.EventStreamModel,
            "test");
        var wrapped = new PlcsimEventBufferFifoControlSnapshot(
            IndexOut: 3,
            IndexIn: 2,
            BufferLength: 4,
            BufferEmpty: false,
            BufferFull: false,
            ConfigError: false,
            IndexError: false);

        Assert.Equal(new ushort[] { 3, 4 }, classifier.DeriveReadablePositions(wrapped, maximumCount: 2));
        Assert.Equal(new ushort[] { 3, 4, 1 }, classifier.DeriveReadablePositions(wrapped, maximumCount: 10));
    }

    [Fact]
    public void Producer_progress_during_a_bounded_prefix_read_remains_valid_and_exposes_backlog()
    {
        var classifier = new EventBufferPollingSnapshotClassifier(
            UnitTestContractVersions.EventStreamModel,
            "test");
        var pre = new PlcsimEventBufferFifoControlSnapshot(1, 3, 4, false, false, false, false);
        var post = new PlcsimEventBufferFifoControlSnapshot(1, 1, 4, false, true, false, false);
        var prefix = classifier.DeriveReadablePositions(pre, maximumCount: 1);

        var classification = classifier.Classify(
            pre,
            Status(sequenceId: 2, deletedSequenceId: 0, lostEventCount: 0),
            prefix,
            new Dictionary<ushort, uint> { [1] = 1 },
            post,
            Status(sequenceId: 4, deletedSequenceId: 0, lostEventCount: 0),
            DateTimeOffset.UtcNow);

        Assert.Equal(EventBufferReadState.Valid, classification.Snapshot.ReadState);
        Assert.Equal(new ushort[] { 1 }, classification.ReadablePositions);
        Assert.True(classifier.DeriveReadablePositions(post).Count > prefix.Count);
    }

    private static PlcsimEventBufferFifoControlSnapshot Fifo(
        bool bufferEmpty,
        bool bufferFull = false,
        ushort bufferLength = 4)
    {
        return new PlcsimEventBufferFifoControlSnapshot(
            IndexOut: 1,
            IndexIn: 1,
            BufferLength: bufferLength,
            BufferEmpty: bufferEmpty,
            BufferFull: bufferFull,
            ConfigError: false,
            IndexError: false);
    }

    private static PlcsimEventBufferStatusSnapshot Status(
        uint sequenceId,
        uint deletedSequenceId,
        ushort lostEventCount)
    {
        return new PlcsimEventBufferStatusSnapshot(sequenceId, deletedSequenceId, lostEventCount);
    }
}
