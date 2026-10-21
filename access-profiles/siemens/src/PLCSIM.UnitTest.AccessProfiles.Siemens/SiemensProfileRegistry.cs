using System.Text.Json;
using System.Text.Json.Serialization;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.AccessProfiles;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed record SiemensProfileRegistry(
    string SchemaVersion,
    string ProfileId,
    string ProfileVersion,
    string ProfileRootPath,
    DateTimeOffset RegisteredAt,
    AccessProfileRegistrationState State,
    IReadOnlyList<AccessApiCapability> AccessApis,
    IReadOnlyList<ProjectSourceCapability> ProjectSources,
    IReadOnlyList<RuntimeCapability> Runtimes,
    IReadOnlyList<string> UnitTestContractVersions,
    IReadOnlyList<SiemensBindingCandidateEvidence> BindingCandidates,
    string? Message)
{
    public const string CurrentSchemaVersion = "1.1";

    public static SiemensProfileRegistry FromRegistrationResult(
        AccessProfileInfo profileInfo,
        string profileRootPath,
        AccessProfileRegistrationResult result,
        DateTimeOffset registeredAt,
        IReadOnlyList<SiemensBindingCandidateEvidence>? bindingCandidates = null)
    {
        ArgumentNullException.ThrowIfNull(profileInfo);
        ArgumentNullException.ThrowIfNull(result);

        return new SiemensProfileRegistry(
            CurrentSchemaVersion,
            profileInfo.ProfileId,
            profileInfo.Version,
            profileRootPath,
            registeredAt,
            result.State,
            result.Capabilities.AccessApis.ToArray(),
            result.Capabilities.ProjectSources.ToArray(),
            result.Capabilities.Runtimes.ToArray(),
            result.Capabilities.UnitTestContractVersions.ToArray(),
            bindingCandidates?.ToArray() ?? Array.Empty<SiemensBindingCandidateEvidence>(),
            result.Message);
    }
}

internal sealed record SiemensBindingCandidateEvidence(
    string BindingKind,
    string BindingVersion,
    string PackageRootPath,
    string DescriptorPath,
    int DescriptorSchemaVersion,
    string DescriptorFingerprint,
    SiemensBindingEntryPoint EntryPoint,
    string PackageFingerprint,
    SiemensBindingDeclaration Declaration,
    string? ProductEvidenceSummary);

internal interface ISiemensProfileRegistryStore
{
    string GetRegistryPath(AccessProfileContext context);

    Task SaveAsync(
        AccessProfileContext context,
        SiemensProfileRegistry registry,
        CancellationToken cancellationToken);

    SiemensProfileRegistry Load(AccessProfileContext context);

    Task<SiemensProfileRegistry> LoadAsync(
        AccessProfileContext context,
        CancellationToken cancellationToken);
}

internal interface ISiemensProfileRegistryPathResolver
{
    string ResolveProfileDataDirectory(string profileDataDirectory);
}

internal sealed class SiemensProfileRegistryPathResolver : ISiemensProfileRegistryPathResolver
{
    public string ResolveProfileDataDirectory(string profileDataDirectory)
    {
        return Path.GetFullPath(profileDataDirectory);
    }
}

internal sealed class FixedSiemensProfileRegistryPathResolver(string profileDataDirectory) : ISiemensProfileRegistryPathResolver
{
    public string ResolveProfileDataDirectory(string ignoredProfileDataDirectory)
    {
        return Path.GetFullPath(profileDataDirectory);
    }
}

internal sealed class FileSiemensProfileRegistryStore : ISiemensProfileRegistryStore
{
    public const string RegistryFileName = "profile-registry.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ISiemensProfileRegistryPathResolver pathResolver;

    public FileSiemensProfileRegistryStore(ISiemensProfileRegistryPathResolver? pathResolver = null)
    {
        this.pathResolver = pathResolver ?? new SiemensProfileRegistryPathResolver();
    }

    public string GetRegistryPath(AccessProfileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return GetRegistryPath(context.ProfileDataDirectory);
    }

    public async Task SaveAsync(
        AccessProfileContext context,
        SiemensProfileRegistry registry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(registry);

        var path = GetRegistryPath(context.ProfileDataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, registry, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public SiemensProfileRegistry Load(AccessProfileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = GetRegistryPath(context.ProfileDataDirectory);
        using var stream = File.OpenRead(path);
        var registry = JsonSerializer.Deserialize<SiemensProfileRegistry>(stream, SerializerOptions);
        return ValidateRegistry(registry, path);
    }

    public async Task<SiemensProfileRegistry> LoadAsync(
        AccessProfileContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = GetRegistryPath(context.ProfileDataDirectory);
        await using var stream = File.OpenRead(path);
        var registry = await JsonSerializer.DeserializeAsync<SiemensProfileRegistry>(
                stream,
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);

        return ValidateRegistry(registry, path);
    }

    private static SiemensProfileRegistry ValidateRegistry(
        SiemensProfileRegistry? registry,
        string path)
    {
        if (registry is null)
        {
            throw new InvalidDataException($"Siemens profile registry '{path}' is empty.");
        }

        if (!string.Equals(registry.SchemaVersion, SiemensProfileRegistry.CurrentSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported Siemens profile registry schema version '{registry.SchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(registry.ProfileId)
            || string.IsNullOrWhiteSpace(registry.ProfileVersion)
            || string.IsNullOrWhiteSpace(registry.ProfileRootPath))
        {
            throw new InvalidDataException($"Siemens profile registry '{path}' misses required profile identity metadata.");
        }

        if (!Enum.IsDefined(registry.State)
            || registry.AccessApis is null
            || registry.ProjectSources is null
            || registry.Runtimes is null
            || registry.UnitTestContractVersions is null
            || registry.BindingCandidates is null)
        {
            throw new InvalidDataException($"Siemens profile registry '{path}' has an invalid state or misses required collections.");
        }

        if (registry.AccessApis.Any(capability => capability is null
                || string.IsNullOrWhiteSpace(capability.Kind)
                || string.IsNullOrWhiteSpace(capability.AccessApiVersion)
                || !Enum.IsDefined(capability.Availability))
            || registry.ProjectSources.Any(capability => capability is null
                || string.IsNullOrWhiteSpace(capability.Kind)
                || string.IsNullOrWhiteSpace(capability.Extension)
                || string.IsNullOrWhiteSpace(capability.Version)
                || !Enum.IsDefined(capability.Availability))
            || registry.Runtimes.Any(capability => capability is null
                || string.IsNullOrWhiteSpace(capability.Kind)
                || string.IsNullOrWhiteSpace(capability.Version)
                || !Enum.IsDefined(capability.Locality)
                || !Enum.IsDefined(capability.Availability))
            || registry.UnitTestContractVersions.Any(string.IsNullOrWhiteSpace)
            || registry.BindingCandidates.Any(candidate => !IsValidCandidate(candidate)))
        {
            throw new InvalidDataException($"Siemens profile registry '{path}' contains invalid capability or binding-candidate evidence.");
        }

        return registry;
    }

    private static bool IsValidCandidate(SiemensBindingCandidateEvidence? candidate)
    {
        return candidate is not null
            && (string.Equals(candidate.BindingKind, SiemensBindingKinds.TiaOpenness, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.BindingKind, SiemensBindingKinds.PlcSimAdvanced, StringComparison.OrdinalIgnoreCase))
            && Version.TryParse(candidate.BindingVersion, out _)
            && !string.IsNullOrWhiteSpace(candidate.PackageRootPath)
            && !string.IsNullOrWhiteSpace(candidate.DescriptorPath)
            && !string.IsNullOrWhiteSpace(candidate.DescriptorFingerprint)
            && !string.IsNullOrWhiteSpace(candidate.PackageFingerprint)
            && candidate.EntryPoint is not null
            && !string.IsNullOrWhiteSpace(candidate.EntryPoint.Assembly)
            && !string.IsNullOrWhiteSpace(candidate.EntryPoint.TypeName)
            && candidate.Declaration is not null
            && candidate.Declaration.AccessApis is not null
            && candidate.Declaration.ProjectSources is not null
            && candidate.Declaration.Runtimes is not null
            && candidate.Declaration.UnitTestCommunicationContractVersions is not null
            && candidate.Declaration.UnitTestEventStreamContractVersions is not null
            && candidate.Declaration.AccessApis.All(accessApi => accessApi is not null
                && !string.IsNullOrWhiteSpace(accessApi.Kind)
                && !string.IsNullOrWhiteSpace(accessApi.ProductVersion)
                && !string.IsNullOrWhiteSpace(accessApi.AccessApiVersion))
            && candidate.Declaration.ProjectSources.All(projectSource => projectSource is not null
                && !string.IsNullOrWhiteSpace(projectSource.Kind)
                && !string.IsNullOrWhiteSpace(projectSource.Extension)
                && !string.IsNullOrWhiteSpace(projectSource.Version))
            && candidate.Declaration.Runtimes.All(runtime => runtime is not null
                && !string.IsNullOrWhiteSpace(runtime.Kind)
                && !string.IsNullOrWhiteSpace(runtime.Version)
                && Enum.IsDefined(runtime.Locality)
                && runtime.CompatibleProjectSourceKinds is not null
                && runtime.CompatibleProjectSourceKinds.All(kind => !string.IsNullOrWhiteSpace(kind)))
            && candidate.Declaration.UnitTestCommunicationContractVersions.All(version => !string.IsNullOrWhiteSpace(version))
            && candidate.Declaration.UnitTestEventStreamContractVersions.All(version => !string.IsNullOrWhiteSpace(version));
    }

    private string GetRegistryPath(string profileDataDirectory)
    {
        var resolvedDataDirectory = pathResolver.ResolveProfileDataDirectory(profileDataDirectory);
        return Path.Combine(resolvedDataDirectory, RegistryFileName);
    }
}
