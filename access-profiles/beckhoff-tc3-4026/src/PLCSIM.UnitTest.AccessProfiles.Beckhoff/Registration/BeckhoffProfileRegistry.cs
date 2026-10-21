using System.Text.Json;
using System.Text.Json.Serialization;
using PLCSIM.UnitTest.Common.AccessProfiles;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Registration;

internal sealed record BeckhoffProfileRegistry(
    int SchemaVersion, string ProfileId, string ProfileVersion, string ProfileRootPath,
    DateTimeOffset RegisteredAt, AccessProfileRegistrationResult Result, BeckhoffEnvironment Environment)
{
    internal static BeckhoffProfileRegistry Create(AccessProfileContext context,
        AccessProfileRegistrationResult result, BeckhoffEnvironment environment) => new(
            1, BeckhoffAccessProfilePlugin.ProfileInfo.ProfileId, BeckhoffAccessProfilePlugin.ProfileInfo.Version,
            Path.GetFullPath(context.ProfileRootPath), context.TimeProvider.GetUtcNow(), result, environment);
}

internal sealed class BeckhoffProfileRegistryStore
{
    internal const string FileName = "profile-registry.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal async Task SaveAsync(AccessProfileContext context, BeckhoffProfileRegistry registry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(context);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(registry, Options), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal BeckhoffProfileRegistry Load(AccessProfileContext context)
    {
        var path = GetPath(context);
        using var stream = File.OpenRead(path);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("TwinCAT profile registry exceeds its metadata size limit.");
        var registry = JsonSerializer.Deserialize<BeckhoffProfileRegistry>(stream, Options);
        if (registry is null || registry.SchemaVersion != 1
            || registry.ProfileId != BeckhoffAccessProfilePlugin.ProfileInfo.ProfileId
            || registry.ProfileVersion != BeckhoffAccessProfilePlugin.ProfileInfo.Version
            || string.IsNullOrWhiteSpace(registry.ProfileRootPath)
            || !Path.GetFullPath(registry.ProfileRootPath).Equals(Path.GetFullPath(context.ProfileRootPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("TwinCAT profile registry schema or package identity does not match. Register the profile again.");

        Validate(registry);
        return registry;
    }

    internal static string GetPath(AccessProfileContext context)
    {
        var data = Path.GetFullPath(context.ProfileDataDirectory);
        var package = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.ProfileRootPath));
        if (data.Equals(package, StringComparison.OrdinalIgnoreCase)
            || data.StartsWith(package + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Profile registration data must not be stored in the installed package tree.");
        return Path.Combine(data, FileName);
    }

    private static void Validate(BeckhoffProfileRegistry registry)
    {
        var result = registry.Result;
        var capabilities = result?.Capabilities;
        var environment = registry.Environment;
        if (result is null || !Enum.IsDefined(result.State) || capabilities is null
            || capabilities.AccessApis is null || capabilities.ProjectSources is null
            || capabilities.Runtimes is null || capabilities.UnitTestContractVersions is null
            || environment is null || environment.Engineering is null || environment.UsermodeRuntime is null)
            throw new InvalidDataException("TwinCAT profile registry contains incomplete registration data.");

        if (capabilities.AccessApis.Any(item => item is null || string.IsNullOrWhiteSpace(item.Kind)
                || string.IsNullOrWhiteSpace(item.AccessApiVersion) || !Enum.IsDefined(item.Availability))
            || capabilities.ProjectSources.Any(item => item is null || string.IsNullOrWhiteSpace(item.Kind)
                || string.IsNullOrWhiteSpace(item.Extension) || string.IsNullOrWhiteSpace(item.Version) || !Enum.IsDefined(item.Availability))
            || capabilities.Runtimes.Any(item => item is null || string.IsNullOrWhiteSpace(item.Kind)
                || string.IsNullOrWhiteSpace(item.Version) || !Enum.IsDefined(item.Availability) || !Enum.IsDefined(item.Locality))
            || capabilities.UnitTestContractVersions.Any(string.IsNullOrWhiteSpace)
            || environment.Engineering.Concat(environment.UsermodeRuntime).Any(item => item is null
                || string.IsNullOrWhiteSpace(item.Path) || !Path.IsPathFullyQualified(item.Path)
                || !BeckhoffEnvironmentProbe.IsSupportedVersion(item.ProductVersion))
            || (environment.XaeShellPath is not null && !Path.IsPathFullyQualified(environment.XaeShellPath)))
            throw new InvalidDataException("TwinCAT profile registry contains invalid capabilities or installation evidence.");
    }
}
