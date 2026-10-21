using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff;

internal sealed class BeckhoffAccessProfileSession : IAccessProfileSession
{
    private readonly AccessProfileContext context;
    private readonly Action onDisposed;
    private readonly BeckhoffConfigurationService configuration;
    private readonly BeckhoffServiceBase[] services;
    private readonly object disposeGate = new();
    private readonly SemaphoreSlim communicationOperations = new(1, 1);
    private Task? disposal;
    private int disposed;

    internal BeckhoffAccessProfileSession(AccessProfileContext context, AccessProfileSessionRequest request, Action onDisposed)
        : this(context, request, onDisposed, new BeckhoffPlcControlService(context, request.ProjectOwnershipMode),
            new BeckhoffUnitTestLifecycleService(context), new BeckhoffUnitTestEventStreamService(context)) { }

    internal BeckhoffAccessProfileSession(AccessProfileContext context, AccessProfileSessionRequest request, Action onDisposed,
        BeckhoffPlcControlService runtime, BeckhoffUnitTestLifecycleService lifecycle, BeckhoffUnitTestEventStreamService stream)
    {
        this.context = context;
        this.onDisposed = onDisposed;
        configuration = new(request.ProjectSource, request.ProjectOwnershipMode, context.TimeProvider);
        var project = new BeckhoffProjectService(context, request.ProjectOwnershipMode, runtimeTransfer: runtime);
        Project = project;
        PlcControl = runtime;
        UnitTestLifecycle = lifecycle;
        UnitTestEventStream = stream;
        services = [project, runtime, lifecycle, stream];
        foreach (var service in services)
        {
            service.HealthChanged += ForwardServiceHealth;
        }
        Health = AccessHealth.Healthy(context.TimeProvider.GetUtcNow());
    }

    public AccessProfileInfo Info => BeckhoffAccessProfilePlugin.ProfileInfo;
    public AccessHealth Health { get; private set; }
    public IAccessProfileConfigurationService Configuration => configuration;
    public IPlcProjectService Project { get; }
    public IPlcControlService PlcControl { get; }
    public IUnitTestLifecycleService UnitTestLifecycle { get; }
    public IUnitTestEventStreamService UnitTestEventStream { get; }
    public event EventHandler<DiagnosticEventArgs>? DiagnosticEventHandler;
    public event EventHandler<ProfileStateChangedEventArgs>? StateChangedEventHandler;
    public event EventHandler<AccessHealthChangedEventArgs>? HealthChangedEventHandler;

    public async Task<OperationResult<PreparedPlcRuntime>> ReconnectRuntimeAsync(PlcRuntimeReconnectRequest request, CancellationToken cancellationToken)
    {
        EnsureActive(cancellationToken);
        await communicationOperations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive(cancellationToken);
            return await ((BeckhoffPlcControlService)PlcControl).ReconnectRuntimeAsync(request, cancellationToken,
                ReleaseCommunicationAsync).ConfigureAwait(false);
        }
        finally { communicationOperations.Release(); }
    }

    internal async Task<OperationResult> PrepareRunControlAsync(CommunicationPrepareRequest request,
        CommunicationSession session, CancellationToken cancellationToken)
    {
        EnsureActive(cancellationToken);
        var settings = RunControlSettings.Parse(request.Configuration, request.ProjectContext.OwnershipMode);
        var endpoint = await ((BeckhoffPlcControlService)PlcControl)
            .GetDataEndpointAsync(request.Runtime, request.RuntimeTarget, cancellationToken).ConfigureAwait(false);
        return await ((BeckhoffUnitTestLifecycleService)UnitTestLifecycle).PrepareAsync(
            new(session, request.ProjectContext, request.Runtime, request.RuntimeTarget), settings, endpoint, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<CommunicationSession>> PrepareUnitTestCommunicationAsync(CommunicationPrepareRequest request, CancellationToken cancellationToken)
    {
        EnsureActive(cancellationToken);
        ArgumentNullException.ThrowIfNull(request);
        await communicationOperations.WaitAsync(cancellationToken).ConfigureAwait(false);
        var setupStarted = false;
        var prepared = false;
        try
        {
            EnsureActive(cancellationToken);
            var streamSettings = EventStreamSettings.Parse(request.Configuration, request.ProjectContext.OwnershipMode);
            var lifecycleSettings = RunControlSettings.Parse(request.Configuration, request.ProjectContext.OwnershipMode);
            setupStarted = true;
            await ReleaseCommunicationAsync().ConfigureAwait(false);
            var endpoint = await ((BeckhoffPlcControlService)PlcControl)
                .GetDataEndpointAsync(request.Runtime, request.RuntimeTarget, cancellationToken).ConfigureAwait(false);
            var session = new CommunicationSession(new(Guid.NewGuid().ToString("N")), context.TimeProvider.GetUtcNow());
            var unitTest = new UnitTestContext(session, request.ProjectContext, request.Runtime, request.RuntimeTarget);
            var bindingGeneration = endpoint.BindingGeneration();
            var stream = await ((BeckhoffUnitTestEventStreamService)UnitTestEventStream)
                .PrepareAsync(unitTest, streamSettings, endpoint, cancellationToken).ConfigureAwait(false);
            if (!stream.IsSuccessful) return OperationResult<CommunicationSession>.Failure(stream.Message ?? "EventStream preparation failed.");
            var lifecycle = await ((BeckhoffUnitTestLifecycleService)UnitTestLifecycle)
                .PrepareAsync(unitTest, lifecycleSettings, endpoint, cancellationToken).ConfigureAwait(false);
            if (!lifecycle.IsSuccessful) return OperationResult<CommunicationSession>.Failure(lifecycle.Message ?? "RunControl preparation failed.");
            EnsureActive(cancellationToken);
            if (!endpoint.IsCurrent() || !endpoint.IsAvailable() || endpoint.BindingGeneration() != bindingGeneration)
                throw new IOException("The selected PLC application became unavailable during communication preparation.");
            prepared = true;
            return OperationResult<CommunicationSession>.Success(session, "TwinCAT UnitTest communication prepared.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ReportCommunicationDiagnostic("Beckhoff.Communication.PrepareFailed", error.Message, DiagnosticPhase.CommunicationValidation);
            return OperationResult<CommunicationSession>.Failure(error.Message);
        }
        finally
        {
            try
            {
                if (setupStarted && !prepared)
                {
                    try { await ReleaseCommunicationAsync().ConfigureAwait(false); }
                    catch (Exception error) { ReportCommunicationDiagnostic("Beckhoff.Communication.CleanupFailed", error.Message, DiagnosticPhase.Cleanup); }
                }
            }
            finally { communicationOperations.Release(); }
        }
    }

    private async Task ReleaseCommunicationAsync()
    {
        Exception? failure = null;
        foreach (var service in new BeckhoffServiceBase[] { (BeckhoffServiceBase)UnitTestEventStream, (BeckhoffServiceBase)UnitTestLifecycle })
        {
            try
            {
                var result = await service.CleanupAsync(CancellationToken.None).ConfigureAwait(false);
                if (!result.IsSuccessful) failure ??= new IOException(result.Message ?? "UnitTest communication cleanup failed.");
            }
            catch (Exception error) { failure ??= error; }
        }
        if (failure is not null) throw new IOException("UnitTest communication cleanup failed: " + failure.Message, failure);
    }

    private void ReportCommunicationDiagnostic(string code, string message, DiagnosticPhase phase)
    {
        context.LoggerFactory.CreateLogger<BeckhoffAccessProfileSession>().LogError("{Code}: {Message}", code, message);
        DiagnosticEventHandler?.Invoke(this, new(new(code, phase == DiagnosticPhase.Cleanup ? DiagnosticCategory.Cleanup : DiagnosticCategory.Communication,
            LogLevel.Error, DiagnosticSource.AccessProfile, phase, message, new(), context.TimeProvider.GetUtcNow())));
    }

    internal async Task<OperationResult> PrepareEventStreamAsync(CommunicationPrepareRequest request,
        CommunicationSession session, CancellationToken cancellationToken)
    {
        EnsureActive(cancellationToken);
        var settings = EventStreamSettings.Parse(request.Configuration, request.ProjectContext.OwnershipMode);
        var endpoint = await ((BeckhoffPlcControlService)PlcControl)
            .GetDataEndpointAsync(request.Runtime, request.RuntimeTarget, cancellationToken).ConfigureAwait(false);
        return await ((BeckhoffUnitTestEventStreamService)UnitTestEventStream).PrepareAsync(
            new(session, request.ProjectContext, request.Runtime, request.RuntimeTarget), settings, endpoint, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            if (disposal is not null) return new(disposal);
            // A synchronous disposal observer can re-enter before the task is assigned.
            if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
            return new(disposal = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await communicationOperations.WaitAsync().ConfigureAwait(false);
        try { await DisposeServicesAsync().ConfigureAwait(false); }
        finally { communicationOperations.Release(); }
    }

    private async Task DisposeServicesAsync()
    {
        Exception? closeError = null;
        foreach (var service in services.AsEnumerable().Reverse())
        {
            try { await service.CloseAsync().ConfigureAwait(false); }
            catch (Exception error) { closeError ??= error; }
        }

        // Owned access is stopped before final invalidation. Host-owned logging is not disposed.
        configuration.Dispose();
        foreach (var service in services)
        {
            service.HealthChanged -= ForwardServiceHealth;
            service.MarkDisposed();
        }
        Health = AccessHealth.Disposed(context.TimeProvider.GetUtcNow());
        try
        {
            foreach (var service in services) service.NotifyDisposed();
            HealthChangedEventHandler?.Invoke(this, new([
                new(AccessServiceKind.Profile, Health),
                new(AccessServiceKind.Project, Project.Health),
                new(AccessServiceKind.PlcControl, PlcControl.Health),
                new(AccessServiceKind.UnitTestLifecycle, UnitTestLifecycle.Health),
                new(AccessServiceKind.UnitTestEventStream, UnitTestEventStream.Health)]));
            StateChangedEventHandler?.Invoke(this, new(AccessProfileState.Disposed, "Profile session disposed."));
        }
        finally
        {
            DiagnosticEventHandler = null;
            StateChangedEventHandler = null;
            HealthChangedEventHandler = null;
            onDisposed();
        }
        if (closeError is not null) throw new IOException("TwinCAT profile technical cleanup failed.", closeError);
    }

    private void ForwardServiceHealth(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0 || sender is not BeckhoffServiceBase service) return;
        var kind = ReferenceEquals(service, PlcControl) ? AccessServiceKind.PlcControl
            : ReferenceEquals(service, Project) ? AccessServiceKind.Project
            : ReferenceEquals(service, UnitTestLifecycle) ? AccessServiceKind.UnitTestLifecycle
            : AccessServiceKind.UnitTestEventStream;
        HealthChangedEventHandler?.Invoke(this, new([new(kind, service.Health)]));
    }
    private void EnsureActive(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
