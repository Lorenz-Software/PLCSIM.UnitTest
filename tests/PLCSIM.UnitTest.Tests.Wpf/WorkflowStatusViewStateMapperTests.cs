using PLCSIM.UnitTest.App.Wpf.ViewModels;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.Workflow;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Wpf;

public sealed class WorkflowStatusViewStateMapperTests
{
    [Theory]
    [InlineData(UnitTestExecutionStatus.Starting, ProcessStatusKind.Starting)]
    [InlineData(UnitTestExecutionStatus.Running, ProcessStatusKind.Running)]
    [InlineData(UnitTestExecutionStatus.Completing, ProcessStatusKind.Running)]
    [InlineData(UnitTestExecutionStatus.Completed, ProcessStatusKind.Completed)]
    [InlineData(UnitTestExecutionStatus.Aborting, ProcessStatusKind.Aborting)]
    [InlineData(UnitTestExecutionStatus.Aborted, ProcessStatusKind.Completed)]
    [InlineData(UnitTestExecutionStatus.Reset, ProcessStatusKind.Resetting)]
    public void Run_control_badge_uses_coarse_execution_status(
        UnitTestExecutionStatus executionStatus,
        ProcessStatusKind expectedKind)
    {
        var observation = Observation(
            executionStatus,
            UnitTestExecutionSignals.None,
            UnitTestLifecycleState.Idle);

        var result = WorkflowStatusViewStateMapper.MapRunControlStatus(Status(observation));

        Assert.Equal(expectedKind, result.Kind);
    }

    [Fact]
    public void Detailed_lifecycle_state_alone_does_not_drive_run_control_badge()
    {
        var observation = Observation(
            UnitTestExecutionStatus.Unknown,
            UnitTestExecutionSignals.None,
            UnitTestLifecycleState.Completed);

        var result = WorkflowStatusViewStateMapper.MapRunControlStatus(Status(observation));

        Assert.Equal(ProcessStatusKind.NotReady, result.Kind);
    }

    [Theory]
    [InlineData(UnitTestExecutionSignals.Running, ProcessStatusKind.Running)]
    [InlineData(UnitTestExecutionSignals.Completed, ProcessStatusKind.Completed)]
    [InlineData(UnitTestExecutionSignals.Aborted, ProcessStatusKind.Completed)]
    public void Run_control_badge_preserves_independent_progress_signals(
        UnitTestExecutionSignals signals,
        ProcessStatusKind expectedKind)
    {
        var observation = Observation(
            UnitTestExecutionStatus.Unknown,
            signals,
            UnitTestLifecycleState.Unknown);

        var result = WorkflowStatusViewStateMapper.MapRunControlStatus(Status(observation));

        Assert.Equal(expectedKind, result.Kind);
    }

    [Theory]
    [InlineData(UnitTestExecutionSignals.Uninitialized)]
    [InlineData(UnitTestExecutionSignals.ConfigError)]
    [InlineData(UnitTestExecutionSignals.Running)]
    [InlineData(UnitTestExecutionSignals.Inactive)]
    [InlineData(UnitTestExecutionSignals.Error)]
    public void Ready_badge_rejects_blocking_execution_signals(UnitTestExecutionSignals blockingSignal)
    {
        var observation = Observation(
            UnitTestExecutionStatus.Idle,
            UnitTestExecutionSignals.Ready | blockingSignal,
            UnitTestLifecycleState.Idle);

        var result = WorkflowStatusViewStateMapper.MapRunControlStatus(Status(observation));

        Assert.NotEqual(ProcessStatusKind.Ready, result.Kind);
    }

    [Fact]
    public void Completed_aborted_projection_remains_a_completed_run_control_badge()
    {
        var observation = Observation(
            UnitTestExecutionStatus.Completed,
            UnitTestExecutionSignals.Completed | UnitTestExecutionSignals.Aborted,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Abort);

        var result = WorkflowStatusViewStateMapper.MapRunControlStatus(Status(observation));

        Assert.Equal(ProcessStatusKind.Completed, result.Kind);
        Assert.False(result.IsAnimated);
    }

    [Fact]
    public void Externally_managed_runtime_remains_runtime_prepared_in_later_workflow_states()
    {
        var status = Status(
            Observation(
                UnitTestExecutionStatus.Idle,
                UnitTestExecutionSignals.Ready,
                UnitTestLifecycleState.Idle),
            ProjectOwnershipMode.ExternallyManaged,
            ProjectSetupState.ExternalManagementAccepted);

        var result = WorkflowStatusViewStateMapper.MapPreparationStatus(Publication(status));

        Assert.Equal(ProcessStatusKind.RuntimePrepared, result.PreparationStatus.Kind);
        Assert.Equal("Runtime prepared", result.PreparationStatus.DisplayText);
        Assert.Equal(
            "Runtime is prepared. Project handling is externally managed.",
            result.PreparationPhase);
    }

    [Fact]
    public void Managed_transfer_remains_transferred_in_later_workflow_states()
    {
        var status = Status(
            Observation(
                UnitTestExecutionStatus.Idle,
                UnitTestExecutionSignals.Ready,
                UnitTestLifecycleState.Idle),
            ProjectOwnershipMode.ApplicationManaged,
            ProjectSetupState.ManagedTransferCompleted);

        var result = WorkflowStatusViewStateMapper.MapPreparationStatus(Publication(status));

        Assert.Equal(ProcessStatusKind.Transferred, result.PreparationStatus.Kind);
        Assert.Equal("Transferred", result.PreparationStatus.DisplayText);
        Assert.Equal(
            "Project is transferred to the selected runtime target.",
            result.PreparationPhase);
    }

    [Fact]
    public void Communication_revalidation_projects_transient_badges_and_activity()
    {
        var updatedAt = DateTimeOffset.UtcNow;
        var healthy = AccessHealth.Healthy(updatedAt);
        var revalidating = new AccessHealth(
            AccessHealthState.RevalidationRequired,
            AccessHealthProblem.RevalidationRequired,
            "PlcsimAdvanced.V8.CommunicationRevalidationRequired",
            "UnitTest communication is revalidating.",
            updatedAt);
        var status = Status(
            Observation(
                UnitTestExecutionStatus.Idle,
                UnitTestExecutionSignals.Ready,
                UnitTestLifecycleState.Idle),
            accessHealth: new AccessHealthSnapshot(
                healthy,
                healthy,
                healthy,
                revalidating,
                revalidating));

        var result = WorkflowStatusViewStateMapper.MapWorkflowStatus(Publication(status));

        Assert.Equal(ProcessStatusKind.Revalidating, result.CommunicationStatus.Kind);
        Assert.Equal(ProcessStatusKind.Revalidating, result.MonitoringStatus.Kind);
        Assert.Equal(ProcessStatusKind.Revalidating, result.RunControlStatus.Kind);
        Assert.True(result.CommunicationStatus.IsAnimated);
        Assert.True(result.ActivityIndicatorActive);
        Assert.Equal("Revalidating UnitTest communication...", result.ActiveOperation);
    }

    [Fact]
    public void Lost_events_keep_monitoring_active_and_move_details_to_tooltip()
    {
        var status = Status(
            Observation(
                UnitTestExecutionStatus.Idle,
                UnitTestExecutionSignals.Ready,
                UnitTestLifecycleState.Idle),
            eventStreamState: EventStreamState(lostEventCount: 3));

        var result = WorkflowStatusViewStateMapper.MapWorkflowStatus(Publication(status));

        Assert.Equal(ProcessStatusKind.Warning, result.MonitoringStatus.Kind);
        Assert.Equal("active", result.MonitoringStatus.DisplayText);
        Assert.Contains("3 events were lost", result.MonitoringStatus.ToolTip, StringComparison.Ordinal);
        Assert.Contains("incomplete", result.MonitoringStatus.ToolTip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Full_buffer_without_lost_events_is_a_live_monitoring_warning()
    {
        var status = Status(
            Observation(
                UnitTestExecutionStatus.Idle,
                UnitTestExecutionSignals.Ready,
                UnitTestLifecycleState.Idle),
            eventStreamState: EventStreamState(bufferFull: true));

        var result = WorkflowStatusViewStateMapper.MapWorkflowStatus(Publication(status));

        Assert.Equal(ProcessStatusKind.Warning, result.MonitoringStatus.Kind);
        Assert.Equal("active", result.MonitoringStatus.DisplayText);
        Assert.Contains("EventBuffer is full", result.MonitoringStatus.ToolTip, StringComparison.Ordinal);
    }

    private static UnitTestRunControlObservation Observation(
        UnitTestExecutionStatus executionStatus,
        UnitTestExecutionSignals signals,
        UnitTestLifecycleState state,
        UnitTestResultState result = UnitTestResultState.NotEvaluated)
    {
        return new UnitTestRunControlObservation(executionStatus, signals, state, result);
    }

    private static WorkflowStatusSnapshot Status(
        UnitTestRunControlObservation observation,
        ProjectOwnershipMode projectOwnershipMode = ProjectOwnershipMode.ApplicationManaged,
        ProjectSetupState projectSetupState = ProjectSetupState.ManagedTransferCompleted,
        AccessHealthSnapshot? accessHealth = null,
        EventStreamState? eventStreamState = null)
    {
        var healthy = AccessHealth.Healthy(DateTimeOffset.UtcNow);
        return new WorkflowStatusSnapshot(
            RunWorkflowState.Monitoring,
            ActiveOperation: null,
            OperationInProgress: false,
            HasAccessProfile: true,
            AccessProfileId: "fake",
            AccessProfileVersion: "1.0.0",
            ProjectOwnershipMode: projectOwnershipMode,
            HasRunSetup: true,
            RuntimeProvisioningMode: RuntimeProvisioningMode.UseExisting,
            HasProjectTarget: true,
            HasRuntimeTarget: true,
            ProjectSetupState: projectSetupState,
            ProjectAccessActive: false,
            RuntimePrepared: true,
            PlcState: new PlcState(
                RuntimeInstanceState.Available,
                PlcOperatingState.Running,
                Array.Empty<RuntimeTargetRef>(),
                ReadyForTransfer: true,
                ReadyForExecution: true),
            UnitTestCommunicationPrepared: true,
            UnitTestCommunicationContextActive: true,
            MonitoringActive: true,
            LifecycleObservation: observation,
            EventStreamState: eventStreamState,
            MonitorState: TestRunMonitoringState.WaitingForRun,
            CurrentRun: null,
            AccessHealth: accessHealth ?? new AccessHealthSnapshot(healthy, healthy, healthy, healthy, healthy),
            IsExportable: false);
    }

    private static EventStreamState EventStreamState(
        ushort lostEventCount = 0,
        bool bufferFull = false)
    {
        return new EventStreamState(
            new CommunicationSessionId("test-session"),
            IsMonitoring: true,
            EventBufferReadState.Valid,
            EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty: false,
            LostEventCount: lostEventCount,
            BufferFull: bufferFull);
    }

    private static RunWorkflowPublication Publication(WorkflowStatusSnapshot status)
    {
        return new RunWorkflowPublication(
            Sequence: 1,
            State: status.State,
            ActiveOperation: status.ActiveOperation,
            Status: status,
            CurrentRun: null,
            MonitorState: TestRunMonitoringState.WaitingForRun,
            LifecycleObservation: status.LifecycleObservation,
            EventStreamState: status.EventStreamState,
            ProjectTargets: null,
            RuntimeTargets: null,
            ActionGates: Array.Empty<ActionGateState>(),
            DiagnosticSnapshot: DiagnosticSnapshot.Empty(DateTimeOffset.UtcNow));
    }
}
