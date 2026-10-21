using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff;

internal sealed class BeckhoffUnitTestLifecycleService : BeckhoffServiceBase, IUnitTestLifecycleService
{
    private sealed class Observation(UnitTestContext context, RunControlSettings settings, RuntimeDataEndpoint endpoint)
    {
        internal UnitTestContext Context { get; } = context;
        internal RunControlSettings Settings { get; } = settings;
        internal RuntimeDataEndpoint Endpoint { get; } = endpoint;
        internal CancellationTokenSource Lifetime { get; } = new();
        internal IRunControlAccess? Access;
        internal long BindingGeneration;
        internal Task Work = Task.CompletedTask;
        internal bool Invalidated;
        internal bool CommandObservationPending;
    }

    private static readonly UnitTestRunControlObservation Unknown = new(UnitTestExecutionStatus.Unknown,
        UnitTestExecutionSignals.None, UnitTestLifecycleState.Unknown, UnitTestResultState.Unknown);
    private readonly Func<RuntimeDataEndpoint, IRunControlAccess> openAccess;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly SemaphoreSlim io = new(1, 1);
    private Observation? observation;
    private string? cleanupFailure;
    private int closing;

    internal BeckhoffUnitTestLifecycleService(AccessProfileContext context)
        : this(context, endpoint => new AdsRunControlAccess(new AdsClientFactory(context.LoggerFactory), endpoint.NetId, endpoint.Port, context.TimeProvider)) { }

    internal BeckhoffUnitTestLifecycleService(AccessProfileContext context, Func<RuntimeDataEndpoint, IRunControlAccess> openAccess)
        : base(context) => this.openAccess = openAccess;

    public event EventHandler<UnitTestRunControlObservationChangedEventArgs>? RunControlObservationChanged;
    public UnitTestRunControlObservation CurrentObservation { get; private set; } = Unknown;

    internal async Task<OperationResult> PrepareAsync(UnitTestContext context, RunControlSettings settings,
        RuntimeDataEndpoint endpoint, CancellationToken cancellationToken)
    {
        EnsureLifecycleActive(cancellationToken);
        if (!Enum.IsDefined(settings.Strategy) || string.IsNullOrWhiteSpace(settings.Root) || settings.Interval <= TimeSpan.Zero
            || settings.Interval.TotalMilliseconds > int.MaxValue)
            throw new ArgumentException("RunControl symbol and positive monitoring interval are required.");
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLifecycleActive(cancellationToken);
            await DetachAsync().ConfigureAwait(false);
            var candidate = new Observation(context, settings, endpoint);
            try
            {
                var first = await ReadAsync(candidate, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                observation = candidate;
                Logger.LogInformation("RunControl prepared: Target={NetId}:{Port}, CommunicationSession={Session}",
                    endpoint.NetId, endpoint.Port, context.Session.SessionId.Value);
                SetHealthy();
                Publish(first);
                candidate.Work = Task.Run(() => ObserveAsync(candidate));
                return OperationResult.Success("RunControl direct-access contract validated and observation started.");
            }
            catch (Exception error)
            {
                observation = null;
                CurrentObservation = Unknown;
                if (error is AdsAccessException ads && AdsAccessException.RequiresSystemCheck(ads.ErrorCode))
                    endpoint.RequestSystemCheck(ads.ErrorCode);
                try { await ReleaseFailedAccessAsync(candidate).ConfigureAwait(false); }
                finally { candidate.Lifetime.Dispose(); }
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsAccessFailure(error))
        {
            ReportFailure(error, null);
            return OperationResult.Failure(error.Message);
        }
        finally { operations.Release(); }
    }

    public Task<OperationResult<TestRunCommandResult>> StartAsync(UnitTestContext context, CancellationToken cancellationToken) =>
        CommandAsync(context, UnitTestRunCommand.Start, cancellationToken);
    public Task<OperationResult<TestRunCommandResult>> AbortAsync(UnitTestContext context, CancellationToken cancellationToken) =>
        CommandAsync(context, UnitTestRunCommand.Abort, cancellationToken);
    public Task<OperationResult<TestRunCommandResult>> ResetAsync(UnitTestContext context, CancellationToken cancellationToken) =>
        CommandAsync(context, UnitTestRunCommand.Reset, cancellationToken);

    private async Task<OperationResult<TestRunCommandResult>> CommandAsync(UnitTestContext context, UnitTestRunCommand command,
        CancellationToken cancellationToken)
    {
        EnsureLifecycleActive(cancellationToken);
        await io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLifecycleActive(cancellationToken);
            var active = observation;
            if (active is null || active.Context != context)
                return OperationResult<TestRunCommandResult>.Failure(new(command, false), "The UnitTest communication context is not prepared or has been replaced.");
            try
            {
                RequireEndpoint(active);
                if (active.Access is not null && active.BindingGeneration != active.Endpoint.BindingGeneration())
                    throw new IOException("RunControl binding requires revalidation after a Runtime availability change.");
                if (active.Access is null || Health.State != AccessHealthState.Healthy)
                    return OperationResult<TestRunCommandResult>.Failure(new(command, false), "RunControl access requires revalidation.");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, active.Lifetime.Token);
                Logger.LogDebug("RunControl command requested: Command={Command}, Target={NetId}:{Port}, CommunicationSession={Session}",
                    command, active.Endpoint.NetId, active.Endpoint.Port, context.Session.SessionId.Value);
                await active.Access.WriteAsync(command, linked.Token).ConfigureAwait(false);
                if (active.Settings.Strategy == ObservationStrategy.Subscription)
                {
                    active.CommandObservationPending = true;
                    ((IRunControlNotificationAccess)active.Access).RequestObservation();
                }
                Logger.LogDebug("RunControl command write accepted by ADS: Command={Command}, Target={NetId}:{Port}, CommunicationSession={Session}",
                    command, active.Endpoint.NetId, active.Endpoint.Port, context.Session.SessionId.Value);
                return OperationResult<TestRunCommandResult>.Success(new(command, true), "RunControl command write accepted by ADS.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (IsAccessFailure(error))
            {
                ReportFailure(error, active);
                await ReleaseFailedAccessAsync(active).ConfigureAwait(false);
                return OperationResult<TestRunCommandResult>.Failure(new(command, false), error.Message);
            }
        }
        finally { io.Release(); }
    }

    private async Task ObserveAsync(Observation active)
    {
        var token = active.Lifetime.Token;
        try
        {
            while (true)
            {
                if (!active.Invalidated && active.Settings.Strategy == ObservationStrategy.Subscription
                    && active.Access is IRunControlNotificationAccess notifications)
                    await notifications.WaitForObservationAsync(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
                else
                    await Task.Delay(active.Settings.Interval, Context.TimeProvider, token).ConfigureAwait(false);
                await io.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(observation, active)) return;
                    if (active.Invalidated) continue;
                    try
                    {
                        var value = await ReadAsync(active, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (!ReferenceEquals(observation, active)) return;
                        SetHealthy();
                        Publish(value, active.CommandObservationPending);
                        active.CommandObservationPending = false;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error) when (IsAccessFailure(error))
                    {
                        if (token.IsCancellationRequested || !ReferenceEquals(observation, active)) return;
                        ReportFailure(error, active);
                        await ReleaseFailedAccessAsync(active).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        if (token.IsCancellationRequested || !ReferenceEquals(observation, active)) return;
                        active.Invalidated = true;
                        SetHealth(new(AccessHealthState.Invalidated, AccessHealthProblem.CommunicationLost,
                            "Beckhoff.RunControl.ObserverFailed", error.Message, Context.TimeProvider.GetUtcNow()));
                        SetState(ServiceState.Invalidated, error.Message);
                        Logger.LogError(error, "RunControl observation failed unexpectedly.");
                        ReportDiagnostic("Beckhoff.RunControl.ObserverFailed", error.Message,
                            DiagnosticCategory.Communication, DiagnosticPhase.ExecutionMonitoring);
                        return;
                    }
                }
                finally { io.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<UnitTestRunControlObservation> ReadAsync(Observation active, CancellationToken token)
    {
        RequireEndpoint(active);
        if (active.Access is not null && active.BindingGeneration != active.Endpoint.BindingGeneration())
            await ReleaseAccessAsync(active).ConfigureAwait(false);
        if (active.Access is null)
        {
            active.BindingGeneration = active.Endpoint.BindingGeneration();
            active.Access = openAccess(active.Endpoint);
            await active.Access.PrepareAsync(active.Settings.Root, token).ConfigureAwait(false);
            if (active.Settings.Strategy == ObservationStrategy.Subscription)
            {
                if (active.Access is not IRunControlNotificationAccess notifications)
                    throw new InvalidDataException("The RunControl access does not support subscription observation.");
                await notifications.RegisterNotificationsAsync(active.Settings.Interval, token).ConfigureAwait(false);
            }
        }
        var value = await active.Access.ReadAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        RequireEndpoint(active);
        if (active.BindingGeneration != active.Endpoint.BindingGeneration())
            throw new IOException("Runtime availability changed during RunControl observation.");
        return value;
    }

    private static void RequireEndpoint(Observation active)
    {
        if (!active.Endpoint.IsCurrent()) throw new InvalidOperationException("The prepared Runtime access context has been replaced or released.");
        if (!active.Endpoint.IsAvailable()) throw new IOException("The selected PLC application is currently unavailable for RunControl access.");
    }

    private void Publish(UnitTestRunControlObservation value, bool confirmCommand = false)
    {
        if (CurrentObservation == value && !confirmCommand) return;
        Logger.LogDebug("RunControl observation changed: Target={NetId}:{Port}, Observation={Observation}",
            observation?.Endpoint.NetId, observation?.Endpoint.Port, value);
        CurrentObservation = value;
        RunControlObservationChanged?.Invoke(this, new(value));
    }

    private void SetHealthy()
    {
        cleanupFailure = null;
        var recovering = Health.State is AccessHealthState.RevalidationRequired or AccessHealthState.Invalidated;
        SetHealth(AccessHealth.Healthy(Context.TimeProvider.GetUtcNow()));
        SetState(ServiceState.Ready, "RunControl observation available.");
        if (recovering) ReportDiagnostic("Beckhoff.RunControl.Recovered", "RunControl access recovered.",
            DiagnosticCategory.Communication, DiagnosticPhase.ExecutionMonitoring, LogLevel.Information);
    }

    private void ReportFailure(Exception error, Observation? active)
    {
        if (error is AdsAccessException ads && AdsAccessException.RequiresSystemCheck(ads.ErrorCode))
            active?.Endpoint.RequestSystemCheck(ads.ErrorCode);
        var contractInvalid = error is InvalidDataException;
        var contextLost = active is not null && !active.Endpoint.IsCurrent();
        if (active is not null) active.Invalidated = contractInvalid || contextLost;
        var code = "Beckhoff.RunControl." + (contractInvalid ? "ContractInvalid" : contextLost ? "ContextLost" : "AccessFailed");
        var changed = Health.ReasonCode != code || Health.Message != error.Message;
        SetHealth(new(contractInvalid || contextLost ? AccessHealthState.Invalidated : AccessHealthState.RevalidationRequired,
            contractInvalid ? AccessHealthProblem.ContractInvalid : contextLost ? AccessHealthProblem.ContextLost : AccessHealthProblem.CommunicationLost,
            code, error.Message, Context.TimeProvider.GetUtcNow()));
        SetState(contractInvalid || contextLost ? ServiceState.Invalidated : ServiceState.NeedsRevalidation, error.Message);
        if (changed) ReportDiagnostic(code, error.Message, DiagnosticCategory.Communication,
            DiagnosticPhase.ExecutionMonitoring, contractInvalid || contextLost ? LogLevel.Error : LogLevel.Warning);
    }

    private static async Task ReleaseAccessAsync(Observation active)
    {
        var access = active.Access;
        active.Access = null;
        if (access is not null) await access.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ReleaseFailedAccessAsync(Observation active)
    {
        try { await ReleaseAccessAsync(active).ConfigureAwait(false); }
        catch (Exception error) when (IsAccessFailure(error))
        {
            if (cleanupFailure != error.Message)
                ReportDiagnostic("Beckhoff.RunControl.CleanupFailed", error.Message, DiagnosticCategory.Cleanup,
                    DiagnosticPhase.Cleanup, LogLevel.Warning);
            cleanupFailure = error.Message;
        }
    }

    public Task<OperationResult> ReleaseCommunicationAsync(CancellationToken cancellationToken) => CleanupAsync(cancellationToken);

    public override async Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
    {
        EnsureLifecycleActive(cancellationToken);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLifecycleActive(cancellationToken);
            await DetachAsync().ConfigureAwait(false);
            return OperationResult.Success("RunControl technical access released.");
        }
        catch (Exception error) when (IsAccessFailure(error))
        {
            ReportDiagnostic("Beckhoff.RunControl.CleanupFailed", error.Message, DiagnosticCategory.Cleanup, DiagnosticPhase.Cleanup);
            SetState(ServiceState.CleanupRequired, error.Message);
            return OperationResult.Failure(error.Message);
        }
        finally { operations.Release(); }
    }

    private async Task DetachAsync()
    {
        var old = Interlocked.Exchange(ref observation, null);
        try
        {
            if (old is null) return;
            old.Lifetime.Cancel();
            try { await old.Work.ConfigureAwait(false); }
            finally
            {
                await io.WaitAsync().ConfigureAwait(false);
                try { await ReleaseAccessAsync(old).ConfigureAwait(false); }
                finally { io.Release(); old.Lifetime.Dispose(); }
            }
        }
        finally
        {
            CurrentObservation = Unknown;
            SetHealth(AccessHealth.NotEstablished(Context.TimeProvider.GetUtcNow()));
            SetState(ServiceState.NotPrepared, "RunControl communication detached.");
        }
    }

    internal override async Task CloseAsync()
    {
        Interlocked.Exchange(ref closing, 1);
        await operations.WaitAsync().ConfigureAwait(false);
        try { await DetachAsync().ConfigureAwait(false); }
        finally { operations.Release(); }
    }

    private void EnsureLifecycleActive(CancellationToken token)
    {
        EnsureActive(token);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref closing) != 0, this);
    }

    private static bool IsAccessFailure(Exception error) => error is IOException or InvalidDataException or InvalidOperationException
        or ArgumentException or AdsErrorException or TwinCAT.Ads.Server.LoopbackNotRegisteredException
        or UnauthorizedAccessException or DllNotFoundException;
}
