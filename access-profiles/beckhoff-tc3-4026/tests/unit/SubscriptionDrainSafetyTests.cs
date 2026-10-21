using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using Xunit;
using static PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit.FakeEventStreamAccess;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class SubscriptionDrainSafetyTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(32, false)]
    [InlineData(255, false)]
    [InlineData(1, true)]
    [InlineData(32, true)]
    [InlineData(255, true)]
    public async Task RepeatedWrappedRingsDeliverExactlyOnceWithEarlyOrMissingNotifications(int batchSize, bool notify)
    {
        var inner = new FakeEventStreamAccess();
        await using var access = new NotificationEventStreamAccess(inner, TimeProvider.System);
        var drain = new EventStreamDrain(new("session"), batchSize);
        var delivered = new List<uint>();
        uint deleted = 0;
        for (var wave = 1; wave <= 3; wave++)
        {
            var producer = (uint)(wave * 255);
            while (deleted < producer)
            {
                inner.Value = Observation((ushort)(producer - deleted), producer, deleted,
                    indexOut: (ushort)((240 + deleted) % 256 + 1));
                inner.Payload = Enumerable.Range((int)deleted + 1, (int)(producer - deleted)).Select(n => Entry((uint)n)).ToArray();
                EventBufferObservation? acknowledged = null;
                inner.BeforeWrite = (sequence, _) =>
                {
                    acknowledged = Observation((ushort)(producer - sequence), producer, sequence,
                        indexOut: (ushort)((240 + sequence) % 256 + 1));
                    if (notify)
                    {
                        // PLC acknowledgement can arrive before the write await returns.
                        access.Cache.Observe(1, acknowledged.Fifo, acknowledged.Status);
                        access.Cache.ObserveCommand(1, 0);
                    }
                    return Task.CompletedTask;
                };
                var batch = await drain.CaptureAsync(access, () => { }, default);
                Assert.NotNull(batch);
                delivered.AddRange(batch.Events.Select(entry => entry.SequenceId));
                deleted = batch.Events[^1].SequenceId;
                Assert.NotNull(acknowledged);
                inner.Value = acknowledged;
            }
            // In particular, do not rely on a final empty callback.
            var empty = await drain.CaptureAsync(access, () => { }, default);
            Assert.NotNull(empty);
            Assert.True(empty.BufferObservedEmpty);
            Assert.Equal(producer, empty.DeletedSequenceId);
        }
        Assert.Equal(Enumerable.Range(1, 765).Select(n => (uint)n), delivered);
        Assert.Equal(inner.Writes.Count, inner.Writes.Distinct().Count());
        Assert.Equal(0, inner.Resets);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ReorderedCounterRegressionCannotBeHiddenByCoalescing(int counter)
    {
        var cache = new EventStreamNotificationCache(1, TimeProvider.System);
        var first = Observation(5, 20, 10, lost: 5);
        cache.Confirm(first, cache.BeginRead());
        cache.Observe(1, first.Fifo, first.Status);
        var regression = counter switch
        {
            0 => first with { Status = first.Status with { ProducerSequenceId = 19 } },
            1 => first with { Status = first.Status with { DeletedSequenceId = 9 } },
            _ => first with { Status = first.Status with { LostEventCount = 4 } }
        };
        cache.Observe(1, regression.Fifo, regression.Status);
        cache.Observe(1, first.Fifo, first.Status);
        Assert.Throws<AdsAccessException>(() => cache.Candidate());
    }

    [Fact]
    public async Task LateOldGenerationCannotConfirmCurrentRelease()
    {
        var inner = new FakeEventStreamAccess();
        await using var access = new NotificationEventStreamAccess(inner, TimeProvider.System);
        var drain = new EventStreamDrain(new("session"), 3);
        Assert.NotNull(await drain.CaptureAsync(access, () => { }, default));
        inner.Value = Observation();
        var late = Observation(0, 3, 3);
        access.Cache.Observe(0, late.Fifo, late.Status);
        access.Cache.ObserveCommand(0, 0);
        Assert.Null(await drain.CaptureAsync(access, () => { }, default));
        Assert.Single(inner.Writes);
        Assert.Equal(1, inner.Reads);
    }
}
