using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed record EventBufferObservation(EventBufferFifo Fifo, EventBufferStatus Status, uint DeleteSequenceId);

internal interface IEventStreamNotificationAccess
{
    uint AcceptedWatermark { get; }
    void RestoreAcceptedWatermark(uint sequence);
    Task<EventBufferObservation> ReadBeforeAsync(CancellationToken token);
    bool IsReleasePending(EventBufferObservation value);
    Task WaitForObservationAsync(TimeSpan fallback, CancellationToken token);
    void DiscardCachedObservation();
}

internal interface IEventStreamAccess : IAsyncDisposable
{
    int ArrayLength { get; }
    Task PrepareAsync(EventStreamSettings settings, CancellationToken token);
    Task<EventBufferObservation> ReadMetadataAsync(CancellationToken token);
    Task<IReadOnlyList<UnitTestEventEntry>> ReadEventsAsync(EventBufferFifo fifo, int maxEvents, CancellationToken token);
    Task<uint> ReadDeleteSequenceAsync(CancellationToken token);
    Task WriteWatermarkAsync(uint sequenceId, CancellationToken token);
    Task WriteResetAsync(CancellationToken token);
}
