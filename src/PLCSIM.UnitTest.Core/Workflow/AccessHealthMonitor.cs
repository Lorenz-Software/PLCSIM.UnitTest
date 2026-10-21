using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Core.Analysis;

namespace PLCSIM.UnitTest.Core.Workflow;

public sealed class AccessHealthMonitor
{
    private readonly object syncRoot = new();
    private readonly Dictionary<AccessServiceKind, AccessHealth> healthByService = new();
    private readonly IUnitTestActivityTraceSink? activityTraceSink;
    private readonly UnitTestActivityTraceContext? activityTraceContext;
    private IAccessProfileSession? profile;

    public AccessHealthMonitor(CoreServiceContext coreContext)
        : this(coreContext, activityTraceSink: null, activityTraceContext: null)
    {
    }

    public AccessHealthMonitor(
        CoreServiceContext coreContext,
        IUnitTestActivityTraceSink? activityTraceSink,
        UnitTestActivityTraceContext? activityTraceContext)
        : this(
            coreContext?.TimeProvider ?? throw new ArgumentNullException(nameof(coreContext)),
            activityTraceSink,
            activityTraceContext)
    {
    }

    public AccessHealthMonitor(
        TimeProvider timeProvider,
        IUnitTestActivityTraceSink? activityTraceSink = null,
        UnitTestActivityTraceContext? activityTraceContext = null)
    {
        this.activityTraceSink = activityTraceSink;
        this.activityTraceContext = activityTraceContext;
        var initial = AccessHealth.NotEstablished(timeProvider.GetUtcNow());
        foreach (var kind in Enum.GetValues<AccessServiceKind>())
        {
            healthByService[kind] = initial;
        }

        Current = CreateSnapshot();
    }

    public AccessHealthSnapshot Current { get; private set; }

    public event EventHandler<AccessHealthMonitorUpdateEventArgs>? UpdateEventHandler;

    public void Bind(IAccessProfileSession loadedProfile)
    {
        ArgumentNullException.ThrowIfNull(loadedProfile);

        Unbind();
        profile = loadedProfile;
        loadedProfile.HealthChangedEventHandler += OnProfileHealthChanged;

        ApplyUpdates([
            new AccessServiceHealthUpdate(AccessServiceKind.Profile, loadedProfile.Health),
            new AccessServiceHealthUpdate(AccessServiceKind.Project, loadedProfile.Project.Health),
            new AccessServiceHealthUpdate(AccessServiceKind.PlcControl, loadedProfile.PlcControl.Health),
            new AccessServiceHealthUpdate(AccessServiceKind.UnitTestLifecycle, loadedProfile.UnitTestLifecycle.Health),
            new AccessServiceHealthUpdate(AccessServiceKind.UnitTestEventStream, loadedProfile.UnitTestEventStream.Health)
        ]);
    }

    public void Unbind()
    {
        if (profile is not null)
        {
            profile.HealthChangedEventHandler -= OnProfileHealthChanged;
            profile = null;
        }
    }

    private void OnProfileHealthChanged(object? sender, AccessHealthChangedEventArgs args)
    {
        ApplyUpdates(args.Updates);
    }

    private void ApplyUpdates(IReadOnlyList<AccessServiceHealthUpdate> updates)
    {
        if (updates.Count == 0)
        {
            return;
        }

        AccessHealthSnapshot snapshot;
        lock (syncRoot)
        {
            foreach (var update in updates)
            {
                healthByService[update.ServiceKind] = update.Health;
            }

            Current = CreateSnapshot();
            snapshot = Current;
        }

        TraceUpdates(updates);
        UpdateEventHandler?.Invoke(this, new AccessHealthMonitorUpdateEventArgs(snapshot, updates.ToArray()));
    }

    private void TraceUpdates(IReadOnlyList<AccessServiceHealthUpdate> updates)
    {
        if (activityTraceSink is null || activityTraceContext is null)
        {
            return;
        }

        foreach (var update in updates)
        {
            var current = activityTraceContext.Current;
            if (current.ApplicationSessionId is not { } sessionId)
            {
                continue;
            }

            if (update.Health.State == AccessHealthState.RevalidationRequired)
            {
                var observationId = new LifecycleObservationId(Guid.NewGuid().ToString("N"));
                activityTraceContext.TrySetLifecycleObservation(sessionId, observationId);
                current = activityTraceContext.Current;
            }

            activityTraceSink.TryRecord(
                new AccessHealthActivityPayload(update.ServiceKind, update.Health),
                new UnitTestActivityCorrelation(
                    LifecycleObservationId: current.LifecycleObservationId));
        }
    }

    private AccessHealthSnapshot CreateSnapshot()
    {
        return new AccessHealthSnapshot(
            healthByService[AccessServiceKind.Profile],
            healthByService[AccessServiceKind.Project],
            healthByService[AccessServiceKind.PlcControl],
            healthByService[AccessServiceKind.UnitTestLifecycle],
            healthByService[AccessServiceKind.UnitTestEventStream]);
    }
}

public sealed record AccessHealthSnapshot(
    AccessHealth Profile,
    AccessHealth Project,
    AccessHealth PlcControl,
    AccessHealth UnitTestLifecycle,
    AccessHealth UnitTestEventStream)
{
    public AccessHealth Get(AccessServiceKind serviceKind)
    {
        return serviceKind switch
        {
            AccessServiceKind.Profile => Profile,
            AccessServiceKind.Project => Project,
            AccessServiceKind.PlcControl => PlcControl,
            AccessServiceKind.UnitTestLifecycle => UnitTestLifecycle,
            AccessServiceKind.UnitTestEventStream => UnitTestEventStream,
            _ => throw new ArgumentOutOfRangeException(nameof(serviceKind), serviceKind, "Unknown access service kind.")
        };
    }
}

public sealed record AccessHealthMonitorUpdateEventArgs(
    AccessHealthSnapshot Snapshot,
    IReadOnlyList<AccessServiceHealthUpdate> Updates);
