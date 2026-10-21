using System.Globalization;
using PLCSIM.UnitTest.Common.UnitTests;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed record EventStreamContractInfo(string ContractVersion, string FrameworkVersion);
internal sealed record EventBufferReadRange(uint Group, uint Offset, int ByteCount, int EventCount);

internal sealed class EventStreamLayout
{
    internal static readonly EventStreamField[] ContractFields =
    [new("ContractVersion", AdsDataTypeId.ADST_STRING, 11), new("FrameworkVersion", AdsDataTypeId.ADST_STRING, 11)];
    internal static readonly EventStreamField[] FifoFields =
    [
        new("IndexOut", AdsDataTypeId.ADST_UINT16, 2), new("IndexIn", AdsDataTypeId.ADST_UINT16, 2),
        new("BufferEmpty", AdsDataTypeId.ADST_BIT, 1), new("BufferFull", AdsDataTypeId.ADST_BIT, 1),
        new("BufferAlmostFull", AdsDataTypeId.ADST_BIT, 1), new("ElementCount", AdsDataTypeId.ADST_UINT16, 2),
        new("BufferLength", AdsDataTypeId.ADST_UINT16, 2), new("ConfigError", AdsDataTypeId.ADST_BIT, 1),
        new("IndexError", AdsDataTypeId.ADST_BIT, 1)
    ];
    internal static readonly EventStreamField[] StatusFields =
    [new("SequenceId", AdsDataTypeId.ADST_UINT32, 4), new("DeletedSequenceId", AdsDataTypeId.ADST_UINT32, 4),
        new("LostEventCount", AdsDataTypeId.ADST_UINT16, 2)];

    private EventStreamLayout(EventStreamStructureLayout contract, EventStreamStructureLayout fifo,
        EventStreamStructureLayout status, EventStreamSymbol buffer, EventEntryLayout entry, EventStreamSymbol last)
    {
        Contract = contract;
        Fifo = fifo;
        Status = status;
        Buffer = buffer;
        Entry = entry;
        var shape = buffer.Array;
        EventStreamStructureLayout.ValidateAddress(buffer);
        if (buffer.Type != AdsDataTypeId.ADST_BIGTYPE || shape is null || shape.LowerBound != 1
            || shape.Length is < 2 or > ushort.MaxValue || shape.ElementSize != entry.Symbol.Size
            || (long)shape.Length * shape.ElementSize != buffer.Size
            || EventStreamStructureLayout.RelativeOffset(buffer, entry.Symbol) != 0
            || EventStreamStructureLayout.RelativeOffset(buffer, last) != (long)(shape.Length - 1) * shape.ElementSize
            || last.Type != entry.Symbol.Type || last.Size != entry.Symbol.Size || last.Array is not null)
            throw new InvalidDataException("EventBuffer must be a contiguous one-dimensional array [1..N] of events.");
        ArrayLength = shape.Length;
    }

    internal EventStreamStructureLayout Contract { get; }
    internal EventStreamStructureLayout Fifo { get; }
    internal EventStreamStructureLayout Status { get; }
    internal EventStreamSymbol Buffer { get; }
    internal EventEntryLayout Entry { get; }
    internal int ArrayLength { get; }

    internal static Task<EventStreamLayout> ResolveAsync(AdsClient client, string root, CancellationToken token) =>
        ResolveAsync(root, async (path, cancellation) =>
        {
            var result = await client.ReadSymbolAsync(path, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (result.ErrorCode != AdsErrorCode.NoError) throw new AdsAccessException("Resolve " + path, result.ErrorCode);
            var symbol = result.Value ?? throw new InvalidDataException("Missing ADS symbol information for " + path);
            EventArrayShape? shape = null;
            if (symbol.DataType is IArrayType array)
            {
                if (array.Dimensions.Count != 1 || array.IsJagged || array.ElementType is null)
                    throw new InvalidDataException("Unsupported EventBuffer array dimensions.");
                var dimension = array.Dimensions[0];
                shape = new(dimension.LowerBound, dimension.ElementCount, array.ElementType.ByteSize);
            }
            return new(path, symbol.IndexGroup, symbol.IndexOffset, symbol.ByteSize, symbol.DataTypeId, shape);
        }, token);

    internal static async Task<EventStreamLayout> ResolveAsync(string root,
        Func<string, CancellationToken, Task<EventStreamSymbol>> describe, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        async Task<EventStreamSymbol> Read(string path)
        {
            token.ThrowIfCancellationRequested();
            var symbol = await describe(path, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (symbol.Path != path) throw new InvalidDataException("Unexpected ADS symbol path for " + path);
            return symbol;
        }
        async Task<EventStreamStructureLayout> Structure(string path, EventStreamField[] definitions)
        {
            var parent = await Read(path).ConfigureAwait(false);
            var members = new List<EventStreamSymbol>();
            foreach (var field in definitions) members.Add(await Read(path + "." + field.Name).ConfigureAwait(false));
            return new(parent, definitions, members);
        }
        var contract = await Structure(root + ".ContractInfo", ContractFields).ConfigureAwait(false);
        var fifo = await Structure(root + ".FifoControl", FifoFields).ConfigureAwait(false);
        var status = await Structure(root + ".Status", StatusFields).ConfigureAwait(false);
        var buffer = await Read(root + ".Buffer").ConfigureAwait(false);
        if (buffer.Array is not { LowerBound: 1, Length: >= 2 and <= ushort.MaxValue } shape)
            throw new InvalidDataException("EventBuffer must declare array bounds [1..N].");
        var first = await Read(buffer.Path + "[1]").ConfigureAwait(false);
        var last = await Read(buffer.Path + "[" + shape.Length.ToString(CultureInfo.InvariantCulture) + "]").ConfigureAwait(false);
        var fields = new List<EventStreamSymbol>();
        foreach (var field in EventEntryLayout.Fields) fields.Add(await Read(first.Path + "." + field.Name).ConfigureAwait(false));
        return new(contract, fifo, status, buffer, new(first, fields), last);
    }

    internal EventStreamContractInfo DecodeContract(ReadOnlySpan<byte> data)
    {
        Contract.RequireComplete(data);
        var contract = Contract.Text(data, "ContractVersion");
        var framework = Contract.Text(data, "FrameworkVersion");
        if (contract != "1.0.0") throw new InvalidDataException($"Unsupported EventStream contract version '{contract}'.");
        var parts = framework.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Any(character => character is < '0' or > '9')))
            throw new InvalidDataException($"Invalid EventStream framework version '{framework}'.");
        return new(contract, framework);
    }

    internal EventBufferFifo DecodeFifo(ReadOnlySpan<byte> data)
    {
        Fifo.RequireComplete(data);
        return new(Fifo.UInt16(data, "IndexOut"), Fifo.UInt16(data, "IndexIn"), Fifo.Boolean(data, "BufferEmpty"),
            Fifo.Boolean(data, "BufferFull"), Fifo.Boolean(data, "BufferAlmostFull"), Fifo.UInt16(data, "ElementCount"),
            Fifo.UInt16(data, "BufferLength"), Fifo.Boolean(data, "ConfigError"), Fifo.Boolean(data, "IndexError"));
    }

    internal EventBufferStatus DecodeStatus(ReadOnlySpan<byte> data)
    {
        Status.RequireComplete(data);
        return new(Status.UInt32(data, "SequenceId"), Status.UInt32(data, "DeletedSequenceId"), Status.UInt16(data, "LostEventCount"));
    }

    internal IReadOnlyList<EventBufferReadRange> GetReadRanges(EventBufferFifo fifo, int maxEvents) =>
        fifo.GetReadablePrefix(maxEvents, ArrayLength).Select(range => new EventBufferReadRange(Buffer.Group,
            checked(Buffer.Offset + (uint)((range.FirstIndex - 1) * Entry.Symbol.Size)),
            checked(range.Count * Entry.Symbol.Size), range.Count)).ToArray();

    internal IReadOnlyList<UnitTestEventEntry> DecodeRange(ReadOnlySpan<byte> data, int count)
    {
        if (count < 0 || count >= ArrayLength) throw new ArgumentOutOfRangeException(nameof(count));
        if (data.Length != (long)count * Entry.Symbol.Size) throw new InvalidDataException("Incomplete EventBuffer range read.");
        var result = new UnitTestEventEntry[count];
        for (var index = 0; index < count; index++) result[index] = Entry.Decode(data.Slice(index * Entry.Symbol.Size, Entry.Symbol.Size));
        return result;
    }
}
