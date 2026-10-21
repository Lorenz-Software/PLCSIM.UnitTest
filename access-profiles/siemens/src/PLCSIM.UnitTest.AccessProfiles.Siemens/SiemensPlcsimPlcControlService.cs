using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensPlcsimPlcControlService : SiemensAccessProfileServiceBase, IPlcControlService
{
    private static readonly TimeSpan RuntimePrepareTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RuntimeControlTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RuntimeReleaseTimeout = TimeSpan.FromSeconds(30);

    private readonly IPlcsimAdvancedRuntimeBinding binding;
    private readonly ILogger<SiemensPlcsimPlcControlService> logger;
    private readonly SiemensProfileConfigurationParser configurationParser = new();
    private IPlcsimRuntimeSession? session;
    private PreparedRuntimeLifecycleConfiguration? preparedRuntimeLifecycle;
    private IReadOnlyList<PlcsimRuntimeTargetCandidate> currentTargets = Array.Empty<PlcsimRuntimeTargetCandidate>();
    private PlcsimRuntimeTargetCandidate? currentRuntime;

    public SiemensPlcsimPlcControlService(
        IPlcsimAdvancedRuntimeBinding binding,
        TimeProvider timeProvider,
        ILogger<SiemensPlcsimPlcControlService>? logger = null)
        : base(timeProvider)
    {
        this.binding = binding ?? throw new ArgumentNullException(nameof(binding));
        this.logger = logger ?? NullLogger<SiemensPlcsimPlcControlService>.Instance;
    }

    public event EventHandler<PlcStateChangedEventArgs>? PlcStateChangedEventHandler;

    public PlcState CurrentState { get; private set; } = new(
        RuntimeInstanceState.Unavailable,
        PlcOperatingState.Unavailable,
        Array.Empty<RuntimeTargetRef>(),
        ReadyForTransfer: false,
        ReadyForExecution: false);

    public async Task<OperationResult<RuntimeTargetDiscoveryResult>> DiscoverRuntimeTargetsAsync(
        PlcRuntimeDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = ParseConfiguration(request.Configuration);
        if (configuration is null)
        {
            return OperationResult<RuntimeTargetDiscoveryResult>.Failure("Siemens PLCSIM runtime configuration is invalid.");
        }

        var ensured = await EnsureSessionAsync(
            configuration: null,
            runtimeTarget: null,
            provisioningMode: null,
            cancellationToken).ConfigureAwait(false);
        if (ensured.IsUnsuccessful)
        {
            return OperationResult<RuntimeTargetDiscoveryResult>.Failure(ensured.Message ?? "Siemens PLCSIM runtime session is not available.");
        }

        var result = await session!.DiscoverRuntimeTargetsAsync(
            new PlcsimRuntimeDiscoveryRequest(CorrelationId: null),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            PublishDiagnostics(result.Diagnostics);
        }

        currentTargets = result.Candidates;
        var candidates = currentTargets.Select(MapRuntimeTarget).ToArray();
        var state = result.Succeeded
            ? candidates.Length == 0 ? TargetDiscoveryState.NoCandidates : TargetDiscoveryState.CandidatesAvailable
            : TargetDiscoveryState.Failed;
        ApplyCurrentState(CreateState(result.Succeeded ? currentRuntime?.State : PlcsimRuntimeControlState.Invalid, candidates));
        return SiemensPlcsimOperationMapping.ToOperationResult(
            result.Status,
            new RuntimeTargetDiscoveryResult(candidates, state, MapDiagnostics(result.Diagnostics)),
            $"Siemens PLCSIM runtime discovery returned {result.Status}.");
    }

    public async Task<OperationResult<PreparedPlcRuntime>> PrepareRuntimeAsync(
        PlcRuntimePrepareRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = ParseConfiguration(request.Configuration, request.ProvisioningMode);
        if (configuration is null)
        {
            return OperationResult<PreparedPlcRuntime>.Failure("Siemens PLCSIM runtime configuration is invalid.");
        }

        var ensured = await EnsureSessionAsync(
            configuration,
            request.RuntimeTarget,
            request.ProvisioningMode,
            cancellationToken).ConfigureAwait(false);
        if (ensured.IsUnsuccessful)
        {
            return OperationResult<PreparedPlcRuntime>.Failure(ensured.Message ?? "Siemens PLCSIM runtime session is not available.");
        }
        var effectiveStoragePath = ResolveRuntimeStoragePath(configuration, request.ProvisioningMode);
        var bindingRequest = new PlcsimRuntimePrepareRequest(
            CreateRuntimeReference(configuration, request.RuntimeTarget, request.ProvisioningMode),
            new PlcsimRuntimeStoragePreference(effectiveStoragePath),
            MapProvisioningMode(request.ProvisioningMode),
            MapCpuType(configuration.RuntimeRef.CpuType),
            RuntimePrepareTimeout,
            CorrelationId: null);
        logger.LogInformation(
            "Preparing Siemens PLC runtime. ConfigurationFingerprint={ConfigurationFingerprint}, ProvisioningMode={ProvisioningMode}, RuntimeTargetId={RuntimeTargetId}, InstanceName={InstanceName}, CpuType={CpuType}, StoragePath={StoragePath}",
            SiemensConfigurationLogFingerprint.Create(request.Configuration),
            bindingRequest.ProvisioningMode,
            bindingRequest.RuntimeReference?.Id,
            bindingRequest.RuntimeReference?.InstanceName,
            bindingRequest.CpuType,
            bindingRequest.StoragePreference.StoragePath);
        var result = await session!.PrepareRuntimeAsync(
            bindingRequest,
            cancellationToken).ConfigureAwait(false);
        PublishDiagnostics(result.Diagnostics);
        if (!result.Succeeded || result.Runtime is null)
        {
            ApplyCurrentState(CreateState(PlcsimRuntimeControlState.Invalid, currentTargets.Select(MapRuntimeTarget).ToArray()));
            return SiemensPlcsimOperationMapping.ToOperationResult<PreparedPlcRuntime>(
                result.Status,
                null,
                $"Siemens PLCSIM runtime prepare returned {result.Status}.");
        }

        preparedRuntimeLifecycle = new PreparedRuntimeLifecycleConfiguration(
            bindingRequest.StoragePreference.StoragePath);
        currentRuntime = result.Runtime;
        currentTargets = MergeRuntime(currentTargets, result.Runtime);
        var context = new PlcRuntimeContext(
            new RuntimeContextId(session.Runtime.SessionId),
            CreateDisplayName(result.Runtime),
            result.Runtime.Reference.InstanceName ?? result.Runtime.Reference.Id);
        ApplyCurrentState(CreateState(result.Runtime.State, currentTargets.Select(MapRuntimeTarget).ToArray()));
        return OperationResult<PreparedPlcRuntime>.Success(
            new PreparedPlcRuntime(context, MapRuntimeTarget(result.Runtime)));
    }

    internal async Task<OperationResult<PreparedPlcRuntime>> ReconnectRuntimeAsync(
        PlcRuntimeReconnectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = ParseConfiguration(request.Configuration, request.ProvisioningMode);
        if (configuration is null)
        {
            return OperationResult<PreparedPlcRuntime>.Failure("Siemens PLCSIM runtime configuration is invalid.");
        }

        if (preparedRuntimeLifecycle is null || currentRuntime is null)
        {
            return OperationResult<PreparedPlcRuntime>.Failure("Siemens PLCSIM runtime is not prepared.");
        }

        if (session is null)
        {
            return OperationResult<PreparedPlcRuntime>.Failure("Siemens PLCSIM runtime session is not available.");
        }

        var revalidated = await session.RevalidateRuntimeAsync(
            new PlcsimRuntimeRevalidationRequest(
                currentRuntime.Reference,
                RuntimePrepareTimeout,
                CorrelationId: null),
            cancellationToken).ConfigureAwait(false);
        PublishDiagnostics(revalidated.Diagnostics);
        if (!revalidated.Succeeded || revalidated.Runtime is null)
        {
            return SiemensPlcsimOperationMapping.ToOperationResult<PreparedPlcRuntime>(
                revalidated.Status,
                null,
                $"Siemens PLCSIM runtime revalidation returned {revalidated.Status}.");
        }

        currentRuntime = revalidated.Runtime;
        currentTargets = MergeRuntime(currentTargets, revalidated.Runtime);
        var context = new PlcRuntimeContext(
            new RuntimeContextId(session.Runtime.SessionId),
            CreateDisplayName(revalidated.Runtime),
            revalidated.Runtime.Reference.InstanceName ?? revalidated.Runtime.Reference.Id);
        ApplyCurrentState(CreateState(revalidated.Runtime.State, currentTargets.Select(MapRuntimeTarget).ToArray()));
        return OperationResult<PreparedPlcRuntime>.Success(
            new PreparedPlcRuntime(context, MapRuntimeTarget(revalidated.Runtime)));
    }

    public async Task<OperationResult<RuntimeTargetResolution>> ResolveRuntimeTargetAsync(
        PlcRuntimeContext runtime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session is null)
        {
            return OperationResult<RuntimeTargetResolution>.Failure("Siemens PLCSIM runtime is not prepared.");
        }

        var result = await session.ObserveRuntimeAsync(
            new PlcsimRuntimeObservationRequest(CorrelationId: null),
            cancellationToken).ConfigureAwait(false);
        PublishDiagnostics(result.Diagnostics);
        if (result.Runtime is not null)
        {
            currentRuntime = result.Runtime;
            currentTargets = MergeRuntime(currentTargets, result.Runtime);
        }

        var candidates = currentTargets.Select(MapRuntimeTarget).ToArray();
        var selected = currentRuntime is null ? null : MapRuntimeTarget(currentRuntime);
        var state = result.Succeeded && selected is not null
            ? TargetResolutionState.Resolved
            : TargetResolutionState.InvalidConfiguration;
        ApplyCurrentState(CreateState(currentRuntime?.State, candidates));
        return result.Succeeded
            ? OperationResult<RuntimeTargetResolution>.Success(new RuntimeTargetResolution(candidates, selected, state))
            : OperationResult<RuntimeTargetResolution>.Failure($"Siemens PLCSIM runtime observation returned {result.Status}.");
    }

    public async Task<OperationResult<PlcState>> StartAsync(
        PlcRuntimeContext runtime,
        RuntimeTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session is null)
        {
            return OperationResult<PlcState>.Failure("Siemens PLCSIM runtime is not prepared.");
        }

        var result = await session.StartRuntimeAsync(
            new PlcsimRuntimeControlRequest(session.Runtime.SessionId, RuntimeControlTimeout, CorrelationId: null),
            cancellationToken).ConfigureAwait(false);
        PublishDiagnostics(result.Diagnostics);
        var state = CreateState(result.State, currentTargets.Select(MapRuntimeTarget).ToArray());
        return SiemensPlcsimOperationMapping.ToOperationResult(result.Status, state, $"Siemens PLCSIM runtime start returned {result.Status}.");
    }

    public async Task<OperationResult<PlcState>> StopAsync(
        PlcRuntimeContext runtime,
        RuntimeTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session is null)
        {
            return OperationResult<PlcState>.Failure("Siemens PLCSIM runtime is not prepared.");
        }

        var result = await session.StopRuntimeAsync(
            new PlcsimRuntimeControlRequest(session.Runtime.SessionId, RuntimeControlTimeout, CorrelationId: null),
            cancellationToken).ConfigureAwait(false);
        PublishDiagnostics(result.Diagnostics);
        var state = CreateState(result.State, currentTargets.Select(MapRuntimeTarget).ToArray());
        return SiemensPlcsimOperationMapping.ToOperationResult(result.Status, state, $"Siemens PLCSIM runtime stop returned {result.Status}.");
    }

    public async Task<OperationResult<PlcRuntimeReleaseOutcome>> ReleaseRuntimeAsync(
        PLCSIM.UnitTest.Common.Services.PlcRuntimeReleaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var cleanup = request.ReleaseMode == RuntimeResourceReleaseMode.DestroyResource
            ? PlcsimRuntimeCleanupPreference.Release
            : PlcsimRuntimeCleanupPreference.Keep;
        if (session is null && preparedRuntimeLifecycle is not null && currentRuntime is not null)
        {
            var reopened = await OpenSessionAsync(
                currentRuntime.Reference,
                new PlcsimRuntimeStoragePreference(preparedRuntimeLifecycle.StoragePath),
                cleanup,
                cancellationToken).ConfigureAwait(false);
            if (reopened.IsUnsuccessful)
            {
                return OperationResult<PlcRuntimeReleaseOutcome>.Failure(
                    reopened.Message ?? "Siemens PLCSIM runtime session could not be reopened for cleanup.");
            }
        }

        var releasingSession = DetachSession();
        if (releasingSession is null)
        {
            ApplyCurrentState(new PlcState(
                RuntimeInstanceState.Unavailable,
                PlcOperatingState.Unavailable,
                Array.Empty<RuntimeTargetRef>(),
                ReadyForTransfer: false,
                ReadyForExecution: false));
            return request.ReleaseMode == RuntimeResourceReleaseMode.KeepResource
                ? OperationResult<PlcRuntimeReleaseOutcome>.Success(
                    PlcRuntimeReleaseOutcome.ResourceKept,
                    "Siemens PLCSIM technical runtime access is already released.")
                : OperationResult<PlcRuntimeReleaseOutcome>.Failure(
                    "Siemens PLCSIM runtime destruction cannot be executed or verified without a prepared runtime session.");
        }

        PlcsimRuntimeReleaseResult result;
        try
        {
            result = await releasingSession.ReleaseRuntimeAsync(
                new Contracts.PlcsimRuntimeReleaseRequest(
                    releasingSession.Runtime.SessionId,
                    cleanup,
                    RuntimeReleaseTimeout,
                    CorrelationId: null),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await CloseSessionAsync(releasingSession, CancellationToken.None).ConfigureAwait(false);
            ApplyCurrentState(new PlcState(
                RuntimeInstanceState.Unavailable,
                PlcOperatingState.Unavailable,
                Array.Empty<RuntimeTargetRef>(),
                ReadyForTransfer: false,
                ReadyForExecution: false));
            throw;
        }

        PublishDiagnostics(result.Diagnostics);
        var close = await CloseSessionAsync(releasingSession, cancellationToken).ConfigureAwait(false);
        ApplyCurrentState(new PlcState(
            RuntimeInstanceState.Unavailable,
            PlcOperatingState.Unavailable,
            Array.Empty<RuntimeTargetRef>(),
            ReadyForTransfer: false,
            ReadyForExecution: false));
        if (close.IsUnsuccessful)
        {
            if (!result.Succeeded)
            {
                return OperationResult<PlcRuntimeReleaseOutcome>.Failure(
                    $"Siemens PLCSIM runtime release returned {result.Status}; {close.Message}");
            }

            var releasedOutcome = request.ReleaseMode == RuntimeResourceReleaseMode.DestroyResource
                ? PlcRuntimeReleaseOutcome.ResourceDestroyed
                : PlcRuntimeReleaseOutcome.ResourceKept;
            return OperationResult<PlcRuntimeReleaseOutcome>.Failure(
                releasedOutcome,
                close.Message ?? "Siemens PLCSIM runtime session close failed.");
        }

        if (!result.Succeeded)
        {
            return OperationResult<PlcRuntimeReleaseOutcome>.Failure(
                $"Siemens PLCSIM runtime release returned {result.Status}.");
        }

        var outcome = request.ReleaseMode == RuntimeResourceReleaseMode.DestroyResource
            ? PlcRuntimeReleaseOutcome.ResourceDestroyed
            : PlcRuntimeReleaseOutcome.ResourceKept;
        var release = OperationResult<PlcRuntimeReleaseOutcome>.Success(
            outcome,
            $"Siemens PLCSIM runtime release returned {result.Status}.");
        if (release.IsSuccessful)
        {
            SetHealth(AccessHealth.NotEstablished(TimeProvider.GetUtcNow()));
        }

        return release;
    }

    public async Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cleanup = await CloseSessionAsync(cancellationToken).ConfigureAwait(false);

        SetDisposed("Siemens PLCSIM runtime service disposed.");
        return cleanup.IsSuccessful
            ? OperationResult.Success("Siemens PLCSIM runtime service cleaned up.")
            : cleanup;
    }

    protected override AccessServiceKind GetServiceKind() => AccessServiceKind.PlcControl;

    private async Task<OperationResult> EnsureSessionAsync(
        SiemensProfileConfiguration? configuration,
        RuntimeTargetRef? runtimeTarget,
        RuntimeProvisioningMode? provisioningMode,
        CancellationToken cancellationToken)
    {
        if (session is not null)
        {
            return OperationResult.Success();
        }

        return await OpenSessionAsync(
            configuration is null || provisioningMode is null
                ? null
                : CreateRuntimeReference(configuration, runtimeTarget, provisioningMode.Value),
            new PlcsimRuntimeStoragePreference(configuration?.RuntimeRef.StoragePath),
            PlcsimRuntimeCleanupPreference.Keep,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationResult> OpenSessionAsync(
        PlcsimRuntimeReference? runtimeReference,
        PlcsimRuntimeStoragePreference storagePreference,
        PlcsimRuntimeCleanupPreference cleanupPreference,
        CancellationToken cancellationToken)
    {
        var result = await binding.OpenRuntimeSessionAsync(
            new PlcsimRuntimeSessionOpenRequest(
                new PlcsimRuntimeApiExpectation(
                    ProductVersion: binding.Declaration.Runtimes.FirstOrDefault()?.Version ?? string.Empty,
                    AccessApiVersion: binding.Declaration.AccessApis.FirstOrDefault()?.AccessApiVersion ?? string.Empty,
                    Locality: SiemensRuntimeLocality.Local),
                runtimeReference,
                storagePreference,
                cleanupPreference,
                CorrelationId: null),
            cancellationToken).ConfigureAwait(false);
        PublishDiagnostics(result.Diagnostics);
        if (!result.Succeeded || result.Session is null)
        {
            return SiemensPlcsimOperationMapping.ToOperationResult(result.Status, $"Siemens PLCSIM runtime session open returned {result.Status}.");
        }

        session = result.Session;
        session.HealthChanged += OnSessionHealthChanged;
        session.ProgressChanged += OnSessionProgressChanged;
        session.RuntimeStateChanged += OnSessionRuntimeStateChanged;
        SetHealth(MapHealth(session.Health, "Siemens PLCSIM runtime session opened."));
        return OperationResult.Success();
    }

    private async Task<OperationResult> CloseSessionAsync(CancellationToken cancellationToken)
    {
        var closingSession = DetachSession();
        return closingSession is null
            ? OperationResult.Success()
            : await CloseSessionAsync(closingSession, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationResult> CloseSessionAsync(
        IPlcsimRuntimeSession closingSession,
        CancellationToken cancellationToken)
    {
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
            var message = $"Siemens PLCSIM runtime session close failed: {ex.GetBaseException().Message}";
            ReportDiagnostic(CreateRuntimeCleanupDiagnostic(message));
            SetHealth(new AccessHealth(
                AccessHealthState.Invalidated,
                AccessHealthProblem.ContextLost,
                "Siemens.PlcsimAdvanced.Runtime.CloseFailed",
                "Siemens PLCSIM runtime session close failed.",
                TimeProvider.GetUtcNow()));
            return OperationResult.Failure(message);
        }
    }

    private IPlcsimRuntimeSession? DetachSession(bool clearPreparedState = true)
    {
        var detachedSession = session;
        if (detachedSession is null)
        {
            return null;
        }

        UnsubscribeSessionEvents(detachedSession);
        if (ReferenceEquals(session, detachedSession))
        {
            session = null;
            if (clearPreparedState)
            {
                preparedRuntimeLifecycle = null;
                currentRuntime = null;
                currentTargets = Array.Empty<PlcsimRuntimeTargetCandidate>();
            }
        }

        return detachedSession;
    }

    private void UnsubscribeSessionEvents(IPlcsimRuntimeSession runtimeSession)
    {
        runtimeSession.HealthChanged -= OnSessionHealthChanged;
        runtimeSession.ProgressChanged -= OnSessionProgressChanged;
        runtimeSession.RuntimeStateChanged -= OnSessionRuntimeStateChanged;
    }

    private void OnSessionHealthChanged(object? sender, PlcsimRuntimeHealthChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, session))
        {
            return;
        }

        SetHealth(MapHealth(args.Current, args.Message));
    }

    private void OnSessionProgressChanged(object? sender, PlcsimRuntimeProgressSnapshot args)
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

    private void OnSessionRuntimeStateChanged(object? sender, PlcsimRuntimeStateChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, session))
        {
            return;
        }

        PublishDiagnostics(args.Change.Diagnostics);
        UpdateCurrentRuntimeState(args.Change.RuntimeReference, args.Change.CurrentState);
        ApplyCurrentState(CreateState(args.Change.CurrentState, currentTargets.Select(MapRuntimeTarget).ToArray()));
    }

    private void ApplyCurrentState(PlcState state)
    {
        if (CurrentState == state)
        {
            return;
        }

        CurrentState = state;
        PlcStateChangedEventHandler?.Invoke(this, new PlcStateChangedEventArgs(state));
    }

    private void UpdateCurrentRuntimeState(
        RuntimeTargetRef target,
        PlcsimRuntimeControlState state)
    {
        var runtime = ResolvePlcsimTarget(target) ?? currentRuntime;
        if (runtime is null)
        {
            return;
        }

        currentRuntime = runtime with { State = state };
        currentTargets = MergeRuntime(currentTargets, currentRuntime);
    }

    private void UpdateCurrentRuntimeState(
        PlcsimRuntimeReference? reference,
        PlcsimRuntimeControlState state)
    {
        var runtime = ResolvePlcsimTarget(reference) ?? currentRuntime;
        if (runtime is null)
        {
            return;
        }

        currentRuntime = runtime with { State = state };
        currentTargets = MergeRuntime(currentTargets, currentRuntime);
    }

    private PlcsimRuntimeTargetCandidate? ResolvePlcsimTarget(RuntimeTargetRef target)
    {
        return currentTargets.FirstOrDefault(candidate =>
            string.Equals(candidate.Reference.Id, target.Id.Value, StringComparison.OrdinalIgnoreCase));
    }

    private PlcsimRuntimeTargetCandidate? ResolvePlcsimTarget(PlcsimRuntimeReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        return currentTargets.FirstOrDefault(candidate =>
            (!string.IsNullOrWhiteSpace(reference.Id)
                && string.Equals(candidate.Reference.Id, reference.Id, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(reference.InstanceName)
                && string.Equals(candidate.Reference.InstanceName, reference.InstanceName, StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<PlcsimRuntimeTargetCandidate> MergeRuntime(
        IReadOnlyList<PlcsimRuntimeTargetCandidate> targets,
        PlcsimRuntimeTargetCandidate runtime)
    {
        var updated = targets
            .Where(candidate => !string.Equals(candidate.Reference.Id, runtime.Reference.Id, StringComparison.OrdinalIgnoreCase))
            .Append(runtime)
            .ToArray();
        return updated;
    }

    private SiemensProfileConfiguration? ParseConfiguration(
        AccessProfileConfig configuration,
        RuntimeProvisioningMode? provisioningMode = null)
    {
        var parsed = configurationParser.Parse(
            configuration,
            SiemensProfileConfigurationValidationMode.Partial,
            provisioningMode);
        foreach (var diagnostic in parsed.Diagnostics)
        {
            ReportDiagnostic(diagnostic);
        }

        return parsed.IsValid ? parsed.Configuration : null;
    }

    private static PlcsimRuntimeReference? CreateRuntimeReference(
        SiemensProfileConfiguration configuration,
        RuntimeTargetRef? selectedRuntimeTarget,
        RuntimeProvisioningMode provisioningMode)
    {
        if (provisioningMode == RuntimeProvisioningMode.UseExisting)
        {
            var runtimeId = selectedRuntimeTarget?.Id.Value ?? configuration.RuntimeRef.Id;
            return runtimeId is null
                ? null
                : new PlcsimRuntimeReference(
                    runtimeId,
                    selectedRuntimeTarget?.TechnicalName);
        }

        return configuration.RuntimeRef.InstanceName is null
            ? null
            : new PlcsimRuntimeReference(
                Id: null,
                InstanceName: configuration.RuntimeRef.InstanceName);
    }

    private static RuntimeTargetRef MapRuntimeTarget(PlcsimRuntimeTargetCandidate target)
    {
        return new RuntimeTargetRef(
            new RuntimeTargetId(target.Reference.Id ?? target.Reference.InstanceName ?? "runtime"),
            CreateDisplayName(target),
            target.Reference.InstanceName);
    }

    private static string CreateDisplayName(PlcsimRuntimeTargetCandidate target)
    {
        return target.Reference.InstanceName
            ?? target.Reference.Id
            ?? "Siemens PLCSIM Runtime";
    }

    private static PlcState CreateState(
        PlcsimRuntimeControlState? state,
        IReadOnlyList<RuntimeTargetRef> targets)
    {
        var instanceState = state switch
        {
            PlcsimRuntimeControlState.Off or null => RuntimeInstanceState.Unavailable,
            PlcsimRuntimeControlState.Booting => RuntimeInstanceState.Starting,
            PlcsimRuntimeControlState.ShuttingDown => RuntimeInstanceState.Stopping,
            PlcsimRuntimeControlState.Invalid => RuntimeInstanceState.Faulted,
            PlcsimRuntimeControlState.Stop
                or PlcsimRuntimeControlState.Startup
                or PlcsimRuntimeControlState.Run
                or PlcsimRuntimeControlState.Freeze
                or PlcsimRuntimeControlState.Hold => RuntimeInstanceState.Available,
            _ => RuntimeInstanceState.Unknown
        };
        var operatingState = state switch
        {
            PlcsimRuntimeControlState.Stop => PlcOperatingState.Stopped,
            PlcsimRuntimeControlState.Startup => PlcOperatingState.Starting,
            PlcsimRuntimeControlState.Run => PlcOperatingState.Running,
            PlcsimRuntimeControlState.Freeze => PlcOperatingState.Frozen,
            PlcsimRuntimeControlState.Hold => PlcOperatingState.Held,
            PlcsimRuntimeControlState.Off
                or PlcsimRuntimeControlState.Booting
                or PlcsimRuntimeControlState.ShuttingDown
                or null => PlcOperatingState.Unavailable,
            _ => PlcOperatingState.Unknown
        };
        return new PlcState(
            instanceState,
            operatingState,
            targets,
            ReadyForTransfer: instanceState == RuntimeInstanceState.Available
                && operatingState is PlcOperatingState.Stopped
                    or PlcOperatingState.Running
                    or PlcOperatingState.Frozen
                    or PlcOperatingState.Held,
            ReadyForExecution: instanceState == RuntimeInstanceState.Available
                && operatingState == PlcOperatingState.Running);
    }

    private AccessHealth MapHealth(
        PlcsimRuntimeHealthState state,
        string message)
    {
        var now = TimeProvider.GetUtcNow();
        return state switch
        {
            PlcsimRuntimeHealthState.Healthy => AccessHealth.Healthy(now),
            PlcsimRuntimeHealthState.Degraded => new AccessHealth(
                AccessHealthState.RevalidationRequired,
                AccessHealthProblem.RevalidationRequired,
                "Siemens.Plcsim.Runtime.Degraded",
                message,
                now),
            PlcsimRuntimeHealthState.RevalidationRequired => new AccessHealth(
                AccessHealthState.RevalidationRequired,
                AccessHealthProblem.RevalidationRequired,
                "Siemens.Plcsim.Runtime.RevalidationRequired",
                message,
                now),
            PlcsimRuntimeHealthState.Invalidated => new AccessHealth(
                AccessHealthState.Invalidated,
                AccessHealthProblem.ContextLost,
                "Siemens.Plcsim.Runtime.Invalidated",
                message,
                now),
            PlcsimRuntimeHealthState.Faulted => new AccessHealth(
                AccessHealthState.Invalidated,
                AccessHealthProblem.InternalFailure,
                "Siemens.Plcsim.Runtime.Faulted",
                message,
                now),
            PlcsimRuntimeHealthState.Disposed => AccessHealth.Disposed(now),
            _ => AccessHealth.NotEstablished(now)
        };
    }

    private static string? ResolveRuntimeStoragePath(
        SiemensProfileConfiguration configuration,
        RuntimeProvisioningMode provisioningMode)
    {
        if (provisioningMode != RuntimeProvisioningMode.CreateOwned)
        {
            return null;
        }

        return configuration.RuntimeRef.StoragePath
            ?? CreateDefaultRuntimeStoragePath(configuration.RuntimeRef.InstanceName);
    }

    private static string CreateDefaultRuntimeStoragePath(string? runtimeInstanceName)
    {
        var runtimeStorageKey = string.Concat(
            CreatePathSegment(runtimeInstanceName ?? "runtime"),
            "-",
            Guid.NewGuid().ToString("N"));
        return Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            "runtimes",
            runtimeStorageKey);
    }

    private static string CreatePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character =>
            invalidChars.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }

    private static PlcsimRuntimeProvisioningMode MapProvisioningMode(RuntimeProvisioningMode mode)
    {
        return mode == RuntimeProvisioningMode.CreateOwned
            ? PlcsimRuntimeProvisioningMode.CreateOwned
            : PlcsimRuntimeProvisioningMode.UseExisting;
    }

    private static PlcsimRuntimeCpuType MapCpuType(SiemensRuntimeCpuType cpuType)
    {
        return cpuType switch
        {
            SiemensRuntimeCpuType.S71500 => PlcsimRuntimeCpuType.S71500,
            _ => PlcsimRuntimeCpuType.S71500
        };
    }

    private IReadOnlyList<Diagnostic> MapDiagnostics(IReadOnlyList<PlcsimDiagnostic> diagnostics)
    {
        return diagnostics.Select(MapDiagnostic).ToArray();
    }

    private void PublishDiagnostics(IReadOnlyList<PlcsimDiagnostic> diagnostics)
    {
        foreach (var diagnostic in MapDiagnostics(diagnostics))
        {
            ReportDiagnostic(diagnostic);
        }
    }

    private Diagnostic CreateRuntimeCleanupDiagnostic(string message)
    {
        return new Diagnostic(
            "Siemens.PlcsimAdvanced.RuntimeCleanupFailed",
            DiagnosticCategory.Cleanup,
            Microsoft.Extensions.Logging.LogLevel.Error,
            DiagnosticSource.AccessProfile,
            DiagnosticPhase.Cleanup,
            message,
            new DiagnosticCorrelation(ServiceId: "Siemens.PlcsimAdvanced"),
            TimeProvider.GetUtcNow());
    }

    private Diagnostic MapDiagnostic(PlcsimDiagnostic diagnostic)
    {
        return SiemensPlcsimOperationMapping.ToDiagnostic(
            diagnostic,
            DiagnosticPhase.RuntimeProvisioning,
            TimeProvider.GetUtcNow());
    }

    private sealed record PreparedRuntimeLifecycleConfiguration(
        string? StoragePath);

}
