using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using Xunit;
using static PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit.FakeEventStreamAccess;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class EventStreamNotificationTests
{
    private static EventStreamNotificationCache NewCache() => new(1, TimeProvider.System);
    private static void Confirm(EventStreamNotificationCache cache, EventBufferObservation value) => cache.Confirm(value, cache.BeginRead());
    private static void Notify(EventStreamNotificationCache cache, EventBufferObservation value, long generation = 1) =>
        cache.Observe(generation, value.Fifo, value.Status);

    [Fact]
    public void BootstrapAndEmptyRequireExplicitReads()
    {
        var cache = NewCache();
        Notify(cache, Observation());
        Assert.Null(cache.Candidate());
        Confirm(cache, Observation());
        Assert.Equal(Observation(), cache.Candidate());
        Notify(cache, Observation(0, 3, 3));
        Assert.Null(cache.Candidate());
    }

    [Fact]
    public void LateNotificationCannotRegressConfirmedCounters()
    {
        var cache = NewCache();
        Confirm(cache, Observation(3, 13, 10));
        Notify(cache, Observation(3, 3));
        Assert.Equal(13u, cache.Candidate()!.Status.ProducerSequenceId);
        Assert.Equal(10u, cache.Candidate()!.Status.DeletedSequenceId);
    }

    [Fact]
    public void NewerNotificationDuringReadIsRetained()
    {
        var cache = NewCache();
        Confirm(cache, Observation());
        var revision = cache.BeginRead();
        Notify(cache, Observation(4, 4));
        cache.Confirm(Observation(), revision);
        Assert.Equal(4u, cache.Candidate()!.Status.ProducerSequenceId);
    }

    [Fact]
    public void ResetRegressionRemainsLatchedDespiteSubsequentProgress()
    {
        var cache = NewCache();
        Confirm(cache, Observation());
        Notify(cache, Observation(20, 20));
        Notify(cache, Observation(0, 0));
        Notify(cache, Observation(30, 30));
        Assert.Throws<AdsAccessException>(() => cache.Candidate());
        Assert.Throws<AdsAccessException>(() => cache.BeginRead());
    }

    [Fact]
    public void FirstCallbackZeroPhaseCannotDisappearBehindLaterProgress()
    {
        var cache = NewCache();
        Confirm(cache, Observation());
        Notify(cache, Observation(0, 0));
        Notify(cache, Observation(30, 30));
        Assert.Throws<AdsAccessException>(() => cache.Candidate());
    }

    [Fact]
    public void OldGenerationAndClosedCallbacksHaveNoEffect()
    {
        var cache = NewCache();
        Confirm(cache, Observation());
        Notify(cache, Observation(30, 30), 0);
        cache.Fault(0);
        cache.ObserveCommand(0, 123);
        Assert.Equal(Observation(), cache.Candidate());
        cache.Close();
        Notify(cache, Observation());
        Assert.Throws<ObjectDisposedException>(() => cache.Candidate());
    }

    [Fact]
    public void CommandZeroCannotAcknowledgeAcceptedBatch()
    {
        var cache = NewCache();
        Confirm(cache, Observation());
        cache.Accepted(3);
        cache.ObserveCommand(1, 0);
        Confirm(cache, Observation());
        Assert.Null(cache.Candidate());
        Assert.True(cache.IsReleasePending(Observation()));
        Notify(cache, Observation(3, 6, 3));
        Assert.NotNull(cache.Candidate());
        Assert.False(cache.IsReleasePending(Observation(3, 6, 3)));
    }

    [Fact]
    public void CommandNotificationDuringReadRequiresAnotherExplicitRead()
    {
        var cache = NewCache();
        Confirm(cache, Observation());
        var revision = cache.BeginRead();
        cache.ObserveCommand(1, 3);
        cache.Confirm(Observation(), revision);
        Assert.Null(cache.Candidate());
        Confirm(cache, Observation(pending: 3));
        cache.ObserveCommand(1, 0);
        Assert.Null(cache.Candidate());
        Confirm(cache, Observation());
        Assert.NotNull(cache.Candidate());
    }

    [Fact]
    public void OnlyConfirmedEmptyResetClearsAcceptedWatermark()
    {
        var cache = NewCache();
        Confirm(cache, Observation());
        cache.Accepted(3);
        cache.Discard();
        Assert.True(cache.IsReleasePending(Observation()));
        Confirm(cache, Observation(0, 0));
        Assert.False(cache.IsReleasePending(Observation(0, 0)));
    }

    [Fact]
    public async Task NotificationCandidateStillUsesPostReadAndPreWriteCheck()
    {
        var inner = new FakeEventStreamAccess();
        await using var access = new NotificationEventStreamAccess(inner, TimeProvider.System);
        Confirm(access.Cache, Observation());
        var drain = new EventStreamDrain(new("session"), 2);
        var batch = await drain.CaptureAsync(access, () => { }, default);
        Assert.Equal(new uint[] { 1, 2 }, batch!.Events.Select(e => e.SequenceId));
        Assert.Equal(new[] { "payload", "metadata", "pending", "write" }, inner.Calls);
        // Even a premature command-zero observation cannot release the same prefix again.
        inner.Value = Observation();
        Assert.Null(await drain.CaptureAsync(access, () => { }, default));
        Assert.Single(inner.Writes);
        Assert.Equal(1, inner.Reads);
        inner.Value = Observation(1, 3, 2, indexOut: 3);
        inner.Payload = [Entry(3)];
        Notify(access.Cache, inner.Value);
        Assert.Equal(3u, Assert.Single((await drain.CaptureAsync(access, () => { }, default))!.Events).SequenceId);
    }

    [Fact]
    public async Task InconsistentPostReadDoesNotWrite()
    {
        var inner = new FakeEventStreamAccess { Value = Observation(2, 3, 1, indexOut: 2) };
        await using var access = new NotificationEventStreamAccess(inner, TimeProvider.System);
        Confirm(access.Cache, Observation());
        var drain = new EventStreamDrain(new("session"), 2);
        await Assert.ThrowsAsync<InvalidDataException>(() => drain.CaptureAsync(access, () => { }, default));
        Assert.Empty(inner.Writes);
    }

    [Fact]
    public async Task EmptyNotificationRequiresTwoExplicitReadsBeforeEmptyPublication()
    {
        var inner = new FakeEventStreamAccess { Value = Observation(0, 3, 3) };
        await using var access = new NotificationEventStreamAccess(inner, TimeProvider.System);
        Confirm(access.Cache, Observation());
        Notify(access.Cache, inner.Value);
        var drain = new EventStreamDrain(new("session"), 2);
        Assert.True((await drain.CaptureAsync(access, () => { }, default))!.BufferObservedEmpty);
        Assert.Equal(new[] { "metadata", "metadata" }, inner.Calls);
        Assert.Empty(inner.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationSpanUsesSymbolSizesIncludingPadding(bool padded)
    {
        var fixture = new EventStreamFixture(alternateLayout: padded);
        var fifo = fixture.Symbols[EventStreamFixture.Root + ".FifoControl"];
        var statusPath = EventStreamFixture.Root + ".Status";
        var shift = (int)(fifo.Offset + fifo.Size) - (int)fixture.Symbols[statusPath].Offset;
        foreach (var key in fixture.Symbols.Keys.Where(k => k == statusPath || k.StartsWith(statusPath + ".")).ToArray())
            fixture.Symbols[key] = fixture.Symbols[key] with { Offset = (uint)(fixture.Symbols[key].Offset + shift) };
        var layout = await fixture.Resolve();
        Assert.Equal((fifo.Group, fifo.Offset, fifo.Size + layout.Status.Symbol.Size), AdsEventStreamAccess.GetNotificationSpan(layout));
    }

    [Fact]
    public async Task NoncontiguousMetadataFailsExplicitly()
    {
        var layout = await new EventStreamFixture().Resolve();
        Assert.Throws<InvalidDataException>(() => AdsEventStreamAccess.GetNotificationSpan(layout));
    }
}
