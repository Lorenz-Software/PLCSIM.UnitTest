using System.Buffers.Binary;
using System.Text;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using TwinCAT.Ads;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

internal sealed class EventStreamFixture
{
    internal const string Root = "Test.EventStream";
    internal const string EventPath = Root + ".Buffer[1]";
    internal Dictionary<string, EventStreamSymbol> Symbols { get; } = new(StringComparer.Ordinal);
    internal List<string> Requests { get; } = [];

    internal EventStreamFixture(int length = 256, bool alternateLayout = false)
    {
        AddStructure(Root + ".ContractInfo", 100, EventStreamLayout.ContractFields, alternateLayout);
        AddStructure(Root + ".FifoControl", 200, EventStreamLayout.FifoFields, alternateLayout);
        AddStructure(Root + ".Status", 300, EventStreamLayout.StatusFields, alternateLayout);
        if (alternateLayout) AddStructure(EventPath, 4096, EventEntryLayout.Fields, true);
        else
        {
            Symbols.Add(EventPath, new(EventPath, 0x4020, 4096, 400, AdsDataTypeId.ADST_BIGTYPE));
            int[] offsets = [0, 8, 16, 17, 18, 19, 100, 134, 136, 138];
            foreach (var (field, index) in EventEntryLayout.Fields.Select((field, index) => (field, index)))
                Symbols.Add(EventPath + "." + field.Name, new(EventPath + "." + field.Name, 0x4020,
                    4096 + (uint)offsets[index], field.Size, field.Type));
        }
        var entry = Symbols[EventPath];
        Symbols.Add(Root + ".Buffer", new(Root + ".Buffer", entry.Group, entry.Offset, checked(length * entry.Size),
            AdsDataTypeId.ADST_BIGTYPE, new(1, length, entry.Size)));
        var lastPath = Root + ".Buffer[" + length + "]";
        Symbols.Add(lastPath, entry with { Path = lastPath, Offset = entry.Offset + (uint)((length - 1) * entry.Size) });
    }

    private void AddStructure(string path, uint offset, IReadOnlyList<EventStreamField> definitions, bool alternate)
    {
        var position = alternate ? 3 : 0;
        foreach (var field in alternate ? definitions.Reverse() : definitions)
        {
            var member = path + "." + field.Name;
            Symbols.Add(member, new(member, 0x4020, offset + (uint)position, field.Size, field.Type));
            position += field.Size + (alternate ? 3 : 0);
        }
        Symbols.Add(path, new(path, 0x4020, offset, position + (alternate ? 2 : 0), AdsDataTypeId.ADST_BIGTYPE));
    }

    internal Task<EventStreamSymbol> Describe(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Requests.Add(path);
        return Task.FromResult(Symbols[path]);
    }

    internal Task<EventStreamLayout> Resolve(CancellationToken token = default) => EventStreamLayout.ResolveAsync(Root, Describe, token);
    internal byte[] Data(string path) => new byte[Symbols[path].Size];
    internal Span<byte> Field(byte[] data, string parent, string name)
    {
        var member = Symbols[parent + "." + name];
        return data.AsSpan((int)(member.Offset - Symbols[parent].Offset), member.Size);
    }
    internal void Text(byte[] data, string parent, string name, string value)
    {
        var span = Field(data, parent, name);
        span.Clear();
        Encoding.ASCII.GetBytes(value).CopyTo(span);
    }

    internal byte[] Event(uint sequence = 1, long timestamp = 1720000000123456700)
    {
        var data = Data(EventPath);
        BinaryPrimitives.WriteUInt32LittleEndian(Field(data, EventPath, "SequenceId"), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(Field(data, EventPath, "Timestamp"), timestamp);
        Field(data, EventPath, "OwnerScope")[0] = 30;
        Field(data, EventPath, "EventType")[0] = 10;
        Field(data, EventPath, "Severity")[0] = 2;
        BinaryPrimitives.WriteInt16LittleEndian(Field(data, EventPath, "State"), 89);
        BinaryPrimitives.WriteInt16LittleEndian(Field(data, EventPath, "Result"), 10);
        Text(data, EventPath, "Name", "UnitTest.Suite.Case");
        Text(data, EventPath, "ResourceName", "Resource");
        return data;
    }
}
