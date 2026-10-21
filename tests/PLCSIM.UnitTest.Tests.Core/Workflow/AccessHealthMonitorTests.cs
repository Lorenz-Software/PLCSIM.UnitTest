using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Core.Analysis;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Workflow;

public sealed class AccessHealthMonitorTests
{
    [Fact]
    public void Initial_snapshot_marks_all_access_contexts_not_established()
    {
        var monitor = new AccessHealthMonitor(FakeTime.Create());

        Assert.Equal(AccessHealthState.NotEstablished, monitor.Current.Profile.State);
        Assert.Equal(AccessHealthState.NotEstablished, monitor.Current.Project.State);
        Assert.Equal(AccessHealthState.NotEstablished, monitor.Current.PlcControl.State);
        Assert.Equal(AccessHealthState.NotEstablished, monitor.Current.UnitTestLifecycle.State);
        Assert.Equal(AccessHealthState.NotEstablished, monitor.Current.UnitTestEventStream.State);
    }

    [Fact]
    public void Bind_publishes_initial_profile_and_service_health_snapshot()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var profile = CreateProfile(services, timeProvider);
        var monitor = new AccessHealthMonitor(timeProvider);
        AccessHealthMonitorUpdateEventArgs? update = null;
        monitor.UpdateEventHandler += (_, args) => update = args;

        monitor.Bind(profile);

        Assert.NotNull(update);
        Assert.Equal(5, update!.Updates.Count);
        Assert.Equal(AccessHealthState.NotEstablished, monitor.Current.Profile.State);
        Assert.Equal(AccessHealthState.NotEstablished, monitor.Current.Project.State);
        Assert.Equal(AccessHealthState.NotEstablished, monitor.Current.PlcControl.State);
        Assert.Equal(AccessHealthState.NotEstablished, monitor.Current.UnitTestLifecycle.State);
        Assert.Equal(AccessHealthState.NotEstablished, monitor.Current.UnitTestEventStream.State);
    }

    [Fact]
    public void Bound_monitor_updates_only_affected_service_health()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var profile = CreateProfile(services, timeProvider);
        var monitor = new AccessHealthMonitor(timeProvider);
        monitor.Bind(profile);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);

        plcControl.SimulateRuntimeContextLost();

        Assert.Equal(AccessHealthState.Invalidated, monitor.Current.PlcControl.State);
        Assert.Equal(AccessHealthProblem.ContextLost, monitor.Current.PlcControl.Problem);
        Assert.Equal(AccessHealthState.NotEstablished, monitor.Current.Profile.State);
        Assert.Equal(AccessHealthState.NotEstablished, monitor.Current.Project.State);
        Assert.Equal(AccessHealthState.NotEstablished, monitor.Current.UnitTestLifecycle.State);
        Assert.Equal(AccessHealthState.NotEstablished, monitor.Current.UnitTestEventStream.State);
    }

    [Fact]
    public void Revalidation_health_update_is_traced_with_a_new_lifecycle_observation()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var profile = CreateProfile(services, timeProvider);
        var sink = new RecordingUnitTestActivityTraceSink();
        var context = new UnitTestActivityTraceContext();
        context.StartSession(new ApplicationSessionId("application"));
        var monitor = new AccessHealthMonitor(
            TestCoreServiceContext.Create(timeProvider: timeProvider),
            sink,
            context);
        monitor.Bind(profile);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);

        lifecycle.SimulateLifecycleCommunicationRevalidationRequired();

        var activity = Assert.Single(sink.Activities.Where(activity =>
            activity.Payload is AccessHealthActivityPayload
            {
                ServiceKind: AccessServiceKind.UnitTestLifecycle,
                Health.State: AccessHealthState.RevalidationRequired
            }));
        Assert.NotNull(activity.Correlation.LifecycleObservationId);
        Assert.Equal(
            activity.Correlation.LifecycleObservationId,
            context.Current.LifecycleObservationId);
    }

    [Fact]
    public void Unbind_stops_observing_previous_profile_health_updates()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var profile = CreateProfile(services, timeProvider);
        var monitor = new AccessHealthMonitor(timeProvider);
        monitor.Bind(profile);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);

        monitor.Unbind();
        lifecycle.SimulateLifecycleCommunicationInvalidated();

        Assert.Equal(AccessHealthState.NotEstablished, monitor.Current.UnitTestLifecycle.State);
    }

    private static FakeAccessProfile CreateProfile(
        FakeAccessProfileServices services,
        TimeProvider timeProvider)
    {
        return new FakeAccessProfile(
            FakeAccessProfilePlugin.CreateDefaultInfo(),
            services,
            timeProvider);
    }
}
