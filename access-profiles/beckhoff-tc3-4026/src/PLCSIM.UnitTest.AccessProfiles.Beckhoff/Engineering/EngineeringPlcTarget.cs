using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PLCSIM.UnitTest.Common.References;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;

internal sealed record EngineeringPlcTarget(string SystemProjectPath, string SystemProjectName,
    string PlcTreePath, string PlcName, Guid PlcProjectId, int PlcPort, string Id)
{
    internal ProjectTargetRef ToReference() => new(new(Id), $"{SystemProjectName} / {PlcName}", PlcTreePath);

    internal static EngineeringPlcTarget FromXml(string workspaceRoot, string systemPath, string systemName, string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024
        });
        var root = XDocument.Load(reader).Root;
        var definition = root?.Element("PlcProjectDef");
        var name = root?.Element("ItemName")?.Value;
        var treePath = root?.Element("PathName")?.Value;
        if (root?.Name != "TreeItem" || root.Element("ItemType")?.Value != "56"
            || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(treePath)
            || !treePath.StartsWith("TIPC^", StringComparison.Ordinal)
            || !Guid.TryParse(definition?.Element("ProjectGUID")?.Value, out var guid) || guid == Guid.Empty
            || !int.TryParse(definition?.Element("AdsPort")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 851 or > 65535)
            throw new InvalidDataException("PLC project metadata has no valid identity, tree path or ADS port.");
        var path = Path.GetFullPath(systemPath);
        var relative = Path.GetRelativePath(workspaceRoot, path).Replace('\\', '/');
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("System project has no source-relative identity.");
        var id = "system:" + Uri.EscapeDataString(relative.ToLowerInvariant()) + "/plc:" + guid.ToString("D");
        return new(path, systemName, treePath, name, guid, port, id);
    }
}
