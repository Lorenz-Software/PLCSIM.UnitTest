using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;

namespace PLCSIM.UnitTest.AccessProfiles.Fake;

/// <summary>
/// Loaded fake access profile instance that exposes deterministic fake functional services.
/// </summary>
public sealed class FakeAccessProfile : IAccessProfileSession
{
    private readonly TimeProvider timeProvider;
    private AccessProfileState state = AccessProfileState.Created;
    private AccessHealth health;

    public FakeAccessProfile(
        AccessProfileInfo info,
        FakeAccessProfileServices services,
        TimeProvider timeProvider,
        string projectSource = "")
    {
        Info = info;
        Services = services;
        Configuration = new FakeProfileConfigurationService(
            Info,
            projectSource,
            initialLostEventCount =>
            {
                if (Services.UnitTestEventStream is FakeUnitTestEventStreamService fakeEventStream)
                {
                    fakeEventStream.InitialLostEventCount = initialLostEventCount;
                }
            },
            safetyAuthorizationRequirement =>
            {
                if (Services.Project is FakePlcProjectService fakeProject)
                {
                    fakeProject.SafetyAuthorizationRequirementResult = safetyAuthorizationRequirement;
                    fakeProject.SafetyAuthorizationHandler = (password, _) => Task.FromResult(
                        OperationResult<SafetyProgramAuthorizationResult>.Success(
                            new SafetyProgramAuthorizationResult(password.Length > 0)));
                }
            });
        this.timeProvider = timeProvider;
        health = AccessHealth.NotEstablished(timeProvider.GetUtcNow());
        WireServiceHealth(Services.Project);
        WireServiceHealth(Services.PlcControl);
        WireServiceHealth(Services.UnitTestLifecycle);
        WireServiceHealth(Services.UnitTestEventStream);
    }

    public AccessProfileInfo Info { get; }

    public FakeAccessProfileServices Services { get; }

    public IAccessProfileConfigurationService Configuration { get; }

    public IPlcProjectService Project => Services.Project;

    public IPlcControlService PlcControl => Services.PlcControl;

    public IUnitTestLifecycleService UnitTestLifecycle => Services.UnitTestLifecycle;

    public IUnitTestEventStreamService UnitTestEventStream => Services.UnitTestEventStream;

    public event EventHandler<DiagnosticEventArgs>? DiagnosticEventHandler;

    public event EventHandler<ProfileStateChangedEventArgs>? StateChangedEventHandler;

    public event EventHandler<AccessHealthChangedEventArgs>? HealthChangedEventHandler;

    public AccessHealth Health => health;

    public CommunicationPrepareRequest? LastPrepareUnitTestCommunicationRequest { get; private set; }

    public PlcRuntimeReconnectRequest? LastReconnectRuntimeRequest { get; private set; }

    public async Task<OperationResult<PreparedPlcRuntime>> ReconnectRuntimeAsync(
        PlcRuntimeReconnectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastReconnectRuntimeRequest = request;

        var eventStreamRelease = await UnitTestEventStream.ReleaseCommunicationAsync(cancellationToken);
        var lifecycleRelease = await UnitTestLifecycle.ReleaseCommunicationAsync(cancellationToken);
        if (!eventStreamRelease.IsSuccessful || !lifecycleRelease.IsSuccessful)
        {
            var message = eventStreamRelease.Message
                ?? lifecycleRelease.Message
                ?? "Fake runtime reconnect could not release UnitTest communication.";
            ReportDiagnostic(FakeDiagnostics.OperationError(
                "Fake.Runtime.Reconnect.CommunicationReleaseFailed",
                DiagnosticCategory.Cleanup,
                DiagnosticPhase.RuntimeProvisioning,
                message,
                timeProvider.GetUtcNow()));
            return OperationResult<PreparedPlcRuntime>.Failure(message);
        }

        if (Services.ReconnectRuntimeResult is { } configuredResult)
        {
            return configuredResult;
        }

        if (PlcControl is FakePlcControlService fakePlcControl)
        {
            fakePlcControl.RepublishCurrentState();
        }

        SetState(AccessProfileState.Ready, "Fake runtime reconnected.");
        return OperationResult<PreparedPlcRuntime>.Success(
            new PreparedPlcRuntime(request.Runtime, request.RuntimeTarget),
            "Fake runtime reconnected.");
    }

    public async Task<OperationResult<CommunicationSession>> PrepareUnitTestCommunicationAsync(
        CommunicationPrepareRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastPrepareUnitTestCommunicationRequest = request;

        if (Services.HoldNextPrepareUnitTestCommunication)
        {
            return ApplyPrepareUnitTestCommunicationResult(
                await Services.HoldPrepareUnitTestCommunicationAsync(cancellationToken));
        }

        if (Services.PrepareUnitTestCommunicationResult is { } configuredResult)
        {
            return ApplyPrepareUnitTestCommunicationResult(configuredResult);
        }

        var session = new CommunicationSession(
            new CommunicationSessionId($"fake-session-{Guid.NewGuid():N}"),
            timeProvider.GetUtcNow());

        return ApplyPrepareUnitTestCommunicationResult(OperationResult<CommunicationSession>.Success(session));
    }

    public ValueTask DisposeAsync()
    {
        SetState(AccessProfileState.Disposed, "Fake access profile disposed.");
        return ValueTask.CompletedTask;
    }

    public void ReportDiagnostic(Diagnostic diagnostic)
    {
        DiagnosticEventHandler?.Invoke(this, new DiagnosticEventArgs(diagnostic));
    }

    public void SimulateProfileInvalidated()
    {
        SetState(AccessProfileState.Invalidated, "Fake access profile was invalidated.");
    }

    private OperationResult<CommunicationSession> ApplyPrepareUnitTestCommunicationResult(
        OperationResult<CommunicationSession> result)
    {
        if (!result.IsSuccessful)
        {
            ReportDiagnostic(FakeDiagnostics.OperationError(
                "Fake.Communication.Prepare.Failed",
                DiagnosticCategory.Workflow,
                DiagnosticPhase.CommunicationValidation,
                result.Message ?? "Fake UnitTest communication preparation failed.",
                timeProvider.GetUtcNow()));
            return result;
        }

        SetState(AccessProfileState.Ready, "Fake UnitTest communication prepared.");
        return result;
    }

    private void SetState(AccessProfileState newState, string reason)
    {
        if (state == newState)
        {
            return;
        }

        state = newState;
        StateChangedEventHandler?.Invoke(this, new ProfileStateChangedEventArgs(newState, reason));
        SetHealth(MapStateToHealth(newState, reason));
    }

    private void SetHealth(AccessHealth newHealth)
    {
        if (health == newHealth)
        {
            return;
        }

        health = newHealth;
        HealthChangedEventHandler?.Invoke(this, new AccessHealthChangedEventArgs([
            new AccessServiceHealthUpdate(AccessServiceKind.Profile, newHealth)
        ]));
    }

    private void WireServiceHealth(FakeAccessProfileServiceBase service)
    {
        service.HealthChangedEventHandler += (_, args) => HealthChangedEventHandler?.Invoke(this, args);
    }

    private void WireServiceHealth(IAccessProfileService service)
    {
        if (service is FakeAccessProfileServiceBase fakeService)
        {
            WireServiceHealth(fakeService);
        }
    }

    private AccessHealth MapStateToHealth(AccessProfileState profileState, string reason)
    {
        var now = timeProvider.GetUtcNow();
        return profileState switch
        {
            AccessProfileState.Ready => AccessHealth.Healthy(now),
            AccessProfileState.Invalidated => new AccessHealth(
                AccessHealthState.Invalidated,
                AccessHealthProblem.ContextLost,
                "Fake.Profile.Invalidated",
                reason,
                now),
            AccessProfileState.Disposed => AccessHealth.Disposed(now),
            _ => AccessHealth.NotEstablished(now)
        };
    }
}
