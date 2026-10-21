using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using System.Security;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensExternalPlcProjectService : SiemensAccessProfileServiceBase, IPlcProjectService
{
    private const string ProjectHandlingUnavailableMessage =
        "Project handling is unavailable because the project is externally managed.";

    public SiemensExternalPlcProjectService(TimeProvider timeProvider)
        : base(timeProvider)
    {
    }

    public event EventHandler<PlcProjectStateChangedEventArgs>? ProjectStateChangedEventHandler
    {
        add { }
        remove { }
    }

    public PlcProjectContext? CurrentProject => null;

    public Task<OperationResult<ProjectTargetDiscoveryResult>> DiscoverProjectTargetsAsync(
        PlcProjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ProjectHandlingUnavailable<ProjectTargetDiscoveryResult>());
    }

    public Task<OperationResult<PlcProjectContext>> PrepareProjectAsync(
        PlcProjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ProjectHandlingUnavailable<PlcProjectContext>());
    }

    public Task<OperationResult<ProjectTargetResolution>> ResolveProjectTargetAsync(
        PlcProjectContext project,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ProjectHandlingUnavailable<ProjectTargetResolution>());
    }

    public Task<OperationResult<SafetyProgramAuthorizationRequirementResult>> GetSafetyProgramAuthorizationRequirementAsync(
        PlcProjectContext project,
        ProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OperationResult<SafetyProgramAuthorizationRequirementResult>.NotSupported(
            ProjectHandlingUnavailableMessage));
    }

    public Task<OperationResult<SafetyProgramAuthorizationResult>> AuthorizeSafetyProgramAsync(
        PlcProjectContext project,
        ProjectTargetRef target,
        SecureString password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(password);
        return Task.FromResult(OperationResult<SafetyProgramAuthorizationResult>.NotSupported(
            ProjectHandlingUnavailableMessage));
    }

    public Task<OperationResult<PlcBuildResult>> BuildAsync(
        PlcProjectContext project,
        ProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ProjectHandlingUnavailable<PlcBuildResult>());
    }

    public Task<OperationResult<PlcTransferResult>> TransferToAsync(
        PlcProjectContext project,
        ProjectTargetRef projectTarget,
        RuntimeTargetRef runtimeTarget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ProjectHandlingUnavailable<PlcTransferResult>());
    }

    public Task<OperationResult> ReleaseProjectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ProjectHandlingUnavailable());
    }

    public Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string message = "Siemens external project service cleaned up.";
        SetDisposed(message);
        return Task.FromResult(OperationResult.Success(message));
    }

    protected override AccessServiceKind GetServiceKind() => AccessServiceKind.Project;

    private static OperationResult ProjectHandlingUnavailable()
    {
        return OperationResult.Failure(ProjectHandlingUnavailableMessage);
    }

    private static OperationResult<T> ProjectHandlingUnavailable<T>()
    {
        return OperationResult<T>.Failure(ProjectHandlingUnavailableMessage);
    }
}
