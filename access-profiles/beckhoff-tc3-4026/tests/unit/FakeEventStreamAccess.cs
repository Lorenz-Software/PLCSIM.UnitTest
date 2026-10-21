using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

internal sealed class FakeEventStreamAccess : IEventStreamAccess
{
    public int ArrayLength { get; set; } = 256;
    internal readonly ConcurrentQueue<EventBufferObservation> Metadata = new();
    internal readonly ConcurrentQueue<string> Calls = new();
    internal readonly ConcurrentQueue<uint> Writes = new();
    internal EventBufferObservation Value = Observation();
    internal UnitTestEventEntry[] Payload = [Entry(1), Entry(2), Entry(3)];
    internal EventStreamSettings? Settings;
    internal int Reads, MetadataReads, Prepares, Disposals, Resets;
    internal uint PendingBeforeWrite;
    internal Exception? PrepareError, MetadataError, ReadError, WriteError, DisposeError, ResetError;
    internal Func<CancellationToken, Task>? BeforePrepare, BeforeMetadata, BeforeRead, BeforeReset;
    internal Func<uint, CancellationToken, Task>? BeforeWrite;
    internal Action? BeforePendingCheck;

    public async Task PrepareAsync(EventStreamSettings settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Settings = settings;
        Interlocked.Increment(ref Prepares);
        if (BeforePrepare is not null) await BeforePrepare(token);
        if (PrepareError is not null) throw PrepareError;
    }

    public async Task<EventBufferObservation> ReadMetadataAsync(CancellationToken token)
    {
        Calls.Enqueue("metadata");
        Interlocked.Increment(ref MetadataReads);
        if (BeforeMetadata is not null) await BeforeMetadata(token);
        token.ThrowIfCancellationRequested();
        if (MetadataError is not null) throw MetadataError;
        return Metadata.TryDequeue(out var value) ? value : Value;
    }

    public async Task<IReadOnlyList<UnitTestEventEntry>> ReadEventsAsync(EventBufferFifo fifo, int maxEvents, CancellationToken token)
    {
        Calls.Enqueue("payload");
        Interlocked.Increment(ref Reads);
        if (BeforeRead is not null) await BeforeRead(token);
        token.ThrowIfCancellationRequested();
        if (ReadError is not null) throw ReadError;
        return Payload.Take(Math.Min(maxEvents, fifo.ElementCount)).ToArray();
    }

    public Task<uint> ReadDeleteSequenceAsync(CancellationToken token)
    {
        Calls.Enqueue("pending");
        BeforePendingCheck?.Invoke();
        token.ThrowIfCancellationRequested();
        return Task.FromResult(PendingBeforeWrite);
    }

    public async Task WriteWatermarkAsync(uint sequenceId, CancellationToken token)
    {
        Calls.Enqueue("write");
        Writes.Enqueue(sequenceId);
        if (BeforeWrite is not null) await BeforeWrite(sequenceId, token);
        if (WriteError is not null) throw WriteError;
        Value = Value with { DeleteSequenceId = sequenceId };
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref Disposals);
        if (DisposeError is not null) throw DisposeError;
        return ValueTask.CompletedTask;
    }

    public async Task WriteResetAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Calls.Enqueue("reset");
        Interlocked.Increment(ref Resets);
        if (BeforeReset is not null) await BeforeReset(token);
        if (ResetError is not null) throw ResetError;
    }

    internal static EventBufferObservation Observation(ushort count = 3, uint producer = 3, uint deleted = 0,
        ushort lost = 0, uint pending = 0, ushort indexOut = 1, ushort length = 256) =>
        new(new(indexOut, (ushort)((indexOut - 1 + count) % length + 1), count == 0, count == length - 1,
            false, count, length, false, false), new(producer, deleted, lost), pending);

    internal static UnitTestEventEntry Entry(uint sequence) => new(sequence, DateTimeOffset.UnixEpoch,
        UnitTestOwnerScope.TestCase, UnitTestEventType.Result, LogLevel.Information, "UnitTest.Suite.Case", "",
        UnitTestLifecycleState.Completed, UnitTestResultState.Passed, "");
}
