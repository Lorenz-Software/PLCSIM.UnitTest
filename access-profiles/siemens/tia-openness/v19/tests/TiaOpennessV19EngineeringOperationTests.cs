using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V19;

public sealed class TiaOpennessV19EngineeringOperationTests
{
    [Fact]
    public async Task V19_engineering_session_maps_worker_build_result()
    {
        var session = new TiaOpennessV19EngineeringSession(
            new RecordingV19EngineeringSessionHandle(
                "tia-session-1",
                targets: new[] { TiaOpennessV19TestSupport.CreateTarget("plc-1") },
                buildResult: new TiaOpennessV19BuildHostResult(
                    TiaOperationStatus.SucceededWithWarnings,
                    1,
                    0,
                    new[]
                    {
                        new TiaDiagnostic(
                            "TiaOpenness.V19.CompilerMessage",
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
        Assert.Contains(build.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V19.CompilerMessage");
        Assert.Contains(build.Evidence, evidence => evidence.Id == "build-message-1");
    }

    [Fact]
    public async Task V19_engineering_session_maps_worker_transfer_result()
    {
        var session = new TiaOpennessV19EngineeringSession(
            new RecordingV19EngineeringSessionHandle(
                "tia-session-1",
                targets: new[] { TiaOpennessV19TestSupport.CreateTarget("plc-1") },
                transferResult: new TiaOpennessV19TransferHostResult(
                    TiaOperationStatus.SucceededWithWarnings,
                    new[]
                    {
                        new TiaDiagnostic(
                            "TiaOpenness.V19.DownloadMessage",
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
        Assert.Contains(transfer.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V19.DownloadMessage");
        Assert.Contains(transfer.Evidence, evidence => evidence.Id == "transfer-message-1");
    }

    [Fact]
    public async Task V19_engineering_session_rejects_invalid_transfer_route_before_worker_dispatch()
    {
        var handle = new RecordingV19EngineeringSessionHandle("tia-session-1");
        var session = new TiaOpennessV19EngineeringSession(handle);
        var invalidRuntimeTarget = CreateRuntimeTarget() with
        {
            PcInterfaceNumber = 0
        };

        var transfer = await session.TransferAsync(
            new TiaTransferRequest(
                TiaOpennessV19TestSupport.CreateTarget("plc-1").ToTargetRef(),
                invalidRuntimeTarget),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Failed, transfer.Status);
        Assert.Contains(transfer.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V19.TransferRouteInvalid");
        Assert.Equal(0, handle.TransferCount);
        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
    }

    [Fact]
    public async Task V19_engineering_session_invalidates_health_when_worker_build_throws()
    {
        var session = new TiaOpennessV19EngineeringSession(
            new RecordingV19EngineeringSessionHandle(
                "tia-session-1",
                buildException: new IOException("worker pipe failed")));
        var health = new List<TiaProjectHealthChangedEventArgs>();
        session.HealthChanged += (_, args) => health.Add(args);
        var target = TiaOpennessV19TestSupport.CreateTarget("plc-1").ToTargetRef();

        var build = await session.BuildAsync(new TiaBuildRequest(target), CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Invalidated, build.Status);
        Assert.Contains(build.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V19.WorkerSessionFailed");
        Assert.Equal(TiaProjectHealthState.Invalidated, session.Health);
        Assert.Contains(health, update => update.Current == TiaProjectHealthState.Invalidated);
    }

    [Fact]
    public async Task V19_engineering_session_keeps_health_when_dispatched_build_is_drained_after_cancellation()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var session = new TiaOpennessV19EngineeringSession(
            new RecordingV19EngineeringSessionHandle(
                "tia-session-1",
                buildException: new TiaOpennessV19WorkerCommandCanceledException(
                    "worker request canceled after dispatch",
                    new OperationCanceledException(canceled.Token),
                    canceled.Token)));
        var target = TiaOpennessV19TestSupport.CreateTarget("plc-1").ToTargetRef();

        await Assert.ThrowsAsync<TiaOpennessV19WorkerCommandCanceledException>(
            () => session.BuildAsync(new TiaBuildRequest(target), CancellationToken.None));
        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(null),
            CancellationToken.None);

        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
        Assert.True(discovery.Succeeded);
    }

    [Fact]
    public async Task V19_engineering_session_keeps_health_when_dispatched_discovery_is_drained_after_cancellation()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var session = new TiaOpennessV19EngineeringSession(
            new RecordingV19EngineeringSessionHandle(
                "tia-session-1",
                discoveryException: new TiaOpennessV19WorkerCommandCanceledException(
                    "worker request canceled after a terminal response",
                    new OperationCanceledException(canceled.Token),
                    canceled.Token)));

        await Assert.ThrowsAsync<TiaOpennessV19WorkerCommandCanceledException>(
            () => session.DiscoverTargetsAsync(
                new TiaProjectTargetDiscoveryRequest(null),
                CancellationToken.None));
        var build = await session.BuildAsync(
            new TiaBuildRequest(TiaOpennessV19TestSupport.CreateTarget("plc-1").ToTargetRef()),
            CancellationToken.None);

        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
        Assert.True(build.Succeeded);
    }

    [Fact]
    public async Task V19_engineering_session_keeps_health_when_dispatched_transfer_is_drained_after_cancellation()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var session = new TiaOpennessV19EngineeringSession(
            new RecordingV19EngineeringSessionHandle(
                "tia-session-1",
                transferException: new TiaOpennessV19WorkerCommandCanceledException(
                    "worker request canceled after a terminal response",
                    new OperationCanceledException(canceled.Token),
                    canceled.Token)));

        await Assert.ThrowsAsync<TiaOpennessV19WorkerCommandCanceledException>(
            () => session.TransferAsync(
                new TiaTransferRequest(
                    TiaOpennessV19TestSupport.CreateTarget("plc-1").ToTargetRef(),
                    CreateRuntimeTarget()),
                CancellationToken.None));
        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(null),
            CancellationToken.None);

        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
        Assert.True(discovery.Succeeded);
    }

    [Fact]
    public async Task V19_engineering_session_keeps_health_for_cancellation_before_worker_dispatch()
    {
        var session = new TiaOpennessV19EngineeringSession(
            new RecordingV19EngineeringSessionHandle(
                "tia-session-1",
                buildException: new OperationCanceledException("canceled before dispatch")));
        var target = TiaOpennessV19TestSupport.CreateTarget("plc-1").ToTargetRef();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => session.BuildAsync(new TiaBuildRequest(target), CancellationToken.None));

        Assert.Equal(TiaProjectHealthState.Healthy, session.Health);
    }

    [Fact]
    public async Task V19_engineering_session_blocks_follow_up_operations_after_worker_failure()
    {
        var session = new TiaOpennessV19EngineeringSession(
            new RecordingV19EngineeringSessionHandle(
                "tia-session-1",
                transferException: new IOException("worker pipe failed")));
        var target = TiaOpennessV19TestSupport.CreateTarget("plc-1").ToTargetRef();

        var transfer = await session.TransferAsync(
            new TiaTransferRequest(target, CreateRuntimeTarget()),
            CancellationToken.None);
        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("plc-1"),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Invalidated, transfer.Status);
        Assert.Equal(TiaOperationStatus.Invalidated, discovery.Status);
        Assert.Contains(discovery.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V19.SessionInvalidated");
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
