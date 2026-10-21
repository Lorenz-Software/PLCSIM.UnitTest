using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed partial class BeckhoffPlcControlService : BeckhoffServiceBase, IPlcControlService, Engineering.IEngineeringRuntimeTransfer
{
    internal static readonly TimeSpan SystemMonitoringInterval = TimeSpan.FromMilliseconds(250);
    private readonly ILocalUsermodeRuntimeCatalog catalog;
    private readonly Func<string, int?, IAdsRuntimeAccess> openAccess;
    private readonly ProjectOwnershipMode projectOwnership;
    private readonly TimeSpan monitoringInterval;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly object publicationGate = new();
    private PreparedRuntimeTarget? prepared;
    private RuntimeObservationContext? observation;
    private int closing;

    internal BeckhoffPlcControlService(AccessProfileContext context, ProjectOwnershipMode ownership)
        : this(context, ownership, new LocalUsermodeRuntimeCatalog(),
            (netId, port) => new AdsRuntimeAccess(new AdsClientFactory(context.LoggerFactory), netId, port)) { }

    internal BeckhoffPlcControlService(AccessProfileContext context, ProjectOwnershipMode ownership,
        ILocalUsermodeRuntimeCatalog catalog, Func<string, int?, IAdsRuntimeAccess> openAccess,
        TimeSpan? monitoringInterval = null, RuntimeProvisioningStrategy strategy = RuntimeProvisioningStrategy.ReuseExisting)
        : base(context)
    {
        projectOwnership = ownership;
        this.catalog = catalog;
        this.openAccess = openAccess;
        this.monitoringInterval = monitoringInterval ?? SystemMonitoringInterval;
        if (this.monitoringInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(monitoringInterval));
        ProvisioningStrategy = strategy;
    }

    internal RuntimeProvisioningStrategy ProvisioningStrategy { get; }
    public event EventHandler<PlcStateChangedEventArgs>? PlcStateChangedEventHandler;
    public PlcState CurrentState { get; private set; } = new(RuntimeInstanceState.Unknown, PlcOperatingState.Unknown, [], false, false);

    public async Task<OperationResult<RuntimeTargetDiscoveryResult>> DiscoverRuntimeTargetsAsync(
        PlcRuntimeDiscoveryRequest request, CancellationToken cancellationToken)
    {
        EnsureRuntimeActive(cancellationToken);
        try
        {
            _ = RuntimeAccessConfiguration.Parse(request.Configuration, projectOwnership);
            var targets = await Task.Run(() => catalog.Discover(cancellationToken), cancellationToken).ConfigureAwait(false);
            var candidates = targets.Select(target => target.ToReference()).ToArray();
            RuntimeTargetRef? defaultTarget = null;
            try
            {
                var localDefault = catalog.ResolveDefault(cancellationToken);
                defaultTarget = targets.SingleOrDefault(target => target.NetId == localDefault.NetId
                    && LocalUsermodeRuntimeCatalog.SameDirectory(target.ConfigurationDirectory, localDefault.ConfigurationDirectory))?.ToReference();
            }
            catch (Exception error) when (IsAccessFailure(error))
            {
                Logger.LogDebug(error, "No local standard Usermode Runtime could be designated as default.");
            }
            return OperationResult<RuntimeTargetDiscoveryResult>.Success(new(candidates,
                candidates.Length == 0 ? TargetDiscoveryState.NoCandidates : TargetDiscoveryState.CandidatesAvailable, [], defaultTarget));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsAccessFailure(error))
        {
            var diagnostic = ReportDiagnostic("Beckhoff.Runtime.DiscoveryFailed", error.Message,
                DiagnosticCategory.Environment, DiagnosticPhase.RuntimeProvisioning);
            return OperationResult<RuntimeTargetDiscoveryResult>.Failure(
                new([], TargetDiscoveryState.Failed, [diagnostic]), error.Message);
        }
    }

    public async Task<OperationResult<PreparedPlcRuntime>> PrepareRuntimeAsync(
        PlcRuntimePrepareRequest request, CancellationToken cancellationToken)
    {
        EnsureRuntimeActive(cancellationToken);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureRuntimeActive(cancellationToken);
            if (prepared is not null) return OperationResult<PreparedPlcRuntime>.Failure("Release or reconnect the prepared Runtime first.");
            if (!Enum.IsDefined(request.ProvisioningMode)) throw new ArgumentException("Invalid provisioning mode.");
            if (request.ProvisioningMode == RuntimeProvisioningMode.CreateOwned)
                return await Unsupported<PreparedPlcRuntime>("dedicated Runtime creation", cancellationToken).ConfigureAwait(false);
            var settings = RuntimeAccessConfiguration.Parse(request.Configuration, projectOwnership);
            var netId = RuntimeAccessConfiguration.NormalizeNetId(request.RuntimeTarget?.Id.Value ?? settings.NetId
                ?? throw new ArgumentException("Select an existing local Usermode Runtime."));
            if (settings.NetId is not null && netId != settings.NetId)
                throw new ArgumentException("The requested target differs from the configured AMS Net ID.");

            Logger.LogInformation("Preparing runtime access: Target={NetId}:{Port}, Ownership={Ownership}, Provisioning={Provisioning}",
                netId, settings.PlcPort, projectOwnership, request.ProvisioningMode);
            var access = openAccess(netId, settings.PlcPort);
            RuntimeObservationContext? candidate = null;
            try
            {
                var identity = await access.ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
                var local = VerifyIdentity(identity, netId, null, cancellationToken);
                var descriptor = new PreparedRuntimeTarget(local, settings.PlcPort, request.ProvisioningMode, ProvisioningStrategy,
                    new(new(new RuntimeContextId(Guid.NewGuid().ToString("N")), local.Name, local.Name), local.ToReference()))
                    { ConfiguredPlcPort = settings.PlcPort };
                candidate = new(descriptor, access, Context.TimeProvider);
                access.PlcStateChanged += candidate.OnPlcStateChanged;
                access.SystemStateInvalidated += candidate.OnSystemStateInvalidated;
                access.PlcStateInvalidated += candidate.OnPlcStateInvalidated;
                await ObserveAsync(candidate, true, cancellationToken).ConfigureAwait(false);
                if (!candidate.SystemObserved) throw new IOException(candidate.Problem ?? "The selected system could not be observed.");
                if (projectOwnership == ProjectOwnershipMode.ExternallyManaged
                    && candidate.LastSystem == AdsState.Run && candidate.PlcPortMissing)
                    throw new IOException($"TwinCAT system {netId} is in RUN, but PLC port {settings.PlcPort} is unavailable (ADS TargetPortNotFound). "
                        + "Verify the configured PLC port and load the PLC application in XAE before preparing external access.");
                cancellationToken.ThrowIfCancellationRequested();
                prepared = descriptor;
                Attach(candidate);
                return OperationResult<PreparedPlcRuntime>.Success(descriptor.Result);
            }
            catch
            {
                prepared = null;
                try { await DisposeObservationAsync(candidate, access).ConfigureAwait(false); }
                finally { PublishDetached(); }
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsAccessFailure(error)) { return Fail<PreparedPlcRuntime>("PrepareFailed", error); }
        finally { operations.Release(); }
    }

    internal async Task<OperationResult<PreparedPlcRuntime>> ReconnectRuntimeAsync(
        PlcRuntimeReconnectRequest request, CancellationToken cancellationToken, Func<Task>? releaseDependentAccess = null)
    {
        EnsureRuntimeActive(cancellationToken);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureRuntimeActive(cancellationToken);
            var descriptor = RequirePrepared(request.Runtime, request.RuntimeTarget);
            var settings = RuntimeAccessConfiguration.Parse(request.Configuration, projectOwnership);
            if (descriptor.Mode != request.ProvisioningMode || (settings.PlcPort != descriptor.PlcPort
                && !(projectOwnership == ProjectOwnershipMode.ApplicationManaged && settings.PlcPort is null))
                || settings.NetId is not null && settings.NetId != descriptor.Local.NetId)
                throw new ArgumentException("Reconnect cannot change the prepared Runtime identity, PLC port or provisioning mode.");
            cancellationToken.ThrowIfCancellationRequested();
            // Cancellation is accepted only before detaching, as required by the public contract.
            Logger.LogInformation("Reconnecting runtime access: Target={NetId}:{Port}", descriptor.Local.NetId, descriptor.PlcPort);
            if (releaseDependentAccess is not null) await releaseDependentAccess().ConfigureAwait(false);
            await DetachAsync().ConfigureAwait(false);
            var access = openAccess(descriptor.Local.NetId, descriptor.PlcPort);
            RuntimeObservationContext? candidate = null;
            try
            {
                _ = VerifyIdentity(await access.ReadIdentityAsync(CancellationToken.None).ConfigureAwait(false),
                    descriptor.Local.NetId, descriptor.Local, CancellationToken.None);
                candidate = new(descriptor, access, Context.TimeProvider);
                access.PlcStateChanged += candidate.OnPlcStateChanged;
                access.SystemStateInvalidated += candidate.OnSystemStateInvalidated;
                access.PlcStateInvalidated += candidate.OnPlcStateInvalidated;
                await ObserveAsync(candidate, true, CancellationToken.None).ConfigureAwait(false);
                if (!candidate.SystemObserved) throw new IOException(candidate.Problem ?? "Runtime reconnect could not observe the system.");
                Attach(candidate);
                Logger.LogInformation("Runtime reconnect completed: Target={NetId}:{Port}", descriptor.Local.NetId, descriptor.PlcPort);
                return OperationResult<PreparedPlcRuntime>.Success(descriptor.Result);
            }
            catch
            {
                try { await DisposeObservationAsync(candidate, access).ConfigureAwait(false); }
                finally { PublishDetached(); }
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsAccessFailure(error)) { return Fail<PreparedPlcRuntime>("ReconnectFailed", error); }
        finally { operations.Release(); }
    }

    public async Task<OperationResult<RuntimeTargetResolution>> ResolveRuntimeTargetAsync(
        PlcRuntimeContext runtime, CancellationToken cancellationToken)
    {
        EnsureRuntimeActive(cancellationToken);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureRuntimeActive(cancellationToken);
            var descriptor = RequirePrepared(runtime, null);
            return OperationResult<RuntimeTargetResolution>.Success(new([descriptor.Result.RuntimeTarget],
                descriptor.Result.RuntimeTarget, TargetResolutionState.Resolved));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return OperationResult<RuntimeTargetResolution>.Failure(error.Message); }
        finally { operations.Release(); }
    }

    public Task<OperationResult<PlcState>> StartAsync(PlcRuntimeContext runtime, RuntimeTargetRef target, CancellationToken cancellationToken) =>
        WriteStateAsync(runtime, target, AdsState.Run, cancellationToken);

    public Task<OperationResult<PlcState>> StopAsync(PlcRuntimeContext runtime, RuntimeTargetRef target, CancellationToken cancellationToken) =>
        WriteStateAsync(runtime, target, AdsState.Stop, cancellationToken);

    private async Task<OperationResult<PlcState>> WriteStateAsync(PlcRuntimeContext runtime, RuntimeTargetRef target,
        AdsState desiredState, CancellationToken cancellationToken)
    {
        EnsureRuntimeActive(cancellationToken);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureRuntimeActive(cancellationToken);
            var descriptor = RequirePrepared(runtime, target);
            var active = observation ?? throw new InvalidOperationException("Runtime access must be reconnected first.");
            await active.Io.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                try
                {
                    _ = VerifyIdentity(await active.Access.ReadIdentityAsync(cancellationToken).ConfigureAwait(false),
                        descriptor.Local.NetId, descriptor.Local, cancellationToken);
                }
                catch (InvalidDataException error)
                {
                    active.Quarantined = true;
                    active.SystemObserved = false;
                    active.Problem = error.Message;
                    active.ProblemCode = "IdentityInvalid";
                    active.SystemMonitor.Invalidate(error.Message);
                    Publish(active);
                    throw;
                }
                await ObserveAsync(active, true, cancellationToken).ConfigureAwait(false);
                Publish(active);
                if (active.LastSystem != AdsState.Run || active.LastPlc is not (AdsState.Run or AdsState.Stop)
                    || active.Problem is not null)
                    return OperationResult<PlcState>.Failure(CurrentState, "The selected PLC is not available for STOP/RUN commands.");
                var writeAccepted = false;
                if (active.LastPlc != desiredState)
                {
                    Logger.LogDebug("PLC state write requested: Target={NetId}:{Port}, DesiredState={DesiredState}",
                        descriptor.Local.NetId, descriptor.PlcPort, desiredState);
                    await active.Access.WritePlcStateAsync(desiredState, cancellationToken).ConfigureAwait(false);
                    writeAccepted = true;
                    Logger.LogDebug("PLC state write accepted by ADS: Target={NetId}:{Port}, DesiredState={DesiredState}",
                        descriptor.Local.NetId, descriptor.PlcPort, desiredState);
                }
                try
                {
                    await ObserveAsync(active, true, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (writeAccepted && cancellationToken.IsCancellationRequested)
                {
                    return OperationResult<PlcState>.Success(CurrentState,
                        "PLC command accepted; subsequent state observation was cancelled. State reflects the latest published observation.");
                }
                Publish(active);
                return OperationResult<PlcState>.Success(CurrentState, "PLC command accepted; state reflects the latest observation.");
            }
            finally { active.Io.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsAccessFailure(error))
        {
            if (error is AdsAccessException ads) RequestSystemStateCheck(runtime, ads.ErrorCode);
            return Fail<PlcState>("CommandFailed", error);
        }
        finally { operations.Release(); }
    }

    public async Task<OperationResult<PlcRuntimeReleaseOutcome>> ReleaseRuntimeAsync(
        PlcRuntimeReleaseRequest request, CancellationToken cancellationToken)
    {
        EnsureRuntimeActive(cancellationToken);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureRuntimeActive(cancellationToken);
            var descriptor = RequirePrepared(request.Runtime, request.RuntimeTarget);
            if (!Enum.IsDefined(request.ReleaseMode)) throw new ArgumentException("Invalid release mode.");
            if (descriptor.Strategy == RuntimeProvisioningStrategy.CreateDedicated && request.ReleaseMode == RuntimeResourceReleaseMode.DestroyResource)
                return await Unsupported<PlcRuntimeReleaseOutcome>("dedicated Runtime destruction", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await DetachAsync().ConfigureAwait(false);
            prepared = null;
            SetHealth(AccessHealth.NotEstablished(Context.TimeProvider.GetUtcNow()));
            SetState(ServiceState.NotPrepared, "Runtime access released; existing Runtime preserved.");
            return OperationResult<PlcRuntimeReleaseOutcome>.Success(PlcRuntimeReleaseOutcome.ResourceKept);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsAccessFailure(error)) { return Fail<PlcRuntimeReleaseOutcome>("ReleaseFailed", error, DiagnosticPhase.Cleanup); }
        finally { operations.Release(); }
    }

    public override async Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
    {
        EnsureRuntimeActive(cancellationToken);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureRuntimeActive(cancellationToken);
            await DetachAsync().ConfigureAwait(false);
            prepared = null;
            SetHealth(AccessHealth.NotEstablished(Context.TimeProvider.GetUtcNow()));
            SetState(ServiceState.NotPrepared, "Runtime technical access cleaned up.");
            return OperationResult.Success();
        }
        catch (Exception error) when (IsAccessFailure(error))
        { return OperationResult.Failure(Fail<PlcRuntimeReleaseOutcome>("CleanupFailed", error, DiagnosticPhase.Cleanup).Message!); }
        finally { operations.Release(); }
    }

    internal override async Task CloseAsync()
    {
        Interlocked.Exchange(ref closing, 1);
        await operations.WaitAsync().ConfigureAwait(false);
        try { await DetachAsync().ConfigureAwait(false); prepared = null; }
        finally { operations.Release(); }
    }

    internal async Task<RuntimeDataEndpoint> GetDataEndpointAsync(PlcRuntimeContext runtime, RuntimeTargetRef target,
        CancellationToken cancellationToken)
    {
        EnsureRuntimeActive(cancellationToken);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureRuntimeActive(cancellationToken);
            var descriptor = RequirePrepared(runtime, target);
            var active = observation ?? throw new InvalidOperationException("Runtime access must be reconnected first.");
            if (!active.HasVerifiedDataAccess || active.LastPlc != AdsState.Run
                || active.PlcObservationSuspended || !CurrentState.ReadyForExecution || descriptor.PlcPort is not { } port)
                throw new InvalidOperationException("The selected PLC application is not running or its ADS port is unresolved.");
            return new(descriptor.Local.NetId, port,
                () => ReferenceEquals(Volatile.Read(ref observation), active)
                    && ReferenceEquals(active.Descriptor, descriptor) && Volatile.Read(ref closing) == 0,
                () => ReferenceEquals(Volatile.Read(ref observation), active)
                    && ReferenceEquals(active.Descriptor, descriptor) && Volatile.Read(ref closing) == 0
                    && active.HasVerifiedDataAccess && active.LastPlc == AdsState.Run
                    && !active.PlcObservationSuspended && CurrentState.ReadyForExecution,
                error => RequestSystemStateCheck(runtime, error))
            { BindingGeneration = () => active.DataBindingGeneration };
        }
        finally { operations.Release(); }
    }

    internal void RequestSystemStateCheck(PlcRuntimeContext runtime, AdsErrorCode errorCode)
    {
        var active = observation;
        if (active is null || active.Descriptor.Result.Runtime.Id != runtime.Id
            || !AdsAccessException.RequiresSystemCheck(errorCode)) return;
        active.RequestEarlyCheck(Context.TimeProvider.GetUtcNow(), monitoringInterval);
    }

    private void Attach(RuntimeObservationContext active)
    {
        lock (publicationGate)
        {
            observation = active;
            Publish(active);
        }
        active.Work = Task.Run(() => MonitorAsync(active));
    }

    private async Task MonitorAsync(RuntimeObservationContext active)
    {
        var token = active.Lifetime.Token;
        try
        {
            while (true)
            {
                await active.Wake.WaitAsync(monitoringInterval, token).ConfigureAwait(false);
                var readPlc = Interlocked.Exchange(ref active.PlcReadRequested, 0) != 0;
                await active.Io.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await ObserveAsync(active, readPlc, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested && ReferenceEquals(observation, active)) Publish(active);
                }
                finally { active.Io.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && ReferenceEquals(observation, active))
            {
                active.SystemObserved = false;
                active.Problem = error.Message;
                active.ProblemCode = "ObserverFailed";
                active.Quarantined = true;
                active.SystemMonitor.Invalidate(error.Message);
                Publish(active);
            }
        }
    }

    private async Task ObserveAsync(RuntimeObservationContext active, bool readPlc, CancellationToken cancellationToken)
    {
        if (active.Quarantined) return;
        var generation = active.InvalidationGeneration;
        active.PlcPortMissing = false;
        var systemReadSucceeded = false;
        try
        {
            var baseline = active.SystemMonitor.Latest;
            var systemRead = await active.Access.ReadSystemObservationAsync(cancellationToken).ConfigureAwait(false);
            var system = systemRead.State;
            cancellationToken.ThrowIfCancellationRequested();
            var changed = system != active.LastSystem || !active.SystemObserved;
            if (!active.IdentityVerified && (system == AdsState.Run || !active.SystemObserved))
            {
                _ = VerifyIdentity(await active.Access.ReadIdentityAsync(cancellationToken).ConfigureAwait(false),
                    active.Descriptor.Local.NetId, active.Descriptor.Local, cancellationToken);
                active.IdentityVerified = true;
            }
            if (!active.SystemMonitor.TryPublish(baseline.Sequence, systemRead))
                throw new IOException("System observation was invalidated while being read.");
            systemReadSucceeded = true;
            active.LastSystem = system;
            active.SystemObserved = true;
            if (system != AdsState.Run)
            {
                active.RecordDataAvailability(false);
                active.IdentityVerified = false;
                active.LastPlc = null;
                await active.Access.ReleasePlcAccessAsync().ConfigureAwait(false);
                active.Problem = null;
                return;
            }
            if (!active.PlcObservationSuspended && active.Descriptor.PlcPort is not null
                && (readPlc || changed || !active.HasVerifiedDataAccess || active.LastPlc is null || active.Problem is not null))
            {
                try
                {
                    await active.Access.SubscribePlcStateAsync(cancellationToken).ConfigureAwait(false);
                    active.LastPlc = await active.Access.ReadPlcStateAsync(cancellationToken).ConfigureAwait(false);
                    active.ConfirmPlcObservation(generation);
                    active.RecordDataAvailability(active.HasVerifiedDataAccess && active.LastPlc == AdsState.Run);
                }
                catch (AdsAccessException error) when (error.ErrorCode == AdsErrorCode.TargetPortNotFound)
                {
                    active.RecordDataAvailability(false);
                    active.PlcPortMissing = true;
                    active.LastPlc = null;
                    await active.Access.ReleasePlcAccessAsync().ConfigureAwait(false);
                    active.Problem = null;
                    return;
                }
            }
            active.Problem = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsAccessFailure(error))
        {
            active.SystemObserved = systemReadSucceeded;
            active.RecordDataAvailability(false);
            if (!systemReadSucceeded || error is InvalidDataException)
                active.SystemMonitor.Invalidate(error.Message);
            if (!systemReadSucceeded) active.IdentityVerified = false;
            active.LastPlc = null;
            active.Problem = error.Message;
            active.ProblemCode = "ObservationFailed";
            if (error is InvalidDataException)
            {
                active.Quarantined = true;
                active.SystemObserved = false;
                active.ProblemCode = "IdentityInvalid";
            }
        }
    }

    private LocalUsermodeRuntime VerifyIdentity(OnlineRuntimeIdentity identity, string netId,
        LocalUsermodeRuntime? expected, CancellationToken cancellationToken)
    {
        string observedNetId;
        try { observedNetId = RuntimeAccessConfiguration.NormalizeNetId(identity.NetId); }
        catch (ArgumentException error) { throw new InvalidDataException("The responding Runtime reports an invalid AMS Net ID.", error); }
        if (observedNetId != netId)
            throw new InvalidDataException("The responding Runtime reports a different AMS Net ID.");
        var local = catalog.Verify(identity, cancellationToken);
        if (expected is not null && !LocalUsermodeRuntimeCatalog.SameDirectory(local.ConfigurationDirectory, expected.ConfigurationDirectory))
            throw new InvalidDataException("The responding Runtime uses a different local configuration directory.");
        return local;
    }

    private void Publish(RuntimeObservationContext active)
    {
        lock (publicationGate)
        {
            if (!ReferenceEquals(observation, active) || Volatile.Read(ref closing) != 0) return;
            PublishCurrentObservation(active);
        }
    }

    private void PublishCurrentObservation(RuntimeObservationContext active)
    {
        var target = active.Descriptor.Result.RuntimeTarget;
        var systemConfirmed = active.SystemObserved && active.SystemMonitor.Latest.Value is not null;
        var healthy = systemConfirmed && active.Problem is null;
        var plcState = healthy && active.LastSystem == AdsState.Run ? active.LastPlc switch
        {
            AdsState.Run => PlcOperatingState.Running,
            AdsState.Stop => PlcOperatingState.Stopped,
            AdsState.Start => PlcOperatingState.Starting,
            AdsState.Stopping => PlcOperatingState.Stopping,
            null => PlcOperatingState.Unavailable,
            _ => PlcOperatingState.Unknown
        } : PlcOperatingState.Unavailable;
        ApplyState(new(systemConfirmed ? RuntimeInstanceState.Available : RuntimeInstanceState.Unavailable,
            plcState, [target], healthy && active.LastSystem is AdsState.Run or AdsState.Config,
            healthy && plcState == PlcOperatingState.Running));
        if (healthy)
        {
            SetHealth(AccessHealth.Healthy(Context.TimeProvider.GetUtcNow()));
            SetState(ServiceState.Ready, "Runtime state observed.");
            if (active.LastSystem == AdsState.Config && !active.ConfigReported)
                ReportDiagnostic("Beckhoff.Runtime.SystemConfig",
                    "PLC access is unavailable because the TwinCAT system is in CONFIG mode.",
                    DiagnosticCategory.Communication, DiagnosticPhase.ExecutionMonitoring, LogLevel.Information);
            else if (active.LastSystem == AdsState.Run && active.ConfigReported)
                ReportDiagnostic("Beckhoff.Runtime.SystemRun", "The selected TwinCAT system returned to RUN mode.",
                    DiagnosticCategory.Communication, DiagnosticPhase.ExecutionMonitoring, LogLevel.Information);
            active.ConfigReported = active.LastSystem == AdsState.Config;
            if (active.PublishedProblem is not null)
                ReportDiagnostic("Beckhoff.Runtime.Recovered", "Runtime access recovered.", DiagnosticCategory.Communication,
                    DiagnosticPhase.ExecutionMonitoring, LogLevel.Information);
            active.PublishedProblem = null;
        }
        else
        {
            SetHealth(new(active.Quarantined ? AccessHealthState.Invalidated : AccessHealthState.RevalidationRequired,
                active.Quarantined ? AccessHealthProblem.ContextLost : AccessHealthProblem.CommunicationLost,
                "Beckhoff.Runtime." + active.ProblemCode, active.Problem ?? "Runtime access unavailable.", Context.TimeProvider.GetUtcNow()));
            SetState(active.Quarantined ? ServiceState.Invalidated : ServiceState.NeedsRevalidation, Health.Message);
            if (active.PublishedProblem != active.Problem)
                ReportDiagnostic("Beckhoff.Runtime." + active.ProblemCode, Health.Message, DiagnosticCategory.Communication,
                    DiagnosticPhase.ExecutionMonitoring, LogLevel.Warning);
            active.PublishedProblem = active.Problem;
        }
    }

    private void ApplyState(PlcState state)
    {
        if (CurrentState.InstanceState == state.InstanceState && CurrentState.OperatingState == state.OperatingState
            && CurrentState.ReadyForExecution == state.ReadyForExecution && CurrentState.ReadyForTransfer == state.ReadyForTransfer
            && CurrentState.RuntimeTargets.SequenceEqual(state.RuntimeTargets)) return;
        CurrentState = state;
        Logger.LogInformation("Runtime observation changed: Target={NetId}:{Port}, Instance={Instance}, Plc={Plc}, ReadyForTransfer={Transfer}, ReadyForExecution={Execution}",
            prepared?.Local.NetId, prepared?.PlcPort, state.InstanceState, state.OperatingState, state.ReadyForTransfer, state.ReadyForExecution);
        PlcStateChangedEventHandler?.Invoke(this, new(state));
    }

    private async Task DetachAsync()
    {
        var old = observation;
        try
        {
            if (old is not null) await DisposeObservationAsync(old, old.Access).ConfigureAwait(false);
        }
        finally { PublishDetached(); }
    }

    private async Task DisposeObservationAsync(RuntimeObservationContext? old, IAdsRuntimeAccess access)
    {
        lock (publicationGate)
        {
            if (ReferenceEquals(observation, old)) observation = null;
        }
        try
        {
            if (old is null) return;
            old.Stop();
            access.PlcStateChanged -= old.OnPlcStateChanged;
            access.SystemStateInvalidated -= old.OnSystemStateInvalidated;
            access.PlcStateInvalidated -= old.OnPlcStateInvalidated;
            await old.Work.ConfigureAwait(false);
        }
        finally
        {
            try { await access.DisposeAsync().ConfigureAwait(false); }
            finally { old?.DisposeSynchronization(); }
        }
    }

    private void PublishDetached()
    {
        ApplyState(new(RuntimeInstanceState.Unknown, PlcOperatingState.Unavailable,
            prepared is null ? [] : [prepared.Result.RuntimeTarget], false, false));
        SetHealth(AccessHealth.NotEstablished(Context.TimeProvider.GetUtcNow()));
        SetState(ServiceState.NotPrepared, "Runtime technical access detached.");
    }

    private PreparedRuntimeTarget RequirePrepared(PlcRuntimeContext runtime, RuntimeTargetRef? target)
    {
        var descriptor = prepared ?? throw new InvalidOperationException("No Runtime has been prepared.");
        if (runtime.Id != descriptor.Result.Runtime.Id || target is not null && target.Id != descriptor.Result.RuntimeTarget.Id)
            throw new ArgumentException("The supplied Runtime context or target is not the prepared target.");
        return descriptor;
    }

    private OperationResult<T> Fail<T>(string suffix, Exception error, DiagnosticPhase phase = DiagnosticPhase.RuntimeProvisioning)
    {
        if (observation is null && prepared is not null)
        {
            SetHealth(new(AccessHealthState.Invalidated, AccessHealthProblem.CommunicationLost,
                "Beckhoff.Runtime." + suffix, error.Message, Context.TimeProvider.GetUtcNow()));
            SetState(phase == DiagnosticPhase.Cleanup ? ServiceState.CleanupRequired : ServiceState.Invalidated, error.Message);
        }
        ReportDiagnostic("Beckhoff.Runtime." + suffix, error.Message,
            phase == DiagnosticPhase.Cleanup ? DiagnosticCategory.Cleanup : DiagnosticCategory.Communication, phase);
        return OperationResult<T>.Failure(error.Message);
    }

    private void EnsureRuntimeActive(CancellationToken cancellationToken)
    {
        EnsureActive(cancellationToken);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref closing) != 0, this);
    }

    private static bool IsAccessFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or NotSupportedException or System.Xml.XmlException
        or AdsErrorException or TwinCAT.Ads.Server.LoopbackNotRegisteredException or DllNotFoundException;
}
