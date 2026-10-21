using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.UnitTests;
using System.Text.Json;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

/// <summary>
/// Siemens access profile package shell.
/// </summary>
public sealed class SiemensAccessProfilePlugin : IAccessProfilePlugin
{
    internal const string DefaultProfileId = "siemens";
    internal const string DefaultProfileVersion = "2.0.0";
    internal const string DefaultDisplayName = "Siemens Access Profile";
    internal const string DefaultDescription = "Siemens TIA Openness and PLCSIM Advanced access profile.";
    internal const string DefaultUnitTestContractVersion = UnitTestContractVersions.EventStreamModel;

    private readonly SiemensProfileConfigurationParser configurationParser;
    private readonly ISiemensProfileRegistryStore profileRegistryStore;
    private readonly SiemensProfileRegistrationOrchestrator registrationOrchestrator;
    private readonly SiemensSelectedBindingSelector selectedBindingSelector;
    private readonly object lifecycleSync = new();
    private SiemensSelectedBindingLoader? selectedBindingLoader;
    private ILogger<SiemensAccessProfilePlugin> logger = NullLogger<SiemensAccessProfilePlugin>.Instance;
    private AccessProfileContext? context;
    private SiemensAccessProfilePluginOperation activeOperation;
    private bool hasActiveSession;
    private bool disposed;

    public SiemensAccessProfilePlugin()
        : this(
            CreateDefaultInfo(),
            new SiemensProfileConfigurationParser(),
            new FileSiemensProfileRegistryStore(),
            new SiemensProfileRegistrationOrchestrator(),
            new SiemensSelectedBindingSelector())
    {
    }

    internal SiemensAccessProfilePlugin(AccessProfileInfo info)
        : this(
            info,
            new SiemensProfileConfigurationParser(),
            new FileSiemensProfileRegistryStore(),
            new SiemensProfileRegistrationOrchestrator(),
            new SiemensSelectedBindingSelector())
    {
    }

    internal SiemensAccessProfilePlugin(
        AccessProfileInfo info,
        SiemensProfileConfigurationParser configurationParser,
        ISiemensProfileRegistryStore? profileRegistryStore = null,
        SiemensProfileRegistrationOrchestrator? registrationOrchestrator = null,
        SiemensSelectedBindingSelector? selectedBindingSelector = null)
    {
        Info = info;
        this.configurationParser = configurationParser;
        this.profileRegistryStore = profileRegistryStore ?? new FileSiemensProfileRegistryStore();
        this.registrationOrchestrator = registrationOrchestrator ?? new SiemensProfileRegistrationOrchestrator();
        this.selectedBindingSelector = selectedBindingSelector ?? new SiemensSelectedBindingSelector();
    }

    public AccessProfileInfo Info { get; }

    public void Initialize(AccessProfileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (this.context is not null)
            {
                throw new InvalidOperationException("The Siemens access profile plugin has already been initialized.");
            }

            this.context = context;
            logger = context.LoggerFactory.CreateLogger<SiemensAccessProfilePlugin>()
                ?? NullLogger<SiemensAccessProfilePlugin>.Instance;
            selectedBindingLoader = new SiemensSelectedBindingLoader(
                context.TimeProvider,
                logger,
                CreateBindingContext(context));
        }
    }

    public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
    {
        return ValidateConfig(config, SiemensProfileConfigurationValidationMode.Partial);
    }

    private AccessProfileValidationResult ValidateConfig(
        AccessProfileConfig config,
        SiemensProfileConfigurationValidationMode validationMode)
    {
        if (!string.Equals(config.ProfileId, Info.ProfileId, StringComparison.OrdinalIgnoreCase))
        {
            var result = new AccessProfileValidationResult(
                false,
                [SiemensDiagnostics.ConfigurationError(
                    "SiemensProfile.Config.ProfileMismatch",
                    $"The Siemens access profile cannot handle profile id '{config.ProfileId}'.")]);
            LogValidationResult(config, result);
            return result;
        }

        if (config.ProfileVersion is not null && !string.Equals(config.ProfileVersion, Info.Version, StringComparison.OrdinalIgnoreCase))
        {
            var result = new AccessProfileValidationResult(
                false,
                [SiemensDiagnostics.ConfigurationError(
                    "SiemensProfile.Config.VersionMismatch",
                    $"The Siemens access profile version '{Info.Version}' cannot handle requested version '{config.ProfileVersion}'.")]);
            LogValidationResult(config, result);
            return result;
        }

        var parse = configurationParser.Parse(config, validationMode);
        var validation = new AccessProfileValidationResult(parse.IsValid, parse.Diagnostics);
        LogValidationResult(config, validation);
        return validation;
    }

    public async Task<AccessProfileRegistrationResult> RegisterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var initializedContext = RequireContext();
        if (!TryBeginOperation(
                SiemensAccessProfilePluginOperation.Registration,
                rejectWhileSessionActive: true,
                out var operation,
                out var rejectionMessage))
        {
            logger.LogWarning(
                "Siemens access profile registration rejected. Reason={Reason}",
                rejectionMessage);
            return new AccessProfileRegistrationResult(
                AccessProfileRegistrationState.Error,
                AccessProfileCapabilities.Empty,
                rejectionMessage);
        }

        using var operationLease = operation;
        logger.LogInformation(
            "Siemens access profile registration started. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, ProfileRootPath={ProfileRootPath}, ProfileDataDirectory={ProfileDataDirectory}",
            Info.ProfileId,
            Info.Version,
            initializedContext.ProfileRootPath,
            initializedContext.ProfileDataDirectory);

        var registration = await registrationOrchestrator.RegisterAsync(
            initializedContext,
            CreateBindingContext(initializedContext),
            cancellationToken).ConfigureAwait(false);
        foreach (var diagnostic in registration.Discovery.Diagnostics)
        {
            LogDiagnostic(diagnostic);
        }

        foreach (var diagnostic in registration.Diagnostics)
        {
            LogDiagnostic(diagnostic);
        }

        logger.Log(
            registration.Result.State == AccessProfileRegistrationState.Error ? LogLevel.Error : LogLevel.Information,
            "Siemens access profile registration completed. State={State}, AccessApiCount={AccessApiCount}, ProjectSourceCount={ProjectSourceCount}, RuntimeCount={RuntimeCount}, UnitTestContractCount={UnitTestContractCount}, BindingCandidateCount={BindingCandidateCount}, Message={Message}",
            registration.Result.State,
            registration.Result.Capabilities.AccessApis.Count,
            registration.Result.Capabilities.ProjectSources.Count,
            registration.Result.Capabilities.Runtimes.Count,
            registration.Result.Capabilities.UnitTestContractVersions.Count,
            registration.BindingCandidates.Count,
            registration.Result.Message);

        return await SaveRegistrationResultAsync(
            initializedContext,
            registration.Result,
            registration.BindingCandidates,
            cancellationToken).ConfigureAwait(false);
    }

    public AccessProfileProjectSourceEvaluation EvaluateProjectSource(string projectSource)
    {
        logger.LogInformation(
            "Siemens project-source evaluation started. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, ProjectSource={ProjectSource}",
            Info.ProfileId,
            Info.Version,
            projectSource);

        _ = RequireContext();
        if (!TryBeginOperation(
                SiemensAccessProfilePluginOperation.ProjectSourceEvaluation,
                rejectWhileSessionActive: false,
                out var operation,
                out var rejectionMessage))
        {
            var rejected = new AccessProfileProjectSourceEvaluation(
                AccessProfileProjectSourceEvaluationState.Error,
                rejectionMessage);
            LogProjectSourceEvaluationResult(rejected, projectSource);
            return rejected;
        }

        using var operationLease = operation;
        try
        {
            var initializedContext = RequireContext();
            var registry = profileRegistryStore.Load(initializedContext);
            if (!string.Equals(registry.ProfileId, Info.ProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(registry.ProfileVersion, Info.Version, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(registry.ProfileRootPath, initializedContext.ProfileRootPath, StringComparison.OrdinalIgnoreCase))
            {
                var result = new AccessProfileProjectSourceEvaluation(
                    AccessProfileProjectSourceEvaluationState.Error,
                    "Siemens profile registry identity does not match the loaded profile.");
                LogProjectSourceEvaluationResult(result, projectSource);
                return result;
            }

            if (registry.State == AccessProfileRegistrationState.Error)
            {
                var result = new AccessProfileProjectSourceEvaluation(
                    AccessProfileProjectSourceEvaluationState.Error,
                    registry.Message ?? "Siemens profile registry reports an error state.");
                LogProjectSourceEvaluationResult(result, projectSource);
                return result;
            }

            if (registry.State != AccessProfileRegistrationState.Registered)
            {
                var result = new AccessProfileProjectSourceEvaluation(
                    AccessProfileProjectSourceEvaluationState.Unsupported,
                    registry.Message ?? "Siemens profile registration is not available.");
                LogProjectSourceEvaluationResult(result, projectSource);
                return result;
            }

            var supported = selectedBindingSelector.CanServeProjectSource(registry, projectSource);

            var evaluation = supported
                ? new AccessProfileProjectSourceEvaluation(
                    AccessProfileProjectSourceEvaluationState.Supported,
                    "Siemens profile supports the selected project source.")
                : new AccessProfileProjectSourceEvaluation(
                    AccessProfileProjectSourceEvaluationState.Unsupported,
                    "Siemens profile does not support the selected project source.");
            LogProjectSourceEvaluationResult(evaluation, projectSource);
            return evaluation;
        }
        catch (Exception ex) when (ex is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException
            or NotSupportedException
            or ArgumentException)
        {
            logger.LogError(
                ex,
                "Siemens project-source evaluation failed while reading profile registry. ProjectSource={ProjectSource}",
                projectSource);
            var result = new AccessProfileProjectSourceEvaluation(
                AccessProfileProjectSourceEvaluationState.Error,
                "Siemens profile registry could not be read.");
            LogProjectSourceEvaluationResult(result, projectSource);
            return result;
        }
    }

    public async Task<AccessProfileSessionLoadResult> LoadAsync(
        AccessProfileSessionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation(
            "Siemens access profile load started. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, ProjectSource={ProjectSource}",
            request.ProfileId,
            request.ProfileVersion,
            request.ProjectSource);

        if (!string.Equals(request.ProfileId, Info.ProfileId, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(request.ProfileVersion)
                && !string.Equals(request.ProfileVersion, Info.Version, StringComparison.OrdinalIgnoreCase)))
        {
            return AccessProfileSessionLoadResult.Failure(
                "The Siemens access profile session request does not match this profile.");
        }

        if (request.ProjectOwnershipMode == ProjectOwnershipMode.ApplicationManaged
            && string.IsNullOrWhiteSpace(request.ProjectSource))
        {
            var message = "Application-managed Siemens sessions require a project source.";
            var diagnostic = SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Config.ProjectSourceMissing",
                message);
            LogDiagnostic(diagnostic);
            return AccessProfileSessionLoadResult.Failure(message, [diagnostic]);
        }

        var initializedContext = RequireContext();
        if (!TryBeginOperation(
                SiemensAccessProfilePluginOperation.Load,
                rejectWhileSessionActive: true,
                out var operation,
                out var rejectionMessage))
        {
            var diagnostic = SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Load.OperationRejected",
                rejectionMessage);
            LogDiagnostic(diagnostic);
            return AccessProfileSessionLoadResult.Failure(rejectionMessage, [diagnostic]);
        }

        using var operationLease = operation;
        SiemensProfileRegistry registry;
        try
        {
            registry = profileRegistryStore.Load(initializedContext);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            var message = "Siemens profile registry is missing. Run profile registration.";
            var diagnostic = SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Load.ProfileRegistryMissing",
                message,
                ex.Message);
            logger.LogWarning(
                ex,
                "Siemens access profile load failed because the profile registry is missing. ProfileDataDirectory={ProfileDataDirectory}",
                initializedContext.ProfileDataDirectory);
            LogDiagnostic(diagnostic);
            return AccessProfileSessionLoadResult.Failure(
                message,
                [diagnostic]);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            var message = "Siemens profile registry is corrupt or unsupported. Run profile registration to regenerate the profile state.";
            var diagnostic = SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Load.ProfileRegistryCorrupt",
                message,
                ex.Message);
            logger.LogError(
                ex,
                "Siemens access profile load failed because the profile registry is corrupt or unsupported. ProfileDataDirectory={ProfileDataDirectory}",
                initializedContext.ProfileDataDirectory);
            LogDiagnostic(diagnostic);
            return AccessProfileSessionLoadResult.Failure(
                message,
                [diagnostic]);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            var message = "Siemens profile registry could not be read.";
            var diagnostic = SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Load.ProfileRegistryUnreadable",
                message,
                ex.Message);
            logger.LogError(
                ex,
                "Siemens access profile load failed because the profile registry could not be read. ProfileDataDirectory={ProfileDataDirectory}",
                initializedContext.ProfileDataDirectory);
            LogDiagnostic(diagnostic);
            return AccessProfileSessionLoadResult.Failure(
                message,
                [diagnostic]);
        }

        var selection = selectedBindingSelector.Select(
            Info,
            initializedContext,
            request.ProjectSource,
            registry,
            request.ProjectOwnershipMode);
        if (!selection.IsSuccessful)
        {
            LogLoadFailure(
                selection.Message ?? "Siemens profile binding selection failed.",
                selection.Diagnostics);
            return AccessProfileSessionLoadResult.Failure(
                selection.Message ?? "Siemens profile binding selection failed.",
                selection.Diagnostics);
        }

        var loadPlan = selection.LoadPlan!;
        logger.LogInformation(
            "Siemens access profile selected binding load plan created. TiaBinding={TiaBinding}, PlcSimBinding={PlcSimBindingKind}/{PlcSimBindingVersion}, PlcSimEntryAssembly={PlcSimEntryAssembly}",
            loadPlan.TiaOpenness is null
                ? "not selected"
                : $"{loadPlan.TiaOpenness.BindingKind}/{loadPlan.TiaOpenness.BindingVersion}",
            loadPlan.PlcSimAdvanced.BindingKind,
            loadPlan.PlcSimAdvanced.BindingVersion,
            loadPlan.PlcSimAdvanced.EntryAssemblyPath);
        var bindingLoad = await selectedBindingLoader!
            .LoadAsync(loadPlan, cancellationToken)
            .ConfigureAwait(false);
        if (!bindingLoad.IsSuccessful)
        {
            LogLoadFailure(
                "Siemens selected binding load failed.",
                bindingLoad.Diagnostics);
            return AccessProfileSessionLoadResult.Failure(
                "Siemens selected binding load failed.",
                bindingLoad.Diagnostics);
        }

        var resourceScope = new SiemensProfileResourceScope(initializedContext.TimeProvider, logger);
        resourceScope.OwnSelectedBindings(bindingLoad.Bindings!);
        SiemensAccessProfileSession session;
        try
        {
            session = new SiemensAccessProfileSession(
                Info,
                bindingLoad.Bindings!,
                request.ProjectOwnershipMode,
                resourceScope,
                initializedContext.TimeProvider,
                logger,
                initializedContext.LoggerFactory,
                request.ProjectSource,
                OnSessionDisposed);
        }
        catch (Exception ex)
        {
            var diagnostics = new List<Diagnostic>
            {
                SiemensDiagnostics.ConfigurationError(
                    "SiemensProfile.Load.SessionCompositionFailed",
                    "Siemens access profile session composition failed.",
                    ex.Message,
                    initializedContext.TimeProvider.GetUtcNow())
            };
            var cleanup = await resourceScope.CleanupAsync(CancellationToken.None).ConfigureAwait(false);
            diagnostics.AddRange(cleanup.Diagnostics);
            LogLoadFailure(
                "Siemens access profile session composition failed.",
                diagnostics);
            return AccessProfileSessionLoadResult.Failure(
                "Siemens access profile session composition failed.",
                diagnostics);
        }

        MarkSessionActive();
        logger.LogInformation(
            "Siemens access profile session composed. ProjectOwnershipMode={ProjectOwnershipMode}",
            request.ProjectOwnershipMode);
        return AccessProfileSessionLoadResult.Success(
            session,
            "Siemens access profile session composed.");
    }

    private SiemensBindingContext CreateBindingContext(AccessProfileContext initializedContext)
    {
        return new SiemensBindingContext(Info.ProfileId, initializedContext.Logging);
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycleSync)
        {
            if (disposed)
            {
                return ValueTask.CompletedTask;
            }

            if (activeOperation != SiemensAccessProfilePluginOperation.None)
            {
                throw new InvalidOperationException(
                    $"The Siemens access profile plugin cannot be disposed while '{activeOperation}' is active.");
            }

            if (hasActiveSession)
            {
                throw new InvalidOperationException(
                    "The Siemens access profile plugin cannot be disposed while its profile session is active.");
            }

            disposed = true;
            context = null;
            selectedBindingLoader = null;
            logger = NullLogger<SiemensAccessProfilePlugin>.Instance;
        }

        return ValueTask.CompletedTask;
    }

    internal static AccessProfileInfo CreateDefaultInfo()
    {
        return new AccessProfileInfo(
            DefaultProfileId,
            DefaultDisplayName,
            DefaultProfileVersion,
            DefaultDescription);
    }

    private AccessProfileContext RequireContext()
    {
        lock (lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return context ?? throw new InvalidOperationException("The Siemens access profile plugin has not been initialized.");
        }
    }

    private bool TryBeginOperation(
        SiemensAccessProfilePluginOperation operation,
        bool rejectWhileSessionActive,
        out IDisposable? lease,
        out string rejectionMessage)
    {
        lock (lifecycleSync)
        {
            if (disposed)
            {
                lease = null;
                rejectionMessage = "The Siemens access profile plugin has already been disposed.";
                return false;
            }

            if (activeOperation != SiemensAccessProfilePluginOperation.None)
            {
                lease = null;
                rejectionMessage = $"The Siemens access profile plugin is already executing '{activeOperation}'.";
                return false;
            }

            if (rejectWhileSessionActive && hasActiveSession)
            {
                lease = null;
                rejectionMessage = "The Siemens access profile plugin already owns an active profile session.";
                return false;
            }

            activeOperation = operation;
            lease = new SiemensAccessProfilePluginOperationLease(this, operation);
            rejectionMessage = string.Empty;
            return true;
        }
    }

    private void EndOperation(SiemensAccessProfilePluginOperation operation)
    {
        lock (lifecycleSync)
        {
            if (activeOperation == operation)
            {
                activeOperation = SiemensAccessProfilePluginOperation.None;
            }
        }
    }

    private void MarkSessionActive()
    {
        lock (lifecycleSync)
        {
            hasActiveSession = true;
        }
    }

    private void OnSessionDisposed()
    {
        lock (lifecycleSync)
        {
            hasActiveSession = false;
        }
    }

    private async Task<AccessProfileRegistrationResult> SaveRegistrationResultAsync(
        AccessProfileContext initializedContext,
        AccessProfileRegistrationResult result,
        IReadOnlyList<SiemensBindingCandidateEvidence> bindingCandidates,
        CancellationToken cancellationToken)
    {
        var registry = SiemensProfileRegistry.FromRegistrationResult(
            Info,
            initializedContext.ProfileRootPath,
            result,
            initializedContext.TimeProvider.GetUtcNow(),
            bindingCandidates);

        try
        {
            await profileRegistryStore.SaveAsync(initializedContext, registry, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            logger.LogError(
                ex,
                "Siemens profile registry could not be written. ProfileDataDirectory={ProfileDataDirectory}",
                initializedContext.ProfileDataDirectory);
            return new AccessProfileRegistrationResult(
                AccessProfileRegistrationState.Error,
                AccessProfileCapabilities.Empty,
                "Siemens profile registry could not be written.");
        }

        return result;
    }

    private void LogValidationResult(
        AccessProfileConfig config,
        AccessProfileValidationResult result)
    {
        if (result.IsValid)
        {
            logger.LogDebug(
                "Siemens access profile configuration validation succeeded. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, ProjectSource={ProjectSource}",
                config.ProfileId,
                config.ProfileVersion,
                config.ProjectSource);
            return;
        }

        logger.LogWarning(
            "Siemens access profile configuration validation failed. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, ProjectSource={ProjectSource}, DiagnosticCount={DiagnosticCount}",
            config.ProfileId,
            config.ProfileVersion,
            config.ProjectSource,
            result.Diagnostics.Count);
        foreach (var diagnostic in result.Diagnostics)
        {
            LogDiagnostic(diagnostic);
        }
    }

    private void LogProjectSourceEvaluationResult(
        AccessProfileProjectSourceEvaluation result,
        string projectSource)
    {
        var level = result.State == AccessProfileProjectSourceEvaluationState.Error
            ? LogLevel.Warning
            : LogLevel.Information;
        logger.Log(
            level,
            "Siemens project-source evaluation completed. State={State}, ProjectSource={ProjectSource}, Message={Message}",
            result.State,
            projectSource,
            result.Message);
    }

    private void LogLoadFailure(
        string message,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        logger.LogWarning(
            "Siemens access profile load failed. Message={Message}, DiagnosticCount={DiagnosticCount}",
            message,
            diagnostics.Count);
        foreach (var diagnostic in diagnostics)
        {
            LogDiagnostic(diagnostic);
        }
    }

    private void LogDiagnostic(Diagnostic diagnostic)
    {
        logger.Log(
            diagnostic.Level,
            "Siemens access profile diagnostic. Code={DiagnosticCode}, Category={Category}, Phase={Phase}, Message={Message}, TechnicalDetail={TechnicalDetail}",
            diagnostic.Code,
            diagnostic.Category,
            diagnostic.Phase,
            diagnostic.Message,
            diagnostic.TechnicalDetail);
    }

    private sealed class SiemensAccessProfilePluginOperationLease(
        SiemensAccessProfilePlugin owner,
        SiemensAccessProfilePluginOperation operation) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.EndOperation(operation);
            }
        }
    }

    private enum SiemensAccessProfilePluginOperation
    {
        None,
        Registration,
        ProjectSourceEvaluation,
        Load
    }
}
