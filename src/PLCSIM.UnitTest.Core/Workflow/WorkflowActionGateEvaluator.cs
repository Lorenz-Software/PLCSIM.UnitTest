using System;
using System.Collections.Generic;
using System.Linq;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Core.Workflow;

public sealed class WorkflowActionGateEvaluator
{
    public IReadOnlyList<ActionGateState> EvaluateAll(WorkflowStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return Enum.GetValues<WorkflowAction>()
            .Select(action => Evaluate(status, action))
            .ToArray();
    }

    public ActionGateState Evaluate(WorkflowStatusSnapshot status, WorkflowAction action)
    {
        ArgumentNullException.ThrowIfNull(status);

        var reasons = new List<ActionGateReason>();

        if (status.OperationInProgress)
        {
            reasons.Add(new ActionGateReason(ActionGateReasonCode.OperationInProgress, "Another workflow operation is already running."));
            return new ActionGateState(action, ActionGateAvailability.Pending, reasons);
        }

        if (status.ProjectOwnershipMode == ProjectOwnershipMode.ExternallyManaged
            && IsManagedProjectAction(action))
        {
            return new ActionGateState(action, ActionGateAvailability.NotApplicable, reasons);
        }

        switch (action)
        {
            case WorkflowAction.SelectConfiguration:
            case WorkflowAction.LoadAccessProfile:
            case WorkflowAction.Cleanup:
                break;

            case WorkflowAction.UnloadAccessProfile:
                RequireAccessProfile(status, reasons);
                break;

            case WorkflowAction.DiscoverProjectTargets:
                RequireAccessProfile(status, reasons);
                RequireHealthy(status.AccessHealth.Profile, ActionGateReasonCode.AccessProfileUnhealthy, "Access profile health is not usable.", reasons);
                break;

            case WorkflowAction.DiscoverRuntimeTargets:
                RequireAccessProfile(status, reasons);
                RequireHealthy(status.AccessHealth.Profile, ActionGateReasonCode.AccessProfileUnhealthy, "Access profile health is not usable.", reasons);
                break;

            case WorkflowAction.PrepareProject:
                RequireConfigured(status, reasons);
                RequireAccessProfile(status, reasons);
                RequireHealthy(status.AccessHealth.Profile, ActionGateReasonCode.AccessProfileUnhealthy, "Access profile health is not usable.", reasons);
                Require(status.HasProjectTarget, ActionGateReasonCode.ProjectTargetNotSelected, "Project target is not selected.", reasons);
                break;

            case WorkflowAction.PrepareRuntime:
                RequireConfigured(status, reasons);
                RequireAccessProfile(status, reasons);
                RequireHealthy(status.AccessHealth.Profile, ActionGateReasonCode.AccessProfileUnhealthy, "Access profile health is not usable.", reasons);
                if (status.RuntimeProvisioningMode != RuntimeProvisioningMode.CreateOwned)
                {
                    Require(status.HasRuntimeTarget, ActionGateReasonCode.RuntimeTargetNotSelected, "Runtime target is not selected.", reasons);
                }

                break;

            case WorkflowAction.ReconnectRuntime:
                RequireConfigured(status, reasons);
                RequireAccessProfile(status, reasons);
                RequireHealthy(status.AccessHealth.Profile, ActionGateReasonCode.AccessProfileUnhealthy, "Access profile health is not usable.", reasons);
                Require(status.RuntimePrepared, ActionGateReasonCode.RuntimeNotPrepared, "Runtime is not prepared.", reasons);
                Require(status.HasRuntimeTarget, ActionGateReasonCode.RuntimeTargetNotSelected, "Runtime target is not selected.", reasons);
                break;

            case WorkflowAction.DiscoverSafetyProgramAuthorizationRequirement:
            case WorkflowAction.AuthorizeSafetyProgram:
                RequireAccessProfile(status, reasons);
                RequireHealthy(status.AccessHealth.Profile, ActionGateReasonCode.AccessProfileUnhealthy, "Access profile health is not usable.", reasons);
                RequireHealthy(status.AccessHealth.Project, ActionGateReasonCode.ProjectAccessUnhealthy, "Project access health is not usable.", reasons);
                Require(status.ProjectPrepared, ActionGateReasonCode.ProjectNotPrepared, "Project is not prepared.", reasons);
                Require(status.HasProjectTarget, ActionGateReasonCode.ProjectTargetNotSelected, "Project target is not selected.", reasons);
                break;

            case WorkflowAction.TransferProject:
                RequireAccessProfile(status, reasons);
                RequireHealthy(status.AccessHealth.Profile, ActionGateReasonCode.AccessProfileUnhealthy, "Access profile health is not usable.", reasons);
                RequireHealthy(status.AccessHealth.Project, ActionGateReasonCode.ProjectAccessUnhealthy, "Project access health is not usable.", reasons);
                RequireHealthy(status.AccessHealth.PlcControl, ActionGateReasonCode.PlcControlAccessUnhealthy, "PLC control access health is not usable.", reasons);
                Require(status.ProjectPrepared, ActionGateReasonCode.ProjectNotPrepared, "Project is not prepared.", reasons);
                Require(status.RuntimePrepared, ActionGateReasonCode.RuntimeNotPrepared, "Runtime is not prepared.", reasons);
                Require(status.HasProjectTarget, ActionGateReasonCode.ProjectTargetNotSelected, "Project target is not selected.", reasons);
                Require(status.HasRuntimeTarget, ActionGateReasonCode.RuntimeTargetNotSelected, "Runtime target is not selected.", reasons);
                break;

            case WorkflowAction.StartPlc:
            case WorkflowAction.StopPlc:
            case WorkflowAction.PrepareUnitTestCommunication:
                RequireAccessProfile(status, reasons);
                RequireHealthy(status.AccessHealth.Profile, ActionGateReasonCode.AccessProfileUnhealthy, "Access profile health is not usable.", reasons);
                RequireHealthy(status.AccessHealth.PlcControl, ActionGateReasonCode.PlcControlAccessUnhealthy, "PLC control access health is not usable.", reasons);
                Require(status.ProjectSetupSatisfied, ActionGateReasonCode.ProjectSetupNotSatisfied, "Project setup is not satisfied.", reasons);
                Require(status.RuntimePrepared, ActionGateReasonCode.RuntimeNotPrepared, "Runtime is not prepared.", reasons);
                Require(status.HasRuntimeTarget, ActionGateReasonCode.RuntimeTargetNotSelected, "Runtime target is not selected.", reasons);
                Require(
                    status.PlcState.InstanceState == RuntimeInstanceState.Available,
                    ActionGateReasonCode.RuntimeInstanceNotAvailable,
                    "Runtime instance is not available.",
                    reasons);
                if (action == WorkflowAction.StartPlc)
                {
                    Require(
                        status.PlcState.OperatingState == PlcOperatingState.Stopped,
                        ActionGateReasonCode.PlcNotStopped,
                        "PLC is not stopped.",
                        reasons);
                }

                if (action == WorkflowAction.StopPlc)
                {
                    Require(
                        status.PlcState.OperatingState is PlcOperatingState.Starting
                            or PlcOperatingState.Running
                            or PlcOperatingState.Frozen
                            or PlcOperatingState.Held,
                        ActionGateReasonCode.PlcNotRunning,
                        "PLC is not in an operating state that can be stopped.",
                        reasons);
                }

                if (action == WorkflowAction.PrepareUnitTestCommunication)
                {
                    Require(status.PlcState.ReadyForExecution, ActionGateReasonCode.PlcNotRunning, "PLC is not ready for UnitTest execution.", reasons);
                }

                break;

            case WorkflowAction.StartMonitoring:
                RequireUnitTestCommunication(status, reasons);
                break;

            case WorkflowAction.StopMonitoring:
                RequireAccessProfile(status, reasons);
                RequireHealthy(status.AccessHealth.Profile, ActionGateReasonCode.AccessProfileUnhealthy, "Access profile health is not usable.", reasons);
                RequireHealthy(status.AccessHealth.UnitTestEventStream, ActionGateReasonCode.UnitTestEventStreamAccessUnhealthy, "UnitTest event-stream access health is not usable.", reasons);
                Require(status.MonitoringActive, ActionGateReasonCode.EventStreamNotMonitoring, "Event stream monitoring is not active.", reasons);
                break;

            case WorkflowAction.StartUnitTest:
                RequireUnitTestCommunication(status, reasons);
                Require(IsRunControlReadyForStart(status), ActionGateReasonCode.UnitTestNotReady, "Run Control is not ready for a UnitTest start.", reasons);
                Require(status.EventStreamState?.LostEventCount == 0, ActionGateReasonCode.EventBufferHasLostEvents, "EventBuffer contains lost-event evidence and must be reset before a new UnitTest start.", reasons);
                break;

            case WorkflowAction.AbortUnitTest:
                RequireUnitTestCommunication(status, reasons);
                Require(IsRunControlAbortable(status), ActionGateReasonCode.NoRunAvailable, "Run Control is not abortable.", reasons);
                break;

            case WorkflowAction.ResetUnitTest:
                RequireUnitTestCommunication(status, reasons);
                break;

            case WorkflowAction.ResetEventBuffer:
                RequireUnitTestCommunication(status, reasons);
                break;

            case WorkflowAction.ReleaseUnitTestCommunication:
                RequireAccessProfile(status, reasons);
                break;

            case WorkflowAction.ReleaseRuntime:
                RequireAccessProfile(status, reasons);
                Require(!status.UnitTestCommunicationContextActive, ActionGateReasonCode.UnitTestCommunicationStillPrepared, "UnitTest communication must be released before runtime is released.", reasons);
                break;

            case WorkflowAction.ReleaseProject:
                RequireAccessProfile(status, reasons);
                Require(!status.RuntimePrepared, ActionGateReasonCode.RuntimeStillPrepared, "Runtime must be released before project is released.", reasons);
                break;

        }

        return new ActionGateState(action, DetermineAvailability(action, reasons), reasons);
    }

    private static void RequireUnitTestCommunication(
        WorkflowStatusSnapshot status,
        List<ActionGateReason> reasons)
    {
        RequireAccessProfile(status, reasons);
        RequireHealthy(status.AccessHealth.Profile, ActionGateReasonCode.AccessProfileUnhealthy, "Access profile health is not usable.", reasons);
        RequireHealthy(status.AccessHealth.PlcControl, ActionGateReasonCode.PlcControlAccessUnhealthy, "PLC control access health is not usable.", reasons);
        RequireHealthy(status.AccessHealth.UnitTestLifecycle, ActionGateReasonCode.UnitTestLifecycleAccessUnhealthy, "UnitTest lifecycle access health is not usable.", reasons);
        RequireHealthy(status.AccessHealth.UnitTestEventStream, ActionGateReasonCode.UnitTestEventStreamAccessUnhealthy, "UnitTest event-stream access health is not usable.", reasons);
        Require(status.ProjectSetupSatisfied, ActionGateReasonCode.ProjectSetupNotSatisfied, "Project setup is not satisfied.", reasons);
        Require(status.RuntimePrepared, ActionGateReasonCode.RuntimeNotPrepared, "Runtime is not prepared.", reasons);
        Require(status.PlcState.ReadyForExecution, ActionGateReasonCode.PlcNotRunning, "PLC is not ready for UnitTest execution.", reasons);
        Require(status.HasRuntimeTarget, ActionGateReasonCode.RuntimeTargetNotSelected, "Runtime target is not selected.", reasons);
        Require(status.UnitTestCommunicationPrepared, ActionGateReasonCode.UnitTestCommunicationNotPrepared, "UnitTest communication is not prepared.", reasons);
    }

    private static bool IsManagedProjectAction(WorkflowAction action)
    {
        return action is WorkflowAction.DiscoverProjectTargets
            or WorkflowAction.PrepareProject
            or WorkflowAction.DiscoverSafetyProgramAuthorizationRequirement
            or WorkflowAction.AuthorizeSafetyProgram
            or WorkflowAction.TransferProject
            or WorkflowAction.ReleaseProject;
    }

    private static void RequireConfigured(
        WorkflowStatusSnapshot status,
        List<ActionGateReason> reasons)
    {
        Require(status.HasRunSetup, ActionGateReasonCode.NotConfigured, "Run setup is not configured.", reasons);
    }

    private static void RequireAccessProfile(
        WorkflowStatusSnapshot status,
        List<ActionGateReason> reasons)
    {
        Require(status.HasAccessProfile, ActionGateReasonCode.AccessProfileNotLoaded, "Access profile is not loaded.", reasons);
    }

    private static void RequireHealthy(
        AccessHealth health,
        ActionGateReasonCode code,
        string message,
        List<ActionGateReason> reasons)
    {
        if (health.State is AccessHealthState.NotEstablished or AccessHealthState.Healthy)
        {
            return;
        }

        var reasonCode = health.State == AccessHealthState.RevalidationRequired
            ? ActionGateReasonCode.AccessRevalidationRequired
            : code;
        reasons.Add(new ActionGateReason(reasonCode, $"{message} {health.Message}"));
    }

    private static bool IsRunControlReadyForStart(WorkflowStatusSnapshot status)
    {
        return status.LifecycleObservation?.Ready == true
            && status.LifecycleObservation.ExecutionStatus == UnitTestExecutionStatus.Idle
            && status.LifecycleObservation.Uninitialized == false
            && status.LifecycleObservation.ConfigError == false
            && status.LifecycleObservation.Inactive == false
            && status.LifecycleObservation.Running == false
            && status.LifecycleObservation.Error == false;
    }

    private static bool IsRunControlAbortable(WorkflowStatusSnapshot status)
    {
        return status.LifecycleObservation?.ExecutionStatus == UnitTestExecutionStatus.Running
            && status.LifecycleObservation.Running
            && !status.LifecycleObservation.Error;
    }

    private static ActionGateAvailability DetermineAvailability(
        WorkflowAction action,
        IReadOnlyList<ActionGateReason> reasons)
    {
        if (reasons.Count == 0)
        {
            return ActionGateAvailability.Available;
        }

        if (reasons.Any(reason => reason.Code == ActionGateReasonCode.OperationInProgress))
        {
            return ActionGateAvailability.Pending;
        }

        return ActionGateAvailability.Blocked;
    }

    private static void Require(
        bool condition,
        ActionGateReasonCode code,
        string message,
        List<ActionGateReason> reasons)
    {
        if (!condition)
        {
            reasons.Add(new ActionGateReason(code, message));
        }
    }
}
