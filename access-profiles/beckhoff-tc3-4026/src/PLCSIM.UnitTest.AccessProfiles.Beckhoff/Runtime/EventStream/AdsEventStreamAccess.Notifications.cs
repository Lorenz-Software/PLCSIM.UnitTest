using System.Buffers.Binary;
using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed partial class AdsEventStreamAccess
{
    private sealed record NotificationSource(long Generation, bool Metadata);
    private static long generations;
    private readonly long generation = Interlocked.Increment(ref generations);
    private readonly List<uint> notificationHandles = new();
    private EventStreamNotificationCache? notifications;

    private async Task PrepareNotificationsAsync(EventStreamSettings settings, EventStreamSymbol delete, CancellationToken token)
    {
        var span = GetNotificationSpan(Layout);
        notifications = new(generation, clock);
        client.AdsNotification += OnNotification;
        var options = new NotificationSettings(AdsTransMode.OnChange, checked((int)settings.MonitoringInterval.TotalMilliseconds), 0);
        foreach (var item in new[] { (span.Group, span.Offset, span.Size, true), (delete.Group, delete.Offset, delete.Size, false) })
        {
            var result = await client.AddDeviceNotificationAsync(item.Group, item.Offset, item.Size,
                options, new NotificationSource(generation, item.Item4), token).ConfigureAwait(false);
            Check(result.ErrorCode, "Register EventBuffer notification");
            notificationHandles.Add(result.Handle);
        }
        // Bootstrap after both registrations. Callbacks alone cannot prove an empty buffer.
        await ReadMetadataAsync(token).ConfigureAwait(false);
    }

    internal static (uint Group, uint Offset, int Size) GetNotificationSpan(EventStreamLayout layout)
    {
        var fifo = layout.Fifo.Symbol;
        var status = layout.Status.Symbol;
        if (fifo.Group != status.Group || (ulong)fifo.Offset + (uint)fifo.Size != status.Offset)
            throw new InvalidDataException("EventBuffer subscription requires contiguous FIFO and status metadata in one ADS index group.");
        return (fifo.Group, fifo.Offset, checked(fifo.Size + status.Size));
    }

    private void OnNotification(object? sender, AdsNotificationEventArgs args)
    {
        if (args.UserData is not NotificationSource source || source.Generation != generation || notifications is not { } cache)
            return;
        try
        {
            if (source.Metadata)
            {
                var split = Layout.Fifo.Symbol.Size;
                if (args.Data.Length != split + Layout.Status.Symbol.Size)
                    throw new InvalidDataException("Incomplete EventBuffer notification.");
                var fifo = Layout.DecodeFifo(args.Data.Span[..split]);
                var status = Layout.DecodeStatus(args.Data.Span[split..]);
                _ = fifo.GetReadablePrefix(1, ArrayLength);
                cache.Observe(source.Generation, fifo, status);
            }
            else
            {
                if (args.Data.Length != 4) throw new InvalidDataException("Incomplete deletion-command notification.");
                // Wakeup only: command zero does not acknowledge a published prefix.
                cache.ObserveCommand(source.Generation, BinaryPrimitives.ReadUInt32LittleEndian(args.Data.Span));
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        {
            cache.Fault(source.Generation);
        }
    }

    public Task<EventBufferObservation> ReadBeforeAsync(CancellationToken token)
    {
        EnsureValid(token);
        return notifications?.Candidate() is { } value ? Task.FromResult(value) : ReadMetadataAsync(token);
    }

    public bool IsReleasePending(EventBufferObservation value) => notifications?.IsReleasePending(value) ?? false;
    public uint AcceptedWatermark => notifications?.AcceptedWatermark ?? 0;
    public void RestoreAcceptedWatermark(uint sequence) => notifications?.RestoreAcceptedWatermark(sequence);

    public Task WaitForObservationAsync(TimeSpan fallback, CancellationToken token) =>
        notifications?.WaitAsync(fallback, token) ?? Task.Delay(fallback, token);

    public void DiscardCachedObservation() => notifications?.Discard();
}
