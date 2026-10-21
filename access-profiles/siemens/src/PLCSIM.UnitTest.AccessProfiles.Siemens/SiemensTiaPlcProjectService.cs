using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using System.Security;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensTiaPlcProjectService : SiemensAccessProfileServiceBase, IPlcProjectService
{
    private readonly ITiaOpennessEngineeringBinding binding;
    private readonly ILogger<SiemensTiaPlcProjectService> logger;
    private readonly SiemensProfileConfigurationParser configurationParser = new();
    private readonly SiemensRuntimeTransferTargetFactory runtimeTransferTargetFactory = new();
    private ITiaEngineeringSession? session;
    private string? configuredPlcRefId;
    private IReadOnlyList<TiaProjectTargetRef> currentTargets = Array.Empty<TiaProjectTargetRef>();

    public SiemensTiaPlcProjectService(
        ITiaOpennessEngineeringBinding binding,
        TimeProvider timeProvider,
        ILogger<SiemensTiaPlcProjectService>? logger = null)
        : base(timeProvider)
    {
        this.binding = binding ?? throw new ArgumentNullException(nameof(binding));
        this.logger = logger ?? NullLogger<SiemensTiaPlcProjectService>.Instance;
    }

    public event EventHandler<PlcProjectStateChangedEventArgs>? ProjectStateChangedEventHandler;

    public PlcProjectContext? CurrentProject { get; private set; }

    public async Task<OperationResult<ProjectTargetDiscoveryResult>> DiscoverProjectTargetsAsync(
        PlcProjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepare = await OpenProjectSessionAsync(request, closeExistingSession: false, cancellationToken).ConfigureAwait(false);
        if (!prepare.IsSuccessful)
        {
            return OperationResult<ProjectTargetDiscoveryResult>.Failure(prepare.Message ?? "Siemens TIA engineering session is not available.");
        }

        var projectSession = session!;
        var result = await projectSession.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(null),
            cancellationToken).ConfigureAwait(false);
        currentTargets = result.Targets;
        var candidates = result.Targets.Select(MapProjectTarget).ToArray();
        var selectionOnlyFailure = IsUnselectedDiscoveryResult(result);
        var discoverySucceeded = result.Succeeded || selectionOnlyFailure;
        var diagnostics = selectionOnlyFailure
            ? result.Diagnostics.Where(diagnostic => !IsSelectionDiagnostic(diagnostic, result.Targets.Count)).ToArray()
            : result.Diagnostics;
        var state = discoverySucceeded
            ? candidates.Length == 0 ? TargetDiscoveryState.NoCandidates : TargetDiscoveryState.CandidatesAvailable
            : TargetDiscoveryState.Failed;
        if (!discoverySucceeded)
        {
            PublishDiagnostics(result.Diagnostics);
            return OperationResult<ProjectTargetDiscoveryResult>.Failure(
                $"Siemens TIA target discovery returned {result.Status}.");
        }

        return OperationResult<ProjectTargetDiscoveryResult>.Success(
            new ProjectTargetDiscoveryResult(candidates, state, MapDiagnostics(diagnostics)));
    }

    public async Task<OperationResult<PlcProjectContext>> PrepareProjectAsync(
        PlcProjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepare = await OpenProjectSessionAsync(request, closeExistingSession: false, cancellationToken).ConfigureAwait(false);
        if (!prepare.IsSuccessful || prepare.Value is null)
        {
            return prepare;
        }

        var discovery = await session!.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(configuredPlcRefId),
            cancellationToken).ConfigureAwait(false);
        PublishDiagnostics(discovery.Diagnostics);
        if (!discovery.Succeeded)
        {
            return OperationResult<PlcProjectContext>.Failure(
                $"Siemens TIA project target refresh returned {discovery.Status}.");
        }

        currentTargets = discovery.Targets;
        return prepare;
    }

    public async Task<OperationResult<ProjectTargetResolution>> ResolveProjectTargetAsync(
        PlcProjectContext project,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session is null)
        {
            return OperationResult<ProjectTargetResolution>.Failure("Siemens TIA project is not prepared.");
        }

        var result = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(configuredPlcRefId),
            cancellationToken).ConfigureAwait(false);
        PublishDiagnostics(result.Diagnostics);
        currentTargets = result.Targets;
        var candidates = result.Targets.Select(MapProjectTarget).ToArray();
        var selected = result.SelectedTarget is null ? null : MapProjectTarget(result.SelectedTarget);
        var state = result.Succeeded && selected is not null
            ? TargetResolutionState.Resolved
            : TargetResolutionState.InvalidConfiguration;
        return result.Succeeded
            ? OperationResult<ProjectTargetResolution>.Success(new ProjectTargetResolution(candidates, selected, state))
            : OperationResult<ProjectTargetResolution>.Failure($"Siemens TIA project target resolution returned {result.Status}.");
    }

    public async Task<OperationResult<SafetyProgramAuthorizationRequirementResult>> GetSafetyProgramAuthorizationRequirementAsync(
        PlcProjectContext project,
        ProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session is null)
        {
            return OperationResult<SafetyProgramAuthorizationRequirementResult>.Failure(
                "Siemens TIA project is not prepared.");
        }

        var tiaTarget = ResolveTiaTarget(target);
        if (tiaTarget is null)
        {
            return OperationResult<SafetyProgramAuthorizationRequirementResult>.Failure(
                "Siemens TIA project target is not known.");
        }

        if (session is not ITiaSafetyProgramAuthorizationSession safetySession)
        {
            return OperationResult<SafetyProgramAuthorizationRequirementResult>.NotSupported(
                "The selected TIA Openness binding does not support Safety-program authorization.");
        }

        var result = await safetySession.GetSafetyProgramAuthorizationRequirementAsync(
            tiaTarget,
            cancellationToken).ConfigureAwait(false);
        PublishDiagnostics(result.Diagnostics);
        if (result.Status == TiaOperationStatus.NotSupported)
        {
            return OperationResult<SafetyProgramAuthorizationRequirementResult>.NotSupported(
                "The selected TIA Openness binding does not support Safety-program authorization.");
        }

        if (!result.Succeeded)
        {
            return OperationResult<SafetyProgramAuthorizationRequirementResult>.Failure(
                $"Siemens TIA Safety-program authorization requirement returned {result.Status}.");
        }

        return OperationResult<SafetyProgramAuthorizationRequirementResult>.Success(
            new SafetyProgramAuthorizationRequirementResult(
                result.Requirement == TiaSafetyProgramAuthorizationRequirement.PasswordRequired
                    ? SafetyProgramAuthorizationRequirement.PasswordRequired
                    : SafetyProgramAuthorizationRequirement.NotRequired));
    }

    public async Task<OperationResult<SafetyProgramAuthorizationResult>> AuthorizeSafetyProgramAsync(
        PlcProjectContext project,
        ProjectTargetRef target,
        SecureString password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(password);
        if (session is null)
        {
            return OperationResult<SafetyProgramAuthorizationResult>.Failure(
                "Siemens TIA project is not prepared.");
        }

        var tiaTarget = ResolveTiaTarget(target);
        if (tiaTarget is null)
        {
            return OperationResult<SafetyProgramAuthorizationResult>.Failure(
                "Siemens TIA project target is not known.");
        }

        if (session is not ITiaSafetyProgramAuthorizationSession safetySession)
        {
            return OperationResult<SafetyProgramAuthorizationResult>.NotSupported(
                "The selected TIA Openness binding does not support Safety-program authorization.");
        }

        var result = await safetySession.AuthorizeSafetyProgramAsync(
            tiaTarget,
            password,
            cancellationToken).ConfigureAwait(false);
        PublishDiagnostics(result.Diagnostics);
        if (result.Status == TiaOperationStatus.NotSupported)
        {
            return OperationResult<SafetyProgramAuthorizationResult>.NotSupported(
                "The selected TIA Openness binding does not support Safety-program authorization.");
        }

        return result.Succeeded && result.Authorized
            ? OperationResult<SafetyProgramAuthorizationResult>.Success(
                new SafetyProgramAuthorizationResult(true))
            : OperationResult<SafetyProgramAuthorizationResult>.Failure(
                new SafetyProgramAuthorizationResult(false),
                $"Siemens TIA Safety-program authorization returned {result.Status}.");
    }

    public async Task<OperationResult<PlcBuildResult>> BuildAsync(
        PlcProjectContext project,
        ProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session is null)
        {
            return OperationResult<PlcBuildResult>.Failure("Siemens TIA project is not prepared.");
        }

        var tiaTarget = ResolveTiaTarget(target);
        if (tiaTarget is null)
        {
            return OperationResult<PlcBuildResult>.Failure("Siemens TIA project target is not known.");
        }

        var result = await session.BuildAsync(new TiaBuildRequest(tiaTarget), cancellationToken).ConfigureAwait(false);
        PublishDiagnostics(result.Diagnostics);
        var build = new PlcBuildResult(
            result.Succeeded,
            result.WarningCount > 0,
            MapDiagnostics(result.Diagnostics));
        return result.Succeeded
            ? OperationResult<PlcBuildResult>.Success(build)
            : OperationResult<PlcBuildResult>.Failure($"Siemens TIA build returned {result.Status}.");
    }

    public async Task<OperationResult<PlcTransferResult>> TransferToAsync(
        PlcProjectContext project,
        ProjectTargetRef projectTarget,
        RuntimeTargetRef runtimeTarget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session is null)
        {
            return OperationResult<PlcTransferResult>.Failure("Siemens TIA project is not prepared.");
        }

        var tiaTarget = ResolveTiaTarget(projectTarget);
        if (tiaTarget is null)
        {
            return OperationResult<PlcTransferResult>.Failure("Siemens TIA project target is not known.");
        }

        var transferRequest = new TiaTransferRequest(
            tiaTarget,
            runtimeTransferTargetFactory.Create(runtimeTarget));
        logger.LogInformation(
            "Transferring Siemens project to runtime. ProjectTargetId={ProjectTargetId}, RuntimeTargetId={RuntimeTargetId}, RuntimeTargetDisplayName={RuntimeTargetDisplayName}",
            projectTarget.Id.Value,
            runtimeTarget.Id.Value,
            runtimeTarget.DisplayName);
        var result = await session.TransferAsync(
            transferRequest,
            cancellationToken).ConfigureAwait(false);
        PublishDiagnostics(result.Diagnostics);
        if (!result.Succeeded)
        {
            return OperationResult<PlcTransferResult>.Failure($"Siemens TIA transfer returned {result.Status}.");
        }

        return OperationResult<PlcTransferResult>.Success(new PlcTransferResult(
            project,
            projectTarget,
            runtimeTarget,
            TimeProvider.GetUtcNow()));
    }

    public async Task<OperationResult> ReleaseProjectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await CloseSessionAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsSuccessful)
        {
            SetHealth(AccessHealth.NotEstablished(TimeProvider.GetUtcNow()));
        }

        return result.IsSuccessful
            ? OperationResult.Success("Siemens TIA project released.")
            : result;
    }

    public async Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await CloseSessionAsync(cancellationToken).ConfigureAwait(false);
        SetDisposed("Siemens TIA project service disposed.");
        return result.IsSuccessful
            ? OperationResult.Success("Siemens TIA project service cleaned up.")
            : result;
    }

    protected override AccessServiceKind GetServiceKind() => AccessServiceKind.Project;

    private async Task<OperationResult> CloseSessionAsync(CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return OperationResult.Success();
        }

        var closingSession = session;
        UnwireSession(closingSession);
        session = null;
        CurrentProject = null;
        configuredPlcRefId = null;
        currentTargets = Array.Empty<TiaProjectTargetRef>();
        try
        {
            await closingSession.CloseAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or InvalidOperationException
            or ObjectDisposedException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            SetHealth(new AccessHealth(
                AccessHealthState.Invalidated,
                AccessHealthProblem.ContextLost,
                "Siemens.Tia.Project.CloseFailed",
                "Siemens TIA project session close failed.",
                TimeProvider.GetUtcNow()));
            return OperationResult.Failure($"Siemens TIA project session close failed: {ex.GetBaseException().Message}");
        }
    }

    private void UnwireSession(ITiaEngineeringSession projectSession)
    {
        projectSession.HealthChanged -= OnSessionHealthChanged;
        projectSession.ProgressChanged -= OnSessionProgressChanged;
    }

    private async Task<OperationResult<PlcProjectContext>> OpenProjectSessionAsync(
        PlcProjectRequest request,
        bool closeExistingSession,
        CancellationToken cancellationToken)
    {
        var configuration = ParseConfiguration(request);
        if (configuration is null)
        {
            return OperationResult<PlcProjectContext>.Failure("Siemens TIA project configuration is invalid.");
        }

        configuredPlcRefId = configuration.PlcRef.Id;
        if (closeExistingSession && session is not null)
        {
            var close = await CloseSessionAsync(cancellationToken).ConfigureAwait(false);
            if (close.IsUnsuccessful)
            {
                return OperationResult<PlcProjectContext>.Failure(close.Message ?? "Siemens TIA project session close failed.");
            }
        }

        if (session is not null && CurrentProject is not null)
        {
            return OperationResult<PlcProjectContext>.Success(CurrentProject);
        }

        var projectSourceDeclaration = ResolveProjectSourceDeclaration(request.Configuration.ProjectSource);
        if (projectSourceDeclaration is null)
        {
            return OperationResult<PlcProjectContext>.Failure(
                "The selected Siemens TIA Openness binding does not declare exactly one matching project source form.");
        }

        var bindingRequest = new TiaProjectOpenRequest(
            request.Configuration.ProjectSource,
            projectSourceDeclaration.OpenMode,
            projectSourceDeclaration.OpenMode == TiaProjectOpenMode.RetrievedArchive
                ? configuration.Tia.ArchiveWorkDirectory
                : null,
            CorrelationId: null);
        logger.LogInformation(
            "Opening Siemens TIA project. ConfigurationFingerprint={ConfigurationFingerprint}, ProjectSource={ProjectSource}, ProjectOpenMode={ProjectOpenMode}, ConfiguredPlcRefId={ConfiguredPlcRefId}",
            SiemensConfigurationLogFingerprint.Create(request.Configuration),
            bindingRequest.ProjectSource,
            bindingRequest.OpenMode,
            configuredPlcRefId);
        var result = await binding.OpenProjectAsync(
            bindingRequest,
            cancellationToken).ConfigureAwait(false);
        PublishDiagnostics(result.Diagnostics);
        if (!result.Succeeded || result.Session is null)
        {
            return OperationResult<PlcProjectContext>.Failure($"Siemens TIA project open returned {result.Status}.");
        }

        session = result.Session;
        session.HealthChanged += OnSessionHealthChanged;
        session.ProgressChanged += OnSessionProgressChanged;
        SetHealth(MapHealth(session.Health, "Siemens TIA project session opened."));
        CurrentProject = new PlcProjectContext(
            new ProjectContextId(result.Session.Project.SessionId),
            $"Siemens project {Path.GetFileNameWithoutExtension(result.Session.Project.ProjectSource)}",
            result.Session.Project.ProjectSource);
        ProjectStateChangedEventHandler?.Invoke(this, new PlcProjectStateChangedEventArgs(CurrentProject));
        return OperationResult<PlcProjectContext>.Success(CurrentProject);
    }

    private SiemensProjectSourceDeclaration? ResolveProjectSourceDeclaration(string projectSource)
    {
        var extension = Path.GetExtension(projectSource);
        var matches = binding.Declaration.ProjectSources
            .Where(source => string.Equals(source.Extension, extension, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    private TiaProjectTargetRef? ResolveTiaTarget(ProjectTargetRef target)
    {
        return currentTargets.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, target.Id.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnselectedDiscoveryResult(TiaProjectTargetDiscoveryResult result)
    {
        // Bindings also resolve a target; only their unselected-result failures are neutral discovery.
        if (result.Status != TiaOperationStatus.Failed
            || result.SelectedTarget is not null
            || result.Targets.Count == 1)
        {
            return false;
        }

        var errorDiagnostics = result.Diagnostics
            .Where(diagnostic => diagnostic.Severity == TiaDiagnosticSeverity.Error)
            .ToArray();
        return errorDiagnostics.Length > 0
            && errorDiagnostics.All(diagnostic => IsSelectionDiagnostic(diagnostic, result.Targets.Count));
    }

    private static bool IsSelectionDiagnostic(TiaDiagnostic diagnostic, int targetCount)
    {
        return diagnostic.Phase == "DiscoverTargets"
            && diagnostic.Code.EndsWith(
                targetCount == 0 ? ".TargetNotFound" : ".TargetAmbiguous",
                StringComparison.Ordinal);
    }

    private SiemensProfileConfiguration? ParseConfiguration(PlcProjectRequest request)
    {
        var parsed = configurationParser.Parse(
            request.Configuration,
            SiemensProfileConfigurationValidationMode.Strict);
        PublishDiagnostics(parsed.Diagnostics.Select(MapConfigurationDiagnostic).ToArray());
        return parsed.IsValid ? parsed.Configuration : null;
    }

    private static TiaDiagnostic MapConfigurationDiagnostic(Diagnostic diagnostic)
    {
        return new TiaDiagnostic(
            diagnostic.Code,
            diagnostic.Level >= Microsoft.Extensions.Logging.LogLevel.Error
                ? TiaDiagnosticSeverity.Error
                : diagnostic.Level >= Microsoft.Extensions.Logging.LogLevel.Warning
                    ? TiaDiagnosticSeverity.Warning
                    : TiaDiagnosticSeverity.Information,
            diagnostic.Message,
            "Configuration",
            diagnostic.TechnicalDetail);
    }

    private void OnSessionHealthChanged(object? sender, TiaProjectHealthChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, session))
        {
            return;
        }

        SetHealth(MapHealth(args.Current, args.Message));
    }

    private void OnSessionProgressChanged(object? sender, TiaProgressSnapshot args)
    {
        if (!ReferenceEquals(sender, session))
        {
            return;
        }

        ReportProgress(new OperationProgress(
            $"{args.Operation}.{args.Phase}",
            args.Message,
            args.Percent));
    }

    private AccessHealth MapHealth(
        TiaProjectHealthState state,
        string message)
    {
        var now = TimeProvider.GetUtcNow();
        return state switch
        {
            TiaProjectHealthState.Healthy => AccessHealth.Healthy(now),
            TiaProjectHealthState.RevalidationRequired => new AccessHealth(
                AccessHealthState.RevalidationRequired,
                AccessHealthProblem.RevalidationRequired,
                "Siemens.Tia.Project.RevalidationRequired",
                message,
                now),
            TiaProjectHealthState.Invalidated => new AccessHealth(
                AccessHealthState.Invalidated,
                AccessHealthProblem.ContextLost,
                "Siemens.Tia.Project.Invalidated",
                message,
                now),
            TiaProjectHealthState.Disposed => AccessHealth.Disposed(now),
            _ => AccessHealth.NotEstablished(now)
        };
    }

    private static ProjectTargetRef MapProjectTarget(TiaProjectTargetRef target)
    {
        return new ProjectTargetRef(
            new ProjectTargetId(target.Id),
            target.DisplayName,
            target.TechnicalPath);
    }

    private IReadOnlyList<Diagnostic> MapDiagnostics(IReadOnlyList<TiaDiagnostic> diagnostics)
    {
        return diagnostics.Select(MapDiagnostic).ToArray();
    }

    private void PublishDiagnostics(IReadOnlyList<TiaDiagnostic> diagnostics)
    {
        foreach (var diagnostic in MapDiagnostics(diagnostics))
        {
            ReportDiagnostic(diagnostic);
        }
    }

    private Diagnostic MapDiagnostic(TiaDiagnostic diagnostic)
    {
        var level = diagnostic.Severity switch
        {
            TiaDiagnosticSeverity.Information => Microsoft.Extensions.Logging.LogLevel.Information,
            TiaDiagnosticSeverity.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
            _ => Microsoft.Extensions.Logging.LogLevel.Error
        };
        return new Diagnostic(
            diagnostic.Code,
            MapDiagnosticCategory(diagnostic.Phase),
            level,
            DiagnosticSource.AccessProfile,
            MapDiagnosticPhase(diagnostic.Phase),
            diagnostic.Message,
            new DiagnosticCorrelation(ServiceId: "Siemens.Tia"),
            TimeProvider.GetUtcNow(),
            TechnicalDetail: diagnostic.EvidenceId);
    }

    private static DiagnosticCategory MapDiagnosticCategory(string phase)
    {
        return phase switch
        {
            "Configuration" => DiagnosticCategory.Configuration,
            "Cleanup" or "Close" => DiagnosticCategory.Cleanup,
            _ => DiagnosticCategory.Environment
        };
    }

    private static DiagnosticPhase MapDiagnosticPhase(string phase)
    {
        return phase switch
        {
            "Configuration" => DiagnosticPhase.Configuration,
            "Transfer" => DiagnosticPhase.ProjectTransfer,
            "Cleanup" or "Close" => DiagnosticPhase.Cleanup,
            _ => DiagnosticPhase.ProjectPreparation
        };
    }
}
