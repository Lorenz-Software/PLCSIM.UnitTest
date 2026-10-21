using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.Workflow;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Workflow;

public sealed class WorkflowActionGateEvaluatorTests
{
    private readonly WorkflowActionGateEvaluator evaluator = new();

    [Fact]
    public void Operation_in_progress_makes_actions_pending()
    {
        var status = CreateStatus(operationInProgress: true);

        var gate = evaluator.Evaluate(status, WorkflowAction.PrepareProject);

        Assert.Equal(ActionGateAvailability.Pending, gate.Availability);
        Assert.Contains(gate.Reasons, reason => reason.Code == ActionGateReasonCode.OperationInProgress);
    }

    [Fact]
    public void Prepare_project_requires_configuration_profile_and_project_target()
    {
        var status = CreateStatus(hasAccessProfile: true, hasRunSetup: true, hasProjectTarget: true);

        var gate = evaluator.Evaluate(status, WorkflowAction.PrepareProject);

        Assert.Equal(ActionGateAvailability.Available, gate.Availability);
        Assert.Empty(gate.Reasons);
    }

    [Theory]
    [InlineData(WorkflowAction.DiscoverSafetyProgramAuthorizationRequirement)]
    [InlineData(WorkflowAction.AuthorizeSafetyProgram)]
    public void Safety_authorization_actions_require_prepared_project_access(WorkflowAction action)
    {
        var available = CreateStatus(
            hasAccessProfile: true,
            hasProjectTarget: true,
            projectSetupState: ProjectSetupState.ManagedProjectPrepared);
        var missingProject = CreateStatus(
            hasAccessProfile: true,
            hasProjectTarget: true);

        Assert.Equal(ActionGateAvailability.Available, evaluator.Evaluate(available, action).Availability);
        Assert.Contains(
            evaluator.Evaluate(missingProject, action).Reasons,
            reason => reason.Code == ActionGateReasonCode.ProjectNotPrepared);
    }

    [Theory]
    [InlineData(WorkflowAction.DiscoverSafetyProgramAuthorizationRequirement)]
    [InlineData(WorkflowAction.AuthorizeSafetyProgram)]
    public void Safety_authorization_actions_do_not_apply_to_externally_managed_projects(WorkflowAction action)
    {
        var status = CreateStatus(
            hasAccessProfile: true,
            hasProjectTarget: true,
            projectOwnershipMode: ProjectOwnershipMode.ExternallyManaged,
            projectSetupState: ProjectSetupState.ExternalManagementAccepted);

        Assert.Equal(ActionGateAvailability.NotApplicable, evaluator.Evaluate(status, action).Availability);
    }

    [Theory]
    [InlineData(RuntimeProvisioningMode.UseExisting, ActionGateAvailability.Blocked)]
    [InlineData(RuntimeProvisioningMode.CreateOwned, ActionGateAvailability.Available)]
    public void Prepare_runtime_requires_a_selected_target_only_for_existing_runtime_provisioning(
        RuntimeProvisioningMode provisioningMode,
        ActionGateAvailability expectedAvailability)
    {
        var status = CreateStatus(
            hasAccessProfile: true,
            hasRunSetup: true,
            runtimeProvisioningMode: provisioningMode,
            hasRuntimeTarget: false);

        var gate = evaluator.Evaluate(status, WorkflowAction.PrepareRuntime);

        Assert.Equal(expectedAvailability, gate.Availability);
        Assert.Equal(
            provisioningMode == RuntimeProvisioningMode.UseExisting,
            gate.Reasons.Any(reason => reason.Code == ActionGateReasonCode.RuntimeTargetNotSelected));
    }

    [Fact]
    public void Reconnect_runtime_requires_existing_prepared_runtime_context()
    {
        var missingRuntime = CreateStatus(
            hasAccessProfile: true,
            hasRunSetup: true,
            hasRuntimeTarget: true);
        var missingTarget = CreateStatus(
            hasAccessProfile: true,
            hasRunSetup: true,
            runtimePrepared: true);

        var runtimeGate = evaluator.Evaluate(missingRuntime, WorkflowAction.ReconnectRuntime);
        var targetGate = evaluator.Evaluate(missingTarget, WorkflowAction.ReconnectRuntime);

        Assert.Contains(runtimeGate.Reasons, reason => reason.Code == ActionGateReasonCode.RuntimeNotPrepared);
        Assert.Contains(targetGate.Reasons, reason => reason.Code == ActionGateReasonCode.RuntimeTargetNotSelected);
    }

    [Fact]
    public void Reconnect_runtime_allows_invalidated_runtime_communication_and_active_run()
    {
        var invalidated = new AccessHealth(
            AccessHealthState.Invalidated,
            AccessHealthProblem.CommunicationLost,
            "AccessHealth.Invalidated",
            "Runtime communication is invalidated.",
            DateTimeOffset.UtcNow);
        var healthy = AccessHealth.Healthy(DateTimeOffset.UtcNow);
        var status = CreateStatus(
            hasAccessProfile: true,
            hasRunSetup: true,
            hasRuntimeTarget: true,
            runtimePrepared: true,
            monitorState: TestRunMonitoringState.RunActive,
            accessHealth: new AccessHealthSnapshot(
                healthy,
                healthy,
                invalidated,
                invalidated,
                invalidated));

        var gate = evaluator.Evaluate(status, WorkflowAction.ReconnectRuntime);

        Assert.Equal(ActionGateAvailability.Available, gate.Availability);
        Assert.Empty(gate.Reasons);
    }

    [Fact]
    public void Start_unittest_requires_ready_run_control()
    {
        var notReady = evaluator.Evaluate(CreateRunnableStatus(lifecycle: Lifecycle(UnitTestLifecycleState.Idle, ready: false, running: false, UnitTestExecutionStatus.Idle)), WorkflowAction.StartUnitTest);
        var readyButExecutionStatusNotIdle = evaluator.Evaluate(CreateRunnableStatus(lifecycle: Lifecycle(UnitTestLifecycleState.Running, ready: true, running: false, UnitTestExecutionStatus.Running)), WorkflowAction.StartUnitTest);
        var readyWithCustomLifecycleState = evaluator.Evaluate(CreateRunnableStatus(lifecycle: Lifecycle(UnitTestLifecycleState.Running, ready: true, running: false, UnitTestExecutionStatus.Idle)), WorkflowAction.StartUnitTest);

        Assert.Equal(ActionGateAvailability.Blocked, notReady.Availability);
        Assert.Contains(notReady.Reasons, reason => reason.Code == ActionGateReasonCode.UnitTestNotReady);
        Assert.Equal(ActionGateAvailability.Blocked, readyButExecutionStatusNotIdle.Availability);
        Assert.Contains(readyButExecutionStatusNotIdle.Reasons, reason => reason.Code == ActionGateReasonCode.UnitTestNotReady);
        Assert.Equal(ActionGateAvailability.Available, readyWithCustomLifecycleState.Availability);
    }

    [Fact]
    public void Start_unittest_is_blocked_while_event_buffer_reports_lost_events()
    {
        var lifecycle = Lifecycle(
            UnitTestLifecycleState.Idle,
            ready: true,
            running: false,
            UnitTestExecutionStatus.Idle);
        var dirtyBuffer = CreateRunnableStatus(lifecycle) with
        {
            EventStreamState = CleanEventStreamState() with { LostEventCount = 3 }
        };

        var start = evaluator.Evaluate(dirtyBuffer, WorkflowAction.StartUnitTest);
        var reset = evaluator.Evaluate(dirtyBuffer, WorkflowAction.ResetEventBuffer);

        Assert.Equal(ActionGateAvailability.Blocked, start.Availability);
        Assert.Contains(start.Reasons, reason => reason.Code == ActionGateReasonCode.EventBufferHasLostEvents);
        Assert.Equal(ActionGateAvailability.Available, reset.Availability);
    }

    [Theory]
    [InlineData(UnitTestExecutionSignals.Uninitialized)]
    [InlineData(UnitTestExecutionSignals.ConfigError)]
    [InlineData(UnitTestExecutionSignals.Inactive)]
    [InlineData(UnitTestExecutionSignals.Error)]
    public void Start_unittest_rejects_blocking_execution_signals(UnitTestExecutionSignals blockingSignal)
    {
        var observation = new UnitTestRunControlObservation(
            UnitTestExecutionStatus.Idle,
            UnitTestExecutionSignals.Ready | blockingSignal,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated);

        var gate = evaluator.Evaluate(CreateRunnableStatus(observation), WorkflowAction.StartUnitTest);

        Assert.Equal(ActionGateAvailability.Blocked, gate.Availability);
        Assert.Contains(gate.Reasons, reason => reason.Code == ActionGateReasonCode.UnitTestNotReady);
    }

    [Fact]
    public void Start_plc_is_blocked_when_operating_state_is_running()
    {
        var status = CreateStatus(
            hasAccessProfile: true,
            hasRunSetup: true,
            hasProjectTarget: true,
            hasRuntimeTarget: true,
            projectSetupState: ProjectSetupState.ManagedTransferCompleted,
            runtimePrepared: true,
            plcRunning: true);

        var gate = evaluator.Evaluate(status, WorkflowAction.StartPlc);

        Assert.Equal(ActionGateAvailability.Blocked, gate.Availability);
        Assert.Contains(gate.Reasons, reason => reason.Code == ActionGateReasonCode.PlcNotStopped);
    }

    [Theory]
    [InlineData(PlcOperatingState.Starting)]
    [InlineData(PlcOperatingState.Running)]
    [InlineData(PlcOperatingState.Stopping)]
    [InlineData(PlcOperatingState.Frozen)]
    [InlineData(PlcOperatingState.Held)]
    [InlineData(PlcOperatingState.Unavailable)]
    public void Start_plc_requires_observed_stopped_operating_state(PlcOperatingState operatingState)
    {
        var status = CreateStatus(
            hasAccessProfile: true,
            hasRunSetup: true,
            hasProjectTarget: true,
            hasRuntimeTarget: true,
            projectSetupState: ProjectSetupState.ManagedTransferCompleted,
            runtimePrepared: true,
            plcOperatingState: operatingState);

        var gate = evaluator.Evaluate(status, WorkflowAction.StartPlc);

        Assert.Equal(ActionGateAvailability.Blocked, gate.Availability);
        Assert.Contains(gate.Reasons, reason => reason.Code == ActionGateReasonCode.PlcNotStopped);
    }

    [Theory]
    [InlineData(PlcOperatingState.Starting, ActionGateAvailability.Available)]
    [InlineData(PlcOperatingState.Running, ActionGateAvailability.Available)]
    [InlineData(PlcOperatingState.Frozen, ActionGateAvailability.Available)]
    [InlineData(PlcOperatingState.Held, ActionGateAvailability.Available)]
    [InlineData(PlcOperatingState.Stopping, ActionGateAvailability.Blocked)]
    [InlineData(PlcOperatingState.Stopped, ActionGateAvailability.Blocked)]
    public void Stop_plc_uses_observed_operating_state(
        PlcOperatingState operatingState,
        ActionGateAvailability expectedAvailability)
    {
        var status = CreateStatus(
            hasAccessProfile: true,
            hasRunSetup: true,
            hasProjectTarget: true,
            hasRuntimeTarget: true,
            projectSetupState: ProjectSetupState.ManagedTransferCompleted,
            runtimePrepared: true,
            plcOperatingState: operatingState);

        var gate = evaluator.Evaluate(status, WorkflowAction.StopPlc);

        Assert.Equal(expectedAvailability, gate.Availability);
    }

    [Fact]
    public void Abort_unittest_uses_coarse_execution_status_and_running_signal()
    {
        var running = evaluator.Evaluate(CreateRunnableStatus(lifecycle: Lifecycle(UnitTestLifecycleState.Aborting, ready: false, running: true, UnitTestExecutionStatus.Running)), WorkflowAction.AbortUnitTest);
        var aborting = evaluator.Evaluate(CreateRunnableStatus(lifecycle: Lifecycle(UnitTestLifecycleState.Running, ready: false, running: true, UnitTestExecutionStatus.Aborting)), WorkflowAction.AbortUnitTest);

        Assert.Equal(ActionGateAvailability.Available, running.Availability);
        Assert.Equal(ActionGateAvailability.Blocked, aborting.Availability);
        Assert.Contains(aborting.Reasons, reason => reason.Code == ActionGateReasonCode.NoRunAvailable);
    }

    [Fact]
    public void Release_runtime_is_blocked_while_unit_test_communication_context_is_active()
    {
        var status = CreateStatus(
            hasAccessProfile: true,
            unitTestCommunicationContextActive: true);

        var gate = evaluator.Evaluate(status, WorkflowAction.ReleaseRuntime);

        Assert.Equal(ActionGateAvailability.Blocked, gate.Availability);
        Assert.Contains(gate.Reasons, reason => reason.Code == ActionGateReasonCode.UnitTestCommunicationStillPrepared);
    }

    [Fact]
    public void Discover_targets_requires_usable_profile_access_health()
    {
        var status = CreateStatus(
            hasAccessProfile: true,
            accessHealth: AccessHealthWithProfile(ProfileRevalidationRequired()));

        var gate = evaluator.Evaluate(status, WorkflowAction.DiscoverProjectTargets);

        Assert.Equal(ActionGateAvailability.Blocked, gate.Availability);
        Assert.Contains(gate.Reasons, reason => reason.Code == ActionGateReasonCode.AccessRevalidationRequired);
    }

    [Fact]
    public void Completed_project_setup_does_not_require_live_project_access_for_plc_actions()
    {
        var status = CreateStatus(
            hasAccessProfile: true,
            hasRunSetup: true,
            hasRuntimeTarget: true,
            projectSetupState: ProjectSetupState.ManagedTransferCompleted,
            runtimePrepared: true,
            accessHealth: AccessHealthWithProject(ProfileRevalidationRequired()));

        var gate = evaluator.Evaluate(status, WorkflowAction.StartPlc);

        Assert.Equal(ActionGateAvailability.Available, gate.Availability);
        Assert.DoesNotContain(gate.Reasons, reason => reason.Code == ActionGateReasonCode.ProjectAccessUnhealthy);
        Assert.DoesNotContain(gate.Reasons, reason => reason.Code == ActionGateReasonCode.AccessRevalidationRequired);
    }

    [Fact]
    public void Unload_access_profile_does_not_require_usable_profile_access_health()
    {
        var status = CreateStatus(
            hasAccessProfile: true,
            accessHealth: AccessHealthWithProfile(ProfileRevalidationRequired()));

        var gate = evaluator.Evaluate(status, WorkflowAction.UnloadAccessProfile);

        Assert.Equal(ActionGateAvailability.Available, gate.Availability);
        Assert.Empty(gate.Reasons);
    }

    private static WorkflowStatusSnapshot CreateRunnableStatus(
        UnitTestRunControlObservation lifecycle)
    {
        return CreateStatus(
            hasAccessProfile: true,
            hasRunSetup: true,
            hasProjectTarget: true,
            hasRuntimeTarget: true,
            projectSetupState: ProjectSetupState.ManagedTransferCompleted,
            runtimePrepared: true,
            plcRunning: true,
            unitTestCommunicationPrepared: true,
            unitTestCommunicationContextActive: true,
            monitoringActive: true,
            lifecycle: lifecycle,
            eventStreamState: CleanEventStreamState());
    }

    private static WorkflowStatusSnapshot CreateStatus(
        bool operationInProgress = false,
        bool hasAccessProfile = false,
        bool hasRunSetup = false,
        RuntimeProvisioningMode? runtimeProvisioningMode = null,
        bool hasProjectTarget = false,
        bool hasRuntimeTarget = false,
        ProjectOwnershipMode projectOwnershipMode = ProjectOwnershipMode.ApplicationManaged,
        ProjectSetupState projectSetupState = ProjectSetupState.NotEstablished,
        bool projectAccessActive = false,
        bool runtimePrepared = false,
        bool plcRunning = false,
        PlcOperatingState? plcOperatingState = null,
        RuntimeInstanceState runtimeInstanceState = RuntimeInstanceState.Available,
        bool unitTestCommunicationPrepared = false,
        bool unitTestCommunicationContextActive = false,
        bool monitoringActive = false,
        UnitTestRunControlObservation? lifecycle = null,
        EventStreamState? eventStreamState = null,
        TestRunMonitoringState monitorState = TestRunMonitoringState.NotMonitoring,
        bool isExportable = false,
        AccessHealthSnapshot? accessHealth = null)
    {
        return new WorkflowStatusSnapshot(
            RunWorkflowState.NotConfigured,
            null,
            operationInProgress,
            hasAccessProfile,
            hasAccessProfile ? "fake" : null,
            hasAccessProfile ? "1.0.0" : null,
            projectOwnershipMode,
            hasRunSetup,
            hasRunSetup ? runtimeProvisioningMode ?? RuntimeProvisioningMode.UseExisting : null,
            hasProjectTarget,
            hasRuntimeTarget,
            projectSetupState,
            projectAccessActive,
            runtimePrepared,
            new PlcState(
                runtimeInstanceState,
                plcOperatingState ?? (plcRunning ? PlcOperatingState.Running : PlcOperatingState.Stopped),
                Array.Empty<RuntimeTargetRef>(),
                ReadyForTransfer: true,
                ReadyForExecution: plcRunning || plcOperatingState == PlcOperatingState.Running),
            unitTestCommunicationPrepared,
            unitTestCommunicationContextActive,
            monitoringActive,
            lifecycle,
            eventStreamState,
            monitorState,
            null,
            accessHealth ?? HealthyAccess(),
            isExportable);
    }

    private static EventStreamState CleanEventStreamState()
    {
        return new EventStreamState(
            new CommunicationSessionId("communication-session-1"),
            IsMonitoring: true,
            EventBufferReadState.Empty,
            EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty: true,
            LostEventCount: 0);
    }

    private static AccessHealthSnapshot HealthyAccess()
    {
        var healthy = AccessHealth.Healthy(DateTimeOffset.UtcNow);
        return new AccessHealthSnapshot(healthy, healthy, healthy, healthy, healthy);
    }

    private static AccessHealthSnapshot AccessHealthWithProfile(AccessHealth profile)
    {
        var healthy = AccessHealth.Healthy(DateTimeOffset.UtcNow);
        return new AccessHealthSnapshot(profile, healthy, healthy, healthy, healthy);
    }

    private static AccessHealthSnapshot AccessHealthWithProject(AccessHealth project)
    {
        var healthy = AccessHealth.Healthy(DateTimeOffset.UtcNow);
        return new AccessHealthSnapshot(healthy, project, healthy, healthy, healthy);
    }

    private static AccessHealth ProfileRevalidationRequired()
    {
        return new AccessHealth(
            AccessHealthState.RevalidationRequired,
            AccessHealthProblem.RevalidationRequired,
            "AccessHealth.RevalidationRequired",
            "Access context requires revalidation.",
            DateTimeOffset.UtcNow);
    }

    private static UnitTestRunControlObservation Lifecycle(
        UnitTestLifecycleState state,
        bool ready,
        bool running,
        UnitTestExecutionStatus executionStatus)
    {
        var signals = UnitTestExecutionSignals.None;
        if (ready)
        {
            signals |= UnitTestExecutionSignals.Ready;
        }

        if (running)
        {
            signals |= UnitTestExecutionSignals.Running;
        }

        return new UnitTestRunControlObservation(
            executionStatus,
            signals,
            state,
            UnitTestResultState.NotEvaluated);
    }
}
