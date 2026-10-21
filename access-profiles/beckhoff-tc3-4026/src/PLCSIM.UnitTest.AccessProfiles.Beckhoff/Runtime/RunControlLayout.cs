using System.Buffers.Binary;
using System.Text;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed record RunControlSymbol(string Path, uint Group, uint Offset, int Size, AdsDataTypeId Type);

internal sealed class RunControlLayout
{
    internal static readonly string[] Fields = ["State", "ExecutionStatus", "Result", "Uninitialized",
        "ConfigError", "Ready", "Running", "Completed", "Aborted", "Inactive", "Error"];
    private readonly int[] offsets;
    private readonly int size;

    internal RunControlLayout(RunControlSymbol execution, IReadOnlyList<RunControlSymbol> fields)
    {
        if (execution.Type != AdsDataTypeId.ADST_BIGTYPE || execution.Size is < 14 or > 4096 || fields.Count != Fields.Length)
            throw new InvalidDataException("Invalid RunControl execution structure.");
        size = execution.Size;
        offsets = new int[Fields.Length];
        var occupied = new HashSet<int>();
        for (var index = 0; index < Fields.Length; index++)
        {
            var field = fields[index];
            RequireType(field, index < 3 ? AdsDataTypeId.ADST_INT16 : AdsDataTypeId.ADST_BIT, index < 3 ? 2 : 1);
            if (field.Path != execution.Path + "." + Fields[index])
                throw new InvalidDataException("Unexpected RunControl execution field.");
            offsets[index] = RelativeOffset(execution, field);
            for (var position = offsets[index]; position < offsets[index] + field.Size; position++)
                if (!occupied.Add(position)) throw new InvalidDataException("Overlapping RunControl execution fields.");
        }
    }

    internal UnitTestRunControlObservation Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != size) throw new InvalidDataException("Incomplete RunControl execution read.");
        var signals = UnitTestExecutionSignals.None;
        for (var index = 3; index < Fields.Length; index++)
        {
            var value = data[offsets[index]];
            if (value > 1) throw new InvalidDataException($"Invalid BOOL in RunControl {Fields[index]}.");
            if (value == 1) signals |= (UnitTestExecutionSignals)(1 << (index - 3));
        }
        // Preserve PLC values, including custom lifecycle states; do not infer results from flags.
        return new((UnitTestExecutionStatus)BinaryPrimitives.ReadInt16LittleEndian(data[offsets[1]..]), signals,
            (UnitTestLifecycleState)BinaryPrimitives.ReadInt16LittleEndian(data[offsets[0]..]),
            (UnitTestResultState)BinaryPrimitives.ReadInt16LittleEndian(data[offsets[2]..]));
    }

    internal static int RelativeOffset(RunControlSymbol parent, RunControlSymbol child)
    {
        var offset = (long)child.Offset - parent.Offset;
        if (child.Group != parent.Group || offset < 0 || offset + child.Size > parent.Size)
            throw new InvalidDataException($"'{child.Path}' lies outside '{parent.Path}'.");
        return checked((int)offset);
    }

    internal static void RequireType(RunControlSymbol symbol, AdsDataTypeId type, int size)
    {
        if (symbol.Type != type || symbol.Size != size)
            throw new InvalidDataException($"'{symbol.Path}' must have ADS type {type} and size {size}.");
    }

    internal static string DecodeVersion(ReadOnlySpan<byte> data, bool contract)
    {
        var end = data.IndexOf((byte)0);
        if (data.Length != 11 || end < 0) throw new InvalidDataException("Expected a terminated STRING(10) version.");
        var text = Encoding.ASCII.GetString(data[..end]);
        var parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Any(character => character is < '0' or > '9'))
            || contract && text != "1.0.0")
            throw new InvalidDataException($"Unsupported or invalid RunControl {(contract ? "contract" : "framework")} version '{text}'.");
        return text;
    }
}
