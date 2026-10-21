using System.Buffers.Binary;
using System.Text;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.UnitTests;
using TwinCAT.Ads;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class RunControlLayoutTests
{
    [Theory]
    [InlineData(-10, -10, -10)]
    [InlineData(10, 10, 0)]
    [InlineData(89, 50, 30)]
    [InlineData(56, 30, 20)]
    public void MapsSignedValuesAndCustomStatesWithoutInferringResults(short state, short status, short result)
    {
        var (root, fields) = Symbols();
        var data = new byte[root.Size];
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2), state);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(6), status);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(10), result);
        data[16] = 1;
        data[20] = 1;
        var actual = new RunControlLayout(root, fields).Decode(data);
        Assert.Equal((UnitTestLifecycleState)state, actual.State);
        Assert.Equal((UnitTestExecutionStatus)status, actual.ExecutionStatus);
        Assert.Equal((UnitTestResultState)result, actual.Result);
        Assert.Equal(UnitTestExecutionSignals.Ready | UnitTestExecutionSignals.Inactive, actual.ExecutionSignals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void MapsEveryIndependentSignal(int bit)
    {
        var (root, fields) = Symbols();
        var data = new byte[root.Size];
        data[14 + bit] = 1;
        Assert.Equal((UnitTestExecutionSignals)(1 << bit), new RunControlLayout(root, fields).Decode(data).ExecutionSignals);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("size")]
    [InlineData("group")]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("overlap")]
    [InlineData("path")]
    public void RejectsInvalidLayout(string change)
    {
        var (root, fields) = Symbols();
        fields[0] = change switch
        {
            "type" => fields[0] with { Type = AdsDataTypeId.ADST_UINT16 },
            "size" => fields[0] with { Size = 4 },
            "group" => fields[0] with { Group = 99 },
            "before" => fields[0] with { Offset = root.Offset - 1 },
            "after" => fields[0] with { Offset = root.Offset + (uint)root.Size - 1 },
            "overlap" => fields[0] with { Offset = fields[1].Offset },
            _ => fields[0] with { Path = "Other.State" }
        };
        Assert.Throws<InvalidDataException>(() => new RunControlLayout(root, fields));
    }

    [Fact]
    public void RejectsTruncationAndInvalidBoolean()
    {
        var (root, fields) = Symbols();
        var layout = new RunControlLayout(root, fields);
        Assert.Throws<InvalidDataException>(() => layout.Decode(new byte[root.Size - 1]));
        var bytes = new byte[root.Size];
        bytes[14] = 2;
        Assert.Throws<InvalidDataException>(() => layout.Decode(bytes));
    }

    [Theory]
    [InlineData("1.0.0", true, true)]
    [InlineData("1.0.1", true, false)]
    [InlineData("2.7.123", false, true)]
    [InlineData("1.0", false, false)]
    [InlineData("1.0.0.1", false, false)]
    [InlineData("1.x.0", false, false)]
    [InlineData("", true, false)]
    public void ValidatesDirectContractSeparatelyFromFrameworkVersion(string value, bool contract, bool valid)
    {
        var bytes = new byte[11];
        Encoding.ASCII.GetBytes(value).CopyTo(bytes, 0);
        if (valid) Assert.Equal(value, RunControlLayout.DecodeVersion(bytes, contract));
        else Assert.Throws<InvalidDataException>(() => RunControlLayout.DecodeVersion(bytes, contract));
    }

    [Fact]
    public void RejectsUnterminatedVersion()
    {
        Assert.Throws<InvalidDataException>(() => RunControlLayout.DecodeVersion(Encoding.ASCII.GetBytes("12345678901"), false));
    }

    [Theory]
    [InlineData("{}", "GVL_UnitTest.RunControl", 10)]
    [InlineData("{\"unitTest\":{\"runControl\":\"Test.Run\",\"monitoringIntervalMs\":25}}", "Test.Run", 25)]
    public void UsesExistingConfigurationAndDefaults(string json, string root, int interval)
    {
        var configuration = new BeckhoffConfigurationService("", ProjectOwnershipMode.ExternallyManaged, TimeProvider.System);
        Assert.True(configuration.LoadConfig(json).IsSuccessful);
        var settings = RunControlSettings.Parse(configuration.Configuration, ProjectOwnershipMode.ExternallyManaged);
        Assert.Equal(root, settings.Root);
        Assert.Equal(TimeSpan.FromMilliseconds(interval), settings.Interval);
    }

    private static (RunControlSymbol Root, RunControlSymbol[] Fields) Symbols()
    {
        var root = new RunControlSymbol("Test.ControlData.Status.Execution", 0x4020, 100, 24, AdsDataTypeId.ADST_BIGTYPE);
        var fields = RunControlLayout.Fields.Select((name, index) => new RunControlSymbol(root.Path + "." + name,
            root.Group, root.Offset + (uint)(index < 3 ? 2 + index * 4 : 14 + index - 3), index < 3 ? 2 : 1,
            index < 3 ? AdsDataTypeId.ADST_INT16 : AdsDataTypeId.ADST_BIT)).ToArray();
        return (root, fields);
    }
}
