using System.Buffers.Binary;
using PLCSIM.UnitTest.Common.UnitTests;
using TwinCAT.Ads;
using TwinCAT.Ads.SumCommand;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed partial class AdsEventStreamAccess : IEventStreamAccess, IEventStreamNotificationAccess
{
    private readonly AdsClient client;
    private readonly TimeProvider clock;
    private EventStreamLayout? layout;
    private SumRead? metadataRead;
    private uint? deleteHandle;
    private uint? resetHandle;
    private int symbolVersion = -1;
    private int invalidated;
    private bool subscribed, disposed;

    internal AdsEventStreamAccess(AdsClientFactory factory, string netId, int port, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        client = factory.CreateClient();
        try
        {
            client.Timeout = 3000;
            client.Connect(netId, port);
            client.AdsNotificationsInvalidated += OnNotificationsInvalidated;
        }
        catch { client.Dispose(); throw; }
    }

    public int ArrayLength => Layout.ArrayLength;
    private EventStreamLayout Layout => layout ?? throw new InvalidOperationException("EventStream access is not prepared.");

    public async Task PrepareAsync(EventStreamSettings settings, CancellationToken token)
    {
        settings.Validate();
        if (symbolVersion >= 0) throw new InvalidOperationException("EventStream access is already bound.");
        symbolVersion = await ReadSymbolVersionAsync(token).ConfigureAwait(false);
        var registration = await client.RegisterSymbolVersionChangedAsync(OnSymbolVersionChanged, token).ConfigureAwait(false);
        Check(registration.ErrorCode, "Register EventStream symbol-version notification");
        subscribed = true;
        var candidate = await EventStreamLayout.ResolveAsync(client, settings.EventRoot, token).ConfigureAwait(false);
        _ = candidate.DecodeContract(await ReadBytesAsync(candidate.Contract.Symbol, token).ConfigureAwait(false));
        foreach (var field in new[] { "ContractVersion", "FrameworkVersion" })
        {
            var symbol = await DescribeAsync(settings.RunControlRoot + ".ContractInfo." + field, token).ConfigureAwait(false);
            EventStreamStructureLayout.RequireType(symbol, AdsDataTypeId.ADST_STRING, 11);
            _ = RunControlLayout.DecodeVersion(await ReadBytesAsync(symbol, token).ConfigureAwait(false), field == "ContractVersion");
        }
        var commands = await DescribeAsync(settings.RunControlRoot + ".ControlData.BufferCmd", token).ConfigureAwait(false);
        var delete = await DescribeAsync(commands.Path + ".DeleteSequenceId", token).ConfigureAwait(false);
        var reset = await DescribeAsync(commands.Path + ".Reset", token).ConfigureAwait(false);
        _ = new EventStreamStructureLayout(commands,
            [new("DeleteSequenceId", AdsDataTypeId.ADST_UINT32, 4), new("Reset", AdsDataTypeId.ADST_BIT, 1)], [delete, reset]);
        deleteHandle = await BindCommandAsync(delete.Path, token).ConfigureAwait(false);
        resetHandle = await BindCommandAsync(reset.Path, token).ConfigureAwait(false);
        var candidateRead = SumRead.Create(client)
            .AddEntry(candidate.Fifo.Symbol.Group, candidate.Fifo.Symbol.Offset, candidate.Fifo.Symbol.Size)
            .AddEntry(candidate.Status.Symbol.Group, candidate.Status.Symbol.Offset, candidate.Status.Symbol.Size)
            .AddEntry(delete.Group, delete.Offset, delete.Size)
            .WithFallbackMode(SumFallbackMode.Sum).Build();
        if (await ReadSymbolVersionAsync(token).ConfigureAwait(false) != symbolVersion) Interlocked.Exchange(ref invalidated, 1);
        EnsureValid(token);
        layout = candidate;
        metadataRead = candidateRead;
        if (settings.Strategy == ObservationStrategy.Subscription)
            await PrepareNotificationsAsync(settings, delete, token).ConfigureAwait(false);
    }

    public async Task<EventBufferObservation> ReadMetadataAsync(CancellationToken token)
    {
        EnsureValid(token);
        var revision = notifications?.BeginRead() ?? 0;
        var read = metadataRead ?? throw new InvalidOperationException("EventStream access is not prepared.");
        var result = await read.ReadRawAsync(token).ConfigureAwait(false);
        EnsureValid(token);
        var value = DecodeMetadata(Layout, result.ErrorCode, result.SubErrors, result.ReadBlocks);
        notifications?.Confirm(value, revision);
        return value;
    }

    internal static EventBufferObservation DecodeMetadata(EventStreamLayout layout, AdsErrorCode error,
        AdsErrorCode[]? errors, IList<ReadOnlyMemory<byte>>? blocks)
    {
        Check(error, "Read EventBuffer metadata");
        if (errors is not { Length: 3 } || blocks is not { Count: 3 })
            throw new InvalidDataException("Incomplete EventBuffer metadata Sum Read response.");
        Check(errors[0], "Read EventBuffer FIFO");
        Check(errors[1], "Read EventBuffer status");
        Check(errors[2], "Read EventBuffer deletion command");
        if (blocks[2].Length != 4)
            throw new InvalidDataException("Incomplete EventBuffer deletion-command read.");
        // Sum Read reduces roundtrips; it does not replace the drain's consistency checks.
        return new(layout.DecodeFifo(blocks[0].Span), layout.DecodeStatus(blocks[1].Span),
            BinaryPrimitives.ReadUInt32LittleEndian(blocks[2].Span));
    }

    public async Task<IReadOnlyList<UnitTestEventEntry>> ReadEventsAsync(EventBufferFifo fifo, int maxEvents, CancellationToken token)
    {
        EnsureValid(token);
        var entries = new List<UnitTestEventEntry>();
        foreach (var range in Layout.GetReadRanges(fifo, maxEvents))
        {
            var bytes = await ReadBytesAsync(range.Group, range.Offset, range.ByteCount, token).ConfigureAwait(false);
            entries.AddRange(Layout.DecodeRange(bytes, range.EventCount));
        }
        EnsureValid(token);
        return entries.AsReadOnly();
    }

    public async Task<uint> ReadDeleteSequenceAsync(CancellationToken token)
    {
        EnsureValid(token);
        var result = await client.ReadAsync(DeleteHandle, 4, token).ConfigureAwait(false);
        EnsureValid(token);
        Check(result.ErrorCode, "Read EventBuffer deletion command");
        if (result.Data.Length != 4) throw new InvalidDataException("Incomplete EventBuffer deletion-command read.");
        return BinaryPrimitives.ReadUInt32LittleEndian(result.Data.Span);
    }

    public async Task WriteWatermarkAsync(uint sequenceId, CancellationToken token)
    {
        EnsureValid(token);
        if (sequenceId == 0) throw new ArgumentOutOfRangeException(nameof(sequenceId));
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, sequenceId);
        var result = await client.WriteAsync(DeleteHandle, data, token).ConfigureAwait(false);
        // Do not add post-write cancellation or symbol checks: success must reach the batch publisher.
        Check(result.ErrorCode, "Write EventBuffer deletion watermark " + sequenceId);
        notifications?.Accepted(sequenceId);
    }

    private uint DeleteHandle => deleteHandle ?? throw new InvalidOperationException("EventBuffer command is not bound.");

    public async Task WriteResetAsync(CancellationToken token)
    {
        EnsureValid(token);
        var handle = resetHandle ?? throw new InvalidOperationException("EventBuffer reset command is not bound.");
        var result = await client.WriteAsync(handle, new byte[] { 1 }, token).ConfigureAwait(false);
        // Write acceptance only. No readback, consumer-side clearing, reset wait or command replay.
        Check(result.ErrorCode, "Write EventBuffer reset command");
        notifications?.Discard();
    }

    private async Task<uint> BindCommandAsync(string path, CancellationToken token)
    {
        var result = await client.CreateVariableHandleAsync(path, token).ConfigureAwait(false);
        Check(result.ErrorCode, "Bind " + path);
        return result.Handle;
    }

    private async Task<EventStreamSymbol> DescribeAsync(string path, CancellationToken token)
    {
        var result = await client.ReadSymbolAsync(path, token).ConfigureAwait(false);
        EnsureValid(token);
        Check(result.ErrorCode, "Resolve " + path);
        var symbol = result.Value ?? throw new InvalidDataException("Missing ADS symbol information for " + path);
        return new(path, symbol.IndexGroup, symbol.IndexOffset, symbol.ByteSize, symbol.DataTypeId);
    }

    private Task<byte[]> ReadBytesAsync(EventStreamSymbol symbol, CancellationToken token) =>
        ReadBytesAsync(symbol.Group, symbol.Offset, symbol.Size, token);

    private async Task<byte[]> ReadBytesAsync(uint group, uint offset, int size, CancellationToken token)
    {
        EnsureValid(token);
        var result = await client.ReadAsync(group, offset, size, token).ConfigureAwait(false);
        EnsureValid(token);
        Check(result.ErrorCode, "Read EventBuffer data");
        if (result.Data.Length != size) throw new InvalidDataException("Incomplete EventBuffer data read.");
        return result.Data.ToArray();
    }

    private async Task<byte> ReadSymbolVersionAsync(CancellationToken token)
    {
        var bytes = await ReadBytesAsync((uint)IndexGroupSymbolAccess.SymbolVersion, 0, 1, token).ConfigureAwait(false);
        return bytes[0];
    }

    private void OnSymbolVersionChanged(object? sender, AdsSymbolVersionChangedEventArgs args)
    {
        if (args.SymbolVersion != Volatile.Read(ref symbolVersion)) InvalidateNotifications();
    }

    private void OnNotificationsInvalidated(object? sender, AdsNotificationsInvalidatedEventArgs args) =>
        InvalidateNotifications();

    private void InvalidateNotifications()
    {
        Interlocked.Exchange(ref invalidated, 1);
        notifications?.Fault(generation);
    }

    private void EnsureValid(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref invalidated) != 0)
            throw new AdsAccessException("EventStream symbols changed", AdsErrorCode.DeviceSymbolVersionInvalid);
    }

    private static void Check(AdsErrorCode error, string operation)
    {
        if (error != AdsErrorCode.NoError) throw new AdsAccessException(operation, error);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        notifications?.Close();
        client.AdsNotification -= OnNotification;
        client.AdsNotificationsInvalidated -= OnNotificationsInvalidated;
        Exception? failure = null;
        try
        {
            foreach (var handle in notificationHandles)
            {
                try
                {
                    var result = await client.DeleteDeviceNotificationAsync(handle, CancellationToken.None).ConfigureAwait(false);
                    CheckCleanup(result.ErrorCode, "Release EventBuffer notification");
                }
                catch (Exception error) { failure ??= error; }
            }
            if (subscribed)
            {
                try
                {
                    var result = await client.UnregisterSymbolVersionChangedAsync(OnSymbolVersionChanged, CancellationToken.None).ConfigureAwait(false);
                    CheckCleanup(result.ErrorCode, "Release EventStream symbol-version notification");
                }
                catch (Exception error) { failure = error; }
            }
            foreach (var handle in new[] { deleteHandle, resetHandle }.OfType<uint>())
            {
                try
                {
                    var result = await client.DeleteVariableHandleAsync(handle, CancellationToken.None).ConfigureAwait(false);
                    CheckCleanup(result.ErrorCode, "Release EventBuffer command handle");
                }
                catch (Exception error) { failure ??= error; }
            }
        }
        finally { client.Dispose(); }
        if (failure is not null) throw new IOException("EventStream ADS cleanup failed: " + failure.Message, failure);
    }

    private static void CheckCleanup(AdsErrorCode error, string operation)
    {
        if (error is AdsErrorCode.DeviceSymbolVersionInvalid or AdsErrorCode.DeviceNotifyHandleInvalid or AdsErrorCode.TargetPortNotFound) return;
        Check(error, operation);
    }
}
