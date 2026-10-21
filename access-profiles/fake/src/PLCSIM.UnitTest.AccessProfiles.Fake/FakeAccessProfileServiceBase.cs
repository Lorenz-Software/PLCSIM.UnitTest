using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;

namespace PLCSIM.UnitTest.AccessProfiles.Fake;

/// <summary>
/// Base class for reusable fake functional services.
/// </summary>
public abstract class FakeAccessProfileServiceBase : IAccessProfileService
{
    private readonly TimeProvider timeProvider;
    private ServiceState state = ServiceState.NotPrepared;
    private AccessHealth health;

    protected FakeAccessProfileServiceBase(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        health = AccessHealth.NotEstablished(this.timeProvider.GetUtcNow());
    }

    public event EventHandler<DiagnosticEventArgs>? DiagnosticEventHandler;

    public event EventHandler<ServiceStateChangedEventArgs>? StateChangedEventHandler;

    public event EventHandler<ProgressChangedEventArgs>? ProgressChangedEventHandler;

    internal event EventHandler<AccessHealthChangedEventArgs>? HealthChangedEventHandler;

    public ServiceState State => state;

    public AccessHealth Health => health;

    public void ReportDiagnostic(Diagnostic diagnostic)
    {
        DiagnosticEventHandler?.Invoke(this, new DiagnosticEventArgs(diagnostic));
    }

    protected void SetState(ServiceState newState, string reason)
    {
        if (state == newState)
        {
            return;
        }

        state = newState;
        StateChangedEventHandler?.Invoke(this, new ServiceStateChangedEventArgs(newState, reason));
        SetHealth(MapStateToHealth(newState, reason));
    }

    protected void ReportProgress(OperationProgress progress)
    {
        ProgressChangedEventHandler?.Invoke(this, new ProgressChangedEventArgs(progress));
    }

    protected void SetHealth(AccessHealth newHealth)
    {
        if (health == newHealth)
        {
            return;
        }

        health = newHealth;
        HealthChangedEventHandler?.Invoke(this, new AccessHealthChangedEventArgs([
            new AccessServiceHealthUpdate(GetServiceKind(), newHealth)
        ]));
    }

    protected abstract AccessServiceKind GetServiceKind();

    private AccessHealth MapStateToHealth(ServiceState serviceState, string reason)
    {
        var now = timeProvider.GetUtcNow();
        return serviceState switch
        {
            ServiceState.Ready => AccessHealth.Healthy(now),
            ServiceState.NeedsRevalidation => new AccessHealth(
                AccessHealthState.RevalidationRequired,
                AccessHealthProblem.RevalidationRequired,
                $"Fake.{GetServiceKind()}.RevalidationRequired",
                reason,
                now),
            ServiceState.Invalidated => new AccessHealth(
                AccessHealthState.Invalidated,
                AccessHealthProblem.ContextLost,
                $"Fake.{GetServiceKind()}.Invalidated",
                reason,
                now),
            ServiceState.Disposed => AccessHealth.Disposed(now),
            _ => AccessHealth.NotEstablished(now)
        };
    }
}
