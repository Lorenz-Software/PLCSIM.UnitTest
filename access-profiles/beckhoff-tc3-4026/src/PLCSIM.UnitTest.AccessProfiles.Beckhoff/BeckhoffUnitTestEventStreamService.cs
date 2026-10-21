using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Logging;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff;

internal sealed class BeckhoffUnitTestEventStreamService : BeckhoffServiceBase, IUnitTestEventStreamService
{
    private sealed class Observation(UnitTestContext context, EventStreamSettings settings, RuntimeDataEndpoint endpoint)
    {
        internal UnitTestContext Context { get; } = context;
        internal EventStreamSettings Settings { get; } = settings;
        internal RuntimeDataEndpoint Endpoint { get; } = endpoint;
        internal EventStreamDrain Drain { get; } = new(context.Session.SessionId, settings.MaxEventsPerBatch);
        internal IEventStreamAccess? Access;
        internal long BindingGeneration;
        internal uint AcceptedWatermark;
        internal CancellationTokenSource? Lifetime;
        internal Task Work = Task.CompletedTask;
        internal bool Invalidated;
        internal Exception? CleanupError;
        internal long ReportedMetadataRevision;
        internal EventStreamState? ReportedBufferState;
        internal bool BufferRecoveryActive, BufferRecoveryReported;
    }

    private readonly Func<RuntimeDataEndpoint, IEventStreamAccess> openAccess;
    private readonly EventBatchLog batchLog;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly SemaphoreSlim io = new(1, 1);
    private readonly SemaphoreSlim observationDispatch = new(1, 1);
    private Observation? observation;
    private string? lastDiagnostic;
    private string? lastCleanupDiagnostic;
    private int closing;

    internal BeckhoffUnitTestEventStreamService(AccessProfileContext context)
        : this(context, endpoint => new AdsEventStreamAccess(new AdsClientFactory(context.LoggerFactory), endpoint.NetId, endpoint.Port, context.TimeProvider)) { }

    internal BeckhoffUnitTestEventStreamService(AccessProfileContext context, Func<RuntimeDataEndpoint, IEventStreamAccess> openAccess)
        : base(context)
    {
        this.openAccess = openAccess;
        batchLog = new(Logger, context.TimeProvider, context.Logging.RawEventLoggingEnabled);
    }

    public event EventHandler<EventBatchReceivedEventArgs>? EventBatchReceivedEventHandler;
    public event EventHandler<EventStreamStateChangedEventArgs>? MonitoringStateChangedEventHandler;
    public EventStreamState CurrentMonitoringState { get; private set; } = new(
        default, false, EventBufferReadState.NotObserved, EventStreamBatchReleaseState.Idle, false, 0);

    internal async Task<OperationResult> PrepareAsync(UnitTestContext context, EventStreamSettings settings,
        RuntimeDataEndpoint endpoint, CancellationToken cancellationToken)
    {
        EnsureStreamActive(cancellationToken);
        settings.Validate();
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStreamActive(cancellationToken);
            await DetachAsync().ConfigureAwait(false);
            var candidate = new Observation(context, settings, endpoint);
            PublishState(candidate, false);
            try
            {
                await EnsureAccessAsync(candidate, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                observation = candidate;
                Logger.LogInformation("EventStream prepared: Target={NetId}:{Port}, CommunicationSession={Session}, MaxEventsPerBatch={MaxEvents}",
                    endpoint.NetId, endpoint.Port, context.Session.SessionId.Value, settings.MaxEventsPerBatch);
                SetHealthy();
                return OperationResult.Success("EventStream contracts and ADS layout validated.");
            }
            catch
            {
                observation = null;
                await ReleaseFailedAccessAsync(candidate).ConfigureAwait(false);
                ReportCleanupFailure(candidate);
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsAccessFailure(error))
        {
            if (error is AdsAccessException ads && AdsAccessException.RequiresSystemCheck(ads.ErrorCode)) endpoint.RequestSystemCheck(ads.ErrorCode);
            ReportAccessFailure(error, error is InvalidDataException, false);
            return OperationResult.Failure(error.Message);
        }
        finally { operations.Release(); }
    }

    public async Task<OperationResult<EventStreamState>> StartMonitoringAsync(UnitTestContext context, CancellationToken cancellationToken)
    {
        EnsureStreamActive(cancellationToken);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStreamActive(cancellationToken);
            var active = observation;
            if (active is null || active.Context != context || active.Invalidated || !active.Endpoint.IsCurrent())
                return OperationResult<EventStreamState>.Failure(CurrentMonitoringState, "EventStream communication is not prepared or requires revalidation.");
            if (!active.Work.IsCompleted) return OperationResult<EventStreamState>.Success(CurrentMonitoringState);
            active.Lifetime?.Dispose();
            active.Lifetime = new();
            PublishState(active, true);
            active.Work = Task.Run(() => ObserveAsync(active, active.Lifetime.Token));
            return OperationResult<EventStreamState>.Success(CurrentMonitoringState, "EventStream monitoring started.");
        }
        finally { operations.Release(); }
    }

    private async Task ObserveAsync(Observation active, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await observationDispatch.WaitAsync(token).ConfigureAwait(false);
                TimeSpan delay;
                IEventStreamNotificationAccess? notificationAccess = null;
                try
                {
                    UnitTestEventBatch? batch = null;
                    Exception? failure = null;
                    bool preparing = true, contractInvalid = false, inconsistent = false, writeFailed = false;
                    await io.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await EnsureAccessAsync(active, token).ConfigureAwait(false);
                        preparing = false;
                        batch = await active.Drain.CaptureAsync(active.Access!, () => RequireBinding(active), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (InvalidDataException error) when (!preparing && !active.Drain.WritingWatermark)
                    {
                        active.Drain.RejectCapture();
                        (active.Access as IEventStreamNotificationAccess)?.DiscardCachedObservation();
                        inconsistent = true;
                        failure = error;
                    }
                    catch (Exception error) when (IsAccessFailure(error))
                    {
                        failure = error;
                        contractInvalid = preparing && error is InvalidDataException;
                        active.Invalidated = contractInvalid || !active.Endpoint.IsCurrent();
                        // A rejected watermark leaves a valid, withheld batch. Other access failures invalidate the read.
                        writeFailed = !preparing && active.Drain.WritingWatermark;
                        if (!writeFailed) active.Drain.InvalidateRead(contractInvalid ? EventBufferReadState.ContractInvalid
                            : active.Invalidated ? EventBufferReadState.SessionInvalid : EventBufferReadState.RetryRequired);
                        await ReleaseFailedAccessAsync(active).ConfigureAwait(false);
                    }
                    finally { io.Release(); }

                    // No host callback runs inside the ADS-access gate. An accepted batch is delivered even when Stop just cancelled.
                    if (failure is null) SetHealthy();
                    if (batch is not null)
                    {
                        batchLog.Record(batch, active.Endpoint.NetId, active.Endpoint.Port);
                        EventBatchReceivedEventHandler?.Invoke(this, new(batch));
                    }
                    PublishState(active, true);
                    ReportBufferStatus(active, batch is { Events.Count: > 0 });
                    ReportCleanupFailure(active);
                    if (inconsistent) ReportOnce("Beckhoff.EventStream.SnapshotRejected", failure!.Message, LogLevel.Warning);
                    else if (failure is not null)
                    {
                        if (failure is AdsAccessException ads && AdsAccessException.RequiresSystemCheck(ads.ErrorCode))
                            active.Endpoint.RequestSystemCheck(ads.ErrorCode);
                        ReportAccessFailure(failure, contractInvalid, !active.Endpoint.IsCurrent(), writeFailed);
                        if (active.Invalidated) return;
                    }
                    delay = failure is not null && !inconsistent ? active.Settings.MonitoringInterval
                        : active.Drain.NeedsDrain ? active.Settings.DrainInterval : active.Settings.MonitoringInterval;
                    if (failure is null && active.Settings.Strategy == ObservationStrategy.Subscription)
                    {
                        notificationAccess = active.Access as IEventStreamNotificationAccess;
                        delay = active.Drain.NeedsDrain ? active.Settings.DrainInterval : TimeSpan.FromMilliseconds(100);
                    }
                }
                finally { observationDispatch.Release(); }
                if (notificationAccess is not null)
                    await notificationAccess.WaitForObservationAsync(delay, token).ConfigureAwait(false);
                else
                    await Task.Delay(delay, Context.TimeProvider, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            active.Invalidated = true;
            Logger.LogError(error, "EventStream observation or batch delivery failed.");
            SetHealth(new(AccessHealthState.Invalidated, AccessHealthProblem.CommunicationLost,
                "Beckhoff.EventStream.ObserverFailed", error.Message, Context.TimeProvider.GetUtcNow()));
            SetState(ServiceState.Invalidated, error.Message);
            ReportOnce("Beckhoff.EventStream.ObserverFailed", error.Message, LogLevel.Error);
        }
        finally { PublishState(active, false); }
    }

    private async Task EnsureAccessAsync(Observation active, CancellationToken token)
    {
        RequireEndpoint(active);
        var generation = active.Endpoint.BindingGeneration();
        if (active.Access is not null && active.BindingGeneration != generation)
            await ReleaseAccessAsync(active).ConfigureAwait(false);
        if (active.BindingGeneration != generation) active.AcceptedWatermark = 0;
        if (active.Access is null)
        {
            active.BindingGeneration = generation;
            var candidate = openAccess(active.Endpoint);
            try
            {
                await candidate.PrepareAsync(active.Settings, token).ConfigureAwait(false);
                if (candidate is IEventStreamNotificationAccess notifications)
                    notifications.RestoreAcceptedWatermark(active.AcceptedWatermark);
                if (active.Settings.Strategy == ObservationStrategy.Subscription && candidate is not IEventStreamNotificationAccess)
                    throw new InvalidDataException("The EventStream access does not support subscription observation.");
                token.ThrowIfCancellationRequested();
                RequireBinding(active);
                active.Access = candidate;
            }
            catch
            {
                try { await candidate.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { active.CleanupError = cleanup; }
                throw;
            }
        }
        token.ThrowIfCancellationRequested();
        RequireBinding(active);
    }

    private static void RequireBinding(Observation active)
    {
        RequireEndpoint(active);
        if (active.BindingGeneration != active.Endpoint.BindingGeneration())
            throw new IOException("Runtime availability changed during EventStream access.");
    }

    private static void RequireEndpoint(Observation active)
    {
        if (!active.Endpoint.IsCurrent()) throw new InvalidOperationException("The prepared Runtime access context has been replaced or released.");
        if (!active.Endpoint.IsAvailable()) throw new IOException("The selected PLC application is currently unavailable for EventStream access.");
    }

    private void PublishState(Observation active, bool monitoring)
    {
        var value = active.Drain.State with { IsMonitoring = monitoring };
        if (CurrentMonitoringState == value) return;
        var previous = CurrentMonitoringState;
        if (previous.IsMonitoring != value.IsMonitoring)
            Logger.LogInformation("EventStream monitoring={Monitoring}: Target={NetId}:{Port}, CommunicationSession={Session}",
                monitoring, active.Endpoint.NetId, active.Endpoint.Port, active.Context.Session.SessionId.Value);
        if (previous.LastReadState != value.LastReadState && value.LastReadState is not (EventBufferReadState.Valid or EventBufferReadState.Empty))
            Logger.LogDebug("EventStream read state={ReadState}: Target={NetId}:{Port}, Producer={Producer}, Deleted={Deleted}, Lost={Lost}",
                value.LastReadState, active.Endpoint.NetId, active.Endpoint.Port, value.ProducerSequenceId, value.DeletedSequenceId, value.LostEventCount);
        CurrentMonitoringState = value;
        MonitoringStateChangedEventHandler?.Invoke(this, new(previous, value));
    }

    private void SetHealthy()
    {
        var recovered = Health.State == AccessHealthState.RevalidationRequired;
        lastDiagnostic = null;
        SetHealth(AccessHealth.Healthy(Context.TimeProvider.GetUtcNow()));
        SetState(ServiceState.Ready, "EventStream access available.");
        if (recovered) ReportDiagnostic("Beckhoff.EventStream.AccessRecovered", "TwinCAT UnitTest EventStream access recovered.",
            DiagnosticCategory.Communication, DiagnosticPhase.ExecutionMonitoring, LogLevel.Information);
    }

    private void ReportAccessFailure(Exception error, bool contractInvalid, bool contextLost, bool writeFailed = false)
    {
        var code = "Beckhoff.EventStream." + (contractInvalid ? "ContractInvalid" : contextLost ? "ContextLost"
            : writeFailed ? "WatermarkWriteFailed" : "AccessFailed");
        SetHealth(new(contractInvalid || contextLost ? AccessHealthState.Invalidated : AccessHealthState.RevalidationRequired,
            contractInvalid ? AccessHealthProblem.ContractInvalid : contextLost ? AccessHealthProblem.ContextLost : AccessHealthProblem.CommunicationLost,
            code, error.Message, Context.TimeProvider.GetUtcNow()));
        SetState(contractInvalid || contextLost ? ServiceState.Invalidated : ServiceState.NeedsRevalidation, error.Message);
        ReportOnce(code, error.Message, contractInvalid || contextLost || writeFailed ? LogLevel.Error : LogLevel.Warning);
    }

    private void ReportOnce(string code, string message, LogLevel level)
    {
        var key = code + ":" + message;
        if (lastDiagnostic == key) return;
        lastDiagnostic = key;
        if (observation is { } active)
            Logger.LogDebug("EventStream diagnostic context: Code={Code}, Target={NetId}:{Port}, CommunicationSession={Session}, Producer={Producer}, Deleted={Deleted}, BatchRelease={Release}",
                code, active.Endpoint.NetId, active.Endpoint.Port, active.Context.Session.SessionId.Value,
                active.Drain.State.ProducerSequenceId, active.Drain.State.DeletedSequenceId, active.Drain.State.BatchReleaseState);
        ReportDiagnostic(code, message, DiagnosticCategory.Communication, DiagnosticPhase.ExecutionMonitoring, level);
    }

    private void ReportBufferStatus(Observation active, bool deliveredBatch)
    {
        if (active.ReportedMetadataRevision == active.Drain.MetadataRevision) return;
        active.ReportedMetadataRevision = active.Drain.MetadataRevision;
        var current = active.Drain.State;
        var previous = active.ReportedBufferState;
        active.ReportedBufferState = current;
        if (current.BufferFull && previous?.BufferFull != true)
            ReportDiagnostic("Beckhoff.EventStream.BufferFull", $"TwinCAT UnitTest EventBuffer became full at producer sequence {current.ProducerSequenceId}.",
                DiagnosticCategory.Communication, DiagnosticPhase.ExecutionMonitoring, LogLevel.Warning);
        if (current.LostEventCount > (previous?.LostEventCount ?? 0))
            ReportDiagnostic("Beckhoff.EventStream.EventsLost", $"TwinCAT UnitTest EventBuffer lost-event count increased from {previous?.LostEventCount ?? 0} to {current.LostEventCount}.",
                DiagnosticCategory.Communication, DiagnosticPhase.ExecutionMonitoring);
        if (current.BufferFull || current.LostEventCount > 0) active.BufferRecoveryActive = true;
        if (active.BufferRecoveryActive && !active.BufferRecoveryReported && deliveredBatch)
        {
            active.BufferRecoveryReported = true;
            ReportDiagnostic("Beckhoff.EventStream.RecoveryStarted", $"TwinCAT UnitTest EventBuffer draining continued at producer sequence {current.ProducerSequenceId}.",
                DiagnosticCategory.Communication, DiagnosticPhase.ExecutionMonitoring, LogLevel.Warning);
        }
        if (active.BufferRecoveryActive && !current.BufferFull && current.LostEventCount == 0
            && current.LastReadState is EventBufferReadState.Valid or EventBufferReadState.Empty)
        {
            active.BufferRecoveryActive = active.BufferRecoveryReported = false;
            ReportDiagnostic("Beckhoff.EventStream.Recovered", "TwinCAT UnitTest EventBuffer recovered and monitoring is active.",
                DiagnosticCategory.Communication, DiagnosticPhase.ExecutionMonitoring, LogLevel.Information);
        }
    }

    public async Task<OperationResult<EventBufferResetCommandResult>> ResetEventBufferAsync(UnitTestContext context, CancellationToken cancellationToken)
    {
        EnsureStreamActive(cancellationToken);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStreamActive(cancellationToken);
            var active = observation;
            if (active is null || active.Context != context || active.Invalidated)
                return OperationResult<EventBufferResetCommandResult>.Failure(new(false), "EventStream communication is not prepared or has been replaced.");
            // Keep result/health publication ordered with observation, without holding the ADS gate during host callbacks.
            await observationDispatch.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Exception? failure = null;
                await io.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    RequireBinding(active);
                    if (active.Access is null || Health.State != AccessHealthState.Healthy)
                        return OperationResult<EventBufferResetCommandResult>.Failure(new(false), "EventStream access requires revalidation.");
                    // Reset is serialized with capture/commit, but does not acknowledge PLC execution or alter observed metadata.
                    Logger.LogDebug("EventBuffer reset requested: Target={NetId}:{Port}, CommunicationSession={Session}",
                        active.Endpoint.NetId, active.Endpoint.Port, context.Session.SessionId.Value);
                    await active.Access.WriteResetAsync(cancellationToken).ConfigureAwait(false);
                    Logger.LogDebug("EventBuffer reset write accepted by ADS: Target={NetId}:{Port}, CommunicationSession={Session}",
                        active.Endpoint.NetId, active.Endpoint.Port, context.Session.SessionId.Value);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (IsAccessFailure(error))
                {
                    failure = error;
                    active.Invalidated = !active.Endpoint.IsCurrent();
                    await ReleaseFailedAccessAsync(active).ConfigureAwait(false);
                }
                finally { io.Release(); }
                if (failure is null) return OperationResult<EventBufferResetCommandResult>.Success(new(true), "EventBuffer reset command write accepted by ADS.");
                if (failure is AdsAccessException ads && AdsAccessException.RequiresSystemCheck(ads.ErrorCode)) active.Endpoint.RequestSystemCheck(ads.ErrorCode);
                ReportAccessFailure(failure, false, active.Invalidated);
                ReportOnce("Beckhoff.EventStream.ResetWriteFailed", failure.Message, LogLevel.Error);
                ReportCleanupFailure(active);
                return OperationResult<EventBufferResetCommandResult>.Failure(new(false), failure.Message);
            }
            finally { observationDispatch.Release(); }
        }
        finally { operations.Release(); }
    }

    public async Task<OperationResult> StopMonitoringAsync(CancellationToken cancellationToken)
    {
        EnsureStreamActive(cancellationToken);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStreamActive(cancellationToken);
            if (observation is { } active) await StopAsync(active).ConfigureAwait(false);
            return OperationResult.Success("EventStream monitoring stopped.");
        }
        finally { operations.Release(); }
    }

    public Task<OperationResult> ReleaseCommunicationAsync(CancellationToken cancellationToken) => CleanupAsync(cancellationToken);

    public override async Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
    {
        EnsureStreamActive(cancellationToken);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStreamActive(cancellationToken);
            await DetachAsync().ConfigureAwait(false);
            return OperationResult.Success("EventStream technical access released.");
        }
        catch (Exception error) when (IsAccessFailure(error))
        {
            SetState(ServiceState.CleanupRequired, error.Message);
            ReportDiagnostic("Beckhoff.EventStream.CleanupFailed", error.Message, DiagnosticCategory.Cleanup, DiagnosticPhase.Cleanup);
            return OperationResult.Failure(error.Message);
        }
        finally { operations.Release(); }
    }

    private async Task DetachAsync()
    {
        var old = observation;
        try
        {
            if (old is null) return;
            try { await StopAsync(old).ConfigureAwait(false); }
            finally
            {
                observation = null;
                await ReleaseAccessAsync(old).ConfigureAwait(false);
            }
        }
        finally
        {
            SetHealth(AccessHealth.NotEstablished(Context.TimeProvider.GetUtcNow()));
            SetState(ServiceState.NotPrepared, "EventStream communication detached.");
        }
    }

    private static async Task StopAsync(Observation active)
    {
        active.Lifetime?.Cancel();
        try { await active.Work.ConfigureAwait(false); }
        finally { active.Lifetime?.Dispose(); active.Lifetime = null; }
    }

    private static async Task ReleaseAccessAsync(Observation active)
    {
        var access = active.Access;
        if (access is IEventStreamNotificationAccess notifications)
            active.AcceptedWatermark = notifications.AcceptedWatermark;
        active.Access = null;
        if (access is not null) await access.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task ReleaseFailedAccessAsync(Observation active)
    {
        try { await ReleaseAccessAsync(active).ConfigureAwait(false); }
        catch (Exception error) { active.CleanupError = error; }
    }

    private void ReportCleanupFailure(Observation active)
    {
        var error = active.CleanupError;
        active.CleanupError = null;
        if (error is null || lastCleanupDiagnostic == error.Message) return;
        lastCleanupDiagnostic = error.Message;
        ReportDiagnostic("Beckhoff.EventStream.CleanupFailed", error.Message, DiagnosticCategory.Cleanup,
            DiagnosticPhase.Cleanup, LogLevel.Warning);
    }

    internal override async Task CloseAsync()
    {
        Interlocked.Exchange(ref closing, 1);
        await operations.WaitAsync().ConfigureAwait(false);
        try { await DetachAsync().ConfigureAwait(false); }
        finally { operations.Release(); }
    }

    private void EnsureStreamActive(CancellationToken token)
    {
        EnsureActive(token);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref closing) != 0, this);
    }

    private static bool IsAccessFailure(Exception error) => error is IOException or InvalidDataException or InvalidOperationException
        or ArgumentException or AdsErrorException or TwinCAT.Ads.Server.LoopbackNotRegisteredException
        or UnauthorizedAccessException or DllNotFoundException;
}
