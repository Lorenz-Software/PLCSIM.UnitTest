using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V21;

public sealed class TiaOpennessV21EngineeringOperationTests
{
    [Fact]
    public async Task V21_engineering_session_reports_safety_password_requirement()
    {
        var handle = new RecordingV21EngineeringSessionHandle(
            "tia-session-1",
            safetyRequirementResult: new TiaOpennessV21SafetyAuthorizationRequirementHostResult(
                TiaOperationStatus.Succeeded,
                TiaSafetyProgramAuthorizationRequirement.PasswordRequired,
                Array.Empty<TiaDiagnostic>(),
                Array.Empty<TiaEvidence>()));
        var session = new TiaOpennessV21EngineeringSession(handle);
        var target = TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef();

        var result = await session.GetSafetyProgramAuthorizationRequirementAsync(
            target,
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Succeeded, result.Status);
        Assert.Equal(TiaSafetyProgramAuthorizationRequirement.PasswordRequired, result.Requirement);
        Assert.Equal(1, handle.SafetyRequirementCount);
        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
    }

    [Fact]
    public async Task V21_engineering_session_maps_successful_safety_authorization()
    {
        var handle = new RecordingV21EngineeringSessionHandle("tia-session-1");
        var session = new TiaOpennessV21EngineeringSession(handle);
        var target = TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef();
        using var password = CreateSecureString("safety-password");

        var result = await session.AuthorizeSafetyProgramAsync(
            target,
            password,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Authorized);
        Assert.Equal(1, handle.SafetyAuthorizationCount);
        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
    }

    [Fact]
    public async Task V21_engineering_session_keeps_health_after_rejected_safety_authorization()
    {
        var handle = new RecordingV21EngineeringSessionHandle(
            "tia-session-1",
            safetyAuthorizationResult: new TiaOpennessV21SafetyAuthorizationHostResult(
                TiaOperationStatus.Failed,
                false,
                new[]
                {
                    new TiaDiagnostic(
                        "TiaOpenness.V21.Worker.SafetyAuthorizationRejected",
                        TiaDiagnosticSeverity.Error,
                        "TIA Openness rejected the offline Safety-program authorization.",
                        "AuthorizeSafetyProgram",
                        "tia-openness-v21-safety-authorization")
                },
                Array.Empty<TiaEvidence>()));
        var session = new TiaOpennessV21EngineeringSession(handle);
        var target = TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef();
        using var password = CreateSecureString("wrong-password");

        var authorization = await session.AuthorizeSafetyProgramAsync(
            target,
            password,
            CancellationToken.None);
        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(null),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Failed, authorization.Status);
        Assert.False(authorization.Authorized);
        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
        Assert.True(discovery.Succeeded);
    }

    [Fact]
    public async Task V21_engineering_session_maps_worker_build_result()
    {
        var session = new TiaOpennessV21EngineeringSession(
            new RecordingV21EngineeringSessionHandle(
                "tia-session-1",
                targets: new[] { TiaOpennessV21TestSupport.CreateTarget("plc-1") },
                buildResult: new TiaOpennessV21BuildHostResult(
                    TiaOperationStatus.SucceededWithWarnings,
                    1,
                    0,
                    new[]
                    {
                        new TiaDiagnostic(
                            "TiaOpenness.V21.CompilerMessage",
                            TiaDiagnosticSeverity.Warning,
                            "Compiler warning.",
                            "Build",
                            "build-message-1")
                    },
                    new[]
                    {
                        new TiaEvidence(
                            "build-message-1",
                            "CompilerMessage",
                            "Compiler warning.")
                    })));

        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("plc-1"),
            CancellationToken.None);
        Assert.NotNull(discovery.SelectedTarget);

        var build = await session.BuildAsync(
            new TiaBuildRequest(discovery.SelectedTarget),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.SucceededWithWarnings, build.Status);
        Assert.Equal(1, build.WarningCount);
        Assert.Equal(0, build.ErrorCount);
        Assert.Contains(build.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V21.CompilerMessage");
        Assert.Contains(build.Evidence, evidence => evidence.Id == "build-message-1");
    }

    [Fact]
    public async Task V21_engineering_session_maps_worker_transfer_result()
    {
        var session = new TiaOpennessV21EngineeringSession(
            new RecordingV21EngineeringSessionHandle(
                "tia-session-1",
                targets: new[] { TiaOpennessV21TestSupport.CreateTarget("plc-1") },
                transferResult: new TiaOpennessV21TransferHostResult(
                    TiaOperationStatus.SucceededWithWarnings,
                    new[]
                    {
                        new TiaDiagnostic(
                            "TiaOpenness.V21.DownloadMessage",
                            TiaDiagnosticSeverity.Warning,
                            "Download warning.",
                            "Transfer",
                            "transfer-message-1")
                    },
                    new[]
                    {
                        new TiaEvidence(
                            "transfer-message-1",
                            "DownloadMessage",
                            "Download warning.")
                    })));

        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("plc-1"),
            CancellationToken.None);
        Assert.NotNull(discovery.SelectedTarget);

        var transfer = await session.TransferAsync(
            new TiaTransferRequest(
                discovery.SelectedTarget,
                CreateRuntimeTarget()),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.SucceededWithWarnings, transfer.Status);
        Assert.Contains(transfer.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V21.DownloadMessage");
        Assert.Contains(transfer.Evidence, evidence => evidence.Id == "transfer-message-1");
    }

    [Fact]
    public async Task V21_engineering_session_rejects_invalid_transfer_route_before_worker_dispatch()
    {
        var handle = new RecordingV21EngineeringSessionHandle("tia-session-1");
        var session = new TiaOpennessV21EngineeringSession(handle);
        var invalidRuntimeTarget = CreateRuntimeTarget() with
        {
            PcInterfaceNumber = 0
        };

        var transfer = await session.TransferAsync(
            new TiaTransferRequest(
                TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef(),
                invalidRuntimeTarget),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Failed, transfer.Status);
        Assert.Contains(transfer.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V21.TransferRouteInvalid");
        Assert.Equal(0, handle.TransferCount);
        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
    }

    [Fact]
    public async Task V21_engineering_session_invalidates_health_when_worker_build_throws()
    {
        var session = new TiaOpennessV21EngineeringSession(
            new RecordingV21EngineeringSessionHandle(
                "tia-session-1",
                buildException: new IOException("worker pipe failed")));
        var health = new List<TiaProjectHealthChangedEventArgs>();
        session.HealthChanged += (_, args) => health.Add(args);
        var target = TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef();

        var build = await session.BuildAsync(new TiaBuildRequest(target), CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Invalidated, build.Status);
        Assert.Contains(build.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V21.WorkerSessionFailed");
        Assert.Equal(TiaProjectHealthState.Invalidated, session.Health);
        Assert.Contains(health, update => update.Current == TiaProjectHealthState.Invalidated);
    }

    [Fact]
    public async Task V21_engineering_session_keeps_health_when_dispatched_build_is_drained_after_cancellation()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var session = new TiaOpennessV21EngineeringSession(
            new RecordingV21EngineeringSessionHandle(
                "tia-session-1",
                buildException: new TiaOpennessV21WorkerCommandCanceledException(
                    "worker request canceled after dispatch",
                    new OperationCanceledException(canceled.Token),
                    canceled.Token)));
        var target = TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef();

        await Assert.ThrowsAsync<TiaOpennessV21WorkerCommandCanceledException>(
            () => session.BuildAsync(new TiaBuildRequest(target), CancellationToken.None));
        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(null),
            CancellationToken.None);

        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
        Assert.True(discovery.Succeeded);
    }

    [Fact]
    public async Task V21_engineering_session_keeps_health_when_dispatched_discovery_is_drained_after_cancellation()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var session = new TiaOpennessV21EngineeringSession(
            new RecordingV21EngineeringSessionHandle(
                "tia-session-1",
                discoveryException: new TiaOpennessV21WorkerCommandCanceledException(
                    "worker request canceled after a terminal response",
                    new OperationCanceledException(canceled.Token),
                    canceled.Token)));

        await Assert.ThrowsAsync<TiaOpennessV21WorkerCommandCanceledException>(
            () => session.DiscoverTargetsAsync(
                new TiaProjectTargetDiscoveryRequest(null),
                CancellationToken.None));
        var build = await session.BuildAsync(
            new TiaBuildRequest(TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef()),
            CancellationToken.None);

        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
        Assert.True(build.Succeeded);
    }

    [Fact]
    public async Task V21_engineering_session_keeps_health_when_dispatched_transfer_is_drained_after_cancellation()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var session = new TiaOpennessV21EngineeringSession(
            new RecordingV21EngineeringSessionHandle(
                "tia-session-1",
                transferException: new TiaOpennessV21WorkerCommandCanceledException(
                    "worker request canceled after a terminal response",
                    new OperationCanceledException(canceled.Token),
                    canceled.Token)));

        await Assert.ThrowsAsync<TiaOpennessV21WorkerCommandCanceledException>(
            () => session.TransferAsync(
                new TiaTransferRequest(
                    TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef(),
                    CreateRuntimeTarget()),
                CancellationToken.None));
        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(null),
            CancellationToken.None);

        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
        Assert.True(discovery.Succeeded);
    }

    [Fact]
    public async Task V21_engineering_session_keeps_health_for_cancellation_before_worker_dispatch()
    {
        var session = new TiaOpennessV21EngineeringSession(
            new RecordingV21EngineeringSessionHandle(
                "tia-session-1",
                buildException: new OperationCanceledException("canceled before dispatch")));
        var target = TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => session.BuildAsync(new TiaBuildRequest(target), CancellationToken.None));

        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
    }

    [Fact]
    public async Task V21_engineering_session_blocks_follow_up_operations_after_worker_failure()
    {
        var session = new TiaOpennessV21EngineeringSession(
            new RecordingV21EngineeringSessionHandle(
                "tia-session-1",
                transferException: new IOException("worker pipe failed")));
        var target = TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef();

        var transfer = await session.TransferAsync(
            new TiaTransferRequest(target, CreateRuntimeTarget()),
            CancellationToken.None);
        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("plc-1"),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Invalidated, transfer.Status);
        Assert.Equal(TiaOperationStatus.Invalidated, discovery.Status);
        Assert.Contains(discovery.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V21.SessionInvalidated");
    }

    private static TiaRuntimeTransferTarget CreateRuntimeTarget()
    {
        return new TiaRuntimeTransferTarget(
            "runtime-1",
            "PLCSIM Advanced Runtime",
            SiemensBindingKinds.PlcSimAdvanced,
            "8.0",
            SiemensRuntimeLocality.Local,
            "PlcUnderTest",
            TiaTransferConnectionMode.PnIe,
            TiaTransferPcInterface.PlcSim,
            1,
            null);
    }

    private static System.Security.SecureString CreateSecureString(string value)
    {
        var result = new System.Security.SecureString();
        foreach (var character in value)
        {
            result.AppendChar(character);
        }

        result.MakeReadOnly();
        return result;
    }
}
