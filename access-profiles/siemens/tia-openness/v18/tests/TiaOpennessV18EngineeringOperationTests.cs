using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V18;

public sealed class TiaOpennessV18EngineeringOperationTests
{
    [Fact]
    public async Task V18_engineering_session_maps_worker_build_result()
    {
        var session = new TiaOpennessV18EngineeringSession(
            new RecordingV18EngineeringSessionHandle(
                "tia-session-1",
                targets: new[] { TiaOpennessV18TestSupport.CreateTarget("plc-1") },
                buildResult: new TiaOpennessV18BuildHostResult(
                    TiaOperationStatus.SucceededWithWarnings,
                    1,
                    0,
                    new[]
                    {
                        new TiaDiagnostic(
                            "TiaOpenness.V18.CompilerMessage",
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
        Assert.Contains(build.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V18.CompilerMessage");
        Assert.Contains(build.Evidence, evidence => evidence.Id == "build-message-1");
    }

    [Fact]
    public async Task V18_engineering_session_maps_worker_transfer_result()
    {
        var session = new TiaOpennessV18EngineeringSession(
            new RecordingV18EngineeringSessionHandle(
                "tia-session-1",
                targets: new[] { TiaOpennessV18TestSupport.CreateTarget("plc-1") },
                transferResult: new TiaOpennessV18TransferHostResult(
                    TiaOperationStatus.SucceededWithWarnings,
                    new[]
                    {
                        new TiaDiagnostic(
                            "TiaOpenness.V18.DownloadMessage",
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
        Assert.Contains(transfer.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V18.DownloadMessage");
        Assert.Contains(transfer.Evidence, evidence => evidence.Id == "transfer-message-1");
    }

    [Fact]
    public async Task V18_failed_build_result_keeps_engineering_session_retryable()
    {
        var handle = new RecordingV18EngineeringSessionHandle(
            "tia-session-1",
            targets: new[] { TiaOpennessV18TestSupport.CreateTarget("plc-1") },
            buildResult: new TiaOpennessV18BuildHostResult(
                TiaOperationStatus.Failed,
                0,
                1,
                new[]
                {
                    new TiaDiagnostic(
                        "TiaOpenness.V18.CompilerMessage",
                        TiaDiagnosticSeverity.Error,
                        "Compiler error.",
                        "Build",
                        null)
                },
                Array.Empty<TiaEvidence>()));
        var session = new TiaOpennessV18EngineeringSession(handle);
        var target = TiaOpennessV18TestSupport.CreateTarget("plc-1").ToTargetRef();

        var build = await session.BuildAsync(
            new TiaBuildRequest(target),
            CancellationToken.None);
        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("plc-1"),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Failed, build.Status);
        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
        Assert.True(discovery.Succeeded);
    }

    [Fact]
    public async Task V18_failed_transfer_result_keeps_engineering_session_retryable()
    {
        var handle = new RecordingV18EngineeringSessionHandle(
            "tia-session-1",
            targets: new[] { TiaOpennessV18TestSupport.CreateTarget("plc-1") },
            transferResult: new TiaOpennessV18TransferHostResult(
                TiaOperationStatus.Failed,
                new[]
                {
                    new TiaDiagnostic(
                        "TiaOpenness.V18.DownloadMessage",
                        TiaDiagnosticSeverity.Error,
                        "Download error.",
                        "Transfer",
                        null)
                },
                Array.Empty<TiaEvidence>()));
        var session = new TiaOpennessV18EngineeringSession(handle);
        var target = TiaOpennessV18TestSupport.CreateTarget("plc-1").ToTargetRef();

        var transfer = await session.TransferAsync(
            new TiaTransferRequest(target, CreateRuntimeTarget()),
            CancellationToken.None);
        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("plc-1"),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Failed, transfer.Status);
        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
        Assert.True(discovery.Succeeded);
    }

    [Fact]
    public async Task V18_engineering_session_rejects_invalid_transfer_route_before_worker_dispatch()
    {
        var handle = new RecordingV18EngineeringSessionHandle("tia-session-1");
        var session = new TiaOpennessV18EngineeringSession(handle);
        var invalidRuntimeTarget = CreateRuntimeTarget() with
        {
            PcInterfaceNumber = 0
        };

        var transfer = await session.TransferAsync(
            new TiaTransferRequest(
                TiaOpennessV18TestSupport.CreateTarget("plc-1").ToTargetRef(),
                invalidRuntimeTarget),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Failed, transfer.Status);
        Assert.Contains(transfer.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V18.TransferRouteInvalid");
        Assert.Equal(0, handle.TransferCount);
        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
    }

    [Fact]
    public async Task V18_engineering_session_invalidates_health_when_worker_build_throws()
    {
        var session = new TiaOpennessV18EngineeringSession(
            new RecordingV18EngineeringSessionHandle(
                "tia-session-1",
                buildException: new IOException("worker pipe failed")));
        var health = new List<TiaProjectHealthChangedEventArgs>();
        session.HealthChanged += (_, args) => health.Add(args);
        var target = TiaOpennessV18TestSupport.CreateTarget("plc-1").ToTargetRef();

        var build = await session.BuildAsync(new TiaBuildRequest(target), CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Invalidated, build.Status);
        Assert.Contains(build.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V18.WorkerSessionFailed");
        Assert.Equal(TiaProjectHealthState.Invalidated, session.Health);
        Assert.Contains(health, update => update.Current == TiaProjectHealthState.Invalidated);
    }

    [Fact]
    public async Task V18_engineering_session_keeps_health_when_dispatched_build_is_drained_after_cancellation()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var session = new TiaOpennessV18EngineeringSession(
            new RecordingV18EngineeringSessionHandle(
                "tia-session-1",
                buildException: new TiaOpennessV18WorkerCommandCanceledException(
                    "worker request canceled after dispatch",
                    new OperationCanceledException(canceled.Token),
                    canceled.Token)));
        var target = TiaOpennessV18TestSupport.CreateTarget("plc-1").ToTargetRef();

        await Assert.ThrowsAsync<TiaOpennessV18WorkerCommandCanceledException>(
            () => session.BuildAsync(new TiaBuildRequest(target), CancellationToken.None));
        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(null),
            CancellationToken.None);

        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
        Assert.True(discovery.Succeeded);
    }

    [Fact]
    public async Task V18_engineering_session_keeps_health_when_dispatched_discovery_is_drained_after_cancellation()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var session = new TiaOpennessV18EngineeringSession(
            new RecordingV18EngineeringSessionHandle(
                "tia-session-1",
                discoveryException: new TiaOpennessV18WorkerCommandCanceledException(
                    "worker request canceled after a terminal response",
                    new OperationCanceledException(canceled.Token),
                    canceled.Token)));

        await Assert.ThrowsAsync<TiaOpennessV18WorkerCommandCanceledException>(
            () => session.DiscoverTargetsAsync(
                new TiaProjectTargetDiscoveryRequest(null),
                CancellationToken.None));
        var build = await session.BuildAsync(
            new TiaBuildRequest(TiaOpennessV18TestSupport.CreateTarget("plc-1").ToTargetRef()),
            CancellationToken.None);

        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
        Assert.True(build.Succeeded);
    }

    [Fact]
    public async Task V18_engineering_session_keeps_health_when_dispatched_transfer_is_drained_after_cancellation()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var session = new TiaOpennessV18EngineeringSession(
            new RecordingV18EngineeringSessionHandle(
                "tia-session-1",
                transferException: new TiaOpennessV18WorkerCommandCanceledException(
                    "worker request canceled after a terminal response",
                    new OperationCanceledException(canceled.Token),
                    canceled.Token)));

        await Assert.ThrowsAsync<TiaOpennessV18WorkerCommandCanceledException>(
            () => session.TransferAsync(
                new TiaTransferRequest(
                    TiaOpennessV18TestSupport.CreateTarget("plc-1").ToTargetRef(),
                    CreateRuntimeTarget()),
                CancellationToken.None));
        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(null),
            CancellationToken.None);

        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
        Assert.True(discovery.Succeeded);
    }

    [Fact]
    public async Task V18_engineering_session_keeps_health_for_cancellation_before_worker_dispatch()
    {
        var session = new TiaOpennessV18EngineeringSession(
            new RecordingV18EngineeringSessionHandle(
                "tia-session-1",
                buildException: new OperationCanceledException("canceled before dispatch")));
        var target = TiaOpennessV18TestSupport.CreateTarget("plc-1").ToTargetRef();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => session.BuildAsync(new TiaBuildRequest(target), CancellationToken.None));

        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
    }

    [Fact]
    public async Task V18_engineering_session_blocks_follow_up_operations_after_worker_failure()
    {
        var session = new TiaOpennessV18EngineeringSession(
            new RecordingV18EngineeringSessionHandle(
                "tia-session-1",
                transferException: new IOException("worker pipe failed")));
        var target = TiaOpennessV18TestSupport.CreateTarget("plc-1").ToTargetRef();

        var transfer = await session.TransferAsync(
            new TiaTransferRequest(target, CreateRuntimeTarget()),
            CancellationToken.None);
        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("plc-1"),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Invalidated, transfer.Status);
        Assert.Equal(TiaOperationStatus.Invalidated, discovery.Status);
        Assert.Contains(discovery.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V18.SessionInvalidated");
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
}
