using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Logging;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff;

internal abstract class BeckhoffServiceBase : IAccessProfileService
{
    protected AccessProfileContext Context { get; }
    protected ILogger Logger { get; }

    protected BeckhoffServiceBase(AccessProfileContext context)
    {
        Context = context;
        Logger = new ProfileLogger(context.LoggerFactory.CreateLogger(GetType().FullName!));
        Health = AccessHealth.NotEstablished(context.TimeProvider.GetUtcNow());
    }

    public event EventHandler<DiagnosticEventArgs>? DiagnosticEventHandler;
    public event EventHandler<ServiceStateChangedEventArgs>? StateChangedEventHandler;
    internal event EventHandler? HealthChanged;
    // No progress can be produced until live operations are implemented.
#pragma warning disable CS0067
    public event EventHandler<ProgressChangedEventArgs>? ProgressChangedEventHandler;
#pragma warning restore CS0067
    public ServiceState State { get; private set; } = ServiceState.NotPrepared;
    public AccessHealth Health { get; private set; }

    protected Task<OperationResult<T>> Unsupported<T>(string operation, CancellationToken cancellationToken)
    {
        EnsureActive(cancellationToken);
        var message = $"TwinCAT {operation} is not implemented by this profile version.";
        Logger.LogWarning("{Message}", message);
        DiagnosticEventHandler?.Invoke(this, new(new Diagnostic(
            "Beckhoff.Operation.NotSupported", DiagnosticCategory.Environment, LogLevel.Error,
            DiagnosticSource.AccessProfile, DiagnosticPhase.ProfileValidation, message,
            new DiagnosticCorrelation(), Context.TimeProvider.GetUtcNow())));
        return Task.FromResult(OperationResult<T>.NotSupported(message));
    }

    public virtual Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
    {
        EnsureActive(cancellationToken);
        return Task.FromResult(OperationResult.Success("No technical access resources have been acquired."));
    }

    internal void MarkDisposed()
    {
        State = ServiceState.Disposed;
        Health = AccessHealth.Disposed(Context.TimeProvider.GetUtcNow());
    }

    internal void NotifyDisposed() => StateChangedEventHandler?.Invoke(this, new(State, "Profile session disposed."));

    internal virtual Task CloseAsync() => Task.CompletedTask;

    protected void SetHealth(AccessHealth health)
    {
        if (Health.State == health.State && Health.Problem == health.Problem
            && Health.ReasonCode == health.ReasonCode && Health.Message == health.Message) return;
        Health = health;
        HealthChanged?.Invoke(this, EventArgs.Empty);
    }

    protected void SetState(ServiceState state, string reason)
    {
        if (State == state) return;
        Logger.LogInformation("{Service} state {Previous} -> {State}: {Reason}", GetType().Name, State, state, reason);
        State = state;
        StateChangedEventHandler?.Invoke(this, new(State, reason));
    }

    protected Diagnostic ReportDiagnostic(string code, string message, DiagnosticCategory category,
        DiagnosticPhase phase, LogLevel level = LogLevel.Error)
    {
        Logger.Log(level, "{Code}: {Message}", code, message);
        var diagnostic = new Diagnostic(code, category, level, DiagnosticSource.AccessProfile,
            phase, message, new DiagnosticCorrelation(), Context.TimeProvider.GetUtcNow());
        DiagnosticEventHandler?.Invoke(this, new(diagnostic));
        return diagnostic;
    }

    protected void EnsureActive(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(State == ServiceState.Disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
