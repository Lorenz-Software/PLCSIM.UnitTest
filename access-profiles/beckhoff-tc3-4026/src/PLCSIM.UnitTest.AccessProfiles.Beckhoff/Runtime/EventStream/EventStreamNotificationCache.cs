using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

// Notification data is only a capture candidate. Fresh reads remain authoritative
// for deletion commands, prefix validation and final empty evidence.
internal sealed class EventStreamNotificationCache(long generation, TimeProvider clock)
{
    private readonly object gate = new();
    private TaskCompletionSource signal = NewSignal();
    private EventBufferObservation? latest, previousNotification, confirmed;
    private uint accepted;
    private long revision, observedRevision;
    private bool faulted, closed, commandChanged;
    private uint? previousCommand;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Observe(long sourceGeneration, EventBufferFifo fifo, EventBufferStatus status)
    {
        lock (gate)
        {
            if (closed || sourceGeneration != generation) return;
            var value = new EventBufferObservation(fifo, status, confirmed?.DeleteSequenceId ?? 0);
            if (previousNotification is { } previous && Regresses(value, previous)) faulted = true;
            // Preserve an observed zero phase even if it is the first callback after bootstrap.
            if (fifo.BufferEmpty && status.ProducerSequenceId == 0 && status.DeletedSequenceId == 0
                && confirmed is { } read && read.Status.ProducerSequenceId != 0) faulted = true;
            previousNotification = value;
            if (latest is not { } current || !Regresses(value, current))
            {
                if (latest?.Fifo != fifo || latest?.Status != status) revision++;
                latest = value;
            }
            Wake();
        }
    }

    internal void ObserveCommand(long sourceGeneration, uint value)
    {
        lock (gate)
        {
            if (closed || sourceGeneration != generation) return;
            if (previousCommand != value) { commandChanged = true; revision++; }
            previousCommand = value;
            Wake();
        }
    }

    internal void Fault(long sourceGeneration)
    {
        lock (gate)
        {
            if (closed || sourceGeneration != generation) return;
            faulted = true;
            Wake();
        }
    }

    internal long BeginRead()
    {
        lock (gate) { EnsureValid(); return revision; }
    }

    internal void Confirm(EventBufferObservation value, long startedAtRevision)
    {
        lock (gate)
        {
            EnsureValid();
            // Only a real reset observation clears the locally accepted watermark.
            if (value.Fifo.BufferEmpty && value.Status.ProducerSequenceId == 0
                && value.Status.DeletedSequenceId == 0 && value.Status.LostEventCount == 0 && value.DeleteSequenceId == 0)
                accepted = 0;
            confirmed = value;
            if (revision == startedAtRevision || latest is null || Regresses(latest, value)) latest = value;
            observedRevision = startedAtRevision;
            if (revision == startedAtRevision) commandChanged = false;
        }
    }

    internal EventBufferObservation? Candidate()
    {
        lock (gate)
        {
            EnsureValid();
            if (confirmed is null || confirmed.DeleteSequenceId != 0 || commandChanged
                || latest is not { } value || value.Fifo.BufferEmpty || value.Status.DeletedSequenceId < accepted)
                return null;
            return value with { DeleteSequenceId = confirmed.DeleteSequenceId };
        }
    }

    internal void Accepted(uint sequence)
    {
        lock (gate) { accepted = sequence; }
    }

    internal uint AcceptedWatermark { get { lock (gate) return accepted; } }

    internal void RestoreAcceptedWatermark(uint sequence)
    {
        lock (gate)
        {
            // A fresh bootstrap reset supersedes the watermark from the old ADS binding.
            if (confirmed is { } value && value.Fifo.BufferEmpty && value.DeleteSequenceId == 0
                && value.Status.ProducerSequenceId == 0 && value.Status.DeletedSequenceId == 0
                && value.Status.LostEventCount == 0) return;
            accepted = sequence;
        }
    }

    internal bool IsReleasePending(EventBufferObservation value)
    {
        lock (gate) return value.Status.DeletedSequenceId < accepted;
    }

    internal void Discard()
    {
        lock (gate) { latest = null; confirmed = null; observedRevision = revision; commandChanged = false; }
    }

    internal async Task WaitAsync(TimeSpan fallback, CancellationToken token)
    {
        var started = clock.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task pending;
            lock (gate)
            {
                if (closed || faulted || commandChanged) return;
                if (latest is { } value && value.Status.DeletedSequenceId >= accepted
                    && (revision > observedRevision || (!value.Fifo.BufferEmpty && confirmed?.DeleteSequenceId == 0))) return;
                pending = signal.Task;
            }
            var remaining = fallback - clock.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return;
            try { await pending.WaitAsync(remaining, clock, token).ConfigureAwait(false); }
            catch (TimeoutException) { return; }
        }
    }

    internal void Close()
    {
        lock (gate) { closed = true; latest = confirmed = previousNotification = null; Wake(); }
    }

    private void EnsureValid()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (faulted) throw new AdsAccessException("EventBuffer notification data requires revalidation", AdsErrorCode.DeviceSymbolVersionInvalid);
    }

    private void Wake()
    {
        var old = signal;
        signal = NewSignal();
        old.TrySetResult();
    }

    private static bool Regresses(EventBufferObservation next, EventBufferObservation previous) =>
        next.Status.ProducerSequenceId < previous.Status.ProducerSequenceId
        || next.Status.DeletedSequenceId < previous.Status.DeletedSequenceId
        || next.Status.LostEventCount < previous.Status.LostEventCount;
}
