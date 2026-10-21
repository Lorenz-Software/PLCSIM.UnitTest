using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.UnitTests;
using System.Security;

namespace PLCSIM.UnitTest.Common.Services;

/// <summary>
/// Common base contract for functional services exposed by an access profile.
/// </summary>
public interface IAccessProfileService
{
    /// <summary>
    /// Publishes service-owned diagnostics.
    /// </summary>
    event EventHandler<DiagnosticEventArgs> DiagnosticEventHandler;

    /// <summary>
    /// Publishes service state changes.
    /// </summary>
    event EventHandler<ServiceStateChangedEventArgs> StateChangedEventHandler;

    /// <summary>
    /// Publishes operation progress for long-running service operations.
    /// </summary>
    event EventHandler<ProgressChangedEventArgs> ProgressChangedEventHandler;

    /// <summary>
    /// Gets the current coarse service state.
    /// </summary>
    ServiceState State { get; }

    /// <summary>
    /// Gets the current application-facing health of this functional access service.
    /// </summary>
    AccessHealth Health { get; }
}

/// <summary>
/// Provides application-oriented PLC project preparation, build, and transfer operations.
/// </summary>
public interface IPlcProjectService : IAccessProfileService
{
    event EventHandler<PlcProjectStateChangedEventArgs> ProjectStateChangedEventHandler;

    PlcProjectContext? CurrentProject { get; }

    Task<OperationResult<ProjectTargetDiscoveryResult>> DiscoverProjectTargetsAsync(
        PlcProjectRequest request,
        CancellationToken cancellationToken);

    Task<OperationResult<PlcProjectContext>> PrepareProjectAsync(
        PlcProjectRequest request,
        CancellationToken cancellationToken);

    Task<OperationResult<ProjectTargetResolution>> ResolveProjectTargetAsync(
        PlcProjectContext project,
        CancellationToken cancellationToken);

    Task<OperationResult<SafetyProgramAuthorizationRequirementResult>> GetSafetyProgramAuthorizationRequirementAsync(
        PlcProjectContext project,
        ProjectTargetRef target,
        CancellationToken cancellationToken);

    Task<OperationResult<SafetyProgramAuthorizationResult>> AuthorizeSafetyProgramAsync(
        PlcProjectContext project,
        ProjectTargetRef target,
        SecureString password,
        CancellationToken cancellationToken);

    Task<OperationResult<PlcBuildResult>> BuildAsync(
        PlcProjectContext project,
        ProjectTargetRef target,
        CancellationToken cancellationToken);

    Task<OperationResult<PlcTransferResult>> TransferToAsync(
        PlcProjectContext project,
        ProjectTargetRef projectTarget,
        RuntimeTargetRef runtimeTarget,
        CancellationToken cancellationToken);

    Task<OperationResult> ReleaseProjectAsync(CancellationToken cancellationToken);

    Task<OperationResult> CleanupAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Provides application-oriented PLC runtime provisioning and control operations.
/// </summary>
public interface IPlcControlService : IAccessProfileService
{
    event EventHandler<PlcStateChangedEventArgs> PlcStateChangedEventHandler;

    PlcState CurrentState { get; }

    Task<OperationResult<RuntimeTargetDiscoveryResult>> DiscoverRuntimeTargetsAsync(
        PlcRuntimeDiscoveryRequest request,
        CancellationToken cancellationToken);

    Task<OperationResult<PreparedPlcRuntime>> PrepareRuntimeAsync(
        PlcRuntimePrepareRequest request,
        CancellationToken cancellationToken);

    Task<OperationResult<RuntimeTargetResolution>> ResolveRuntimeTargetAsync(
        PlcRuntimeContext runtime,
        CancellationToken cancellationToken);

    Task<OperationResult<PlcState>> StartAsync(
        PlcRuntimeContext runtime,
        RuntimeTargetRef target,
        CancellationToken cancellationToken);

    Task<OperationResult<PlcState>> StopAsync(
        PlcRuntimeContext runtime,
        RuntimeTargetRef target,
        CancellationToken cancellationToken);

    Task<OperationResult<PlcRuntimeReleaseOutcome>> ReleaseRuntimeAsync(
        PlcRuntimeReleaseRequest request,
        CancellationToken cancellationToken);

    Task<OperationResult> CleanupAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Provides UnitTest RunControl lifecycle commands and observations.
/// </summary>
public interface IUnitTestLifecycleService : IAccessProfileService
{
    event EventHandler<UnitTestRunControlObservationChangedEventArgs> RunControlObservationChanged;

    UnitTestRunControlObservation CurrentObservation { get; }

    Task<OperationResult<TestRunCommandResult>> StartAsync(
        UnitTestContext context,
        CancellationToken cancellationToken);

    Task<OperationResult<TestRunCommandResult>> AbortAsync(
        UnitTestContext context,
        CancellationToken cancellationToken);

    Task<OperationResult<TestRunCommandResult>> ResetAsync(
        UnitTestContext context,
        CancellationToken cancellationToken);

    Task<OperationResult> ReleaseCommunicationAsync(CancellationToken cancellationToken);

    Task<OperationResult> CleanupAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Provides UnitTest event-stream monitoring and batch delivery.
/// </summary>
public interface IUnitTestEventStreamService : IAccessProfileService
{
    event EventHandler<EventBatchReceivedEventArgs> EventBatchReceivedEventHandler;

    event EventHandler<EventStreamStateChangedEventArgs> MonitoringStateChangedEventHandler;

    EventStreamState CurrentMonitoringState { get; }

    Task<OperationResult<EventStreamState>> StartMonitoringAsync(
        UnitTestContext context,
        CancellationToken cancellationToken);

    Task<OperationResult<EventBufferResetCommandResult>> ResetEventBufferAsync(
        UnitTestContext context,
        CancellationToken cancellationToken);

    Task<OperationResult> StopMonitoringAsync(CancellationToken cancellationToken);

    Task<OperationResult> ReleaseCommunicationAsync(CancellationToken cancellationToken);

    Task<OperationResult> CleanupAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Event arguments for functional service state changes.
/// </summary>
public sealed record ServiceStateChangedEventArgs(
    ServiceState State,
    string Reason);

/// <summary>
/// Event arguments for functional service progress changes.
/// </summary>
public sealed record ProgressChangedEventArgs(
    OperationProgress Progress);

/// <summary>
/// Coarse lifecycle state of a functional service.
/// </summary>
public enum ServiceState
{
    NotPrepared,
    Ready,
    Busy,
    NeedsRevalidation,
    Invalidated,
    CleanupRequired,
    Disposed
}
