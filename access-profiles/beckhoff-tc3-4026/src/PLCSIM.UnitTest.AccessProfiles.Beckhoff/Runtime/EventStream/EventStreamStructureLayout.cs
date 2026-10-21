using System.Buffers.Binary;
using System.Text;
using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed record EventArrayShape(int LowerBound, int Length, int ElementSize);
internal sealed record EventStreamSymbol(string Path, uint Group, uint Offset, int Size,
    AdsDataTypeId Type, EventArrayShape? Array = null);
internal sealed record EventStreamField(string Name, AdsDataTypeId Type, int Size);

internal sealed class EventStreamStructureLayout
{
    private readonly Dictionary<string, (int Offset, int Size)> fields = new(StringComparer.Ordinal);

    internal EventStreamStructureLayout(EventStreamSymbol symbol, IReadOnlyList<EventStreamField> expected,
        IReadOnlyList<EventStreamSymbol> members)
    {
        if (symbol.Type != AdsDataTypeId.ADST_BIGTYPE || symbol.Size < expected.Sum(field => field.Size)
            || members.Count != expected.Count || symbol.Array is not null)
            throw new InvalidDataException($"Invalid EventStream structure '{symbol.Path}'.");
        ValidateAddress(symbol);
        Symbol = symbol;
        var occupied = new HashSet<int>();
        foreach (var definition in expected)
        {
            var matches = members.Where(member => member.Path == symbol.Path + "." + definition.Name).ToArray();
            if (matches.Length != 1) throw new InvalidDataException($"Missing or duplicate field '{symbol.Path}.{definition.Name}'.");
            var member = matches[0];
            RequireType(member, definition.Type, definition.Size);
            var offset = RelativeOffset(symbol, member);
            for (var index = offset; index < offset + member.Size; index++)
                if (!occupied.Add(index)) throw new InvalidDataException($"Overlapping EventStream field '{member.Path}'.");
            fields.Add(definition.Name, (offset, member.Size));
        }
    }

    internal EventStreamSymbol Symbol { get; }

    internal void RequireComplete(ReadOnlySpan<byte> data)
    {
        if (data.Length != Symbol.Size) throw new InvalidDataException($"Incomplete read of '{Symbol.Path}'.");
    }

    internal ReadOnlySpan<byte> Field(ReadOnlySpan<byte> data, string name)
    {
        var field = fields[name];
        return data.Slice(field.Offset, field.Size);
    }

    internal ushort UInt16(ReadOnlySpan<byte> data, string name) => BinaryPrimitives.ReadUInt16LittleEndian(Field(data, name));
    internal uint UInt32(ReadOnlySpan<byte> data, string name) => BinaryPrimitives.ReadUInt32LittleEndian(Field(data, name));
    internal short Int16(ReadOnlySpan<byte> data, string name) => BinaryPrimitives.ReadInt16LittleEndian(Field(data, name));

    internal bool Boolean(ReadOnlySpan<byte> data, string name) => Field(data, name)[0] switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException($"Invalid BOOL in '{Symbol.Path}.{name}'.")
    };

    internal string Text(ReadOnlySpan<byte> data, string name)
    {
        var bytes = Field(data, name);
        var end = bytes.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException($"Unterminated STRING in '{Symbol.Path}.{name}'.");
        foreach (var value in bytes[..end])
            if (value > 127) throw new InvalidDataException($"Non-ASCII text in '{Symbol.Path}.{name}'.");
        return Encoding.ASCII.GetString(bytes[..end]);
    }

    internal static void RequireType(EventStreamSymbol symbol, AdsDataTypeId type, int size)
    {
        ValidateAddress(symbol);
        if (symbol.Type != type || symbol.Size != size || symbol.Array is not null)
            throw new InvalidDataException($"'{symbol.Path}' must have ADS type {type} and size {size}.");
    }

    internal static int RelativeOffset(EventStreamSymbol parent, EventStreamSymbol child)
    {
        ValidateAddress(parent);
        ValidateAddress(child);
        var offset = (long)child.Offset - parent.Offset;
        if (child.Group != parent.Group || offset < 0 || offset + child.Size > parent.Size)
            throw new InvalidDataException($"'{child.Path}' lies outside '{parent.Path}'.");
        return checked((int)offset);
    }

    internal static void ValidateAddress(EventStreamSymbol symbol)
    {
        if (symbol.Size <= 0 || (ulong)symbol.Offset + (ulong)symbol.Size > (ulong)uint.MaxValue + 1)
            throw new InvalidDataException($"Invalid ADS address/size for '{symbol.Path}'.");
    }
}
