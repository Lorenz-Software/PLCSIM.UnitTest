using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class EngineeringMetadataTests
{
    [Fact]
    public void IdentityIsStableAcrossImportRootsNamesAndPortsButDistinguishesSystemProjects()
    {
        var root = Path.Combine(Path.GetTempPath(), "engineering-identities");
        var first = EngineeringPlcTarget.FromXml(Path.Combine(root, "one"), Path.Combine(root, "one", "System.tsproj"), "System", EngineeringFixture.PlcXml());
        var second = EngineeringPlcTarget.FromXml(Path.Combine(root, "two"), Path.Combine(root, "two", "system.tsproj"), "Renamed", EngineeringFixture.PlcXml("RenamedPlc", 853));
        var sibling = EngineeringPlcTarget.FromXml(Path.Combine(root, "two"), Path.Combine(root, "two", "other.tsproj"), "System", EngineeringFixture.PlcXml());
        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Id, sibling.Id);
        Assert.Equal("System / Tests", first.ToReference().DisplayName);
        Assert.Equal("TIPC^Tests", first.ToReference().TechnicalName);
    }

    [Theory]
    [InlineData("ItemName", "")]
    [InlineData("PathName", "OTHER^Tests")]
    [InlineData("ItemType", "55")]
    [InlineData("ProjectGUID", "broken")]
    [InlineData("ProjectGUID", "00000000-0000-0000-0000-000000000000")]
    [InlineData("AdsPort", "850")]
    [InlineData("AdsPort", "65536")]
    [InlineData("AdsPort", "852.0")]
    public void InvalidMetadataCannotProduceTarget(string element, string value)
    {
        var xml = XElement.Parse(EngineeringFixture.PlcXml());
        xml.Descendants(element).Single().Value = value;
        Assert.Throws<InvalidDataException>(() => Parse(xml.ToString()));
    }

    [Theory]
    [InlineData("<TreeItem>")]
    [InlineData("<!DOCTYPE TreeItem [<!ENTITY value SYSTEM 'file:///never-read'>]><TreeItem>&value;</TreeItem>")]
    public void MalformedXmlAndExternalEntitiesAreRejected(string xml) => Assert.Throws<XmlException>(() => Parse(xml));

    [Fact]
    public void MissingAndExcessiveMetadataAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Parse("<TreeItem/>"));
        Assert.Throws<XmlException>(() => Parse("<TreeItem>" + new string('x', 1024 * 1024) + "</TreeItem>"));
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("folder/../../escaped.txt")]
    [InlineData("C:/escaped.txt")]
    [InlineData("/escaped.txt")]
    [InlineData("folder/file:stream")]
    [InlineData("folder/./file")]
    public async Task UnsafeArchiveEntriesAreRejectedBeforeEngineeringOrWorkspaceCreation(string entry)
    {
        await using var test = new EngineeringFixture(archive: true);
        using (var archive = ZipFile.Open(test.Source, ZipArchiveMode.Update)) archive.CreateEntry(entry);
        var result = await test.Service.PrepareProjectAsync(new(test.Config()), default);
        Assert.False(result.IsSuccessful);
        Assert.Empty(test.Accesses);
        Assert.True(File.Exists(test.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateArchivePathsAndSymbolicLinksAreRejected(bool symbolicLink)
    {
        await using var test = new EngineeringFixture(archive: true);
        using (var archive = ZipFile.Open(test.Source, ZipArchiveMode.Update))
        {
            if (symbolicLink) archive.CreateEntry("linked").ExternalAttributes = unchecked((int)0xA0000000);
            else { archive.CreateEntry("folder/file"); archive.CreateEntry("FOLDER/FILE"); }
        }
        Assert.False((await test.Service.PrepareProjectAsync(new(test.Config()), default)).IsSuccessful);
        Assert.Empty(test.Accesses);
    }

    private static EngineeringPlcTarget Parse(string xml)
    {
        var root = Path.GetTempPath();
        return EngineeringPlcTarget.FromXml(root, Path.Combine(root, "system.tsproj"), "System", xml);
    }
}
