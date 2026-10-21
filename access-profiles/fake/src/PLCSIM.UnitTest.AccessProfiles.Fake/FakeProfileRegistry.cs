using PLCSIM.UnitTest.Common.AccessProfiles;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PLCSIM.UnitTest.AccessProfiles.Fake;

public sealed record FakeProfileRegistry(
    string SchemaVersion,
    DateTimeOffset RegisteredAt,
    AccessProfileRegistrationState State,
    IReadOnlyList<FakeProjectSourceRegistration> ProjectSources,
    string? Message)
{
    public const string CurrentSchemaVersion = "1.0";

    public static FakeProfileRegistry FromCapabilities(
        AccessProfileRegistrationResult result,
        DateTimeOffset registeredAt)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new FakeProfileRegistry(
            CurrentSchemaVersion,
            registeredAt,
            result.State,
            result.Capabilities.ProjectSources
                .Select(source => new FakeProjectSourceRegistration(
                    source.Kind,
                    source.Extension,
                    source.Version,
                    source.Availability))
                .ToArray(),
            result.Message);
    }
}

public sealed record FakeProjectSourceRegistration(
    string Kind,
    string Extension,
    string Version,
    CapabilityAvailability Availability);

public interface IFakeProfileRegistryStore
{
    string GetRegistryPath(AccessProfileContext context);

    Task SaveAsync(
        AccessProfileContext context,
        FakeProfileRegistry registry,
        CancellationToken cancellationToken);

    FakeProfileRegistry Load(AccessProfileContext context);

    Task<FakeProfileRegistry> LoadAsync(
        AccessProfileContext context,
        CancellationToken cancellationToken);
}

public interface IFakeProfileRegistryPathResolver
{
    string ResolveProfileDataDirectory(string profileDataDirectory);
}

public sealed class FakeProfileRegistryPathResolver : IFakeProfileRegistryPathResolver
{
    public string ResolveProfileDataDirectory(string profileDataDirectory)
    {
        return Path.GetFullPath(profileDataDirectory);
    }
}

public sealed class FixedFakeProfileRegistryPathResolver(string profileDataDirectory) : IFakeProfileRegistryPathResolver
{
    public string ResolveProfileDataDirectory(string ignoredProfileDataDirectory)
    {
        return Path.GetFullPath(profileDataDirectory);
    }
}

public sealed class FileFakeProfileRegistryStore : IFakeProfileRegistryStore
{
    public const string RegistryFileName = "profile-registry.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IFakeProfileRegistryPathResolver pathResolver;

    public FileFakeProfileRegistryStore(IFakeProfileRegistryPathResolver? pathResolver = null)
    {
        this.pathResolver = pathResolver ?? new FakeProfileRegistryPathResolver();
    }

    public string GetRegistryPath(AccessProfileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return GetRegistryPath(context.ProfileDataDirectory);
    }

    public async Task SaveAsync(
        AccessProfileContext context,
        FakeProfileRegistry registry,
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

    public FakeProfileRegistry Load(AccessProfileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = GetRegistryPath(context.ProfileDataDirectory);
        using var stream = File.OpenRead(path);
        var registry = JsonSerializer.Deserialize<FakeProfileRegistry>(stream, SerializerOptions);
        return ValidateRegistry(registry, path);
    }

    public async Task<FakeProfileRegistry> LoadAsync(
        AccessProfileContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = GetRegistryPath(context.ProfileDataDirectory);
        await using var stream = File.OpenRead(path);
        var registry = await JsonSerializer.DeserializeAsync<FakeProfileRegistry>(
                stream,
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);

        return ValidateRegistry(registry, path);
    }

    private static FakeProfileRegistry ValidateRegistry(
        FakeProfileRegistry? registry,
        string path)
    {
        if (registry is null)
        {
            throw new InvalidDataException($"Fake profile registry '{path}' is empty.");
        }

        if (!string.Equals(registry.SchemaVersion, FakeProfileRegistry.CurrentSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported fake profile registry schema version '{registry.SchemaVersion}'.");
        }

        return registry;
    }

    private string GetRegistryPath(
        string profileDataDirectory)
    {
        var resolvedDataDirectory = pathResolver.ResolveProfileDataDirectory(profileDataDirectory);
        return Path.Combine(resolvedDataDirectory, RegistryFileName);
    }
}
