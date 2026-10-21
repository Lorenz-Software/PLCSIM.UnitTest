using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.PluginLoading;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core;
using System.Text.Json;

namespace PLCSIM.UnitTest.Core.AccessProfiles;

/// <summary>
/// Siemens-independent access profile loader for application workflow composition.
/// </summary>
public sealed class AccessProfilePluginLoader
{
    private const int SupportedManifestSchemaVersion = 1;
    private const string SupportedAccessProfileContractVersion = "2.0.0";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IReadOnlyList<IAccessProfilePlugin> builtInPlugins;
    private readonly CoreServiceContext coreContext;
    private readonly AccessProfileDataDirectoryResolver profileDataDirectoryResolver;
    private readonly ILogger<AccessProfilePluginLoader> logger;
    private readonly AccessProfileLoaderDiagnosticReporter diagnosticReporter;

    public AccessProfilePluginLoader(
        CoreServiceContext coreContext,
        IEnumerable<IAccessProfilePlugin> builtInPlugins)
    {
        ArgumentNullException.ThrowIfNull(coreContext);

        this.builtInPlugins = builtInPlugins.ToArray();
        this.coreContext = coreContext;
        this.profileDataDirectoryResolver = new AccessProfileDataDirectoryResolver(coreContext.AccessProfileData);
        this.logger = coreContext.LoggerFactory.CreateLogger<AccessProfilePluginLoader>()
            ?? NullLogger<AccessProfilePluginLoader>.Instance;
        diagnosticReporter = new AccessProfileLoaderDiagnosticReporter(
            this.logger,
            coreContext.TimeProvider,
            coreContext.Diagnostics.Writer);
    }

    public AccessProfileDiscoveryResult Discover(AccessProfileDiscoveryOptions options)
    {
        logger.LogInformation(
            "Discovering access profiles. IncludeBuiltInProfiles={IncludeBuiltInProfiles}, ProfileDiscoveryDirectory={ProfileDiscoveryDirectory}",
            options.IncludeBuiltInProfiles,
            options.ProfileDiscoveryDirectory);

        var candidates = new List<AccessProfileCandidate>();
        var diagnostics = new List<Diagnostic>();

        if (options.IncludeBuiltInProfiles)
        {
            foreach (var plugin in builtInPlugins)
            {
                candidates.Add(CreateBuiltInCandidate(plugin.Info));
            }
        }

        if (!string.IsNullOrWhiteSpace(options.ProfileDiscoveryDirectory))
        {
            DiscoverDirectoryPackages(options.ProfileDiscoveryDirectory, candidates, diagnostics);
        }

        candidates = candidates
            .OrderBy(candidate => candidate.SourceKind)
            .ThenBy(candidate => candidate.Info.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(candidate => ParseVersion(candidate.Info.Version))
            .ThenBy(candidate => candidate.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        diagnosticReporter.PublishRange(diagnostics);

        logger.LogInformation(
            "Access profile discovery finished. CandidateCount={CandidateCount}, DiagnosticCount={DiagnosticCount}",
            candidates.Count,
            diagnostics.Count);

        return new AccessProfileDiscoveryResult(candidates, diagnostics);
    }

    public async Task<AccessProfileLoadResult> LoadAsync(
        AccessProfileSessionRequest request,
        AccessProfileDiscoveryResult discovery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        logger.LogInformation(
            "Loading access profile. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, CandidateCount={CandidateCount}",
            request.ProfileId,
            request.ProfileVersion,
            discovery.Candidates.Count);

        var diagnostics = new List<Diagnostic>();
        var matches = discovery.Candidates
            .Where(candidate => candidate.State == AccessProfileCandidateState.Discovered)
            .Where(candidate => string.Equals(candidate.Info.ProfileId, request.ProfileId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Load.NotFound",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                $"No access profile with id '{request.ProfileId}' was discovered."));
            logger.LogError("Access profile load failed because no matching profile was discovered. ProfileId={ProfileId}", request.ProfileId);
            diagnosticReporter.PublishRange(diagnostics);
            return new AccessProfileLoadResult(AccessProfileLoadState.NotFound, null, null, diagnostics);
        }

        if (!string.IsNullOrWhiteSpace(request.ProfileVersion))
        {
            matches = matches
                .Where(candidate => string.Equals(candidate.Info.Version, request.ProfileVersion, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length == 0)
            {
                diagnostics.Add(diagnosticReporter.Create(
                    "Profile.Load.VersionNotFound",
                    DiagnosticCategory.Configuration,
                    LogLevel.Error,
                    $"No access profile with id '{request.ProfileId}' and version '{request.ProfileVersion}' was discovered."));
                logger.LogError(
                    "Access profile load failed because no matching profile version was discovered. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}",
                    request.ProfileId,
                    request.ProfileVersion);
                diagnosticReporter.PublishRange(diagnostics);
                return new AccessProfileLoadResult(AccessProfileLoadState.NotFound, null, null, diagnostics);
            }
        }
        else
        {
            matches = SelectLatestVersion(matches);
        }

        if (matches.Length > 1)
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Load.AmbiguousProfileVersion",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                $"Multiple access profiles with id '{request.ProfileId}' and version '{matches[0].Info.Version}' were discovered."));
            logger.LogError(
                "Access profile load failed because multiple matching profile versions were discovered. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, MatchCount={MatchCount}",
                request.ProfileId,
                matches[0].Info.Version,
                matches.Length);
            diagnosticReporter.PublishRange(diagnostics);
            return new AccessProfileLoadResult(AccessProfileLoadState.Failed, null, matches[0].Info, diagnostics);
        }

        var candidate = matches[0];
        var pluginLease = await ResolvePluginCoreAsync(
            candidate,
            diagnostics,
            cancellationToken).ConfigureAwait(false);
        if (pluginLease is null)
        {
            logger.LogError(
                "Access profile load failed because the selected plugin is unavailable or invalid. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, SourceKind={SourceKind}, SourcePath={SourcePath}",
                candidate.Info.ProfileId,
                candidate.Info.Version,
                candidate.SourceKind,
                candidate.SourcePath);
            diagnosticReporter.PublishRange(diagnostics);
            return new AccessProfileLoadResult(AccessProfileLoadState.Failed, null, candidate.Info, diagnostics);
        }

        var leaseTransferred = false;
        IAccessProfileSession? rejectedSession = null;

        try
        {
            var plugin = pluginLease.Plugin;
            var load = await plugin.LoadAsync(request, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(load.Diagnostics);
            rejectedSession = load.Session;

            if (load.IsSuccessful && rejectedSession is null)
            {
                diagnostics.Add(diagnosticReporter.Create(
                    "Profile.Load.InvalidSessionResult",
                    DiagnosticCategory.Environment,
                    LogLevel.Error,
                    $"The access profile '{candidate.Info.ProfileId}' reported a successful session load without returning a session."));
                logger.LogError(
                    "Access profile returned an invalid successful session load result. ProfileId={ProfileId}",
                    candidate.Info.ProfileId);
                diagnosticReporter.PublishRange(diagnostics);
                return new AccessProfileLoadResult(AccessProfileLoadState.Failed, null, candidate.Info, diagnostics);
            }

            if (!load.IsSuccessful || rejectedSession is null)
            {
                if (rejectedSession is not null)
                {
                    await DisposeRejectedSessionAsync(rejectedSession, candidate, diagnostics).ConfigureAwait(false);
                    rejectedSession = null;
                }

                logger.LogError(
                    "Access profile session load failed. ProfileId={ProfileId}, Message={Message}, DiagnosticCount={DiagnosticCount}",
                    candidate.Info.ProfileId,
                    load.Message,
                    load.Diagnostics.Count);
                diagnosticReporter.PublishRange(diagnostics);
                return new AccessProfileLoadResult(AccessProfileLoadState.Failed, null, candidate.Info, diagnostics);
            }

            var serviceDiagnostics = ValidateServices(rejectedSession);
            diagnostics.AddRange(serviceDiagnostics);
            if (serviceDiagnostics.Count > 0)
            {
                await DisposeRejectedSessionAsync(rejectedSession, candidate, diagnostics).ConfigureAwait(false);
                rejectedSession = null;
                logger.LogError(
                    "Access profile service validation failed. ProfileId={ProfileId}, DiagnosticCount={DiagnosticCount}",
                    candidate.Info.ProfileId,
                    serviceDiagnostics.Count);
                diagnosticReporter.PublishRange(diagnostics);
                return new AccessProfileLoadResult(AccessProfileLoadState.Failed, null, candidate.Info, diagnostics);
            }

            var profile = new PackageOwnedAccessProfileSession(rejectedSession, pluginLease);
            rejectedSession = null;
            leaseTransferred = true;

            diagnosticReporter.PublishRange(diagnostics);
            logger.LogInformation(
                "Access profile loaded. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}",
                candidate.Info.ProfileId,
                candidate.Info.Version);
            return new AccessProfileLoadResult(AccessProfileLoadState.Loaded, profile, candidate.Info, diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (rejectedSession is not null)
            {
                await DisposeRejectedSessionAsync(rejectedSession, candidate, diagnostics).ConfigureAwait(false);
                rejectedSession = null;
            }

            logger.LogInformation(
                "Access profile session load was cancelled. ProfileId={ProfileId}",
                candidate.Info.ProfileId);
            throw;
        }
        catch (Exception ex)
        {
            if (rejectedSession is not null)
            {
                await DisposeRejectedSessionAsync(rejectedSession, candidate, diagnostics).ConfigureAwait(false);
                rejectedSession = null;
            }

            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Load.SessionLoadFailed",
                DiagnosticCategory.Environment,
                LogLevel.Error,
                $"The access profile '{candidate.Info.ProfileId}' session could not be loaded.",
                ex.Message));
            logger.LogError(ex, "Access profile session load failed. ProfileId={ProfileId}", candidate.Info.ProfileId);
            diagnosticReporter.PublishRange(diagnostics);
            return new AccessProfileLoadResult(AccessProfileLoadState.Failed, null, candidate.Info, diagnostics);
        }
        finally
        {
            if (!leaseTransferred)
            {
                await DisposePluginLeaseAsync(
                    pluginLease,
                    candidate,
                    diagnostics,
                    publishImmediately: true).ConfigureAwait(false);
            }
        }
    }

    internal async Task<AccessProfilePluginResolveResult> ResolvePluginAsync(
        AccessProfileCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        logger.LogInformation(
            "Resolving access profile plugin. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, SourceKind={SourceKind}, SourcePath={SourcePath}",
            candidate.Info.ProfileId,
            candidate.Info.Version,
            candidate.SourceKind,
            candidate.SourcePath);

        var diagnostics = new List<Diagnostic>();
        var lease = await ResolvePluginCoreAsync(
            candidate,
            diagnostics,
            cancellationToken).ConfigureAwait(false);
        diagnosticReporter.PublishRange(diagnostics);
        if (lease is null)
        {
            logger.LogError(
                "Access profile plugin resolve failed. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}",
                candidate.Info.ProfileId,
                candidate.Info.Version);
            return new AccessProfilePluginResolveResult(
                AccessProfilePluginResolveState.Failed,
                null,
                candidate.Info,
                diagnostics);
        }

        logger.LogInformation(
            "Access profile plugin resolved. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}",
            candidate.Info.ProfileId,
            candidate.Info.Version);
        return new AccessProfilePluginResolveResult(
            AccessProfilePluginResolveState.Resolved,
            lease,
            candidate.Info,
            diagnostics);
    }

    private IReadOnlyList<Diagnostic> ValidateServices(IAccessProfileSession profile)
    {
        var diagnostics = new List<Diagnostic>();

        AddMissingServiceDiagnostic(profile.Configuration, nameof(profile.Configuration), diagnostics);
        AddMissingServiceDiagnostic(profile.Project, nameof(profile.Project), diagnostics);
        AddMissingServiceDiagnostic(profile.PlcControl, nameof(profile.PlcControl), diagnostics);
        AddMissingServiceDiagnostic(profile.UnitTestLifecycle, nameof(profile.UnitTestLifecycle), diagnostics);
        AddMissingServiceDiagnostic(profile.UnitTestEventStream, nameof(profile.UnitTestEventStream), diagnostics);

        return diagnostics;
    }

    private async Task DisposeRejectedSessionAsync(
        IAccessProfileSession profile,
        AccessProfileCandidate candidate,
        List<Diagnostic> diagnostics)
    {
        try
        {
            await profile.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Load.RejectedSessionDisposeFailed",
                DiagnosticCategory.Environment,
                LogLevel.Error,
                $"The rejected access profile session '{candidate.Info.ProfileId}' could not be disposed.",
                ex.Message));
            logger.LogError(
                ex,
                "Rejected access profile session dispose failed. ProfileId={ProfileId}",
                candidate.Info.ProfileId);
        }
    }

    private async Task DisposePluginLeaseAsync(
        AccessProfilePluginLease lease,
        AccessProfileCandidate candidate,
        List<Diagnostic> diagnostics,
        bool publishImmediately)
    {
        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var diagnostic = diagnosticReporter.Create(
                "Profile.Load.PluginDisposeFailed",
                DiagnosticCategory.Environment,
                LogLevel.Error,
                $"The access profile plugin '{candidate.Info.ProfileId}' could not be disposed.",
                ex.Message);
            diagnostics.Add(diagnostic);
            if (publishImmediately)
            {
                diagnosticReporter.Publish(diagnostic);
            }

            logger.LogError(
                ex,
                "Access profile plugin dispose failed. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}",
                candidate.Info.ProfileId,
                candidate.Info.Version);
        }
    }

    private static AccessProfileCandidate CreateBuiltInCandidate(AccessProfileInfo info)
    {
        return new AccessProfileCandidate(
            info,
            AccessProfileSourceKind.BuiltIn,
            $"built-in:{info.ProfileId}@{info.Version}",
            null,
            AccessProfileCandidateState.Discovered,
            Array.Empty<Diagnostic>());
    }

    private async Task<AccessProfilePluginLease?> ResolvePluginCoreAsync(
        AccessProfileCandidate candidate,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (candidate.SourceKind == AccessProfileSourceKind.BuiltIn)
        {
            var builtInPlugin = builtInPlugins.FirstOrDefault(plugin => Equals(plugin.Info, candidate.Info));
            if (builtInPlugin is null)
            {
                diagnostics.Add(diagnosticReporter.Create(
                    "Profile.Load.PluginUnavailable",
                    DiagnosticCategory.Environment,
                    LogLevel.Error,
                    $"The selected built-in access profile '{candidate.Info.ProfileId}' is no longer available."));
                return null;
            }

            if (!InitializePlugin(builtInPlugin, candidate, diagnostics))
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new AccessProfilePluginLease(
                builtInPlugin,
                packageScope: null,
                ownsPlugin: false);
        }

        if (candidate.EntryPoint is null)
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Load.EntryPointMissing",
                DiagnosticCategory.Environment,
                LogLevel.Error,
                $"The selected access profile '{candidate.Info.ProfileId}' does not define an entry point."));
            return null;
        }

        PluginPackageLoadScope? packageScope = null;
        IAccessProfilePlugin plugin;
        try
        {
            packageScope = new PluginPackageLoadScope(
                GetProfileRootPath(candidate),
                candidate.EntryPoint.AssemblyPath,
                [
                    typeof(UnitTestContractVersions).Assembly,
                    typeof(ILoggerFactory).Assembly
                ]);
            plugin = packageScope.LoadEntryPoint<IAccessProfilePlugin>(
                candidate.EntryPoint.TypeName);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            packageScope?.Dispose();
            throw;
        }
        catch (PluginPackageLoadException ex)
        {
            packageScope?.Dispose();
            diagnostics.Add(CreatePackageLoadDiagnostic(candidate, ex));
            return null;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or BadImageFormatException
            or ArgumentException
            or NotSupportedException)
        {
            packageScope?.Dispose();
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Load.AssemblyLoadFailed",
                DiagnosticCategory.Environment,
                LogLevel.Error,
                $"The selected access profile assembly '{candidate.EntryPoint.AssemblyPath}' could not be loaded.",
                ex.Message));
            return null;
        }

        var lease = new AccessProfilePluginLease(
            plugin,
            packageScope,
            ownsPlugin: true);

        AccessProfileInfo pluginInfo;
        try
        {
            pluginInfo = plugin.Info;
        }
        catch (Exception ex)
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Load.EntryProfileInfoFailed",
                DiagnosticCategory.Environment,
                LogLevel.Error,
                $"The selected access profile entry type '{candidate.EntryPoint.TypeName}' did not provide valid profile information.",
                ex.Message));
            await DisposePluginLeaseAsync(
                lease,
                candidate,
                diagnostics,
                publishImmediately: false).ConfigureAwait(false);
            return null;
        }

        if (!string.Equals(pluginInfo.ProfileId, candidate.Info.ProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(pluginInfo.Version, candidate.Info.Version, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Load.EntryProfileMismatch",
                DiagnosticCategory.Environment,
                LogLevel.Error,
                $"The selected access profile entry type '{candidate.EntryPoint.TypeName}' reports id/version '{pluginInfo.ProfileId}/{pluginInfo.Version}' but the manifest selected '{candidate.Info.ProfileId}/{candidate.Info.Version}'."));
            await DisposePluginLeaseAsync(
                lease,
                candidate,
                diagnostics,
                publishImmediately: false).ConfigureAwait(false);
            return null;
        }

        if (!InitializePlugin(plugin, candidate, diagnostics))
        {
            await DisposePluginLeaseAsync(
                lease,
                candidate,
                diagnostics,
                publishImmediately: false).ConfigureAwait(false);
            return null;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await DisposePluginLeaseAsync(
                lease,
                candidate,
                diagnostics,
                publishImmediately: false).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return lease;
    }

    private Diagnostic CreatePackageLoadDiagnostic(
        AccessProfileCandidate candidate,
        PluginPackageLoadException exception)
    {
        var code = exception.FailureKind switch
        {
            PluginPackageLoadFailureKind.EntryPointTypeNotFound => "Profile.Load.EntryTypeMissing",
            PluginPackageLoadFailureKind.EntryPointTypeInvalid => "Profile.Load.EntryTypeInvalid",
            PluginPackageLoadFailureKind.EntryPointActivationFailed => "Profile.Load.EntryTypeCreateFailed",
            _ => "Profile.Load.AssemblyLoadFailed"
        };

        var message = exception.FailureKind switch
        {
            PluginPackageLoadFailureKind.EntryPointTypeNotFound =>
                $"The selected access profile entry type '{candidate.EntryPoint!.TypeName}' was not found in '{candidate.EntryPoint.AssemblyPath}'.",
            PluginPackageLoadFailureKind.EntryPointTypeInvalid =>
                $"The selected access profile entry type '{candidate.EntryPoint!.TypeName}' does not implement IAccessProfilePlugin.",
            PluginPackageLoadFailureKind.EntryPointActivationFailed =>
                $"The selected access profile entry type '{candidate.EntryPoint!.TypeName}' could not be created.",
            _ =>
                $"The selected access profile assembly '{candidate.EntryPoint!.AssemblyPath}' could not be loaded."
        };

        return diagnosticReporter.Create(
            code,
            DiagnosticCategory.Environment,
            LogLevel.Error,
            message,
            exception.InnerException?.Message ?? exception.Message);
    }

    private bool InitializePlugin(
        IAccessProfilePlugin plugin,
        AccessProfileCandidate candidate,
        List<Diagnostic> diagnostics)
    {
        try
        {
            var profileDataDirectory = profileDataDirectoryResolver.Resolve(candidate);
            Directory.CreateDirectory(profileDataDirectory);
            plugin.Initialize(coreContext.CreateAccessProfileContext(
                GetProfileRootPath(candidate),
                candidate.Info.ProfileId,
                profileDataDirectory));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or ArgumentException
            or IOException
            or UnauthorizedAccessException)
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Load.InitializeFailed",
                DiagnosticCategory.Environment,
                LogLevel.Error,
                $"The selected access profile '{candidate.Info.ProfileId}' could not be initialized.",
                ex.Message));
            return false;
        }
    }

    private static string GetProfileRootPath(AccessProfileCandidate candidate)
    {
        return candidate.SourceKind == AccessProfileSourceKind.DirectoryPackage
            ? Path.GetFullPath(candidate.SourcePath)
            : candidate.SourcePath;
    }

    private void DiscoverDirectoryPackages(
        string profileDiscoveryDirectory,
        List<AccessProfileCandidate> candidates,
        List<Diagnostic> diagnostics)
    {
        string discoveryDirectory;
        try
        {
            discoveryDirectory = Path.GetFullPath(profileDiscoveryDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Discovery.Directory.InvalidPath",
                DiagnosticCategory.Configuration,
                LogLevel.Warning,
                $"The configured access profile discovery directory '{profileDiscoveryDirectory}' is not a valid path.",
                ex.Message));
            return;
        }

        if (!Directory.Exists(discoveryDirectory))
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Discovery.Directory.NotFound",
                DiagnosticCategory.Configuration,
                LogLevel.Warning,
                $"The configured access profile discovery directory '{discoveryDirectory}' does not exist."));
            return;
        }

        string[] packageDirectories;
        try
        {
            packageDirectories = Directory.GetDirectories(discoveryDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Discovery.Directory.Inaccessible",
                DiagnosticCategory.Configuration,
                LogLevel.Warning,
                $"The configured access profile discovery directory '{discoveryDirectory}' could not be read.",
                ex.Message));
            return;
        }

        foreach (var packageDirectory in packageDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(packageDirectory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            DiscoverDirectoryPackage(packageDirectory, manifestPath, candidates, diagnostics);
        }
    }

    private void DiscoverDirectoryPackage(
        string packageDirectory,
        string manifestPath,
        List<AccessProfileCandidate> candidates,
        List<Diagnostic> diagnostics)
    {
        AccessProfileManifest? manifest;
        try
        {
            var json = File.ReadAllText(manifestPath);
            manifest = JsonSerializer.Deserialize<AccessProfileManifest>(json, ManifestJsonOptions);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Discovery.Manifest.InvalidJson",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                $"Access profile manifest '{manifestPath}' is not valid JSON.",
                ex.Message));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Discovery.Manifest.Unreadable",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                $"Access profile manifest '{manifestPath}' could not be read.",
                ex.Message));
            return;
        }

        if (manifest is null)
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Discovery.Manifest.Empty",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                $"Access profile manifest '{manifestPath}' is empty."));
            return;
        }

        var manifestDiagnostics = ValidateManifest(manifest, packageDirectory, manifestPath, out var info, out var entryPoint, out var state);
        diagnostics.AddRange(manifestDiagnostics);

        if (info is null)
        {
            return;
        }

        candidates.Add(new AccessProfileCandidate(
            info,
            AccessProfileSourceKind.DirectoryPackage,
            packageDirectory,
            entryPoint,
            state,
            manifestDiagnostics));
    }

    private IReadOnlyList<Diagnostic> ValidateManifest(
        AccessProfileManifest manifest,
        string packageDirectory,
        string manifestPath,
        out AccessProfileInfo? info,
        out AccessProfileEntryPoint? entryPoint,
        out AccessProfileCandidateState state)
    {
        var diagnostics = new List<Diagnostic>();
        info = null;
        entryPoint = null;
        state = AccessProfileCandidateState.Discovered;

        if (manifest.SchemaVersion != SupportedManifestSchemaVersion)
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Discovery.Manifest.UnsupportedSchemaVersion",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                $"Access profile manifest '{manifestPath}' uses unsupported schema version '{manifest.SchemaVersion}'."));
            return diagnostics;
        }

        if (manifest.Profile is null)
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Discovery.Manifest.ProfileMissing",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                $"Access profile manifest '{manifestPath}' does not define profile metadata."));
            return diagnostics;
        }

        if (string.IsNullOrWhiteSpace(manifest.Profile.Id)
            || string.IsNullOrWhiteSpace(manifest.Profile.Version)
            || string.IsNullOrWhiteSpace(manifest.Profile.DisplayName)
            || string.IsNullOrWhiteSpace(manifest.Profile.ContractVersion))
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Discovery.Manifest.ProfileMetadataInvalid",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                $"Access profile manifest '{manifestPath}' misses required profile metadata."));
            return diagnostics;
        }

        if (!IsValidSemanticVersion(manifest.Profile.Version))
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Discovery.Manifest.ProfileVersionInvalid",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                $"Access profile manifest '{manifestPath}' defines invalid profile version '{manifest.Profile.Version}'."));
            return diagnostics;
        }

        info = new AccessProfileInfo(
            manifest.Profile.Id,
            manifest.Profile.DisplayName,
            manifest.Profile.Version,
            manifest.Profile.Description ?? string.Empty);

        if (!string.Equals(manifest.Profile.ContractVersion, SupportedAccessProfileContractVersion, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Discovery.Manifest.ContractIncompatible",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                $"Access profile manifest '{manifestPath}' requires unsupported access profile contract version '{manifest.Profile.ContractVersion}'. This application requires '{SupportedAccessProfileContractVersion}'. Install a compatible profile package."));
            state = AccessProfileCandidateState.ContractIncompatible;
            return diagnostics;
        }

        if (manifest.EntryPoint is null
            || string.IsNullOrWhiteSpace(manifest.EntryPoint.Assembly)
            || string.IsNullOrWhiteSpace(manifest.EntryPoint.Type))
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Discovery.Manifest.EntryPointInvalid",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                $"Access profile manifest '{manifestPath}' misses required entry point metadata."));
            state = AccessProfileCandidateState.MetadataInvalid;
            return diagnostics;
        }

        if (Path.IsPathRooted(manifest.EntryPoint.Assembly))
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Discovery.Manifest.EntryAssemblyPathInvalid",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                $"Access profile manifest '{manifestPath}' defines an entry assembly path that is not relative."));
            state = AccessProfileCandidateState.MetadataInvalid;
            return diagnostics;
        }

        var fullPackageDirectory = Path.GetFullPath(packageDirectory);
        var assemblyPath = Path.GetFullPath(Path.Combine(fullPackageDirectory, manifest.EntryPoint.Assembly));
        var packagePrefix = fullPackageDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? fullPackageDirectory
            : fullPackageDirectory + Path.DirectorySeparatorChar;
        if (!assemblyPath.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Discovery.Manifest.EntryAssemblyPathInvalid",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                $"Access profile manifest '{manifestPath}' defines an entry assembly path outside the package directory."));
            state = AccessProfileCandidateState.MetadataInvalid;
            return diagnostics;
        }

        entryPoint = new AccessProfileEntryPoint(assemblyPath, manifest.EntryPoint.Type);
        if (!File.Exists(assemblyPath))
        {
            diagnostics.Add(diagnosticReporter.Create(
                "Profile.Discovery.Manifest.EntryAssemblyMissing",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                $"Access profile manifest '{manifestPath}' references missing entry assembly '{assemblyPath}'."));
            state = AccessProfileCandidateState.LoadFailed;
        }

        return diagnostics;
    }

    private static AccessProfileCandidate[] SelectLatestVersion(AccessProfileCandidate[] candidates)
    {
        var latest = candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Version = ParseVersion(candidate.Info.Version)
            })
            .OrderByDescending(item => item.Version)
            .ThenByDescending(item => item.Candidate.Info.Version, StringComparer.OrdinalIgnoreCase)
            .First();

        return candidates
            .Where(candidate => string.Equals(candidate.Info.Version, latest.Candidate.Info.Version, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static Version ParseVersion(string value)
    {
        return Version.TryParse(value, out var version)
            ? version
            : new Version(0, 0, 0);
    }

    private static bool IsValidSemanticVersion(string value)
    {
        if (!Version.TryParse(value, out var version))
        {
            return false;
        }

        return version.Major >= 0
            && version.Minor >= 0
            && version.Build >= 0;
    }

    private void AddMissingServiceDiagnostic(
        object? service,
        string serviceName,
        List<Diagnostic> diagnostics)
    {
        if (service is not null)
        {
            return;
        }

        diagnostics.Add(diagnosticReporter.Create(
            $"Profile.Load.ServiceMissing.{serviceName}",
            DiagnosticCategory.Environment,
            LogLevel.Error,
            $"The loaded access profile does not provide required service '{serviceName}'."));
    }

    private sealed record AccessProfileManifest(
        int SchemaVersion,
        AccessProfileManifestProfile? Profile,
        AccessProfileManifestEntryPoint? EntryPoint);

    private sealed record AccessProfileManifestProfile(
        string? Id,
        string? Version,
        string? DisplayName,
        string? Description,
        string? ContractVersion);

    private sealed record AccessProfileManifestEntryPoint(
        string? Assembly,
        string? Type);
}
