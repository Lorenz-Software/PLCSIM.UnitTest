using System.Security;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff;

internal sealed class BeckhoffProjectService : BeckhoffServiceBase, IPlcProjectService
{
    private readonly ProjectOwnershipMode ownership;
    private readonly Func<IEngineeringProjectAccess> createAccess;
    private readonly IEngineeringRuntimeTransfer? runtimeTransfer;
    private readonly SemaphoreSlim operations = new(1, 1);
    private IEngineeringProjectAccess? access;
    private ProjectWorkspace? workspace;
    private ProjectSettings? settings;
    private IReadOnlyList<EngineeringPlcTarget> targets = [];
    private bool cleanupRequired;
    private int closing;

    internal BeckhoffProjectService(AccessProfileContext context, ProjectOwnershipMode ownership,
        Func<IEngineeringProjectAccess>? createAccess = null, IEngineeringRuntimeTransfer? runtimeTransfer = null) : base(context)
    {
        this.ownership = ownership;
        this.createAccess = createAccess ?? (() => new StaEngineeringAccess(() => new XaeProjectAccess(Logger)));
        this.runtimeTransfer = runtimeTransfer;
    }

    public event EventHandler<PlcProjectStateChangedEventArgs>? ProjectStateChangedEventHandler;
    public PlcProjectContext? CurrentProject { get; private set; }
    internal EngineeringPlcTarget? SelectedTarget { get; private set; }

    public Task<OperationResult<ProjectTargetDiscoveryResult>> DiscoverProjectTargetsAsync(PlcProjectRequest request, CancellationToken cancellationToken) =>
        WithProjectAsync(request, () => OperationResult<ProjectTargetDiscoveryResult>.Success(new(
            targets.Select(target => target.ToReference()).ToArray(),
            targets.Count == 0 ? TargetDiscoveryState.NoCandidates : TargetDiscoveryState.CandidatesAvailable, [])), cancellationToken);

    public Task<OperationResult<PlcProjectContext>> PrepareProjectAsync(PlcProjectRequest request, CancellationToken cancellationToken) =>
        WithProjectAsync(request, () =>
        {
            var resolution = ResolveSelection();
            if (resolution.State != TargetResolutionState.Resolved)
                return SelectionFailure<PlcProjectContext>(resolution.State);
            return OperationResult<PlcProjectContext>.Success(CurrentProject!);
        }, cancellationToken);

    public async Task<OperationResult<ProjectTargetResolution>> ResolveProjectTargetAsync(PlcProjectContext project, CancellationToken cancellationToken)
    {
        EnsureProjectActive(cancellationToken);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureProjectActive(cancellationToken);
            if (cleanupRequired || CurrentProject is null || project.Id != CurrentProject.Id || settings is null)
                return OperationResult<ProjectTargetResolution>.Failure("The supplied project context is not active.");
            var result = ResolveSelection();
            return OperationResult<ProjectTargetResolution>.Success(result);
        }
        finally { operations.Release(); }
    }

    private async Task<OperationResult<T>> WithProjectAsync<T>(PlcProjectRequest request,
        Func<OperationResult<T>> action, CancellationToken cancellationToken)
    {
        EnsureProjectActive(cancellationToken);
        if (ownership != ProjectOwnershipMode.ApplicationManaged)
            return await Unsupported<T>("managed project access in an externally managed session", cancellationToken).ConfigureAwait(false);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureProjectActive(cancellationToken);
            if (cleanupRequired) return OperationResult<T>.Failure("Release the incomplete engineering context before retrying.");
            var requested = ProjectSettings.Parse(request.Configuration);
            await OpenAndRefreshAsync(requested, cancellationToken).ConfigureAwait(false);
            return action();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            ReportDiagnostic("Beckhoff.Project.PrepareFailed", error.GetBaseException().Message,
                DiagnosticCategory.Environment, DiagnosticPhase.ProjectPreparation);
            return error is NotSupportedException
                ? OperationResult<T>.NotSupported(error.Message) : OperationResult<T>.Failure(error.GetBaseException().Message);
        }
        finally { operations.Release(); }
    }

    private async Task OpenAndRefreshAsync(ProjectSettings requested, CancellationToken cancellationToken)
    {
        if (settings is null || !settings.UsesSameSource(requested))
        {
            await Task.Run(() => ProjectWorkspace.ValidateSource(requested, cancellationToken), cancellationToken).ConfigureAwait(false);
            await ReleaseAccessAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            if (workspace is null)
            {
                workspace = ProjectWorkspace.Create(requested);
                access = createAccess();
                await access.OpenAsync(workspace, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                CurrentProject = new(new(Guid.NewGuid().ToString("N")), Path.GetFileNameWithoutExtension(requested.SourcePath), requested.SourcePath);
                Logger.LogInformation("XAE project opened: Source={Source}, ProjectContext={ProjectContext}", requested.SourcePath, CurrentProject.Id);
            }
            var discovered = await access!.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (discovered.Select(target => target.Id).Distinct(StringComparer.Ordinal).Count() != discovered.Count)
                throw new InvalidDataException("Project discovery returned duplicate PLC target identities.");
            targets = discovered.ToArray();
            Logger.LogDebug("Project discovery completed: ProjectContext={ProjectContext}, TargetCount={Count}", CurrentProject!.Id, targets.Count);
            settings = requested;
            SelectedTarget = null;
            SetHealth(AccessHealth.Healthy(Context.TimeProvider.GetUtcNow()));
            SetState(ServiceState.Ready, "TwinCAT project targets discovered.");
            ProjectStateChangedEventHandler?.Invoke(this, new(CurrentProject!));
        }
        catch
        {
            try { await ReleaseAccessAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* ReleaseAccessAsync reports incomplete cleanup; preserve the operation failure. */ }
            throw;
        }
    }

    private ProjectTargetResolution ResolveSelection()
    {
        SelectedTarget = null;
        var candidates = targets.Select(target => target.ToReference()).ToArray();
        if (settings!.TargetId is { } id)
        {
            var selected = targets.SingleOrDefault(target => target.Id == id);
            if (selected is null) return new(candidates, null, TargetResolutionState.InvalidConfiguration);
            SelectedTarget = selected;
        }
        else if (targets.Count == 1) SelectedTarget = targets[0];
        else return new(candidates, null, targets.Count == 0 ? TargetResolutionState.NotAvailable : TargetResolutionState.Ambiguous);
        if (settings.ExplicitPlcPort is { } port && SelectedTarget.PlcPort != port)
        {
            SelectedTarget = null;
            return new(candidates, null, TargetResolutionState.Incompatible);
        }
        return new(candidates, SelectedTarget.ToReference(), TargetResolutionState.Resolved);
    }

    private OperationResult<T> SelectionFailure<T>(TargetResolutionState state)
    {
        var message = state switch
        {
            TargetResolutionState.Ambiguous => "Select one of the discovered PLC projects using plcRef.id.",
            TargetResolutionState.NotAvailable => "The source contains no supported PLC project.",
            TargetResolutionState.Incompatible => "The configured PLC port conflicts with the selected PLC project's ADS port.",
            _ => "The configured PLC target was not found in the project."
        };
        ReportDiagnostic("Beckhoff.Project.TargetSelectionFailed", message, DiagnosticCategory.Configuration, DiagnosticPhase.ProjectPreparation);
        return OperationResult<T>.Failure(message);
    }

    public Task<OperationResult<SafetyProgramAuthorizationRequirementResult>> GetSafetyProgramAuthorizationRequirementAsync(PlcProjectContext project, ProjectTargetRef target, CancellationToken cancellationToken) =>
        WithSelectedTargetAsync(project, target, "AuthorizationCheck", async selected =>
        {
            await access!.EnsureStandardPlcAsync(selected, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return OperationResult<SafetyProgramAuthorizationRequirementResult>.Success(new(SafetyProgramAuthorizationRequirement.NotRequired));
        }, cancellationToken);
    public Task<OperationResult<SafetyProgramAuthorizationResult>> AuthorizeSafetyProgramAsync(PlcProjectContext project, ProjectTargetRef target, SecureString password, CancellationToken cancellationToken) =>
        Unsupported<SafetyProgramAuthorizationResult>("Safety program authorization", cancellationToken);
    public Task<OperationResult<PlcBuildResult>> BuildAsync(PlcProjectContext project, ProjectTargetRef target, CancellationToken cancellationToken) =>
        WithSelectedTargetAsync(project, target, "Build", async selected =>
        {
            var result = await access!.BuildAsync(selected, cancellationToken).ConfigureAwait(false);
            var diagnostics = result.Messages.Select(message => ReportDiagnostic(message.Code, message.Message,
                DiagnosticCategory.Environment, DiagnosticPhase.ProjectPreparation, message.Level)).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            var build = new PlcBuildResult(result.Succeeded, diagnostics.Any(item => item.Level == LogLevel.Warning), diagnostics);
            return result.Succeeded ? OperationResult<PlcBuildResult>.Success(build)
                : OperationResult<PlcBuildResult>.Failure(build, "TwinCAT build failed. See the build diagnostics for details.");
        }, cancellationToken);

    private async Task<OperationResult<T>> WithSelectedTargetAsync<T>(PlcProjectContext project, ProjectTargetRef target,
        string operation, Func<EngineeringPlcTarget, Task<OperationResult<T>>> action, CancellationToken cancellationToken)
    {
        var phase = operation == "Transfer" ? DiagnosticPhase.ProjectTransfer : DiagnosticPhase.ProjectPreparation;
        EnsureProjectActive(cancellationToken);
        if (ownership != ProjectOwnershipMode.ApplicationManaged)
            return await Unsupported<T>("managed project access in an externally managed session", cancellationToken).ConfigureAwait(false);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureProjectActive(cancellationToken);
            if (cleanupRequired || access is null || CurrentProject is null || project?.Id != CurrentProject.Id
                || SelectedTarget is null || target?.Id.Value != SelectedTarget.Id)
            {
                const string message = "The project context or selected PLC target is not active. Prepare and select the project first.";
                ReportDiagnostic("Beckhoff.Project.TargetNotPrepared", message, DiagnosticCategory.Configuration, phase);
                return OperationResult<T>.Failure(message);
            }
            SetState(ServiceState.Busy, $"TwinCAT project {operation} is running.");
            Logger.LogDebug("Engineering operation started: Operation={Operation}, ProjectContext={ProjectContext}, PlcTarget={Target}, Port={Port}",
                operation, project.Id, SelectedTarget.Id, SelectedTarget.PlcPort);
            var result = await action(SelectedTarget).ConfigureAwait(false);
            Logger.LogInformation("Engineering operation returned: Operation={Operation}, ProjectContext={ProjectContext}, PlcTarget={Target}, Successful={Successful}",
                operation, project.Id, SelectedTarget.Id, result.IsSuccessful);
            return result;
        }
        catch (OperationCanceledException)
        {
            ReportDiagnostic($"Beckhoff.Project.{operation}Cancelled", $"TwinCAT project {operation} was cancelled; dispatched engineering work has returned.",
                DiagnosticCategory.Workflow, phase, LogLevel.Information);
            throw;
        }
        catch (Exception error)
        {
            var message = XaeProjectAccess.DescribeEngineeringError(error);
            ReportDiagnostic($"Beckhoff.Project.{operation}{(error is NotSupportedException ? "NotSupported" : "Failed")}", message,
                DiagnosticCategory.Environment, phase);
            return error is NotSupportedException ? OperationResult<T>.NotSupported(message) : OperationResult<T>.Failure(message);
        }
        finally
        {
            try { if (State == ServiceState.Busy) SetState(ServiceState.Ready, "TwinCAT project operation finished."); }
            finally { operations.Release(); }
        }
    }
    public Task<OperationResult<PlcTransferResult>> TransferToAsync(PlcProjectContext project, ProjectTargetRef projectTarget, RuntimeTargetRef runtimeTarget, CancellationToken cancellationToken) =>
        WithSelectedTargetAsync(project, projectTarget, "Transfer", async selected =>
        {
            if (runtimeTransfer is null) throw new InvalidOperationException("The prepared Runtime transfer service is unavailable.");
            await access!.EnsureStandardPlcAsync(selected, cancellationToken).ConfigureAwait(false);
            await runtimeTransfer.TransferAsync(runtimeTarget, selected.PlcPort,
                (netId, token) => access.ActivateAsync(selected, netId, token),
                (netId, token) => access.DownloadAsync(selected, netId, token), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return OperationResult<PlcTransferResult>.Success(new(project, projectTarget, runtimeTarget, Context.TimeProvider.GetUtcNow()));
        }, cancellationToken);
    public async Task<OperationResult> ReleaseProjectAsync(CancellationToken cancellationToken)
    {
        EnsureActive(cancellationToken);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReleaseAccessAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult.Success("Owned TwinCAT project access released.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return OperationResult.Failure(error.GetBaseException().Message);
        }
        finally { operations.Release(); }
    }

    public override Task<OperationResult> CleanupAsync(CancellationToken cancellationToken) => ReleaseProjectAsync(cancellationToken);

    internal override async Task CloseAsync()
    {
        Interlocked.Exchange(ref closing, 1);
        var result = await ReleaseProjectAsync(CancellationToken.None).ConfigureAwait(false);
        if (!result.IsSuccessful) throw new IOException(result.Message);
    }

    private async Task ReleaseAccessAsync(CancellationToken cancellationToken)
    {
        SelectedTarget = null;
        targets = [];
        try
        {
            if (access is not null) await access.CloseAsync(cancellationToken).ConfigureAwait(false);
            access = null;
            workspace?.Release();
            workspace = null;
            settings = null;
            CurrentProject = null;
            cleanupRequired = false;
            SetHealth(AccessHealth.NotEstablished(Context.TimeProvider.GetUtcNow()));
            SetState(ServiceState.NotPrepared, "Owned project access released.");
        }
        catch (Exception error)
        {
            cleanupRequired = true;
            ReportCleanupFailure(error);
            throw;
        }
    }

    private void ReportCleanupFailure(Exception error)
    {
        var message = error.GetBaseException().Message;
        cleanupRequired = true;
        SetHealth(new(AccessHealthState.Invalidated, AccessHealthProblem.ContextLost,
            "Beckhoff.Project.CleanupFailed", message, Context.TimeProvider.GetUtcNow()));
        SetState(ServiceState.CleanupRequired, message);
        ReportDiagnostic("Beckhoff.Project.CleanupFailed", message, DiagnosticCategory.Cleanup, DiagnosticPhase.Cleanup);
    }

    private void EnsureProjectActive(CancellationToken cancellationToken)
    {
        EnsureActive(cancellationToken);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref closing) != 0, this);
    }
}
