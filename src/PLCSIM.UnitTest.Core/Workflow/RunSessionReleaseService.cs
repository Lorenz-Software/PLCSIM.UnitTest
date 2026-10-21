using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Core.AccessProfiles;

namespace PLCSIM.UnitTest.Core.Workflow;

public sealed class RunSessionReleaseService
{
    private readonly RunWorkflowService workflow;
    private readonly AccessProfileSessionService accessProfileSessions;
    private readonly ILogger<RunSessionReleaseService> logger;
    private readonly SemaphoreSlim releaseGate = new(1, 1);

    public RunSessionReleaseService(
        RunWorkflowService workflow,
        AccessProfileSessionService accessProfileSessions,
        CoreServiceContext coreContext)
    {
        this.workflow = workflow;
        this.accessProfileSessions = accessProfileSessions;
        logger = coreContext.LoggerFactory.CreateLogger<RunSessionReleaseService>()
            ?? NullLogger<RunSessionReleaseService>.Instance;
    }

    public Task<OperationResult> BeginScopedReleaseAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return workflow.QuiesceActiveOperationAsync(timeout, cancellationToken);
    }

    public void EndScopedRelease()
    {
        workflow.CompleteSessionRelease();
    }

    public async Task<RunSessionReleaseResult> ReleaseAsync(
        WorkflowActionPolicy workflowCleanupPolicy,
        TimeSpan profileUnloadTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflowCleanupPolicy);
        if (profileUnloadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(profileUnloadTimeout),
                profileUnloadTimeout,
                "Profile unload timeout must be greater than zero.");
        }

        await releaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            logger.LogInformation(
                "Run session release started. WorkflowCleanupTimeout={WorkflowCleanupTimeout}, ProfileUnloadTimeout={ProfileUnloadTimeout}",
                workflowCleanupPolicy.Timeout,
                profileUnloadTimeout);

            var operationRelease = await BeginScopedReleaseAsync(
                workflowCleanupPolicy.Timeout,
                cancellationToken).ConfigureAwait(false);
            if (!operationRelease.IsSuccessful)
            {
                logger.LogWarning(
                    "Active workflow operation release did not complete successfully. State={State}, Message={Message}",
                    operationRelease.State,
                    operationRelease.Message);
            }

            var workflowCleanup = await CleanupWorkflowAsync(
                workflowCleanupPolicy,
                cancellationToken).ConfigureAwait(false);
            var profileUnload = await UnloadProfileAsync(profileUnloadTimeout).ConfigureAwait(false);
            var result = new RunSessionReleaseResult(operationRelease, workflowCleanup, profileUnload);

            logger.Log(
                result.IsSuccessful ? LogLevel.Information : LogLevel.Warning,
                "Run session release finished. ActiveOperationReleaseState={ActiveOperationReleaseState}, WorkflowCleanupState={WorkflowCleanupState}, ProfileUnloadState={ProfileUnloadState}",
                operationRelease.State,
                workflowCleanup.State,
                profileUnload.State);
            return result;
        }
        finally
        {
            EndScopedRelease();
            releaseGate.Release();
        }
    }

    private async Task<OperationResult> CleanupWorkflowAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await workflow.CleanupAsync(policy, cancellationToken).ConfigureAwait(false)).Result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OperationResult.Cancelled("Run session release was cancelled while workflow cleanup was active.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Workflow cleanup failed unexpectedly during run session release.");
            return OperationResult.Failure($"Workflow cleanup failed unexpectedly: {ex.Message}");
        }
    }

    private async Task<OperationResult> UnloadProfileAsync(TimeSpan timeout)
    {
        try
        {
            // Once release starts, profile unload is always attempted even when workflow cleanup
            // failed or the initiating caller cancelled its workflow wait.
            await accessProfileSessions.UnloadAsync(timeout, CancellationToken.None).ConfigureAwait(false);
            return OperationResult.Success("Access profile session unloaded.");
        }
        catch (TimeoutException ex)
        {
            logger.LogError(ex, "Access-profile unload timed out during run session release.");
            return OperationResult.TimedOut(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Access-profile unload failed unexpectedly during run session release.");
            return OperationResult.Failure($"Access-profile unload failed unexpectedly: {ex.Message}");
        }
    }
}

public sealed record RunSessionReleaseResult(
    OperationResult ActiveOperationRelease,
    OperationResult WorkflowCleanup,
    OperationResult ProfileUnload)
{
    public bool IsSuccessful =>
        ActiveOperationRelease.IsSuccessful
        && WorkflowCleanup.IsSuccessful
        && ProfileUnload.IsSuccessful;
}
