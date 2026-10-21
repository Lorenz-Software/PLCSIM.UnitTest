using System.Windows.Media;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.Workflow;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

internal sealed record WorkflowStatusViewState(
    string ActiveOperation,
    bool ActivityIndicatorActive,
    ProcessStatusViewState PlcProcessStatus,
    ProcessStatusViewState CommunicationStatus,
    ProcessStatusViewState MonitoringStatus,
    ProcessStatusViewState RunControlStatus,
    TestRunMonitoringState RunMonitorState,
    string RunStatus);

internal sealed record WorkflowPreparationViewState(
    ProcessStatusViewState PreparationStatus,
    string PreparationPhase);

internal static class WorkflowStatusViewStateMapper
{
    private static readonly ProcessStatusViewState NotPreparedProcessStatusValue = new(
        ProcessStatusKind.NotPrepared,
        "Not prepared",
        Brushes.Gray);

    private static readonly ProcessStatusViewState NotReadyProcessStatusValue = new(
        ProcessStatusKind.NotReady,
        "not ready",
        Brushes.Gray);

    private static readonly ProcessStatusViewState DisconnectedProcessStatusValue = new(
        ProcessStatusKind.Disconnected,
        "disconnected",
        Brushes.Gray);

    private static readonly ProcessStatusViewState OffProcessStatusValue = new(
        ProcessStatusKind.Off,
        "off",
        Brushes.Gray);

    public static ProcessStatusViewState NotPreparedProcessStatus => NotPreparedProcessStatusValue;

    public static ProcessStatusViewState NotReadyProcessStatus => NotReadyProcessStatusValue;

    public static ProcessStatusViewState DisconnectedProcessStatus => DisconnectedProcessStatusValue;

    public static ProcessStatusViewState OffProcessStatus => OffProcessStatusValue;

    public static WorkflowStatusViewState MapWorkflowStatus(RunWorkflowPublication publication)
    {
        return new WorkflowStatusViewState(
            CreateActivityText(publication.Status),
            IsActivityActive(publication.Status),
            CreatePlcProcessStatus(publication.Status),
            CreateCommunicationStatus(publication.Status),
            CreateMonitoringStatus(publication.Status),
            MapRunControlStatus(publication.Status),
            publication.Status.MonitorState,
            CreateRunStatus(publication.Status.MonitorState));
    }

    public static WorkflowPreparationViewState MapPreparationStatus(RunWorkflowPublication publication)
    {
        return new WorkflowPreparationViewState(
            CreatePreparationStatus(publication.Status),
            CreatePreparationPhase(publication));
    }

    public static bool IsPreparationAction(WorkflowAction? action)
    {
        return action is WorkflowAction.PrepareProject
            or WorkflowAction.PrepareRuntime
            or WorkflowAction.DiscoverSafetyProgramAuthorizationRequirement
            or WorkflowAction.AuthorizeSafetyProgram
            or WorkflowAction.TransferProject;
    }

    private static ProcessStatusViewState ProcessStatus(
        ProcessStatusKind kind,
        string displayText,
        Brush markerBrush,
        bool isAnimated = false,
        string? toolTip = null)
    {
        return new ProcessStatusViewState(kind, displayText, markerBrush, isAnimated, toolTip);
    }

    private static ProcessStatusViewState CreatePlcProcessStatus(WorkflowStatusSnapshot status)
    {
        if (status.PlcState.InstanceState == RuntimeInstanceState.Faulted)
        {
            return ProcessStatus(ProcessStatusKind.Error, "error", Brushes.Firebrick);
        }

        if (status.PlcState.InstanceState == RuntimeInstanceState.Starting)
        {
            return ProcessStatus(
                ProcessStatusKind.Starting,
                "runtime starting",
                Brushes.DodgerBlue,
                isAnimated: true);
        }

        if (status.PlcState.InstanceState == RuntimeInstanceState.Stopping)
        {
            return ProcessStatus(
                ProcessStatusKind.Stopping,
                "runtime stopping",
                Brushes.DodgerBlue,
                isAnimated: true);
        }

        if (status.PlcState.InstanceState != RuntimeInstanceState.Available)
        {
            return NotReadyProcessStatus;
        }

        return status.PlcState.OperatingState switch
        {
            PlcOperatingState.Starting => ProcessStatus(
                ProcessStatusKind.Starting,
                "starting",
                Brushes.DodgerBlue,
                isAnimated: true),
            PlcOperatingState.Running => ProcessStatus(
                ProcessStatusKind.Running,
                "running",
                Brushes.ForestGreen),
            PlcOperatingState.Stopping => ProcessStatus(
                ProcessStatusKind.Stopping,
                "stopping",
                Brushes.DodgerBlue,
                isAnimated: true),
            PlcOperatingState.Stopped => ProcessStatus(
                ProcessStatusKind.Stopped,
                "stopped",
                Brushes.Goldenrod),
            PlcOperatingState.Frozen => ProcessStatus(
                ProcessStatusKind.Frozen,
                "frozen",
                Brushes.Goldenrod),
            PlcOperatingState.Held => ProcessStatus(
                ProcessStatusKind.Held,
                "held",
                Brushes.Goldenrod),
            _ => NotReadyProcessStatus
        };
    }

    private static ProcessStatusViewState CreateCommunicationStatus(WorkflowStatusSnapshot status)
    {
        if (status.ActiveOperation?.Action == WorkflowAction.PrepareUnitTestCommunication)
        {
            return ProcessStatus(ProcessStatusKind.Starting, "connecting", Brushes.DodgerBlue, isAnimated: true);
        }

        if (IsCommunicationRevalidationRequired(status))
        {
            return CreateRevalidatingStatus();
        }

        return status.UnitTestCommunicationPrepared
            || status.UnitTestCommunicationContextActive
            ? ProcessStatus(ProcessStatusKind.Connected, "connected", Brushes.ForestGreen)
            : DisconnectedProcessStatus;
    }

    private static ProcessStatusViewState CreateMonitoringStatus(WorkflowStatusSnapshot status)
    {
        if (status.ActiveOperation?.Action == WorkflowAction.StartMonitoring)
        {
            return ProcessStatus(ProcessStatusKind.Starting, "starting", Brushes.DodgerBlue, isAnimated: true);
        }

        if (IsCommunicationRevalidationRequired(status))
        {
            return CreateRevalidatingStatus();
        }

        if (status.MonitoringActive && status.EventStreamState?.LostEventCount > 0)
        {
            var count = status.EventStreamState.LostEventCount;
            return ProcessStatus(
                ProcessStatusKind.Warning,
                "active",
                Brushes.Goldenrod,
                toolTip: $"Event monitoring is active, but {count} event{(count == 1 ? " was" : "s were")} lost. The current run evidence is incomplete.");
        }

        if (status.MonitoringActive && status.EventStreamState?.BufferFull == true)
        {
            return ProcessStatus(
                ProcessStatusKind.Warning,
                "active",
                Brushes.Goldenrod,
                toolTip: "Event monitoring is active, but the PLC EventBuffer is full. No lost events have been reported yet.");
        }

        if (status.MonitoringActive
            && status.EventStreamState?.LastReadState is EventBufferReadState.RetryRequired
                or EventBufferReadState.Inconsistent)
        {
            return ProcessStatus(
                ProcessStatusKind.Warning,
                "active",
                Brushes.Goldenrod,
                toolTip: "Event monitoring is active and recovering from a temporary EventBuffer read condition.");
        }

        return status.MonitoringActive
            ? ProcessStatus(
                ProcessStatusKind.Active,
                "active",
                Brushes.ForestGreen,
                toolTip: "UnitTest event-stream monitoring is active.")
            : OffProcessStatus;
    }

    internal static ProcessStatusViewState MapRunControlStatus(WorkflowStatusSnapshot status)
    {
        if (status.ActiveOperation?.Action == WorkflowAction.StartUnitTest)
        {
            return ProcessStatus(ProcessStatusKind.Starting, "starting", Brushes.ForestGreen, isAnimated: true);
        }

        if (status.ActiveOperation?.Action == WorkflowAction.AbortUnitTest)
        {
            return ProcessStatus(ProcessStatusKind.Aborting, "aborting", Brushes.Goldenrod, isAnimated: true);
        }

        if (status.ActiveOperation?.Action == WorkflowAction.ResetUnitTest)
        {
            return ProcessStatus(ProcessStatusKind.Resetting, "resetting", Brushes.Goldenrod, isAnimated: true);
        }

        if (IsCommunicationRevalidationRequired(status))
        {
            return CreateRevalidatingStatus();
        }

        if (status.LifecycleObservation is not { } observation)
        {
            return status.UnitTestCommunicationPrepared
                ? NotReadyProcessStatus
                : DisconnectedProcessStatus;
        }

        if (observation.ExecutionStatus == UnitTestExecutionStatus.Error
            || observation.ConfigError
            || observation.Error)
        {
            return ProcessStatus(ProcessStatusKind.Error, "error", Brushes.Firebrick);
        }

        return observation.ExecutionStatus switch
        {
            UnitTestExecutionStatus.Reset => ProcessStatus(
                ProcessStatusKind.Resetting,
                "resetting",
                Brushes.Goldenrod,
                isAnimated: true),
            UnitTestExecutionStatus.Aborting => ProcessStatus(
                ProcessStatusKind.Aborting,
                "aborting",
                Brushes.Goldenrod,
                isAnimated: true),
            UnitTestExecutionStatus.Completed or UnitTestExecutionStatus.Aborted => ProcessStatus(
                ProcessStatusKind.Completed,
                "completed",
                Brushes.ForestGreen),
            UnitTestExecutionStatus.Starting => ProcessStatus(
                ProcessStatusKind.Starting,
                "starting",
                Brushes.ForestGreen,
                isAnimated: true),
            UnitTestExecutionStatus.Running or UnitTestExecutionStatus.Completing => ProcessStatus(
                ProcessStatusKind.Running,
                "running",
                Brushes.ForestGreen,
                isAnimated: true),
            UnitTestExecutionStatus.Idle when IsReady(observation) => ProcessStatus(
                ProcessStatusKind.Ready,
                "ready",
                Brushes.DodgerBlue),
            _ when observation.Completed || observation.Aborted => ProcessStatus(
                ProcessStatusKind.Completed,
                "completed",
                Brushes.ForestGreen),
            _ when observation.Running => ProcessStatus(
                ProcessStatusKind.Running,
                "running",
                Brushes.ForestGreen,
                isAnimated: true),
            _ => NotReadyProcessStatus
        };
    }

    private static bool IsReady(UnitTestRunControlObservation observation)
    {
        return observation.Ready
            && !observation.Uninitialized
            && !observation.ConfigError
            && !observation.Running
            && !observation.Inactive
            && !observation.Error;
    }

    private static string CreateRunStatus(TestRunMonitoringState monitorState)
    {
        return monitorState switch
        {
            TestRunMonitoringState.RunActive => "running",
            TestRunMonitoringState.CompletionPending => "finalizing",
            TestRunMonitoringState.Completed => "completed",
            TestRunMonitoringState.Aborted => "aborted",
            TestRunMonitoringState.TimedOut => "timeout",
            TestRunMonitoringState.Invalidated => "invalidated",
            _ => "idle"
        };
    }

    private static string CreateActivityText(WorkflowStatusSnapshot status)
    {
        if (status.ActiveOperation is { } operation)
        {
            return operation.Action switch
            {
                WorkflowAction.LoadAccessProfile => "Loading profile...",
                WorkflowAction.PrepareProject => "Preparing project...",
                WorkflowAction.PrepareRuntime => "Preparing runtime...",
                WorkflowAction.DiscoverSafetyProgramAuthorizationRequirement => "Checking Safety authorization...",
                WorkflowAction.AuthorizeSafetyProgram => "Authorizing Safety program...",
                WorkflowAction.TransferProject => "Transferring project...",
                WorkflowAction.StartPlc => "Starting PLC...",
                WorkflowAction.StopPlc => "Stopping PLC...",
                WorkflowAction.PrepareUnitTestCommunication => "Preparing UnitTest communication...",
                WorkflowAction.StartMonitoring => "Starting monitor...",
                WorkflowAction.StartUnitTest => "Starting test...",
                WorkflowAction.AbortUnitTest => "Aborting test...",
                WorkflowAction.ResetUnitTest => "Resetting test...",
                WorkflowAction.ResetEventBuffer => "Resetting EventBuffer...",
                WorkflowAction.Cleanup => "Ending session...",
                _ => $"{operation.Action}..."
            };
        }

        if (IsCommunicationRevalidationRequired(status))
        {
            return "Revalidating UnitTest communication...";
        }

        return status.MonitorState switch
        {
            TestRunMonitoringState.RunActive => "Running test...",
            TestRunMonitoringState.CompletionPending => "Finalizing test...",
            _ => "Idle"
        };
    }

    private static bool IsActivityActive(WorkflowStatusSnapshot status)
    {
        return status.ActiveOperation is not null
            || IsCommunicationRevalidationRequired(status)
            || status.MonitorState is TestRunMonitoringState.RunActive
                or TestRunMonitoringState.CompletionPending;
    }

    internal static bool IsCommunicationRevalidationRequired(WorkflowStatusSnapshot status)
    {
        return status.AccessHealth.UnitTestLifecycle.State == AccessHealthState.RevalidationRequired
            || status.AccessHealth.UnitTestEventStream.State == AccessHealthState.RevalidationRequired;
    }

    private static ProcessStatusViewState CreateRevalidatingStatus()
    {
        return ProcessStatus(
            ProcessStatusKind.Revalidating,
            "revalidating",
            Brushes.Goldenrod,
            isAnimated: true);
    }

    private static ProcessStatusViewState CreatePreparationStatus(WorkflowStatusSnapshot status)
    {
        if (status.State == RunWorkflowState.Error)
        {
            return ProcessStatus(ProcessStatusKind.Failed, "Failed", Brushes.Firebrick);
        }

        if (IsPreparationAction(status.ActiveOperation?.Action))
        {
            return ProcessStatus(ProcessStatusKind.Preparing, "Preparing", Brushes.Goldenrod, isAnimated: true);
        }

        if (status.ProjectSetupState == ProjectSetupState.ManagedTransferCompleted
            && HasUsablePreparedRuntime(status))
        {
            return ProcessStatus(
                ProcessStatusKind.Transferred,
                "Transferred",
                Brushes.ForestGreen);
        }

        if (HasUsablePreparedRuntime(status))
        {
            return ProcessStatus(
                ProcessStatusKind.RuntimePrepared,
                "Runtime prepared",
                Brushes.DodgerBlue);
        }

        if (status.ProjectSetupState == ProjectSetupState.ManagedProjectPrepared)
        {
            return ProcessStatus(
                ProcessStatusKind.Ready,
                "Project prepared",
                Brushes.DodgerBlue);
        }

        return status.HasRunSetup
            ? ProcessStatus(
                ProcessStatusKind.Configured,
                "Configured",
                Brushes.Gray)
            : NotPreparedProcessStatus;
    }

    private static string CreatePreparationPhase(RunWorkflowPublication publication)
    {
        if (publication.ActiveOperation is { } operation && IsPreparationAction(operation.Action))
        {
            var progressText = operation.Progress is null
                ? string.Empty
                : operation.Progress.HasPercent
                    ? $" - {operation.Progress.Message} ({operation.Progress.Percent:0.#}%)"
                    : $" - {operation.Progress.Message}";
            return $"{operation.Action} / {operation.Phase}{progressText}";
        }

        if (publication.State == RunWorkflowState.Error)
        {
            return "Preparation/runtime operation failed. Check diagnostics.";
        }

        if (publication.Status.ProjectSetupState == ProjectSetupState.ManagedTransferCompleted
            && HasUsablePreparedRuntime(publication.Status))
        {
            return "Project is transferred to the selected runtime target.";
        }

        if (HasUsablePreparedRuntime(publication.Status))
        {
            return publication.Status.ProjectOwnershipMode == ProjectOwnershipMode.ExternallyManaged
                ? "Runtime is prepared. Project handling is externally managed."
                : "Runtime is prepared. Project transfer is pending.";
        }

        if (publication.Status.ProjectSetupState == ProjectSetupState.ManagedProjectPrepared)
        {
            return "Project preparation completed.";
        }

        return publication.Status.HasRunSetup
            ? "Final setup is configured. Preparation has not started."
            : "No preparation operation running.";
    }

    private static bool HasUsablePreparedRuntime(WorkflowStatusSnapshot status)
    {
        return status.RuntimePrepared
            && status.PlcState.InstanceState is not RuntimeInstanceState.Unavailable
                and not RuntimeInstanceState.Faulted;
    }

}
