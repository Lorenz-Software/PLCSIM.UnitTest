using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PLCSIM.UnitTest.Common.AccessProfiles;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Registration;

internal static class BeckhoffProjectSourceInspector
{
    private const int MaxSolutionBytes = 4 * 1024 * 1024;
    private const int MaxSystemProjectBytes = 16 * 1024 * 1024;
    private static readonly Regex ProjectLine = new(
        "^\\s*Project\\(\"[^\"]+\"\\)\\s*=\\s*\"[^\"]*\"\\s*,\\s*\"(?<path>[^\"]+)\"\\s*,\\s*\"[^\"]+\"\\s*$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static AccessProfileProjectSourceEvaluation Inspect(string source)
    {
        var extension = Path.GetExtension(source);
        if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)) return InspectSolution(source);
        if (extension.Equals(".tszip", StringComparison.OrdinalIgnoreCase)) return InspectArchive(source);
        return new(AccessProfileProjectSourceEvaluationState.Unsupported, "Expected a complete TwinCAT solution (.sln) or system-project archive (.tszip).");
    }

    private static AccessProfileProjectSourceEvaluation InspectSolution(string source)
    {
        using var stream = File.OpenRead(source);
        if (stream.Length > MaxSolutionBytes) throw new InvalidDataException("Solution metadata exceeds the lightweight inspection limit.");
        using var reader = new StreamReader(stream);
        var header = reader.ReadLine();
        while (header is not null && string.IsNullOrWhiteSpace(header)) header = reader.ReadLine();
        if (header is null || !header.StartsWith("Microsoft Visual Studio Solution File, Format Version", StringComparison.Ordinal))
            throw new InvalidDataException("The selected solution has no valid solution header.");

        var versions = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var match = ProjectLine.Match(line);
            if (!match.Success) continue;
            var path = match.Groups["path"].Value;
            if (!Path.GetExtension(path).Equals(".tsproj", StringComparison.OrdinalIgnoreCase)) continue;
            var projectPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, path.Replace('\\', Path.DirectorySeparatorChar)));
            using var project = File.OpenRead(projectPath);
            versions.Add(ReadSystemProjectVersion(project, project.Length));
        }
        return EvaluateVersions(versions);
    }

    private static AccessProfileProjectSourceEvaluation InspectArchive(string source)
    {
        using var archive = ZipFile.OpenRead(source);
        if (archive.Entries.Count > 32000) throw new InvalidDataException("Archive directory exceeds the lightweight inspection limit.");
        var projects = archive.Entries.Where(entry => entry.FullName.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (projects.Select(entry => entry.FullName.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != projects.Length)
            throw new InvalidDataException("Archive contains ambiguous duplicate system-project entries.");
        var versions = new List<string>();
        foreach (var entry in projects)
        {
            using var project = entry.Open();
            versions.Add(ReadSystemProjectVersion(project, entry.Length));
        }
        return EvaluateVersions(versions);
    }

    private static string ReadSystemProjectVersion(Stream stream, long length)
    {
        if (length > MaxSystemProjectBytes) throw new InvalidDataException("System-project metadata exceeds the lightweight inspection limit.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxSystemProjectBytes
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "TcSmProject" || !document.Root.Elements().Any(element => element.Name.LocalName == "Project"))
            throw new InvalidDataException("The selected source contains invalid TwinCAT system-project metadata.");
        var version = document.Root.Attribute("TcVersion")?.Value;
        if (!Version.TryParse(version, out _)) throw new InvalidDataException("TwinCAT system-project version metadata is missing or invalid.");
        return version!;
    }

    private static AccessProfileProjectSourceEvaluation EvaluateVersions(IReadOnlyList<string> versions)
    {
        if (versions.Count == 0)
            return new(AccessProfileProjectSourceEvaluationState.Unsupported, "The selected source contains no TwinCAT system project.");
        if (versions.Any(version => !BeckhoffEnvironmentProbe.IsSupportedVersion(version)))
            return new(AccessProfileProjectSourceEvaluationState.Unsupported, "The selected source is outside the TwinCAT 3.1 Build 4026 profile scope.");
        return new(AccessProfileProjectSourceEvaluationState.Supported,
            "Recognized TwinCAT 4026 system-project metadata. Full project dependencies and target compatibility are checked during preparation.");
    }
}
