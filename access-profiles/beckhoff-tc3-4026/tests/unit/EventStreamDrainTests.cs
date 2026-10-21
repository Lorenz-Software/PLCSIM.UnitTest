using PLCSIM.UnitTest.AccessProfiles.Beckhoff;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.UnitTests;
using TwinCAT.Ads;
using Xunit;
using static PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit.FakeEventStreamAccess;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class EventStreamDrainTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(256)]
    public async Task RepeatedFullRingsWithDelayedDeletionDeliverEveryEventOnce(int batchSize)
    {
        const int capacity = 255;
        var access = new FakeEventStreamAccess();
        var drain = new EventStreamDrain(new("session"), batchSize);
        var delivered = new List<uint>();
        uint deleted = 0;
        for (var wave = 1; wave <= 4; wave++)
        {
            var producer = (uint)(wave * capacity);
            while (deleted < producer)
            {
                access.Value = Observation((ushort)(producer - deleted), producer, deleted,
                    indexOut: (ushort)((240 + deleted) % 256 + 1));
                access.Payload = Enumerable.Range((int)deleted + 1, (int)(producer - deleted))
                    .Select(value => Entry((uint)value)).ToArray();
                var batch = await drain.CaptureAsync(access, () => { }, default);
                Assert.NotNull(batch);
                Assert.InRange(batch.Events.Count, 1, Math.Min(batchSize, capacity));
                delivered.AddRange(batch.Events.Select(e => e.SequenceId));
                var reads = access.Reads;
                var writes = access.Writes.Count;
                for (var pending = 0; pending < 3; pending++)
                {
                    Assert.Null(await drain.CaptureAsync(access, () => { }, default));
                    Assert.Equal(deleted, drain.State.DeletedSequenceId);
                    Assert.False(drain.State.BufferObservedEmpty);
                    Assert.Equal(reads, access.Reads);
                    Assert.Equal(writes, access.Writes.Count);
                }
                deleted = access.Writes.Last();
            }
            access.Value = Observation(0, producer, deleted);
            Assert.True((await drain.CaptureAsync(access, () => { }, default))!.BufferObservedEmpty);
        }
        Assert.Equal(Enumerable.Range(1, 4 * capacity).Select(n => (uint)n), delivered);
        Assert.Equal(0, access.Resets);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(32, 3)]
    [InlineData(int.MaxValue, 3)]
    public async Task CapturesOneBoundedPrefixThenWritesHighestCapturedSequence(int limit, int expected)
    {
        var access = new FakeEventStreamAccess();
        var drain = new EventStreamDrain(new("session"), limit);
        var batch = await drain.CaptureAsync(access, () => { }, default);
        Assert.NotNull(batch);
        Assert.Equal(expected, batch.Events.Count);
        Assert.Equal((uint)expected, Assert.Single(access.Writes));
        Assert.Equal(new[] { "metadata", "payload", "metadata", "pending", "write" }, access.Calls);
        Assert.Equal(3u, batch.ProducerSequenceId);
        Assert.Equal(0u, batch.DeletedSequenceId);
        Assert.False(batch.BufferObservedEmpty);
        Assert.Equal(EventBufferReadState.Valid, drain.State.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Completed, drain.State.BatchReleaseState);
        Assert.True(drain.NeedsDrain);
    }

    [Fact]
    public async Task FullBufferAndLostEventsDoNotRejectPhysicalPrefixOrNumericGaps()
    {
        var access = new FakeEventStreamAccess
        {
            Value = Observation(3, 20, 0, 17, indexOut: 3, length: 4),
            Payload = [Entry(1), Entry(7), Entry(20)]
        };
        var drain = new EventStreamDrain(new("session"), 32);
        var batch = await drain.CaptureAsync(access, () => { }, default);
        Assert.Equal(new uint[] { 1, 7, 20 }, batch!.Events.Select(item => item.SequenceId));
        Assert.Equal(20u, Assert.Single(access.Writes));
        Assert.Equal((ushort)17, batch.LostEventCount);
        Assert.True(drain.State.BufferFull);
    }

    [Fact]
    public async Task ProducerAppendAndIncreasingLossUseRealPostMetadata()
    {
        var access = new FakeEventStreamAccess();
        access.Metadata.Enqueue(Observation());
        access.Metadata.Enqueue(Observation(8, 100, lost: 92));
        var drain = new EventStreamDrain(new("session"), 3);
        var batch = await drain.CaptureAsync(access, () => { }, default);
        Assert.Equal(3u, Assert.Single(access.Writes));
        Assert.Equal(100u, batch!.ProducerSequenceId);
        Assert.Equal((ushort)92, batch.LostEventCount);
        Assert.Equal(0u, drain.State.DeletedSequenceId);
        Assert.True(drain.NeedsDrain);
    }

    [Fact]
    public async Task PendingDeletionRefreshesMetadataWithoutPayloadOrWriteOrFakeBatchState()
    {
        var access = new FakeEventStreamAccess();
        var drain = new EventStreamDrain(new("session"), 2);
        await drain.CaptureAsync(access, () => { }, default);
        access.Value = Observation(8, 12, lost: 4, pending: 2);
        var batch = await drain.CaptureAsync(access, () => { }, default);
        Assert.Null(batch);
        Assert.Equal(1, access.Reads);
        Assert.Single(access.Writes);
        Assert.Equal(3, access.MetadataReads);
        Assert.Equal(12u, drain.State.ProducerSequenceId);
        Assert.Equal((ushort)4, drain.State.LostEventCount);
        Assert.Equal(EventBufferReadState.Valid, drain.State.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Completed, drain.State.BatchReleaseState);
        Assert.True(drain.NeedsDrain);
    }

    [Fact]
    public async Task InitialPendingObservationDoesNotManufactureReadEvidence()
    {
        var access = new FakeEventStreamAccess { Value = Observation(pending: 3) };
        var drain = new EventStreamDrain(new("session"), 32);
        Assert.Null(await drain.CaptureAsync(access, () => { }, default));
        Assert.Equal(EventBufferReadState.NotObserved, drain.State.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Idle, drain.State.BatchReleaseState);
        Assert.False(drain.State.BufferObservedEmpty);
        Assert.Equal(3u, drain.State.ProducerSequenceId);
        Assert.Equal(0, access.Reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NewPendingRequestAfterPayloadWithholdsBatch(bool inPostMetadata)
    {
        var access = new FakeEventStreamAccess();
        if (inPostMetadata)
        {
            access.Metadata.Enqueue(Observation());
            access.Metadata.Enqueue(Observation(pending: 1));
        }
        else access.PendingBeforeWrite = 1;
        var drain = new EventStreamDrain(new("session"), 32);
        Assert.Null(await drain.CaptureAsync(access, () => { }, default));
        Assert.Empty(access.Writes);
        Assert.Equal(EventBufferReadState.Valid, drain.State.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Idle, drain.State.BatchReleaseState);
        Assert.True(drain.NeedsDrain);
    }

    [Theory]
    [InlineData("indexOut")]
    [InlineData("deleted")]
    [InlineData("producer")]
    [InlineData("lost")]
    [InlineData("length")]
    [InlineData("shrink")]
    public async Task RejectsChangedPrefixWithoutPartialSalvage(string change)
    {
        var before = Observation(3, 10, 0, 7);
        var after = change switch
        {
            "indexOut" => Observation(3, 10, 0, 7, indexOut: 2),
            "deleted" => Observation(3, 10, 1, 7),
            "producer" => Observation(3, 9, 0, 7),
            "lost" => Observation(3, 10, 0, 6),
            "length" => Observation(3, 10, 0, 7, length: 128),
            _ => Observation(2, 10, 0, 7)
        };
        var access = new FakeEventStreamAccess();
        access.Metadata.Enqueue(before);
        access.Metadata.Enqueue(after);
        var drain = new EventStreamDrain(new("session"), 32);
        await Assert.ThrowsAsync<InvalidDataException>(() => drain.CaptureAsync(access, () => { }, default));
        drain.RejectCapture();
        Assert.Empty(access.Writes);
        Assert.Equal(EventBufferReadState.Inconsistent, drain.State.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Idle, drain.State.BatchReleaseState);
        Assert.Equal(after.Status.ProducerSequenceId, drain.State.ProducerSequenceId);
    }

    [Theory]
    [InlineData(0, 2, 3)]
    [InlineData(1, 1, 3)]
    [InlineData(2, 1, 3)]
    [InlineData(1, 2, 4)]
    public async Task RejectsInvalidSequenceWithoutUsingTimestamps(int a, int b, int c)
    {
        var access = new FakeEventStreamAccess { Payload = [Entry((uint)a), Entry((uint)b), Entry((uint)c)] };
        await Assert.ThrowsAsync<InvalidDataException>(() => new EventStreamDrain(new("session"), 32)
            .CaptureAsync(access, () => { }, default));
        Assert.Empty(access.Writes);
    }

    [Fact]
    public async Task RejectsAlreadyDeletedPayload()
    {
        var access = new FakeEventStreamAccess { Value = Observation(3, 4, 1) };
        await Assert.ThrowsAsync<InvalidDataException>(() => new EventStreamDrain(new("session"), 32)
            .CaptureAsync(access, () => { }, default));
        Assert.Empty(access.Writes);
    }

    [Fact]
    public async Task TruncatedPayloadPreservesPreMetadataAndReleasesNothing()
    {
        var access = new FakeEventStreamAccess { Payload = [Entry(1)] };
        var drain = new EventStreamDrain(new("session"), 32);
        await Assert.ThrowsAsync<InvalidDataException>(() => drain.CaptureAsync(access, () => { }, default));
        Assert.Empty(access.Writes);
        Assert.Equal(3u, drain.State.ProducerSequenceId);
        Assert.False(drain.State.BufferObservedEmpty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPayloadOrPostReadPreservesTrustedPreMetadata(bool post)
    {
        var access = new FakeEventStreamAccess { Value = Observation(3, 9, lost: 6) };
        var error = new AdsAccessException("Read", AdsErrorCode.ClientSyncTimeOut);
        if (post) access.BeforeMetadata = _ => access.MetadataReads == 2 ? Task.FromException(error) : Task.CompletedTask;
        else access.ReadError = error;
        var drain = new EventStreamDrain(new("session"), 32);
        Assert.Same(error, await Assert.ThrowsAsync<AdsAccessException>(() => drain.CaptureAsync(access, () => { }, default)));
        Assert.Equal(9u, drain.State.ProducerSequenceId);
        Assert.Equal((ushort)6, drain.State.LostEventCount);
        Assert.False(drain.State.BufferObservedEmpty);
        Assert.Empty(access.Writes);
    }

    [Fact]
    public async Task InvalidMetadataDoesNotReplaceLastTrustedObservation()
    {
        var access = new FakeEventStreamAccess();
        var drain = new EventStreamDrain(new("session"), 32);
        await drain.CaptureAsync(access, () => { }, default);
        access.Value = Observation(0, 0) with { Fifo = Observation(0, 0).Fifo with { ConfigError = true } };
        await Assert.ThrowsAsync<InvalidDataException>(() => drain.CaptureAsync(access, () => { }, default));
        Assert.Equal(3u, drain.State.ProducerSequenceId);
        Assert.False(drain.State.BufferObservedEmpty);
        Assert.Single(access.Writes);
    }

    [Fact]
    public async Task WatermarkFailureDoesNotPublishOrInventDeletionAndNextAttemptRereads()
    {
        var access = new FakeEventStreamAccess { WriteError = new IOException("Write rejected") };
        var drain = new EventStreamDrain(new("session"), 32);
        await Assert.ThrowsAsync<IOException>(() => drain.CaptureAsync(access, () => { }, default));
        Assert.True(drain.WritingWatermark);
        Assert.Equal(EventBufferReadState.Valid, drain.State.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Idle, drain.State.BatchReleaseState);
        Assert.Equal(0u, drain.State.DeletedSequenceId);
        access.WriteError = null;
        access.Value = Observation(2, 5, deleted: 3);
        access.Payload = [Entry(4), Entry(5)];
        var next = await drain.CaptureAsync(access, () => { }, default);
        Assert.Equal(new uint[] { 3, 5 }, access.Writes);
        Assert.Equal(new uint[] { 4, 5 }, next!.Events.Select(item => item.SequenceId));
        Assert.Equal(2, access.Reads);
    }

    [Fact]
    public async Task AcceptedWriteSurvivesCancellationAndBatchOwnsPayload()
    {
        using var cancel = new CancellationTokenSource();
        var access = new FakeEventStreamAccess();
        access.BeforeWrite = (_, token) =>
        {
            Assert.False(token.CanBeCanceled);
            access.Payload[0] = Entry(99);
            cancel.Cancel();
            return Task.CompletedTask;
        };
        var batch = await new EventStreamDrain(new("session"), 32).CaptureAsync(access, () => { }, cancel.Token);
        Assert.Equal(1u, batch!.Events[0].SequenceId);
        Assert.Throws<NotSupportedException>(() => ((IList<UnitTestEventEntry>)batch.Events)[0] = Entry(99));
        Assert.Single(access.Writes);
    }

    [Fact]
    public async Task CancellationBeforeCommitNeverWrites()
    {
        using var cancel = new CancellationTokenSource();
        var access = new FakeEventStreamAccess { BeforePendingCheck = cancel.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EventStreamDrain(new("session"), 32)
            .CaptureAsync(access, () => { }, cancel.Token));
        Assert.Empty(access.Writes);
    }

    [Fact]
    public async Task ChangedTechnicalContextBeforeCommitNeverWrites()
    {
        var current = true;
        var access = new FakeEventStreamAccess { BeforePendingCheck = () => current = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EventStreamDrain(new("session"), 32)
            .CaptureAsync(access, () => { if (!current) throw new InvalidOperationException("Replaced context"); }, default));
        Assert.Empty(access.Writes);
    }

    [Fact]
    public async Task EmptyIsObservedNotInferredAndRepeatedUnchangedEmptyMayCoalesce()
    {
        var access = new FakeEventStreamAccess();
        var drain = new EventStreamDrain(new("session"), 32);
        await drain.CaptureAsync(access, () => { }, default);
        Assert.False(drain.State.BufferObservedEmpty);
        access.Value = Observation(0, 3, 3, indexOut: 4);
        var empty = await drain.CaptureAsync(access, () => { }, default);
        Assert.NotNull(empty);
        Assert.Empty(empty.Events);
        Assert.True(empty.BufferObservedEmpty);
        Assert.Equal(EventBufferReadState.Empty, drain.State.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Idle, drain.State.BatchReleaseState);
        Assert.Equal(3u, drain.State.DeletedSequenceId);
        Assert.False(drain.NeedsDrain);
        Assert.Null(await drain.CaptureAsync(access, () => { }, default));
        access.Value = Observation(0, 0);
        var resetObservation = await drain.CaptureAsync(access, () => { }, default);
        Assert.NotNull(resetObservation);
        Assert.Equal(0u, resetObservation.ProducerSequenceId);
        Assert.Single(access.Writes);
    }

    [Fact]
    public async Task ProducerAppendAfterEmptyPreReadSchedulesDrainWithoutFalseEmptyBatch()
    {
        var access = new FakeEventStreamAccess();
        access.Metadata.Enqueue(Observation(0, 0));
        access.Metadata.Enqueue(Observation());
        var drain = new EventStreamDrain(new("session"), 32);
        Assert.Null(await drain.CaptureAsync(access, () => { }, default));
        Assert.True(drain.NeedsDrain);
        Assert.False(drain.State.BufferObservedEmpty);
        Assert.Empty(access.Writes);
        Assert.Equal(0, access.Reads);
    }

    [Fact]
    public async Task MultipleBatchesDrainWrappedRingWithoutDuplicateDeliveryAndFinishWithRealEmpty()
    {
        const int total = 200;
        var access = new FakeEventStreamAccess();
        var drain = new EventStreamDrain(new("session"), 32);
        var delivered = new List<uint>();
        var consumed = 0;
        while (consumed < total)
        {
            var remaining = total - consumed;
            access.Value = Observation((ushort)remaining, total, (uint)consumed, indexOut: (ushort)((240 + consumed) % 256 + 1));
            access.Payload = Enumerable.Range(consumed + 1, remaining).Select(value => Entry((uint)value)).ToArray();
            var batch = await drain.CaptureAsync(access, () => { }, default);
            Assert.NotNull(batch);
            Assert.InRange(batch.Events.Count, 1, 32);
            delivered.AddRange(batch.Events.Select(item => item.SequenceId));
            Assert.Null(await drain.CaptureAsync(access, () => { }, default));
            Assert.Equal((uint)consumed, drain.State.DeletedSequenceId);
            consumed += batch.Events.Count;
        }
        access.Value = Observation(0, total, total, indexOut: (240 + total) % 256 + 1);
        var empty = await drain.CaptureAsync(access, () => { }, default);
        Assert.True(empty!.BufferObservedEmpty);
        Assert.Equal(Enumerable.Range(1, total).Select(item => (uint)item), delivered);
        Assert.Equal(new uint[] { 32, 64, 96, 128, 160, 192, 200 }, access.Writes);
    }

    [Theory]
    [InlineData("{}", "GVL_UnitTest.EventStream", "GVL_UnitTest.RunControl", 10, 1, 32)]
    [InlineData("{\"unitTest\":{\"eventBuffer\":\"Test.Events\",\"runControl\":\"Test.Run\",\"monitoringIntervalMs\":25,\"drainIntervalMs\":2,\"maxEventsPerBatch\":1024}}", "Test.Events", "Test.Run", 25, 2, 1024)]
    public void SettingsUseExistingConfigurationAndDoNotCapBatchAt256(string json, string buffer, string run, int monitoring, int drain, int count)
    {
        var configuration = new BeckhoffConfigurationService("", ProjectOwnershipMode.ExternallyManaged, TimeProvider.System);
        Assert.True(configuration.LoadConfig(json).IsSuccessful);
        var settings = EventStreamSettings.Parse(configuration.Configuration, ProjectOwnershipMode.ExternallyManaged);
        settings.Validate();
        Assert.Equal(buffer, settings.EventRoot);
        Assert.Equal(run, settings.RunControlRoot);
        Assert.Equal(TimeSpan.FromMilliseconds(monitoring), settings.MonitoringInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(drain), settings.DrainInterval);
        Assert.Equal(count, settings.MaxEventsPerBatch);
    }
}
