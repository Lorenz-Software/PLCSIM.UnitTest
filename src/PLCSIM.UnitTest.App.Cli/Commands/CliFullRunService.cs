using System.Security;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Artifacts;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Output;
using PLCSIM.UnitTest.Core.Workflow;

namespace PLCSIM.UnitTest.App.Cli.Commands;

public enum CliFullRunState
{
    Completed,
    TestFailed,
    InvalidConfiguration,
    ProfileUnavailable,
    EnvironmentUnavailable,
    Failed,
    TimedOut,
    Cancelled,
    ArtifactFailed,
    CleanupFailed
}

internal sealed record CliOutputPreparationResult(
    bool IsSuccessful,
    string? Path,
    string? Message)
{
    public static CliOutputPreparationResult Success(string path) => new(true, path, null);

    public static CliOutputPreparationResult Failure(string message) => new(false, null, message);
}

public sealed record CliFullRunResult(
    CliFullRunState State,
    RunWorkflowPublication? Publication,
    RunSessionReleaseResult? Release,
    IReadOnlyList<Diagnostic> Diagnostics,
    string? Message,
    string? OutputPath = null,
    CliAnalysisExportResult? AnalysisExport = null);

public interface ICliFullRunService
{
    Task<CliFullRunResult> RunAsync(
        string configurationPath,
        string outputPath,
        Action<string>? progress,
        CancellationToken cancellationToken,
        CliRunOptions? runOptions = null);
}

public sealed class CliFullRunService : ICliFullRunService
{
    private readonly ICliConfigurationCheckService configurationCheck;
    private readonly AccessProfileSessionService profileSessions;
    private readonly RunWorkflowService workflow;
    private readonly RunSessionReleaseService sessionRelease;
    private readonly ArtifactExportService artifactExport;
    private readonly TimeProvider timeProvider;
    private readonly ApplicationOperationTimeoutPolicy operationTimeouts;

    public CliFullRunService(
        ICliConfigurationCheckService configurationCheck,
        AccessProfileSessionService profileSessions,
        RunWorkflowService workflow,
        RunSessionReleaseService sessionRelease,
        ArtifactExportService artifactExport,
        TimeProvider timeProvider)
    {
        this.configurationCheck = configurationCheck ?? throw new ArgumentNullException(nameof(configurationCheck));
        this.profileSessions = profileSessions ?? throw new ArgumentNullException(nameof(profileSessions));
        this.workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        this.sessionRelease = sessionRelease ?? throw new ArgumentNullException(nameof(sessionRelease));
        this.artifactExport = artifactExport ?? throw new ArgumentNullException(nameof(artifactExport));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        operationTimeouts = RunConfigurationDefaults.CreateDefaultApplicationOperationTimeoutPolicy();
    }

    public async Task<CliFullRunResult> RunAsync(
        string configurationPath,
        string outputPath,
        Action<string>? progress,
        CancellationToken cancellationToken,
        CliRunOptions? runOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        runOptions ??= new CliRunOptions();
        runOptions.Validate();

        RunSessionReleaseResult? release = null;
        CliFullRunResult? result = null;
        var analysis = runOptions.AnalysisOutputDirectory is null ? null : new CliAnalysisExport(timeProvider);
        string? captureError = null;
        var previousTraceSession = analysis is null ? null : workflow.EvidenceAnalysis.CurrentTrace?.SessionId;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputPreparation = PrepareOutputPath(configurationPath, outputPath);
            if (!outputPreparation.IsSuccessful)
            {
                result = CreateResult(
                    CliFullRunState.ArtifactFailed,
                    null,
                    outputPreparation.Message);
            }
            else
            {
                result = await ExecuteAsync(
                    configurationPath,
                    outputPreparation.Path!,
                    progress,
                    runOptions,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = CreateResult(
                CliFullRunState.Cancelled,
                workflow.Current,
                "CLI run was cancelled.");
        }
        catch (TimeoutException ex)
        {
            result = CreateResult(CliFullRunState.TimedOut, workflow.Current, ex.Message);
        }
        catch (Exception ex)
        {
            result = CreateResult(CliFullRunState.Failed, workflow.Current, ex.Message);
        }
        finally
        {
            // Capture while workflow evidence still belongs to this invocation, before cleanup.
            if (analysis is not null)
            {
                try
                {
                    var trace = workflow.EvidenceAnalysis.CurrentTrace;
                    analysis.Capture(trace?.SessionId == previousTraceSession ? null : trace, result);
                }
                catch (Exception ex)
                {
                    captureError = $"Evidence analysis capture failed: {ex.Message}";
                }
            }
            WriteProgress(progress, "Releasing runtime and profile resources...");
            release = await ReleaseSessionAsync().ConfigureAwait(false);
            WriteProgress(progress, release.IsSuccessful
                ? "Runtime and profile resources released."
                : "Runtime and profile resource release completed with errors.");
        }

        result = result! with { Release = release };
        if (analysis is not null && (runOptions.AnalysisOn == CliAnalysisTrigger.Always
            || CliOutcomeResolver.Resolve(result) != CliExitCode.Success))
        {
            var exported = captureError is { } error
                ? new CliAnalysisExportResult(null, error)
                : await analysis.WriteAsync(runOptions.AnalysisOutputDirectory!).ConfigureAwait(false);
            result = result with { AnalysisExport = exported };
        }
        return result;
    }

    private async Task<RunSessionReleaseResult> ReleaseSessionAsync()
    {
        try
        {
            return await sessionRelease.ReleaseAsync(
                new WorkflowActionPolicy(operationTimeouts.Cleanup),
                operationTimeouts.ProfileUnload,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var failure = OperationResult.Failure(
                $"Coordinated run-session release failed unexpectedly: {ex.Message}");
            return new RunSessionReleaseResult(failure, failure, failure);
        }
    }

    private static CliOutputPreparationResult PrepareOutputPath(string configurationPath, string outputPath)
    {
        try
        {
            var resolvedPath = Path.GetFullPath(outputPath, Environment.CurrentDirectory);
            var resolvedConfigurationPath = Path.GetFullPath(configurationPath, Environment.CurrentDirectory);
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.Equals(resolvedPath, resolvedConfigurationPath, pathComparison))
            {
                return CliOutputPreparationResult.Failure(
                    "The JUnit output path must differ from the configuration file path. The configuration file was not changed.");
            }

            var directory = Path.GetDirectoryName(resolvedPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return CliOutputPreparationResult.Failure("The JUnit output path has no parent directory.");
            }

            Directory.CreateDirectory(directory);
            if (File.Exists(resolvedPath))
            {
                File.Delete(resolvedPath);
            }

            return CliOutputPreparationResult.Success(resolvedPath);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return CliOutputPreparationResult.Failure(
                $"The JUnit output path could not be prepared: {ex.Message}");
        }
    }

    private async Task<CliFullRunResult> ExecuteAsync(
        string configurationPath,
        string outputPath,
        Action<string>? progress,
        CliRunOptions runOptions,
        CancellationToken cancellationToken)
    {
        WriteProgress(progress, $"Validating configuration '{Path.GetFullPath(configurationPath)}'...");
        var check = await configurationCheck
            .CheckAsync(configurationPath, cancellationToken)
            .ConfigureAwait(false);
        if (!check.IsValid || check.Setup is null || check.Profile is null)
        {
            return new CliFullRunResult(
                check.State == CliConfigurationCheckState.ProfileUnavailable
                    ? CliFullRunState.ProfileUnavailable
                    : CliFullRunState.InvalidConfiguration,
                null,
                null,
                check.Diagnostics,
                check.Diagnostics.FirstOrDefault()?.Message);
        }

        var initialSetup = check.Setup;
        WriteProgress(
            progress,
            $"Loading access profile '{initialSetup.AccessProfile.ProfileId} "
            + $"{initialSetup.AccessProfile.ProfileVersion}'...");
        var profileLoad = await profileSessions.LoadAsync(
            new AccessProfileSessionRequest(
                initialSetup.AccessProfile.ProfileId,
                initialSetup.AccessProfile.ProfileVersion,
                initialSetup.AccessProfile.ProjectSource,
                ProjectOwnershipMode.ApplicationManaged),
            operationTimeouts.ProfileLoad,
            cancellationToken).ConfigureAwait(false);
        if (profileLoad.State != AccessProfileLoadState.Loaded || profileLoad.Session is null)
        {
            var state = profileLoad.State == AccessProfileLoadState.TimedOut
                ? CliFullRunState.TimedOut
                : CliFullRunState.EnvironmentUnavailable;
            return new CliFullRunResult(
                state,
                null,
                null,
                profileLoad.Diagnostics,
                $"Access profile load finished with state '{profileLoad.State}'.");
        }

        var bind = await workflow.BindAccessProfileSessionAsync(
            profileLoad.Session,
            Policy(operationTimeouts.ProfileLoad),
            cancellationToken).ConfigureAwait(false);
        if (!bind.Result.IsSuccessful)
        {
            return FromOperation(bind.Result, bind.Publication);
        }

        var configurationService = profileLoad.Session.Configuration;
        if (configurationService is null)
        {
            return CreateResult(
                CliFullRunState.InvalidConfiguration,
                workflow.Current,
                "The loaded access profile does not provide a configuration service.");
        }

        var configurationLoad = configurationService.LoadConfig(initialSetup.AccessProfile.Values.Json);
        if (!configurationLoad.IsSuccessful || configurationLoad.Value is null)
        {
            return new CliFullRunResult(
                CliFullRunState.InvalidConfiguration,
                workflow.Current,
                null,
                configurationLoad.Diagnostics,
                configurationLoad.Diagnostics.FirstOrDefault()?.Message);
        }

        var loadedConfiguration = configurationLoad.Value;
        WriteProgress(progress, "Discovering project targets...");
        var projectDiscovery = await workflow.DiscoverProjectTargetsAsync(
            loadedConfiguration,
            Policy(operationTimeouts.ProjectDiscovery),
            cancellationToken).ConfigureAwait(false);
        if (!projectDiscovery.Result.IsSuccessful)
        {
            return FromOperation(projectDiscovery.Result, projectDiscovery.Publication);
        }

        var projectTarget = ResolveProjectTarget(
            configurationService.ProjectTargetId,
            projectDiscovery.Publication.ProjectTargets);
        if (projectTarget is null)
        {
            return CreateResult(
                CliFullRunState.InvalidConfiguration,
                projectDiscovery.Publication,
                "The configured project target was not found in project discovery.");
        }

        WriteProgress(progress, "Discovering runtime capabilities...");
        var runtimeDiscovery = await workflow.DiscoverRuntimeTargetsAsync(
            loadedConfiguration,
            Policy(operationTimeouts.RuntimeDiscovery),
            cancellationToken).ConfigureAwait(false);
        if (!runtimeDiscovery.Result.IsSuccessful)
        {
            return FromOperation(runtimeDiscovery.Result, runtimeDiscovery.Publication);
        }

        var projectConfiguration = configurationService.SetProjectTarget(projectTarget);
        if (!projectConfiguration.IsSuccessful || projectConfiguration.Value is null)
        {
            return new CliFullRunResult(
                CliFullRunState.InvalidConfiguration,
                workflow.Current,
                null,
                projectConfiguration.Diagnostics,
                projectConfiguration.Diagnostics.FirstOrDefault()?.Message);
        }

        var provisioningMode = initialSetup.RuntimeProvisioningMode;
        var (runtimeTarget, targetError) = RuntimeTargetResolver.Resolve(
            provisioningMode, configurationService.RuntimeTargetId, runtimeDiscovery.Publication.RuntimeTargets);
        if (targetError is not null)
        {
            return CreateResult(CliFullRunState.InvalidConfiguration, workflow.Current, targetError);
        }

        var runtimeConfiguration = runtimeTarget is null
            ? AccessProfileConfigurationResult<AccessProfileConfig>.Success(configurationService.Configuration)
            : configurationService.SetRuntimeTarget(runtimeTarget);
        if (!runtimeConfiguration.IsSuccessful || runtimeConfiguration.Value is null)
        {
            return new CliFullRunResult(
                CliFullRunState.InvalidConfiguration,
                workflow.Current,
                null,
                runtimeConfiguration.Diagnostics,
                runtimeConfiguration.Diagnostics.FirstOrDefault()?.Message);
        }

        var profileValidation = configurationService.ValidateConfig(provisioningMode);
        if (!profileValidation.IsValid)
        {
            return new CliFullRunResult(
                CliFullRunState.InvalidConfiguration,
                workflow.Current,
                null,
                profileValidation.Diagnostics,
                profileValidation.Diagnostics.FirstOrDefault()?.Message);
        }

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))
            ?? Environment.CurrentDirectory;
        var setup = initialSetup with
        {
            AccessProfile = runtimeConfiguration.Value,
            RuntimeProvisioningMode = provisioningMode,
            ProjectTarget = projectTarget,
            RuntimeTarget = runtimeTarget,
            OutputPolicy = new OutputPolicy(outputDirectory, TestResult: true)
        };
        var configure = workflow.Configure(setup, operationTimeouts);
        if (!configure.IsSuccessful)
        {
            return CreateResult(CliFullRunState.InvalidConfiguration, workflow.Current, configure.Message);
        }

        WriteProgress(progress, "Preparing project...");
        var prepareProject = await workflow.PrepareProjectAsync(
            Policy(setup.Execution.Timeouts.ProjectPrepare),
            cancellationToken).ConfigureAwait(false);
        if (!prepareProject.Result.IsSuccessful)
        {
            return FromOperation(prepareProject.Result, prepareProject.Publication);
        }

        WriteProgress(progress, provisioningMode == RuntimeProvisioningMode.CreateOwned
            ? "Creating and preparing owned runtime..."
            : "Preparing selected existing runtime...");
        var prepareRuntime = await workflow.PrepareRuntimeAsync(
            Policy(setup.Execution.Timeouts.RuntimePrepare),
            cancellationToken).ConfigureAwait(false);
        if (!prepareRuntime.Result.IsSuccessful)
        {
            return FromOperation(prepareRuntime.Result, prepareRuntime.Publication);
        }

        WriteProgress(progress, "Checking Safety program authorization...");
        var requirement = await workflow.GetSafetyProgramAuthorizationRequirementAsync(
            Policy(setup.Execution.Timeouts.BuildTransfer),
            cancellationToken).ConfigureAwait(false);
        if (!requirement.Result.IsSuccessful || requirement.Result.Value is null)
        {
            return FromOperation(requirement.Result, requirement.Publication);
        }

        if (requirement.Result.Value.Requirement == SafetyProgramAuthorizationRequirement.PasswordRequired)
        {
            var configuredPassword = check.ProjectAuthorization?.SafetyPassword;
            if (string.IsNullOrEmpty(configuredPassword))
            {
                return CreateResult(
                    CliFullRunState.InvalidConfiguration,
                    requirement.Publication,
                    "The selected project target requires a Safety program password, but project.authorization.safetyPassword is not configured.");
            }

            WriteProgress(progress, "Authorizing Safety program...");
            using var password = CreateReadOnlyPassword(configuredPassword);
            var authorization = await workflow.AuthorizeSafetyProgramAsync(
                password,
                Policy(setup.Execution.Timeouts.BuildTransfer),
                cancellationToken).ConfigureAwait(false);
            if (!authorization.Result.IsSuccessful || authorization.Result.Value?.Authorized != true)
            {
                return FromOperation(authorization.Result, authorization.Publication);
            }
        }

        WriteProgress(progress, "Building and transferring project...");
        var transfer = await workflow.TransferProjectAsync(
            Policy(setup.Execution.Timeouts.BuildTransfer),
            Policy(operationTimeouts.ProjectRelease),
            cancellationToken).ConfigureAwait(false);
        if (!transfer.Result.IsSuccessful)
        {
            return FromOperation(transfer.Result, transfer.Publication);
        }

        if (workflow.Current.Status.PlcState.OperatingState != PlcOperatingState.Running)
        {
            WriteProgress(progress, "Starting PLC...");
            var startPlc = await workflow.StartPlcAsync(
                Policy(setup.Execution.Timeouts.PlcCommand),
                cancellationToken).ConfigureAwait(false);
            if (!startPlc.Result.IsSuccessful)
            {
                return FromOperation(startPlc.Result, startPlc.Publication);
            }
        }

        var running = await WaitForPublicationAsync(
            publication => publication.Status.PlcState.OperatingState == PlcOperatingState.Running,
            setup.Execution.Timeouts.PlcStartReaction,
            cancellationToken).ConfigureAwait(false);

        WriteProgress(progress, "Preparing UnitTest communication and event-stream monitoring...");
        var communication = await WaitForPublicationAsync(
            publication => publication.Status.UnitTestCommunicationContextActive
                && publication.Status.MonitoringActive,
            setup.Execution.Timeouts.CommunicationPrepare + operationTimeouts.Monitoring,
            cancellationToken).ConfigureAwait(false);

        var readinessFailure = await EnsureUnitTestReadyAsync(
            runOptions, setup.Execution.Timeouts, progress, cancellationToken).ConfigureAwait(false);
        if (readinessFailure is not null) return readinessFailure;

        var startGate = workflow.Current.ActionGates.Single(
            gate => gate.Action == WorkflowAction.StartUnitTest);
        var lostEventReason = startGate.Reasons.FirstOrDefault(
            reason => reason.Code == ActionGateReasonCode.EventBufferHasLostEvents);
        if (lostEventReason is not null)
        {
            return CreateEventBufferStartBlockedResult(workflow.Current, lostEventReason.Message);
        }

        WriteProgress(progress, "Starting UnitTest run...");
        var startRun = await workflow.StartUnitTestAsync(
            Policy(setup.Execution.Timeouts.UnitTestCommand),
            cancellationToken).ConfigureAwait(false);
        if (!startRun.Result.IsSuccessful)
        {
            return FromOperation(startRun.Result, startRun.Publication);
        }

        WriteProgress(progress, "Waiting for UnitTest completion and final event evidence...");
        var completed = await WaitForPublicationAsync(
            publication => publication.Status.IsExportable,
            setup.Execution.Timeouts.TestRun + setup.Execution.Timeouts.FinalEvent,
            cancellationToken).ConfigureAwait(false);
        if (completed.CurrentRun is null)
        {
            return CreateResult(
                CliFullRunState.ArtifactFailed,
                completed,
                "The completed workflow publication contains no run snapshot for JUnit export.");
        }

        WriteProgress(progress, $"Writing JUnit artifact '{outputPath}'...");
        artifactExport.UpdateOptions(new ArtifactExportOptions(
            outputDirectory,
            TestResult: true,
            Path.GetFileName(outputPath)));
        var output = await artifactExport
            .ExportAsync(completed.CurrentRun, cancellationToken)
            .ConfigureAwait(false);
        var junit = output.Artifacts.SingleOrDefault(result => result.Format == ArtifactFormat.JUnitXml);
        if (junit?.State != ArtifactState.Written)
        {
            return CreateResult(
                CliFullRunState.ArtifactFailed,
                completed,
                "The required JUnit XML artifact could not be written.",
                outputPath);
        }

        var runState = completed.CurrentRun.Result == UnitTestResultState.Passed
            ? CliFullRunState.Completed
            : CliFullRunState.TestFailed;
        return CreateResult(
            runState,
            completed,
            runState == CliFullRunState.Completed
                ? "UnitTest run completed successfully."
                : $"UnitTest run completed with result '{completed.CurrentRun.Result}'.",
            junit.OutputPath);
    }

    internal async Task<CliFullRunResult?> EnsureUnitTestReadyAsync(
        CliRunOptions options,
        RunOperationTimeoutPolicy timeouts,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        WriteProgress(progress, $"Waiting for UnitTest Idle + Ready (up to {options.UnitTestReadyTimeoutSeconds} seconds)...");
        try
        {
            var observed = await WaitForReadinessAsync(
                TimeSpan.FromSeconds(options.UnitTestReadyTimeoutSeconds), cancellationToken).ConfigureAwait(false);
            return ReadinessError(observed);
        }
        catch (TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = workflow.Current;
            var failure = ReadinessError(current);
            if (failure is not null) return failure;
            if (HasCurrentReadiness(current)) return null;
            if (!options.ResetOnStartupTimeout)
                return CreateReadinessFailure(current, true, "Initial UnitTest readiness timed out. No Reset was requested.");
        }

        WriteProgress(progress, "Initial UnitTest readiness timed out. Attempting one explicitly enabled recovery Reset; a timeout does not guarantee a stable PLC state.");
        // Re-evaluate after notifying the caller; readiness may have arrived at the deadline.
        cancellationToken.ThrowIfCancellationRequested();
        var beforeReset = workflow.Current;
        var readinessError = ReadinessError(beforeReset);
        if (readinessError is not null) return readinessError;
        if (HasCurrentReadiness(beforeReset)) return null;
        var reset = await workflow.ResetUnitTestAsync(
            Policy(timeouts.UnitTestCommand), cancellationToken).ConfigureAwait(false);
        if (!reset.Result.IsSuccessful) return FromOperation(reset.Result, reset.Publication);

        try
        {
            var observed = await WaitForReadinessAsync(
                timeouts.UnitTestCommandReaction, cancellationToken).ConfigureAwait(false);
            return ReadinessError(observed);
        }
        catch (TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = workflow.Current;
            var failure = ReadinessError(current);
            if (failure is not null) return failure;
            if (HasCurrentReadiness(current)) return null;
            return CreateReadinessFailure(current, true,
                "UnitTest readiness timed out after the single recovery Reset. No further Reset will be requested.");
        }
    }

    private Task<RunWorkflowPublication> WaitForReadinessAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => WaitForPublicationAsync(
            publication => HasReadinessError(publication) || HasCurrentReadiness(publication), timeout, cancellationToken);

    private static bool HasCurrentReadiness(RunWorkflowPublication publication)
        => publication.Status.UnitTestCommunicationContextActive && publication.Status.MonitoringActive
            && IsIdleAndReady(publication.Status.LifecycleObservation);

    private static bool HasReadinessError(RunWorkflowPublication publication)
        => publication.State == RunWorkflowState.Error
            || publication.Status.LifecycleObservation is { ConfigError: true } or { Error: true }
                or { ExecutionStatus: UnitTestExecutionStatus.Error } or { State: UnitTestLifecycleState.Error };

    private CliFullRunResult? ReadinessError(RunWorkflowPublication publication)
        => HasReadinessError(publication)
            ? CreateReadinessFailure(publication, false,
                "UnitTest readiness failed: the workflow or RunControl reports an error. No recovery Reset will be requested.")
            : null;

    private CliFullRunResult CreateReadinessFailure(RunWorkflowPublication publication, bool timedOut, string reason)
    {
        var observation = publication.Status.LifecycleObservation;
        var detail = observation is null ? "No RunControl observation available."
            : $"Last RunControl observation: State={observation.State}, ExecutionStatus={observation.ExecutionStatus}, "
                + $"Ready={observation.Ready}, Running={observation.Running}, ConfigError={observation.ConfigError}, Error={observation.Error}.";
        var message = $"{reason} {detail} Check PLC startup/reset handling and the preceding diagnostics.";
        var diagnostic = new Diagnostic(
            timedOut ? "Cli.Run.UnitTestReadyTimeout" : "Cli.Run.UnitTestReadyFailed",
            DiagnosticCategory.Workflow, Microsoft.Extensions.Logging.LogLevel.Error,
            DiagnosticSource.CliApplication, DiagnosticPhase.RunStart, message,
            new DiagnosticCorrelation(), timeProvider.GetUtcNow());
        return new CliFullRunResult(timedOut ? CliFullRunState.TimedOut : CliFullRunState.Failed,
            publication, null, publication.Diagnostics.Append(diagnostic).ToArray(), message);
    }

    private async Task<RunWorkflowPublication> WaitForPublicationAsync(
        Func<RunWorkflowPublication, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        bool Matches(RunWorkflowPublication publication) => predicate(publication);

        var current = workflow.Current;
        if (Matches(current))
        {
            return current;
        }

        ThrowIfWorkflowFailed(current);

        var completion = new TaskCompletionSource<RunWorkflowPublication>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPublication(object? sender, RunWorkflowPublicationEventArgs args)
        {
            if (Matches(args.Publication))
            {
                completion.TrySetResult(args.Publication);
            }
            else if (args.Publication.State == RunWorkflowState.Error)
            {
                completion.TrySetException(new InvalidOperationException(
                    "The shared workflow entered an error state while the CLI was waiting for observed state."));
            }
        }

        workflow.PublicationEventHandler += OnPublication;
        try
        {
            current = workflow.Current;
            if (Matches(current))
            {
                return current;
            }

            ThrowIfWorkflowFailed(current);
            return await completion.Task
                .WaitAsync(timeout, timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            workflow.PublicationEventHandler -= OnPublication;
        }
    }

    private static void ThrowIfWorkflowFailed(RunWorkflowPublication publication)
    {
        if (publication.State == RunWorkflowState.Error)
        {
            throw new InvalidOperationException(
                publication.Diagnostics.LastOrDefault()?.Message
                ?? "The shared workflow entered an error state while the CLI was waiting for observed state.");
        }
    }

    private static ProjectTargetRef? ResolveProjectTarget(
        ProjectTargetId? configuredId,
        ProjectTargetDiscoveryResult? discovery)
    {
        if (configuredId is null || discovery is null)
        {
            return null;
        }

        return discovery.Candidates.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Id.Value,
                configuredId.Value.Value,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsIdleAndReady(UnitTestRunControlObservation? observation)
    {
        return observation is
        {
            State: UnitTestLifecycleState.Idle,
            ExecutionStatus: UnitTestExecutionStatus.Idle,
            Ready: true,
            Running: false
        };
    }

    private static void WriteProgress(Action<string>? progress, string message)
    {
        progress?.Invoke(message);
    }

    private static SecureString CreateReadOnlyPassword(string value)
    {
        var password = new SecureString();
        foreach (var character in value)
        {
            password.AppendChar(character);
        }

        password.MakeReadOnly();
        return password;
    }

    private static WorkflowActionPolicy Policy(TimeSpan timeout) => new(timeout);

    private CliFullRunResult FromOperation(
        OperationResult result,
        RunWorkflowPublication publication)
    {
        return FromOperation(result.State, result.Message, publication);
    }

    private CliFullRunResult FromOperation<T>(
        OperationResult<T> result,
        RunWorkflowPublication publication)
    {
        return FromOperation(result.State, result.Message, publication);
    }

    private CliFullRunResult FromOperation(
        OperationResultState resultState,
        string? message,
        RunWorkflowPublication publication)
    {
        var state = resultState switch
        {
            OperationResultState.Cancelled => CliFullRunState.Cancelled,
            OperationResultState.TimedOut => CliFullRunState.TimedOut,
            OperationResultState.NotSupported => CliFullRunState.EnvironmentUnavailable,
            _ => CliFullRunState.Failed
        };
        return CreateResult(state, publication, message);
    }

    private CliFullRunResult CreateEventBufferStartBlockedResult(
        RunWorkflowPublication publication,
        string gateMessage)
    {
        var message = $"{gateMessage} The CLI does not reset the EventBuffer automatically; "
            + "reset it in the WPF application or restart the PLC before retrying.";
        var diagnostic = new Diagnostic(
            "Cli.Run.EventBufferHasLostEvents",
            DiagnosticCategory.Workflow,
            Microsoft.Extensions.Logging.LogLevel.Error,
            DiagnosticSource.CliApplication,
            DiagnosticPhase.RunStart,
            message,
            new DiagnosticCorrelation(),
            timeProvider.GetUtcNow());

        return new CliFullRunResult(
            CliFullRunState.Failed,
            publication,
            null,
            publication.Diagnostics.Append(diagnostic).ToArray(),
            message);
    }

    private CliFullRunResult CreateResult(
        CliFullRunState state,
        RunWorkflowPublication? publication,
        string? message,
        string? outputPath = null)
    {
        return new CliFullRunResult(
            state,
            publication,
            null,
            publication?.Diagnostics ?? [],
            message,
            outputPath);
    }
}
