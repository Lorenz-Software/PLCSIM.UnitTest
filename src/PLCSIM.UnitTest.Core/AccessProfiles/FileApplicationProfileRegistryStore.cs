using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PLCSIM.UnitTest.Core.AccessProfiles;

public sealed class FileApplicationProfileRegistryStore : IApplicationProfileRegistryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ApplicationProfileRegistryOptions options;
    private readonly CoreServiceContext coreContext;
    private readonly AccessProfileCapabilityValidator validator;
    private readonly ILogger<FileApplicationProfileRegistryStore> logger;

    public FileApplicationProfileRegistryStore(
        CoreServiceContext coreContext,
        ApplicationProfileRegistryOptions options,
        AccessProfileCapabilityValidator validator)
    {
        this.coreContext = coreContext ?? throw new ArgumentNullException(nameof(coreContext));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        logger = coreContext.LoggerFactory.CreateLogger<FileApplicationProfileRegistryStore>();
    }

    public async Task<ApplicationProfileRegistry> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        var path = GetRegistryPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(path))
        {
            var empty = ApplicationProfileRegistry.Empty(coreContext.TimeProvider.GetUtcNow());
            await SaveAsync(empty, cancellationToken).ConfigureAwait(false);
            return empty;
        }

        try
        {
            return await LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            var quarantinePath = Quarantine(path);
            PublishRegistryDiagnostic(
                "Profile.Registry.Corrupt",
                LogLevel.Error,
                $"Access profile registry '{path}' is corrupt or unreadable and was reset.",
                $"Reason: {ex.Message}; QuarantinePath: {quarantinePath}");

            logger.LogError(
                ex,
                "Access profile registry was corrupt or unreadable and was quarantined. RegistryPath={RegistryPath}, QuarantinePath={QuarantinePath}",
                path,
                quarantinePath);

            var empty = ApplicationProfileRegistry.Empty(coreContext.TimeProvider.GetUtcNow());
            await SaveAsync(empty, cancellationToken).ConfigureAwait(false);
            return empty;
        }
    }

    public async Task<ApplicationProfileRegistry> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetRegistryPath();
        if (!File.Exists(path))
        {
            return ApplicationProfileRegistry.Empty(coreContext.TimeProvider.GetUtcNow());
        }

        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var registry = await JsonSerializer.DeserializeAsync<ApplicationProfileRegistry>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (registry is null)
            {
                throw new InvalidDataException($"Access profile registry '{path}' is empty.");
            }

            var validation = validator.ValidateRegistry(registry);
            if (!validation.IsValid)
            {
                throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors));
            }

            return registry;
        }
    }

    public async Task SaveAsync(ApplicationProfileRegistry registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var validation = validator.ValidateRegistry(registry);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors));
        }

        var path = GetRegistryPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = File.Create(path);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, registry, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private string GetRegistryPath()
    {
        if (string.IsNullOrWhiteSpace(options.RegistryPath))
        {
            return ApplicationProfileRegistryOptions.DefaultRegistryPath;
        }

        return Path.GetFullPath(options.RegistryPath);
    }

    private string Quarantine(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var timestamp = coreContext.TimeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss");
        var quarantinePath = Path.Combine(directory, $"application-profile-registry.corrupt-{timestamp}.json");
        File.Move(path, quarantinePath, overwrite: true);
        return quarantinePath;
    }

    private void PublishRegistryDiagnostic(
        string code,
        LogLevel level,
        string message,
        string? technicalDetail)
    {
        var diagnostic = new Diagnostic(
            code,
            DiagnosticCategory.Configuration,
            level,
            DiagnosticSource.AccessProfileRegistry,
            DiagnosticPhase.Configuration,
            message,
            new DiagnosticCorrelation(),
            coreContext.TimeProvider.GetUtcNow(),
            TechnicalDetail: technicalDetail);
        coreContext.Diagnostics.Writer.Add(diagnostic);
    }
}
