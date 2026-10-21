using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.App.Wpf.Services;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Artifacts;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Output;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.App.Wpf.Dialogs;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Diagnostics;
using PLCSIM.UnitTest.Core.Output;
using PLCSIM.UnitTest.Core.Workflow;
using System.Globalization;
using System.IO;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private const string EmptyAnalysisReport = "No analysis generated yet.";
    private const string EmptyAnalysisStatus = "No analysis generated.";
    private readonly RunWorkflowService workflow;
    private readonly RunSessionReleaseService sessionRelease;
    private readonly AccessProfileSessionService accessProfileSessions;
    private readonly IApplicationProfileRegistryStore accessProfileRegistry;
    private readonly IApplicationProfileRegistrationService accessProfileRegistrations;
    private readonly IApplicationProfileSelectionService accessProfileSelections;
    private readonly ArtifactExportService artifactExports;
    private readonly EvidenceAnalysisPackageZipWriter evidenceAnalysisPackageWriter;
    private readonly IConfirmationDialogService confirmations;
    private readonly ISafetyProgramPasswordDialogService safetyProgramPasswordDialogService;
    private readonly ILogFileOpenService logFileOpenService;
    private readonly IProjectSourceFileDialogService projectSourceFileDialogService;
    private readonly IAnalysisReportFileDialogService analysisReportFileDialogService;
    private readonly ISettingsDialogService settingsDialogService;
    private readonly IWpfApplicationSettingsService applicationSettings;
    private readonly string applicationLogFilePath;
    private readonly DiagnosticServices diagnostics;
    private readonly WpfApplicationDiagnosticReporter diagnosticReporter;
    private readonly TimeProvider timeProvider;
    private readonly DiagnosticOutputTracker diagnosticOutputTracker = new();
    private readonly TerminalOutputPresenter terminalOutput = new();
    private readonly RunConfigurationLoadCoordinator configurationLoadCoordinator;
    private readonly RunResultTreePresenter runResultTree = new();
    private readonly CancellableOperationController cancellableOperation = new();
    private readonly ILogger<MainWindowViewModel> logger;
    private RunWorkflowState workflowState;
    private string activeOperation = "Idle";
    private bool activityIndicatorActive;
    private string workflowActiveOperation = "Idle";
    private bool workflowActivityIndicatorActive;
    private string profileStatus = "Not loaded";
    private string projectStatus = "No project target";
    private string plcStatus = "No PLC";
    private string runtimeStatus = "No runtime target";
    private ProcessStatusViewState plcProcessStatus = WorkflowStatusViewStateMapper.NotReadyProcessStatus;
    private ProcessStatusViewState communicationStatus = WorkflowStatusViewStateMapper.DisconnectedProcessStatus;
    private ProcessStatusViewState monitoringStatus = WorkflowStatusViewStateMapper.OffProcessStatus;
    private ProcessStatusViewState runControlStatus = WorkflowStatusViewStateMapper.NotReadyProcessStatus;
    private TestRunMonitoringState runMonitorState = TestRunMonitoringState.NotMonitoring;
    private DiagnosticStatusKind diagnosticStatusKind = DiagnosticStatusKind.Clear;
    private string runStatus = "idle";
    private string diagnosticBadgeText = "0";
    private ProcessStatusViewState preparationStatus = WorkflowStatusViewStateMapper.NotPreparedProcessStatus;
    private string preparationPhase = "No preparation operation running.";
    private string terminalText = string.Empty;
    private string analysisReport = EmptyAnalysisReport;
    private string analysisStatus = EmptyAnalysisStatus;
    private string analysisRefreshStatus = EmptyAnalysisStatus;
    private string analysisFreshnessStatus = string.Empty;
    private string analysisExportFileName = "unittest-evidence-analysis.zip";
    private EvidenceAnalysisPackage? analysisPackage;
    private int selectedEvidenceTabIndex;
    private bool publicationOutputInitialized;
    private long lastAppliedWorkflowPublicationSequence;
    private TestRunMonitoringState? lastOutputRunMonitorState;
    private PlcOperatingState? lastOutputPlcOperatingState;
    private bool? lastOutputMonitoringActive;
    private bool? lastOutputCommunicationRevalidationRequired;
    private IReadOnlyList<ActionGateState> currentActionGates = [];
    private string logFilePath = WpfApplicationDefaults.LogFilePath;
    private string projectSourceDialogFilter = ProjectSourceFileDialogFilterBuilder.DefaultFilter;
    private ExportResult? testResultArtifact;
    private bool canBrowseProjectSource;
    private string testResultArtifactStatus = "Not requested";
    private string testResultArtifactPreview = "No test result artifact generated yet.";
    private string runResultStatus = "No run captured yet";
    private ushort runResultLostEventCount;
    private RunResultNodeViewModel? selectedRunResultNode;
    private string projectSource = string.Empty;
    private bool buildAndDownloadProject;
    private bool projectAccessActive;
    private AccessProfileConfig? importedAccessProfileDraft;
    private TimeoutConfiguration? importedTimeoutOverrides;
    private string? importedProjectTargetId;
    private string? importedRuntimeTargetId;
    private RuntimeProvisioningMode? importedProvisioningMode;
    private string importedConfigurationOperationName = "Import Configuration";
    private AccessProfileSelectionViewModel? selectedAccessProfile;
    private AccessProfileConfig? loadedAccessProfileConfig;
    private RunSetup? configuredRunSetup;
    private ProjectTargetSelectionViewModel? selectedProjectTarget;
    private RuntimeTargetSelectionViewModel? selectedRuntimeTarget;
    private DiagnosticViewModel? selectedDiagnostic;
    private string step1Status = "Select a project/profile source.";
    private string profileRegistrationNotice = string.Empty;
    private Visibility profileRegistrationNoticeVisibility = Visibility.Collapsed;
    private Visibility profileRegistrationActionVisibility = Visibility.Collapsed;
    private string step2Status = "Load an access profile before selecting PLC/runtime targets.";
    private string communicationPrepareTimeoutSeconds = "";
    private string communicationRevalidationTimeoutSeconds = "";
    private string sessionSettingsStatus = "";
    private string step1GateStatus = "LoadAccessProfile: not evaluated";
    private string step2GateStatus = "DiscoverRuntimeTargets: not evaluated";
    private string returnToStep1GateStatus = "Return to Step 1: not evaluated";
    private string returnToStep2GateStatus = "Return to Step 2: not evaluated";
    private string endSessionGateStatus = "End Session: not evaluated";
    private int activeStep = 1;
    private Visibility step1PanelVisibility = Visibility.Visible;
    private Visibility step2PanelVisibility = Visibility.Collapsed;
    private Visibility step3PanelVisibility = Visibility.Collapsed;
    private Visibility step4PanelVisibility = Visibility.Collapsed;
    private bool step2InputsEnabled;
    private bool configurationFileOperationPending;
    private bool contextReleaseInProgress;
    private Task? initializationTask;
    private bool isDisposed;

    public MainWindowViewModel(
        RunWorkflowService workflow,
        RunSessionReleaseService sessionRelease,
        AccessProfileSessionService accessProfileSessions,
        IApplicationProfileRegistryStore accessProfileRegistry,
        IApplicationProfileRegistrationService accessProfileRegistrations,
        IApplicationProfileSelectionService accessProfileSelections,
        ArtifactExportService artifactExports,
        IConfirmationDialogService confirmations,
        ILogger<MainWindowViewModel> logger,
        ILogFileOpenService? logFileOpenService = null,
        IConfigurationFileLoadService? configurationFileLoadService = null,
        IRunConfigurationFileDialogService? configurationFileDialogService = null,
        IProjectSourceFileDialogService? projectSourceFileDialogService = null,
        ISettingsDialogService? settingsDialogService = null,
        IWpfApplicationSettingsService? applicationSettings = null,
        DiagnosticServices? diagnostics = null,
        TimeProvider? timeProvider = null,
        IAnalysisReportFileDialogService? analysisReportFileDialogService = null,
        EvidenceAnalysisPackageZipWriter? evidenceAnalysisPackageWriter = null,
        ISafetyProgramPasswordDialogService? safetyProgramPasswordDialogService = null)
    {
        this.workflow = workflow;
        this.sessionRelease = sessionRelease;
        this.accessProfileSessions = accessProfileSessions;
        this.accessProfileRegistry = accessProfileRegistry;
        this.accessProfileRegistrations = accessProfileRegistrations;
        this.accessProfileSelections = accessProfileSelections;
        this.artifactExports = artifactExports;
        this.evidenceAnalysisPackageWriter = evidenceAnalysisPackageWriter ?? new EvidenceAnalysisPackageZipWriter();
        this.confirmations = confirmations;
        this.safetyProgramPasswordDialogService = safetyProgramPasswordDialogService
            ?? new SafetyProgramPasswordDialogService();
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.diagnostics = diagnostics ?? new DiagnosticServices(new DiagnosticSink());
        diagnosticReporter = new WpfApplicationDiagnosticReporter(this.diagnostics.Writer, this.timeProvider);
        this.logFileOpenService = logFileOpenService ?? new ShellLogFileOpenService();
        var resolvedConfigurationFileLoadService = configurationFileLoadService
            ?? throw new ArgumentNullException(nameof(configurationFileLoadService));
        var resolvedConfigurationFileDialogService = configurationFileDialogService
            ?? new RunConfigurationFileDialogService();
        this.projectSourceFileDialogService = projectSourceFileDialogService
            ?? new ProjectSourceFileDialogService();
        this.analysisReportFileDialogService = analysisReportFileDialogService
            ?? new AnalysisReportFileDialogService();
        this.applicationSettings = applicationSettings ?? WpfApplicationSettingsService.CreateDefault();
        applicationLogFilePath = this.applicationSettings.Current.LogFilePath;
        buildAndDownloadProject = this.applicationSettings.Current.BuildAndDownloadProjectByDefault;
        communicationPrepareTimeoutSeconds = Convert.ToInt32(
                this.applicationSettings.Current.RunTimeouts.CommunicationPrepare.TotalSeconds)
            .ToString(WpfApplicationDefaults.Culture);
        communicationRevalidationTimeoutSeconds = Convert.ToInt32(
                this.applicationSettings.Current.RunTimeouts.CommunicationRevalidation.TotalSeconds)
            .ToString(WpfApplicationDefaults.Culture);
        this.settingsDialogService = settingsDialogService ?? new SettingsDialogService(
            this.applicationSettings,
            new SettingsPathDialogService());
        configurationLoadCoordinator = new RunConfigurationLoadCoordinator(
            resolvedConfigurationFileLoadService,
            resolvedConfigurationFileDialogService,
            logger);
        ProfileConfiguration = new ProfileConfigurationEditorViewModel();

        workflow.PublicationEventHandler += OnWorkflowPublication;
        artifactExports.OutputStateChangedEventHandler += OnArtifactOutputStateChanged;
        this.diagnostics.Reader.SnapshotChangedEventHandler += OnDiagnosticSnapshotChanged;
        this.applicationSettings.SettingsChanged += OnApplicationSettingsChanged;
        cancellableOperation.StateChanged += OnCancellableOperationStateChanged;

        LoadStep1Command = new AsyncCommand(
            cancellationToken => ExecuteCancellableOperationAsync(
                "Loading access profile and discovering targets...",
                LoadSelectedAccessProfileAsync,
                () =>
                {
                    Step1Status = "Access profile loading and target discovery canceled.";
                    Step2Status = "Operation canceled. Use Back or End Session to recover.";
                    Step2InputsEnabled = false;
                },
                cancellationToken),
            CanLoadSelectedAccessProfile,
            logger);
        BrowseProjectSourceCommand = new AsyncCommand(
            BrowseProjectSourceAsync,
            CanBrowseProjectSource,
            logger);
        ClearProjectSourceCommand = new AsyncCommand(
            ClearProjectSourceAsync,
            CanClearProjectSource,
            logger);
        ConfigureStep2Command = new AsyncCommand(
            OpenProfileConfigurationAsync,
            CanOpenProfileConfiguration,
            logger);
        ConfigureProfileCommand = new AsyncCommand(
            cancellationToken => ExecuteCancellableOperationAsync(
                "Preparing runtime setup...",
                ConfigureSelectedTargetsAsync,
                () =>
                {
                    Step2Status = "Runtime setup canceled. Use Back or End Session to recover.";
                    Step2InputsEnabled = true;
                },
                cancellationToken),
            CanConfigureSelectedTargets,
            logger);
        ProfileConfiguration.StateChanged += (_, _) =>
        {
            ConfigureProfileCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(Step3GateStatus));
        };
        StartPlcCommand = new AsyncCommand(
            StartPlcAsync,
            CanStartPlc,
            logger);
        StopPlcCommand = new AsyncCommand(
            StopPlcAsync,
            CanStopPlc,
            logger);
        StartUnitTestCommand = new AsyncCommand(
            StartUnitTestAsync,
            CanStartUnitTest,
            logger);
        AbortUnitTestCommand = new AsyncCommand(
            AbortUnitTestAsync,
            CanAbortUnitTest,
            logger);
        ResetUnitTestCommand = new AsyncCommand(
            ResetUnitTestAsync,
            CanResetUnitTest,
            logger);
        ResetEventBufferCommand = new AsyncCommand(
            ResetEventBufferRequestedAsync,
            CanResetEventBuffer,
            logger);
        ReconnectRuntimeCommand = new AsyncCommand(
            ReconnectRuntimeRequestedAsync,
            CanReconnectRuntime,
            logger);
        ReturnToStep1Command = new AsyncCommand(
            ReturnToStep1Async,
            CanReturnToStep1,
            logger);
        ReturnToStep2Command = new AsyncCommand(
            ReturnToStep2Async,
            CanReturnToStep2,
            logger);
        ReturnToStep3Command = new AsyncCommand(
            ReturnToStep3Async,
            CanReturnToStep3,
            logger);
        EndSessionCommand = new AsyncCommand(
            EndSessionAsync,
            CanEndSession,
            logger);
        ImportConfigurationFileCommand = new AsyncCommand(
            ImportConfigurationFileAsync,
            CanSelectRunConfigurationFile,
            logger);
        LoadUnitTestFileCommand = new AsyncCommand(
            cancellationToken => ExecuteCancellableOperationAsync(
                "Loading UnitTest...",
                LoadUnitTestFileAsync,
                () =>
                {
                    if (activeStep == 1)
                    {
                        Step1Status = "Load UnitTest canceled.";
                    }
                    else
                    {
                        Step2Status = "Load UnitTest canceled. Use Back or End Session to recover.";
                    }
                },
                cancellationToken),
            CanSelectRunConfigurationFile,
            logger);
        RefreshProfileRegistrationsCommand = new AsyncCommand(
            cancellationToken => ExecuteCancellableOperationAsync(
                "Refreshing profile registrations...",
                RefreshProfileRegistrationsAsync,
                () => Step1Status = "Profile registration refresh canceled.",
                cancellationToken),
            CanRefreshProfileRegistrations,
            logger);
        CancelCurrentOperationCommand = new AsyncCommand(
            CancelCurrentOperationAsync,
            CanCancelCurrentOperation,
            logger);
        ClearOutputCommand = new AsyncCommand(
            ClearOutputAsync,
            CanClearOutput,
            logger);
        ClearDiagnosticsCommand = new AsyncCommand(
            ClearDiagnosticsAsync,
            CanClearDiagnostics,
            logger);
        ClearRunResultCommand = new AsyncCommand(
            ClearRunResultAsync,
            CanClearRunResult,
            logger);
        AnalyzeCurrentRunCommand = new AsyncCommand(
            AnalyzeCurrentRunAsync,
            CanAnalyzeCurrentRun,
            logger);
        AnalyzeLifecycleCommand = new AsyncCommand(
            AnalyzeLifecycleAsync,
            CanAnalyzeLifecycle,
            logger);
        AnalyzeCommunicationSessionCommand = new AsyncCommand(
            AnalyzeCommunicationSessionAsync,
            CanAnalyzeCommunicationSession,
            logger);
        AnalyzeSelectedDiagnosticCommand = new AsyncCommand(
            AnalyzeSelectedDiagnosticAsync,
            CanAnalyzeSelectedDiagnostic,
            logger);
        ExportAnalysisCommand = new AsyncCommand(
            ExportAnalysisAsync,
            CanUseAnalysisReport,
            logger);
        ClearAnalysisCommand = new AsyncCommand(
            ClearAnalysisAsync,
            CanClearAnalysis,
            logger);
        OpenApplicationLogCommand = new AsyncCommand(
            OpenApplicationLogAsync,
            CanOpenApplicationLog,
            logger);
        OpenAccessProfileLogCommand = new AsyncCommand(
            OpenAccessProfileLogAsync,
            CanOpenAccessProfileLog,
            logger);
        OpenLogFolderCommand = new AsyncCommand(
            OpenLogFolderAsync,
            CanOpenLogFolder,
            logger);
        OpenSettingsCommand = new AsyncCommand(
            OpenSettingsAsync,
            () => true,
            logger);
        OpenTestResultArtifactCommand = new AsyncCommand(
            OpenTestResultArtifactAsync,
            CanOpenTestResultArtifact,
            logger);
        OpenOutputFolderCommand = new AsyncCommand(
            OpenOutputFolderAsync,
            CanOpenOutputFolder,
            logger);

        ApplyApplicationSettings();
        ApplyPublication(workflow.Current);
    }

    public RunWorkflowState WorkflowState
    {
        get => workflowState;
        private set => SetProperty(ref workflowState, value);
    }

    public string ActiveOperation
    {
        get => activeOperation;
        private set => SetProperty(ref activeOperation, value);
    }

    public bool ActivityIndicatorActive
    {
        get => activityIndicatorActive;
        private set
        {
            if (SetProperty(ref activityIndicatorActive, value))
            {
                OnPropertyChanged(nameof(ActivityIndicatorVisibility));
                OnPropertyChanged(nameof(ActivityIndicatorOpacity));
            }
        }
    }

    public Visibility ActivityIndicatorVisibility => ActivityIndicatorActive
        ? Visibility.Visible
        : Visibility.Collapsed;

    public double ActivityIndicatorOpacity => ActivityIndicatorActive ? 1.0 : 0.0;

    public Visibility CancelCurrentOperationVisibility => !contextReleaseInProgress && cancellableOperation.IsActive
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string ProfileStatus
    {
        get => profileStatus;
        private set => SetProperty(ref profileStatus, value);
    }

    public string ProjectStatus
    {
        get => projectStatus;
        private set => SetProperty(ref projectStatus, value);
    }

    public string PlcStatus
    {
        get => plcStatus;
        private set => SetProperty(ref plcStatus, value);
    }

    public string RuntimeStatus
    {
        get => runtimeStatus;
        private set => SetProperty(ref runtimeStatus, value);
    }

    public ProcessStatusViewState PlcProcessStatusViewState
    {
        get => plcProcessStatus;
        private set => SetProcessStatusProperty(
            ref plcProcessStatus,
            value,
            nameof(PlcProcessStatusViewState),
            nameof(PlcProcessStatus),
            nameof(PlcProcessStatusBrush));
    }

    public string PlcProcessStatus => PlcProcessStatusViewState.DisplayText;

    public ProcessStatusViewState CommunicationStatusViewState
    {
        get => communicationStatus;
        private set => SetProcessStatusProperty(
            ref communicationStatus,
            value,
            nameof(CommunicationStatusViewState),
            nameof(CommunicationStatus),
            nameof(CommunicationStatusBrush));
    }

    public string CommunicationStatus => CommunicationStatusViewState.DisplayText;

    public ProcessStatusViewState MonitoringStatusViewState
    {
        get => monitoringStatus;
        private set => SetProcessStatusProperty(
            ref monitoringStatus,
            value,
            nameof(MonitoringStatusViewState),
            nameof(MonitoringStatus),
            nameof(MonitoringStatusBrush),
            nameof(MonitoringStatusToolTip));
    }

    public string MonitoringStatus => MonitoringStatusViewState.DisplayText;

    public string MonitoringStatusToolTip => MonitoringStatusViewState.ToolTip
        ?? "UnitTest event-stream evidence intake and monitoring state.";

    public ProcessStatusViewState RunControlStatusViewState
    {
        get => runControlStatus;
        private set => SetProcessStatusProperty(
            ref runControlStatus,
            value,
            nameof(RunControlStatusViewState),
            nameof(RunControlStatus),
            nameof(RunControlStatusBrush));
    }

    public string RunControlStatus => RunControlStatusViewState.DisplayText;

    public TestRunMonitoringState RunMonitorState
    {
        get => runMonitorState;
        private set => SetProperty(ref runMonitorState, value);
    }

    public string RunStatus
    {
        get => runStatus;
        private set => SetProperty(ref runStatus, value);
    }

    public DiagnosticStatusKind DiagnosticStatusKind
    {
        get => diagnosticStatusKind;
        private set => SetProperty(ref diagnosticStatusKind, value);
    }

    public string DiagnosticBadgeText
    {
        get => diagnosticBadgeText;
        private set => SetProperty(ref diagnosticBadgeText, value);
    }

    public ProcessStatusViewState PreparationStatusViewState
    {
        get => preparationStatus;
        private set => SetProcessStatusProperty(
            ref preparationStatus,
            value,
            nameof(PreparationStatusViewState),
            nameof(PreparationStatus),
            nameof(PreparationStatusBrush));
    }

    public string PreparationStatus => PreparationStatusViewState.DisplayText;

    public string PreparationPhase
    {
        get => preparationPhase;
        private set => SetProperty(ref preparationPhase, value);
    }

    public Brush PlcProcessStatusBrush => PlcProcessStatusViewState.MarkerBrush;

    public Brush ProfileStatusBrush => selectedAccessProfile is null
        ? Brushes.Gray
        : workflow.Current.State >= RunWorkflowState.AccessProfileLoaded
            ? Brushes.ForestGreen
            : Brushes.Gray;

    public Brush ProjectStatusBrush => string.IsNullOrWhiteSpace(projectSource)
        ? Brushes.Gray
        : Brushes.DodgerBlue;

    public Brush PlcStatusBrush => workflow.Current.Status.PlcState.InstanceState == RuntimeInstanceState.Faulted
        ? Brushes.Firebrick
        : workflow.Current.Status.PlcState.OperatingState switch
        {
            PlcOperatingState.Running => Brushes.ForestGreen,
            PlcOperatingState.Starting or PlcOperatingState.Stopping => Brushes.DodgerBlue,
            PlcOperatingState.Stopped or PlcOperatingState.Frozen or PlcOperatingState.Held => Brushes.Goldenrod,
            _ => Brushes.Gray
        };

    public Brush RuntimeStatusBrush => workflow.Current.Status.PlcState.InstanceState switch
    {
        RuntimeInstanceState.Faulted => Brushes.Firebrick,
        RuntimeInstanceState.Available when workflow.Current.Status.RuntimePrepared => Brushes.ForestGreen,
        _ => Brushes.Gray
    };

    public Brush CommunicationStatusBrush => CommunicationStatusViewState.MarkerBrush;

    public Brush MonitoringStatusBrush => MonitoringStatusViewState.MarkerBrush;

    public Brush RunControlStatusBrush => RunControlStatusViewState.MarkerBrush;

    public Brush PreparationStatusBrush => PreparationStatusViewState.MarkerBrush;

    public Brush DiagnosticStatusBrush => DiagnosticStatusKind switch
    {
        DiagnosticStatusKind.Information => Brushes.SteelBlue,
        DiagnosticStatusKind.Warning => Brushes.Goldenrod,
        DiagnosticStatusKind.Error => Brushes.Firebrick,
        _ => Brushes.Gray
    };

    public string TerminalText
    {
        get => terminalText;
        private set => SetProperty(ref terminalText, value);
    }

    public string AnalysisReport
    {
        get => analysisReport;
        private set
        {
            if (SetProperty(ref analysisReport, value))
            {
                OnPropertyChanged(nameof(CanCopyAnalysisReport));
                ExportAnalysisCommand.RaiseCanExecuteChanged();
                ClearAnalysisCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string AnalysisStatus
    {
        get => analysisStatus;
        private set => SetProperty(ref analysisStatus, value);
    }

    public string AnalysisRefreshStatus
    {
        get => analysisRefreshStatus;
        private set => SetProperty(ref analysisRefreshStatus, value);
    }

    public string AnalysisFreshnessStatus
    {
        get => analysisFreshnessStatus;
        private set => SetProperty(ref analysisFreshnessStatus, value);
    }

    public string AnalysisTitle => analysisPackage?.Analysis.Scope switch
    {
        EvidenceAnalysisScope.Run => "RUN ANALYSIS",
        EvidenceAnalysisScope.Lifecycle => "PLC LIFECYCLE ANALYSIS",
        EvidenceAnalysisScope.Session => "COMMUNICATION HEALTH ANALYSIS",
        _ => "EVIDENCE ANALYSIS"
    };

    public string AnalysisFocus => analysisPackage?.Analysis switch
    {
        { Scope: EvidenceAnalysisScope.Run, RunEpochId: { } runEpochId } => $"Run epoch {runEpochId.Value}",
        { Scope: EvidenceAnalysisScope.Lifecycle, LifecycleObservationId: { } observationId } => $"PLC lifecycle observation {observationId.Value}",
        { Scope: EvidenceAnalysisScope.Session } analysis => $"Application session {analysis.SessionId.Value}",
        _ => "No analysis generated."
    };

    public string AnalysisAssessment => analysisPackage?.Analysis.Assessment.ToString() ?? "-";

    public string AnalysisCompleteness => analysisPackage?.Analysis.Completeness.ToString() ?? "-";

    public string AnalysisGeneratedAt => analysisPackage is null
        ? "-"
        : analysisPackage.Analysis.GeneratedAt.ToLocalTime().ToString("dd MMM yyyy, HH:mm:ss", CultureInfo.InvariantCulture);

    public string AnalysisActivityRange => analysisPackage is null
        ? "-"
        : $"{analysisPackage.Analysis.AnalyzedFromSequence}..{analysisPackage.Analysis.AnalyzedThroughSequence}";

    public bool IsRunAnalysisDisplayed => analysisPackage?.Analysis.Scope == EvidenceAnalysisScope.Run;

    public bool IsLifecycleAnalysisDisplayed => analysisPackage?.Analysis.Scope == EvidenceAnalysisScope.Lifecycle;

    public bool IsCommunicationSessionAnalysisDisplayed => analysisPackage?.Analysis.Scope == EvidenceAnalysisScope.Session;

    public int SelectedEvidenceTabIndex
    {
        get => selectedEvidenceTabIndex;
        set => SetProperty(ref selectedEvidenceTabIndex, value);
    }

    public bool CanCopyAnalysisReport => CanUseAnalysisReport();

    public string LogFilePath
    {
        get => logFilePath;
        private set => SetProperty(ref logFilePath, value);
    }

    public string TestResultArtifactStatus
    {
        get => testResultArtifactStatus;
        private set => SetProperty(ref testResultArtifactStatus, value);
    }

    public string TestResultArtifactPreview
    {
        get => testResultArtifactPreview;
        private set => SetProperty(ref testResultArtifactPreview, value);
    }

    public string ProjectSource
    {
        get => projectSource;
        set
        {
            if (SetProperty(ref projectSource, value))
            {
                if (importedAccessProfileDraft is not null
                    && !string.Equals(importedAccessProfileDraft.ProjectSource, value, StringComparison.Ordinal))
                {
                    importedAccessProfileDraft = importedAccessProfileDraft with
                    {
                        ProjectSource = value
                    };
                }

                ProjectStatus = CreateProjectDisplayName();
                LoadStep1Command.RaiseCanExecuteChanged();
                ClearProjectSourceCommand.RaiseCanExecuteChanged();
                Step1GateStatus = CreateStep1GateStatus();
                OnPropertyChanged(nameof(ProjectSourceStatus));
                OnPropertyChanged(nameof(ProjectStatusBrush));
            }
        }
    }

    public string ProjectSourceStatus => string.IsNullOrWhiteSpace(projectSource)
        ? BuildAndDownloadProject
            ? "Project source missing"
            : "No project source filter"
        : projectSource;

    public bool BuildAndDownloadProject
    {
        get => buildAndDownloadProject;
        set
        {
            if (!SetProperty(ref buildAndDownloadProject, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedProjectOwnershipMode));
            OnPropertyChanged(nameof(ManagedProjectControlsVisibility));
            OnPropertyChanged(nameof(ProjectSourceStatus));
            PlcStatus = CreatePlcDisplayName();
            if (activeStep == 1 && loadedAccessProfileConfig is null)
            {
                Step1Status = value
                    ? "Select a project source and access profile."
                    : "Select an access profile. Project source is optional.";
            }

            LoadStep1Command.RaiseCanExecuteChanged();
            ConfigureStep2Command.RaiseCanExecuteChanged();
            Step1GateStatus = CreateStep1GateStatus();
            Step2GateStatus = CreateStep2GateStatus();
        }
    }

    public ProjectOwnershipMode SelectedProjectOwnershipMode => BuildAndDownloadProject
        ? ProjectOwnershipMode.ApplicationManaged
        : ProjectOwnershipMode.ExternallyManaged;

    public Visibility ManagedProjectControlsVisibility => BuildAndDownloadProject
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ProjectLockIconVisibility => projectAccessActive
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string Step1Status
    {
        get => step1Status;
        private set => SetProperty(ref step1Status, value);
    }

    public AccessProfileSelectionViewModel? SelectedAccessProfile
    {
        get => selectedAccessProfile;
        set
        {
            if (SetProperty(ref selectedAccessProfile, value))
            {
                LoadStep1Command.RaiseCanExecuteChanged();
                OpenAccessProfileLogCommand.RaiseCanExecuteChanged();
                Step1GateStatus = CreateStep1GateStatus();
                OnPropertyChanged(nameof(ProfileStatusBrush));
            }
        }
    }

    public ObservableCollection<AccessProfileSelectionViewModel> AvailableAccessProfiles { get; } = [];

    public ProjectTargetSelectionViewModel? SelectedProjectTarget
    {
        get => selectedProjectTarget;
        set
        {
            if (SetProperty(ref selectedProjectTarget, value))
            {
                PlcStatus = CreatePlcDisplayName();
                ConfigureStep2Command.RaiseCanExecuteChanged();
                Step2GateStatus = CreateStep2GateStatus();
                OnPropertyChanged(nameof(PlcStatusBrush));
            }
        }
    }

    public RuntimeTargetSelectionViewModel? SelectedRuntimeTarget
    {
        get => selectedRuntimeTarget;
        set
        {
            if (SetProperty(ref selectedRuntimeTarget, value))
            {
                RuntimeStatus = CreateRuntimeDisplayName();
                PlcStatus = CreatePlcDisplayName();
                ConfigureStep2Command.RaiseCanExecuteChanged();
                Step2GateStatus = CreateStep2GateStatus();
                OnPropertyChanged(nameof(RuntimeStatusBrush));
            }
        }
    }

    public string Step2Status
    {
        get => step2Status;
        private set => SetProperty(ref step2Status, value);
    }

    public string CommunicationPrepareTimeoutSeconds
    {
        get => communicationPrepareTimeoutSeconds;
        set
        {
            if (SetProperty(ref communicationPrepareTimeoutSeconds, value))
            {
                ValidateCommunicationTimeoutOverrides(updateStatus: true, out _);
                OnPropertyChanged(nameof(CommunicationPrepareTimeoutValidationMessage));
                OnPropertyChanged(nameof(IsCommunicationPrepareTimeoutValid));
                ConfigureProfileCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(Step3GateStatus));
            }
        }
    }

    public string CommunicationRevalidationTimeoutSeconds
    {
        get => communicationRevalidationTimeoutSeconds;
        set
        {
            if (SetProperty(ref communicationRevalidationTimeoutSeconds, value))
            {
                ValidateCommunicationTimeoutOverrides(updateStatus: true, out _);
                OnPropertyChanged(nameof(CommunicationRevalidationTimeoutValidationMessage));
                OnPropertyChanged(nameof(IsCommunicationRevalidationTimeoutValid));
                ConfigureProfileCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(Step3GateStatus));
            }
        }
    }

    public string? CommunicationPrepareTimeoutValidationMessage =>
        int.TryParse(
            CommunicationPrepareTimeoutSeconds,
            NumberStyles.Integer,
            WpfApplicationDefaults.Culture,
            out var seconds)
        && seconds > 0
            ? null
            : "Communication preparation timeout must be a positive whole number of seconds.";

    public bool IsCommunicationPrepareTimeoutValid =>
        CommunicationPrepareTimeoutValidationMessage is null;

    public string? CommunicationRevalidationTimeoutValidationMessage =>
        int.TryParse(
            CommunicationRevalidationTimeoutSeconds,
            NumberStyles.Integer,
            WpfApplicationDefaults.Culture,
            out var seconds)
        && seconds >= 0
            ? null
            : "Communication revalidation timeout must be a non-negative whole number of seconds.";

    public bool IsCommunicationRevalidationTimeoutValid =>
        CommunicationRevalidationTimeoutValidationMessage is null;

    public string SessionSettingsStatus
    {
        get => sessionSettingsStatus;
        private set => SetProperty(ref sessionSettingsStatus, value);
    }

    public string Step1GateStatus
    {
        get => step1GateStatus;
        private set => SetProperty(ref step1GateStatus, value);
    }

    public string Step2GateStatus
    {
        get => step2GateStatus;
        private set => SetProperty(ref step2GateStatus, value);
    }

    public string Step3GateStatus => CreateStep3GateStatus();

    public string ReturnToStep1GateStatus
    {
        get => returnToStep1GateStatus;
        private set => SetProperty(ref returnToStep1GateStatus, value);
    }

    public string ReturnToStep2GateStatus
    {
        get => returnToStep2GateStatus;
        private set => SetProperty(ref returnToStep2GateStatus, value);
    }

    public string EndSessionGateStatus
    {
        get => endSessionGateStatus;
        private set => SetProperty(ref endSessionGateStatus, value);
    }

    public string ReturnToStep3GateStatus =>
        "Return to Step 3 and release the current operation and runtime context.";

    public string StartPlcGateStatus => CreateGateStatus(WorkflowAction.StartPlc, "Start PLC");

    public string StopPlcGateStatus => CreateGateStatus(WorkflowAction.StopPlc, "Stop PLC");

    public string StartUnitTestGateStatus => CreateGateStatus(WorkflowAction.StartUnitTest, "Start UnitTest");

    public string AbortUnitTestGateStatus => CreateGateStatus(WorkflowAction.AbortUnitTest, "Abort UnitTest");

    public string ResetUnitTestGateStatus => CreateGateStatus(WorkflowAction.ResetUnitTest, "Reset UnitTest");

    public string ResetEventBufferGateStatus => CreateGateStatus(WorkflowAction.ResetEventBuffer, "Reset Event Buffer");

    public string ReconnectRuntimeGateStatus => CreateGateStatus(WorkflowAction.ReconnectRuntime, "Reconnect Runtime");

    public string ProfileRegistrationNotice
    {
        get => profileRegistrationNotice;
        private set => SetProperty(ref profileRegistrationNotice, value);
    }

    public Visibility ProfileRegistrationNoticeVisibility
    {
        get => profileRegistrationNoticeVisibility;
        private set => SetProperty(ref profileRegistrationNoticeVisibility, value);
    }

    public Visibility ProfileRegistrationActionVisibility
    {
        get => profileRegistrationActionVisibility;
        private set => SetProperty(ref profileRegistrationActionVisibility, value);
    }

    public Visibility Step1PanelVisibility
    {
        get => step1PanelVisibility;
        private set => SetProperty(ref step1PanelVisibility, value);
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        return initializationTask ??= InitializeCoreAsync(cancellationToken);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        if (!await TryLoadAvailableAccessProfilesFromRegistryAsync(cancellationToken))
        {
            return;
        }

        await CheckProfileRegistrationsAsync(
            appendOutput: true,
            cancellationToken);
    }

    public bool RequiresApplicationExitConfirmation => activeStep > 1;

    public Visibility Step2PanelVisibility
    {
        get => step2PanelVisibility;
        private set => SetProperty(ref step2PanelVisibility, value);
    }

    public Visibility Step3PanelVisibility
    {
        get => step3PanelVisibility;
        private set => SetProperty(ref step3PanelVisibility, value);
    }

    public Visibility Step4PanelVisibility
    {
        get => step4PanelVisibility;
        private set => SetProperty(ref step4PanelVisibility, value);
    }

    public bool Step2InputsEnabled
    {
        get => step2InputsEnabled;
        private set => SetProperty(ref step2InputsEnabled, value);
    }

    public string Step1HeaderBackground => activeStep == 1 ? "White" : "#F5F7FA";

    public string Step2HeaderBackground => activeStep == 2 ? "White" : "#F5F7FA";

    public string Step3HeaderBackground => activeStep == 3 ? "White" : "#F5F7FA";

    public string Step4HeaderBackground => activeStep == 4 ? "White" : "#F5F7FA";

    public string Step1MarkerFill => CreateStepMarkerFill(1);

    public string Step2MarkerFill => CreateStepMarkerFill(2);

    public string Step3MarkerFill => CreateStepMarkerFill(3);

    public string Step4MarkerFill => CreateStepMarkerFill(4);

    public string Step1HeaderWeight => activeStep == 1 ? "SemiBold" : "Normal";

    public string Step2HeaderWeight => activeStep == 2 ? "SemiBold" : "Normal";

    public string Step3HeaderWeight => activeStep == 3 ? "SemiBold" : "Normal";

    public string Step4HeaderWeight => activeStep == 4 ? "SemiBold" : "Normal";

    public ObservableCollection<ProjectTargetSelectionViewModel> ProjectTargets { get; } = [];

    public ObservableCollection<RuntimeTargetSelectionViewModel> RuntimeTargets { get; } = [];

    public ProfileConfigurationEditorViewModel ProfileConfiguration { get; }

    public ObservableCollection<ActionGateViewModel> ActionGates { get; } = [];

    public ObservableCollection<DiagnosticViewModel> Diagnostics { get; } = [];

    public ObservableCollection<RunResultNodeViewModel> RunResultTree { get; } = [];

    public ObservableCollection<RunResultNodeViewModel> RunResultRows { get; } = [];

    public string RunResultStatus
    {
        get => runResultStatus;
        private set => SetProperty(ref runResultStatus, value);
    }

    public Visibility RunResultLostEventsVisibility =>
        runResultLostEventCount > 0 && RunResultTree.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string RunResultLostEventsText => $"Lost events: {runResultLostEventCount}";

    public string RunResultLostEventsToolTip =>
        $"{runResultLostEventCount} event{(runResultLostEventCount == 1 ? " was" : "s were")} lost during this run. The result evidence is incomplete.";

    public RunResultNodeViewModel? SelectedRunResultNode
    {
        get => selectedRunResultNode;
        set
        {
            if (SetProperty(ref selectedRunResultNode, value))
            {
                OnPropertyChanged(nameof(SelectedRunResultName));
                OnPropertyChanged(nameof(SelectedRunResultResult));
                OnPropertyChanged(nameof(SelectedRunResultTiming));
                OnPropertyChanged(nameof(RunResultSummary));
                OnPropertyChanged(nameof(SelectedRunResultFindings));
                OnPropertyChanged(nameof(SelectedRunResultFindingStatus));
            }
        }
    }

    public string SelectedRunResultName => selectedRunResultNode?.DisplayName ?? "No run node selected";

    public string SelectedRunResultResult => selectedRunResultNode is null
        ? "Not evaluated"
        : $"{selectedRunResultNode.PresentationResult} (reported {selectedRunResultNode.Result}) / {selectedRunResultNode.LifecycleState} / {selectedRunResultNode.EvidenceState}";

    public string SelectedRunResultTiming => selectedRunResultNode is null
        ? "-"
        : $"Created {selectedRunResultNode.CreatedAt}; updated {selectedRunResultNode.LastUpdatedAt}; runtime {selectedRunResultNode.Runtime}";

    public string RunResultSummary => selectedRunResultNode is null
        ? "Select a run node to inspect its snapshot data."
        : selectedRunResultNode.Summary;

    public IEnumerable<RunResultFindingViewModel> SelectedRunResultFindings => selectedRunResultNode?.Findings
        ?? Enumerable.Empty<RunResultFindingViewModel>();

    public string SelectedRunResultFindingStatus => selectedRunResultNode?.FindingCountText
        ?? "No findings";

    public DiagnosticViewModel? SelectedDiagnostic
    {
        get => selectedDiagnostic;
        set
        {
            if (SetProperty(ref selectedDiagnostic, value))
            {
                OnPropertyChanged(nameof(CanCopySelectedDiagnostic));
                AnalyzeSelectedDiagnosticCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanCopySelectedDiagnostic => SelectedDiagnostic is not null;

    public bool CanCopyAllDiagnostics => Diagnostics.Count > 0;

    public AsyncCommand LoadStep1Command { get; }

    public AsyncCommand BrowseProjectSourceCommand { get; }

    public AsyncCommand ClearProjectSourceCommand { get; }

    public AsyncCommand ConfigureStep2Command { get; }

    public AsyncCommand ConfigureProfileCommand { get; }

    public AsyncCommand StartPlcCommand { get; }

    public AsyncCommand StopPlcCommand { get; }

    public AsyncCommand StartUnitTestCommand { get; }

    public AsyncCommand AbortUnitTestCommand { get; }

    public AsyncCommand ResetUnitTestCommand { get; }

    public AsyncCommand ResetEventBufferCommand { get; }

    public AsyncCommand ReconnectRuntimeCommand { get; }

    public AsyncCommand ReturnToStep1Command { get; }

    public AsyncCommand ReturnToStep2Command { get; }

    public AsyncCommand ReturnToStep3Command { get; }

    public AsyncCommand EndSessionCommand { get; }

    public AsyncCommand ImportConfigurationFileCommand { get; }

    public AsyncCommand LoadUnitTestFileCommand { get; }

    public AsyncCommand RefreshProfileRegistrationsCommand { get; }

    public AsyncCommand CancelCurrentOperationCommand { get; }

    public AsyncCommand ClearOutputCommand { get; }

    public AsyncCommand ClearDiagnosticsCommand { get; }

    public AsyncCommand ClearRunResultCommand { get; }

    public AsyncCommand AnalyzeCurrentRunCommand { get; }

    public AsyncCommand AnalyzeLifecycleCommand { get; }

    public AsyncCommand AnalyzeCommunicationSessionCommand { get; }

    public AsyncCommand AnalyzeSelectedDiagnosticCommand { get; }

    public event EventHandler? AnalysisPresented;

    public AsyncCommand ExportAnalysisCommand { get; }

    public AsyncCommand ClearAnalysisCommand { get; }

    public AsyncCommand OpenApplicationLogCommand { get; }

    public AsyncCommand OpenAccessProfileLogCommand { get; }

    public AsyncCommand OpenLogFolderCommand { get; }

    public AsyncCommand OpenSettingsCommand { get; }

    public AsyncCommand OpenTestResultArtifactCommand { get; }

    public AsyncCommand OpenOutputFolderCommand { get; }

    public bool ConfirmApplicationExit()
    {
        return !RequiresApplicationExitConfirmation
            || confirmations.Confirm(
                "End Session and Exit?",
                "The current session, prepared runtime, UnitTest communication, project context, access profile, and configuration draft will be released before the application exits.");
    }

    public async Task CleanupForApplicationExitAsync(CancellationToken cancellationToken)
    {
        if (!await BeginContextReleaseAsync())
        {
            return;
        }

        try
        {
            AppendTerminalLine("Ending session before application exit. Releasing workflow resources...");
            ReleaseStep2UiSelection();

            try
            {
                await ReleaseSessionResourcesAsync();
                logger.LogInformation("Application exit session cleanup completed.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Application exit session cleanup failed. Shutdown will continue.");
            }
        }
        finally
        {
            EndContextRelease();
        }
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        workflow.PublicationEventHandler -= OnWorkflowPublication;
        artifactExports.OutputStateChangedEventHandler -= OnArtifactOutputStateChanged;
        diagnostics.Reader.SnapshotChangedEventHandler -= OnDiagnosticSnapshotChanged;
        applicationSettings.SettingsChanged -= OnApplicationSettingsChanged;
        cancellableOperation.StateChanged -= OnCancellableOperationStateChanged;
        cancellableOperation.Dispose();
        isDisposed = true;
    }

    private bool SetProcessStatusProperty(
        ref ProcessStatusViewState field,
        ProcessStatusViewState value,
        string propertyName,
        params string[] dependentPropertyNames)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        foreach (var dependentPropertyName in dependentPropertyNames)
        {
            OnPropertyChanged(dependentPropertyName);
        }

        return true;
    }

    private bool CanLoadSelectedAccessProfile()
    {
        return !IsInteractionBlocked()
            && selectedAccessProfile is not null
            && selectedAccessProfile.IsRegistered
            && (!BuildAndDownloadProject || !string.IsNullOrWhiteSpace(projectSource))
            && IsGateAvailable(WorkflowAction.LoadAccessProfile);
    }

    private bool CanRefreshProfileRegistrations()
    {
        return !IsInteractionBlocked()
            && activeStep == 1
            && loadedAccessProfileConfig is null;
    }

    private async Task RefreshProfileRegistrationsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Step1Status = "Refreshing profile registrations...";
        AppendTerminalLine("Refreshing application profile registrations...");
        var result = await accessProfileRegistrations.RefreshAllProfileRegistrationsAsync(
            applicationSettings.Current.OperationTimeouts.RegistrationRefresh,
            cancellationToken);
        UpdateProjectSourceDialogFilter(result.Registry.Profiles);
        await LoadAvailableAccessProfilesForProjectSourceAsync(cancellationToken);

        var registeredProfileCount = result.Registry.Profiles.Count(profile =>
            profile.Registration.State == AccessProfileRegistrationState.Registered);
        var visibleProfileCount = AvailableAccessProfiles.Count(profile => profile.IsRegistered);
        var message = result.Status == ApplicationProfileRegistrationRefreshStatus.Completed
            ? $"Access profile registration refresh completed: {registeredProfileCount} registered profile(s), {visibleProfileCount} matching profile(s)."
            : $"Access profile registration refresh completed with status {result.Status}: {registeredProfileCount} registered profile(s), {visibleProfileCount} matching profile(s).";
        if (string.IsNullOrWhiteSpace(projectSource))
        {
            Step1Status = registeredProfileCount == 0
                ? "No registered access profiles available."
                : "Select a project/profile source.";
        }

        AppendTerminalLine(message);
        logger.LogInformation(
            "WPF application profile registration refresh completed. Status={Status}, ResultCount={ResultCount}, RegisteredProfileCount={RegisteredProfileCount}",
            result.Status,
            result.Profiles.Count,
            registeredProfileCount);

        if (result.Status == ApplicationProfileRegistrationRefreshStatus.Completed
            && result.Registry.Profiles.Count > 0)
        {
            await CheckProfileRegistrationsAsync(appendOutput: false, cancellationToken);
        }
        else if (result.Status == ApplicationProfileRegistrationRefreshStatus.Completed)
        {
            ApplyProfileRegistrationCheckResult(new ApplicationProfileRegistrationCheckResult(
                ApplicationProfileRegistrationCheckStatus.NoProfilesDiscovered,
                "No access profile packages were discovered.",
                []));
        }
        else
        {
            ApplyProfileRegistrationCheckResult(new ApplicationProfileRegistrationCheckResult(
                ApplicationProfileRegistrationCheckStatus.Failed,
                result.Message ?? "Access profile registration refresh did not complete successfully.",
                []));
        }
    }

    private async Task CheckProfileRegistrationsAsync(
        bool appendOutput,
        CancellationToken cancellationToken)
    {
        var result = await accessProfileRegistrations.CheckProfileRegistrationsAsync(cancellationToken);
        if (isDisposed)
        {
            return;
        }

        ApplyProfileRegistrationCheckResult(result);

        if (appendOutput && !result.IsCurrent)
        {
            AppendTerminalLine($"Access profile registration check: {result.Message}");
        }

        logger.LogInformation(
            "WPF application profile registration check completed. Status={Status}",
            result.Status);
    }

    private void ApplyProfileRegistrationCheckResult(ApplicationProfileRegistrationCheckResult result)
    {
        switch (result.Status)
        {
            case ApplicationProfileRegistrationCheckStatus.Current:
                ProfileRegistrationNotice = string.Empty;
                ProfileRegistrationNoticeVisibility = Visibility.Collapsed;
                ProfileRegistrationActionVisibility = Visibility.Collapsed;
                break;

            case ApplicationProfileRegistrationCheckStatus.NoProfilesDiscovered:
                ProfileRegistrationNotice =
                    "No access profile packages are installed in the configured profile directory.";
                ProfileRegistrationNoticeVisibility = Visibility.Visible;
                ProfileRegistrationActionVisibility = Visibility.Collapsed;
                break;

            case ApplicationProfileRegistrationCheckStatus.RefreshRequired:
                ProfileRegistrationNotice =
                    "Installed access profiles are not registered or their registration is no longer current.";
                ProfileRegistrationNoticeVisibility = Visibility.Visible;
                ProfileRegistrationActionVisibility = Visibility.Visible;
                break;

            default:
                ProfileRegistrationNotice =
                    "Access profile registration could not be checked. Register the profiles again and review Diagnostics if the problem remains.";
                ProfileRegistrationNoticeVisibility = Visibility.Visible;
                ProfileRegistrationActionVisibility = Visibility.Visible;
                break;
        }
    }

    private async Task<bool> TryLoadAvailableAccessProfilesFromRegistryAsync(
        CancellationToken cancellationToken,
        bool appendOutput = true)
    {
        try
        {
            await LoadAvailableAccessProfilesFromRegistryAsync(cancellationToken, appendOutput);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HandleRegistryUnavailable(ex);
            return false;
        }
    }

    private async Task LoadAvailableAccessProfilesFromRegistryAsync(
        CancellationToken cancellationToken,
        bool appendOutput)
    {
        var previouslySelectedProfileId = selectedAccessProfile?.ProfileId;
        var previouslySelectedProfileVersion = selectedAccessProfile?.ProfileVersion;
        var previouslySelectedSourceKind = selectedAccessProfile?.SourceKind;
        var previouslySelectedSourcePath = selectedAccessProfile?.SourcePath;
        var registry = await accessProfileRegistry
            .EnsureLoadedAsync(cancellationToken)
            .ConfigureAwait(true);
        UpdateProjectSourceDialogFilter(registry.Profiles);

        var registeredProfiles = registry.Profiles
            .Select(AccessProfileSelectionViewModel.FromRegistryEntry)
            .ToArray();

        Replace(AvailableAccessProfiles, registeredProfiles);

        SelectedAccessProfile = AvailableAccessProfiles.FirstOrDefault(profile =>
            string.Equals(profile.ProfileId, previouslySelectedProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.ProfileVersion, previouslySelectedProfileVersion, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.SourceKind, previouslySelectedSourceKind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.SourcePath, previouslySelectedSourcePath, StringComparison.OrdinalIgnoreCase))
            ?? AvailableAccessProfiles.FirstOrDefault(profile => profile.IsRegistered)
            ?? AvailableAccessProfiles.FirstOrDefault();

        var registeredProfileCount = AvailableAccessProfiles.Count(profile => profile.IsRegistered);
        Step1Status = registeredProfileCount == 0
            ? "No registered access profiles available."
            : "Select a project/profile source.";
        if (appendOutput)
        {
            AppendTerminalLine(registeredProfileCount == 0
                ? "Access profile registry loaded: no registered profiles available."
                : $"Access profile registry loaded: {registeredProfileCount} registered profile(s), {AvailableAccessProfiles.Count} registry entry/entries.");
        }

        logger.LogInformation(
            "WPF access profile registry loaded. ProfileCount={ProfileCount}, RegisteredProfileCount={RegisteredProfileCount}",
            AvailableAccessProfiles.Count,
            registeredProfileCount);
        LoadStep1Command.RaiseCanExecuteChanged();
        RefreshProfileRegistrationsCommand.RaiseCanExecuteChanged();
    }

    private void HandleRegistryUnavailable(Exception exception)
    {
        Replace(AvailableAccessProfiles, []);
        SelectedAccessProfile = null;
        UpdateProjectSourceDialogFilter([]);
        Step1Status = "Access profile registry is unavailable.";
        ApplyProfileRegistrationCheckResult(new ApplicationProfileRegistrationCheckResult(
            ApplicationProfileRegistrationCheckStatus.Failed,
            exception.Message,
            []));

        var message = "Access profile registry could not be loaded or created.";
        diagnosticReporter.ReportProfileRegistryUnavailable(message, exception.Message);
        AppendTerminalLine($"Access profile registry unavailable: {exception.Message}");
        logger.LogError(exception, "WPF access profile registry could not be loaded or created.");
        LoadStep1Command.RaiseCanExecuteChanged();
        RefreshProfileRegistrationsCommand.RaiseCanExecuteChanged();
    }

    private void UpdateProjectSourceDialogFilter(IEnumerable<AccessProfileRegistryEntry> profiles)
    {
        projectSourceDialogFilter = ProjectSourceFileDialogFilterBuilder.Build(profiles);
        canBrowseProjectSource = !string.Equals(
            projectSourceDialogFilter,
            ProjectSourceFileDialogFilterBuilder.DefaultFilter,
            StringComparison.Ordinal);
        BrowseProjectSourceCommand.RaiseCanExecuteChanged();
    }

    private async Task LoadAvailableAccessProfilesForProjectSourceAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectSource))
        {
            await TryLoadAvailableAccessProfilesFromRegistryAsync(cancellationToken);
            return;
        }

        var previouslySelectedProfileId = selectedAccessProfile?.ProfileId;
        var previouslySelectedProfileVersion = selectedAccessProfile?.ProfileVersion;
        var previouslySelectedSourceKind = selectedAccessProfile?.SourceKind;
        var previouslySelectedSourcePath = selectedAccessProfile?.SourcePath;
        var result = await accessProfileSelections
            .EvaluateProjectSourceAsync(projectSource, cancellationToken)
            .ConfigureAwait(true);
        var supportedProfiles = result.Profiles
            .Where(profile => profile.Evaluation.State == AccessProfileProjectSourceEvaluationState.Supported)
            .Select(profile => AccessProfileSelectionViewModel.FromRegistryEntry(profile.Profile))
            .ToArray();

        Replace(AvailableAccessProfiles, supportedProfiles);

        SelectedAccessProfile = AvailableAccessProfiles.FirstOrDefault(profile =>
            string.Equals(profile.ProfileId, previouslySelectedProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.ProfileVersion, previouslySelectedProfileVersion, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.SourceKind, previouslySelectedSourceKind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.SourcePath, previouslySelectedSourcePath, StringComparison.OrdinalIgnoreCase))
            ?? AvailableAccessProfiles.FirstOrDefault(profile => profile.IsRegistered)
            ?? AvailableAccessProfiles.FirstOrDefault();

        if (AvailableAccessProfiles.Count == 0)
        {
            Step1Status = "No registered access profile supports the selected project source.";
            AppendTerminalLine("Project source evaluation found no matching registered access profile.");
        }
        else
        {
            Step1Status = "Select a project/profile source.";
            AppendTerminalLine($"Project source evaluation matched {AvailableAccessProfiles.Count} registered access profile(s).");
        }

        logger.LogInformation(
            "WPF project-source profile evaluation completed. ProjectSource={ProjectSource}, MatchingProfileCount={MatchingProfileCount}, EvaluationCount={EvaluationCount}",
            projectSource,
            AvailableAccessProfiles.Count,
            result.Profiles.Count);
        LoadStep1Command.RaiseCanExecuteChanged();
        RefreshProfileRegistrationsCommand.RaiseCanExecuteChanged();
    }

    private bool CanOpenProfileConfiguration()
    {
        return !contextReleaseInProgress
            && loadedAccessProfileConfig is not null
            && (!BuildAndDownloadProject || selectedProjectTarget is not null)
            && selectedRuntimeTarget is not null
            && IsGateAvailable(WorkflowAction.DiscoverRuntimeTargets);
    }

    private bool CanConfigureSelectedTargets()
    {
        return !IsInteractionBlocked()
            && activeStep == 3
            && loadedAccessProfileConfig is not null
            && ProfileConfiguration.Validate(updateStatus: false)
            && ValidateCommunicationTimeoutOverrides(updateStatus: false, out _);
    }

    private bool CanStartPlc()
    {
        return !contextReleaseInProgress
            && Step4PanelVisibility == Visibility.Visible
            && IsGateAvailable(WorkflowAction.StartPlc);
    }

    private bool CanStopPlc()
    {
        return !contextReleaseInProgress
            && Step4PanelVisibility == Visibility.Visible
            && IsGateAvailable(WorkflowAction.StopPlc);
    }

    private bool CanStartUnitTest()
    {
        return !contextReleaseInProgress
            && Step4PanelVisibility == Visibility.Visible
            && IsGateAvailable(WorkflowAction.StartUnitTest);
    }

    private bool CanAbortUnitTest()
    {
        return !contextReleaseInProgress
            && Step4PanelVisibility == Visibility.Visible
            && IsGateAvailable(WorkflowAction.AbortUnitTest);
    }

    private bool CanResetUnitTest()
    {
        return !contextReleaseInProgress
            && Step4PanelVisibility == Visibility.Visible
            && IsGateAvailable(WorkflowAction.ResetUnitTest);
    }

    private bool CanResetEventBuffer()
    {
        return !contextReleaseInProgress
            && Step4PanelVisibility == Visibility.Visible
            && IsGateAvailable(WorkflowAction.ResetEventBuffer);
    }

    private bool CanReconnectRuntime()
    {
        return !contextReleaseInProgress
            && Step4PanelVisibility == Visibility.Visible
            && IsGateAvailable(WorkflowAction.ReconnectRuntime);
    }

    private bool HasSessionContext()
    {
        return workflow.Current.State != RunWorkflowState.NotConfigured
            || loadedAccessProfileConfig is not null
            || selectedProjectTarget is not null
            || selectedRuntimeTarget is not null;
    }

    private bool CanSelectRunConfigurationFile()
    {
        return !configurationFileOperationPending
            && !IsInteractionBlocked()
            && workflow.Current.ActiveOperation is null;
    }

    private bool CanCancelCurrentOperation()
    {
        return !contextReleaseInProgress
            && cancellableOperation.IsActive
            && !cancellableOperation.IsCancellationRequested;
    }

    private bool CanOpenApplicationLog()
    {
        return !string.IsNullOrWhiteSpace(LogFilePath);
    }

    private bool CanOpenAccessProfileLog()
    {
        return GetAccessProfileLogPath() is { } configuredPath
            && logFileOpenService.ResolveExistingLogFilePath(configuredPath) is not null;
    }

    private bool CanOpenLogFolder()
    {
        return Directory.Exists(applicationSettings.Current.ResolvedLogDirectory);
    }

    private bool CanOpenTestResultArtifact()
    {
        return testResultArtifact?.State == ArtifactState.Written
            && !string.IsNullOrWhiteSpace(testResultArtifact.OutputPath)
            && File.Exists(testResultArtifact.OutputPath);
    }

    private bool CanOpenOutputFolder()
    {
        var directory = GetTestResultArtifactDirectory();
        return !string.IsNullOrWhiteSpace(directory);
    }

    private bool CanClearOutput()
    {
        return terminalOutput.HasText;
    }

    private bool CanClearDiagnostics()
    {
        return Diagnostics.Count > 0;
    }

    private bool CanClearRunResult()
    {
        return RunResultTree.Count > 0
            && runResultTree.DisplayedState is UnitTestRunState.Completed
                or UnitTestRunState.Aborted
                or UnitTestRunState.TimedOut
                or UnitTestRunState.Invalidated;
    }

    private bool CanAnalyzeCurrentRun()
    {
        return GetLatestRunEpochId() is not null;
    }

    private bool CanAnalyzeLifecycle()
    {
        return GetLatestLifecycleObservationId() is not null;
    }

    private bool CanAnalyzeCommunicationSession()
    {
        return workflow.EvidenceAnalysis.CurrentTrace is { Entries.Count: > 0 };
    }

    private bool CanAnalyzeSelectedDiagnostic()
    {
        return SelectedDiagnostic is not null && CanAnalyzeCommunicationSession();
    }

    private bool CanUseAnalysisReport()
    {
        return analysisPackage is not null;
    }

    private bool CanClearAnalysis()
    {
        return CanUseAnalysisReport()
            || workflow.EvidenceAnalysis.CurrentTrace is { IsCaptureActive: false };
    }

    private bool CanBrowseProjectSource()
    {
        return !IsInteractionBlocked()
            && activeStep == 1
            && canBrowseProjectSource;
    }

    private bool CanClearProjectSource()
    {
        return !IsInteractionBlocked()
            && activeStep == 1
            && !string.IsNullOrWhiteSpace(projectSource);
    }

    private bool CanReturnToStep1()
    {
        return !contextReleaseInProgress
            && activeStep >= 2;
    }

    private bool CanReturnToStep2()
    {
        return !contextReleaseInProgress
            && activeStep >= 3
            && loadedAccessProfileConfig is not null
            && workflow.Current.State >= RunWorkflowState.DiscoveryReady;
    }

    private bool CanReturnToStep3()
    {
        return !contextReleaseInProgress
            && activeStep == 4
            && loadedAccessProfileConfig is not null
            && workflow.Current.State >= RunWorkflowState.Configured;
    }

    private bool CanEndSession()
    {
        return !contextReleaseInProgress;
    }

    private async Task BrowseProjectSourceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = projectSourceFileDialogService.SelectProjectSourceFile(projectSourceDialogFilter);
        if (!string.IsNullOrWhiteSpace(path))
        {
            ProjectSource = path;
            await LoadAvailableAccessProfilesForProjectSourceAsync(cancellationToken);
        }
    }

    private async Task ClearProjectSourceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProjectSource = string.Empty;
        if (!await TryLoadAvailableAccessProfilesFromRegistryAsync(cancellationToken, appendOutput: false))
        {
            return;
        }

        Step1Status = BuildAndDownloadProject
            ? "Select a project source and access profile."
            : "Select an access profile. Project source is optional.";
    }

    private Task ImportConfigurationFileAsync(CancellationToken cancellationToken)
    {
        return SelectAndLoadConfigurationFileAsync(
            RunConfigurationFileDialogPurpose.ImportConfiguration,
            "Import Configuration",
            "Configuration file loaded and validated for import.",
            cancellationToken);
    }

    private Task LoadUnitTestFileAsync(CancellationToken cancellationToken)
    {
        return SelectAndLoadConfigurationFileAsync(
            RunConfigurationFileDialogPurpose.LoadUnitTest,
            "Load UnitTest",
            "UnitTest configuration file loaded and validated.",
            cancellationToken);
    }

    private async Task SelectAndLoadConfigurationFileAsync(
        RunConfigurationFileDialogPurpose purpose,
        string operationName,
        string validMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        configurationFileOperationPending = true;
        RaiseConfigurationFileCommandCanExecuteChanged();
        try
        {
            var request = new RunConfigurationLoadRequest(purpose, operationName, validMessage);
            var result = await configurationLoadCoordinator.LoadAsync(
                request,
                cancellationToken,
                path => AppendTerminalLine($"{operationName}: loading configuration file {path}..."));
            if (result.WasCanceled)
            {
                AppendTerminalLine($"{operationName} canceled.");
                return;
            }

            if (!result.IsValid)
            {
                AppendTerminalLine($"{operationName} failed: {result.Diagnostics.Count} diagnostic(s).");
                return;
            }

            var accessProfile = result.AccessProfile!;
            if (!await ConfirmAndCleanupConfigurationReplacementAsync(operationName, cancellationToken))
            {
                AppendTerminalLine($"{operationName} canceled. Existing session or configuration draft kept.");
                return;
            }

            if (purpose == RunConfigurationFileDialogPurpose.ImportConfiguration)
            {
                BuildAndDownloadProject = applicationSettings.Current.BuildAndDownloadProjectByDefault;
                await ApplyConfigurationDraftAsync(
                    accessProfile,
                    result.TimeoutOverrides,
                    result.ProvisioningMode,
                    operationName,
                    "Configuration imported. Review the draft and load the selected access profile.",
                    cancellationToken);
            }
            else
            {
                await ApplyLoadedUnitTestSetupAsync(
                    accessProfile,
                    result.TimeoutOverrides,
                    result.ProvisioningMode,
                    cancellationToken);
            }

            AppendTerminalLine($"{operationName}: {validMessage}");
        }
        finally
        {
            configurationFileOperationPending = false;
            RaiseConfigurationFileCommandCanExecuteChanged();
        }
    }

    private async Task<bool> ConfirmAndCleanupConfigurationReplacementAsync(
        string operationName,
        CancellationToken cancellationToken)
    {
        if (!HasConfigurationReplacementContext())
        {
            return true;
        }

        if (!confirmations.Confirm(
            $"{operationName}?",
            "The current session or configuration draft will be replaced. Prepared runtime, UnitTest communication, selected targets, run result, and test result preview may be cleared."))
        {
            return false;
        }

        ClearRunResultDisplay(suppressCurrentEpoch: true);
        ReleaseStep2UiSelection();
        if (HasSessionContext())
        {
            AppendTerminalLine($"{operationName}: releasing current workflow context before loading configuration...");
            await YieldUiAsync();
            await ReleaseSessionResourcesAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }

        ResetTargetSelection(clearProfileDraft: true);
        RefreshLocalConfigurationStatus();
        Step1Status = "Select a project/profile source.";
        Step2Status = "Load an access profile before selecting PLC/runtime targets.";
        SetActiveStep(1);
        return true;
    }

    private bool HasConfigurationReplacementContext()
    {
        return HasSessionContext()
            || importedAccessProfileDraft is not null;
    }

    private async Task ApplyLoadedUnitTestSetupAsync(
        AccessProfileConfig config,
        TimeoutConfiguration? timeoutOverrides,
        RuntimeProvisioningMode provisioningMode,
        CancellationToken cancellationToken)
    {
        BuildAndDownloadProject = true;
        await ApplyConfigurationDraftAsync(
            config,
            timeoutOverrides,
            provisioningMode,
            "Load UnitTest",
            "UnitTest configuration loaded. Applying setup...",
            cancellationToken);
        if (selectedAccessProfile is null)
        {
            return;
        }

        await LoadSelectedAccessProfileAsync(cancellationToken);
        if (selectedProjectTarget is null || selectedRuntimeTarget is null)
        {
            var diagnostic = diagnosticReporter.CreateConfiguration(
                "Wpf.Configuration.Targets.NotResolved",
                "Loaded UnitTest configuration did not resolve the PLC target or requested runtime provisioning. Review the target selection.",
                LogLevel.Error,
                config.ProfileId);
            diagnosticReporter.Add(diagnostic);
            Step2Status = "Loaded UnitTest configuration did not resolve all required setup values.";
            return;
        }

        if (!ApplySelectedTargetsToProfileConfiguration())
        {
            return;
        }

        ProfileConfiguration.Load(
            accessProfileSessions.CurrentSession?.Configuration
                ?? throw new InvalidOperationException("The loaded access profile has no configuration service."),
            loadedAccessProfileConfig ?? throw new InvalidOperationException("No access profile configuration is loaded."),
            selectedRuntimeTarget.ProvisioningMode);
        SetActiveStep(3);
        if (ProfileConfiguration.Validate(updateStatus: true))
        {
            await ConfigureSelectedTargetsAsync(cancellationToken);
        }

        if (Step4PanelVisibility == Visibility.Visible)
        {
            AppendTerminalLine("Load UnitTest: setup is ready. Start the PLC when ready.");
        }
    }

    private async Task ApplyConfigurationDraftAsync(
        AccessProfileConfig config,
        TimeoutConfiguration? timeoutOverrides,
        RuntimeProvisioningMode provisioningMode,
        string operationName,
        string selectedProfileStatus,
        CancellationToken cancellationToken)
    {
        if (!await TryLoadAvailableAccessProfilesFromRegistryAsync(cancellationToken, appendOutput: false))
        {
            return;
        }
        var presentation = RunConfigurationDraftPresentationMapper.Map(
            config,
            operationName,
            selectedProfileStatus,
            AvailableAccessProfiles,
            diagnosticReporter.CreateConfiguration);

        importedAccessProfileDraft = presentation.Draft;
        importedTimeoutOverrides = timeoutOverrides;
        importedProvisioningMode = provisioningMode;
        ResetCommunicationTimeoutOverrides();
        importedConfigurationOperationName = presentation.OperationName;
        importedProjectTargetId = null;
        importedRuntimeTargetId = null;
        ProjectSource = presentation.ProjectSource;
        SelectedAccessProfile = presentation.SelectedAccessProfile;
        Step1Status = presentation.Step1Status;

        if (presentation.Diagnostic is not null)
        {
            diagnosticReporter.Add(presentation.Diagnostic);
        }

        if (!string.IsNullOrWhiteSpace(presentation.OutputLine))
        {
            AppendTerminalLine(presentation.OutputLine);
        }

        SetActiveStep(1);
        LoadStep1Command.RaiseCanExecuteChanged();
    }

    private async Task LoadSelectedAccessProfileAsync(CancellationToken cancellationToken)
    {
        if (selectedAccessProfile is null)
        {
            Step1Status = "Select an access profile.";
            return;
        }

        if (!selectedAccessProfile.IsRegistered)
        {
            Step1Status = "Refresh profile registrations before loading this access profile.";
            AppendTerminalLine($"Access profile '{selectedAccessProfile.DisplayName}' is not registered for use.");
            return;
        }

        var selectedProfileText = RunConfigurationDraftPresentationMapper.FormatAccessProfileSelection(selectedAccessProfile);
        Step1Status = $"Loading {selectedAccessProfile.DisplayName}...";
        Step2Status = "Loading access profile and discovering targets...";
        AppendTerminalLine($"Loading access profile '{selectedProfileText}'...");
        logger.LogInformation(
            "WPF run selection: access profile selected. AccessProfileId={AccessProfileId}, AccessProfileVersion={AccessProfileVersion}, AccessProfileDisplayName={AccessProfileDisplayName}, AccessProfileSourceKind={AccessProfileSourceKind}, AccessProfileSourcePath={AccessProfileSourcePath}, ProjectSource={ProjectSource}, ProjectOwnershipMode={ProjectOwnershipMode}",
            selectedAccessProfile.ProfileId,
            selectedAccessProfile.ProfileVersion,
            selectedAccessProfile.DisplayName,
            selectedAccessProfile.SourceKind,
            selectedAccessProfile.SourcePath,
            projectSource,
            SelectedProjectOwnershipMode);
        Step2InputsEnabled = false;
        SetActiveStep(2);

        var config = RunConfigurationDraftPresentationMapper.CreateSelectedAccessProfileConfig(
            selectedAccessProfile,
            importedAccessProfileDraft,
            projectSource);
        var ownershipMode = SelectedProjectOwnershipMode;
        var load = await accessProfileSessions.LoadAsync(
            new AccessProfileSessionRequest(
                config.ProfileId,
                config.ProfileVersion,
                config.ProjectSource,
                ownershipMode),
            applicationSettings.Current.OperationTimeouts.ProfileLoad,
            cancellationToken);
        if (load.Session is null || load.State != AccessProfileLoadState.Loaded)
        {
            Step1Status = "Access profile load failed.";
            Step2Status = "Access profile load failed.";
            AppendTerminalLine($"Access profile load failed: {Step1Status}");
            return;
        }

        var bind = await workflow.BindAccessProfileSessionAsync(
            load.Session,
            CreatePolicy(WorkflowAction.LoadAccessProfile),
            cancellationToken);
        if (!bind.Result.IsSuccessful)
        {
            Step1Status = bind.Result.Message ?? "Access profile bind failed.";
            Step2Status = "Access profile bind failed.";
            AppendTerminalLine($"Access profile bind failed: {Step1Status}");
            await UnloadAccessProfileSessionAsync();
            return;
        }

        loadedAccessProfileConfig = config;
        if (load.Session.Configuration is { } configurationService)
        {
            var configurationLoad = configurationService.LoadConfig(config.Values.Json);
            if (!configurationLoad.IsSuccessful || configurationLoad.Value is null)
            {
                Step1Status = configurationLoad.Diagnostics.FirstOrDefault()?.Message
                    ?? "Access profile configuration could not be loaded.";
                Step2Status = "Access profile configuration could not be loaded.";
                AppendTerminalLine($"Access profile configuration load failed: {Step1Status}");
                return;
            }

            loadedAccessProfileConfig = configurationLoad.Value;
            importedProjectTargetId = configurationService.ProjectTargetId?.Value;
            importedRuntimeTargetId = configurationService.RuntimeTargetId?.Value;
        }
        if (ownershipMode == ProjectOwnershipMode.ApplicationManaged)
        {
            Step2Status = "Discovering project and PLC targets...";
            AppendTerminalLine("Discovering project and PLC targets...");
            var discovery = await workflow.DiscoverProjectTargetsAsync(
                config,
                CreatePolicy(WorkflowAction.DiscoverProjectTargets),
                cancellationToken);
            if (!discovery.Result.IsSuccessful)
            {
                Step1Status = discovery.Result.Message ?? "Project target discovery failed.";
                Step2Status = "Project target discovery failed.";
                AppendTerminalLine($"Project target discovery failed: {Step1Status}");
                return;
            }
        }

        Step2Status = "Discovering runtime targets...";
        AppendTerminalLine("Discovering runtime targets...");
        var runtimeDiscovery = await workflow.DiscoverRuntimeTargetsAsync(
            config,
            CreatePolicy(WorkflowAction.DiscoverRuntimeTargets),
            cancellationToken);
        if (!runtimeDiscovery.Result.IsSuccessful)
        {
            Step1Status = ownershipMode == ProjectOwnershipMode.ApplicationManaged
                ? "Access profile loaded. Project target discovery is ready."
                : "Access profile loaded for external project handling.";
            Step2Status = runtimeDiscovery.Result.Message ?? "Runtime target discovery failed.";
            AppendTerminalLine($"Runtime target discovery failed: {Step2Status}");
            return;
        }

        Step1Status = ownershipMode == ProjectOwnershipMode.ApplicationManaged
            ? "Access profile loaded. Target discovery is ready."
            : "Access profile loaded for external project handling.";
        Step2Status = ownershipMode == ProjectOwnershipMode.ApplicationManaged
            ? "Select PLC and runtime targets and continue."
            : "Select a runtime target and continue.";
        ApplyImportedTargetDraft();
        AppendTerminalLine(ownershipMode == ProjectOwnershipMode.ApplicationManaged
            ? "Target discovery completed. Select PLC and runtime targets."
            : "Runtime target discovery completed. External project handling is active.");
        Step2InputsEnabled = true;
    }

    private void ApplyImportedTargetDraft()
    {
        var selectedAnyTarget = false;
        if (BuildAndDownloadProject && !string.IsNullOrWhiteSpace(importedProjectTargetId))
        {
            SelectedProjectTarget = ProjectTargets.FirstOrDefault(target =>
                string.Equals(target.Target.Id.Value, importedProjectTargetId, StringComparison.OrdinalIgnoreCase));
            selectedAnyTarget = SelectedProjectTarget is not null;
        }

        if (importedProvisioningMode is { } mode)
        {
            var (target, error) = RuntimeTargetResolver.Resolve(
                mode,
                accessProfileSessions.CurrentSession?.Configuration?.RuntimeTargetId,
                workflow.Current.RuntimeTargets);
            SelectedRuntimeTarget = error is not null ? null
                : mode == RuntimeProvisioningMode.CreateOwned
                    ? RuntimeTargets.FirstOrDefault(item => item.CreatesOwnedRuntime)
                    : RuntimeTargets.FirstOrDefault(item => item.Target?.Id == target?.Id);
            if (error is not null)
            {
                diagnosticReporter.Add(diagnosticReporter.CreateConfiguration(
                    "Wpf.Configuration.RuntimeTarget.NotResolved", error, LogLevel.Error,
                    loadedAccessProfileConfig?.ProfileId));
                Step2Status = error;
            }
            selectedAnyTarget = selectedAnyTarget || selectedRuntimeTarget is not null;
        }
        else if (!string.IsNullOrWhiteSpace(importedRuntimeTargetId))
        {
            SelectedRuntimeTarget = RuntimeTargets.FirstOrDefault(target =>
                string.Equals(target.Target?.Id.Value, importedRuntimeTargetId, StringComparison.OrdinalIgnoreCase));
            selectedAnyTarget = selectedAnyTarget || SelectedRuntimeTarget is not null;
        }

        if (selectedAnyTarget)
        {
            AppendTerminalLine($"{importedConfigurationOperationName}: preselected available PLC/runtime targets from profile values.");
        }
    }

    private Task OpenProfileConfigurationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanOpenProfileConfiguration())
        {
            Step2Status = "Select the required PLC and runtime targets first.";
            return Task.CompletedTask;
        }

        if (!ApplySelectedTargetsToProfileConfiguration())
        {
            return Task.CompletedTask;
        }

        ProfileConfiguration.Load(
            accessProfileSessions.CurrentSession?.Configuration
                ?? throw new InvalidOperationException("The loaded access profile has no configuration service."),
            loadedAccessProfileConfig ?? throw new InvalidOperationException("No access profile configuration is loaded."),
            selectedRuntimeTarget!.ProvisioningMode);
        SetActiveStep(3);
        return Task.CompletedTask;
    }

    private bool ApplySelectedTargetsToProfileConfiguration()
    {
        var configurationService = accessProfileSessions.CurrentSession?.Configuration;
        if (configurationService is null || loadedAccessProfileConfig is null)
        {
            ProfileConfiguration.ReportFailure(
                "The loaded access profile configuration service is not available.");
            return false;
        }

        var load = configurationService.LoadConfig(loadedAccessProfileConfig.Values.Json);
        if (!load.IsSuccessful)
        {
            ProfileConfiguration.ReportFailure(
                load.Diagnostics.FirstOrDefault()?.Message
                ?? "The access profile configuration could not be loaded.");
            return false;
        }

        var project = configurationService.SetProjectTarget(
            BuildAndDownloadProject ? selectedProjectTarget?.Target : null);
        if (!project.IsSuccessful)
        {
            ProfileConfiguration.ReportFailure(
                project.Diagnostics.FirstOrDefault()?.Message
                ?? "The selected project target could not be applied.");
            return false;
        }

        var runtime = configurationService.SetRuntimeTarget(selectedRuntimeTarget?.Target);
        if (!runtime.IsSuccessful || runtime.Value is null)
        {
            ProfileConfiguration.ReportFailure(
                runtime.Diagnostics.FirstOrDefault()?.Message
                ?? "The selected runtime target could not be applied.");
            return false;
        }

        loadedAccessProfileConfig = runtime.Value;
        return true;
    }

    private async Task ConfigureSelectedTargetsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ProfileConfiguration.Validate(updateStatus: true))
        {
            ConfigureProfileCommand.RaiseCanExecuteChanged();
            return;
        }

        if (!ValidateCommunicationTimeoutOverrides(updateStatus: true, out var communicationTimeouts))
        {
            ConfigureProfileCommand.RaiseCanExecuteChanged();
            return;
        }

        loadedAccessProfileConfig = ProfileConfiguration.Configuration;

        Step2InputsEnabled = false;

        if (loadedAccessProfileConfig is null
            || (BuildAndDownloadProject && selectedProjectTarget is null))
        {
            Step2Status = BuildAndDownloadProject
                ? "Select an access profile and project target first."
                : "Select an access profile first.";
            AppendTerminalLine($"Runtime setup blocked: {Step2Status}");
            Step2InputsEnabled = true;
            return;
        }

        if (selectedRuntimeTarget is null)
        {
            Step2Status = "Select a runtime target.";
            AppendTerminalLine($"Runtime setup blocked: {Step2Status}");
            Step2InputsEnabled = true;
            return;
        }

        var setup = new RunSetup(
            loadedAccessProfileConfig,
            selectedRuntimeTarget.ProvisioningMode,
            new OutputPolicy(applicationSettings.Current.ResolvedOutputDirectory, TestResult: true),
            new ExecutionPolicy(CreateRunTimeoutPolicy(communicationTimeouts)),
            selectedProjectTarget?.Target,
            selectedRuntimeTarget.Target);
        logger.LogInformation(
            "WPF run selection: PLC and runtime targets selected. AccessProfileId={AccessProfileId}, ProjectSource={ProjectSource}, ProjectTargetId={ProjectTargetId}, ProjectTargetDisplayName={ProjectTargetDisplayName}, ProjectTargetTechnicalName={ProjectTargetTechnicalName}, RuntimeTargetId={RuntimeTargetId}, RuntimeTargetDisplayName={RuntimeTargetDisplayName}, RuntimeTargetTechnicalName={RuntimeTargetTechnicalName}",
            loadedAccessProfileConfig.ProfileId,
            projectSource,
            selectedProjectTarget?.Target.Id.Value,
            selectedProjectTarget?.Target.DisplayName,
            selectedProjectTarget?.Target.TechnicalName,
            selectedRuntimeTarget.Target?.Id.Value,
            selectedRuntimeTarget.Target?.DisplayName,
            selectedRuntimeTarget.Target?.TechnicalName);

        var setupAlreadyConfigured = workflow.Current.Status.HasRunSetup
            && Equals(configuredRunSetup, setup);
        if (!setupAlreadyConfigured)
        {
            if (configuredRunSetup is not null
                && (workflow.Current.Status.ProjectAccessActive
                || workflow.Current.Status.RuntimePrepared
                || workflow.Current.Status.UnitTestCommunicationContextActive))
            {
                Step2Status = "Release the current setup before applying changed profile or target values.";
                AppendTerminalLine($"Run setup blocked: {Step2Status}");
                Step2InputsEnabled = true;
                return;
            }

            var configure = workflow.Configure(
                setup,
                applicationSettings.Current.OperationTimeouts);
            Step2Status = configure.IsSuccessful
                ? "Run setup configured."
                : configure.Message ?? "Run setup configuration failed.";
            if (!configure.IsSuccessful)
            {
                AppendTerminalLine($"Run setup configuration failed: {Step2Status}");
                Step2InputsEnabled = true;
                return;
            }

            configuredRunSetup = setup;
        }

        if (BuildAndDownloadProject && !workflow.Current.Status.ProjectPrepared)
        {
            Step2Status = "Preparing project...";
            AppendTerminalLine($"Opening project file: {setup.AccessProfile.ProjectSource}");
            AppendTerminalLine("Preparing project...");
            var prepareProject = await workflow.PrepareProjectAsync(
                CreatePolicy(WorkflowAction.PrepareProject),
                cancellationToken);
            if (!prepareProject.Result.IsSuccessful)
            {
                Step2Status = prepareProject.Result.Message ?? "Project preparation failed.";
                AppendTerminalLine($"Project preparation failed: {Step2Status}");
                Step2InputsEnabled = true;
                return;
            }
        }

        if (!workflow.Current.Status.RuntimePrepared)
        {
            Step2Status = "Preparing runtime...";
            AppendTerminalLine("Preparing runtime...");
            var prepareRuntime = await workflow.PrepareRuntimeAsync(
                CreatePolicy(WorkflowAction.PrepareRuntime),
                cancellationToken);
            if (!prepareRuntime.Result.IsSuccessful)
            {
                Step2Status = prepareRuntime.Result.Message ?? "Runtime preparation failed.";
                AppendTerminalLine($"Runtime preparation failed: {Step2Status}");
                Step2InputsEnabled = true;
                if (selectedRuntimeTarget.ProvisioningMode == RuntimeProvisioningMode.CreateOwned)
                {
                    SetActiveStep(2);
                }

                return;
            }
        }

        if (BuildAndDownloadProject && !workflow.Current.Status.ProjectTransferred)
        {
            if (!await EnsureSafetyProgramAuthorizationAsync(cancellationToken))
            {
                Step2InputsEnabled = true;
                return;
            }

            Step2Status = "Transferring project...";
            AppendTerminalLine("Transferring project to runtime target...");
            var transfer = await workflow.TransferProjectAsync(
                CreatePolicy(WorkflowAction.TransferProject),
                CreatePolicy(WorkflowAction.ReleaseProject),
                cancellationToken);
            if (!transfer.Result.IsSuccessful)
            {
                Step2Status = transfer.Result.Message ?? "Project transfer failed.";
                AppendTerminalLine($"Project transfer failed: {Step2Status}");
                Step2InputsEnabled = true;
                return;
            }
        }

        Step2Status = "Runtime setup is ready.";
        AppendTerminalLine("Runtime setup is ready.");
        Step2InputsEnabled = false;
        SetActiveStep(4);
    }

    private async Task<bool> EnsureSafetyProgramAuthorizationAsync(CancellationToken cancellationToken)
    {
        Step2Status = "Checking Safety program authorization...";
        AppendTerminalLine("Checking Safety program authorization...");
        var requirement = await workflow.GetSafetyProgramAuthorizationRequirementAsync(
            CreatePolicy(WorkflowAction.DiscoverSafetyProgramAuthorizationRequirement),
            cancellationToken);
        if (requirement.Result.State == OperationResultState.NotSupported)
        {
            Step2Status = requirement.Result.Message
                ?? "Safety program handling is not supported by the selected project binding.";
            AppendTerminalLine($"Project setup cannot continue: {Step2Status}");
            return false;
        }

        if (!requirement.Result.IsSuccessful || requirement.Result.Value is null)
        {
            Step2Status = requirement.Result.Message ?? "Safety program authorization check failed.";
            AppendTerminalLine($"Safety program authorization check failed: {Step2Status}");
            return false;
        }

        if (requirement.Result.Value.Requirement == SafetyProgramAuthorizationRequirement.NotRequired)
        {
            AppendTerminalLine("Safety program authorization is not required.");
            return true;
        }

        using var password = safetyProgramPasswordDialogService.RequestPassword(
            selectedProjectTarget?.Target.DisplayName ?? "Selected PLC target");
        if (password is null)
        {
            Step2Status = "Safety program authorization canceled.";
            AppendTerminalLine(Step2Status);
            return false;
        }

        Step2Status = "Authorizing Safety program...";
        AppendTerminalLine("Authorizing Safety program...");
        var authorization = await workflow.AuthorizeSafetyProgramAsync(
            password,
            CreatePolicy(WorkflowAction.AuthorizeSafetyProgram),
            cancellationToken);
        if (authorization.Result.State == OperationResultState.NotSupported)
        {
            Step2Status = authorization.Result.Message
                ?? "Safety program authorization is not supported by the selected project binding.";
            AppendTerminalLine($"Project setup cannot continue: {Step2Status}");
            return false;
        }

        if (!authorization.Result.IsSuccessful || authorization.Result.Value?.Authorized != true)
        {
            Step2Status = authorization.Result.Message ?? "Safety program authorization failed.";
            AppendTerminalLine($"Safety program authorization failed: {Step2Status}");
            return false;
        }

        AppendTerminalLine("Safety program authorization succeeded.");
        return true;
    }

    private async Task ReturnToStep2Async(CancellationToken cancellationToken)
    {
        if (activeStep == 3
            && (!workflow.Current.Status.HasRunSetup
                || (!workflow.Current.Status.ProjectAccessActive
                    && !workflow.Current.Status.RuntimePrepared
                    && !workflow.Current.Status.UnitTestCommunicationContextActive)))
        {
            ProfileConfiguration.Clear();
            Step2InputsEnabled = true;
            SetActiveStep(2);
            return;
        }

        if (!confirmations.Confirm(
            "Return to step 2 - PLC / Runtime / Communication",
            "The current operation context, UnitTest communication, and runtime target will be released."))
        {
            return;
        }

        await ExecuteContextReleaseAsync(async () =>
        {
            ClearRunResultDisplay(suppressCurrentEpoch: true);
            AppendTerminalLine("Returning to Step 2. Releasing operation/runtime context...");
            Step2InputsEnabled = false;
            SetActiveStep(2);
            var communicationReleased = await ExecuteIfAvailableAsync(
                WorkflowAction.ReleaseUnitTestCommunication,
                () => workflow.ReleaseUnitTestCommunicationAsync(
                    CreatePolicy(WorkflowAction.ReleaseUnitTestCommunication),
                    cancellationToken));
            var runtimeReleased = await ExecuteIfAvailableAsync(
                WorkflowAction.ReleaseRuntime,
                () => workflow.ReleaseRuntimeAsync(
                    CreatePolicy(WorkflowAction.ReleaseRuntime),
                    cancellationToken));
            if (communicationReleased && runtimeReleased)
            {
                configuredRunSetup = null;
                Step2Status = "PLC/runtime selection is editable again.";
                AppendTerminalLine("Operation context released. PLC/runtime selection is editable again.");
                Step2InputsEnabled = true;
                return;
            }

            Step2Status = "Release failed. Check diagnostics before changing PLC/runtime selection.";
            AppendTerminalLine($"Release failed: {Step2Status}");
        });
    }

    private async Task ReturnToStep3Async(CancellationToken cancellationToken)
    {
        if (!confirmations.Confirm(
            "Return to step 3 - Profile configuration",
            "The current operation context, UnitTest communication, and runtime target will be released."))
        {
            return;
        }

        await ExecuteContextReleaseAsync(async () =>
        {
            ClearRunResultDisplay(suppressCurrentEpoch: true);
            AppendTerminalLine("Returning to Step 3. Releasing operation/runtime context...");
            var retainedProjectTargets = ProjectTargets.ToArray();
            var retainedRuntimeTargets = RuntimeTargets.ToArray();
            var retainedProjectTarget = selectedProjectTarget;
            var retainedRuntimeTarget = selectedRuntimeTarget;
            var communicationReleased = await ExecuteIfAvailableAsync(
                WorkflowAction.ReleaseUnitTestCommunication,
                () => workflow.ReleaseUnitTestCommunicationAsync(
                    CreatePolicy(WorkflowAction.ReleaseUnitTestCommunication),
                    cancellationToken));
            var runtimeReleased = await ExecuteIfAvailableAsync(
                WorkflowAction.ReleaseRuntime,
                () => workflow.ReleaseRuntimeAsync(
                    CreatePolicy(WorkflowAction.ReleaseRuntime),
                    cancellationToken));
            if (communicationReleased && runtimeReleased)
            {
                ProfileConfiguration.Load(
                    accessProfileSessions.CurrentSession?.Configuration
                        ?? throw new InvalidOperationException("The loaded access profile has no configuration service."),
                    loadedAccessProfileConfig ?? throw new InvalidOperationException("No access profile configuration is loaded."),
                    retainedRuntimeTarget?.ProvisioningMode ?? RuntimeProvisioningMode.UseExisting);
                Replace(ProjectTargets, retainedProjectTargets);
                Replace(RuntimeTargets, retainedRuntimeTargets);
                SelectedProjectTarget = retainedProjectTarget;
                SelectedRuntimeTarget = retainedRuntimeTarget;
                configuredRunSetup = null;
                SetActiveStep(3);
                AppendTerminalLine("Operation context released. Profile configuration is editable again.");
                return;
            }

            const string failure = "Release failed. Check diagnostics before changing profile configuration.";
            ProfileConfiguration.ReportFailure(failure);
            AppendTerminalLine($"Release failed: {failure}");
        });
    }

    private async Task StartPlcAsync(CancellationToken cancellationToken)
    {
        var previousRunEpochId = runResultTree.DisplayedEpochId;
        AppendTerminalLine("Starting PLC...");
        var result = await workflow.StartPlcAsync(CreatePolicy(WorkflowAction.StartPlc), cancellationToken);
        if (!result.Result.IsSuccessful)
        {
            AppendTerminalLine($"PLC start failed: {result.Result.Message ?? "unknown error"}");
            return;
        }

        SuppressRunResultEpoch(previousRunEpochId);
        AppendTerminalLine("PLC start command accepted.");
    }

    private async Task StopPlcAsync(CancellationToken cancellationToken)
    {
        AppendTerminalLine("Stopping PLC...");
        var result = await workflow.StopPlcAsync(CreatePolicy(WorkflowAction.StopPlc), cancellationToken);
        if (!result.Result.IsSuccessful)
        {
            AppendTerminalLine($"PLC stop failed: {result.Result.Message ?? "unknown error"}");
            return;
        }

        AppendTerminalLine("PLC stop command accepted.");
    }

    private async Task StartUnitTestAsync(CancellationToken cancellationToken)
    {
        var previousRunEpochId = runResultTree.DisplayedEpochId;
        AppendTerminalLine("Starting UnitTest run...");
        ClearArtifactExportState();
        ClearTestResultArtifactPreview("No run captured yet.", status: "Not requested");
        var result = await workflow.StartUnitTestAsync(CreatePolicy(WorkflowAction.StartUnitTest), cancellationToken);
        if (!result.Result.IsSuccessful)
        {
            AppendTerminalLine($"UnitTest start failed: {result.Result.Message ?? "unknown error"}");
            return;
        }

        SuppressRunResultEpoch(previousRunEpochId);
    }

    private async Task AbortUnitTestAsync(CancellationToken cancellationToken)
    {
        AppendTerminalLine("Aborting UnitTest run...");
        var result = await workflow.AbortUnitTestAsync(CreatePolicy(WorkflowAction.AbortUnitTest), cancellationToken);
        if (!result.Result.IsSuccessful)
        {
            AppendTerminalLine($"UnitTest abort failed: {result.Result.Message ?? "unknown error"}");
            return;
        }
    }

    private async Task ResetUnitTestAsync(CancellationToken cancellationToken)
    {
        var previousRunEpochId = runResultTree.DisplayedEpochId;
        AppendTerminalLine("Resetting UnitTest runtime...");
        var result = await workflow.ResetUnitTestAsync(CreatePolicy(WorkflowAction.ResetUnitTest), cancellationToken);
        if (!result.Result.IsSuccessful)
        {
            AppendTerminalLine($"UnitTest reset failed: {result.Result.Message ?? "unknown error"}");
            return;
        }

        SuppressRunResultEpoch(previousRunEpochId);
    }

    private async Task ResetEventBufferRequestedAsync(CancellationToken cancellationToken)
    {
        if (workflow.Current.CurrentRun?.State is UnitTestRunState.Observing
                or UnitTestRunState.Running
                or UnitTestRunState.Completing
            && !confirmations.Confirm(
                "Reset Event Buffer?",
                "The current UnitTest run evidence will be retained as incomplete. All PLC EventBuffer data will then be discarded."))
        {
            return;
        }

        AppendTerminalLine("Resetting UnitTest EventBuffer...");
        var result = await workflow.ResetEventBufferAsync(
            CreatePolicy(WorkflowAction.ResetEventBuffer),
            cancellationToken);
        if (!result.Result.IsSuccessful)
        {
            AppendTerminalLine($"EventBuffer reset failed: {result.Result.Message ?? "unknown error"}");
            return;
        }

        AppendTerminalLine(result.Result.Value?.Accepted == true
            ? "EventBuffer reset command accepted. Waiting for a clean EventBuffer observation."
            : "EventBuffer reset command was not accepted.");
    }

    private async Task ReconnectRuntimeRequestedAsync(CancellationToken cancellationToken)
    {
        if (RunMonitorState is TestRunMonitoringState.RunActive or TestRunMonitoringState.CompletionPending
            && !confirmations.Confirm(
                "Reconnect Runtime?",
                "The active UnitTest run will be retained as incomplete. Runtime and UnitTest communication will then be reconnected without downloading or reprovisioning."))
        {
            return;
        }

        await ExecuteCancellableOperationAsync(
            "Reconnecting runtime...",
            ReconnectRuntimeAsync,
            () => AppendTerminalLine("Runtime reconnect canceled before the existing session was detached."),
            cancellationToken);
    }

    private async Task ReconnectRuntimeAsync(CancellationToken cancellationToken)
    {
        AppendTerminalLine("Reconnecting runtime and UnitTest communication...");
        var result = await workflow.ReconnectRuntimeAsync(
            CreatePolicy(WorkflowAction.ReconnectRuntime),
            cancellationToken);
        if (!result.Result.IsSuccessful)
        {
            AppendTerminalLine($"Runtime reconnect failed: {result.Result.Message ?? "unknown error"}");
            return;
        }

        AppendTerminalLine("Runtime reconnected.");
    }

    private async Task ReturnToStep1Async(CancellationToken cancellationToken)
    {
        if (!confirmations.Confirm(
            "Return to step 1 - Project / Profile",
            "The prepared runtime, UnitTest communication, project context, and access profile will be released."))
        {
            return;
        }

        await ExecuteContextReleaseAsync(async () =>
        {
            ClearRunResultDisplay(suppressCurrentEpoch: true);
            AppendTerminalLine("Returning to Step 1. Releasing project/profile context...");
            ReleaseStep2UiSelection();
            await YieldUiAsync();
            await ReleaseSessionResourcesAsync();
            ResetTargetSelection(clearProfileDraft: true);
            ResetCommunicationTimeoutOverrides();
            RefreshLocalConfigurationStatus();
            Step1Status = "Project/profile selection is editable again.";
            Step2Status = "Load an access profile before selecting PLC/runtime targets.";
            AppendTerminalLine("Project/profile context released.");
            SetActiveStep(1);
        });
    }

    private async Task EndSessionAsync(CancellationToken cancellationToken)
    {
        if (!confirmations.Confirm(
            "End Session?",
            "The current session, prepared runtime, UnitTest communication, project context, access profile, and configuration draft will be released."))
        {
            return;
        }

        await ExecuteContextReleaseAsync(async () =>
        {
            ClearRunResultDisplay(suppressCurrentEpoch: true);
            AppendTerminalLine("Ending session. Releasing workflow resources...");
            ReleaseStep2UiSelection();
            await YieldUiAsync();
            await ReleaseSessionResourcesAsync();
            ResetTargetSelection(clearProfileDraft: true);
            BuildAndDownloadProject = applicationSettings.Current.BuildAndDownloadProjectByDefault;
            ResetCommunicationTimeoutOverrides();
            SelectedAccessProfile = AvailableAccessProfiles.FirstOrDefault();
            RefreshLocalConfigurationStatus();
            Step1Status = "Select a project/profile source.";
            Step2Status = "Load an access profile before selecting PLC/runtime targets.";
            AppendTerminalLine("Session ended.");
            SetActiveStep(1);
        });
    }

    private async Task UnloadAccessProfileSessionAsync()
    {
        try
        {
            await accessProfileSessions.UnloadAsync(
                applicationSettings.Current.OperationTimeouts.ProfileUnload,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Access-profile unload failed after profile binding was rejected.");
            diagnosticReporter.ReportCleanupFailure(
                "Wpf.Cleanup.AccessProfileUnload.Failed",
                "Access-profile unload failed after profile binding was rejected.",
                ex.Message);
        }
    }

    private async Task ReleaseSessionResourcesAsync()
    {
        var result = await sessionRelease.ReleaseAsync(
            CreatePolicy(WorkflowAction.Cleanup),
            applicationSettings.Current.OperationTimeouts.ProfileUnload,
            CancellationToken.None);

        if (!result.ActiveOperationRelease.IsSuccessful)
        {
            logger.LogWarning(
                "Active workflow operation release completed without success. ResultState={ResultState}, Message={Message}",
                result.ActiveOperationRelease.State,
                result.ActiveOperationRelease.Message);
            diagnosticReporter.ReportCleanupFailure(
                "Wpf.Cleanup.ActiveOperation.Failed",
                "The active workflow operation did not stop cleanly before session release continued.",
                result.ActiveOperationRelease.Message);
        }

        if (!result.WorkflowCleanup.IsSuccessful)
        {
            logger.LogWarning(
                "Workflow cleanup completed without success. ResultState={ResultState}, Message={Message}",
                result.WorkflowCleanup.State,
                result.WorkflowCleanup.Message);
            diagnosticReporter.ReportCleanupFailure(
                "Wpf.Cleanup.Workflow.Failed",
                "Workflow cleanup did not complete successfully. Access-profile unload was still attempted.",
                result.WorkflowCleanup.Message);
        }

        if (!result.ProfileUnload.IsSuccessful)
        {
            logger.LogWarning(
                "Access-profile unload completed without success. ResultState={ResultState}, Message={Message}",
                result.ProfileUnload.State,
                result.ProfileUnload.Message);
            diagnosticReporter.ReportCleanupFailure(
                "Wpf.Cleanup.AccessProfileUnload.Failed",
                "Access-profile unload did not complete successfully.",
                result.ProfileUnload.Message);
        }
    }

    private Task OpenApplicationLogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configuredPath = LogFilePath;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            ReportApplicationLogUnavailable("Application log path is not configured.", null);
            return Task.CompletedTask;
        }

        var path = logFileOpenService.ResolveExistingLogFilePath(configuredPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            ReportApplicationLogUnavailable($"Application log file is not available for configured path: {configuredPath}", configuredPath);
            return Task.CompletedTask;
        }

        try
        {
            logFileOpenService.Open(path);
            AppendTerminalLine($"Application log opened: {path}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Application log could not be opened. Path={LogFilePath}", path);
            ReportApplicationLogUnavailable($"Application log file could not be opened: {path}", path, ex.Message);
        }

        return Task.CompletedTask;
    }

    private Task OpenAccessProfileLogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configuredPath = GetAccessProfileLogPath();
        var path = configuredPath is null
            ? null
            : logFileOpenService.ResolveExistingLogFilePath(configuredPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        try
        {
            logFileOpenService.Open(path);
            AppendTerminalLine($"Access profile log opened: {path}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Access profile log could not be opened. Path={LogFilePath}", path);
        }

        return Task.CompletedTask;
    }

    private Task OpenLogFolderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = applicationSettings.Current.ResolvedLogDirectory;
        if (!Directory.Exists(path))
        {
            return Task.CompletedTask;
        }

        try
        {
            logFileOpenService.Open(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Log folder could not be opened. Path={LogDirectory}", path);
        }

        return Task.CompletedTask;
    }

    private Task OpenSettingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (settingsDialogService.ShowSettingsDialog())
            {
                AppendTerminalLine("Application settings saved.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Application settings could not be saved.");
            diagnosticReporter.ReportSettingsSaveFailure(ex.Message);
        }

        return Task.CompletedTask;
    }

    private Task OpenTestResultArtifactAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = testResultArtifact?.OutputPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ReportArtifactUnavailable("Test result artifact file is not available.", path);
            return Task.CompletedTask;
        }

        try
        {
            logFileOpenService.Open(path);
            AppendTerminalLine($"Test result artifact opened: {path}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Test result artifact could not be opened. Path={ArtifactPath}", path);
            ReportArtifactUnavailable($"Test result artifact could not be opened: {path}", path, ex.Message);
        }

        return Task.CompletedTask;
    }

    private Task OpenOutputFolderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directory = GetTestResultArtifactDirectory();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            ReportArtifactUnavailable("Output folder is not available.", directory);
            return Task.CompletedTask;
        }

        try
        {
            logFileOpenService.Open(directory);
            AppendTerminalLine($"Output folder opened: {directory}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Output folder could not be opened. Path={OutputFolder}", directory);
            ReportArtifactUnavailable($"Output folder could not be opened: {directory}", directory, ex.Message);
        }

        return Task.CompletedTask;
    }

    private Task ClearOutputAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        publicationOutputInitialized = true;
        lastOutputRunMonitorState = RunMonitorState;
        lastOutputMonitoringActive = workflow.Current.Status.MonitoringActive;
        lastOutputCommunicationRevalidationRequired =
            WorkflowStatusViewStateMapper.IsCommunicationRevalidationRequired(workflow.Current.Status);
        terminalOutput.Clear();
        TerminalText = terminalOutput.Text;
        ClearOutputCommand.RaiseCanExecuteChanged();

        return Task.CompletedTask;
    }

    private Task ClearDiagnosticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        workflow.ClearDiagnostics();

        return Task.CompletedTask;
    }

    private Task ClearRunResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ClearRunResultDisplay(suppressCurrentEpoch: true);
        AppendTerminalLine("Run result cleared.");

        return Task.CompletedTask;
    }

    private Task AnalyzeCurrentRunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runEpochId = GetLatestRunEpochId();
        if (runEpochId is not null)
        {
            PresentAnalysis(() => workflow.EvidenceAnalysis.AnalyzeRunPackage(runEpochId.Value));
        }

        return Task.CompletedTask;
    }

    private Task AnalyzeLifecycleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observationId = GetLatestLifecycleObservationId();
        if (observationId is not null)
        {
            PresentAnalysis(() => workflow.EvidenceAnalysis.AnalyzeLifecyclePackage(observationId.Value));
        }

        return Task.CompletedTask;
    }

    private Task AnalyzeCommunicationSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PresentAnalysis(() => workflow.EvidenceAnalysis.AnalyzeCommunicationSessionPackage());
        return Task.CompletedTask;
    }

    private Task AnalyzeSelectedDiagnosticAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectedDiagnostic?.RunEpochId is { } runEpochId)
        {
            PresentAnalysis(() => workflow.EvidenceAnalysis.AnalyzeRunPackage(runEpochId));
        }
        else if (SelectedDiagnostic?.LifecycleObservationId is { } lifecycleObservationId)
        {
            PresentAnalysis(() => workflow.EvidenceAnalysis.AnalyzeLifecyclePackage(lifecycleObservationId));
        }
        else
        {
            PresentAnalysis(() => workflow.EvidenceAnalysis.AnalyzeCommunicationSessionPackage());
        }

        return Task.CompletedTask;
    }

    private async Task ExportAnalysisAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var package = analysisPackage;
        if (package is null)
        {
            return;
        }

        var path = analysisReportFileDialogService.SelectPackageExportPath(analysisExportFileName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await evidenceAnalysisPackageWriter.WriteAsync(package, path, cancellationToken);
            AppendTerminalLine($"Evidence analysis package exported: {path}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Evidence analysis could not be exported. Path={AnalysisExportPath}", path);
            diagnosticReporter.ReportAnalysisExportFailure(
                "Evidence analysis could not be exported.",
                path,
                ex.Message);
        }

    }

    private Task ClearAnalysisAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var trace = workflow.EvidenceAnalysis.CurrentTrace;
        var traceCleared = workflow.EvidenceAnalysis.ClearCompletedTrace();
        analysisPackage = null;
        AnalysisReport = EmptyAnalysisReport;
        AnalysisStatus = EmptyAnalysisStatus;
        AnalysisRefreshStatus = EmptyAnalysisStatus;
        AnalysisFreshnessStatus = string.Empty;
        analysisExportFileName = "unittest-evidence-analysis.zip";
        RaiseAnalysisIdentityChanged();

        if (traceCleared)
        {
            AppendTerminalLine("Evidence analysis and completed activity trace cleared.");
        }
        else if (trace?.IsCaptureActive == true)
        {
            AppendTerminalLine("Evidence analysis cleared. Active activity trace retained.");
        }
        else
        {
            AppendTerminalLine("Evidence analysis cleared.");
        }

        RefreshCommandStates();
        return Task.CompletedTask;
    }

    private void PresentAnalysis(Func<EvidenceAnalysisPackage> createPackage)
    {
        var previous = analysisPackage;
        AnalysisRefreshStatus = "Generating analysis...";
        try
        {
            var package = createPackage();
            var analysis = package.Analysis;
            analysisPackage = package;
            AnalysisReport = package.AnalysisMarkdown;
            AnalysisStatus = $"{AnalysisScopeLabel(analysis.Scope)} / {analysis.Assessment} / {analysis.Completeness} / {analysis.Incidents.Count} finding(s)";
            AnalysisRefreshStatus = CreateAnalysisRefreshStatus(previous, package);
            RefreshAnalysisFreshnessStatus();
            analysisExportFileName = $"unittest-evidence-analysis-{AnalysisScopeFileName(analysis.Scope)}-{package.Metadata.CapturedAt:yyyyMMdd-HHmmss}.zip";
            RaiseAnalysisIdentityChanged();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            analysisPackage = null;
            AnalysisStatus = "Analysis unavailable";
            AnalysisRefreshStatus = "Analysis unavailable";
            AnalysisFreshnessStatus = string.Empty;
            AnalysisReport = ex.Message;
            RaiseAnalysisIdentityChanged();
            logger.LogWarning(ex, "Evidence analysis could not be generated.");
        }

        AnalysisPresented?.Invoke(this, EventArgs.Empty);
    }

    private static string AnalysisScopeLabel(EvidenceAnalysisScope scope) => scope switch
    {
        EvidenceAnalysisScope.Session => "Communication Health",
        _ => scope.ToString()
    };

    private static string AnalysisScopeFileName(EvidenceAnalysisScope scope) => scope switch
    {
        EvidenceAnalysisScope.Session => "communication-health",
        _ => scope.ToString().ToLowerInvariant()
    };

    internal void RefreshAnalysisFreshnessStatus()
    {
        var package = analysisPackage;
        if (package is null)
        {
            AnalysisFreshnessStatus = string.Empty;
            return;
        }

        var trace = workflow.EvidenceAnalysis.CurrentTrace;
        if (trace is null || trace.SessionId != package.Metadata.SessionId)
        {
            AnalysisFreshnessStatus = "Previous session";
            return;
        }

        var hasNewEvidence = trace.Entries.Any(entry =>
            entry.Sequence > package.Metadata.ActivityThroughSequence
            && IsRelevantToDisplayedAnalysis(entry, package));
        AnalysisFreshnessStatus = hasNewEvidence
            ? "New evidence available"
            : "Analysis is current";
    }

    private static bool IsRelevantToDisplayedAnalysis(
        UnitTestActivityEntry entry,
        EvidenceAnalysisPackage package)
    {
        return package.Analysis.Scope switch
        {
            EvidenceAnalysisScope.Run => entry.RunEpochId == package.Analysis.RunEpochId,
            EvidenceAnalysisScope.Lifecycle =>
                entry.LifecycleObservationId == package.Analysis.LifecycleObservationId,
            EvidenceAnalysisScope.Session => true,
            _ => false
        };
    }

    private static string CreateAnalysisRefreshStatus(
        EvidenceAnalysisPackage? previous,
        EvidenceAnalysisPackage current)
    {
        if (previous is null
            || previous.Analysis.Scope != current.Analysis.Scope
            || previous.Analysis.RunEpochId != current.Analysis.RunEpochId
            || previous.Analysis.LifecycleObservationId != current.Analysis.LifecycleObservationId)
        {
            return $"Analysis generated · {current.Analysis.GeneratedAt.ToLocalTime():HH:mm:ss}";
        }

        var newActivityCount = Math.Max(
            0,
            current.Analysis.AnalyzedThroughSequence - previous.Analysis.AnalyzedThroughSequence);
        return newActivityCount == 0
            ? $"Analysis refreshed · no new evidence · {current.Analysis.GeneratedAt.ToLocalTime():HH:mm:ss}"
            : $"Analysis updated · {newActivityCount} new activit{(newActivityCount == 1 ? "y" : "ies")} · {current.Analysis.GeneratedAt.ToLocalTime():HH:mm:ss}";
    }

    private void RaiseAnalysisIdentityChanged()
    {
        OnPropertyChanged(nameof(AnalysisTitle));
        OnPropertyChanged(nameof(AnalysisFocus));
        OnPropertyChanged(nameof(AnalysisAssessment));
        OnPropertyChanged(nameof(AnalysisCompleteness));
        OnPropertyChanged(nameof(AnalysisGeneratedAt));
        OnPropertyChanged(nameof(AnalysisActivityRange));
        OnPropertyChanged(nameof(IsRunAnalysisDisplayed));
        OnPropertyChanged(nameof(IsLifecycleAnalysisDisplayed));
        OnPropertyChanged(nameof(IsCommunicationSessionAnalysisDisplayed));
    }

    private RunEpochId? GetLatestRunEpochId()
    {
        if (workflow.Current.CurrentRun is { } currentRun)
        {
            return currentRun.EpochId;
        }

        var trace = workflow.EvidenceAnalysis.CurrentTrace;
        if (trace is null)
        {
            return null;
        }

        for (var index = trace.Entries.Count - 1; index >= 0; index--)
        {
            if (trace.Entries[index].RunEpochId is { } runEpochId)
            {
                return runEpochId;
            }
        }

        return null;
    }

    private LifecycleObservationId? GetLatestLifecycleObservationId()
    {
        var trace = workflow.EvidenceAnalysis.CurrentTrace;
        if (trace is null)
        {
            return null;
        }

        for (var index = trace.Entries.Count - 1; index >= 0; index--)
        {
            var entry = trace.Entries[index];
            if (entry.LifecycleObservationId is { } observationId)
            {
                return observationId;
            }

            if (entry.Payload is DiagnosticActivityPayload
                {
                    Diagnostic.Correlation.LifecycleObservationId: { } diagnosticObservationId
                })
            {
                return diagnosticObservationId;
            }
        }

        return null;
    }

    private void OnWorkflowPublication(object? sender, RunWorkflowPublicationEventArgs args)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ApplyPublication(args.Publication));
            return;
        }

        ApplyPublication(args.Publication);
    }

    private void OnArtifactOutputStateChanged(object? sender, OutputStateChangedEventArgs args)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ApplyArtifactOutputState(args.OutputState, args.RunEpochId));
            return;
        }

        ApplyArtifactOutputState(args.OutputState, args.RunEpochId);
    }

    private void OnDiagnosticSnapshotChanged(object? sender, DiagnosticSnapshotChangedEventArgs args)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ApplyDiagnosticSnapshot(args.Snapshot));
            return;
        }

        ApplyDiagnosticSnapshot(args.Snapshot);
    }

    private void ApplyArtifactOutputState(
        OutputState outputState,
        RunEpochId? runEpochId = null,
        bool requireRunEpochMatch = true)
    {
        if (isDisposed)
        {
            return;
        }

        if (requireRunEpochMatch
            && runEpochId is not null
            && !string.Equals(runResultTree.DisplayedEpochId, runEpochId.Value.Value, StringComparison.Ordinal))
        {
            return;
        }

        RefreshTestResultArtifact(outputState);
        ApplyArtifactTerminalOutput(outputState.TerminalText);
        OpenTestResultArtifactCommand.RaiseCanExecuteChanged();
        OpenOutputFolderCommand.RaiseCanExecuteChanged();
    }

    private void ApplyPublication(RunWorkflowPublication publication)
    {
        if (publication.Sequence <= lastAppliedWorkflowPublicationSequence)
        {
            logger.LogDebug(
                "Ignoring stale workflow publication. Sequence={Sequence}, LastAppliedSequence={LastAppliedSequence}, State={WorkflowState}",
                publication.Sequence,
                lastAppliedWorkflowPublicationSequence,
                publication.State);
            return;
        }

        lastAppliedWorkflowPublicationSequence = publication.Sequence;
        logger.LogDebug(
            "Applying workflow publication. Sequence={Sequence}, State={WorkflowState}, ActiveOperation={ActiveOperation}, OperationInProgress={OperationInProgress}, DiagnosticCount={DiagnosticCount}",
            publication.Sequence,
            publication.State,
            publication.ActiveOperation?.Action,
            publication.Status.OperationInProgress,
            publication.Diagnostics.Count);

        ApplyWorkflowStatus(publication);
        ApplyFooterContext(publication);
        ApplyPreparationStatus(publication);
        ApplyRunResult(publication);
        ApplyDiagnostics(publication);
        ApplyOutput(publication);
        ApplyTargetSelections(publication);
        ApplyActionGates(publication.ActionGates);
        RefreshDerivedStatusProperties();
        RefreshCommandStates();
    }

    private void ApplyWorkflowStatus(RunWorkflowPublication publication)
    {
        var viewState = WorkflowStatusViewStateMapper.MapWorkflowStatus(publication);
        WorkflowState = publication.State;
        workflowActiveOperation = viewState.ActiveOperation;
        workflowActivityIndicatorActive = viewState.ActivityIndicatorActive;
        RefreshActivityPresentation();
        PlcProcessStatusViewState = viewState.PlcProcessStatus;
        CommunicationStatusViewState = viewState.CommunicationStatus;
        MonitoringStatusViewState = viewState.MonitoringStatus;
        RunControlStatusViewState = viewState.RunControlStatus;
        RunMonitorState = viewState.RunMonitorState;
        RunStatus = viewState.RunStatus;
        projectAccessActive = publication.Status.ProjectAccessActive;
        OnPropertyChanged(nameof(ProjectLockIconVisibility));
    }

    private void ApplyFooterContext(RunWorkflowPublication publication)
    {
        ProfileStatus = CreateProfileDisplayName();
        ProjectStatus = CreateProjectDisplayName();
        PlcStatus = CreatePlcDisplayName();
        RuntimeStatus = CreateRuntimeDisplayName(publication);
        LogFilePath = applicationLogFilePath;
    }

    private void ApplyRunResult(RunWorkflowPublication publication)
    {
        RefreshRunResultTree(publication.CurrentRun);
    }

    private void ApplyDiagnostics(RunWorkflowPublication publication)
    {
        RefreshDiagnostics(diagnostics.Reader.Current);
    }

    private void ApplyOutput(RunWorkflowPublication publication)
    {
        AppendPublicationOutput(publication);
    }

    private void ApplyTargetSelections(RunWorkflowPublication publication)
    {
        ReplaceProjectTargets(publication);
        ReplaceRuntimeTargets(publication);
    }

    private void ApplyActionGates(IReadOnlyList<ActionGateState> actionGates)
    {
        currentActionGates = actionGates;
        Replace(ActionGates, actionGates.Select(ActionGateViewModel.FromGate));
        Step1GateStatus = CreateStep1GateStatus();
        Step2GateStatus = CreateStep2GateStatus();
        ReturnToStep1GateStatus = CreateReleaseGateStatus(WorkflowAction.UnloadAccessProfile, "Return to Step 1");
        ReturnToStep2GateStatus = CreateReleaseGateStatus(WorkflowAction.ReleaseRuntime, "Return to Step 2");
        EndSessionGateStatus = CreateGateStatus(WorkflowAction.Cleanup, "End Session");
        OnPropertyChanged(nameof(Step3GateStatus));
        OnPropertyChanged(nameof(StartPlcGateStatus));
        OnPropertyChanged(nameof(StopPlcGateStatus));
        OnPropertyChanged(nameof(StartUnitTestGateStatus));
        OnPropertyChanged(nameof(AbortUnitTestGateStatus));
        OnPropertyChanged(nameof(ResetUnitTestGateStatus));
        OnPropertyChanged(nameof(ResetEventBufferGateStatus));
        OnPropertyChanged(nameof(ReconnectRuntimeGateStatus));
    }

    private void RefreshDerivedStatusProperties()
    {
        OnPropertyChanged(nameof(ProfileStatusBrush));
        OnPropertyChanged(nameof(ProjectStatusBrush));
        OnPropertyChanged(nameof(PlcStatusBrush));
        OnPropertyChanged(nameof(RuntimeStatusBrush));
        OnPropertyChanged(nameof(PlcProcessStatusBrush));
        OnPropertyChanged(nameof(CommunicationStatusBrush));
        OnPropertyChanged(nameof(MonitoringStatusBrush));
        OnPropertyChanged(nameof(RunControlStatusBrush));
        OnPropertyChanged(nameof(PreparationStatusBrush));
        OnPropertyChanged(nameof(DiagnosticStatusBrush));
    }

    private void OnApplicationSettingsChanged(object? sender, EventArgs e)
    {
        ApplyApplicationSettings();
    }

    private void ApplyApplicationSettings()
    {
        LogFilePath = applicationLogFilePath;
        if (activeStep == 1 && loadedAccessProfileConfig is null)
        {
            ResetCommunicationTimeoutOverrides();
        }

        OpenApplicationLogCommand?.RaiseCanExecuteChanged();
        OpenAccessProfileLogCommand?.RaiseCanExecuteChanged();
        OpenLogFolderCommand?.RaiseCanExecuteChanged();
        OpenOutputFolderCommand?.RaiseCanExecuteChanged();
    }

    private bool ValidateCommunicationTimeoutOverrides(
        bool updateStatus,
        out CommunicationTimeoutPolicy policy)
    {
        policy = applicationSettings.Current.RunTimeouts.ToCommunicationTimeoutPolicy();
        if (!int.TryParse(
                CommunicationPrepareTimeoutSeconds,
                NumberStyles.Integer,
                WpfApplicationDefaults.Culture,
                out var prepareSeconds)
            || prepareSeconds <= 0)
        {
            if (updateStatus)
            {
                SessionSettingsStatus = "Communication preparation timeout must be a positive whole number of seconds.";
            }

            return false;
        }

        if (!int.TryParse(
                CommunicationRevalidationTimeoutSeconds,
                NumberStyles.Integer,
                WpfApplicationDefaults.Culture,
                out var revalidationSeconds)
            || revalidationSeconds < 0)
        {
            if (updateStatus)
            {
                SessionSettingsStatus = "Communication revalidation timeout must be a non-negative whole number of seconds.";
            }

            return false;
        }

        policy = new CommunicationTimeoutPolicy(
            TimeSpan.FromSeconds(prepareSeconds),
            TimeSpan.FromSeconds(revalidationSeconds));
        if (updateStatus)
        {
            SessionSettingsStatus = revalidationSeconds == 0
                ? "Communication revalidation has no automatic terminal timeout."
                : $"Communication revalidation becomes terminal after {revalidationSeconds} seconds.";
        }

        return true;
    }

    private void ResetCommunicationTimeoutOverrides()
    {
        var applicationTimeouts = applicationSettings.Current.RunTimeouts;
        CommunicationPrepareTimeoutSeconds = Convert.ToInt32(
                (importedTimeoutOverrides?.CommunicationPrepare ?? applicationTimeouts.CommunicationPrepare).TotalSeconds)
            .ToString(WpfApplicationDefaults.Culture);
        CommunicationRevalidationTimeoutSeconds = Convert.ToInt32(
                (importedTimeoutOverrides?.CommunicationRevalidation ?? applicationTimeouts.CommunicationRevalidation).TotalSeconds)
            .ToString(WpfApplicationDefaults.Culture);
        ValidateCommunicationTimeoutOverrides(updateStatus: true, out _);
    }

    private RunOperationTimeoutPolicy CreateRunTimeoutPolicy(CommunicationTimeoutPolicy communicationTimeouts)
    {
        var applicationTimeouts = applicationSettings.Current.ToTimeoutPolicy();
        var imported = importedTimeoutOverrides;
        return applicationTimeouts with
        {
            ProjectPrepare = imported?.ProjectPrepare ?? applicationTimeouts.ProjectPrepare,
            BuildTransfer = imported?.BuildTransfer ?? applicationTimeouts.BuildTransfer,
            RuntimePrepare = imported?.RuntimePrepare ?? applicationTimeouts.RuntimePrepare,
            PlcCommand = imported?.PlcCommand ?? applicationTimeouts.PlcCommand,
            PlcStartReaction = imported?.PlcStartReaction ?? applicationTimeouts.PlcStartReaction,
            PlcStopReaction = imported?.PlcStopReaction ?? applicationTimeouts.PlcStopReaction,
            CommunicationPrepare = communicationTimeouts.Prepare,
            CommunicationRevalidation = communicationTimeouts.Revalidation,
            UnitTestCommand = imported?.UnitTestCommand ?? applicationTimeouts.UnitTestCommand,
            UnitTestCommandReaction = imported?.UnitTestCommandReaction ?? applicationTimeouts.UnitTestCommandReaction,
            TestRun = imported?.TestRun ?? applicationTimeouts.TestRun,
            FinalEvent = imported?.FinalEvent ?? applicationTimeouts.FinalEvent
        };
    }

    private void RefreshCommandStates()
    {
        OpenApplicationLogCommand.RaiseCanExecuteChanged();
        OpenAccessProfileLogCommand.RaiseCanExecuteChanged();
        OpenLogFolderCommand.RaiseCanExecuteChanged();
        OpenTestResultArtifactCommand.RaiseCanExecuteChanged();
        OpenOutputFolderCommand.RaiseCanExecuteChanged();
        LoadStep1Command.RaiseCanExecuteChanged();
        BrowseProjectSourceCommand.RaiseCanExecuteChanged();
        ClearProjectSourceCommand.RaiseCanExecuteChanged();
        RefreshProfileRegistrationsCommand.RaiseCanExecuteChanged();
        CancelCurrentOperationCommand.RaiseCanExecuteChanged();
        ConfigureStep2Command.RaiseCanExecuteChanged();
        ConfigureProfileCommand.RaiseCanExecuteChanged();
        StartPlcCommand.RaiseCanExecuteChanged();
        StopPlcCommand.RaiseCanExecuteChanged();
        StartUnitTestCommand.RaiseCanExecuteChanged();
        AbortUnitTestCommand.RaiseCanExecuteChanged();
        ResetUnitTestCommand.RaiseCanExecuteChanged();
        ResetEventBufferCommand.RaiseCanExecuteChanged();
        ReconnectRuntimeCommand.RaiseCanExecuteChanged();
        ReturnToStep1Command.RaiseCanExecuteChanged();
        ReturnToStep2Command.RaiseCanExecuteChanged();
        ReturnToStep3Command.RaiseCanExecuteChanged();
        EndSessionCommand.RaiseCanExecuteChanged();
        RaiseConfigurationFileCommandCanExecuteChanged();
        ClearOutputCommand.RaiseCanExecuteChanged();
        ClearDiagnosticsCommand.RaiseCanExecuteChanged();
        ClearRunResultCommand.RaiseCanExecuteChanged();
        AnalyzeCurrentRunCommand.RaiseCanExecuteChanged();
        AnalyzeLifecycleCommand.RaiseCanExecuteChanged();
        AnalyzeCommunicationSessionCommand.RaiseCanExecuteChanged();
        AnalyzeSelectedDiagnosticCommand.RaiseCanExecuteChanged();
        ExportAnalysisCommand.RaiseCanExecuteChanged();
        ClearAnalysisCommand.RaiseCanExecuteChanged();
    }

    private void RefreshTestResultArtifact(OutputState outputState)
    {
        ApplyTestResultArtifactPresentation(
            TestResultArtifactPresentationMapper.Map(
                outputState,
                !string.IsNullOrWhiteSpace(runResultTree.DisplayedEpochId)));
    }

    private void RefreshRunResultTree(UnitTestRunEpochSnapshot? currentRun)
    {
        var presentation = runResultTree.Refresh(currentRun, selectedRunResultNode?.Id);
        ApplyRunResultTreePresentation(presentation);
        if (presentation.Tree.Count == 0)
        {
            ClearArtifactExportState();
            ClearTestResultArtifactPreview("No run captured yet.");
        }

        ClearRunResultCommand.RaiseCanExecuteChanged();
    }

    private void ClearRunResultDisplay(bool suppressCurrentEpoch)
    {
        ApplyRunResultTreePresentation(
            runResultTree.Clear(suppressCurrentEpoch, workflow.Current.CurrentRun?.EpochId.Value));
        ClearArtifactExportState();
        ClearTestResultArtifactPreview("No run captured yet.");
        ClearRunResultCommand.RaiseCanExecuteChanged();
    }

    private void ClearTestResultArtifactPreview(string preview, string? status = null)
    {
        ApplyTestResultArtifactPresentation(
            TestResultArtifactPresentationMapper.Clear(preview, status ?? TestResultArtifactStatus));
    }

    private void SuppressRunResultEpoch(string? epochId)
    {
        var previousDisplayedEpochId = runResultTree.DisplayedEpochId;
        ApplyRunResultTreePresentation(
            runResultTree.Suppress(epochId, RunResultTree.ToList(), selectedRunResultNode?.Id),
            applyStatus: false);
        if (!string.IsNullOrWhiteSpace(epochId)
            && string.Equals(previousDisplayedEpochId, epochId, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(runResultTree.DisplayedEpochId))
        {
            ClearArtifactExportState();
            ClearTestResultArtifactPreview("No run captured yet.");
            ClearRunResultCommand.RaiseCanExecuteChanged();
        }
    }

    public void ToggleRunResultNodeExpansion(RunResultNodeViewModel? node)
    {
        ApplyRunResultTreePresentation(
            runResultTree.ToggleExpansion(node, RunResultTree.ToList(), selectedRunResultNode?.Id),
            applyStatus: false);
    }

    private void ApplyRunResultTreePresentation(
        RunResultTreePresentationState presentation,
        bool applyStatus = true)
    {
        Replace(RunResultTree, presentation.Tree);
        Replace(RunResultRows, presentation.Rows);
        if (applyStatus)
        {
            RunResultStatus = presentation.Status;
        }

        runResultLostEventCount = presentation.LostEventCount;
        OnPropertyChanged(nameof(RunResultLostEventsVisibility));
        OnPropertyChanged(nameof(RunResultLostEventsText));
        OnPropertyChanged(nameof(RunResultLostEventsToolTip));

        SelectedRunResultNode = presentation.SelectedNode;
    }

    private bool IsGateAvailable(WorkflowAction action)
    {
        return currentActionGates
            .FirstOrDefault(gate => gate.Action == action)
            ?.IsAvailable == true;
    }

    private string CreateGateStatus(
        WorkflowAction action,
        string? label = null)
    {
        var gate = currentActionGates.FirstOrDefault(item => item.Action == action);
        if (gate is null)
        {
            return $"{label ?? action.ToString()}: not evaluated";
        }

        var reason = gate.Reasons.Count == 0
            ? string.Empty
            : $" - {string.Join("; ", gate.Reasons.Select(item => item.Message))}";
        return $"{label ?? action.ToString()}: {gate.Availability}{reason}";
    }

    private string CreateStep2GateStatus()
    {
        if (loadedAccessProfileConfig is null)
        {
            return "Next: Blocked - access profile is not loaded.";
        }

        if (BuildAndDownloadProject && selectedProjectTarget is null)
        {
            return "Next: Blocked - PLC target is not selected.";
        }

        if (selectedRuntimeTarget is null)
        {
            return "Next: Blocked - runtime target is not selected.";
        }

        return CreateGateStatus(WorkflowAction.DiscoverRuntimeTargets, "Next");
    }

    private string CreateStep1GateStatus()
    {
        if (IsInteractionBlocked())
        {
            return "Next: Blocked - another operation is active.";
        }

        if (selectedAccessProfile is null)
        {
            return "Next: Blocked - access profile is not selected.";
        }

        if (!selectedAccessProfile.IsRegistered)
        {
            return "Next: Blocked - selected access profile is not registered.";
        }

        if (BuildAndDownloadProject && string.IsNullOrWhiteSpace(projectSource))
        {
            return "Next: Blocked - project source is not selected.";
        }

        return CreateGateStatus(WorkflowAction.LoadAccessProfile, "Next");
    }

    private string CreateStep3GateStatus()
    {
        if (!ProfileConfiguration.Validate(updateStatus: false))
        {
            return $"Next: Blocked - {ProfileConfiguration.Status}";
        }

        if (!ValidateCommunicationTimeoutOverrides(updateStatus: false, out _))
        {
            return $"Next: Blocked - {SessionSettingsStatus}";
        }

        return "Next: Available - profile and session configuration are valid.";
    }

    private string CreateReleaseGateStatus(
        WorkflowAction action,
        string label)
    {
        if (!HasSessionContext())
        {
            return $"{label}: NotApplicable";
        }

        return CreateGateStatus(action, label);
    }

    private string CreateProfileDisplayName()
    {
        return workflow.Current.State >= RunWorkflowState.AccessProfileLoaded && selectedAccessProfile is not null
            ? selectedAccessProfile.DisplayName
            : "No profile loaded";
    }

    private string CreateProjectDisplayName()
    {
        return string.IsNullOrWhiteSpace(projectSource)
            ? "No project"
            : projectSource;
    }

    private string CreatePlcDisplayName()
    {
        if (selectedProjectTarget is not null)
        {
            return selectedProjectTarget.DisplayName;
        }

        return selectedRuntimeTarget?.Target is not null
            ? "Runtime PLC"
            : "No PLC";
    }

    private string CreateRuntimeDisplayName(RunWorkflowPublication? publication = null)
    {
        if (selectedRuntimeTarget is not null)
        {
            return selectedRuntimeTarget.DisplayName;
        }

        var candidateCount = publication?.RuntimeTargets?.Candidates.Count;
        return candidateCount > 0
            ? $"{candidateCount} runtime target(s)"
            : "No runtime target";
    }

    private void ApplyPreparationStatus(RunWorkflowPublication publication)
    {
        if (publication.Status.State == RunWorkflowState.Error
            && !WorkflowStatusViewStateMapper.IsPreparationAction(publication.Status.ActiveOperation?.Action))
        {
            return;
        }

        var viewState = WorkflowStatusViewStateMapper.MapPreparationStatus(publication);
        PreparationStatusViewState = viewState.PreparationStatus;
        PreparationPhase = viewState.PreparationPhase;
    }

    private string CreateStepMarkerFill(int step)
    {
        if (step == activeStep)
        {
            return "#2F80ED";
        }

        return step < activeStep ? "#5E6B7D" : "#B8C0CC";
    }

    private void SetActiveStep(int step)
    {
        if (activeStep == step)
        {
            return;
        }

        activeStep = step;
        Step1PanelVisibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2PanelVisibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3PanelVisibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4PanelVisibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;

        OnPropertyChanged(nameof(Step1HeaderBackground));
        OnPropertyChanged(nameof(Step2HeaderBackground));
        OnPropertyChanged(nameof(Step3HeaderBackground));
        OnPropertyChanged(nameof(Step4HeaderBackground));
        OnPropertyChanged(nameof(Step1MarkerFill));
        OnPropertyChanged(nameof(Step2MarkerFill));
        OnPropertyChanged(nameof(Step3MarkerFill));
        OnPropertyChanged(nameof(Step4MarkerFill));
        OnPropertyChanged(nameof(Step1HeaderWeight));
        OnPropertyChanged(nameof(Step2HeaderWeight));
        OnPropertyChanged(nameof(Step3HeaderWeight));
        OnPropertyChanged(nameof(Step4HeaderWeight));
        LoadStep1Command.RaiseCanExecuteChanged();
        BrowseProjectSourceCommand.RaiseCanExecuteChanged();
        ClearProjectSourceCommand.RaiseCanExecuteChanged();
        RefreshProfileRegistrationsCommand.RaiseCanExecuteChanged();
        ConfigureStep2Command.RaiseCanExecuteChanged();
        ConfigureProfileCommand.RaiseCanExecuteChanged();
        StartPlcCommand.RaiseCanExecuteChanged();
        StopPlcCommand.RaiseCanExecuteChanged();
        StartUnitTestCommand.RaiseCanExecuteChanged();
        AbortUnitTestCommand.RaiseCanExecuteChanged();
        ResetUnitTestCommand.RaiseCanExecuteChanged();
        ResetEventBufferCommand.RaiseCanExecuteChanged();
        ReconnectRuntimeCommand.RaiseCanExecuteChanged();
        ReturnToStep1Command.RaiseCanExecuteChanged();
        ReturnToStep2Command.RaiseCanExecuteChanged();
        ReturnToStep3Command.RaiseCanExecuteChanged();
        EndSessionCommand.RaiseCanExecuteChanged();
        RaiseConfigurationFileCommandCanExecuteChanged();
    }

    private void ReplaceProjectTargets(RunWorkflowPublication publication)
    {
        var previousId = selectedProjectTarget?.Target.Id.Value;
        var nextItems = (publication.ProjectTargets?.Candidates.Select(ProjectTargetSelectionViewModel.FromTarget) ?? [])
            .ToList();
        var nextSelection = nextItems.FirstOrDefault(item => item.Target.Id.Value == previousId);
        if (nextSelection is null)
        {
            SelectedProjectTarget = null;
        }

        Replace(ProjectTargets, nextItems);
        SelectedProjectTarget = nextSelection;
    }

    private void ReplaceRuntimeTargets(RunWorkflowPublication publication)
    {
        var previousId = selectedRuntimeTarget?.Target?.Id.Value;
        var previouslyCreatedOwnedRuntime = selectedRuntimeTarget?.CreatesOwnedRuntime == true;
        var nextItems = (publication.RuntimeTargets?.Candidates.Select(RuntimeTargetSelectionViewModel.FromTarget) ?? [])
            .ToList();
        if (publication.RuntimeTargets is { State: not TargetDiscoveryState.Failed and not TargetDiscoveryState.NotAvailable })
        {
            nextItems.Add(RuntimeTargetSelectionViewModel.CreateOwned());
        }

        var nextSelection = previouslyCreatedOwnedRuntime
            ? nextItems.FirstOrDefault(item => item.CreatesOwnedRuntime)
            : previousId is null
                ? null
                : nextItems.FirstOrDefault(item => item.Target?.Id.Value == previousId);
        if (nextSelection is null)
        {
            SelectedRuntimeTarget = null;
        }

        Replace(RuntimeTargets, nextItems);
        SelectedRuntimeTarget = nextSelection;
    }

    private void ReportApplicationLogUnavailable(
        string message,
        string? path,
        string? technicalDetail = null)
    {
        diagnosticReporter.ReportApplicationLogUnavailable(message, path, technicalDetail);
    }

    private void ReportArtifactUnavailable(
        string message,
        string? path,
        string? technicalDetail = null)
    {
        diagnosticReporter.ReportArtifactUnavailable(message, path, technicalDetail);
    }

    private void RaiseConfigurationFileCommandCanExecuteChanged()
    {
        ImportConfigurationFileCommand.RaiseCanExecuteChanged();
        LoadUnitTestFileCommand.RaiseCanExecuteChanged();
    }

    private string? GetTestResultArtifactDirectory()
    {
        var path = testResultArtifact?.OutputPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return applicationSettings.Current.ResolvedOutputDirectory;
        }

        return Path.GetDirectoryName(path);
    }

    private void ApplyTestResultArtifactPresentation(TestResultArtifactPresentationState presentation)
    {
        testResultArtifact = presentation.Artifact;
        TestResultArtifactStatus = presentation.Status;
        TestResultArtifactPreview = presentation.Preview;
    }

    private void RefreshDiagnostics(DiagnosticSnapshot workflowDiagnostics)
    {
        var presentation = DiagnosticPresentationMapper.Map(workflowDiagnostics);
        DiagnosticStatusKind = presentation.StatusKind;
        DiagnosticBadgeText = presentation.BadgeText;
        ReplaceDiagnostics(presentation.Diagnostics);
        OnPropertyChanged(nameof(DiagnosticStatusBrush));
        ClearDiagnosticsCommand.RaiseCanExecuteChanged();
    }

    private void ApplyDiagnosticSnapshot(DiagnosticSnapshot snapshot)
    {
        AppendDiagnosticOutputChanges(snapshot);
        RefreshDiagnostics(snapshot);
    }

    private void AppendDiagnosticOutputChanges(DiagnosticSnapshot snapshot)
    {
        foreach (var line in diagnosticOutputTracker.CaptureNewOutputLines(snapshot))
        {
            AppendTerminalLine(line);
        }
    }

    private void ReplaceDiagnostics(IEnumerable<DiagnosticViewModel> diagnostics)
    {
        var previousKey = selectedDiagnostic?.Key;
        Replace(Diagnostics, diagnostics);
        SelectedDiagnostic = !string.IsNullOrWhiteSpace(previousKey)
            ? Diagnostics.FirstOrDefault(diagnostic => diagnostic.Key == previousKey) ?? Diagnostics.LastOrDefault()
            : Diagnostics.LastOrDefault();
        OnPropertyChanged(nameof(CanCopyAllDiagnostics));
    }

    private string? GetAccessProfileLogPath()
    {
        return SelectedAccessProfile is null
            ? null
            : WpfOperationalLogPaths.CreateAccessProfileLogPath(
                applicationSettings.Current.ResolvedLogDirectory,
                SelectedAccessProfile.ProfileId);
    }

    private async Task<bool> ExecuteIfAvailableAsync(
        WorkflowAction action,
        Func<Task<WorkflowActionResult>> execute)
    {
        if (!IsGateAvailable(action))
        {
            logger.LogDebug("Skipping unavailable release action. Action={Action}", action);
            return true;
        }

        var result = await execute();
        return result.Result.IsSuccessful;
    }

    private void ResetTargetSelection(bool clearProfileDraft)
    {
        if (clearProfileDraft)
        {
            loadedAccessProfileConfig = null;
            configuredRunSetup = null;
            importedAccessProfileDraft = null;
            importedTimeoutOverrides = null;
            importedProjectTargetId = null;
            importedRuntimeTargetId = null;
            importedProvisioningMode = null;
            importedConfigurationOperationName = "Import Configuration";
        }

        SelectedProjectTarget = null;
        SelectedRuntimeTarget = null;
        ProjectTargets.Clear();
        RuntimeTargets.Clear();
        ProfileConfiguration.Clear();
    }

    private void ReleaseStep2UiSelection()
    {
        Step2InputsEnabled = false;
        SelectedProjectTarget = null;
        SelectedRuntimeTarget = null;
    }

    private void AppendTerminalLine(string message)
    {
        terminalOutput.AppendLine(message);
        RefreshTerminalText();
    }

    private void AppendPublicationOutput(RunWorkflowPublication publication)
    {
        if (!publicationOutputInitialized)
        {
            publicationOutputInitialized = true;
            lastOutputRunMonitorState = publication.Status.MonitorState;
            lastOutputPlcOperatingState = publication.Status.PlcState.OperatingState;
            lastOutputMonitoringActive = publication.Status.MonitoringActive;
            lastOutputCommunicationRevalidationRequired =
                WorkflowStatusViewStateMapper.IsCommunicationRevalidationRequired(publication.Status);
            return;
        }

        AppendPlcOperatingStateOutput(publication.Status.PlcState.OperatingState);
        AppendMonitoringStatusOutput(publication.Status.MonitoringActive);
        AppendCommunicationRevalidationOutput(publication.Status);
        AppendRunStatusOutput(publication.Status.MonitorState);
    }

    private void AppendPlcOperatingStateOutput(PlcOperatingState operatingState)
    {
        if (lastOutputPlcOperatingState == operatingState)
        {
            return;
        }

        lastOutputPlcOperatingState = operatingState;
        switch (operatingState)
        {
            case PlcOperatingState.Starting:
                AppendTerminalLine("PLC is starting.");
                break;
            case PlcOperatingState.Running:
                AppendTerminalLine("PLC is running.");
                break;
            case PlcOperatingState.Stopping:
                AppendTerminalLine("PLC is stopping.");
                break;
            case PlcOperatingState.Stopped:
                AppendTerminalLine("PLC is stopped.");
                break;
            case PlcOperatingState.Frozen:
                AppendTerminalLine("PLC is frozen.");
                break;
            case PlcOperatingState.Held:
                AppendTerminalLine("PLC is held.");
                break;
        }
    }

    private void AppendMonitoringStatusOutput(bool monitoringActive)
    {
        if (lastOutputMonitoringActive == monitoringActive)
        {
            return;
        }

        lastOutputMonitoringActive = monitoringActive;
        if (monitoringActive)
        {
            AppendTerminalLine("UnitTest event-stream monitoring is active.");
        }
    }

    private void AppendCommunicationRevalidationOutput(WorkflowStatusSnapshot status)
    {
        var revalidationRequired =
            WorkflowStatusViewStateMapper.IsCommunicationRevalidationRequired(status);
        if (lastOutputCommunicationRevalidationRequired == revalidationRequired)
        {
            return;
        }

        var wasRevalidationRequired = lastOutputCommunicationRevalidationRequired == true;
        lastOutputCommunicationRevalidationRequired = revalidationRequired;
        if (revalidationRequired)
        {
            AppendTerminalLine("UnitTest communication is revalidating.");
        }
        else if (wasRevalidationRequired)
        {
            AppendTerminalLine("UnitTest communication recovered.");
        }
    }

    private void AppendRunStatusOutput(TestRunMonitoringState currentRunMonitorState)
    {
        if (lastOutputRunMonitorState == currentRunMonitorState)
        {
            return;
        }

        lastOutputRunMonitorState = currentRunMonitorState;
        switch (currentRunMonitorState)
        {
            case TestRunMonitoringState.RunActive:
                AppendTerminalLine("Test run started.");
                break;
            case TestRunMonitoringState.CompletionPending:
                AppendTerminalLine("Test run finalizing. Waiting for final event evidence.");
                break;
            case TestRunMonitoringState.Completed:
                AppendTerminalLine("Test run completed.");
                break;
            case TestRunMonitoringState.Aborted:
                AppendTerminalLine("Test run aborted.");
                break;
            case TestRunMonitoringState.TimedOut:
                AppendTerminalLine("Test run observation timed out.");
                break;
            case TestRunMonitoringState.Invalidated:
                AppendTerminalLine("Test run evidence invalidated.");
                break;
        }
    }

    private void ApplyTerminalOutput(string? workflowTerminalText)
    {
        terminalOutput.ApplyWorkflowOutput(workflowTerminalText);
        RefreshTerminalText();
    }

    private void ApplyArtifactTerminalOutput(string? artifactTerminalText)
    {
        terminalOutput.ApplyArtifactOutput(artifactTerminalText);
        RefreshTerminalText();
    }

    private void RefreshTerminalText()
    {
        TerminalText = terminalOutput.Text;
        ClearOutputCommand.RaiseCanExecuteChanged();
    }

    private void ClearArtifactExportState()
    {
        testResultArtifact = null;
        terminalOutput.ClearArtifactOutput();
        RefreshTerminalText();
        OpenTestResultArtifactCommand.RaiseCanExecuteChanged();
        OpenOutputFolderCommand.RaiseCanExecuteChanged();
    }

    private void RefreshLocalConfigurationStatus()
    {
        PlcProcessStatusViewState = WorkflowStatusViewStateMapper.NotReadyProcessStatus;
        CommunicationStatusViewState = WorkflowStatusViewStateMapper.DisconnectedProcessStatus;
        MonitoringStatusViewState = WorkflowStatusViewStateMapper.OffProcessStatus;
        RunControlStatusViewState = WorkflowStatusViewStateMapper.NotReadyProcessStatus;
        RunMonitorState = TestRunMonitoringState.NotMonitoring;
        RunStatus = "idle";
        OnPropertyChanged(nameof(PlcProcessStatusBrush));
        OnPropertyChanged(nameof(CommunicationStatusBrush));
        OnPropertyChanged(nameof(MonitoringStatusBrush));
        OnPropertyChanged(nameof(RunControlStatusBrush));
    }

    private WorkflowActionPolicy CreatePolicy(WorkflowAction action)
    {
        var run = applicationSettings.Current.RunTimeouts;
        var application = applicationSettings.Current.OperationTimeouts;
        var timeout = action switch
        {
            WorkflowAction.LoadAccessProfile => application.ProfileLoad,
            WorkflowAction.UnloadAccessProfile => application.ProfileUnload,
            WorkflowAction.DiscoverProjectTargets => application.ProjectDiscovery,
            WorkflowAction.DiscoverRuntimeTargets => application.RuntimeDiscovery,
            WorkflowAction.PrepareProject => run.ProjectPrepare,
            WorkflowAction.PrepareRuntime => run.RuntimePrepare,
            WorkflowAction.ReconnectRuntime => run.RuntimePrepare,
            WorkflowAction.DiscoverSafetyProgramAuthorizationRequirement
                or WorkflowAction.AuthorizeSafetyProgram => run.BuildTransfer,
            WorkflowAction.TransferProject => run.BuildTransfer,
            WorkflowAction.StartPlc or WorkflowAction.StopPlc => run.PlcCommand,
            WorkflowAction.PrepareUnitTestCommunication => run.CommunicationPrepare,
            WorkflowAction.StartMonitoring or WorkflowAction.StopMonitoring => application.Monitoring,
            WorkflowAction.StartUnitTest or WorkflowAction.AbortUnitTest or WorkflowAction.ResetUnitTest
                or WorkflowAction.ResetEventBuffer =>
                run.UnitTestCommand,
            WorkflowAction.ReleaseUnitTestCommunication => application.Monitoring,
            WorkflowAction.ReleaseRuntime => application.RuntimeRelease,
            WorkflowAction.ReleaseProject => application.ProjectRelease,
            WorkflowAction.Cleanup => application.Cleanup,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "No timeout policy is defined.")
        };
        return new WorkflowActionPolicy(timeout);
    }

    private Task ExecuteCancellableOperationAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        Action onCanceled,
        CancellationToken cancellationToken)
    {
        return cancellableOperation.RunAsync(
            operationName,
            operation,
            () =>
            {
                onCanceled();
                AppendTerminalLine($"{operationName.TrimEnd('.')} canceled.");
                logger.LogInformation(
                    "WPF operation cancellation completed. Operation={Operation}",
                    operationName);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    private Task CancelCurrentOperationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestActiveOperationCancellation();
        return Task.CompletedTask;
    }

    private async Task ExecuteContextReleaseAsync(Func<Task> release)
    {
        if (!await BeginContextReleaseAsync())
        {
            return;
        }

        try
        {
            await release();
        }
        finally
        {
            EndContextRelease();
        }
    }

    private async Task<bool> BeginContextReleaseAsync()
    {
        if (contextReleaseInProgress)
        {
            return false;
        }

        contextReleaseInProgress = true;
        RefreshActivityPresentation();
        RefreshCommandStates();
        OnPropertyChanged(nameof(CancelCurrentOperationVisibility));

        RequestActiveOperationCancellation();
        var timeout = applicationSettings.Current.OperationTimeouts.Cleanup;
        try
        {
            await cancellableOperation.CancelAndWaitAsync().WaitAsync(timeout, timeProvider);
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "Active WPF operation did not stop before context release continued. Timeout={Timeout}, Operation={Operation}",
                timeout,
                cancellableOperation.OperationName);
            diagnosticReporter.ReportCleanupFailure(
                "Wpf.Cleanup.ActiveOperation.TimedOut",
                $"The active WPF operation did not stop within the cleanup timeout of {timeout}.",
                "Context release continued with lower-layer bounded cleanup.");
        }

        var workflowOperationRelease = await sessionRelease.BeginScopedReleaseAsync(
            timeout,
            CancellationToken.None);
        if (!workflowOperationRelease.IsSuccessful)
        {
            logger.LogWarning(
                "Active workflow operation did not stop before scoped context release. ResultState={ResultState}, Message={Message}",
                workflowOperationRelease.State,
                workflowOperationRelease.Message);
            diagnosticReporter.ReportCleanupFailure(
                "Wpf.Cleanup.ActiveWorkflowOperation.Failed",
                "The active workflow operation did not stop; the scoped context release was not started.",
                workflowOperationRelease.Message);
            EndContextRelease();
            return false;
        }

        return true;
    }

    private void EndContextRelease()
    {
        sessionRelease.EndScopedRelease();
        contextReleaseInProgress = false;
        RefreshActivityPresentation();
        RefreshCommandStates();
        OnPropertyChanged(nameof(CancelCurrentOperationVisibility));
    }

    private bool IsInteractionBlocked()
    {
        return contextReleaseInProgress || cancellableOperation.IsActive;
    }

    private void RequestActiveOperationCancellation()
    {
        var operationName = cancellableOperation.OperationName;
        if (!cancellableOperation.RequestCancellation())
        {
            return;
        }

        AppendTerminalLine($"Cancel requested for {operationName}");
        logger.LogInformation(
            "WPF operation cancellation requested. Operation={Operation}",
            operationName);
    }

    private void OnCancellableOperationStateChanged(object? sender, EventArgs args)
    {
        if (isDisposed)
        {
            return;
        }

        RefreshActivityPresentation();
        RefreshCommandStates();
        Step1GateStatus = CreateStep1GateStatus();
        OnPropertyChanged(nameof(Step3GateStatus));
        OnPropertyChanged(nameof(CancelCurrentOperationVisibility));
    }

    private void RefreshActivityPresentation()
    {
        if (cancellableOperation.IsCancellationRequested)
        {
            ActiveOperation = "Cancelling operation...";
            ActivityIndicatorActive = true;
            return;
        }

        if (contextReleaseInProgress && workflowActivityIndicatorActive)
        {
            ActiveOperation = workflowActiveOperation;
            ActivityIndicatorActive = true;
            return;
        }

        if (contextReleaseInProgress)
        {
            ActiveOperation = "Releasing session resources...";
            ActivityIndicatorActive = true;
            return;
        }

        if (cancellableOperation.IsActive)
        {
            ActiveOperation = workflowActivityIndicatorActive
                ? workflowActiveOperation
                : cancellableOperation.OperationName ?? "Working...";
            ActivityIndicatorActive = true;
            return;
        }

        ActiveOperation = workflowActiveOperation;
        ActivityIndicatorActive = workflowActivityIndicatorActive;
    }

    private static async Task YieldUiAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || !dispatcher.CheckAccess())
        {
            await Task.Yield();
            return;
        }

        await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private static void Replace<T>(
        ObservableCollection<T> target,
        IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
