using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PLCSIM.UnitTest.Core.AccessProfiles;

public interface IApplicationProfileRegistrationService
{
    Task<ApplicationProfileRegistrationCheckResult> CheckProfileRegistrationsAsync(
        CancellationToken cancellationToken);

    Task<ApplicationProfileRegistrationRefreshResult> RefreshProfileRegistrationAsync(
        AccessProfileCandidateKey profile,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<ApplicationProfileRegistrationRefreshAllResult> RefreshAllProfileRegistrationsAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class ApplicationProfileRegistrationService : IApplicationProfileRegistrationService
{
    private readonly AccessProfilePluginLoader pluginLoader;
    private readonly AccessProfileDiscoveryOptions discoveryOptions;
    private readonly IApplicationProfileRegistryStore registryStore;
    private readonly AccessProfileCapabilityValidator validator;
    private readonly CoreServiceContext coreContext;
    private readonly ILogger<ApplicationProfileRegistrationService> logger;

    public ApplicationProfileRegistrationService(
        AccessProfilePluginLoader pluginLoader,
        AccessProfileDiscoveryOptions discoveryOptions,
        IApplicationProfileRegistryStore registryStore,
        AccessProfileCapabilityValidator validator,
        CoreServiceContext coreContext)
    {
        this.pluginLoader = pluginLoader ?? throw new ArgumentNullException(nameof(pluginLoader));
        this.discoveryOptions = discoveryOptions ?? throw new ArgumentNullException(nameof(discoveryOptions));
        this.registryStore = registryStore ?? throw new ArgumentNullException(nameof(registryStore));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.coreContext = coreContext ?? throw new ArgumentNullException(nameof(coreContext));
        logger = coreContext.LoggerFactory.CreateLogger<ApplicationProfileRegistrationService>();
    }

    public async Task<ApplicationProfileRegistrationCheckResult> CheckProfileRegistrationsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var registry = await registryStore
                .EnsureLoadedAsync(cancellationToken)
                .ConfigureAwait(false);
            var discovery = pluginLoader.Discover(discoveryOptions);
            if (HasDiscoveryFailure(discovery))
            {
                const string message = "Access profile registration status could not be determined because profile discovery failed.";
                logger.LogWarning(
                    "Application profile registration check failed during discovery. CandidateCount={CandidateCount}, DiagnosticCount={DiagnosticCount}",
                    discovery.Candidates.Count,
                    discovery.Diagnostics.Count);
                return new ApplicationProfileRegistrationCheckResult(
                    ApplicationProfileRegistrationCheckStatus.Failed,
                    message,
                    discovery.Diagnostics);
            }

            var candidates = discovery.Candidates
                .Where(candidate => candidate.State == AccessProfileCandidateState.Discovered)
                .ToArray();
            if (candidates.Length == 0 && registry.Profiles.Count == 0)
            {
                const string message = "No access profile packages were discovered.";
                logger.LogInformation("Application profile registration check found no profile candidates.");
                return new ApplicationProfileRegistrationCheckResult(
                    ApplicationProfileRegistrationCheckStatus.NoProfilesDiscovered,
                    message,
                    discovery.Diagnostics);
            }

            var refreshRequired = candidates.Any(candidate =>
            {
                var entry = registry.Profiles.FirstOrDefault(profile => SameIdentity(profile, candidate));
                return entry is null
                    || entry.Registration.State != AccessProfileRegistrationState.Registered
                    || !string.Equals(
                        entry.Source.Fingerprint,
                        ComputeFingerprint(candidate),
                        StringComparison.OrdinalIgnoreCase);
            }) || registry.Profiles.Any(entry =>
                !candidates.Any(candidate => SameIdentity(entry, candidate)));

            var status = refreshRequired
                ? ApplicationProfileRegistrationCheckStatus.RefreshRequired
                : ApplicationProfileRegistrationCheckStatus.Current;
            var resultMessage = refreshRequired
                ? "Access profile registration is missing, stale, unavailable, or contains entries for packages that are no longer installed."
                : "Access profile registrations are current.";
            logger.LogInformation(
                "Application profile registration check completed. Status={Status}, CandidateCount={CandidateCount}, RegistryEntryCount={RegistryEntryCount}",
                status,
                candidates.Length,
                registry.Profiles.Count);
            return new ApplicationProfileRegistrationCheckResult(
                status,
                resultMessage,
                discovery.Diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            const string message = "Access profile registration status could not be determined.";
            var diagnostic = CreateDiagnostic(
                "Profile.Registry.Check.OperationFailed",
                LogLevel.Error,
                message,
                ex.Message);
            coreContext.Diagnostics.Writer.Add(diagnostic);
            logger.LogError(ex, "Application profile registration check failed.");
            return new ApplicationProfileRegistrationCheckResult(
                ApplicationProfileRegistrationCheckStatus.Failed,
                message,
                [diagnostic]);
        }
    }

    public async Task<ApplicationProfileRegistrationRefreshResult> RefreshProfileRegistrationAsync(
        AccessProfileCandidateKey profile,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        using var operationCancellation = new CoreOperationCancellationScope(
            coreContext.TimeProvider,
            timeout,
            cancellationToken);

        try
        {
            return await RefreshProfileRegistrationCoreAsync(
                profile,
                operationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (operationCancellation.DeadlineExpired)
        {
            var message = $"Application profile registration refresh timed out after {timeout}.";
            logger.LogWarning(
                "Application profile registration refresh timed out. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, Timeout={Timeout}",
                profile.ProfileId,
                profile.ProfileVersion,
                timeout);
            return new ApplicationProfileRegistrationRefreshResult(
                ApplicationProfileRegistrationRefreshStatus.TimedOut,
                null,
                message);
        }
        catch (Exception ex)
        {
            var message = $"Application profile registration refresh failed for '{profile.ProfileId}' version '{profile.ProfileVersion}'.";
            PublishDiagnostic(
                "Profile.Registry.Refresh.OperationFailed",
                LogLevel.Error,
                message,
                ex.Message);
            logger.LogError(
                ex,
                "Application profile registration refresh operation failed. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}",
                profile.ProfileId,
                profile.ProfileVersion);
            return new ApplicationProfileRegistrationRefreshResult(
                ApplicationProfileRegistrationRefreshStatus.Error,
                null,
                message);
        }
    }

    private async Task<ApplicationProfileRegistrationRefreshResult> RefreshProfileRegistrationCoreAsync(
        AccessProfileCandidateKey profile,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Refreshing application profile registration. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, SourceKind={SourceKind}, SourcePath={SourcePath}",
            profile.ProfileId,
            profile.ProfileVersion,
            profile.SourceKind,
            profile.SourcePath);

        var current = await registryStore.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var discovery = pluginLoader.Discover(discoveryOptions);
        var candidate = discovery.Candidates.FirstOrDefault(candidate => Matches(candidate, profile));
        var timestamp = coreContext.TimeProvider.GetUtcNow();

        if (candidate is null)
        {
            var registryWithoutProfile = RemoveRegistryEntry(current, profile, timestamp);
            await registryStore.SaveAsync(registryWithoutProfile, cancellationToken).ConfigureAwait(false);

            var message = $"Access profile '{profile.ProfileId}' version '{profile.ProfileVersion}' was not discovered during application profile registration refresh.";
            PublishDiagnostic(
                "Profile.Registry.Refresh.NotFound",
                LogLevel.Error,
                message,
                $"SourceKind={profile.SourceKind}; SourcePath={profile.SourcePath}");
            logger.LogError(
                "Application profile registration refresh target was not discovered. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, SourceKind={SourceKind}, SourcePath={SourcePath}",
                profile.ProfileId,
                profile.ProfileVersion,
                profile.SourceKind,
                profile.SourcePath);

            return new ApplicationProfileRegistrationRefreshResult(
                ApplicationProfileRegistrationRefreshStatus.CompletedWithErrors,
                null,
                message);
        }

        var result = await ResolveCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
        var updated = UpsertRegistryEntry(current, result.Entry!, timestamp);
        await registryStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<ApplicationProfileRegistrationRefreshAllResult> RefreshAllProfileRegistrationsAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var operationCancellation = new CoreOperationCancellationScope(
            coreContext.TimeProvider,
            timeout,
            cancellationToken);
        var registryBeforeRefresh = ApplicationProfileRegistry.Empty(coreContext.TimeProvider.GetUtcNow());

        try
        {
            return await RefreshAllProfileRegistrationsCoreAsync(
                operationCancellation.Token,
                registry => registryBeforeRefresh = registry).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (operationCancellation.DeadlineExpired)
        {
            var message = $"Application profile registration refresh-all timed out after {timeout}.";
            logger.LogWarning(
                "Application profile registration refresh-all timed out. Timeout={Timeout}",
                timeout);
            return new ApplicationProfileRegistrationRefreshAllResult(
                ApplicationProfileRegistrationRefreshStatus.TimedOut,
                registryBeforeRefresh,
                Array.Empty<ApplicationProfileRegistrationRefreshResult>(),
                message);
        }
        catch (Exception ex)
        {
            var message = "Application profile registration refresh-all failed.";
            PublishDiagnostic(
                "Profile.Registry.RefreshAll.OperationFailed",
                LogLevel.Error,
                message,
                ex.Message);
            logger.LogError(ex, "Application profile registration refresh-all operation failed.");
            return new ApplicationProfileRegistrationRefreshAllResult(
                ApplicationProfileRegistrationRefreshStatus.Error,
                ApplicationProfileRegistry.Empty(coreContext.TimeProvider.GetUtcNow()),
                Array.Empty<ApplicationProfileRegistrationRefreshResult>(),
                message);
        }
    }

    private async Task<ApplicationProfileRegistrationRefreshAllResult> RefreshAllProfileRegistrationsCoreAsync(
        CancellationToken cancellationToken,
        Action<ApplicationProfileRegistry> registryLoaded)
    {
        logger.LogInformation("Refreshing all application profile registrations.");

        var current = await registryStore.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        registryLoaded(current);
        var discovery = pluginLoader.Discover(discoveryOptions);
        var timestamp = coreContext.TimeProvider.GetUtcNow();
        var results = new List<ApplicationProfileRegistrationRefreshResult>();
        var entries = new List<AccessProfileRegistryEntry>();

        foreach (var candidate in discovery.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await ResolveCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (result.Entry is not null)
            {
                entries.Add(result.Entry);
            }
        }

        var registry = new ApplicationProfileRegistry(
            ApplicationProfileRegistry.CurrentSchemaVersion,
            current.CreatedAt,
            timestamp,
            entries);
        await registryStore.SaveAsync(registry, cancellationToken).ConfigureAwait(false);

        var status = results.Any(result => result.Status != ApplicationProfileRegistrationRefreshStatus.Completed
            || result.Entry?.Registration.State == AccessProfileRegistrationState.Error)
            ? ApplicationProfileRegistrationRefreshStatus.CompletedWithErrors
            : ApplicationProfileRegistrationRefreshStatus.Completed;

        logger.LogInformation(
            "Application profile registration refresh-all completed. ProfileCount={ProfileCount}, Status={Status}",
            results.Count,
            status);

        return new ApplicationProfileRegistrationRefreshAllResult(
            status,
            registry,
            results,
            status == ApplicationProfileRegistrationRefreshStatus.Completed
                ? "Application profile registry refreshed."
                : "Application profile registry refreshed with profile errors.");
    }

    private async Task<ApplicationProfileRegistrationRefreshResult> ResolveCandidateAsync(
        AccessProfileCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (candidate.State != AccessProfileCandidateState.Discovered)
        {
            var message = $"Access profile '{candidate.Info.ProfileId}' version '{candidate.Info.Version}' is not a valid discovered candidate.";
            PublishDiagnostic(
                "Profile.Registry.Refresh.CandidateInvalid",
                LogLevel.Error,
                message,
                $"CandidateState={candidate.State}; SourceKind={candidate.SourceKind}; SourcePath={candidate.SourcePath}");

            var invalidCandidateEntry = CreateEntry(
                candidate,
                AccessProfileRegistrationState.Error,
                AccessProfileCapabilities.Empty,
                message);
            return new ApplicationProfileRegistrationRefreshResult(
                ApplicationProfileRegistrationRefreshStatus.CompletedWithErrors,
                invalidCandidateEntry,
                message);
        }

        var resolve = await pluginLoader.ResolvePluginAsync(
            candidate,
            cancellationToken).ConfigureAwait(false);
        if (resolve.State != AccessProfilePluginResolveState.Resolved || resolve.Lease is null)
        {
            var message = $"Access profile '{candidate.Info.ProfileId}' version '{candidate.Info.Version}' could not be loaded for registration.";
            var loadErrorEntry = CreateEntry(
                candidate,
                AccessProfileRegistrationState.Error,
                AccessProfileCapabilities.Empty,
                message);
            return new ApplicationProfileRegistrationRefreshResult(
                ApplicationProfileRegistrationRefreshStatus.CompletedWithErrors,
                loadErrorEntry,
                message);
        }

        AccessProfileRegistrationResult registration;
        try
        {
            await using var lease = resolve.Lease;
            registration = await lease.Plugin.RegisterAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = $"Access profile '{candidate.Info.ProfileId}' version '{candidate.Info.Version}' failed during registration.";
            PublishDiagnostic(
                "Profile.Registry.Refresh.RegisterFailed",
                LogLevel.Error,
                message,
                ex.Message);
            logger.LogError(
                ex,
                "Access profile registration failed. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}",
                candidate.Info.ProfileId,
                candidate.Info.Version);

            var resolveErrorEntry = CreateEntry(
                candidate,
                AccessProfileRegistrationState.Error,
                AccessProfileCapabilities.Empty,
                message);
            return new ApplicationProfileRegistrationRefreshResult(
                ApplicationProfileRegistrationRefreshStatus.CompletedWithErrors,
                resolveErrorEntry,
                message);
        }

        var validation = validator.Validate(registration);
        if (!validation.IsValid)
        {
            var message = $"Access profile '{candidate.Info.ProfileId}' version '{candidate.Info.Version}' returned invalid capability data.";
            PublishDiagnostic(
                "Profile.Registry.Refresh.InvalidCapabilities",
                LogLevel.Error,
                message,
                string.Join(Environment.NewLine, validation.Errors));

            var invalidDataEntry = CreateEntry(
                candidate,
                AccessProfileRegistrationState.Error,
                AccessProfileCapabilities.Empty,
                message);
            return new ApplicationProfileRegistrationRefreshResult(
                ApplicationProfileRegistrationRefreshStatus.CompletedWithErrors,
                invalidDataEntry,
                message);
        }

        var entryStatus = registration.State == AccessProfileRegistrationState.Error
            ? ApplicationProfileRegistrationRefreshStatus.CompletedWithErrors
            : ApplicationProfileRegistrationRefreshStatus.Completed;
        var entry = CreateEntry(
            candidate,
            registration.State,
            registration.Capabilities,
            registration.Message);

        logger.LogInformation(
            "Access profile registration completed. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, RegistrationState={RegistrationState}",
            candidate.Info.ProfileId,
            candidate.Info.Version,
            registration.State);

        return new ApplicationProfileRegistrationRefreshResult(
            entryStatus,
            entry,
            registration.Message);
    }

    private AccessProfileRegistryEntry CreateEntry(
        AccessProfileCandidate candidate,
        AccessProfileRegistrationState state,
        AccessProfileCapabilities capabilities,
        string? message)
    {
        return new AccessProfileRegistryEntry(
            candidate.Info.ProfileId,
            candidate.Info.Version,
            candidate.Info.DisplayName,
            new AccessProfileRegistrySource(
                candidate.SourceKind.ToString(),
                candidate.SourcePath,
                ComputeFingerprint(candidate)),
            new AccessProfileRegistryRegistration(
                state,
                coreContext.TimeProvider.GetUtcNow(),
                message),
            capabilities);
    }

    private ApplicationProfileRegistry UpsertRegistryEntry(
        ApplicationProfileRegistry registry,
        AccessProfileRegistryEntry entry,
        DateTimeOffset updatedAt)
    {
        var profiles = registry.Profiles
            .Where(existing => !SameIdentity(existing, entry))
            .Append(entry)
            .OrderBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.ProfileVersion, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.Source.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.Source.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return registry with
        {
            UpdatedAt = updatedAt,
            Profiles = profiles
        };
    }

    private static ApplicationProfileRegistry RemoveRegistryEntry(
        ApplicationProfileRegistry registry,
        AccessProfileCandidateKey key,
        DateTimeOffset updatedAt)
    {
        return registry with
        {
            UpdatedAt = updatedAt,
            Profiles = registry.Profiles
                .Where(entry => !Matches(entry, key))
                .ToArray()
        };
    }

    private static bool SameIdentity(AccessProfileRegistryEntry left, AccessProfileRegistryEntry right)
    {
        return string.Equals(left.ProfileId, right.ProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ProfileVersion, right.ProfileVersion, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Source.Kind, right.Source.Kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Source.Path, right.Source.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameIdentity(AccessProfileRegistryEntry entry, AccessProfileCandidate candidate)
    {
        return string.Equals(entry.ProfileId, candidate.Info.ProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.ProfileVersion, candidate.Info.Version, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Source.Kind, candidate.SourceKind.ToString(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Source.Path, candidate.SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasDiscoveryFailure(AccessProfileDiscoveryResult discovery)
    {
        return discovery.Candidates.Any(candidate => candidate.State != AccessProfileCandidateState.Discovered)
            || discovery.Diagnostics.Any(diagnostic =>
                !string.Equals(
                    diagnostic.Code,
                    "Profile.Discovery.Directory.NotFound",
                    StringComparison.Ordinal));
    }

    private static bool Matches(AccessProfileCandidate candidate, AccessProfileCandidateKey key)
    {
        return string.Equals(candidate.Info.ProfileId, key.ProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Info.Version, key.ProfileVersion, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.SourceKind.ToString(), key.SourceKind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.SourcePath, key.SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(AccessProfileRegistryEntry entry, AccessProfileCandidateKey key)
    {
        return string.Equals(entry.ProfileId, key.ProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.ProfileVersion, key.ProfileVersion, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Source.Kind, key.SourceKind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Source.Path, key.SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeFingerprint(AccessProfileCandidate candidate)
    {
        if (candidate.SourceKind == AccessProfileSourceKind.BuiltIn)
        {
            return Sha256($"built-in|{candidate.Info.ProfileId}|{candidate.Info.Version}|{candidate.SourcePath}");
        }

        var builder = new StringBuilder();
        builder.Append("directory|");
        builder.Append(Path.GetFullPath(candidate.SourcePath));
        builder.Append('|');
        AppendFileHash(builder, Path.Combine(candidate.SourcePath, "manifest.json"));
        builder.Append('|');
        if (candidate.EntryPoint is not null)
        {
            AppendFileHash(builder, candidate.EntryPoint.AssemblyPath);
        }

        AppendNestedManifestHashes(builder, candidate.SourcePath);

        return Sha256(builder.ToString());
    }

    private static void AppendNestedManifestHashes(StringBuilder builder, string profileRootPath)
    {
        string[] manifestPaths;
        try
        {
            manifestPaths = Directory
                .EnumerateFiles(
                    profileRootPath,
                    "manifest.json",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = false,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    })
                .Where(path => !string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(Path.Combine(profileRootPath, "manifest.json")),
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetRelativePath(profileRootPath, path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            builder.Append("|nested-manifests-unreadable:");
            builder.Append(ex.GetType().Name);
            return;
        }

        foreach (var manifestPath in manifestPaths)
        {
            builder.Append('|');
            builder.Append(Path.GetRelativePath(profileRootPath, manifestPath).Replace('\\', '/'));
            builder.Append(':');
            AppendFileHash(builder, manifestPath);
        }
    }

    private static void AppendFileHash(StringBuilder builder, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                builder.Append("missing:");
                builder.Append(path);
                return;
            }

            builder.Append(path);
            builder.Append(':');
            builder.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            builder.Append("unreadable:");
            builder.Append(path);
            builder.Append(':');
            builder.Append(ex.GetType().Name);
        }
    }

    private static string Sha256(string value)
    {
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private void PublishDiagnostic(
        string code,
        LogLevel level,
        string message,
        string? technicalDetail)
    {
        coreContext.Diagnostics.Writer.Add(CreateDiagnostic(code, level, message, technicalDetail));
    }

    private Diagnostic CreateDiagnostic(
        string code,
        LogLevel level,
        string message,
        string? technicalDetail)
    {
        return new Diagnostic(
            code,
            DiagnosticCategory.Configuration,
            level,
            DiagnosticSource.AccessProfileRegistry,
            DiagnosticPhase.ProfileValidation,
            message,
            new DiagnosticCorrelation(),
            coreContext.TimeProvider.GetUtcNow(),
            TechnicalDetail: technicalDetail);
    }
}
