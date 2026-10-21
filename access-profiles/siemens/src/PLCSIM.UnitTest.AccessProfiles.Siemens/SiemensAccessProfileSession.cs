using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensAccessProfileSession : IAccessProfileSession
{
    private static readonly TimeSpan RuntimeReconnectTimeout = TimeSpan.FromSeconds(30);

    private readonly SiemensProfileResourceScope resourceScope;
    private readonly SiemensProfileConfigurationParser configurationParser;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly SiemensPlcsimPlcControlService plcControlService;
    private readonly SiemensPlcsimUnitTestLifecycleService lifecycleService;
    private readonly SiemensPlcsimUnitTestEventStreamService eventStreamService;
    private readonly List<SiemensAccessProfileServiceBase> wiredServices = [];
    private readonly object disposeSync = new();
    private Action? disposedCallback;
    private Task? disposeTask;
    private bool disposalStarted;
    private AccessProfileState state = AccessProfileState.Created;
    private AccessHealth health;

    public SiemensAccessProfileSession(
        AccessProfileInfo info,
        SiemensSelectedBindings bindings,
        ProjectOwnershipMode projectOwnershipMode,
        SiemensProfileResourceScope resourceScope,
        TimeProvider timeProvider,
        ILogger logger,
        ILoggerFactory? loggerFactory = null,
        string projectSource = "",
        Action? disposedCallback = null)
    {
        Info = info;
        this.disposedCallback = disposedCallback;
        configurationParser = new SiemensProfileConfigurationParser();
        Bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        this.resourceScope = resourceScope ?? throw new ArgumentNullException(nameof(resourceScope));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        health = AccessHealth.NotEstablished(timeProvider.GetUtcNow());
        var resolvedLoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

        Project = projectOwnershipMode == ProjectOwnershipMode.ApplicationManaged
            ? new SiemensTiaPlcProjectService(
                bindings.TiaOpenness
                    ?? throw new InvalidOperationException("Application-managed Siemens sessions require a TIA Openness binding."),
                timeProvider,
                resolvedLoggerFactory.CreateLogger<SiemensTiaPlcProjectService>())
            : new SiemensExternalPlcProjectService(timeProvider);
        plcControlService = new SiemensPlcsimPlcControlService(
            bindings.PlcSimAdvancedRuntime,
            timeProvider,
            resolvedLoggerFactory.CreateLogger<SiemensPlcsimPlcControlService>());
        PlcControl = plcControlService;
        var communicationSessionOwner = new SiemensPlcsimCommunicationSessionOwner(
            resolvedLoggerFactory.CreateLogger<SiemensPlcsimCommunicationSessionOwner>());
        lifecycleService = new SiemensPlcsimUnitTestLifecycleService(
            timeProvider,
            resolvedLoggerFactory.CreateLogger<SiemensPlcsimUnitTestLifecycleService>(),
            communicationSessionOwner);
        UnitTestLifecycle = lifecycleService;
        eventStreamService = new SiemensPlcsimUnitTestEventStreamService(
            timeProvider,
            resolvedLoggerFactory.CreateLogger<SiemensPlcsimUnitTestEventStreamService>(),
            communicationSessionOwner);
        UnitTestEventStream = eventStreamService;
        var projectOpenMode = bindings.TiaOpenness?.Declaration.ProjectSources
            .SingleOrDefault(source =>
                string.Equals(
                    source.Extension,
                    Path.GetExtension(projectSource),
                    StringComparison.OrdinalIgnoreCase))
            ?.OpenMode;
        Configuration = new SiemensProfileConfigurationService(
            Info,
            configurationParser,
            projectSource,
            projectOpenMode);
        WireService(Project, AccessServiceKind.Project);
        WireService(PlcControl, AccessServiceKind.PlcControl);
        WireService(UnitTestLifecycle, AccessServiceKind.UnitTestLifecycle);
        WireService(UnitTestEventStream, AccessServiceKind.UnitTestEventStream);
        logger.LogInformation(
            "Siemens access profile session services composed. ProjectService={ProjectService}, PlcControlService={PlcControlService}, LifecycleService={LifecycleService}, EventStreamService={EventStreamService}",
            Project.GetType().Name,
            PlcControl.GetType().Name,
            UnitTestLifecycle.GetType().Name,
            UnitTestEventStream.GetType().Name);
        SetState(AccessProfileState.Ready, "Siemens access profile session created.");
    }

    public AccessProfileInfo Info { get; }

    public SiemensSelectedBindings Bindings { get; }

    public IPlcProjectService Project { get; }

    public IPlcControlService PlcControl { get; }

    public IUnitTestLifecycleService UnitTestLifecycle { get; }

    public IUnitTestEventStreamService UnitTestEventStream { get; }

    public IAccessProfileConfigurationService Configuration { get; }

    public AccessHealth Health => health;

    public event EventHandler<DiagnosticEventArgs>? DiagnosticEventHandler;

    public event EventHandler<ProfileStateChangedEventArgs>? StateChangedEventHandler;

    public event EventHandler<AccessHealthChangedEventArgs>? HealthChangedEventHandler;

    public async Task<OperationResult<PreparedPlcRuntime>> ReconnectRuntimeAsync(
        PlcRuntimeReconnectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reconnectTimeout = new CancellationTokenSource(RuntimeReconnectTimeout);
        var reconnectToken = reconnectTimeout.Token;

        try
        {
            var eventStreamRelease = await eventStreamService
                .ReleaseCommunicationAsync(reconnectToken)
                .ConfigureAwait(false);
            if (eventStreamRelease.IsUnsuccessful)
            {
                return OperationResult<PreparedPlcRuntime>.Failure(
                    eventStreamRelease.Message ?? "Siemens PLCSIM event-stream communication could not be released for reconnect.");
            }

            var lifecycleRelease = await lifecycleService
                .ReleaseCommunicationAsync(reconnectToken)
                .ConfigureAwait(false);
            if (lifecycleRelease.IsUnsuccessful)
            {
                return OperationResult<PreparedPlcRuntime>.Failure(
                    lifecycleRelease.Message ?? "Siemens PLCSIM lifecycle communication could not be released for reconnect.");
            }

            return await plcControlService
                .ReconnectRuntimeAsync(request, reconnectToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (reconnectTimeout.IsCancellationRequested)
        {
            return OperationResult<PreparedPlcRuntime>.Failure(
                $"Siemens PLCSIM runtime reconnect exceeded the {RuntimeReconnectTimeout.TotalSeconds:0}-second internal deadline.");
        }
    }

    public async Task<OperationResult<CommunicationSession>> PrepareUnitTestCommunicationAsync(
        CommunicationPrepareRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = configurationParser.Parse(
            request.Configuration,
            SiemensProfileConfigurationValidationMode.Partial);
        foreach (var diagnostic in parsed.Diagnostics)
        {
            DiagnosticEventHandler?.Invoke(this, new DiagnosticEventArgs(diagnostic));
        }

        if (!parsed.IsValid || parsed.Configuration is null)
        {
            return OperationResult<CommunicationSession>.Failure(
                "Siemens UnitTest communication configuration is invalid.");
        }

        var configuration = parsed.Configuration;
        var timeouts = request.Timeouts
            ?? RunConfigurationDefaults.CreateDefaultCommunicationTimeoutPolicy();
        var bindingRequest = new PlcsimCommunicationPrepareRequest(
            request.Runtime.Id.Value,
            configuration.UnitTest.RunControl,
            configuration.UnitTest.EventBuffer,
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            new PlcsimCommunicationWorkerOptions(
                MapCommunicationObservationStrategy(configuration.UnitTest.ObservationStrategy),
                configuration.UnitTest.MonitoringInterval)
            {
                DrainInterval = configuration.UnitTest.DrainInterval,
                MaxEventsPerBatch = configuration.UnitTest.MaxEventsPerBatch
            },
            timeouts.Prepare,
            timeouts.Revalidation,
            CorrelationId: null);
        logger.LogInformation(
            "Preparing Siemens UnitTest communication. ConfigurationFingerprint={ConfigurationFingerprint}, RuntimeContextId={RuntimeContextId}, RuntimeTargetId={RuntimeTargetId}, RunControlRoot={RunControlRoot}, EventBufferRoot={EventBufferRoot}, ObservationStrategy={ObservationStrategy}, MonitoringIntervalMs={MonitoringIntervalMs}, DrainIntervalMs={DrainIntervalMs}, MaxEventsPerBatch={MaxEventsPerBatch}, PrepareTimeoutMs={PrepareTimeoutMs}, RevalidationTimeoutMs={RevalidationTimeoutMs}",
            SiemensConfigurationLogFingerprint.Create(request.Configuration),
            request.Runtime.Id.Value,
            request.RuntimeTarget.Id.Value,
            bindingRequest.RunControlRoot,
            bindingRequest.EventBufferRoot,
            bindingRequest.WorkerOptions.ObservationStrategy,
            bindingRequest.WorkerOptions.MonitoringInterval?.TotalMilliseconds,
            bindingRequest.WorkerOptions.DrainInterval?.TotalMilliseconds,
            bindingRequest.WorkerOptions.MaxEventsPerBatch,
            bindingRequest.PrepareTimeout.TotalMilliseconds,
            bindingRequest.RevalidationTimeout.TotalMilliseconds);
        var result = await Bindings.PlcSimAdvancedCommunication.PrepareCommunicationAsync(
            bindingRequest,
            cancellationToken).ConfigureAwait(false);
        foreach (var diagnostic in MapCommunicationDiagnostics(result.Diagnostics, DiagnosticPhase.CommunicationValidation))
        {
            DiagnosticEventHandler?.Invoke(this, new DiagnosticEventArgs(diagnostic));
        }

        if (!result.Succeeded || result.Session is null)
        {
            return SiemensPlcsimOperationMapping.ToOperationResult<CommunicationSession>(
                result.Status,
                null,
                $"Siemens PLCSIM UnitTest communication preparation returned {result.Status}.");
        }

        lifecycleService.BindCommunicationSession(result.Session);
        eventStreamService.BindCommunicationSession(result.Session);
        var validation = await lifecycleService.ValidateAndObserveAsync(cancellationToken).ConfigureAwait(false);
        if (validation.IsUnsuccessful)
        {
            await ReleasePreparedCommunicationAfterValidationFailureAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult<CommunicationSession>.Failure(
                validation.Message ?? "Siemens PLCSIM UnitTest communication validation failed.");
        }

        var communication = new CommunicationSession(
            new CommunicationSessionId(result.Session.Communication.SessionId),
            result.Session.Communication.PreparedAt);
        return OperationResult<CommunicationSession>.Success(communication);
    }

    private async Task ReleasePreparedCommunicationAfterValidationFailureAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning("Siemens PLCSIM UnitTest communication validation failed after prepare; releasing prepared communication session.");
        var eventStreamRelease = await UnitTestEventStream.ReleaseCommunicationAsync(cancellationToken).ConfigureAwait(false);
        if (eventStreamRelease.IsUnsuccessful)
        {
            var diagnostic = CreateServiceCleanupDiagnostic(
                "UnitTest event-stream service",
                eventStreamRelease.Message ?? "UnitTest event-stream communication release failed.");
            DiagnosticEventHandler?.Invoke(this, new DiagnosticEventArgs(diagnostic));
            logger.LogWarning(
                "Siemens PLCSIM UnitTest event-stream communication release after validation failure returned failure. State={State}, Message={Message}",
                eventStreamRelease.State,
                eventStreamRelease.Message);
        }

        var lifecycleRelease = await UnitTestLifecycle.ReleaseCommunicationAsync(cancellationToken).ConfigureAwait(false);
        if (lifecycleRelease.IsUnsuccessful)
        {
            var diagnostic = CreateServiceCleanupDiagnostic(
                "UnitTest lifecycle service",
                lifecycleRelease.Message ?? "UnitTest lifecycle communication release failed.");
            DiagnosticEventHandler?.Invoke(this, new DiagnosticEventArgs(diagnostic));
            logger.LogWarning(
                "Siemens PLCSIM UnitTest lifecycle communication release after validation failure returned failure. State={State}, Message={Message}",
                lifecycleRelease.State,
                lifecycleRelease.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<object?>? disposeCompletion = null;
        lock (disposeSync)
        {
            if (disposeTask is null)
            {
                disposalStarted = true;
                disposeCompletion = new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                disposeTask = disposeCompletion.Task;
            }
        }

        if (disposeCompletion is not null)
        {
            _ = CompleteDisposeAsync(disposeCompletion);
        }

        return new ValueTask(disposeTask);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource<object?> completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.SetResult(null);
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            logger.LogInformation("Siemens access profile session dispose started.");
            UnwireServices();
            SetState(AccessProfileState.Disposed, "Siemens access profile session disposed.");
            await CleanupServiceAsync(UnitTestEventStream, "UnitTest event-stream service").ConfigureAwait(false);
            await CleanupServiceAsync(UnitTestLifecycle, "UnitTest lifecycle service").ConfigureAwait(false);
            await CleanupServiceAsync(PlcControl, "PLC control service").ConfigureAwait(false);
            await CleanupServiceAsync(Project, "PLC project service").ConfigureAwait(false);
            var cleanup = await resourceScope.CleanupAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var diagnostic in cleanup.Diagnostics)
            {
                DiagnosticEventHandler?.Invoke(this, new DiagnosticEventArgs(diagnostic));
            }

            logger.LogInformation(
                "Siemens access profile session dispose completed. ResourceCleanupDiagnosticCount={DiagnosticCount}",
                cleanup.Diagnostics.Count);
        }
        finally
        {
            Interlocked.Exchange(ref disposedCallback, null)?.Invoke();
        }
    }

    private static PlcsimCommunicationObservationStrategyKind MapCommunicationObservationStrategy(
        SiemensCommunicationObservationStrategy strategy)
    {
        return strategy switch
        {
            SiemensCommunicationObservationStrategy.PlcCycle => PlcsimCommunicationObservationStrategyKind.PlcCycle,
            _ => PlcsimCommunicationObservationStrategyKind.Polling
        };
    }

    private void WireService(
        IAccessProfileService service,
        AccessServiceKind serviceKind)
    {
        if (service is SiemensAccessProfileServiceBase profileService)
        {
            profileService.HealthChangedEventHandler += OnServiceHealthChanged;
            wiredServices.Add(profileService);
        }

        if (service.Health.State != AccessHealthState.NotEstablished)
        {
            HealthChangedEventHandler?.Invoke(this, new AccessHealthChangedEventArgs([
                new AccessServiceHealthUpdate(serviceKind, service.Health)
            ]));
        }
    }

    private void UnwireServices()
    {
        foreach (var service in wiredServices)
        {
            service.HealthChangedEventHandler -= OnServiceHealthChanged;
        }

        wiredServices.Clear();
    }

    private void OnServiceHealthChanged(object? sender, AccessHealthChangedEventArgs args)
    {
        if (disposalStarted
            || sender is not SiemensAccessProfileServiceBase service
            || !wiredServices.Contains(service))
        {
            return;
        }

        HealthChangedEventHandler?.Invoke(this, args);
    }

    private void SetState(
        AccessProfileState newState,
        string reason)
    {
        if (state == newState)
        {
            return;
        }

        state = newState;
        StateChangedEventHandler?.Invoke(this, new ProfileStateChangedEventArgs(newState, reason));
        SetHealth(MapStateToHealth(newState, reason));
    }

    private void SetHealth(AccessHealth newHealth)
    {
        if (health == newHealth)
        {
            return;
        }

        health = newHealth;
        HealthChangedEventHandler?.Invoke(this, new AccessHealthChangedEventArgs([
            new AccessServiceHealthUpdate(AccessServiceKind.Profile, newHealth)
        ]));
    }

    private async Task CleanupServiceAsync(
        IAccessProfileService service,
        string serviceName)
    {
        try
        {
            logger.LogDebug(
                "Siemens access profile service cleanup started. ServiceName={ServiceName}, ServiceType={ServiceType}",
                serviceName,
                service.GetType().Name);
            var result = service switch
            {
                IPlcProjectService project => await project.CleanupAsync(CancellationToken.None).ConfigureAwait(false),
                IPlcControlService plcControl => await plcControl.CleanupAsync(CancellationToken.None).ConfigureAwait(false),
                IUnitTestLifecycleService lifecycle => await lifecycle.CleanupAsync(CancellationToken.None).ConfigureAwait(false),
                IUnitTestEventStreamService eventStream => await eventStream.CleanupAsync(CancellationToken.None).ConfigureAwait(false),
                _ => OperationResult.Success()
            };

            if (result.IsUnsuccessful)
            {
                var diagnostic = CreateServiceCleanupDiagnostic(serviceName, result.Message ?? "Service cleanup failed.");
                DiagnosticEventHandler?.Invoke(this, new DiagnosticEventArgs(diagnostic));
                logger.LogWarning(
                    "Siemens access profile service cleanup returned failure. ServiceName={ServiceName}, State={State}, Message={Message}",
                    serviceName,
                    result.State,
                    result.Message);
            }
        }
        catch (Exception ex)
        {
            var diagnostic = CreateServiceCleanupDiagnostic(serviceName, ex.Message);
            DiagnosticEventHandler?.Invoke(this, new DiagnosticEventArgs(diagnostic));
            logger.LogError(
                ex,
                "Siemens access profile service cleanup failed. ServiceName={ServiceName}, ServiceType={ServiceType}",
                serviceName,
                service.GetType().Name);
        }
    }

    private Diagnostic CreateServiceCleanupDiagnostic(
        string serviceName,
        string details)
    {
        return SiemensDiagnostics.ConfigurationError(
            "SiemensProfile.ServiceCleanupFailed",
            $"Siemens access profile service cleanup failed for '{serviceName}'.",
            details,
            timeProvider.GetUtcNow());
    }

    private AccessHealth MapStateToHealth(
        AccessProfileState profileState,
        string reason)
    {
        var now = timeProvider.GetUtcNow();
        return profileState switch
        {
            AccessProfileState.Ready => AccessHealth.Healthy(now),
            AccessProfileState.Disposed => AccessHealth.Disposed(now),
            AccessProfileState.Invalidated => new AccessHealth(
                AccessHealthState.Invalidated,
                AccessHealthProblem.ContextLost,
                "Siemens.Profile.Invalidated",
                reason,
                now),
            _ => AccessHealth.NotEstablished(now)
        };
    }

    private IReadOnlyList<Diagnostic> MapCommunicationDiagnostics(
        IReadOnlyList<PlcsimDiagnostic> diagnostics,
        DiagnosticPhase phase)
    {
        return diagnostics.Select(diagnostic => MapCommunicationDiagnostic(diagnostic, phase)).ToArray();
    }

    private Diagnostic MapCommunicationDiagnostic(
        PlcsimDiagnostic diagnostic,
        DiagnosticPhase phase)
    {
        return SiemensPlcsimOperationMapping.ToDiagnostic(
            diagnostic,
            phase,
            timeProvider.GetUtcNow());
    }

}
