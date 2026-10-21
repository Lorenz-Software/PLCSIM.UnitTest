using System.IO;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensPlcsimUnitTestLifecycleService : SiemensAccessProfileServiceBase, IUnitTestLifecycleService
{
    private static readonly TimeSpan DefaultRunControlCommandTimeout = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly ILogger<SiemensPlcsimUnitTestLifecycleService> logger;
    private readonly SiemensPlcsimCommunicationSessionOwner sessionOwner;
    private IPlcsimUnitTestCommunicationSession? session;

    public SiemensPlcsimUnitTestLifecycleService(
        TimeProvider timeProvider,
        ILogger<SiemensPlcsimUnitTestLifecycleService>? logger = null)
        : this(timeProvider, logger, new SiemensPlcsimCommunicationSessionOwner())
    {
    }

    internal SiemensPlcsimUnitTestLifecycleService(
        TimeProvider timeProvider,
        ILogger<SiemensPlcsimUnitTestLifecycleService>? logger,
        SiemensPlcsimCommunicationSessionOwner sessionOwner)
        : base(timeProvider)
    {
        this.logger = logger ?? NullLogger<SiemensPlcsimUnitTestLifecycleService>.Instance;
        this.sessionOwner = sessionOwner ?? throw new ArgumentNullException(nameof(sessionOwner));
    }

    public event EventHandler<UnitTestRunControlObservationChangedEventArgs>? RunControlObservationChanged;

    public UnitTestRunControlObservation CurrentObservation { get; private set; } = CreateSafeObservation();

    public void BindCommunicationSession(IPlcsimUnitTestCommunicationSession communicationSession)
    {
        ArgumentNullException.ThrowIfNull(communicationSession);
        operationLock.Wait();
        try
        {
            if (session is not null && !ReferenceEquals(session, communicationSession))
            {
                throw new InvalidOperationException(
                    "A different Siemens PLCSIM UnitTest communication session is already bound to the lifecycle adapter.");
            }

            if (ReferenceEquals(session, communicationSession))
            {
                return;
            }

            sessionOwner.Attach(SiemensPlcsimCommunicationAdapterKind.Lifecycle, communicationSession);
            session = communicationSession;
            session.HealthChanged += OnSessionHealthChanged;
            session.RunControlObservationChanged += OnRunControlObservationChanged;
            SetHealth(MapHealth(session.Health, "Siemens PLCSIM UnitTest communication session bound."));
            logger.LogInformation(
                "Siemens PLCSIM UnitTest lifecycle communication session bound. RuntimeSessionId={RuntimeSessionId}, CommunicationSessionId={CommunicationSessionId}, RunControlRoot={RunControlRoot}",
                communicationSession.Communication.RuntimeSessionId,
                communicationSession.Communication.SessionId,
                communicationSession.Communication.RunControlRoot);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<OperationResult<TestRunCommandResult>> StartAsync(
        UnitTestContext context,
        CancellationToken cancellationToken)
    {
        return await RequestCommandAsync(UnitTestRunCommand.Start, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<TestRunCommandResult>> AbortAsync(
        UnitTestContext context,
        CancellationToken cancellationToken)
    {
        return await RequestCommandAsync(UnitTestRunCommand.Abort, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<TestRunCommandResult>> ResetAsync(
        UnitTestContext context,
        CancellationToken cancellationToken)
    {
        return await RequestCommandAsync(UnitTestRunCommand.Reset, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> ReleaseCommunicationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Siemens PLCSIM UnitTest lifecycle communication release started.");
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var close = await DetachSessionAsync(cancellationToken).ConfigureAwait(false);
            CurrentObservation = new UnitTestRunControlObservation(
                UnitTestExecutionStatus.Unknown,
                UnitTestExecutionSignals.None,
                UnitTestLifecycleState.Unknown,
                UnitTestResultState.NotEvaluated);
            if (close.IsSuccessful)
            {
                SetHealth(AccessHealth.NotEstablished(TimeProvider.GetUtcNow()));
            }
            LogOperationCompleted(
                "ReleaseCommunication",
                close.State,
                "Siemens PLCSIM UnitTest lifecycle communication release completed.");
            return close.IsSuccessful
                ? OperationResult.Success("Siemens PLCSIM UnitTest lifecycle communication released.")
                : close;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Siemens PLCSIM UnitTest lifecycle service cleanup started.");
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var close = await DetachSessionAsync(cancellationToken).ConfigureAwait(false);
            CurrentObservation = new UnitTestRunControlObservation(
                UnitTestExecutionStatus.Unknown,
                UnitTestExecutionSignals.None,
                UnitTestLifecycleState.Unknown,
                UnitTestResultState.NotEvaluated);
            SetDisposed("Siemens PLCSIM UnitTest lifecycle service disposed.");
            LogOperationCompleted(
                "Cleanup",
                close.State,
                "Siemens PLCSIM UnitTest lifecycle service cleanup completed.");
            return close.IsSuccessful
                ? OperationResult.Success("Siemens PLCSIM UnitTest lifecycle service cleaned up.")
                : close;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<OperationResult> ValidateAndObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogDebug("Siemens PLCSIM RunControl validation and observation started.");
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var activeSession = session;
            if (activeSession is null || !sessionOwner.IsUsable(activeSession))
            {
                logger.LogWarning("Siemens PLCSIM RunControl validation and observation failed because UnitTest communication is not usable.");
                return OperationResult.Failure("Siemens PLCSIM UnitTest communication is not usable.");
            }

            var validation = await activeSession.ValidateRunControlAsync(
                new PlcsimRunControlValidationRequest(
                    activeSession.Communication.SessionId,
                    UnitTestContractVersions.PlcsimAdvancedCommunication,
                    CorrelationId: null),
                cancellationToken).ConfigureAwait(false);
            if (!sessionOwner.IsUsable(activeSession))
            {
                return OperationResult.Failure("Siemens PLCSIM UnitTest communication became terminal during RunControl validation.");
            }

            PublishDiagnostics(validation.Diagnostics, DiagnosticPhase.CommunicationValidation);
            if (!validation.Succeeded || !validation.ContractValid)
            {
                LogPlcsimDiagnostics("RunControlValidation", validation.Status, validation.Diagnostics);
                ApplyOperationHealth(
                    validation.Status,
                    "Siemens.PlcsimAdvanced.RunControl.ValidationFailed",
                    $"Siemens PLCSIM RunControl validation returned {validation.Status}.");
                LogOperationCompleted(
                    "RunControlValidation",
                    validation.Status,
                    $"Siemens PLCSIM RunControl validation completed. ContractValid={validation.ContractValid}");
                return SiemensPlcsimOperationMapping.ToOperationResult(validation.Status, $"Siemens PLCSIM RunControl validation returned {validation.Status}.");
            }

            var observation = await activeSession.ObserveRunControlAsync(
                new PlcsimRunControlObservationRequest(activeSession.Communication.SessionId, CorrelationId: null),
                cancellationToken).ConfigureAwait(false);
            if (!sessionOwner.IsUsable(activeSession))
            {
                return OperationResult.Failure("Siemens PLCSIM UnitTest communication became terminal during RunControl observation.");
            }

            PublishDiagnostics(observation.Diagnostics, DiagnosticPhase.CommunicationValidation);
            if (observation.Succeeded && observation.Observation is not null)
            {
                ApplyObservation(observation.Observation);
            }
            else if (observation.Succeeded)
            {
                const string message = "Siemens PLCSIM RunControl observation succeeded without an observation snapshot.";
                logger.LogError(message);
                ReportDiagnostic(CreateObservationContractDiagnostic(message));
                SetHealth(new AccessHealth(
                    AccessHealthState.Invalidated,
                    AccessHealthProblem.InternalFailure,
                    "Siemens.PlcsimAdvanced.RunControl.ObservationMissing",
                    message,
                    TimeProvider.GetUtcNow()));
                return OperationResult.Failure(message);
            }
            else
            {
                LogPlcsimDiagnostics("RunControlObservation", observation.Status, observation.Diagnostics);
                ApplyOperationHealth(
                    observation.Status,
                    "Siemens.PlcsimAdvanced.RunControl.ObservationFailed",
                    $"Siemens PLCSIM RunControl observation returned {observation.Status}.");
            }

            LogOperationCompleted(
                "RunControlObservation",
                observation.Status,
                $"Siemens PLCSIM RunControl observation completed. LifecycleState={CurrentObservation.State}, ExecutionStatus={CurrentObservation.ExecutionStatus}, Ready={CurrentObservation.Ready}, Running={CurrentObservation.Running}, Completed={CurrentObservation.Completed}, Error={CurrentObservation.Error}");
            return SiemensPlcsimOperationMapping.ToOperationResult(observation.Status, $"Siemens PLCSIM RunControl observation returned {observation.Status}.");
        }
        finally
        {
            operationLock.Release();
        }
    }

    protected override AccessServiceKind GetServiceKind() => AccessServiceKind.UnitTestLifecycle;

    private async Task<OperationResult<TestRunCommandResult>> RequestCommandAsync(
        UnitTestRunCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation(
            "Siemens PLCSIM RunControl command write started. Command={Command}",
            command);
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var activeSession = session;
            if (activeSession is null || !sessionOwner.IsUsable(activeSession))
            {
                logger.LogWarning(
                    "Siemens PLCSIM RunControl command write failed because UnitTest communication is not usable. Command={Command}",
                    command);
                return OperationResult<TestRunCommandResult>.Failure("Siemens PLCSIM UnitTest communication is not usable.");
            }

            var result = await activeSession.RequestRunControlCommandAsync(
                new PlcsimRunControlCommandRequest(
                    activeSession.Communication.SessionId,
                    command,
                    DefaultRunControlCommandTimeout,
                    CorrelationId: null),
                cancellationToken).ConfigureAwait(false);
            if (!sessionOwner.IsUsable(activeSession))
            {
                return OperationResult<TestRunCommandResult>.Failure(
                    $"Siemens PLCSIM UnitTest communication became terminal while writing RunControl command {command}.");
            }

            PublishDiagnostics(result.Diagnostics, DiagnosticPhase.RunStart);
            if (!result.Succeeded || !result.Accepted)
            {
                LogPlcsimDiagnostics("RunControlCommand", result.Status, result.Diagnostics);
                ApplyOperationHealth(
                    result.Status,
                    "Siemens.PlcsimAdvanced.RunControl.CommandFailed",
                    $"Siemens PLCSIM RunControl command {command} returned {result.Status}.");
            }

            var commandResult = new TestRunCommandResult(
                command,
                result.Accepted);
            LogOperationCompleted(
                "RunControlCommand",
                result.Status,
                $"Siemens PLCSIM RunControl command write completed. Command={command}, Accepted={result.Accepted}, DiagnosticCount={result.Diagnostics.Count}");
            return SiemensPlcsimOperationMapping.ToOperationResult(
                result.Status,
                commandResult,
                $"Siemens PLCSIM RunControl command {command} returned {result.Status}.");
        }
        finally
        {
            operationLock.Release();
        }
    }

    private UnitTestRunControlObservation ApplyObservation(PlcsimRunControlObservation observation)
    {
        return ApplyCurrentObservation(MapObservation(observation));
    }

    private UnitTestRunControlObservation ApplyCurrentObservation(UnitTestRunControlObservation observation)
    {
        if (CurrentObservation != observation)
        {
            CurrentObservation = observation;
            RunControlObservationChanged?.Invoke(this, new UnitTestRunControlObservationChangedEventArgs(observation));
        }

        return observation;
    }

    private void OnRunControlObservationChanged(object? sender, PlcsimRunControlObservationChangedEventArgs args)
    {
        if (sender is not IPlcsimUnitTestCommunicationSession communicationSession
            || !ReferenceEquals(communicationSession, session)
            || !sessionOwner.IsUsable(communicationSession))
        {
            return;
        }

        ApplyObservation(args.Observation);
    }

    private static UnitTestRunControlObservation MapObservation(PlcsimRunControlObservation observation)
    {
        return new UnitTestRunControlObservation(
            observation.ExecutionStatus,
            observation.ExecutionSignals,
            observation.State,
            observation.Result);
    }

    private async Task<OperationResult> DetachSessionAsync(CancellationToken cancellationToken)
    {
        var detachedSession = session;
        if (detachedSession is null)
        {
            return OperationResult.Success();
        }

        detachedSession.HealthChanged -= OnSessionHealthChanged;
        detachedSession.RunControlObservationChanged -= OnRunControlObservationChanged;
        if (ReferenceEquals(session, detachedSession))
        {
            session = null;
        }

        var close = await sessionOwner.DetachAsync(
            SiemensPlcsimCommunicationAdapterKind.Lifecycle,
            detachedSession,
            cancellationToken).ConfigureAwait(false);
        if (close.IsUnsuccessful)
        {
            var message = close.Message ?? "Siemens PLCSIM UnitTest communication session close failed.";
            ReportDiagnostic(CreateCommunicationCleanupDiagnostic(message));
            SetHealth(new AccessHealth(
                AccessHealthState.Invalidated,
                AccessHealthProblem.ContextLost,
                "Siemens.PlcsimAdvanced.Communication.CloseFailed",
                "Siemens PLCSIM UnitTest communication session close failed.",
                TimeProvider.GetUtcNow()));
        }

        return close;
    }

    private void OnSessionHealthChanged(object? sender, PlcsimCommunicationHealthChangedEventArgs args)
    {
        if (sender is not IPlcsimUnitTestCommunicationSession communicationSession
            || !ReferenceEquals(communicationSession, session))
        {
            return;
        }

        if (IsTerminal(args.Current))
        {
            sessionOwner.MarkTerminal(communicationSession);
            ApplyCurrentObservation(CreateSafeObservation());
        }
        else if (!sessionOwner.IsUsable(communicationSession))
        {
            return;
        }

        SetHealth(MapHealth(args.Current, args.Message));
    }

    private static bool IsTerminal(PlcsimCommunicationHealthState state)
    {
        return state is PlcsimCommunicationHealthState.Invalidated
            or PlcsimCommunicationHealthState.Faulted
            or PlcsimCommunicationHealthState.Disposed;
    }

    private static UnitTestRunControlObservation CreateSafeObservation()
    {
        return new UnitTestRunControlObservation(
            UnitTestExecutionStatus.Unknown,
            UnitTestExecutionSignals.None,
            UnitTestLifecycleState.Unknown,
            UnitTestResultState.NotEvaluated);
    }

    private AccessHealth MapHealth(
        PlcsimCommunicationHealthState state,
        string message)
    {
        var now = TimeProvider.GetUtcNow();
        return state switch
        {
            PlcsimCommunicationHealthState.Healthy => AccessHealth.Healthy(now),
            PlcsimCommunicationHealthState.Degraded => new AccessHealth(
                AccessHealthState.RevalidationRequired,
                AccessHealthProblem.RevalidationRequired,
                "Siemens.Plcsim.Communication.Degraded",
                message,
                now),
            PlcsimCommunicationHealthState.RevalidationRequired => new AccessHealth(
                AccessHealthState.RevalidationRequired,
                AccessHealthProblem.RevalidationRequired,
                "Siemens.Plcsim.Communication.RevalidationRequired",
                message,
                now),
            PlcsimCommunicationHealthState.Invalidated => new AccessHealth(
                AccessHealthState.Invalidated,
                AccessHealthProblem.ContextLost,
                "Siemens.Plcsim.Communication.Invalidated",
                message,
                now),
            PlcsimCommunicationHealthState.Faulted => new AccessHealth(
                AccessHealthState.Invalidated,
                AccessHealthProblem.InternalFailure,
                "Siemens.Plcsim.Communication.Faulted",
                message,
                now),
            PlcsimCommunicationHealthState.Disposed => AccessHealth.Disposed(now),
            _ => AccessHealth.NotEstablished(now)
        };
    }

    private void ApplyOperationHealth(
        PlcsimOperationStatus status,
        string code,
        string message)
    {
        if (status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings)
        {
            return;
        }

        var now = TimeProvider.GetUtcNow();
        var health = status switch
        {
            PlcsimOperationStatus.Unavailable => new AccessHealth(
                AccessHealthState.Invalidated,
                AccessHealthProblem.ContextLost,
                code,
                message,
                now),
            PlcsimOperationStatus.Failed
                or PlcsimOperationStatus.TimedOut
                or PlcsimOperationStatus.Unclear => new AccessHealth(
                    AccessHealthState.RevalidationRequired,
                    AccessHealthProblem.RevalidationRequired,
                    code,
                    message,
                    now),
            _ => Health
        };

        SetHealth(health);
    }

    private void LogOperationCompleted(
        string operation,
        OperationResultState state,
        string message)
    {
        logger.Log(
            SiemensPlcsimOperationMapping.ToLogLevel(state),
            "{Message} Operation={Operation}, State={State}",
            message,
            operation,
            state);
    }

    private void LogOperationCompleted(
        string operation,
        PlcsimOperationStatus status,
        string message)
    {
        logger.Log(
            SiemensPlcsimOperationMapping.ToLogLevel(status),
            "{Message} Operation={Operation}, Status={Status}",
            message,
            operation,
            status);
    }

    private void LogPlcsimDiagnostics(
        string operation,
        PlcsimOperationStatus status,
        IReadOnlyList<PlcsimDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            logger.Log(
                SiemensPlcsimOperationMapping.ToLogLevel(diagnostic.Severity),
                "Siemens PLCSIM UnitTest lifecycle communication diagnostic. Operation={Operation}, Status={Status}, Code={Code}, Severity={Severity}, EvidenceId={EvidenceId}, Message={DiagnosticMessage}",
                operation,
                status,
                diagnostic.Code,
                diagnostic.Severity,
                diagnostic.EvidenceId,
                diagnostic.Message);
        }
    }

    private IReadOnlyList<Diagnostic> MapDiagnostics(
        IReadOnlyList<PlcsimDiagnostic> diagnostics,
        DiagnosticPhase phase)
    {
        return diagnostics.Select(diagnostic => MapDiagnostic(diagnostic, phase)).ToArray();
    }

    private void PublishDiagnostics(
        IReadOnlyList<PlcsimDiagnostic> diagnostics,
        DiagnosticPhase phase)
    {
        foreach (var diagnostic in MapDiagnostics(diagnostics, phase))
        {
            ReportDiagnostic(diagnostic);
        }
    }

    private Diagnostic MapDiagnostic(
        PlcsimDiagnostic diagnostic,
        DiagnosticPhase phase)
    {
        return SiemensPlcsimOperationMapping.ToDiagnostic(
            diagnostic,
            phase,
            TimeProvider.GetUtcNow());
    }

    private Diagnostic CreateCommunicationCleanupDiagnostic(string message)
    {
        return new Diagnostic(
            "Siemens.PlcsimAdvanced.CommunicationCleanupFailed",
            DiagnosticCategory.Cleanup,
            Microsoft.Extensions.Logging.LogLevel.Error,
            DiagnosticSource.AccessProfile,
            DiagnosticPhase.Cleanup,
            message,
            new DiagnosticCorrelation(ServiceId: "Siemens.PlcsimAdvanced"),
            TimeProvider.GetUtcNow());
    }

    private Diagnostic CreateObservationContractDiagnostic(string message)
    {
        return new Diagnostic(
            "Siemens.PlcsimAdvanced.RunControl.ObservationMissing",
            DiagnosticCategory.Internal,
            Microsoft.Extensions.Logging.LogLevel.Error,
            DiagnosticSource.AccessProfile,
            DiagnosticPhase.CommunicationValidation,
            message,
            new DiagnosticCorrelation(ServiceId: "Siemens.PlcsimAdvanced"),
            TimeProvider.GetUtcNow());
    }

}
