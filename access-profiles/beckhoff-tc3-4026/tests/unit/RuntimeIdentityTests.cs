using System.Text;
using System.Xml;
using System.Xml.Linq;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.AccessProfiles;
using TwinCAT.Ads;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class RuntimeIdentityTests
{
    [Theory]
    [InlineData("UmRT_Default", "Local Default Runtime")]
    [InlineData("umrt_default", "Local Default Runtime")]
    [InlineData("TestRuntime", "TestRuntime")]
    public void DisplayNamePreservesRuntimeIdentity(string name, string displayName)
    {
        var runtime = new LocalUsermodeRuntime(name, "192.0.2.10.1.1", @"C:\runtime\3.1");
        var target = runtime.ToReference();
        Assert.Equal("192.0.2.10.1.1", target.Id.Value);
        Assert.Equal($"{displayName} (192.0.2.10.1.1)", target.DisplayName);
        Assert.Equal(name, runtime.Name);
    }

    [Fact]
    public void DefaultUsesNamedConfigurationNotFirstCandidateAndDiscoveryIsReadOnly()
    {
        using var files = new RuntimeFiles();
        files.Add("A_First", "C000020B0101");
        var directory = files.Add("UmRT_Default");
        var before = File.ReadAllText(Path.Combine(directory, "TcRegistry.xml"));
        var catalog = new LocalUsermodeRuntimeCatalog(files.Root);
        Assert.Equal(2, catalog.Discover(default).Count);
        var runtime = catalog.ResolveDefault(default);
        Assert.Equal("UmRT_Default", runtime.Name);
        Assert.Equal("192.0.2.10.1.1", runtime.NetId);
        Assert.Equal(before, File.ReadAllText(Path.Combine(directory, "TcRegistry.xml")));
        Assert.Equal(runtime, catalog.Verify(new(runtime.NetId, directory, "PC-WIN-U"), default));
    }

    [Fact]
    public void MissingDefaultNeverSelectsAnotherConfiguration()
    {
        using var files = new RuntimeFiles();
        files.Add("Other");
        var catalog = new LocalUsermodeRuntimeCatalog(files.Root);
        Assert.ThrowsAny<IOException>(() => catalog.ResolveDefault(default));
        Assert.Single(catalog.Discover(default));
    }

    [Fact]
    public void DuplicateNetIdsAreAmbiguousButExplicitVerifiedTargetRemainsUsable()
    {
        using var files = new RuntimeFiles();
        var directory = files.Add("UmRT_Default");
        files.Add("Other");
        var catalog = new LocalUsermodeRuntimeCatalog(files.Root);
        Assert.Throws<InvalidDataException>(() => catalog.Discover(default));
        Assert.Equal("UmRT_Default", catalog.Verify(new("192.0.2.10.1.1", directory, "PC-WIN-U"), default).Name);
    }

    [Theory]
    [InlineData("000000000000")]
    [InlineData("FFFFFFFFFFFF")]
    [InlineData("1234")]
    [InlineData("not-hex")]
    public void InvalidStoredAddressIsRejected(string address)
    {
        using var files = new RuntimeFiles();
        var directory = files.Add("UmRT_Default", address);
        Assert.Throws<InvalidDataException>(() => LocalUsermodeRuntimeCatalog.Read(directory, default));
    }

    [Fact]
    public void OnlineKernelIdentityWrongNetIdAndRemotePathsAreRejected()
    {
        using var files = new RuntimeFiles();
        var directory = files.Add("UmRT_Default");
        var catalog = new LocalUsermodeRuntimeCatalog(files.Root);
        Assert.Throws<InvalidDataException>(() => catalog.Verify(new("192.0.2.10.1.1", directory, "PC-WIN"), default));
        Assert.Throws<InvalidDataException>(() => catalog.Verify(new("192.0.2.11.1.1", directory, "PC-WIN-U"), default));
        Assert.Throws<InvalidDataException>(() => catalog.Verify(new("192.0.2.10.1.1", @"\\server\runtime", "PC-WIN-U"), default));
        Assert.Throws<InvalidDataException>(() => catalog.Verify(new("192.0.2.10.1.1", "relative-path", "PC-WIN-U"), default));
    }

    [Theory]
    [InlineData("DeviceType", "PC-WIN")]
    [InlineData("InstallDir", "C:\\different-runtime\\3.1")]
    public void ContradictoryLocalIdentityIsRejected(string name, string value)
    {
        using var files = new RuntimeFiles();
        var directory = files.Add("UmRT_Default");
        var path = Path.Combine(directory, "TcRegistry.xml");
        var document = XDocument.Load(path);
        document.Descendants("Value").Single(item => (string?)item.Attribute("Name") == name).Value = value;
        document.Save(path);
        Assert.Throws<InvalidDataException>(() => LocalUsermodeRuntimeCatalog.Read(directory, default));
    }

    [Fact]
    public void XmlEntitiesAndCancelledReadsAreRejected()
    {
        using var files = new RuntimeFiles();
        var directory = files.Add("UmRT_Default");
        Assert.ThrowsAny<OperationCanceledException>(() => LocalUsermodeRuntimeCatalog.Read(directory, new(true)));
        File.WriteAllText(Path.Combine(directory, "TcRegistry.xml"),
            "<!DOCTYPE TcRegistry [<!ENTITY external SYSTEM 'file:///not-read'>]><TcRegistry>&external;</TcRegistry>");
        Assert.Throws<XmlException>(() => LocalUsermodeRuntimeCatalog.Read(directory, default));
    }

    [Fact]
    public void RegistryValuesUseBoundedNameTypeDataDecoding()
    {
        var wire = Encoding.UTF8.GetBytes("AmsNetId\0").Concat(new byte[] { 3, 0, 0, 0, 192, 0, 2, 10, 1, 1 }).ToArray();
        var value = AdsRuntimeAccess.DecodeRegistryValue(wire);
        Assert.Equal("AmsNetId", value.Name);
        Assert.Equal(3U, value.Type);
        Assert.Equal(new byte[] { 192, 0, 2, 10, 1, 1 }, value.Data);
        Assert.Equal("", AdsRuntimeAccess.DecodeRegistryValue([0, 1, 0, 0, 0, 0]).Name);
        Assert.Throws<InvalidDataException>(() => AdsRuntimeAccess.DecodeRegistryValue([]));
        Assert.Throws<InvalidDataException>(() => AdsRuntimeAccess.DecodeRegistryValue([65, 0, 1, 0, 0]));
        Assert.Throws<InvalidDataException>(() => AdsRuntimeAccess.DecodeRegistryValue([65, 66, 67]));
    }

    [Fact]
    public void RegistryStringsRejectTruncationEmbeddedTerminatorsAndInvalidUtf8()
    {
        Assert.Equal("PC-WIN-U", AdsRuntimeAccess.ReadRegistryString(Encoding.UTF8.GetBytes("PC-WIN-U\0")));
        Assert.Throws<InvalidDataException>(() => AdsRuntimeAccess.ReadRegistryString([]));
        Assert.Throws<InvalidDataException>(() => AdsRuntimeAccess.ReadRegistryString([65]));
        Assert.Throws<InvalidDataException>(() => AdsRuntimeAccess.ReadRegistryString([65, 0, 66, 0]));
        Assert.Throws<DecoderFallbackException>(() => AdsRuntimeAccess.ReadRegistryString([255, 0]));
    }

    [Theory]
    [InlineData("0.0.0.0.0.0")]
    [InlineData("127.0.0.1.1.1")]
    [InlineData("255.255.255.255.255.255")]
    [InlineData("invalid")]
    public void RouterAliasesAreNotConcreteRuntimeSelections(string address) =>
        Assert.Throws<ArgumentException>(() => RuntimeAccessConfiguration.NormalizeNetId(address));

    [Theory]
    [InlineData(AdsErrorCode.TargetPortNotFound, true)]
    [InlineData(AdsErrorCode.TargetMachineNotFound, true)]
    [InlineData(AdsErrorCode.ClientSyncTimeOut, true)]
    [InlineData(AdsErrorCode.DeviceInvalidState, true)]
    [InlineData(AdsErrorCode.DeviceSymbolNotFound, false)]
    [InlineData(AdsErrorCode.DeviceInvalidSize, false)]
    [InlineData(AdsErrorCode.NoError, false)]
    public void EarlyCheckIsRestrictedToPotentialStateOrTransportFailures(AdsErrorCode error, bool expected) =>
        Assert.Equal(expected, AdsAccessException.RequiresSystemCheck(error));

    private sealed class RuntimeFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "beckhoff-runtime-tests-" + Guid.NewGuid().ToString("N"));
        public string Add(string name, string address = "C000020A0101")
        {
            var directory = Directory.CreateDirectory(Path.Combine(Root, name, "3.1")).FullName;
            static XElement Value(string name, string type, string value) =>
                new("Value", new XAttribute("Name", name), new XAttribute("Type", type), value);
            static XElement Key(string name, params XElement[] children) => new("Key", new XAttribute("Name", name), children);
            var document = new XDocument(new XElement("TcRegistry", Key("HKLM",
                Key("Software", Key("Beckhoff", Key("TwinCAT3",
                    Key("System", Value("AmsNetId", "BIN", address)),
                    Key("3.1", Value("InstallDir", "SZ", directory + @"\\"))))),
                Key("Ident", Value("DeviceType", "SZ", "PC-WIN-U")))));
            document.Save(Path.Combine(directory, "TcRegistry.xml"));
            return directory;
        }
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
