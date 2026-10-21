using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Analysis;
using PLCSIM.UnitTest.Core.Diagnostics;

namespace PLCSIM.UnitTest.Core.Workflow;

/// <summary>
/// Shared Siemens-independent workflow skeleton used by WPF and CLI.
/// </summary>
public sealed class RunWorkflowService
{
    private readonly RunSetupValidator setupValidator;
    private readonly CoreServiceContext coreContext;
    private readonly ILogger<RunWorkflowService> logger;
    private readonly TestRunMonitor testRunMonitor;
    private readonly EventStreamProcessor eventStreamProcessor;
    private readonly AccessHealthMonitor accessHealthMonitor;
    private readonly WorkflowDiagnosticReporter diagnosticReporter;
    private readonly EvidenceAnalysisService evidenceAnalysis;
    private readonly WorkflowActionGateEvaluator gateEvaluator = new();

    private AccessProfileSession? boundAccessProfileSession;
    private bool ownsBoundAccessProfileSession;
    private RunSetup? setup;
    private ApplicationOperationTimeoutPolicy applicationOperationTimeouts =
        RunConfigurationDefaults.CreateDefaultApplicationOperationTimeoutPolicy();
    private ProjectTargetDiscoveryResult? projectTargetDiscovery;
    private RuntimeTargetDiscoveryResult? runtimeTargetDiscovery;
    private PlcProjectContext? project;
    private ProjectSetupState projectSetupState = ProjectSetupState.NotEstablished;
    private bool projectAccessActive;
    private PlcRuntimeContext? runtime;
    private ProjectTargetRef? projectTarget;
    private RuntimeTargetRef? runtimeTarget;
    private CommunicationSession? communicationSession;
    private UnitTestContext? unitTestContext;
    private UnitTestRunControlObservation? lifecycleObservation;
    private EventStreamState? eventStreamState;
    private bool monitoringActive;
    private readonly object plcStateSync = new();
    private PlcState currentPlcState = new(
        RuntimeInstanceState.Unavailable,
        PlcOperatingState.Unavailable,
        Array.Empty<RuntimeTargetRef>(),
        ReadyForTransfer: false,
        ReadyForExecution: false);
    private long plcStateObservationSequence;
    private PlcCommandObservationMonitor? plcCommandObservationMonitor;
    private readonly object activeOperationSync = new();
    private ActiveWorkflowOperation? activeWorkflowOperation;
    private int sessionReleaseDepth;
    private bool operationInProgress;
    private long publicationSequence;
    private bool communicationAutoPrepareInProgress;
    private bool communicationAutoPreparePending;
    private int observedPlcUnavailableReleaseInProgress;
    private readonly object activityCorrelationSync = new();
    private PlcState? lastCapturedPlcState;
    private LifecycleObservationId? pendingPlcStartActivity;

    public RunWorkflowService(
        RunSetupValidator setupValidator,
        CoreServiceContext coreContext)
        : this(setupValidator, coreContext, new EvidenceAnalysisService(coreContext))
    {
    }

    public RunWorkflowService(
        RunSetupValidator setupValidator,
        CoreServiceContext coreContext,
        EvidenceAnalysisService evidenceAnalysis)
    {
        this.setupValidator = setupValidator;
        this.coreContext = coreContext;
        this.evidenceAnalysis = evidenceAnalysis ?? throw new ArgumentNullException(nameof(evidenceAnalysis));
        this.logger = coreContext.LoggerFactory.CreateLogger<RunWorkflowService>() ?? NullLogger<RunWorkflowService>.Instance;
        diagnosticReporter = new WorkflowDiagnosticReporter(
            coreContext,
            this.logger,
            OnDiagnosticPublished,
            evidenceAnalysis.TraceSink);
        testRunMonitor = new TestRunMonitor(coreContext, evidenceAnalysis.TraceSink);
        eventStreamProcessor = new EventStreamProcessor(coreContext, evidenceAnalysis.TraceSink);
        accessHealthMonitor = new AccessHealthMonitor(
            coreContext,
            evidenceAnalysis.TraceSink,
            evidenceAnalysis.TraceContext);
        testRunMonitor.DiagnosticEventHandler += (_, args) =>
            diagnosticReporter.Add(ApplyActivityCorrelation(args.Diagnostic));
        testRunMonitor.MonitorUpdateEventHandler += (_, args) => PublishMonitorUpdate(args.Update);
        accessHealthMonitor.UpdateEventHandler += (_, args) => ExecuteProfileCallback(
            AccessServiceKind.Profile,
            "AccessHealthChanged",
            DiagnosticPhase.ExecutionMonitoring,
            invalidateRunObservation: true,
            () => ApplyAccessHealthUpdate(args));

        Current = CreatePublication(RunWorkflowState.NotConfigured);
    }

    public RunWorkflowPublication Current { get; private set; }

    public EvidenceAnalysisService EvidenceAnalysis => evidenceAnalysis;

    public event EventHandler<RunWorkflowPublicationEventArgs>? PublicationEventHandler;

    private IAccessProfileSession? profile => boundAccessProfileSession?.Profile;

    private ProjectOwnershipMode projectOwnershipMode => boundAccessProfileSession?.ProjectOwnershipMode
        ?? ProjectOwnershipMode.ApplicationManaged;

    public void ClearDiagnostics()
    {
        coreContext.Diagnostics.Controller.Clear(coreContext.TimeProvider.GetUtcNow());
        Current = CreatePublication(Current.State, Current.ActiveOperation);
        PublicationEventHandler?.Invoke(this, new RunWorkflowPublicationEventArgs(Current));
    }

    public async Task<WorkflowActionResult> BindAccessProfileSessionAsync(
        AccessProfileSession session,
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        if (ReferenceEquals(boundAccessProfileSession, session))
        {
            logger.LogDebug(
                "Access profile session is already bound. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}",
                session.SelectedProfile.ProfileId,
                session.SelectedProfile.Version);
            return new WorkflowActionResult(
                WorkflowAction.LoadAccessProfile,
                OperationResult.Success("Access profile session is already bound."),
                Current);
        }

        return await ExecuteAsync(
            WorkflowAction.LoadAccessProfile,
            "AccessProfileBind",
            policy,
            cancellationToken,
            async operationToken =>
        {
            var validation = ValidateAccessProfileSession(session);
            if (!validation.IsSuccessful)
            {
                return validation;
            }

            if (profile is not null)
            {
                await CleanupLoadedProfileAsync(operationToken);
            }

            ClearPreparedContexts(clearSetup: false);
            projectTargetDiscovery = null;
            runtimeTargetDiscovery = null;
            BindLoadedAccessProfileSession(session, ownsSession: false);

            logger.LogInformation(
                "Access profile session bound. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}",
                session.SelectedProfile.ProfileId,
                session.SelectedProfile.Version);
            return OperationResult.Success("Access profile session bound.");
        }, RunWorkflowState.AccessProfileLoaded);
    }

    public async Task<WorkflowActionResult> DiscoverProjectTargetsAsync(
        AccessProfileConfig config,
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var gate = RequireAvailableActionGate(WorkflowAction.DiscoverProjectTargets);
        if (gate is not null)
        {
            return gate;
        }

        return await ExecuteAsync(
            WorkflowAction.DiscoverProjectTargets,
            "ProjectTargetDiscovery",
            policy,
            cancellationToken,
            async operationToken =>
        {
            var result = await ExecuteProfileCallAsync(
                AccessServiceKind.Project,
                "DiscoverProjectTargets",
                DiagnosticPhase.ProjectPreparation,
                operationToken,
                () => profile!.Project.DiscoverProjectTargetsAsync(
                    CreateProjectRequest(config),
                    operationToken));

            diagnosticReporter.AddRange(result.Value?.Diagnostics ?? Array.Empty<Diagnostic>());
            if (result.Value is not null)
            {
                projectTargetDiscovery = result.Value;
            }

            if (result.IsSuccessful)
            {
                projectAccessActive = profile!.Project.CurrentProject is not null;
            }

            return result.IsSuccessful
                ? OperationResult.Success(result.Message)
                : OperationResult.Failure(result.Message ?? "Project target discovery failed.");
        }, RunWorkflowState.DiscoveryReady);
    }

    public async Task<WorkflowActionResult> DiscoverRuntimeTargetsAsync(
        AccessProfileConfig config,
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var gate = RequireAvailableActionGate(WorkflowAction.DiscoverRuntimeTargets);
        if (gate is not null)
        {
            return gate;
        }

        return await ExecuteAsync(
            WorkflowAction.DiscoverRuntimeTargets,
            "RuntimeTargetDiscovery",
            policy,
            cancellationToken,
            async operationToken =>
        {
            var result = await ExecuteProfileCallAsync(
                AccessServiceKind.PlcControl,
                "DiscoverRuntimeTargets",
                DiagnosticPhase.RuntimeProvisioning,
                operationToken,
                () => profile!.PlcControl.DiscoverRuntimeTargetsAsync(
                    new PlcRuntimeDiscoveryRequest(config),
                    operationToken));

            diagnosticReporter.AddRange(result.Value?.Diagnostics ?? Array.Empty<Diagnostic>());
            if (result.Value is not null)
            {
                runtimeTargetDiscovery = result.Value;
            }

            return result.IsSuccessful
                ? OperationResult.Success(result.Message)
                : OperationResult.Failure(result.Message ?? "Runtime target discovery failed.");
        }, RunWorkflowState.DiscoveryReady);
    }

    public async Task<WorkflowActionResult> UnloadAccessProfileAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var gate = RequireAvailableActionGate(WorkflowAction.UnloadAccessProfile);
        if (gate is not null)
        {
            return gate;
        }

        return await ExecuteAsync(
            WorkflowAction.UnloadAccessProfile,
            "AccessProfileUnload",
            policy,
            cancellationToken,
            async operationToken =>
        {
            try
            {
                var cleanupSucceeded = await CleanupLoadedProfileAsync(operationToken);
                return cleanupSucceeded
                    ? OperationResult.Success("Access profile unloaded.")
                    : OperationResult.Failure("Access profile unload finished with diagnostics.");
            }
            finally
            {
                ClearPreparedContexts(clearSetup: true);
            }
        }, RunWorkflowState.NotConfigured, RunWorkflowState.NotConfigured);
    }

    public OperationResult Configure(
        RunSetup setup,
        ApplicationOperationTimeoutPolicy? operationTimeouts = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        applicationOperationTimeouts = operationTimeouts
            ?? RunConfigurationDefaults.CreateDefaultApplicationOperationTimeoutPolicy();

        logger.LogInformation(
            "Configuring run workflow. AccessProfileId={AccessProfileId}, ProjectSource={ProjectSource}, RuntimeProvisioningMode={RuntimeProvisioningMode}, ConfigurationFingerprint={ConfigurationFingerprint}, ConfigurationPayloadLength={ConfigurationPayloadLength}, ProjectTargetId={ProjectTargetId}, ProjectTargetDisplayName={ProjectTargetDisplayName}, ProjectTargetTechnicalName={ProjectTargetTechnicalName}, RuntimeTargetId={RuntimeTargetId}, RuntimeTargetDisplayName={RuntimeTargetDisplayName}, RuntimeTargetTechnicalName={RuntimeTargetTechnicalName}",
            setup.AccessProfile.ProfileId,
            setup.AccessProfile.ProjectSource,
            setup.RuntimeProvisioningMode,
            CreateConfigurationFingerprint(setup.AccessProfile),
            setup.AccessProfile.Values.Json.Length,
            setup.ProjectTarget?.Id.Value,
            setup.ProjectTarget?.DisplayName,
            setup.ProjectTarget?.TechnicalName,
            setup.RuntimeTarget?.Id.Value,
            setup.RuntimeTarget?.DisplayName,
            setup.RuntimeTarget?.TechnicalName);
        var validation = setupValidator.Validate(setup, projectOwnershipMode);
        diagnosticReporter.AddRange(validation.Diagnostics);

        if (!validation.IsValid || validation.Setup is null)
        {
            logger.LogWarning(
                "Run workflow configuration failed. DiagnosticCount={DiagnosticCount}, DiagnosticDetails={DiagnosticDetails}",
                validation.Diagnostics.Count,
                FormatDiagnosticDetails(validation.Diagnostics));
            Publish(RunWorkflowState.Error);
            return OperationResult.Failure("Run setup is invalid.");
        }

        if (profile is null || boundAccessProfileSession is null)
        {
            diagnosticReporter.Add(diagnosticReporter.Create(
                "Workflow.Configure.AccessProfileNotLoaded",
                DiagnosticCategory.Workflow,
                LogLevel.Error,
                "Access profile must be loaded before final run setup is configured.",
                phase: DiagnosticPhase.Configuration));
            Publish(RunWorkflowState.Error);
            return OperationResult.Failure("Access profile is not loaded.");
        }

        if (!string.Equals(boundAccessProfileSession.ProfileId, validation.Setup.AccessProfile.ProfileId, StringComparison.OrdinalIgnoreCase))
        {
            diagnosticReporter.Add(diagnosticReporter.Create(
                "Workflow.Configure.AccessProfileMismatch",
                DiagnosticCategory.Workflow,
                LogLevel.Error,
                "Configured access profile does not match the loaded access profile.",
                phase: DiagnosticPhase.Configuration));
            Publish(RunWorkflowState.Error);
            return OperationResult.Failure("Configured access profile does not match the loaded access profile.");
        }

        if (!string.Equals(boundAccessProfileSession.ProfileVersion, validation.Setup.AccessProfile.ProfileVersion, StringComparison.OrdinalIgnoreCase))
        {
            diagnosticReporter.Add(diagnosticReporter.Create(
                "Workflow.Configure.AccessProfileVersionMismatch",
                DiagnosticCategory.Workflow,
                LogLevel.Error,
                "Configured access profile version does not match the loaded access profile version.",
                phase: DiagnosticPhase.Configuration));
            Publish(RunWorkflowState.Error);
            return OperationResult.Failure("Configured access profile version does not match the loaded access profile version.");
        }

        if (projectOwnershipMode == ProjectOwnershipMode.ApplicationManaged
            && validation.Setup.ProjectTarget is null)
        {
            diagnosticReporter.Add(diagnosticReporter.Create(
                "Workflow.Configure.ProjectTargetMissing",
                DiagnosticCategory.Workflow,
                LogLevel.Error,
                "Project target must be selected before final run setup is configured.",
                phase: DiagnosticPhase.Configuration));
            Publish(RunWorkflowState.Error);
            return OperationResult.Failure("Project target is not selected.");
        }

        this.setup = validation.Setup;
        projectTarget = validation.Setup.ProjectTarget;
        runtimeTarget = validation.Setup.RuntimeTarget;
        ClearPreparedContexts(clearSetup: false);
        projectAccessActive = profile?.Project.CurrentProject is not null;
        projectSetupState = projectOwnershipMode == ProjectOwnershipMode.ExternallyManaged
            ? ProjectSetupState.ExternalManagementAccepted
            : ProjectSetupState.NotEstablished;

        Publish(RunWorkflowState.Configured);
        logger.LogInformation(
            "Run workflow configured. AccessProfileId={AccessProfileId}, ProjectSource={ProjectSource}, RuntimeProvisioningMode={RuntimeProvisioningMode}, ConfigurationFingerprint={ConfigurationFingerprint}, ConfigurationPayloadLength={ConfigurationPayloadLength}, ProjectTargetId={ProjectTargetId}, ProjectTargetDisplayName={ProjectTargetDisplayName}, ProjectTargetTechnicalName={ProjectTargetTechnicalName}, RuntimeTargetId={RuntimeTargetId}, RuntimeTargetDisplayName={RuntimeTargetDisplayName}, RuntimeTargetTechnicalName={RuntimeTargetTechnicalName}",
            validation.Setup.AccessProfile.ProfileId,
            validation.Setup.AccessProfile.ProjectSource,
            validation.Setup.RuntimeProvisioningMode,
            CreateConfigurationFingerprint(validation.Setup.AccessProfile),
            validation.Setup.AccessProfile.Values.Json.Length,
            validation.Setup.ProjectTarget?.Id.Value,
            validation.Setup.ProjectTarget?.DisplayName,
            validation.Setup.ProjectTarget?.TechnicalName,
            validation.Setup.RuntimeTarget?.Id.Value,
            validation.Setup.RuntimeTarget?.DisplayName,
            validation.Setup.RuntimeTarget?.TechnicalName);
        return OperationResult.Success("Run setup configured.");
    }

    private static string CreateConfigurationFingerprint(AccessProfileConfig configuration)
    {
        var bytes = Encoding.UTF8.GetBytes(configuration.Values.Json);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public async Task<WorkflowActionResult> PrepareProjectAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var gate = RequireAvailableActionGate(WorkflowAction.PrepareProject);
        if (gate is not null)
        {
            return gate;
        }

        return await ExecuteAsync(
            WorkflowAction.PrepareProject,
            "ProjectPreparation",
            policy,
            cancellationToken,
            async operationToken =>
        {
            logger.LogInformation(
                "Preparing PLC project. AccessProfileId={AccessProfileId}, ProjectSource={ProjectSource}, ProjectTargetId={ProjectTargetId}, ProjectTargetDisplayName={ProjectTargetDisplayName}, ProjectTargetTechnicalName={ProjectTargetTechnicalName}",
                setup!.AccessProfile.ProfileId,
                setup.AccessProfile.ProjectSource,
                projectTarget?.Id.Value,
                projectTarget?.DisplayName,
                projectTarget?.TechnicalName);
            var result = await ExecuteProfileCallAsync(
                AccessServiceKind.Project,
                "PrepareProject",
                DiagnosticPhase.ProjectPreparation,
                operationToken,
                () => profile!.Project.PrepareProjectAsync(
                    CreateProjectRequest(setup.AccessProfile),
                    operationToken));
            if (result.IsSuccessful && result.Value is not null)
            {
                project = result.Value;
                projectAccessActive = true;
                projectSetupState = ProjectSetupState.ManagedProjectPrepared;
                logger.LogInformation(
                    "PLC project prepared. ProjectContextId={ProjectContextId}, ProjectContextDisplayName={ProjectContextDisplayName}, ProjectSourcePath={ProjectSourcePath}",
                    project.Id.Value,
                    project.DisplayName,
                    project.SourcePath);
            }

            return result.IsSuccessful
                ? OperationResult.Success(result.Message)
                : OperationResult.Failure(result.Message ?? "Project preparation failed.");
        }, RunWorkflowState.PreparingProject);
    }

    public async Task<WorkflowActionResult> PrepareRuntimeAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var gate = RequireAvailableActionGate(WorkflowAction.PrepareRuntime);
        if (gate is not null)
        {
            return gate;
        }

        return await ExecuteAsync(
            WorkflowAction.PrepareRuntime,
            "RuntimeProvisioning",
            policy,
            cancellationToken,
            async operationToken =>
        {
            logger.LogInformation(
                "Preparing PLC runtime. AccessProfileId={AccessProfileId}, RuntimeTargetId={RuntimeTargetId}, RuntimeTargetDisplayName={RuntimeTargetDisplayName}, RuntimeTargetTechnicalName={RuntimeTargetTechnicalName}",
                setup!.AccessProfile.ProfileId,
                runtimeTarget?.Id.Value,
                runtimeTarget?.DisplayName,
                runtimeTarget?.TechnicalName);
            var result = await ExecuteProfileCallAsync(
                AccessServiceKind.PlcControl,
                "PrepareRuntime",
                DiagnosticPhase.RuntimeProvisioning,
                operationToken,
                () => profile!.PlcControl.PrepareRuntimeAsync(
                    new PlcRuntimePrepareRequest(
                        setup!.AccessProfile,
                        setup.RuntimeProvisioningMode,
                        runtimeTarget),
                    operationToken));
            if (result.Value is not null)
            {
                runtime = result.Value.Runtime;
                runtimeTarget = result.Value.RuntimeTarget;
                logger.LogInformation(
                    "PLC runtime prepared. RuntimeContextId={RuntimeContextId}, RuntimeContextDisplayName={RuntimeContextDisplayName}, RuntimeName={RuntimeName}",
                    runtime.Id.Value,
                    runtime.DisplayName,
                    runtime.RuntimeName);
            }

            return result.IsSuccessful
                ? OperationResult.Success(result.Message)
                : OperationResult.Failure(result.Message ?? "Runtime preparation failed.");
        }, RunWorkflowState.RuntimePrepared);
    }

    public async Task<WorkflowActionResult> ReconnectRuntimeAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var gate = RequireAvailableActionGate(WorkflowAction.ReconnectRuntime);
        if (gate is not null)
        {
            return gate;
        }

        return await ExecuteAsync(
            WorkflowAction.ReconnectRuntime,
            "RuntimeReconnection",
            policy,
            cancellationToken,
            async operationToken =>
        {
            InvalidateActiveRunForRuntimeReconnect();
            logger.LogInformation(
                "Reconnecting PLC runtime. RuntimeContextId={RuntimeContextId}, RuntimeTargetId={RuntimeTargetId}, RuntimeTargetDisplayName={RuntimeTargetDisplayName}, RuntimeTargetTechnicalName={RuntimeTargetTechnicalName}",
                runtime!.Id.Value,
                runtimeTarget!.Id.Value,
                runtimeTarget.DisplayName,
                runtimeTarget.TechnicalName);
            var result = await ExecuteProfileCallAsync(
                AccessServiceKind.PlcControl,
                "ReconnectRuntime",
                DiagnosticPhase.RuntimeProvisioning,
                operationToken,
                () => profile!.ReconnectRuntimeAsync(
                    new PlcRuntimeReconnectRequest(
                        setup!.AccessProfile,
                        setup.RuntimeProvisioningMode,
                        runtime,
                        runtimeTarget),
                    operationToken));
            if (result.State != OperationResultState.Cancelled)
            {
                await ResetUnitTestCommunicationContextAfterProfileReconnectAsync();
            }

            if (result.Value is not null)
            {
                runtime = result.Value.Runtime;
                runtimeTarget = result.Value.RuntimeTarget;
                logger.LogInformation(
                    "PLC runtime reconnected. RuntimeContextId={RuntimeContextId}, RuntimeContextDisplayName={RuntimeContextDisplayName}, RuntimeName={RuntimeName}",
                    runtime.Id.Value,
                    runtime.DisplayName,
                    runtime.RuntimeName);
            }

            if (result.IsSuccessful)
            {
                communicationAutoPreparePending = GetCurrentPlcState().ReadyForExecution;
                Publish(RunWorkflowState.RuntimePrepared);
            }
            else if (result.State != OperationResultState.Cancelled)
            {
                communicationAutoPreparePending = false;
                Publish(RunWorkflowState.RuntimePrepared);
            }

            return result.IsSuccessful
                ? OperationResult.Success(result.Message)
                : OperationResult.Failure(result.Message ?? "Runtime reconnect failed.");
        }, RunWorkflowState.RuntimePrepared);
    }

    public async Task<WorkflowActionResult> TransferProjectAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        return await TransferProjectAsync(
            policy,
            new WorkflowActionPolicy(RunConfigurationDefaults.DefaultProjectReleaseTimeout),
            cancellationToken);
    }

    public async Task<SafetyAuthorizationRequirementWorkflowActionResult> GetSafetyProgramAuthorizationRequirementAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        const WorkflowAction action = WorkflowAction.DiscoverSafetyProgramAuthorizationRequirement;
        var gate = gateEvaluator.Evaluate(CreateStatusSnapshot(Current.State, Current.ActiveOperation), action);
        if (!gate.IsAvailable)
        {
            return BlockedSafetyAuthorizationRequirement(action, gate);
        }

        var result = await ExecuteTypedProjectActionAsync(
            action,
            "SafetyAuthorizationRequirementDiscovery",
            policy,
            cancellationToken,
            async operationToken =>
            {
                var requirement = await ExecuteProfileCallAsync(
                    AccessServiceKind.Project,
                    "GetSafetyProgramAuthorizationRequirement",
                    DiagnosticPhase.ProjectTransfer,
                    operationToken,
                    () => profile!.Project.GetSafetyProgramAuthorizationRequirementAsync(
                        project!,
                        projectTarget!,
                        operationToken));
                return requirement.IsSuccessful && requirement.Value is null
                    ? OperationResult<SafetyProgramAuthorizationRequirementResult>.Failure(
                        "Safety-program authorization requirement discovery returned no result.")
                    : requirement;
            });
        return new SafetyAuthorizationRequirementWorkflowActionResult(action, result, Current);
    }

    public async Task<SafetyAuthorizationWorkflowActionResult> AuthorizeSafetyProgramAsync(
        SecureString password,
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);

        const WorkflowAction action = WorkflowAction.AuthorizeSafetyProgram;
        var gate = gateEvaluator.Evaluate(CreateStatusSnapshot(Current.State, Current.ActiveOperation), action);
        if (!gate.IsAvailable)
        {
            return BlockedSafetyAuthorization(action, gate);
        }

        var result = await ExecuteTypedProjectActionAsync(
            action,
            "SafetyProgramAuthorization",
            policy,
            cancellationToken,
            async operationToken =>
            {
                var authorization = await ExecuteProfileCallAsync(
                    AccessServiceKind.Project,
                    "AuthorizeSafetyProgram",
                    DiagnosticPhase.ProjectTransfer,
                    operationToken,
                    () => profile!.Project.AuthorizeSafetyProgramAsync(
                        project!,
                        projectTarget!,
                        password,
                        operationToken));
                return authorization.IsSuccessful && authorization.Value?.Authorized != true
                    ? OperationResult<SafetyProgramAuthorizationResult>.Failure(
                        "Safety-program authorization was not accepted.")
                    : authorization;
            });
        return new SafetyAuthorizationWorkflowActionResult(action, result, Current);
    }

    public async Task<WorkflowActionResult> TransferProjectAsync(
        WorkflowActionPolicy policy,
        WorkflowActionPolicy projectReleasePolicy,
        CancellationToken cancellationToken)
    {
        var gate = RequireAvailableActionGate(WorkflowAction.TransferProject);
        if (gate is not null)
        {
            return gate;
        }

        return await ExecuteAsync(
            WorkflowAction.TransferProject,
            "ProjectTransfer",
            policy,
            cancellationToken,
            async operationToken =>
        {
            logger.LogInformation(
                "Building and transferring PLC project. ProjectContextId={ProjectContextId}, RuntimeContextId={RuntimeContextId}, ProjectTargetId={ProjectTargetId}, RuntimeTargetId={RuntimeTargetId}",
                project?.Id.Value,
                runtime?.Id.Value,
                projectTarget?.Id.Value,
                runtimeTarget?.Id.Value);
            var build = await ExecuteProfileCallAsync(
                AccessServiceKind.Project,
                "BuildProject",
                DiagnosticPhase.ProjectTransfer,
                operationToken,
                () => profile!.Project.BuildAsync(project!, projectTarget!, operationToken));
            if (!build.IsSuccessful || build.Value?.Succeeded == false)
            {
                return OperationResult.Failure(build.Message ?? "Project build failed.");
            }

            var transfer = await ExecuteProfileCallAsync(
                AccessServiceKind.Project,
                "TransferProject",
                DiagnosticPhase.ProjectTransfer,
                operationToken,
                () => profile!.Project.TransferToAsync(project!, projectTarget!, runtimeTarget!, operationToken));
            if (!transfer.IsSuccessful)
            {
                return OperationResult.Failure(transfer.Message ?? "Project transfer failed.");
            }

            projectSetupState = ProjectSetupState.ManagedTransferCompleted;
            await ReleaseManagedProjectAccessAfterTransferAsync(projectReleasePolicy);
            return OperationResult.Success(transfer.Message);
        }, RunWorkflowState.ProjectTransferred);
    }

    public async Task<WorkflowActionResult> StartPlcAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var gate = RequireAvailableActionGate(WorkflowAction.StartPlc);
        if (gate is not null)
        {
            return gate;
        }

        return await ExecuteAsync(
            WorkflowAction.StartPlc,
            "RuntimeStartup",
            policy,
            cancellationToken,
            async operationToken =>
        {
            logger.LogInformation(
                "Starting PLC runtime. RuntimeContextId={RuntimeContextId}, RuntimeTargetId={RuntimeTargetId}, RuntimeTargetDisplayName={RuntimeTargetDisplayName}, RuntimeTargetTechnicalName={RuntimeTargetTechnicalName}",
                runtime?.Id.Value,
                runtimeTarget?.Id.Value,
                runtimeTarget?.DisplayName,
                runtimeTarget?.TechnicalName);
            var observationBaseline = CapturePlcStateObservationSequence();
            var result = await ExecuteProfileCallAsync(
                AccessServiceKind.PlcControl,
                "StartPlc",
                DiagnosticPhase.RuntimeStartup,
                operationToken,
                () => profile!.PlcControl.StartAsync(runtime!, runtimeTarget!, operationToken));
            if (!result.IsSuccessful)
            {
                return OperationResult.Failure(result.Message ?? "PLC start failed.");
            }

            eventStreamProcessor.Reset();
            BeginPlcCommandObservationMonitoring(WorkflowAction.StartPlc, observationBaseline);
            return OperationResult.Success(result.Message ?? "PLC start command accepted.");
        }, successState: null);
    }

    public async Task<WorkflowActionResult> StopPlcAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var gate = RequireAvailableActionGate(WorkflowAction.StopPlc);
        if (gate is not null)
        {
            return gate;
        }

        return await ExecuteAsync(
            WorkflowAction.StopPlc,
            "RuntimeStop",
            policy,
            cancellationToken,
            async operationToken =>
        {
            var communicationReleased = await ReleaseUnitTestCommunicationCoreAsync(operationToken);
            var observationBaseline = CapturePlcStateObservationSequence();
            var result = await ExecuteProfileCallAsync(
                AccessServiceKind.PlcControl,
                "StopPlc",
                DiagnosticPhase.RuntimeStartup,
                operationToken,
                () => profile!.PlcControl.StopAsync(runtime!, runtimeTarget!, operationToken));
            if (!result.IsSuccessful)
            {
                return OperationResult.Failure(result.Message ?? "PLC stop failed.");
            }

            BeginPlcCommandObservationMonitoring(WorkflowAction.StopPlc, observationBaseline);
            if (!communicationReleased)
            {
                logger.LogWarning(
                    "PLC stop command was accepted after UnitTest communication release finished with diagnostics.");
            }

            return OperationResult.Success(result.Message ?? "PLC stop command accepted.");
        }, successState: null);
    }

    public async Task<WorkflowActionResult> PrepareUnitTestCommunicationAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var gate = RequireAvailableActionGate(WorkflowAction.PrepareUnitTestCommunication);
        if (gate is not null)
        {
            return gate;
        }

        return await ExecuteAsync(
            WorkflowAction.PrepareUnitTestCommunication,
            "CommunicationValidation",
            policy,
            cancellationToken,
            operationToken => PrepareUnitTestCommunicationCoreAsync(operationToken),
            RunWorkflowState.UnitTestCommunicationPrepared);
    }

    public Task<WorkflowActionResult> StartMonitoringAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        return StartMonitoringAsync(policy, cancellationToken, exceptionObserved: null);
    }

    private async Task<WorkflowActionResult> StartMonitoringAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken,
        Action? exceptionObserved)
    {
        var gate = RequireAvailableActionGate(WorkflowAction.StartMonitoring);
        if (gate is not null)
        {
            return gate;
        }

        return await ExecuteAsync(
            WorkflowAction.StartMonitoring,
            "ExecutionMonitoring",
            policy,
            cancellationToken,
            async operationToken =>
        {
            var monitorStart = testRunMonitor.StartMonitoring(new TestRunMonitoringContext(
                communicationSession!.SessionId,
                setup!.Execution.Timeouts,
                EventStreamSequenceBaseline: null,
                RunEpochBaseline: GetMonitoringRunEpochBaseline(),
                coreContext.TimeProvider.GetUtcNow()));
            if (!monitorStart.IsSuccessful)
            {
                return monitorStart;
            }

            var observationStart = eventStreamProcessor.StartObservation(new EventStreamObservationContext(
                communicationSession!.SessionId,
                SequenceBaseline: null,
                coreContext.TimeProvider.GetUtcNow()));
            if (!observationStart.IsSuccessful)
            {
                await testRunMonitor.StopMonitoringAsync(CancellationToken.None);
                return observationStart;
            }

            OperationResult<EventStreamState> result;
            try
            {
                result = await ExecuteProfileCallAsync(
                    AccessServiceKind.UnitTestEventStream,
                    "StartMonitoring",
                    DiagnosticPhase.ExecutionMonitoring,
                    operationToken,
                    () => profile!.UnitTestEventStream.StartMonitoringAsync(unitTestContext!, operationToken),
                    exceptionObserved);
            }
            catch (OperationCanceledException)
            {
                await testRunMonitor.StopMonitoringAsync(CancellationToken.None);
                eventStreamProcessor.CompleteObservation(
                    EventStreamCompletionReason.Cleanup,
                    coreContext.TimeProvider.GetUtcNow());
                throw;
            }

            monitoringActive = result.Value?.IsMonitoring == true;
            eventStreamState = result.Value;
            if (result.Value is not null)
            {
                testRunMonitor.ApplyEventStreamUpdate(new TestRunEventStreamUpdate(
                    result.Value,
                    CurrentRun: eventStreamProcessor.Current));
            }

            if (!result.IsSuccessful || !monitoringActive)
            {
                await testRunMonitor.StopMonitoringAsync(CancellationToken.None);
                eventStreamProcessor.CompleteObservation(
                    EventStreamCompletionReason.Cleanup,
                    coreContext.TimeProvider.GetUtcNow());
            }

            return result.IsSuccessful && monitoringActive
                ? OperationResult.Success(result.Message)
                : OperationResult.Failure(result.Message ?? "Event stream monitoring failed.");
        }, RunWorkflowState.Monitoring);
    }

    public async Task<WorkflowActionResult> StopMonitoringAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var gate = RequireAvailableActionGate(WorkflowAction.StopMonitoring);
        if (gate is not null)
        {
            return gate;
        }

        return await ExecuteAsync(
            WorkflowAction.StopMonitoring,
            "ExecutionMonitoring",
            policy,
            cancellationToken,
            async operationToken =>
        {
            var result = await ExecuteProfileCallAsync(
                AccessServiceKind.UnitTestEventStream,
                "StopMonitoring",
                DiagnosticPhase.ExecutionMonitoring,
                operationToken,
                () => profile!.UnitTestEventStream.StopMonitoringAsync(operationToken));
            if (result.IsSuccessful)
            {
                monitoringActive = false;
                eventStreamState = profile!.UnitTestEventStream.CurrentMonitoringState;
                await testRunMonitor.StopMonitoringAsync(operationToken);
                eventStreamProcessor.CompleteObservation(
                    EventStreamCompletionReason.Cleanup,
                    coreContext.TimeProvider.GetUtcNow());
            }

            return result;
        }, RunWorkflowState.UnitTestCommunicationPrepared);
    }

    public async Task<UnitTestWorkflowActionResult> StartUnitTestAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        return await ExecuteUnitTestCommandAsync(WorkflowAction.StartUnitTest, policy, cancellationToken,
            (context, token) => profile!.UnitTestLifecycle.StartAsync(context, token));
    }

    public async Task<UnitTestWorkflowActionResult> AbortUnitTestAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        return await ExecuteUnitTestCommandAsync(WorkflowAction.AbortUnitTest, policy, cancellationToken,
            (context, token) => profile!.UnitTestLifecycle.AbortAsync(context, token));
    }

    public async Task<UnitTestWorkflowActionResult> ResetUnitTestAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        return await ExecuteUnitTestCommandAsync(WorkflowAction.ResetUnitTest, policy, cancellationToken,
            (context, token) => profile!.UnitTestLifecycle.ResetAsync(context, token));
    }

    public async Task<EventBufferResetWorkflowActionResult> ResetEventBufferAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy.Timeout,
                "Workflow action timeout must be greater than zero.");
        }

        const WorkflowAction action = WorkflowAction.ResetEventBuffer;
        var gate = gateEvaluator.Evaluate(CreateStatusSnapshot(Current.State, Current.ActiveOperation), action);
        if (!gate.IsAvailable)
        {
            var message = string.Join(" ", gate.Reasons.Select(static reason => reason.Message));
            return new EventBufferResetWorkflowActionResult(
                action,
                OperationResult<EventBufferResetCommandResult>.Failure(message),
                Current);
        }

        var operationId = Guid.NewGuid().ToString("N");
        using var operation = TryBeginActiveWorkflowOperation(action, operationId, cancellationToken);
        if (operation is null)
        {
            return new EventBufferResetWorkflowActionResult(
                action,
                OperationResult<EventBufferResetCommandResult>.Failure(
                    "Another workflow operation or session release is already running."),
                Current);
        }

        operationInProgress = true;
        const string phase = "EventBufferReset";
        var lifecycleObservationId = BeginCommandActivity(
            UnitTestActivityCommandKind.EventBufferReset,
            operationId);
        logger.LogInformation(
            "EventBuffer reset started. Action={Action}, Phase={Phase}, OperationId={OperationId}",
            action,
            phase,
            operationId);
        Current = Current with
        {
            ActiveOperation = new ActiveOperation(operationId, action, phase, coreContext.TimeProvider.GetUtcNow(), null)
        };
        Publish(Current.State);

        OperationResult<EventBufferResetCommandResult> result;
        using var operationCancellation = new CoreOperationCancellationScope(
            coreContext.TimeProvider,
            policy.Timeout,
            operation.Token);
        try
        {
            operationCancellation.Token.ThrowIfCancellationRequested();
            testRunMonitor.TimeoutActiveObservation(
                "Test run evidence was discarded by an explicit EventBuffer reset.");
            result = await ExecuteProfileCallAsync(
                AccessServiceKind.UnitTestEventStream,
                action.ToString(),
                DiagnosticPhase.ExecutionMonitoring,
                operationCancellation.Token,
                () => profile!.UnitTestEventStream.ResetEventBufferAsync(
                    unitTestContext!,
                    operationCancellation.Token));
            result = NormalizeWorkflowResult(action, phase, result, operationCancellation);
            CompleteCommandActivity(
                UnitTestActivityCommandKind.EventBufferReset,
                lifecycleObservationId,
                operationId,
                result.State,
                result.Value?.Accepted,
                result.Message);
            Publish(Current.State);
            logger.Log(
                result.IsSuccessful ? LogLevel.Information : LogLevel.Warning,
                "EventBuffer reset finished. Action={Action}, Phase={Phase}, OperationId={OperationId}, ResultState={ResultState}, Accepted={Accepted}, Message={Message}",
                action,
                phase,
                operationId,
                result.State,
                result.Value?.Accepted,
                result.Message);
        }
        catch (OperationCanceledException ex)
        {
            result = ClassifyEventBufferResetCancellation(action, phase, ex, operationCancellation);
            CompleteCommandActivity(
                UnitTestActivityCommandKind.EventBufferReset,
                lifecycleObservationId,
                operationId,
                result.State,
                result.Value?.Accepted,
                result.Message);
            Publish(Current.State);
        }
        finally
        {
            operationInProgress = false;
            Current = Current with { ActiveOperation = null };
            Publish(Current.State);
            CompleteActiveWorkflowOperation(operation);
        }

        return new EventBufferResetWorkflowActionResult(action, result, Current);
    }

    public async Task<WorkflowActionResult> ReleaseUnitTestCommunicationAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var gate = RequireAvailableActionGate(WorkflowAction.ReleaseUnitTestCommunication);
        if (gate is not null)
        {
            return gate;
        }

        return await ExecuteAsync(
            WorkflowAction.ReleaseUnitTestCommunication,
            "UnitTestCommunicationRelease",
            policy,
            cancellationToken,
            async operationToken =>
        {
            var releaseSucceeded = await ReleaseUnitTestCommunicationCoreAsync(operationToken);
            return releaseSucceeded
                ? OperationResult.Success("UnitTest communication released.")
                : OperationResult.Failure("UnitTest communication release finished with diagnostics.");
        }, CreatePreparedWorkflowState());
    }

    public async Task<WorkflowActionResult> ReleaseRuntimeAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var gate = RequireAvailableActionGate(WorkflowAction.ReleaseRuntime);
        if (gate is not null)
        {
            return gate;
        }

        return await ExecuteAsync(
            WorkflowAction.ReleaseRuntime,
            "RuntimeRelease",
            policy,
            cancellationToken,
            async operationToken =>
        {
            return await ReleaseRuntimeCoreAsync(operationToken);
        }, project is not null ? RunWorkflowState.PreparingProject : RunWorkflowState.Configured);
    }

    public async Task<WorkflowActionResult> ReleaseProjectAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var gate = RequireAvailableActionGate(WorkflowAction.ReleaseProject);
        if (gate is not null)
        {
            return gate;
        }

        return await ExecuteAsync(
            WorkflowAction.ReleaseProject,
            "ProjectRelease",
            policy,
            cancellationToken,
            async operationToken =>
        {
            var result = await ExecuteProfileCallAsync(
                AccessServiceKind.Project,
                "ReleaseProject",
                DiagnosticPhase.Cleanup,
                operationToken,
                () => profile!.Project.ReleaseProjectAsync(operationToken));
            if (result.IsSuccessful)
            {
                projectAccessActive = false;
                if (projectSetupState != ProjectSetupState.ManagedTransferCompleted)
                {
                    project = null;
                    projectSetupState = ProjectSetupState.NotEstablished;
                }
            }

            return result.IsSuccessful
                ? OperationResult.Success(result.Message)
                : OperationResult.Failure(result.Message ?? "Project release failed.");
        }, RunWorkflowState.Configured);
    }

    public async Task<WorkflowActionResult> CleanupAsync(
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(
            WorkflowAction.Cleanup,
            "Cleanup",
            policy,
            cancellationToken,
            async operationToken =>
        {
            var cleanupSucceeded = await CleanupWorkflowAsync(operationToken);

            return cleanupSucceeded
                ? OperationResult.Success("Workflow cleaned up.")
                : OperationResult.Failure("Workflow cleanup finished with diagnostics.");
        }, RunWorkflowState.NotConfigured, RunWorkflowState.NotConfigured);
    }

    internal async Task<OperationResult> QuiesceActiveOperationAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Active workflow operation release timeout must be greater than zero.");
        }

        ActiveWorkflowOperation? operation;
        lock (activeOperationSync)
        {
            sessionReleaseDepth++;
            operation = activeWorkflowOperation;
        }

        if (operation is null)
        {
            return OperationResult.Success("No workflow operation is active.");
        }

        logger.LogInformation(
            "Cancelling active workflow operation before release. Action={Action}, OperationId={OperationId}",
            operation.Action,
            operation.OperationId);
        operation.Cancel();

        try
        {
            await operation.Completion.Task
                .WaitAsync(timeout, coreContext.TimeProvider, cancellationToken)
                .ConfigureAwait(false);
            return OperationResult.Success("The active workflow operation stopped before release.");
        }
        catch (TimeoutException)
        {
            var message = $"The active workflow operation '{operation.Action}' did not stop within {timeout}.";
            logger.LogWarning(
                "Active workflow operation did not stop before the release deadline. Action={Action}, OperationId={OperationId}, Timeout={Timeout}",
                operation.Action,
                operation.OperationId,
                timeout);
            return OperationResult.TimedOut(message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OperationResult.Cancelled("Run session release was cancelled while waiting for the active workflow operation.");
        }
    }

    internal void CompleteSessionRelease()
    {
        lock (activeOperationSync)
        {
            if (sessionReleaseDepth > 0)
            {
                sessionReleaseDepth--;
            }
        }
    }

    private WorkflowActionResult? RequireAvailableActionGate(WorkflowAction action)
    {
        var gate = gateEvaluator.Evaluate(CreateStatusSnapshot(Current.State, Current.ActiveOperation), action);
        return gate.IsAvailable ? null : Blocked(action, gate);
    }

    private async Task<OperationResult<T>> ExecuteTypedProjectActionAsync<T>(
        WorkflowAction action,
        string phase,
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<OperationResult<T>>> execute)
    {
        OperationResult<T>? typedResult = null;
        var retainedState = Current.State;
        var workflowResult = await ExecuteAsync(
            action,
            phase,
            policy,
            cancellationToken,
            async operationToken =>
            {
                typedResult = await execute(operationToken);
                return new OperationResult(
                    typedResult.State,
                    typedResult.Message,
                    typedResult.Progress);
            },
            successState: null,
            failureState: retainedState);

        if (typedResult is not null && typedResult.State == workflowResult.Result.State)
        {
            return typedResult with
            {
                Message = workflowResult.Result.Message,
                Progress = workflowResult.Result.Progress
            };
        }

        return new OperationResult<T>(
            workflowResult.Result.State,
            default,
            workflowResult.Result.Message,
            workflowResult.Result.Progress);
    }

    private async Task<OperationResult> ExecuteProfileCallAsync(
        AccessServiceKind serviceKind,
        string operation,
        DiagnosticPhase phase,
        CancellationToken cancellationToken,
        Func<Task<OperationResult>> execute)
    {
        try
        {
            var result = await execute();
            if (result.State is not (OperationResultState.Cancelled or OperationResultState.TimedOut))
            {
                return result;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return OperationResult.Failure(ReportUnexpectedProfileResult(
                serviceKind,
                operation,
                phase,
                result.State,
                result.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return OperationResult.Failure(
                ReportProfileCallException(serviceKind, operation, phase, ex, "UnexpectedCancellation"));
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(
                ReportProfileCallException(serviceKind, operation, phase, ex, "UnexpectedException"));
        }
    }

    private async Task<OperationResult<T>> ExecuteProfileCallAsync<T>(
        AccessServiceKind serviceKind,
        string operation,
        DiagnosticPhase phase,
        CancellationToken cancellationToken,
        Func<Task<OperationResult<T>>> execute,
        Action? exceptionObserved = null)
    {
        try
        {
            var result = await execute();
            if (result.State is not (OperationResultState.Cancelled or OperationResultState.TimedOut))
            {
                return result;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            exceptionObserved?.Invoke();
            return OperationResult<T>.Failure(ReportUnexpectedProfileResult(
                serviceKind,
                operation,
                phase,
                result.State,
                result.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            exceptionObserved?.Invoke();
            return OperationResult<T>.Failure(
                ReportProfileCallException(serviceKind, operation, phase, ex, "UnexpectedCancellation"));
        }
        catch (Exception ex)
        {
            exceptionObserved?.Invoke();
            return OperationResult<T>.Failure(
                ReportProfileCallException(serviceKind, operation, phase, ex, "UnexpectedException"));
        }
    }

    private string ReportUnexpectedProfileResult(
        AccessServiceKind serviceKind,
        string operation,
        DiagnosticPhase phase,
        OperationResultState state,
        string? detail)
    {
        var outcome = state == OperationResultState.Cancelled
            ? "UnexpectedCancellationResult"
            : "TechnicalTimeout";
        var message = state == OperationResultState.Cancelled
            ? $"Access profile service '{serviceKind}' operation '{operation}' returned Cancelled without caller cancellation."
            : $"Access profile service '{serviceKind}' operation '{operation}' timed out before the Shared Core operation deadline.";
        if (!string.IsNullOrWhiteSpace(detail))
        {
            message += $" {detail}";
        }

        logger.LogError(
            "Access profile call returned an unexpected terminal result. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, Action={Action}, Service={Service}, Operation={Operation}, ResultState={ResultState}, Message={Message}",
            boundAccessProfileSession?.ProfileId,
            boundAccessProfileSession?.ProfileVersion,
            Current.ActiveOperation?.Action,
            serviceKind,
            operation,
            state,
            detail);
        diagnosticReporter.Add(diagnosticReporter.Create(
            $"Workflow.AccessProfile.{serviceKind}.{operation}.{outcome}",
            DiagnosticCategory.Workflow,
            LogLevel.Error,
            message,
            detail,
            phase) with
        {
            Correlation = new DiagnosticCorrelation(
                OperationId: Current.ActiveOperation is null
                    ? null
                    : new OperationId(Current.ActiveOperation.OperationId),
                AccessProfileId: boundAccessProfileSession?.ProfileId,
                ServiceId: serviceKind.ToString(),
                ProjectContextId: project?.Id,
                RuntimeTargetId: runtimeTarget?.Id)
        });

        return message;
    }

    private string ReportProfileCallException(
        AccessServiceKind serviceKind,
        string operation,
        DiagnosticPhase phase,
        Exception exception,
        string outcome)
    {
        var action = Current.ActiveOperation?.Action;
        var message =
            $"Access profile service '{serviceKind}' operation '{operation}' failed unexpectedly: {exception.Message}";
        var technicalDetail =
            $"{exception.GetType().FullName}: {exception.Message}; "
            + $"ProfileVersion={boundAccessProfileSession?.ProfileVersion ?? "<unknown>"}; "
            + $"Action={action?.ToString() ?? "<none>"}; Service={serviceKind}; Operation={operation}";
        var correlation = new DiagnosticCorrelation(
            OperationId: Current.ActiveOperation is null
                ? null
                : new OperationId(Current.ActiveOperation.OperationId),
            AccessProfileId: boundAccessProfileSession?.ProfileId,
            ServiceId: serviceKind.ToString(),
            ProjectContextId: project?.Id,
            RuntimeTargetId: runtimeTarget?.Id);

        logger.LogError(
            exception,
            "Access profile call failed unexpectedly. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, Action={Action}, Service={Service}, Operation={Operation}, Outcome={Outcome}",
            boundAccessProfileSession?.ProfileId,
            boundAccessProfileSession?.ProfileVersion,
            action,
            serviceKind,
            operation,
            outcome);
        diagnosticReporter.Add(diagnosticReporter.Create(
            $"Workflow.AccessProfile.{serviceKind}.{operation}.{outcome}",
            DiagnosticCategory.Workflow,
            LogLevel.Error,
            message,
            technicalDetail,
            phase) with
        {
            Correlation = correlation
        });

        return message;
    }

    private void ExecuteProfileCallback(
        AccessServiceKind serviceKind,
        string callback,
        DiagnosticPhase phase,
        bool invalidateRunObservation,
        Action execute)
    {
        try
        {
            execute();
        }
        catch (Exception ex)
        {
            HandleProfileCallbackFailure(serviceKind, callback, phase, invalidateRunObservation, ex);
        }
    }

    private async Task ExecuteProfileCallbackAsync(
        AccessServiceKind serviceKind,
        string callback,
        DiagnosticPhase phase,
        bool invalidateRunObservation,
        Func<Task> execute)
    {
        try
        {
            await execute();
        }
        catch (Exception ex)
        {
            HandleProfileCallbackFailure(serviceKind, callback, phase, invalidateRunObservation, ex);
        }
    }

    private void HandleProfileCallbackFailure(
        AccessServiceKind serviceKind,
        string callback,
        DiagnosticPhase phase,
        bool invalidateRunObservation,
        Exception exception)
    {
        logger.LogError(
            exception,
            "Access profile callback processing failed. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, Service={Service}, Callback={Callback}",
            boundAccessProfileSession?.ProfileId,
            boundAccessProfileSession?.ProfileVersion,
            serviceKind,
            callback);

        var message =
            $"Access profile callback '{serviceKind}.{callback}' could not be processed: {exception.Message}";
        try
        {
            diagnosticReporter.Add(diagnosticReporter.Create(
                $"Workflow.AccessProfile.Callback.{callback}.ProcessingFailed",
                DiagnosticCategory.Workflow,
                LogLevel.Error,
                message,
                $"{exception.GetType().FullName}: {exception.Message}; "
                + $"ProfileVersion={boundAccessProfileSession?.ProfileVersion ?? "<unknown>"}; "
                + $"Service={serviceKind}; Callback={callback}",
                phase) with
            {
                Correlation = new DiagnosticCorrelation(
                    AccessProfileId: boundAccessProfileSession?.ProfileId,
                    ServiceId: serviceKind.ToString(),
                    ProjectContextId: project?.Id,
                    RuntimeTargetId: runtimeTarget?.Id)
            });
        }
        catch (Exception containmentException)
        {
            logger.LogCritical(
                containmentException,
                "Publishing an access profile callback failure diagnostic failed. ProfileId={ProfileId}, Service={Service}, Callback={Callback}",
                boundAccessProfileSession?.ProfileId,
                serviceKind,
                callback);
        }

        try
        {
            if (invalidateRunObservation
                && testRunMonitor.Current is TestRunMonitoringState.WaitingForRun
                    or TestRunMonitoringState.RunActive
                    or TestRunMonitoringState.CompletionPending)
            {
                testRunMonitor.InvalidateMonitoring(message);
                eventStreamProcessor.CompleteCurrentEpoch(
                    EventStreamCompletionReason.TargetInvalidated,
                    coreContext.TimeProvider.GetUtcNow());
            }
        }
        catch (Exception containmentException)
        {
            logger.LogCritical(
                containmentException,
                "Invalidating workflow observation after an access profile callback failure failed. ProfileId={ProfileId}, Service={Service}, Callback={Callback}",
                boundAccessProfileSession?.ProfileId,
                serviceKind,
                callback);
        }

        try
        {
            Publish(RunWorkflowState.Error);
        }
        catch (Exception containmentException)
        {
            logger.LogCritical(
                containmentException,
                "Publishing workflow error state after an access profile callback failure failed. ProfileId={ProfileId}, Service={Service}, Callback={Callback}",
                boundAccessProfileSession?.ProfileId,
                serviceKind,
                callback);
        }
    }

    private async Task<WorkflowActionResult> ExecuteAsync(
        WorkflowAction action,
        string phase,
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<OperationResult>> execute,
        RunWorkflowState? successState,
        RunWorkflowState failureState = RunWorkflowState.Error)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy.Timeout,
                "Workflow action timeout must be greater than zero.");
        }

        var operationId = Guid.NewGuid().ToString("N");
        using var operation = TryBeginActiveWorkflowOperation(action, operationId, cancellationToken);
        if (operation is null)
        {
            return Blocked(action, ActionGateReasonCode.OperationInProgress, "Another workflow operation or session release is already running.");
        }

        operationInProgress = true;
        logger.LogInformation(
            "Workflow action started. Action={Action}, Phase={Phase}, OperationId={OperationId}",
            action,
            phase,
            operationId);
        Current = Current with
        {
            ActiveOperation = new ActiveOperation(operationId, action, phase, coreContext.TimeProvider.GetUtcNow(), null)
        };
        Publish(Current.State);

        OperationResult result;
        RunWorkflowState nextState;
        var plcStartObservation = action == WorkflowAction.StartPlc
            ? BeginCommandActivity(UnitTestActivityCommandKind.PlcStart, operationId)
            : (LifecycleObservationId?)null;
        using var operationCancellation = new CoreOperationCancellationScope(
            coreContext.TimeProvider,
            policy.Timeout,
            operation.Token);
        try
        {
            result = await execute(operationCancellation.Token);
            result = NormalizeWorkflowResult(action, phase, result, operationCancellation);
            nextState = result.State switch
            {
                OperationResultState.Succeeded => successState ?? Current.State,
                OperationResultState.Cancelled => action == WorkflowAction.Cleanup
                    ? successState ?? RunWorkflowState.NotConfigured
                    : Current.State,
                _ => failureState
            };
            Publish(nextState);
            logger.Log(
                result.IsSuccessful ? LogLevel.Information : LogLevel.Warning,
                "Workflow action finished. Action={Action}, Phase={Phase}, OperationId={OperationId}, ResultState={ResultState}, Message={Message}",
                action,
                phase,
                operationId,
                result.State,
                result.Message);
        }
        catch (OperationCanceledException ex)
        {
            result = ClassifyCancellation(action, phase, ex, operationCancellation);
            nextState = result.State == OperationResultState.Cancelled
                ? action == WorkflowAction.Cleanup
                    ? successState ?? RunWorkflowState.NotConfigured
                    : Current.State
                : failureState;
            Publish(nextState);
            logger.Log(
                result.State == OperationResultState.Cancelled ? LogLevel.Information : LogLevel.Warning,
                "Workflow action interrupted. Action={Action}, Phase={Phase}, OperationId={OperationId}, ResultState={ResultState}, Message={Message}",
                action,
                phase,
                operationId,
                result.State,
                result.Message);
        }
        catch (Exception ex)
        {
            if (plcStartObservation is { } failedObservation)
                CompleteCommandActivity(UnitTestActivityCommandKind.PlcStart, failedObservation, operationId,
                    OperationResultState.Failed, null, ex.Message);
            logger.LogError(
                ex,
                "Workflow action failed with exception. Action={Action}, Phase={Phase}, OperationId={OperationId}",
                action,
                phase,
                operationId);
            throw;
        }
        finally
        {
            operationInProgress = false;
            Current = Current with { ActiveOperation = null };
            Publish(Current.State);
            CompleteActiveWorkflowOperation(operation);
            if (communicationAutoPreparePending)
            {
                await AutoPrepareUnitTestCommunicationAsync();
            }
        }

        if (plcStartObservation is { } observationId)
            CompleteCommandActivity(UnitTestActivityCommandKind.PlcStart, observationId, operationId,
                result.State, result.IsSuccessful, result.Message);
        return new WorkflowActionResult(action, result, Current);
    }

    private OperationResult NormalizeWorkflowResult(
        WorkflowAction action,
        string phase,
        OperationResult result,
        CoreOperationCancellationScope cancellation)
    {
        if (cancellation.CallerCancellationRequested)
        {
            return OperationResult.Cancelled($"Workflow action '{action}' was cancelled by the caller.");
        }

        if (cancellation.DeadlineExpired)
        {
            return OperationResult.TimedOut(ReportWorkflowTimeout(action, phase, cancellation.Timeout));
        }

        if (result.State is OperationResultState.Cancelled or OperationResultState.TimedOut)
        {
            return OperationResult.Failure(ReportUnexpectedWorkflowResult(
                action,
                phase,
                result.State,
                result.Message));
        }

        return result;
    }

    private OperationResult<T> NormalizeWorkflowResult<T>(
        WorkflowAction action,
        string phase,
        OperationResult<T> result,
        CoreOperationCancellationScope cancellation)
    {
        if (cancellation.CallerCancellationRequested)
        {
            return OperationResult<T>.Cancelled($"Workflow action '{action}' was cancelled by the caller.");
        }

        if (cancellation.DeadlineExpired)
        {
            return OperationResult<T>.TimedOut(ReportWorkflowTimeout(action, phase, cancellation.Timeout));
        }

        if (result.State is OperationResultState.Cancelled or OperationResultState.TimedOut)
        {
            return OperationResult<T>.Failure(ReportUnexpectedWorkflowResult(
                action,
                phase,
                result.State,
                result.Message));
        }

        return result;
    }

    private OperationResult ClassifyCancellation(
        WorkflowAction action,
        string phase,
        OperationCanceledException exception,
        CoreOperationCancellationScope cancellation)
    {
        if (cancellation.CallerCancellationRequested)
        {
            return OperationResult.Cancelled($"Workflow action '{action}' was cancelled by the caller.");
        }

        if (cancellation.DeadlineExpired)
        {
            return OperationResult.TimedOut(ReportWorkflowTimeout(action, phase, cancellation.Timeout));
        }

        return OperationResult.Failure(ReportUnexpectedWorkflowCancellation(action, phase, exception));
    }

    private OperationResult<TestRunCommandResult> ClassifyUnitTestCommandCancellation(
        WorkflowAction action,
        string phase,
        OperationCanceledException exception,
        CoreOperationCancellationScope cancellation)
    {
        if (cancellation.CallerCancellationRequested)
        {
            return OperationResult<TestRunCommandResult>.Cancelled(
                $"UnitTest command '{action}' was cancelled by the caller.");
        }

        if (cancellation.DeadlineExpired)
        {
            return OperationResult<TestRunCommandResult>.TimedOut(
                ReportWorkflowTimeout(action, phase, cancellation.Timeout));
        }

        return OperationResult<TestRunCommandResult>.Failure(
            ReportUnexpectedWorkflowCancellation(action, phase, exception));
    }

    private OperationResult<EventBufferResetCommandResult> ClassifyEventBufferResetCancellation(
        WorkflowAction action,
        string phase,
        OperationCanceledException exception,
        CoreOperationCancellationScope cancellation)
    {
        if (cancellation.CallerCancellationRequested)
        {
            return OperationResult<EventBufferResetCommandResult>.Cancelled(
                "EventBuffer reset was cancelled by the caller.");
        }

        if (cancellation.DeadlineExpired)
        {
            return OperationResult<EventBufferResetCommandResult>.TimedOut(
                ReportWorkflowTimeout(action, phase, cancellation.Timeout));
        }

        return OperationResult<EventBufferResetCommandResult>.Failure(
            ReportUnexpectedWorkflowCancellation(action, phase, exception));
    }

    private string ReportUnexpectedWorkflowResult(
        WorkflowAction action,
        string phase,
        OperationResultState state,
        string? detail)
    {
        var outcome = state == OperationResultState.Cancelled
            ? "UnexpectedCancellationResult"
            : "TechnicalTimeout";
        var message = state == OperationResultState.Cancelled
            ? $"Workflow action '{action}' returned Cancelled without caller cancellation."
            : $"Workflow action '{action}' reported a technical timeout before its Shared Core operation deadline.";
        if (!string.IsNullOrWhiteSpace(detail))
        {
            message += $" {detail}";
        }

        diagnosticReporter.Add(diagnosticReporter.Create(
            $"Workflow.{action}.{outcome}",
            DiagnosticCategory.Workflow,
            LogLevel.Error,
            message,
            detail,
            ToDiagnosticPhase(action)));
        logger.LogError(
            "Workflow action returned an unexpected terminal result. Action={Action}, Phase={Phase}, ResultState={ResultState}, Message={Message}",
            action,
            phase,
            state,
            detail);
        return message;
    }

    private string ReportWorkflowTimeout(WorkflowAction action, string phase, TimeSpan timeout)
    {
        var message = $"Workflow action '{action}' exceeded its overall timeout of {timeout}.";
        diagnosticReporter.Add(diagnosticReporter.Create(
            $"Workflow.{action}.TimedOut",
            DiagnosticCategory.Workflow,
            LogLevel.Error,
            message,
            phase: ToDiagnosticPhase(action)));
        logger.LogWarning(
            "Workflow action exceeded its Shared Core deadline. Action={Action}, Phase={Phase}, Timeout={Timeout}",
            action,
            phase,
            timeout);
        return message;
    }

    private string ReportUnexpectedWorkflowCancellation(
        WorkflowAction action,
        string phase,
        OperationCanceledException exception)
    {
        var message =
            $"Workflow action '{action}' was cancelled internally without caller cancellation or an expired Shared Core deadline.";
        diagnosticReporter.Add(diagnosticReporter.Create(
            $"Workflow.{action}.UnexpectedCancellation",
            DiagnosticCategory.Workflow,
            LogLevel.Error,
            message,
            exception.ToString(),
            ToDiagnosticPhase(action)));
        logger.LogError(
            exception,
            "Workflow action was cancelled unexpectedly. Action={Action}, Phase={Phase}",
            action,
            phase);
        return message;
    }

    private static DiagnosticPhase ToDiagnosticPhase(WorkflowAction action)
    {
        return action switch
        {
            WorkflowAction.LoadAccessProfile => DiagnosticPhase.ProfileValidation,
            WorkflowAction.DiscoverProjectTargets or WorkflowAction.PrepareProject =>
                DiagnosticPhase.ProjectPreparation,
            WorkflowAction.DiscoverRuntimeTargets or WorkflowAction.PrepareRuntime
                or WorkflowAction.ReconnectRuntime =>
                DiagnosticPhase.RuntimeProvisioning,
            WorkflowAction.DiscoverSafetyProgramAuthorizationRequirement
                or WorkflowAction.AuthorizeSafetyProgram
                or WorkflowAction.TransferProject => DiagnosticPhase.ProjectTransfer,
            WorkflowAction.StartPlc or WorkflowAction.StopPlc => DiagnosticPhase.RuntimeStartup,
            WorkflowAction.PrepareUnitTestCommunication => DiagnosticPhase.CommunicationValidation,
            WorkflowAction.StartUnitTest => DiagnosticPhase.RunStart,
            WorkflowAction.StartMonitoring or WorkflowAction.StopMonitoring
                or WorkflowAction.AbortUnitTest or WorkflowAction.ResetUnitTest
                or WorkflowAction.ResetEventBuffer =>
                DiagnosticPhase.ExecutionMonitoring,
            _ => DiagnosticPhase.Cleanup
        };
    }

    private async Task<UnitTestWorkflowActionResult> ExecuteUnitTestCommandAsync(
        WorkflowAction action,
        WorkflowActionPolicy policy,
        CancellationToken cancellationToken,
        Func<UnitTestContext, CancellationToken, Task<OperationResult<TestRunCommandResult>>> execute)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy.Timeout,
                "Workflow action timeout must be greater than zero.");
        }

        var gate = gateEvaluator.Evaluate(CreateStatusSnapshot(Current.State, Current.ActiveOperation), action);
        if (!gate.IsAvailable)
        {
            return BlockedUnitTestCommand(action, gate);
        }

        var operationId = Guid.NewGuid().ToString("N");
        using var operation = TryBeginActiveWorkflowOperation(action, operationId, cancellationToken);
        if (operation is null)
        {
            return BlockedUnitTestCommand(
                action,
                new ActionGateState(
                    action,
                    ActionGateAvailability.Pending,
                    [new ActionGateReason(
                        ActionGateReasonCode.OperationInProgress,
                        "Another workflow operation or session release is already running.")]));
        }

        operationInProgress = true;
        const string phase = "UnitTestCommand";
        var activityCommand = ToActivityCommand(action);
        var lifecycleObservationId = BeginCommandActivity(activityCommand, operationId);
        logger.LogInformation(
            "UnitTest command started. Action={Action}, Phase={Phase}, OperationId={OperationId}",
            action,
            phase,
            operationId);
        Current = Current with
        {
            ActiveOperation = new ActiveOperation(operationId, action, phase, coreContext.TimeProvider.GetUtcNow(), null)
        };
        Publish(Current.State);
        OperationResult<TestRunCommandResult> result;
        UnitTestRunCommand? command = null;
        RunWorkflowState nextState;
        using var operationCancellation = new CoreOperationCancellationScope(
            coreContext.TimeProvider,
            policy.Timeout,
            operation.Token);
        try
        {
            operationCancellation.Token.ThrowIfCancellationRequested();
            command = ToUnitTestRunCommand(action);
            testRunMonitor.BeginCommandLifecycleMonitoring(command.Value);
            var exceptionNormalized = false;
            result = await ExecuteProfileCallAsync(
                AccessServiceKind.UnitTestLifecycle,
                action.ToString(),
                action == WorkflowAction.StartUnitTest
                    ? DiagnosticPhase.RunStart
                    : DiagnosticPhase.ExecutionMonitoring,
                operationCancellation.Token,
                () => execute(unitTestContext!, operationCancellation.Token),
                () => exceptionNormalized = true);
            result = NormalizeWorkflowResult(action, phase, result, operationCancellation);
            var accepted = EvaluateUnitTestCommandOutcome(action, result, !exceptionNormalized);
            CompleteCommandActivity(
                activityCommand,
                lifecycleObservationId,
                operationId,
                result.State,
                result.Value?.Accepted,
                result.Message);
            if (!accepted)
            {
                testRunMonitor.CancelCommandLifecycleMonitoring(command.Value);
            }
            else
            {
                ApplyAcceptedUnitTestCommand(action);
            }

            nextState = accepted ? Current.State : RunWorkflowState.Error;
            Publish(nextState);
            logger.Log(
                accepted ? LogLevel.Information : LogLevel.Warning,
                "UnitTest command finished. Action={Action}, Command={Command}, Successful={Successful}, Accepted={Accepted}, LifecycleState={LifecycleState}, Result={Result}, Message={Message}",
                action,
                command,
                result.IsSuccessful,
                result.Value?.Accepted,
                lifecycleObservation?.State,
                lifecycleObservation?.Result,
                result.Message);
        }
        catch (OperationCanceledException ex)
        {
            if (command is not null)
            {
                testRunMonitor.CancelCommandLifecycleMonitoring(command.Value);
            }

            result = ClassifyUnitTestCommandCancellation(action, phase, ex, operationCancellation);
            CompleteCommandActivity(
                activityCommand,
                lifecycleObservationId,
                operationId,
                result.State,
                result.Value?.Accepted,
                result.Message);
            nextState = result.State == OperationResultState.Cancelled
                ? Current.State
                : RunWorkflowState.Error;
            Publish(nextState);
            logger.Log(
                result.State == OperationResultState.Cancelled ? LogLevel.Information : LogLevel.Warning,
                "UnitTest command interrupted. Action={Action}, OperationId={OperationId}, ResultState={ResultState}, Message={Message}",
                action,
                operationId,
                result.State,
                result.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UnitTest command failed with exception. Action={Action}", action);
            throw;
        }
        finally
        {
            operationInProgress = false;
            Current = Current with { ActiveOperation = null };
            Publish(Current.State);
            CompleteActiveWorkflowOperation(operation);
        }

        return new UnitTestWorkflowActionResult(action, result, Current);
    }

    private bool EvaluateUnitTestCommandOutcome(
        WorkflowAction action,
        OperationResult<TestRunCommandResult> result,
        bool reportServiceFailure = true)
    {
        if (!result.IsSuccessful)
        {
            if (reportServiceFailure)
            {
                diagnosticReporter.Add(diagnosticReporter.CreateUnitTestCommand(
                    action,
                    "ServiceFailed",
                    LogLevel.Error,
                    result.Message ?? "UnitTest lifecycle command failed."));
            }

            return false;
        }

        if (result.Value is null)
        {
            diagnosticReporter.Add(diagnosticReporter.CreateUnitTestCommand(
                action,
                "MissingResult",
                LogLevel.Error,
                "UnitTest lifecycle command succeeded without a command result."));
            return false;
        }

        if (result.Value.Command != ToUnitTestRunCommand(action))
        {
            diagnosticReporter.Add(diagnosticReporter.CreateUnitTestCommand(
                action,
                "UnexpectedCommand",
                LogLevel.Error,
                $"UnitTest lifecycle service returned unexpected command result '{result.Value.Command}' for workflow action '{action}'."));
            return false;
        }

        if (!result.Value.Accepted)
        {
            diagnosticReporter.Add(diagnosticReporter.CreateUnitTestCommand(
                action,
                "Rejected",
                LogLevel.Warning,
                result.Message ?? $"UnitTest lifecycle command '{result.Value.Command}' was rejected by the lifecycle service."));
            return false;
        }

        return true;
    }

    private static UnitTestRunCommand ToUnitTestRunCommand(WorkflowAction action)
    {
        return action switch
        {
            WorkflowAction.StartUnitTest => UnitTestRunCommand.Start,
            WorkflowAction.AbortUnitTest => UnitTestRunCommand.Abort,
            WorkflowAction.ResetUnitTest => UnitTestRunCommand.Reset,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Workflow action is not a UnitTest command.")
        };
    }

    private static PlcProjectRequest CreateProjectRequest(AccessProfileConfig config)
    {
        return new PlcProjectRequest(config);
    }

    private static string FormatDiagnosticDetails(IReadOnlyCollection<Diagnostic> diagnostics)
    {
        return diagnostics.Count == 0
            ? "-"
            : string.Join("; ", diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
    }

    private WorkflowActionResult Blocked(WorkflowAction action, ActionGateReasonCode reasonCode, string message)
    {
        logger.LogWarning(
            "Workflow action blocked. Action={Action}, ReasonCode={ReasonCode}, Message={Message}",
            action,
            reasonCode,
            message);
        var diagnostic = diagnosticReporter.Create(
            $"Workflow.ActionBlocked.{action}.{reasonCode}",
            DiagnosticCategory.Workflow,
            LogLevel.Error,
            message,
            phase: DiagnosticPhase.Configuration);
        diagnosticReporter.Add(diagnostic);
        Publish(Current.State);
        return new WorkflowActionResult(action, OperationResult.Failure(message), Current);
    }

    private WorkflowActionResult Blocked(WorkflowAction action, ActionGateState gate)
    {
        var reason = gate.Reasons.FirstOrDefault();
        var reasonCode = reason?.Code ?? ActionGateReasonCode.OperationInProgress;
        var message = reason?.Message ?? "Action is not available.";
        return Blocked(action, reasonCode, message);
    }

    private UnitTestWorkflowActionResult BlockedUnitTestCommand(
        WorkflowAction action,
        ActionGateState gate)
    {
        var reason = gate.Reasons.FirstOrDefault();
        var reasonCode = reason?.Code ?? ActionGateReasonCode.OperationInProgress;
        var message = reason?.Message ?? "Action is not available.";
        logger.LogWarning(
            "UnitTest command blocked. Action={Action}, ReasonCode={ReasonCode}, Message={Message}",
            action,
            reasonCode,
            message);
        return new UnitTestWorkflowActionResult(
            action,
            OperationResult<TestRunCommandResult>.Failure(message),
            Current);
    }

    private SafetyAuthorizationRequirementWorkflowActionResult BlockedSafetyAuthorizationRequirement(
        WorkflowAction action,
        ActionGateState gate)
    {
        var message = gate.Reasons.FirstOrDefault()?.Message ?? "Action is not available.";
        return new SafetyAuthorizationRequirementWorkflowActionResult(
            action,
            OperationResult<SafetyProgramAuthorizationRequirementResult>.Failure(message),
            Current);
    }

    private SafetyAuthorizationWorkflowActionResult BlockedSafetyAuthorization(
        WorkflowAction action,
        ActionGateState gate)
    {
        var message = gate.Reasons.FirstOrDefault()?.Message ?? "Action is not available.";
        return new SafetyAuthorizationWorkflowActionResult(
            action,
            OperationResult<SafetyProgramAuthorizationResult>.Failure(message),
            Current);
    }

    private bool IsActionAvailable(WorkflowAction action)
    {
        return gateEvaluator.Evaluate(CreateStatusSnapshot(Current.State, Current.ActiveOperation), action).IsAvailable;
    }

    private OperationResult ValidateAccessProfileSession(AccessProfileSession session)
    {
        if (!string.Equals(session.Profile.Info.ProfileId, session.SelectedProfile.ProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(session.Profile.Info.Version, session.SelectedProfile.Version, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Failure("Access profile session metadata does not match the loaded profile.");
        }

        if (!string.Equals(session.ProfileId, session.SelectedProfile.ProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Failure("Access profile session request does not match the selected profile.");
        }

        if (!string.IsNullOrWhiteSpace(session.ProfileVersion)
            && !string.Equals(session.ProfileVersion, session.SelectedProfile.Version, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Failure("Access profile session request version does not match the selected profile version.");
        }

        return OperationResult.Success("Access profile session is valid.");
    }

    private void BindLoadedAccessProfileSession(
        AccessProfileSession session,
        bool ownsSession)
    {
        StartActivitySession();
        boundAccessProfileSession = session;
        ownsBoundAccessProfileSession = ownsSession;
        WireProfile(session.Profile);
        accessHealthMonitor.Bind(session.Profile);
    }

    private void ClearBoundAccessProfileSession()
    {
        accessHealthMonitor.Unbind();
        boundAccessProfileSession = null;
        ownsBoundAccessProfileSession = false;
        EndActivitySession();
    }

    private void WireProfile(IAccessProfileSession loaded)
    {
        loaded.DiagnosticEventHandler += (_, args) => ExecuteProfileCallback(
            AccessServiceKind.Profile,
            "DiagnosticPublished",
            DiagnosticPhase.Configuration,
            invalidateRunObservation: false,
            () => diagnosticReporter.Add(args.Diagnostic));
        loaded.Project.DiagnosticEventHandler += (_, args) => ExecuteProfileCallback(
            AccessServiceKind.Project,
            "DiagnosticPublished",
            DiagnosticPhase.ProjectPreparation,
            invalidateRunObservation: false,
            () => diagnosticReporter.Add(args.Diagnostic));
        loaded.Project.ProjectStateChangedEventHandler += (_, _) => ExecuteProfileCallback(
            AccessServiceKind.Project,
            "ProjectStateChanged",
            DiagnosticPhase.ProjectPreparation,
            invalidateRunObservation: false,
            () =>
            {
                projectAccessActive = true;
                Publish(Current.State);
            });
        loaded.PlcControl.DiagnosticEventHandler += (_, args) => ExecuteProfileCallback(
            AccessServiceKind.PlcControl,
            "DiagnosticPublished",
            DiagnosticPhase.RuntimeProvisioning,
            invalidateRunObservation: false,
            () => diagnosticReporter.Add(args.Diagnostic));
        loaded.PlcControl.PlcStateChangedEventHandler += (_, args) =>
        {
            _ = ExecuteProfileCallbackAsync(
                AccessServiceKind.PlcControl,
                "PlcStateChanged",
                DiagnosticPhase.RuntimeStartup,
                invalidateRunObservation: true,
                () => HandleObservedPlcStateChangedAsync(args.State));
        };
        loaded.UnitTestLifecycle.DiagnosticEventHandler += (_, args) => ExecuteProfileCallback(
            AccessServiceKind.UnitTestLifecycle,
            "DiagnosticPublished",
            DiagnosticPhase.ExecutionMonitoring,
            invalidateRunObservation: false,
            () => diagnosticReporter.Add(args.Diagnostic));
        loaded.UnitTestLifecycle.RunControlObservationChanged += (_, args) => ExecuteProfileCallback(
            AccessServiceKind.UnitTestLifecycle,
            "RunControlObservationChanged",
            DiagnosticPhase.ExecutionMonitoring,
            invalidateRunObservation: true,
            () =>
            {
                lifecycleObservation = args.Observation;
                logger.LogDebug(
                    "UnitTest lifecycle observation changed. State={State}, Result={Result}, Ready={Ready}, Running={Running}, Completed={Completed}, Error={Error}",
                    args.Observation.State,
                    args.Observation.Result,
                    args.Observation.Ready,
                    args.Observation.Running,
                    args.Observation.Completed,
                    args.Observation.Error);
                testRunMonitor.ApplyLifecycleUpdate(args.Observation);
                Publish(Current.State);
            });
        loaded.UnitTestEventStream.DiagnosticEventHandler += (_, args) => ExecuteProfileCallback(
            AccessServiceKind.UnitTestEventStream,
            "DiagnosticPublished",
            DiagnosticPhase.ExecutionMonitoring,
            invalidateRunObservation: false,
            () => diagnosticReporter.Add(args.Diagnostic));
        loaded.UnitTestEventStream.EventBatchReceivedEventHandler += (_, args) => ExecuteProfileCallback(
            AccessServiceKind.UnitTestEventStream,
            "EventBatchReceived",
            DiagnosticPhase.ExecutionMonitoring,
            invalidateRunObservation: true,
            () =>
            {
                eventStreamState = loaded.UnitTestEventStream.CurrentMonitoringState;
                monitoringActive = eventStreamState.IsMonitoring;
                logger.LogDebug(
                    "UnitTest event-stream batch received. EventCount={EventCount}, ProducerSequenceId={ProducerSequenceId}, DeletedSequenceId={DeletedSequenceId}, LostEventCount={LostEventCount}, BufferObservedEmpty={BufferObservedEmpty}, ReadState={ReadState}, CompletionAnchorObserved={CompletionAnchorObserved}",
                    args.Batch.Events.Count,
                    args.Batch.ProducerSequenceId,
                    args.Batch.DeletedSequenceId,
                    args.Batch.LostEventCount,
                    args.Batch.BufferObservedEmpty,
                    args.Batch.ReadState,
                    ContainsRunControlCompletionAnchor(args.Batch));
                var processingResult = eventStreamProcessor.ProcessBatch(args.Batch);
                diagnosticReporter.AddRange(processingResult.Diagnostics);
                testRunMonitor.ApplyEventStreamUpdate(new TestRunEventStreamUpdate(
                    eventStreamState,
                    processingResult.CurrentRun));
                Publish(Current.State);
            });
        loaded.UnitTestEventStream.MonitoringStateChangedEventHandler += (_, args) => ExecuteProfileCallback(
            AccessServiceKind.UnitTestEventStream,
            "MonitoringStateChanged",
            DiagnosticPhase.ExecutionMonitoring,
            invalidateRunObservation: false,
            () =>
            {
                eventStreamState = args.CurrentState;
                monitoringActive = eventStreamState.IsMonitoring;
                var processingResult = eventStreamProcessor.ApplyMonitoringState(eventStreamState);
                diagnosticReporter.AddRange(processingResult.Diagnostics);
                testRunMonitor.ApplyEventStreamUpdate(new TestRunEventStreamUpdate(
                    eventStreamState,
                    processingResult.CurrentRun ?? eventStreamProcessor.Current));
                Publish(Current.State);
            });
    }

    private void ClearPreparedContexts(bool clearSetup)
    {
        if (clearSetup)
        {
            setup = null;
            ClearBoundAccessProfileSession();
            projectTargetDiscovery = null;
            runtimeTargetDiscovery = null;
        }

        project = null;
        projectSetupState = ProjectSetupState.NotEstablished;
        projectAccessActive = false;
        runtime = null;
        projectTarget = setup?.ProjectTarget;
        runtimeTarget = setup?.RuntimeTarget;
        ClearUnitTestCommunicationContext();
        eventStreamProcessor.Reset();
        ResetObservedPlcState();
        communicationAutoPrepareInProgress = false;
        communicationAutoPreparePending = false;
    }

    private void ClearUnitTestCommunicationContext()
    {
        ClearActivityCommunicationContext();
        communicationSession = null;
        unitTestContext = null;
        lifecycleObservation = null;
        eventStreamState = null;
        monitoringActive = false;
    }

    private RunWorkflowState CreateWorkflowStateForPlcState(PlcState state)
    {
        if (state.InstanceState is RuntimeInstanceState.Unavailable or RuntimeInstanceState.Faulted)
        {
            return RunWorkflowState.Configured;
        }

        return state.OperatingState == PlcOperatingState.Stopped
            && Current.Status.ProjectSetupSatisfied
                ? RunWorkflowState.PlcStopped
                : Current.State;
    }

    private async Task HandleObservedPlcStateChangedAsync(PlcState state)
    {
        ApplyObservedPlcState(state);
        RecordPlcState(state);
        if (state.ReadyForExecution)
        {
            communicationAutoPreparePending = true;
            Publish(CreateWorkflowStateForPlcState(state));
            await AutoPrepareUnitTestCommunicationAsync();
            return;
        }

        communicationAutoPreparePending = false;
        await ReleaseUnitTestCommunicationAfterObservedPlcUnavailableAsync(state);
        Publish(CreateWorkflowStateForPlcState(state));
    }

    private long CapturePlcStateObservationSequence()
    {
        lock (plcStateSync)
        {
            return plcStateObservationSequence;
        }
    }

    private PlcState GetCurrentPlcState()
    {
        lock (plcStateSync)
        {
            return currentPlcState;
        }
    }

    private void ApplyObservedPlcState(PlcState state)
    {
        PlcCommandObservationMonitor? completedMonitor = null;
        lock (plcStateSync)
        {
            currentPlcState = state;
            plcStateObservationSequence++;
            if (plcCommandObservationMonitor is { } monitor
                && plcStateObservationSequence > monitor.ObservationBaseline
                && IsExpectedPlcCommandState(monitor.Action, state.OperatingState))
            {
                plcCommandObservationMonitor = null;
                completedMonitor = monitor;
            }
        }

        if (completedMonitor is not null)
        {
            completedMonitor.Dispose();
            logger.LogInformation(
                "PLC command state observation completed. Action={Action}, OperatingState={OperatingState}",
                completedMonitor.Action,
                state.OperatingState);
        }
    }

    private void BeginPlcCommandObservationMonitoring(
        WorkflowAction action,
        long observationBaseline)
    {
        var monitor = new PlcCommandObservationMonitor(
            Guid.NewGuid(),
            action,
            observationBaseline);
        PlcCommandObservationMonitor? replacedMonitor;
        var alreadyObserved = false;
        lock (plcStateSync)
        {
            replacedMonitor = plcCommandObservationMonitor;
            if (plcStateObservationSequence > observationBaseline
                && IsExpectedPlcCommandState(action, currentPlcState.OperatingState))
            {
                alreadyObserved = true;
                plcCommandObservationMonitor = null;
            }
            else
            {
                plcCommandObservationMonitor = monitor;
            }
        }

        replacedMonitor?.Dispose();
        if (alreadyObserved)
        {
            logger.LogInformation(
                "PLC command state observation completed before command result returned. Action={Action}, OperatingState={OperatingState}",
                action,
                GetCurrentPlcState().OperatingState);
            return;
        }

        var timeout = action == WorkflowAction.StartPlc
            ? setup!.Execution.Timeouts.PlcStartReaction
            : setup!.Execution.Timeouts.PlcStopReaction;

        var timer = coreContext.TimeProvider.CreateTimer(
            static state =>
            {
                var callback = (PlcCommandObservationTimeoutCallback)state!;
                callback.Owner.HandlePlcCommandObservationTimeout(callback.MonitorId);
            },
            new PlcCommandObservationTimeoutCallback(this, monitor.Id),
            timeout,
            Timeout.InfiniteTimeSpan);

        lock (plcStateSync)
        {
            if (ReferenceEquals(plcCommandObservationMonitor, monitor))
            {
                monitor.Timer = timer;
                timer = null;
            }
        }

        timer?.Dispose();
    }

    private void HandlePlcCommandObservationTimeout(Guid monitorId)
    {
        PlcCommandObservationMonitor? timedOutMonitor = null;
        lock (plcStateSync)
        {
            if (plcCommandObservationMonitor?.Id == monitorId)
            {
                timedOutMonitor = plcCommandObservationMonitor;
                plcCommandObservationMonitor = null;
            }
        }

        if (timedOutMonitor is null)
        {
            return;
        }

        timedOutMonitor.Dispose();
        var expectedState = timedOutMonitor.Action == WorkflowAction.StartPlc
            ? "Starting or Running"
            : "Stopping or Stopped";
        var message =
            $"PLC {timedOutMonitor.Action} command succeeded, but no {expectedState} runtime state was observed before timeout.";
        logger.LogWarning(
            "PLC command state observation timed out. Action={Action}, ExpectedState={ExpectedState}, CurrentState={CurrentState}",
            timedOutMonitor.Action,
            expectedState,
            GetCurrentPlcState().OperatingState);
        diagnosticReporter.Add(diagnosticReporter.Create(
            "Workflow.PlcCommand.StateObservationTimeout",
            DiagnosticCategory.Workflow,
            LogLevel.Warning,
            message,
            phase: DiagnosticPhase.RuntimeStartup));
    }

    private void ResetObservedPlcState()
    {
        PlcCommandObservationMonitor? monitor;
        lock (plcStateSync)
        {
            currentPlcState = new PlcState(
                RuntimeInstanceState.Unavailable,
                PlcOperatingState.Unavailable,
                Array.Empty<RuntimeTargetRef>(),
                ReadyForTransfer: false,
                ReadyForExecution: false);
            plcStateObservationSequence = 0;
            monitor = plcCommandObservationMonitor;
            plcCommandObservationMonitor = null;
        }

        monitor?.Dispose();
    }

    private static bool IsExpectedPlcCommandState(
        WorkflowAction action,
        PlcOperatingState operatingState)
    {
        return action switch
        {
            WorkflowAction.StartPlc => operatingState is PlcOperatingState.Starting or PlcOperatingState.Running,
            WorkflowAction.StopPlc => operatingState is PlcOperatingState.Stopping or PlcOperatingState.Stopped,
            _ => false
        };
    }

    private async Task ReleaseUnitTestCommunicationAfterObservedPlcUnavailableAsync(PlcState state)
    {
        communicationAutoPrepareInProgress = false;
        if (!HasUnitTestCommunicationContext())
        {
            return;
        }

        if (Interlocked.Exchange(ref observedPlcUnavailableReleaseInProgress, 1) == 1)
        {
            ClearUnitTestCommunicationContext();
            return;
        }

        logger.LogInformation(
            "Observed PLC state is not ready for execution. InstanceState={InstanceState}, OperatingState={OperatingState}; releasing UnitTest communication.",
            state.InstanceState,
            state.OperatingState);

        using var releaseCancellation = new CoreOperationCancellationScope(
            coreContext.TimeProvider,
            applicationOperationTimeouts.Monitoring,
            CancellationToken.None);
        try
        {
            await ReleaseUnitTestCommunicationCoreAsync(
                releaseCancellation.Token,
                preserveOrInvalidateRunObservation: true);
        }
        catch (OperationCanceledException ex) when (releaseCancellation.DeadlineExpired)
        {
            logger.LogError(
                ex,
                "Observed PLC-state cleanup timed out. InstanceState={InstanceState}, OperatingState={OperatingState}, Timeout={Timeout}",
                state.InstanceState,
                state.OperatingState,
                releaseCancellation.Timeout);
            diagnosticReporter.Add(diagnosticReporter.Create(
                "Workflow.PlcState.UnitTestCommunicationRelease.TimedOut",
                DiagnosticCategory.Cleanup,
                LogLevel.Error,
                $"UnitTest communication release after observed PLC state change timed out after {releaseCancellation.Timeout}.",
                ex.Message));
            ClearUnitTestCommunicationContext();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Observed PLC-state cleanup failed. InstanceState={InstanceState}, OperatingState={OperatingState}",
                state.InstanceState,
                state.OperatingState);
            diagnosticReporter.Add(diagnosticReporter.Create(
                "Workflow.PlcState.UnitTestCommunicationRelease.Failed",
                DiagnosticCategory.Cleanup,
                LogLevel.Error,
                "UnitTest communication release after observed PLC state change failed.",
                ex.Message));
            ClearUnitTestCommunicationContext();
        }
        finally
        {
            Interlocked.Exchange(ref observedPlcUnavailableReleaseInProgress, 0);
        }
    }

    private bool HasUnitTestCommunicationContext()
    {
        return communicationSession is not null
            || unitTestContext is not null
            || lifecycleObservation is not null
            || eventStreamState is not null
            || monitoringActive;
    }

    private async Task<OperationResult> PrepareUnitTestCommunicationCoreAsync(
        CancellationToken cancellationToken,
        Action? exceptionObserved = null)
    {
        var projectContext = new ProjectExecutionContext(
            projectOwnershipMode,
            project,
            projectTarget);
        var result = await ExecuteProfileCallAsync(
            AccessServiceKind.Profile,
            "PrepareUnitTestCommunication",
            DiagnosticPhase.CommunicationValidation,
            cancellationToken,
                () => profile!.PrepareUnitTestCommunicationAsync(
                    new CommunicationPrepareRequest(
                        setup!.AccessProfile,
                    projectContext,
                    runtime!,
                        runtimeTarget!,
                        setup.Execution.Timeouts.ToCommunicationTimeoutPolicy()),
                    cancellationToken),
                exceptionObserved);

        if (result.Value is not null)
        {
            communicationSession = result.Value;
            unitTestContext = new UnitTestContext(result.Value, projectContext, runtime!, runtimeTarget!);
            SetActivityCommunicationContext(result.Value.SessionId);
            lifecycleObservation = profile!.UnitTestLifecycle.CurrentObservation;
            testRunMonitor.RecordLifecycleObservation(lifecycleObservation);
        }

        return result.IsSuccessful
            ? OperationResult.Success(result.Message)
            : OperationResult.Failure(result.Message ?? "UnitTest communication preparation failed.");
    }

    private async Task AutoPrepareUnitTestCommunicationAsync()
    {
        if (!communicationAutoPreparePending
            || communicationAutoPrepareInProgress
            || operationInProgress
            || unitTestContext is not null
            || profile is null
            || !IsProjectSetupSatisfied()
            || runtime is null
            || runtimeTarget is null
            || !GetCurrentPlcState().ReadyForExecution)
        {
            return;
        }

        communicationAutoPreparePending = false;
        communicationAutoPrepareInProgress = true;
        var operationId = Guid.NewGuid().ToString("N");
        using var operation = TryBeginActiveWorkflowOperation(
            WorkflowAction.PrepareUnitTestCommunication,
            operationId,
            CancellationToken.None);
        if (operation is null)
        {
            communicationAutoPrepareInProgress = false;
            return;
        }

        logger.LogInformation(
            "Auto-preparing UnitTest communication after observed PLC running state. OperationId={OperationId}",
            operationId);

        try
        {
            var prepareExceptionNormalized = false;
            OperationResult result;
            using (var operationCancellation = new CoreOperationCancellationScope(
                coreContext.TimeProvider,
                setup!.Execution.Timeouts.CommunicationPrepare,
                operation.Token))
            {
                try
                {
                    result = await PrepareUnitTestCommunicationCoreAsync(
                        operationCancellation.Token,
                        () => prepareExceptionNormalized = true);
                    result = NormalizeWorkflowResult(
                        WorkflowAction.PrepareUnitTestCommunication,
                        "CommunicationValidation",
                        result,
                        operationCancellation);
                }
                catch (OperationCanceledException ex)
                {
                    result = ClassifyCancellation(
                        WorkflowAction.PrepareUnitTestCommunication,
                        "CommunicationValidation",
                        ex,
                        operationCancellation);
                    prepareExceptionNormalized = true;
                }
            }

            if (result.IsSuccessful)
            {
                CompleteActiveWorkflowOperation(operation);
                Publish(RunWorkflowState.UnitTestCommunicationPrepared);
                logger.LogInformation(
                    "Auto-prepared UnitTest communication. OperationId={OperationId}, Message={Message}",
                    operationId,
                    result.Message);

                if (IsActionAvailable(WorkflowAction.StartMonitoring))
                {
                    var monitoringExceptionNormalized = false;
                    var monitoringResult = await StartMonitoringAsync(
                        new WorkflowActionPolicy(applicationOperationTimeouts.Monitoring),
                        CancellationToken.None,
                        () => monitoringExceptionNormalized = true);
                    if (!monitoringResult.Result.IsSuccessful && !monitoringExceptionNormalized)
                    {
                        diagnosticReporter.Add(diagnosticReporter.Create(
                            "Workflow.EventStream.MonitoringStart.Failed",
                            DiagnosticCategory.Workflow,
                            LogLevel.Error,
                            monitoringResult.Result.Message ?? "UnitTest event-stream monitoring start failed.",
                            phase: DiagnosticPhase.ExecutionMonitoring));
                    }
                }
            }
            else
            {
                if (!prepareExceptionNormalized)
                {
                    diagnosticReporter.Add(diagnosticReporter.Create(
                        "Workflow.Communication.Prepare.Failed",
                        DiagnosticCategory.Workflow,
                        LogLevel.Error,
                        result.Message ?? "UnitTest communication preparation failed.",
                        phase: DiagnosticPhase.CommunicationValidation));
                }

                Publish(RunWorkflowState.Error);
                logger.LogWarning(
                    "Auto-preparing UnitTest communication failed. OperationId={OperationId}, Message={Message}",
                    operationId,
                    result.Message);
            }
        }
        catch (Exception ex)
        {
            Publish(RunWorkflowState.Error);
            logger.LogError(
                ex,
                "Auto-preparing UnitTest communication failed with exception. OperationId={OperationId}",
                operationId);
            diagnosticReporter.Add(diagnosticReporter.Create(
                "Workflow.Communication.AutoPrepare.ProcessingFailed",
                DiagnosticCategory.Workflow,
                LogLevel.Error,
                "Automatic UnitTest communication preparation could not be completed.",
                $"{ex.GetType().FullName}: {ex.Message}",
                DiagnosticPhase.CommunicationValidation));
        }
        finally
        {
            communicationAutoPrepareInProgress = false;
            CompleteActiveWorkflowOperation(operation);
            Publish(Current.State);
        }
    }

    private ActiveWorkflowOperation? TryBeginActiveWorkflowOperation(
        WorkflowAction action,
        string operationId,
        CancellationToken callerCancellationToken)
    {
        lock (activeOperationSync)
        {
            if (activeWorkflowOperation is not null
                || sessionReleaseDepth > 0 && !IsReleaseAction(action))
            {
                return null;
            }

            var operation = new ActiveWorkflowOperation(action, operationId, callerCancellationToken);
            activeWorkflowOperation = operation;
            return operation;
        }
    }

    private static bool IsReleaseAction(WorkflowAction action)
    {
        return action is WorkflowAction.ReleaseUnitTestCommunication
            or WorkflowAction.ReleaseRuntime
            or WorkflowAction.ReleaseProject
            or WorkflowAction.UnloadAccessProfile
            or WorkflowAction.Cleanup;
    }

    private void CompleteActiveWorkflowOperation(ActiveWorkflowOperation operation)
    {
        lock (activeOperationSync)
        {
            if (ReferenceEquals(activeWorkflowOperation, operation))
            {
                activeWorkflowOperation = null;
            }
        }

        operation.Completion.TrySetResult();
    }

    private async Task<bool> CleanupLoadedProfileAsync(CancellationToken cancellationToken)
    {
        var cleanupSucceeded = true;

        if (profile is not null)
        {
            cleanupSucceeded &= await CleanupStepAsync(
                "Workflow.Cleanup.EventStream.Failed",
                "UnitTest event-stream cleanup failed.",
                () => profile.UnitTestEventStream.CleanupAsync(cancellationToken),
                cancellationToken);
            cleanupSucceeded &= await CleanupStepAsync(
                "Workflow.Cleanup.Lifecycle.Failed",
                "UnitTest lifecycle cleanup failed.",
                () => profile.UnitTestLifecycle.CleanupAsync(cancellationToken),
                cancellationToken);
            cleanupSucceeded &= await CleanupStepAsync(
                "Workflow.Cleanup.PlcControl.Failed",
                "PLC control cleanup failed.",
                () => profile.PlcControl.CleanupAsync(cancellationToken),
                cancellationToken);
            cleanupSucceeded &= await CleanupStepAsync(
                "Workflow.Cleanup.Project.Failed",
                "PLC project cleanup failed.",
                () => profile.Project.CleanupAsync(cancellationToken),
                cancellationToken);
        }

        try
        {
            if (profile is not null)
            {
                if (ownsBoundAccessProfileSession && boundAccessProfileSession is not null)
                {
                    await boundAccessProfileSession.DisposeAsync();
                }

                ClearBoundAccessProfileSession();
            }
        }
        catch (Exception ex)
        {
            cleanupSucceeded = false;
            diagnosticReporter.Add(diagnosticReporter.Create(
                "Workflow.Cleanup.ProfileUnload.Failed",
                DiagnosticCategory.Cleanup,
                LogLevel.Error,
                "Access profile unload failed.",
                ex.Message));
        }

        return cleanupSucceeded;
    }

    private async Task<bool> ReleaseUnitTestCommunicationCoreAsync(
        CancellationToken cancellationToken,
        bool preserveOrInvalidateRunObservation = false)
    {
        var releaseSucceeded = true;

        try
        {
            if (profile is not null)
            {
                if (monitoringActive)
                {
                    releaseSucceeded &= await CleanupStepAsync(
                        "Workflow.Release.EventStream.StopMonitoring.Failed",
                        "UnitTest event-stream monitoring stop failed.",
                        () => profile.UnitTestEventStream.StopMonitoringAsync(cancellationToken),
                        cancellationToken);
                }

                var observationCompletionReason = EventStreamCompletionReason.Cleanup;
                if (preserveOrInvalidateRunObservation
                    && ShouldInvalidateActiveRunObservation(testRunMonitor.Current))
                {
                    testRunMonitor.InvalidateMonitoring("PLC runtime became unavailable while a test run was active.");
                    observationCompletionReason = EventStreamCompletionReason.TargetInvalidated;
                }

                eventStreamProcessor.CompleteObservation(
                    observationCompletionReason,
                    coreContext.TimeProvider.GetUtcNow());
                await testRunMonitor.StopMonitoringAsync(cancellationToken);

                releaseSucceeded &= await CleanupStepAsync(
                    "Workflow.Release.EventStreamCommunication.Failed",
                    "UnitTest event-stream communication release failed.",
                    () => profile.UnitTestEventStream.ReleaseCommunicationAsync(cancellationToken),
                    cancellationToken);
                releaseSucceeded &= await CleanupStepAsync(
                    "Workflow.Release.LifecycleCommunication.Failed",
                    "UnitTest lifecycle communication release failed.",
                    () => profile.UnitTestLifecycle.ReleaseCommunicationAsync(cancellationToken),
                    cancellationToken);
            }
        }
        finally
        {
            ClearUnitTestCommunicationContext();
        }

        return releaseSucceeded;
    }

    private void InvalidateActiveRunForRuntimeReconnect()
    {
        if (ShouldInvalidateActiveRunObservation(testRunMonitor.Current))
        {
            testRunMonitor.InvalidateMonitoring(
                "Runtime reconnect interrupted the active UnitTest run observation.");
        }
    }

    private RunEpochId? GetMonitoringRunEpochBaseline()
    {
        var currentRun = eventStreamProcessor.Current;
        return currentRun?.State is UnitTestRunState.Completed
                or UnitTestRunState.Aborted
                or UnitTestRunState.TimedOut
            ? currentRun.EpochId
            : null;
    }

    private async Task ResetUnitTestCommunicationContextAfterProfileReconnectAsync()
    {
        eventStreamProcessor.CompleteObservation(
            EventStreamCompletionReason.TargetInvalidated,
            coreContext.TimeProvider.GetUtcNow());
        await testRunMonitor.StopMonitoringAsync(CancellationToken.None);
        ClearUnitTestCommunicationContext();
    }

    private static bool ShouldInvalidateActiveRunObservation(TestRunMonitoringState state)
    {
        return state is TestRunMonitoringState.RunActive or TestRunMonitoringState.CompletionPending;
    }

    private async Task<bool> CleanupWorkflowAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cleanupSucceeded = true;
            cleanupSucceeded &= await ReleaseUnitTestCommunicationCoreAsync(cancellationToken);
            if (runtime is not null && runtimeTarget is not null && setup is not null)
            {
                cleanupSucceeded &= (await ReleaseRuntimeCoreAsync(cancellationToken)).IsSuccessful;
            }
            cleanupSucceeded &= await CleanupLoadedProfileAsync(cancellationToken);
            return cleanupSucceeded;
        }
        finally
        {
            ClearBoundAccessProfileSession();
            ClearPreparedContexts(clearSetup: true);
        }
    }

    private async Task<OperationResult> ReleaseRuntimeCoreAsync(CancellationToken cancellationToken)
    {
        if (profile is null || setup is null || runtime is null || runtimeTarget is null)
        {
            return OperationResult.Success("PLC runtime is already released.");
        }

        var releaseMode = setup.RuntimeProvisioningMode == RuntimeProvisioningMode.CreateOwned
            ? RuntimeResourceReleaseMode.DestroyResource
            : RuntimeResourceReleaseMode.KeepResource;
        var result = await ExecuteProfileCallAsync(
            AccessServiceKind.PlcControl,
            "ReleaseRuntime",
            DiagnosticPhase.Cleanup,
            cancellationToken,
            () => profile.PlcControl.ReleaseRuntimeAsync(
                new PlcRuntimeReleaseRequest(runtime, runtimeTarget, releaseMode),
                cancellationToken));
        var hasTerminalResourceOutcome = result.Value is PlcRuntimeReleaseOutcome.ResourceKept
            or PlcRuntimeReleaseOutcome.ResourceDestroyed
            or PlcRuntimeReleaseOutcome.ResourcePartiallyDestroyed;
        if (!result.IsSuccessful && !hasTerminalResourceOutcome)
        {
            return OperationResult.Failure(result.Message ?? "Runtime release failed.");
        }

        if (hasTerminalResourceOutcome)
        {
            eventStreamProcessor.Reset();
            runtime = null;
            runtimeTarget = null;
            if (projectSetupState == ProjectSetupState.ManagedTransferCompleted)
            {
                projectSetupState = projectAccessActive
                    ? ProjectSetupState.ManagedProjectPrepared
                    : ProjectSetupState.NotEstablished;
            }
        }

        return result.IsSuccessful
            ? OperationResult.Success(result.Message)
            : OperationResult.Failure(result.Message ?? "Runtime release did not reach the requested resource state.");
    }

    private void OnDiagnosticPublished(Diagnostic diagnostic)
    {
        Current = CreatePublication(Current.State, Current.ActiveOperation);
        PublicationEventHandler?.Invoke(this, new RunWorkflowPublicationEventArgs(Current));
    }

    private Diagnostic ApplyActivityCorrelation(Diagnostic diagnostic)
    {
        var context = evidenceAnalysis.TraceContext.Current;
        var currentRun = eventStreamProcessor.Current;
        RunEpochId? activeRunEpochId = currentRun is not null && !IsTerminalRunState(currentRun.State)
            ? currentRun.EpochId
            : null;

        var correlation = diagnostic.Correlation;
        return diagnostic with
        {
            Correlation = correlation with
            {
                SessionId = correlation.SessionId ?? context.ApplicationSessionId,
                UnitTestRunEpochId = correlation.UnitTestRunEpochId ?? activeRunEpochId,
                LifecycleObservationId = correlation.LifecycleObservationId ?? context.LifecycleObservationId,
                EventStreamContextId = correlation.EventStreamContextId ?? context.EventStreamContextId
            }
        };
    }

    private void ApplyAccessHealthUpdate(AccessHealthMonitorUpdateEventArgs update)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var serviceUpdate in update.Updates)
        {
            if (ShouldReportAccessHealthDiagnostic(serviceUpdate))
            {
                diagnostics.Add(diagnosticReporter.Create(
                    $"Workflow.AccessHealth.{serviceUpdate.ServiceKind}.{serviceUpdate.Health.State}",
                    DiagnosticCategory.Workflow,
                    LogLevel.Error,
                    $"{serviceUpdate.ServiceKind} access health changed to {serviceUpdate.Health.State}: {serviceUpdate.Health.Message}",
                    serviceUpdate.Health.ReasonCode,
                    DiagnosticPhase.Configuration));
            }

            if (ShouldInvalidateActiveObservation(serviceUpdate))
            {
                testRunMonitor.InvalidateMonitoring(
                    $"{serviceUpdate.ServiceKind} access health invalidated active observation: {serviceUpdate.Health.Message}");
            }
        }

        diagnosticReporter.AddRange(diagnostics);
        Publish(Current.State);
    }

    private bool ShouldReportAccessHealthDiagnostic(AccessServiceHealthUpdate update)
    {
        if (Current.State == RunWorkflowState.CleaningUp)
        {
            return false;
        }

        if (update.Health.State is AccessHealthState.NotEstablished
            or AccessHealthState.Healthy
            or AccessHealthState.RevalidationRequired
            or AccessHealthState.Disposed)
        {
            return false;
        }

        return setup is not null || project is not null || runtime is not null || unitTestContext is not null || monitoringActive;
    }

    private bool ShouldInvalidateActiveObservation(AccessServiceHealthUpdate update)
    {
        if (update.Health.State is not AccessHealthState.Invalidated)
        {
            return false;
        }

        if (IsExportableRun(testRunMonitor.Current, eventStreamProcessor.Current))
        {
            return false;
        }

        if (!monitoringActive && testRunMonitor.Current is TestRunMonitoringState.NotMonitoring or TestRunMonitoringState.Completed)
        {
            return false;
        }

        return update.ServiceKind is AccessServiceKind.Profile
            or AccessServiceKind.PlcControl
            or AccessServiceKind.UnitTestLifecycle
            or AccessServiceKind.UnitTestEventStream;
    }

    private void PublishMonitorUpdate(TestRunMonitorUpdate update)
    {
        if (update.Kind == TestRunMonitorUpdateKind.NoChange)
        {
            return;
        }

        ApplyMonitorUpdateToEventStreamProcessor(update);

        var state = update.State switch
        {
            TestRunMonitoringState.WaitingForRun => RunWorkflowState.Monitoring,
            TestRunMonitoringState.RunActive => RunWorkflowState.RunActive,
            TestRunMonitoringState.Completed => RunWorkflowState.RunCompleted,
            TestRunMonitoringState.Aborted => RunWorkflowState.Monitoring,
            TestRunMonitoringState.TimedOut or TestRunMonitoringState.Invalidated => RunWorkflowState.Error,
            _ => Current.State
        };

        logger.Log(
            update.Kind == TestRunMonitorUpdateKind.EvidenceUpdated ? LogLevel.Debug : LogLevel.Information,
            "Test run monitor update published. Kind={Kind}, MonitorState={MonitorState}, WorkflowState={WorkflowState}, DiagnosticCount={DiagnosticCount}",
            update.Kind,
            update.State,
            state,
            update.Diagnostics.Count);
        Publish(state);
    }

    private void ApplyAcceptedUnitTestCommand(WorkflowAction action)
    {
        if (action == WorkflowAction.StartUnitTest && setup is not null)
        {
            return;
        }

        if (action != WorkflowAction.ResetUnitTest)
        {
            return;
        }

        eventStreamProcessor.CompleteCurrentEpoch(
            EventStreamCompletionReason.ResetObserved,
            coreContext.TimeProvider.GetUtcNow());
        eventStreamProcessor.ClearCurrentEpoch();
    }

    private void ApplyMonitorUpdateToEventStreamProcessor(TestRunMonitorUpdate update)
    {
        switch (update.Kind)
        {
            case TestRunMonitorUpdateKind.TimeoutDetected:
                eventStreamProcessor.CompleteCurrentEpoch(
                    EventStreamCompletionReason.Timeout,
                    coreContext.TimeProvider.GetUtcNow());
                break;
            case TestRunMonitorUpdateKind.Invalidated:
                eventStreamProcessor.CompleteCurrentEpoch(
                    EventStreamCompletionReason.TargetInvalidated,
                    coreContext.TimeProvider.GetUtcNow());
                break;
        }
    }

    private static bool ContainsRunControlCompletionAnchor(UnitTestEventBatch batch)
    {
        return batch.Events.Any(static entry =>
            entry.OwnerScope == UnitTestOwnerScope.RunControl
            && entry.EventType == UnitTestEventType.StateTransition
            && entry.State == UnitTestLifecycleState.Completed);
    }

    private void StartActivitySession()
    {
        var sessionId = new ApplicationSessionId(Guid.NewGuid().ToString("N"));
        evidenceAnalysis.StartSession(sessionId);
        lock (activityCorrelationSync)
        {
            lastCapturedPlcState = null;
            pendingPlcStartActivity = null;
        }
    }

    private void EndActivitySession()
    {
        var sessionId = evidenceAnalysis.TraceContext.Current.ApplicationSessionId;
        lock (activityCorrelationSync)
        {
            lastCapturedPlcState = null;
            pendingPlcStartActivity = null;
        }

        if (sessionId is not null)
        {
            evidenceAnalysis.EndSession(sessionId.Value);
        }
    }

    private LifecycleObservationId BeginCommandActivity(
        UnitTestActivityCommandKind command,
        string operationId)
    {
        var observationId = new LifecycleObservationId(Guid.NewGuid().ToString("N"));
        SetActivityLifecycleObservation(observationId);
        lock (activityCorrelationSync)
        {
            pendingPlcStartActivity = command == UnitTestActivityCommandKind.PlcStart ? observationId : null;
        }

        RecordActivity(
            new CommandActivityPayload(command, UnitTestActivityCommandPhase.Requested),
            lifecycleObservationId: observationId,
            operationId: new OperationId(operationId));
        return observationId;
    }

    private void CompleteCommandActivity(
        UnitTestActivityCommandKind command,
        LifecycleObservationId observationId,
        string operationId,
        OperationResultState resultState,
        bool? accepted,
        string? message)
    {
        RecordActivity(
            new CommandActivityPayload(
                command,
                UnitTestActivityCommandPhase.Completed,
                resultState,
                accepted,
                message),
            lifecycleObservationId: observationId,
            operationId: new OperationId(operationId));
    }

    private void RecordPlcState(PlcState state)
    {
        LifecycleObservationId? startObservation;
        lock (activityCorrelationSync)
        {
            if (HasSamePlcState(lastCapturedPlcState, state))
            {
                return;
            }

            lastCapturedPlcState = state;
            startObservation = pendingPlcStartActivity;
            if (state.OperatingState == PlcOperatingState.Running)
                pendingPlcStartActivity = null;
        }

        if (state.OperatingState == PlcOperatingState.Running)
        {
            SetActivityLifecycleObservation(startObservation ?? new LifecycleObservationId(Guid.NewGuid().ToString("N")));
        }

        var observationId = evidenceAnalysis.TraceContext.Current.LifecycleObservationId;
        RecordActivity(new PlcStateActivityPayload(state), lifecycleObservationId: observationId);
    }

    private void RecordActivity(
        UnitTestActivityPayload payload,
        CommunicationSessionId? communicationSessionId = null,
        RunEpochId? runEpochId = null,
        LifecycleObservationId? lifecycleObservationId = null,
        EventStreamContextId? eventStreamContextId = null,
        OperationId? operationId = null)
    {
        communicationSessionId ??= communicationSession?.SessionId;
        lifecycleObservationId ??= payload is DiagnosticActivityPayload diagnostic
            ? diagnostic.Diagnostic.Correlation.LifecycleObservationId
            : null;
        operationId ??= payload is DiagnosticActivityPayload diagnosticPayload
            ? diagnosticPayload.Diagnostic.Correlation.OperationId
            : null;
        runEpochId ??= payload is DiagnosticActivityPayload diagnosticEntry
            ? diagnosticEntry.Diagnostic.Correlation.UnitTestRunEpochId
            : null;

        evidenceAnalysis.TraceSink.TryRecord(
            payload,
            new UnitTestActivityCorrelation(
                communicationSessionId,
                runEpochId,
                lifecycleObservationId,
                eventStreamContextId,
                operationId));
    }

    private void SetActivityCommunicationContext(CommunicationSessionId communicationSessionId)
    {
        var current = evidenceAnalysis.TraceContext.Current;
        if (current.ApplicationSessionId is not { } sessionId)
        {
            return;
        }

        evidenceAnalysis.TraceContext.TrySetCommunicationSession(sessionId, communicationSessionId);
        evidenceAnalysis.TraceContext.TrySetEventStreamContext(
            sessionId,
            new EventStreamContextId(communicationSessionId.Value));
    }

    private void ClearActivityCommunicationContext()
    {
        var current = evidenceAnalysis.TraceContext.Current;
        if (current.ApplicationSessionId is not { } sessionId)
        {
            return;
        }

        evidenceAnalysis.TraceContext.TrySetCommunicationSession(sessionId, null);
    }

    private void SetActivityLifecycleObservation(LifecycleObservationId observationId)
    {
        var current = evidenceAnalysis.TraceContext.Current;
        if (current.ApplicationSessionId is { } sessionId)
        {
            evidenceAnalysis.TraceContext.TrySetLifecycleObservation(sessionId, observationId);
        }
    }

    private static UnitTestActivityCommandKind ToActivityCommand(WorkflowAction action)
    {
        return action switch
        {
            WorkflowAction.StartUnitTest => UnitTestActivityCommandKind.Start,
            WorkflowAction.AbortUnitTest => UnitTestActivityCommandKind.Abort,
            WorkflowAction.ResetUnitTest => UnitTestActivityCommandKind.Reset,
            WorkflowAction.ResetEventBuffer => UnitTestActivityCommandKind.EventBufferReset,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Workflow action is not a UnitTest activity command.")
        };
    }

    private static bool HasSamePlcState(PlcState? previous, PlcState current)
    {
        return previous is not null
            && previous.InstanceState == current.InstanceState
            && previous.OperatingState == current.OperatingState
            && previous.ReadyForTransfer == current.ReadyForTransfer
            && previous.ReadyForExecution == current.ReadyForExecution
            && previous.RuntimeTargets.Select(static target => target.Id)
                .SequenceEqual(current.RuntimeTargets.Select(static target => target.Id));
    }

    private static bool IsTerminalRunState(UnitTestRunState state)
    {
        return state is UnitTestRunState.Completed
            or UnitTestRunState.Aborted
            or UnitTestRunState.TimedOut
            or UnitTestRunState.Invalidated;
    }

    private async Task<bool> CleanupStepAsync(
        string code,
        string message,
        Func<Task<OperationResult>> cleanup,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogDebug("Cleanup step started. DiagnosticCode={DiagnosticCode}", code);
            var result = await cleanup();
            if (result.IsSuccessful)
            {
                logger.LogInformation("Cleanup step finished. DiagnosticCode={DiagnosticCode}", code);
                return true;
            }

            logger.LogWarning(
                "Cleanup step reported failure. DiagnosticCode={DiagnosticCode}, Message={Message}",
                code,
                result.Message);
            diagnosticReporter.Add(diagnosticReporter.Create(
                code,
                DiagnosticCategory.Cleanup,
                LogLevel.Error,
                result.Message ?? message));
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cleanup step failed with exception. DiagnosticCode={DiagnosticCode}", code);
            diagnosticReporter.Add(diagnosticReporter.Create(
                code,
                DiagnosticCategory.Cleanup,
                LogLevel.Error,
                message,
                ex.Message));
            return false;
        }
    }

    private void Publish(RunWorkflowState state)
    {
        Current = CreatePublication(state, Current.ActiveOperation);
        PublicationEventHandler?.Invoke(this, new RunWorkflowPublicationEventArgs(Current));
    }

    private RunWorkflowPublication CreatePublication(
        RunWorkflowState state,
        ActiveOperation? activeOperation = null)
    {
        var sequence = Interlocked.Increment(ref publicationSequence);
        var run = eventStreamProcessor.Current;
        var status = CreateStatusSnapshot(state, activeOperation, run);
        return new RunWorkflowPublication(
            sequence,
            state,
            activeOperation,
            status,
            run,
            testRunMonitor.Current,
            lifecycleObservation,
            eventStreamState,
            projectTargetDiscovery,
            runtimeTargetDiscovery,
            gateEvaluator.EvaluateAll(status),
            coreContext.Diagnostics.Reader.Current);
    }

    private WorkflowStatusSnapshot CreateStatusSnapshot(
        RunWorkflowState state,
        ActiveOperation? activeOperation)
        => CreateStatusSnapshot(state, activeOperation, eventStreamProcessor.Current);

    private WorkflowStatusSnapshot CreateStatusSnapshot(
        RunWorkflowState state,
        ActiveOperation? activeOperation,
        UnitTestRunEpochSnapshot? run)
    {
        return new WorkflowStatusSnapshot(
            state,
            activeOperation,
            operationInProgress,
            profile is not null,
            boundAccessProfileSession?.SelectedProfile.ProfileId,
            boundAccessProfileSession?.SelectedProfile.Version,
            projectOwnershipMode,
            setup is not null,
            setup?.RuntimeProvisioningMode,
            projectTarget is not null,
            runtimeTarget is not null,
            projectSetupState,
            projectAccessActive,
            runtime is not null,
            GetCurrentPlcState(),
            unitTestContext is not null && communicationSession is not null,
            unitTestContext is not null || communicationSession is not null || monitoringActive,
            monitoringActive,
            lifecycleObservation,
            eventStreamState,
            testRunMonitor.Current,
            run,
            accessHealthMonitor.Current,
            IsExportableRun(testRunMonitor.Current, run));
    }

    private async Task ReleaseManagedProjectAccessAfterTransferAsync(WorkflowActionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy.Timeout,
                "Project release timeout must be greater than zero.");
        }

        using var releaseCancellation = new CoreOperationCancellationScope(
            coreContext.TimeProvider,
            policy.Timeout,
            CancellationToken.None);
        try
        {
            await CleanupStepAsync(
                "Workflow.Project.PostTransferRelease.Failed",
                "Project transfer succeeded, but active project access could not be released.",
                () => profile!.Project.ReleaseProjectAsync(releaseCancellation.Token),
                releaseCancellation.Token);
        }
        catch (OperationCanceledException) when (releaseCancellation.DeadlineExpired)
        {
            var message =
                $"Project transfer succeeded, but active project access was not released within {policy.Timeout}.";
            logger.LogWarning(
                "Post-transfer project release timed out. Timeout={Timeout}",
                policy.Timeout);
            diagnosticReporter.Add(diagnosticReporter.Create(
                "Workflow.Project.PostTransferRelease.TimedOut",
                DiagnosticCategory.Cleanup,
                LogLevel.Error,
                message,
                phase: DiagnosticPhase.Cleanup));
        }

        projectAccessActive = profile!.Project.CurrentProject is not null;
    }

    private bool IsProjectSetupSatisfied()
    {
        return projectSetupState is ProjectSetupState.ManagedTransferCompleted
            or ProjectSetupState.ExternalManagementAccepted;
    }

    private RunWorkflowState CreatePreparedWorkflowState()
    {
        if (projectSetupState == ProjectSetupState.ManagedTransferCompleted)
        {
            return RunWorkflowState.ProjectTransferred;
        }

        return runtime is not null
            ? RunWorkflowState.RuntimePrepared
            : RunWorkflowState.Configured;
    }

    private static bool IsExportableRun(
        TestRunMonitoringState monitorState,
        UnitTestRunEpochSnapshot? runEpoch)
    {
        return monitorState == TestRunMonitoringState.Completed
            && runEpoch?.State == UnitTestRunState.Completed
            && runEpoch.EvidenceState == EvidenceState.Complete;
    }

    private sealed class PlcCommandObservationMonitor : IDisposable
    {
        public PlcCommandObservationMonitor(
            Guid id,
            WorkflowAction action,
            long observationBaseline)
        {
            Id = id;
            Action = action;
            ObservationBaseline = observationBaseline;
        }

        public Guid Id { get; }

        public WorkflowAction Action { get; }

        public long ObservationBaseline { get; }

        public ITimer? Timer { get; set; }

        public void Dispose()
        {
            Timer?.Dispose();
            Timer = null;
        }
    }

    private sealed record PlcCommandObservationTimeoutCallback(
        RunWorkflowService Owner,
        Guid MonitorId);

    private sealed class ActiveWorkflowOperation : IDisposable
    {
        private readonly CancellationTokenSource cancellation;

        public ActiveWorkflowOperation(
            WorkflowAction action,
            string operationId,
            CancellationToken callerCancellationToken)
        {
            Action = action;
            OperationId = operationId;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
        }

        public WorkflowAction Action { get; }

        public string OperationId { get; }

        public CancellationToken Token => cancellation.Token;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Cancel()
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            cancellation.Dispose();
        }
    }

}
