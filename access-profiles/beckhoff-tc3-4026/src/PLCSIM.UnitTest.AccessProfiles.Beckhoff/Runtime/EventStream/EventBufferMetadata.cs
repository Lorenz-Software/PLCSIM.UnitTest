namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed record EventBufferStatus(uint ProducerSequenceId, uint DeletedSequenceId, ushort LostEventCount);
internal sealed record EventBufferRange(int FirstIndex, int Count);

internal sealed record EventBufferFifo(ushort IndexOut, ushort IndexIn, bool BufferEmpty, bool BufferFull,
    bool BufferAlmostFull, ushort ElementCount, ushort BufferLength, bool ConfigError, bool IndexError)
{
    internal IReadOnlyList<EventBufferRange> GetReadablePrefix(int maxEvents, int declaredArrayLength)
    {
        if (maxEvents <= 0) throw new ArgumentOutOfRangeException(nameof(maxEvents));
        if (ConfigError || IndexError || BufferLength < 2 || BufferLength > declaredArrayLength
            || IndexOut < 1 || IndexOut > BufferLength || IndexIn < 1 || IndexIn > BufferLength)
            throw new InvalidDataException("EventBuffer FIFO is not a valid readable range.");
        var count = (IndexIn - IndexOut + BufferLength) % BufferLength;
        if (ElementCount != count || BufferEmpty != (count == 0) || BufferFull != (count == BufferLength - 1))
            throw new InvalidDataException("EventBuffer FIFO indices, count and flags disagree.");
        if (count == 0) return Array.Empty<EventBufferRange>();
        var selected = Math.Min(count, maxEvents);
        var first = Math.Min(selected, BufferLength - IndexOut + 1);
        return selected == first
            ? [new(IndexOut, first)]
            : [new(IndexOut, first), new(1, selected - first)];
    }
}
