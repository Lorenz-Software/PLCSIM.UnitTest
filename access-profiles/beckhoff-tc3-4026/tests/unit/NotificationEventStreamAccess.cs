using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

// The same cache/drain boundary as ADS, without a PLC or native ADS calls.
internal sealed class NotificationEventStreamAccess(FakeEventStreamAccess inner, TimeProvider clock)
    : IEventStreamAccess, IEventStreamNotificationAccess
{
    internal EventStreamNotificationCache Cache { get; } = new(1, clock);
    internal Func<TimeSpan, CancellationToken, Task>? Wait;
    public int ArrayLength => inner.ArrayLength;
    public async Task PrepareAsync(EventStreamSettings settings, CancellationToken token)
    {
        await inner.PrepareAsync(settings, token);
        await ReadMetadataAsync(token);
    }
    public async Task<EventBufferObservation> ReadMetadataAsync(CancellationToken token)
    {
        var revision = Cache.BeginRead();
        var value = await inner.ReadMetadataAsync(token);
        Cache.Confirm(value, revision);
        return value;
    }
    public Task<EventBufferObservation> ReadBeforeAsync(CancellationToken token) =>
        Cache.Candidate() is { } value ? Task.FromResult(value) : ReadMetadataAsync(token);
    public bool IsReleasePending(EventBufferObservation value) => Cache.IsReleasePending(value);
    public uint AcceptedWatermark => Cache.AcceptedWatermark;
    public void RestoreAcceptedWatermark(uint sequence) => Cache.RestoreAcceptedWatermark(sequence);
    public Task WaitForObservationAsync(TimeSpan fallback, CancellationToken token) =>
        Wait?.Invoke(fallback, token) ?? Cache.WaitAsync(fallback, token);
    public void DiscardCachedObservation() => Cache.Discard();
    public Task<IReadOnlyList<UnitTestEventEntry>> ReadEventsAsync(EventBufferFifo fifo, int maxEvents, CancellationToken token) =>
        inner.ReadEventsAsync(fifo, maxEvents, token);
    public Task<uint> ReadDeleteSequenceAsync(CancellationToken token) => inner.ReadDeleteSequenceAsync(token);
    public async Task WriteWatermarkAsync(uint sequence, CancellationToken token)
    {
        await inner.WriteWatermarkAsync(sequence, token);
        Cache.Accepted(sequence);
    }
    public Task WriteResetAsync(CancellationToken token) => inner.WriteResetAsync(token);
    public async ValueTask DisposeAsync() { Cache.Close(); await inner.DisposeAsync(); }
}
