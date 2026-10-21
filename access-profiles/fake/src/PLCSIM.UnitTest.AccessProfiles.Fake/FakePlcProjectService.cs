using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using System.Security;

namespace PLCSIM.UnitTest.AccessProfiles.Fake;

/// <summary>
/// Deterministic fake PLC project service for shared-core tests.
/// </summary>
public sealed class FakePlcProjectService : FakeAccessProfileServiceBase, IPlcProjectService
{
    private readonly TimeProvider timeProvider;

    public FakePlcProjectService(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
    }

    public PlcProjectContext? CurrentProject { get; private set; }

    public ProjectTargetRef DefaultProjectTarget { get; set; } = new(
        new ProjectTargetId("fake-project-target"),
        "Fake PLC Project Target",
        "FakeProjectTarget");

    public int DiscoverProjectTargetsCallCount { get; private set; }

    public PlcProjectRequest? LastDiscoverProjectTargetsRequest { get; private set; }

    public int PrepareProjectCallCount { get; private set; }

    public PlcProjectRequest? LastPrepareProjectRequest { get; private set; }

    public OperationResult<PlcProjectContext>? PrepareProjectResult { get; set; }

    public Func<CancellationToken, Task<OperationResult<PlcProjectContext>>>? PrepareProjectHandler { get; set; }

    public PlcProjectContext? ProjectContextOpenedDuringDiscovery { get; set; }

    public OperationResult<ProjectTargetDiscoveryResult>? DiscoverProjectTargetsResult { get; set; }

    public Func<CancellationToken, Task<OperationResult<ProjectTargetDiscoveryResult>>>? DiscoverProjectTargetsHandler { get; set; }

    public OperationResult<ProjectTargetResolution>? ResolveProjectTargetResult { get; set; }

    public OperationResult<SafetyProgramAuthorizationRequirementResult>? SafetyAuthorizationRequirementResult { get; set; }

    public Func<CancellationToken, Task<OperationResult<SafetyProgramAuthorizationRequirementResult>>>? SafetyAuthorizationRequirementHandler { get; set; }

    public OperationResult<SafetyProgramAuthorizationResult>? SafetyAuthorizationResult { get; set; }

    public Func<SecureString, CancellationToken, Task<OperationResult<SafetyProgramAuthorizationResult>>>? SafetyAuthorizationHandler { get; set; }

    public OperationResult<PlcBuildResult>? BuildResult { get; set; }

    public Func<CancellationToken, Task<OperationResult<PlcBuildResult>>>? BuildHandler { get; set; }

    public OperationResult<PlcTransferResult>? TransferResult { get; set; }

    public Func<CancellationToken, Task<OperationResult<PlcTransferResult>>>? TransferHandler { get; set; }

    public Func<CancellationToken, Task<OperationResult>>? ReleaseProjectHandler { get; set; }

    public int ReleaseProjectCallCount { get; private set; }

    public event EventHandler<PlcProjectStateChangedEventArgs>? ProjectStateChangedEventHandler;

    protected override AccessServiceKind GetServiceKind()
    {
        return AccessServiceKind.Project;
    }

    public async Task<OperationResult<ProjectTargetDiscoveryResult>> DiscoverProjectTargetsAsync(
        PlcProjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiscoverProjectTargetsCallCount++;
        LastDiscoverProjectTargetsRequest = request;

        if (ProjectContextOpenedDuringDiscovery is not null)
        {
            CurrentProject = ProjectContextOpenedDuringDiscovery;
            ProjectStateChangedEventHandler?.Invoke(
                this,
                new PlcProjectStateChangedEventArgs(ProjectContextOpenedDuringDiscovery));
        }

        return DiscoverProjectTargetsHandler is not null
            ? await DiscoverProjectTargetsHandler(cancellationToken)
            : DiscoverProjectTargetsResult ?? OperationResult<ProjectTargetDiscoveryResult>.Success(
                new ProjectTargetDiscoveryResult(
                    [DefaultProjectTarget],
                    TargetDiscoveryState.CandidatesAvailable,
                    Array.Empty<Diagnostic>()));
    }

    public async Task<OperationResult<PlcProjectContext>> PrepareProjectAsync(
        PlcProjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PrepareProjectCallCount++;
        LastPrepareProjectRequest = request;
        SetState(ServiceState.Busy, "Preparing fake PLC project.");

        var result = PrepareProjectHandler is not null
            ? await PrepareProjectHandler(cancellationToken)
            : PrepareProjectResult ?? OperationResult<PlcProjectContext>.Success(
                new PlcProjectContext(new ProjectContextId("fake-project"), "Fake PLC Project", "fake-project.ap"));
        ReportFailureDiagnostic(
            result,
            "Fake.Project.Prepare.Failed",
            DiagnosticPhase.ProjectPreparation);

        if (result.Value is not null)
        {
            CurrentProject = result.Value;
            ProjectStateChangedEventHandler?.Invoke(this, new PlcProjectStateChangedEventArgs(result.Value));
        }

        SetState(
            result.IsSuccessful
                ? ServiceState.Ready
                : result.State == OperationResultState.Cancelled
                    ? CurrentProject is null ? ServiceState.NotPrepared : ServiceState.Ready
                    : ServiceState.NeedsRevalidation,
            "Fake PLC project preparation finished.");
        return result;
    }

    public Task<OperationResult<ProjectTargetResolution>> ResolveProjectTargetAsync(
        PlcProjectContext project,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = ResolveProjectTargetResult ?? OperationResult<ProjectTargetResolution>.Success(
            new ProjectTargetResolution([DefaultProjectTarget], DefaultProjectTarget, TargetResolutionState.Resolved));

        return Task.FromResult(result);
    }

    public Task<OperationResult<SafetyProgramAuthorizationRequirementResult>> GetSafetyProgramAuthorizationRequirementAsync(
        PlcProjectContext project,
        ProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SafetyAuthorizationRequirementHandler is not null
            ? SafetyAuthorizationRequirementHandler(cancellationToken)
            : Task.FromResult(SafetyAuthorizationRequirementResult
                ?? OperationResult<SafetyProgramAuthorizationRequirementResult>.Success(
                    new SafetyProgramAuthorizationRequirementResult(
                        SafetyProgramAuthorizationRequirement.NotRequired)));
    }

    public Task<OperationResult<SafetyProgramAuthorizationResult>> AuthorizeSafetyProgramAsync(
        PlcProjectContext project,
        ProjectTargetRef target,
        SecureString password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(password);
        return SafetyAuthorizationHandler is not null
            ? SafetyAuthorizationHandler(password, cancellationToken)
            : Task.FromResult(SafetyAuthorizationResult
                ?? OperationResult<SafetyProgramAuthorizationResult>.NotSupported(
                    "Fake Safety-program authorization is not configured."));
    }

    public async Task<OperationResult<PlcBuildResult>> BuildAsync(
        PlcProjectContext project,
        ProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = BuildHandler is not null
            ? await BuildHandler(cancellationToken)
            : BuildResult ?? OperationResult<PlcBuildResult>.Success(
                new PlcBuildResult(true, false, Array.Empty<Diagnostic>()));
        ReportFailureDiagnostic(
            result,
            "Fake.Project.Build.Failed",
            DiagnosticPhase.ProjectTransfer);

        return result;
    }

    public async Task<OperationResult<PlcTransferResult>> TransferToAsync(
        PlcProjectContext project,
        ProjectTargetRef projectTarget,
        RuntimeTargetRef runtimeTarget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = TransferHandler is not null
            ? await TransferHandler(cancellationToken)
            : TransferResult ?? OperationResult<PlcTransferResult>.Success(
                new PlcTransferResult(project, projectTarget, runtimeTarget, timeProvider.GetUtcNow()));
        ReportFailureDiagnostic(
            result,
            "Fake.Project.Transfer.Failed",
            DiagnosticPhase.ProjectTransfer);

        return result;
    }

    public async Task<OperationResult> ReleaseProjectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReleaseProjectCallCount++;
        if (ReleaseProjectHandler is not null)
        {
            return await ReleaseProjectHandler(cancellationToken);
        }

        CurrentProject = null;
        SetState(ServiceState.NotPrepared, "Fake PLC project service released.");
        return OperationResult.Success();
    }

    public Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
    {
        return ReleaseProjectAsync(cancellationToken);
    }

    public void SimulateProjectContextLost()
    {
        CurrentProject = null;
        SetState(ServiceState.Invalidated, "Fake PLC project context was externally lost.");
    }

    private void ReportFailureDiagnostic<T>(
        OperationResult<T> result,
        string code,
        DiagnosticPhase phase)
    {
        if (result.IsSuccessful)
        {
            return;
        }

        ReportDiagnostic(FakeDiagnostics.OperationError(
            code,
            DiagnosticCategory.Workflow,
            phase,
            result.Message ?? "Fake PLC project operation failed.",
            timeProvider.GetUtcNow()));
    }
}
