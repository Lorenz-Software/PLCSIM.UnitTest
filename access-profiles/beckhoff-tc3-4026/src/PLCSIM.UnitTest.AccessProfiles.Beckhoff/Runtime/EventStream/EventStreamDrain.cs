using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

// One bounded attempt. The service owns serialization, scheduling and publication.
internal sealed class EventStreamDrain(CommunicationSessionId sessionId, int maxEvents)
{
    private EventBufferObservation? lastEmptyPublication;
    internal EventStreamState State { get; private set; } = new(sessionId, false,
        EventBufferReadState.NotObserved, EventStreamBatchReleaseState.Idle, false, 0);
    internal bool NeedsDrain { get; private set; }
    internal bool WritingWatermark { get; private set; }
    internal long MetadataRevision { get; private set; }

    internal async Task<UnitTestEventBatch?> CaptureAsync(IEventStreamAccess access, Action requireContext, CancellationToken token)
    {
        WritingWatermark = false;
        requireContext();
        var before = await ObserveAsync(access, requireContext, token, true).ConfigureAwait(false);
        if (before.DeleteSequenceId != 0) return null;
        if (access is IEventStreamNotificationAccess notifications && notifications.IsReleasePending(before))
        {
            NeedsDrain = true;
            return null;
        }

        var count = Math.Min(before.Fifo.ElementCount, maxEvents);
        IReadOnlyList<UnitTestEventEntry> events = count == 0 ? Array.Empty<UnitTestEventEntry>()
            : await access.ReadEventsAsync(before.Fifo, maxEvents, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        requireContext();
        var after = await ObserveAsync(access, requireContext, token).ConfigureAwait(false);
        if (!IsStablePrefix(before, after))
            throw new InvalidDataException("EventBuffer changed while capturing its readable prefix; a fresh observation is required.");
        if (events.Count != count) throw new InvalidDataException("Incomplete EventBuffer prefix.");
        var previous = before.Status.DeletedSequenceId;
        foreach (var entry in events)
        {
            if (entry.SequenceId <= previous || entry.SequenceId > before.Status.ProducerSequenceId)
                throw new InvalidDataException("EventBuffer event sequences disagree with the captured FIFO prefix.");
            previous = entry.SequenceId;
        }

        if (count == 0)
        {
            if (!after.Fifo.BufferEmpty || after.DeleteSequenceId != 0) return null;
            SetReadOutcome(EventBufferReadState.Empty, EventStreamBatchReleaseState.Idle);
            if (lastEmptyPublication == after) return null;
            lastEmptyPublication = after;
            return CreateBatch(Array.Empty<UnitTestEventEntry>(), after, true);
        }

        SetReadOutcome(EventBufferReadState.Valid, EventStreamBatchReleaseState.Idle);
        // Own the immutable payload before committing its deletion. No consumer or epoch acknowledgement is involved.
        var batch = CreateBatch(Array.AsReadOnly(events.ToArray()), after, false);
        if (after.DeleteSequenceId != 0 || await access.ReadDeleteSequenceAsync(token).ConfigureAwait(false) != 0)
        {
            NeedsDrain = true;
            return null;
        }
        token.ThrowIfCancellationRequested();
        requireContext();
        WritingWatermark = true;
        // Once dispatched, let the bounded ADS write finish. Cancellation must not lose an accepted batch.
        await access.WriteWatermarkAsync(previous, CancellationToken.None).ConfigureAwait(false);
        WritingWatermark = false;
        NeedsDrain = true;
        SetReadOutcome(EventBufferReadState.Valid, EventStreamBatchReleaseState.Completed);
        return batch;
    }

    internal void RejectCapture() => SetReadOutcome(EventBufferReadState.Inconsistent, EventStreamBatchReleaseState.Idle);
    internal void InvalidateRead(EventBufferReadState reason = EventBufferReadState.RetryRequired) =>
        State = State with { LastReadState = reason };

    private async Task<EventBufferObservation> ObserveAsync(IEventStreamAccess access, Action requireContext, CancellationToken token, bool before = false)
    {
        var value = before && access is IEventStreamNotificationAccess notifications
            ? await notifications.ReadBeforeAsync(token).ConfigureAwait(false)
            : await access.ReadMetadataAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        requireContext();
        _ = value.Fifo.GetReadablePrefix(maxEvents, access.ArrayLength);
        if (value.Status.DeletedSequenceId > value.Status.ProducerSequenceId)
            throw new InvalidDataException("EventBuffer deleted sequence exceeds the producer sequence.");
        State = State with
        {
            BufferObservedEmpty = value.Fifo.BufferEmpty, BufferFull = value.Fifo.BufferFull,
            ProducerSequenceId = value.Status.ProducerSequenceId, DeletedSequenceId = value.Status.DeletedSequenceId,
            LostEventCount = value.Status.LostEventCount
        };
        MetadataRevision++;
        NeedsDrain = !value.Fifo.BufferEmpty || value.DeleteSequenceId != 0;
        if (!value.Fifo.BufferEmpty) lastEmptyPublication = null;
        return value;
    }

    private void SetReadOutcome(EventBufferReadState read, EventStreamBatchReleaseState release) =>
        State = State with { LastReadState = read, BatchReleaseState = release };

    private static bool IsStablePrefix(EventBufferObservation before, EventBufferObservation after) =>
        before.Fifo.BufferLength == after.Fifo.BufferLength && before.Fifo.IndexOut == after.Fifo.IndexOut
        && before.Status.DeletedSequenceId == after.Status.DeletedSequenceId
        && after.Fifo.ElementCount >= before.Fifo.ElementCount
        && after.Status.ProducerSequenceId >= before.Status.ProducerSequenceId
        && after.Status.LostEventCount >= before.Status.LostEventCount;

    private UnitTestEventBatch CreateBatch(IReadOnlyList<UnitTestEventEntry> events, EventBufferObservation observation, bool empty) =>
        new(sessionId, events, observation.Status.ProducerSequenceId, observation.Status.DeletedSequenceId,
            observation.Status.LostEventCount, empty, empty ? EventBufferReadState.Empty : EventBufferReadState.Valid);
}
