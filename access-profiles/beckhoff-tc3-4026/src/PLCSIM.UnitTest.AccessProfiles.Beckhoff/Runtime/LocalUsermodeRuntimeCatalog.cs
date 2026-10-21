using System.Xml;
using System.Xml.Linq;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed record LocalUsermodeRuntime(string Name, string NetId, string ConfigurationDirectory)
{
    public RuntimeTargetRef ToReference()
    {
        var displayName = string.Equals(Name, "UmRT_Default", StringComparison.OrdinalIgnoreCase)
            ? "Local Default Runtime" : Name;
        return new(new RuntimeTargetId(NetId), $"{displayName} ({NetId})", Name);
    }
}

internal sealed record OnlineRuntimeIdentity(string NetId, string ConfigurationDirectory, string DeviceType);

internal interface ILocalUsermodeRuntimeCatalog
{
    IReadOnlyList<LocalUsermodeRuntime> Discover(CancellationToken cancellationToken);
    LocalUsermodeRuntime ResolveDefault(CancellationToken cancellationToken);
    LocalUsermodeRuntime Verify(OnlineRuntimeIdentity identity, CancellationToken cancellationToken);
}

internal sealed class LocalUsermodeRuntimeCatalog : ILocalUsermodeRuntimeCatalog
{
    private readonly string root;

    public LocalUsermodeRuntimeCatalog() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Beckhoff", "TwinCAT", "3.1", "Runtimes")) { }

    internal LocalUsermodeRuntimeCatalog(string root) => this.root = Path.GetFullPath(root);

    public IReadOnlyList<LocalUsermodeRuntime> Discover(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(root)) return [];
        var result = Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => Read(Path.Combine(path, "3.1"), cancellationToken)).ToArray();
        if (result.GroupBy(item => item.NetId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidDataException("Multiple local Usermode configurations declare the same AMS Net ID.");
        return result;
    }

    public LocalUsermodeRuntime ResolveDefault(CancellationToken cancellationToken) =>
        Read(Path.Combine(root, "UmRT_Default", "3.1"), cancellationToken);

    public LocalUsermodeRuntime Verify(OnlineRuntimeIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(identity.DeviceType, "PC-WIN-U", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected target is not a Windows Usermode Runtime.");
        if (!Path.IsPathFullyQualified(identity.ConfigurationDirectory)
            || identity.ConfigurationDirectory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("The Runtime did not report an absolute local configuration directory.");
        var local = Read(identity.ConfigurationDirectory, cancellationToken);
        if (!string.Equals(local.NetId, identity.NetId, StringComparison.Ordinal))
            throw new InvalidDataException("The online AMS identity does not match the local Usermode configuration.");
        return local;
    }

    internal static LocalUsermodeRuntime Read(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        using var reader = XmlReader.Create(Path.Combine(directory, "TcRegistry.xml"), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4 * 1024 * 1024
        });
        var document = XDocument.Load(reader);
        var hklm = Key(document.Root, "HKLM");
        var twinCat = Key(Key(Key(hklm, "Software"), "Beckhoff"), "TwinCAT3");
        var value = Value(Key(twinCat, "System"), "AmsNetId", "BIN");
        byte[] bytes;
        try { bytes = Convert.FromHexString(value); }
        catch (FormatException error) { throw new InvalidDataException("Invalid Usermode AMS Net ID encoding.", error); }
        if (bytes.Length != 6 || bytes.All(b => b == 0) || bytes.All(b => b == 255))
            throw new InvalidDataException("The Usermode AMS Net ID must contain six concrete address bytes.");
        if (!string.Equals(Value(Key(hklm, "Ident"), "DeviceType", "SZ"), "PC-WIN-U", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The local configuration is not a Windows Usermode Runtime.");
        var installDirectory = Value(Key(twinCat, "3.1"), "InstallDir", "SZ");
        if (!Path.IsPathFullyQualified(installDirectory) || !SameDirectory(directory, installDirectory))
            throw new InvalidDataException("The Usermode InstallDir does not match its configuration directory.");
        cancellationToken.ThrowIfCancellationRequested();
        return new(Directory.GetParent(directory)?.Name ?? directory, new AmsNetId(bytes).ToString(), directory);
    }

    internal static bool SameDirectory(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    private static XElement Key(XElement? parent, string name) => Single(parent, "Key", name);
    private static string Value(XElement parent, string name, string type)
    {
        var value = Single(parent, "Value", name);
        if ((string?)value.Attribute("Type") != type || string.IsNullOrWhiteSpace(value.Value))
            throw new InvalidDataException($"Invalid Usermode registry value '{name}'.");
        return value.Value.Trim();
    }
    private static XElement Single(XElement? parent, string element, string name)
    {
        var matches = parent?.Elements(element).Where(item => (string?)item.Attribute("Name") == name).ToArray();
        if (matches?.Length != 1) throw new InvalidDataException($"Missing or ambiguous Usermode registry entry '{name}'.");
        return matches[0];
    }
}
