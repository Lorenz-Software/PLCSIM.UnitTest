using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.Core.AccessProfiles;

public interface IApplicationProfileSelectionService
{
    Task<ApplicationProfileProjectSourceSelectionResult> EvaluateProjectSourceAsync(
        string projectSource,
        CancellationToken cancellationToken);
}

public sealed class ApplicationProfileSelectionService : IApplicationProfileSelectionService
{
    private readonly AccessProfilePluginLoader pluginLoader;
    private readonly AccessProfileDiscoveryOptions discoveryOptions;
    private readonly IApplicationProfileRegistryStore registryStore;
    private readonly ILogger<ApplicationProfileSelectionService> logger;
    private readonly AccessProfileLoaderDiagnosticReporter diagnosticReporter;

    public ApplicationProfileSelectionService(
        AccessProfilePluginLoader pluginLoader,
        AccessProfileDiscoveryOptions discoveryOptions,
        IApplicationProfileRegistryStore registryStore,
        CoreServiceContext coreContext)
    {
        this.pluginLoader = pluginLoader ?? throw new ArgumentNullException(nameof(pluginLoader));
        this.discoveryOptions = discoveryOptions ?? throw new ArgumentNullException(nameof(discoveryOptions));
        this.registryStore = registryStore ?? throw new ArgumentNullException(nameof(registryStore));
        ArgumentNullException.ThrowIfNull(coreContext);

        logger = coreContext.LoggerFactory.CreateLogger<ApplicationProfileSelectionService>()
            ?? NullLogger<ApplicationProfileSelectionService>.Instance;
        diagnosticReporter = new AccessProfileLoaderDiagnosticReporter(
            logger,
            coreContext.TimeProvider,
            coreContext.Diagnostics.Writer);
    }

    public async Task<ApplicationProfileProjectSourceSelectionResult> EvaluateProjectSourceAsync(
        string projectSource,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<Diagnostic>();
        var registry = await registryStore.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var discovery = pluginLoader.Discover(discoveryOptions);
        var selections = new List<ApplicationProfileProjectSourceSelectionEntry>();

        foreach (var profile in registry.Profiles.Where(profile =>
            profile.Registration.State == AccessProfileRegistrationState.Registered))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = discovery.Candidates.FirstOrDefault(candidate => Matches(profile, candidate));
            if (candidate is null)
            {
                continue;
            }

            var resolve = await pluginLoader.ResolvePluginAsync(
                candidate,
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(resolve.Diagnostics);
            if (resolve.State != AccessProfilePluginResolveState.Resolved || resolve.Lease is null)
            {
                continue;
            }

            try
            {
                await using var lease = resolve.Lease;
                selections.Add(new ApplicationProfileProjectSourceSelectionEntry(
                    profile,
                    lease.Plugin.EvaluateProjectSource(projectSource)));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var diagnostic = diagnosticReporter.Create(
                    "Profile.Selection.ProjectSourceEvaluationFailed",
                    DiagnosticCategory.Configuration,
                    LogLevel.Error,
                    $"The access profile '{profile.ProfileId}' failed while evaluating the selected project source.",
                    ex.Message);
                diagnostics.Add(diagnostic);
                diagnosticReporter.Publish(diagnostic);
                logger.LogError(
                    ex,
                    "Access profile project-source evaluation failed. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}",
                    profile.ProfileId,
                    profile.ProfileVersion);
                selections.Add(new ApplicationProfileProjectSourceSelectionEntry(
                    profile,
                    new AccessProfileProjectSourceEvaluation(
                        AccessProfileProjectSourceEvaluationState.Error,
                        diagnostic.Message)));
            }
        }

        return new ApplicationProfileProjectSourceSelectionResult(selections, diagnostics);
    }

    private static bool Matches(
        AccessProfileRegistryEntry profile,
        AccessProfileCandidate candidate)
    {
        return candidate.State == AccessProfileCandidateState.Discovered
            && string.Equals(profile.ProfileId, candidate.Info.ProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.ProfileVersion, candidate.Info.Version, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.Source.Kind, candidate.SourceKind.ToString(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.Source.Path, candidate.SourcePath, StringComparison.OrdinalIgnoreCase);
    }
}
