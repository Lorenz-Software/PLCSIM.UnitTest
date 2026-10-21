using System.Text.Json;
using PLCSIM.UnitTest.Common.AccessProfiles;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;

internal sealed record ProjectSettings(string SourcePath, string? ArchiveParent, string? TargetId, int? ExplicitPlcPort)
{
    internal bool IsArchive => Path.GetExtension(SourcePath).Equals(".tszip", StringComparison.OrdinalIgnoreCase);

    internal static ProjectSettings Parse(AccessProfileConfig configuration)
    {
        if (configuration.ProfileId != BeckhoffAccessProfilePlugin.ProfileInfo.ProfileId
            || configuration.ProfileVersion is not null && configuration.ProfileVersion != BeckhoffAccessProfilePlugin.ProfileInfo.Version)
            throw new ArgumentException("Configuration belongs to another profile or version.");
        var validator = new BeckhoffConfigurationService(configuration.ProjectSource, ProjectOwnershipMode.ApplicationManaged, TimeProvider.System);
        var validation = validator.LoadConfig(configuration.Values.Json);
        if (!validation.IsSuccessful) throw new ArgumentException(string.Join(" ", validation.Diagnostics.Select(item => item.Message)));
        if (string.IsNullOrWhiteSpace(configuration.ProjectSource)) throw new ArgumentException("Select a TwinCAT solution or archive.");
        var source = Path.GetFullPath(configuration.ProjectSource);
        using var json = JsonDocument.Parse(configuration.Values.Json);
        var root = json.RootElement;
        string? parent = null;
        if (Path.GetExtension(source).Equals(".tszip", StringComparison.OrdinalIgnoreCase)
            && root.TryGetProperty("twincat", out var twincat) && twincat.TryGetProperty("archiveWorkDirectory", out var directory))
            parent = Path.GetFullPath(directory.GetString()!);
        int? port = root.TryGetProperty("runtimeRef", out var runtime) && runtime.TryGetProperty("plcPort", out var portValue)
            ? portValue.GetInt32() : null;
        return new(source, parent, validator.ProjectTargetId?.Value, port);
    }

    internal bool UsesSameSource(ProjectSettings other) =>
        string.Equals(SourcePath, other.SourcePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ArchiveParent, other.ArchiveParent, StringComparison.OrdinalIgnoreCase);
}
