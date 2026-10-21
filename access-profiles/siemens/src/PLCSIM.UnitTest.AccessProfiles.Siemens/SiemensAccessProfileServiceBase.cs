using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal abstract class SiemensAccessProfileServiceBase : IAccessProfileService
{
    private readonly TimeProvider timeProvider;

    protected SiemensAccessProfileServiceBase(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Health = AccessHealth.Healthy(timeProvider.GetUtcNow());
    }

    public event EventHandler<DiagnosticEventArgs>? DiagnosticEventHandler;

    public event EventHandler<ServiceStateChangedEventArgs>? StateChangedEventHandler;

    public event EventHandler<ProgressChangedEventArgs>? ProgressChangedEventHandler;

    internal event EventHandler<AccessHealthChangedEventArgs>? HealthChangedEventHandler;

    public ServiceState State { get; private set; } = ServiceState.Ready;

    public AccessHealth Health { get; private set; }

    protected TimeProvider TimeProvider => timeProvider;

    protected void SetDisposed(string reason)
    {
        State = ServiceState.Disposed;
        Health = AccessHealth.Disposed(timeProvider.GetUtcNow());
        StateChangedEventHandler?.Invoke(this, new ServiceStateChangedEventArgs(State, reason));
        HealthChangedEventHandler?.Invoke(this, new AccessHealthChangedEventArgs([
            new AccessServiceHealthUpdate(GetServiceKind(), Health)
        ]));
    }

    protected void SetHealth(AccessHealth health)
    {
        if (Health == health)
        {
            return;
        }

        Health = health;
        HealthChangedEventHandler?.Invoke(this, new AccessHealthChangedEventArgs([
            new AccessServiceHealthUpdate(GetServiceKind(), Health)
        ]));
    }

    protected void ReportDiagnostic(Diagnostic diagnostic)
    {
        DiagnosticEventHandler?.Invoke(this, new DiagnosticEventArgs(diagnostic));
    }

    protected void ReportProgress(OperationProgress progress)
    {
        ProgressChangedEventHandler?.Invoke(this, new ProgressChangedEventArgs(progress));
    }

    protected abstract AccessServiceKind GetServiceKind();
}
