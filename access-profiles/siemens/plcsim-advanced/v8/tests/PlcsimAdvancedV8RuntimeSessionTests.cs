using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8;

public sealed class PlcsimAdvancedV8RuntimeSessionTests
{
    [Fact]
    public async Task V8_runtime_session_retains_cancelled_owned_prepare_for_retry_and_cleanup()
    {
        var worker = await OpenValidationWorkerSessionAsync(
            "delayed-prepare-owned",
            TimeSpan.FromSeconds(10));
        var session = new PlcsimAdvancedV8RuntimeSession(
            worker,
            runtimeReference: null,
            DateTimeOffset.UtcNow,
            unregisterSession: () => { });
        var request = new PlcsimRuntimePrepareRequest(
            new PlcsimRuntimeReference(null, "OwnedAfterCancel"),
            new PlcsimRuntimeStoragePreference("C:\\Temp\\OwnedAfterCancel"),
            PlcsimRuntimeProvisioningMode.CreateOwned,
            PlcsimRuntimeCpuType.S71500,
            TimeSpan.FromSeconds(5),
            "cancelled-owned-prepare");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var cancelled = await session.PrepareRuntimeAsync(request, cancellation.Token);

        Assert.Equal(PlcsimOperationStatus.Cancelled, cancelled.Status);
        Assert.True(Assert.IsType<PlcsimRuntimeTargetCandidate>(cancelled.Runtime).IsOwned);
        Assert.True(session.Runtime.IsOwned);
        Assert.Equal("OwnedAfterCancel", session.Runtime.RuntimeReference?.InstanceName);

        var retry = await session.PrepareRuntimeAsync(
            request with { CorrelationId = "retry-owned-prepare" },
            CancellationToken.None);
        Assert.True(retry.Succeeded);
        Assert.True(Assert.IsType<PlcsimRuntimeTargetCandidate>(retry.Runtime).IsOwned);

        var release = await session.ReleaseRuntimeAsync(
            new PlcsimRuntimeReleaseRequest(
                session.Runtime.SessionId,
                PlcsimRuntimeCleanupPreference.Release,
                TimeSpan.FromSeconds(5),
                "release-owned-after-cancel"),
            CancellationToken.None);
        Assert.True(release.Succeeded);
        Assert.True(release.Released);
        Assert.True(release.StorageCleaned);
        await worker.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_prepare_attaches_existing_runtime()
    {
        var session = await OpenValidationWorkerSessionAsync("prepare-existing");

        var result = await session.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                new PlcsimRuntimeReference("0", "PlcUnderTest"),
                new PlcsimRuntimeStoragePreference(null),
                PlcsimRuntimeProvisioningMode.UseExisting,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(30),
                "correlation"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Runtime);
        var runtime = result.Runtime;
        Assert.Equal("0", runtime.Reference.Id);
        Assert.Equal("PlcUnderTest", runtime.Reference.InstanceName);
        Assert.False(runtime.IsOwned);
        Assert.Equal(PlcsimRuntimeControlState.Stop, runtime.State);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_prepare_creates_owned_runtime_when_allowed()
    {
        var session = await OpenValidationWorkerSessionAsync("prepare-owned");

        var result = await session.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                new PlcsimRuntimeReference(null, "CreatedByTest"),
                new PlcsimRuntimeStoragePreference("D:\\RuntimeStorage"),
                PlcsimRuntimeProvisioningMode.CreateOwned,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(30),
                "correlation"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Runtime);
        var runtime = result.Runtime;
        Assert.Equal("CreatedByTest", runtime.Reference.InstanceName);
        Assert.True(runtime.IsOwned);
        Assert.Equal(PlcsimRuntimeControlState.Off, runtime.State);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_prepare_reports_clean_owned_runtime_rollback_as_failed()
    {
        var session = await OpenValidationWorkerSessionAsync("prepare-storage-failure-clean-rollback");

        var result = await session.PrepareRuntimeAsync(CreateOwnedPrepareRequest(), CancellationToken.None);
        var retry = await session.PrepareRuntimeAsync(CreateOwnedPrepareRequest("retry"), CancellationToken.None);

        Assert.Equal(PlcsimOperationStatus.Failed, result.Status);
        Assert.Null(result.Runtime);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V8.Worker.StoragePathFailed");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code.Contains("Compensation", StringComparison.Ordinal));
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "RuntimePrepareCompensationResource");
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "RuntimePrepareCompensationUnregister"
            && evidence.Summary.Contains("status=Succeeded", StringComparison.Ordinal));
        Assert.Equal(PlcsimOperationStatus.Failed, retry.Status);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_prepare_reports_poweroff_failure_but_keeps_session_when_unregister_succeeds()
    {
        var session = await OpenValidationWorkerSessionAsync("prepare-poweron-failure-poweroff-failed");

        var result = await session.PrepareRuntimeAsync(CreateOwnedPrepareRequest(), CancellationToken.None);
        var retry = await session.PrepareRuntimeAsync(CreateOwnedPrepareRequest("retry"), CancellationToken.None);

        Assert.Equal(PlcsimOperationStatus.Failed, result.Status);
        Assert.Null(result.Runtime);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V8.Worker.PowerOnFailed");
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "PlcsimAdvanced.V8.RuntimePrepareCompensationPowerOffFailed"
            && diagnostic.Severity == PlcsimDiagnosticSeverity.Warning);
        Assert.DoesNotContain(result.Diagnostics, diagnostic =>
            diagnostic.Code == "PlcsimAdvanced.V8.RuntimePrepareCompensationUnregisterFailed");
        Assert.Equal(PlcsimOperationStatus.Failed, retry.Status);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_prepare_invalidates_session_when_unregister_compensation_fails()
    {
        var session = await OpenValidationWorkerSessionAsync("prepare-poweron-failure-unregister-failed");

        var result = await session.PrepareRuntimeAsync(CreateOwnedPrepareRequest(), CancellationToken.None);
        var retry = await session.PrepareRuntimeAsync(CreateOwnedPrepareRequest("retry"), CancellationToken.None);

        Assert.Equal(PlcsimOperationStatus.Unclear, result.Status);
        Assert.Null(result.Runtime);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V8.Worker.PowerOnFailed");
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "PlcsimAdvanced.V8.RuntimePrepareCompensationUnregisterFailed"
            && diagnostic.Severity == PlcsimDiagnosticSeverity.Error);
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "RuntimePrepareCompensationResource"
            && evidence.Summary.Contains("validation-created-runtime", StringComparison.Ordinal));
        Assert.Equal(PlcsimOperationStatus.Failed, retry.Status);
        Assert.Contains(retry.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V8.WorkerUnavailable");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_prepare_reports_combined_compensation_failures_and_invalidates_session()
    {
        var session = await OpenValidationWorkerSessionAsync("prepare-storage-failure-compensation-failed");

        var result = await session.PrepareRuntimeAsync(CreateOwnedPrepareRequest(), CancellationToken.None);
        var retry = await session.PrepareRuntimeAsync(CreateOwnedPrepareRequest("retry"), CancellationToken.None);

        Assert.Equal(PlcsimOperationStatus.Unclear, result.Status);
        Assert.Null(result.Runtime);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V8.Worker.StoragePathFailed");
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "PlcsimAdvanced.V8.RuntimePrepareCompensationPowerOffFailed"
            && diagnostic.Severity == PlcsimDiagnosticSeverity.Error);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "PlcsimAdvanced.V8.RuntimePrepareCompensationUnregisterFailed");
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "RuntimePrepareCompensationPowerOff"
            && evidence.Summary.Contains("status=Failed", StringComparison.Ordinal));
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "RuntimePrepareCompensationUnregister"
            && evidence.Summary.Contains("status=Failed", StringComparison.Ordinal));
        Assert.Equal(PlcsimOperationStatus.Failed, retry.Status);
        Assert.Contains(retry.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V8.WorkerUnavailable");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_observes_prepared_runtime_state()
    {
        var session = await OpenValidationWorkerSessionAsync("runtime-observe-stop");

        var result = await session.ObserveRuntimeAsync(
            new PlcsimRuntimeObservationRequest("correlation"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Runtime);
        Assert.Equal("PlcUnderTest", result.Runtime.Reference.InstanceName);
        Assert.Equal(PlcsimRuntimeControlState.Stop, result.Runtime.State);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_revalidates_prepared_runtime_in_place()
    {
        var session = await OpenValidationWorkerSessionAsync("runtime-observe-stop");
        var sessionId = session.SessionId;

        var result = await session.RevalidateRuntimeAsync(
            new PlcsimRuntimeRevalidationRequest(
                new PlcsimRuntimeReference("0", "PlcUnderTest"),
                TimeSpan.FromSeconds(5),
                "runtime-revalidation"),
            CancellationToken.None);

        Assert.True(result.Succeeded, FormatDiagnostics(result.Diagnostics, result.Evidence));
        Assert.Equal(sessionId, session.SessionId);
        Assert.Equal("0", result.Runtime?.Reference.Id);
        Assert.Equal("PlcUnderTest", result.Runtime?.Reference.InstanceName);
        Assert.Equal(PlcsimRuntimeControlState.Stop, result.Runtime?.State);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_revalidates_running_runtime_in_place()
    {
        var session = await OpenValidationWorkerSessionAsync("runtime-observe-run");
        var sessionId = session.SessionId;

        var result = await session.RevalidateRuntimeAsync(
            new PlcsimRuntimeRevalidationRequest(
                new PlcsimRuntimeReference("0", "PlcUnderTest"),
                TimeSpan.FromSeconds(5),
                "runtime-revalidation-running"),
            CancellationToken.None);

        Assert.True(result.Succeeded, FormatDiagnostics(result.Diagnostics, result.Evidence));
        Assert.Equal(sessionId, session.SessionId);
        Assert.Equal(PlcsimRuntimeControlState.Run, result.Runtime?.State);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_runtime_session_invalidates_after_worker_disconnect_during_revalidation()
    {
        var host = new PlcsimAdvancedV8WorkerSessionHost(
            ResolveBuiltValidationWorkerPath);
        var opened = await host.OpenAsync(
            "exit-after-ready",
            CreateBindingContext(),
            CancellationToken.None);
        var workerSession = Assert.IsAssignableFrom<IPlcsimAdvancedV8WorkerSessionHandle>(opened.Session);
        var runtimeSession = new PlcsimAdvancedV8RuntimeSession(
            workerSession,
            new PlcsimRuntimeReference("0", "PlcUnderTest"),
            DateTimeOffset.UtcNow,
            () => { });
        await Task.Delay(100);

        var result = await runtimeSession.RevalidateRuntimeAsync(
            new PlcsimRuntimeRevalidationRequest(
                new PlcsimRuntimeReference("0", "PlcUnderTest"),
                TimeSpan.FromSeconds(5),
                "runtime-revalidation-disconnected"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PlcsimRuntimeHealthState.Invalidated, runtimeSession.Health);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code is "PlcsimAdvanced.V8.RuntimeRevalidationWorkerDisconnected"
                or "PlcsimAdvanced.V8.RuntimeRevalidationIpcFailed"
                or "PlcsimAdvanced.V8.WorkerUnavailable");

        await runtimeSession.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_rejects_runtime_revalidation_for_different_identity()
    {
        var session = await OpenValidationWorkerSessionAsync("runtime-observe-stop");

        var result = await session.RevalidateRuntimeAsync(
            new PlcsimRuntimeRevalidationRequest(
                new PlcsimRuntimeReference("different", "DifferentRuntime"),
                TimeSpan.FromSeconds(5),
                "runtime-revalidation-mismatch"),
            CancellationToken.None);

        Assert.Equal(PlcsimOperationStatus.Rejected, result.Status);
        Assert.Null(result.Runtime);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "PlcsimAdvanced.V8.Worker.RuntimeIdentityMismatch");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_controls_prepared_runtime_state()
    {
        var logDirectory = CreateTestLogDirectory();
        var host = new PlcsimAdvancedV8WorkerSessionHost(
            ResolveBuiltValidationWorkerPath);
        var open = await host.OpenAsync(
            "runtime-control",
            CreateBindingContext(logDirectory),
            CancellationToken.None);
        Assert.True(open.Succeeded);
        var session = Assert.IsAssignableFrom<IPlcsimAdvancedV8WorkerSessionHandle>(open.Session);
        var changes = new List<PlcsimRuntimeStateChange>();
        session.RuntimeStateChanged += (_, args) => changes.Add(args.Change);

        var started = await session.StartRuntimeAsync(
            new PlcsimRuntimeControlRequest(session.SessionId, TimeSpan.FromSeconds(30), "correlation"),
            CancellationToken.None);
        var stopped = await session.StopRuntimeAsync(
            new PlcsimRuntimeControlRequest(session.SessionId, TimeSpan.FromSeconds(30), "correlation"),
            CancellationToken.None);

        Assert.True(started.Succeeded);
        Assert.Equal(PlcsimRuntimeControlState.Run, started.State);
        Assert.True(stopped.Succeeded);
        Assert.Equal(PlcsimRuntimeControlState.Stop, stopped.State);
        Assert.Empty(changes);

        await session.CloseAsync(CancellationToken.None);

        var log = ReadWorkerLog(logDirectory, "siemens-test");
        Assert.Contains("Worker command received. CommandType=StartRuntime", log);
        Assert.Contains("Worker command handling ended. CommandType=StartRuntime DurationMs=", log);
        Assert.Contains("Worker command received. CommandType=StopRuntime", log);
        Assert.Contains("Worker command handling ended. CommandType=StopRuntime DurationMs=", log);
        Assert.Contains("Worker command received. CommandType=Close", log);
        Assert.DoesNotContain("SIEMENS-WORKER/1|", log);
    }

    [Fact]
    public async Task V8_worker_session_drains_cancelled_runtime_operations_and_remains_reusable()
    {
        var session = await OpenValidationWorkerSessionAsync(
            "delayed-runtime-operations",
            TimeSpan.FromSeconds(10));
        using var observationCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var observation = await session.ObserveRuntimeAsync(
            new PlcsimRuntimeObservationRequest("cancelled-observation"),
            observationCancellation.Token);

        Assert.Equal(PlcsimOperationStatus.Cancelled, observation.Status);
        Assert.Equal(PlcsimRuntimeControlState.Stop, observation.Runtime?.State);

        using var controlCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var start = await session.StartRuntimeAsync(
            new PlcsimRuntimeControlRequest(
                session.SessionId,
                TimeSpan.FromSeconds(5),
                "cancelled-start"),
            controlCancellation.Token);

        Assert.Equal(PlcsimOperationStatus.Cancelled, start.Status);
        Assert.Equal(PlcsimRuntimeControlState.Run, start.State);

        var stop = await session.StopRuntimeAsync(
            new PlcsimRuntimeControlRequest(
                session.SessionId,
                TimeSpan.FromSeconds(5),
                "stop-after-cancel"),
            CancellationToken.None);
        Assert.True(stop.Succeeded);
        Assert.Equal(PlcsimRuntimeControlState.Stop, stop.State);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_tolerates_interleaved_runtime_state_events()
    {
        var session = await OpenValidationWorkerSessionAsync("runtime-state-before-control");
        var changes = new List<PlcsimRuntimeStateChange>();
        session.RuntimeStateChanged += (_, args) => changes.Add(args.Change);

        var started = await session.StartRuntimeAsync(
            new PlcsimRuntimeControlRequest(session.SessionId, TimeSpan.FromSeconds(30), "correlation"),
            CancellationToken.None);

        Assert.True(started.Succeeded);
        Assert.Equal(PlcsimRuntimeControlState.Run, started.State);
        var change = Assert.Single(changes);
        Assert.Equal(PlcsimRuntimeStateChangeKind.OperatingStateChanged, change.Kind);
        Assert.Equal(PlcsimRuntimeControlState.Run, change.CurrentState);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_control_reports_unavailable_without_prepared_runtime()
    {
        var session = await OpenValidationWorkerSessionAsync("runtime-control-unavailable");

        var result = await session.StartRuntimeAsync(
            new PlcsimRuntimeControlRequest(session.SessionId, TimeSpan.FromSeconds(30), "correlation"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Unavailable, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V8.Worker.RuntimeNotPrepared");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_explains_start_failure_for_empty_runtime()
    {
        var session = await OpenValidationWorkerSessionAsync("runtime-control-empty");

        var result = await session.StartRuntimeAsync(
            new PlcsimRuntimeControlRequest(session.SessionId, TimeSpan.FromSeconds(30), "correlation"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Failed, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("PlcsimAdvanced.V8.Worker.RuntimeProgramMissing", diagnostic.Code);
        Assert.Contains("no downloaded STEP 7 program", diagnostic.Message);
        Assert.Contains("before starting the PLC", diagnostic.Message);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_release_reports_owned_storage_cleanup()
    {
        var session = await OpenValidationWorkerSessionAsync("release-owned");

        var result = await session.ReleaseRuntimeAsync(
            new PlcsimRuntimeReleaseRequest(
                session.SessionId,
                PlcsimRuntimeCleanupPreference.Release,
                TimeSpan.FromSeconds(30),
                "correlation"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Released);
        Assert.True(result.StorageCleaned);
        Assert.Equal(PlcsimRuntimeCleanupPreference.Release, result.CleanupPreference);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_release_does_not_clean_attached_runtime_storage()
    {
        var session = await OpenValidationWorkerSessionAsync("release-attached");

        var result = await session.ReleaseRuntimeAsync(
            new PlcsimRuntimeReleaseRequest(
                session.SessionId,
                PlcsimRuntimeCleanupPreference.Release,
                TimeSpan.FromSeconds(30),
                "correlation"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Released);
        Assert.False(result.StorageCleaned);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_prepare_maps_unavailable_target()
    {
        var session = await OpenValidationWorkerSessionAsync("prepare-unavailable");

        var result = await session.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                new PlcsimRuntimeReference(null, "MissingRuntime"),
                new PlcsimRuntimeStoragePreference(null),
                PlcsimRuntimeProvisioningMode.UseExisting,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(30),
                "correlation"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Unavailable, result.Status);
        Assert.Null(result.Runtime);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V8.Worker.RuntimeTargetUnavailable");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_worker_session_prepare_maps_ambiguous_target_selection()
    {
        var session = await OpenValidationWorkerSessionAsync("prepare-ambiguous");

        var result = await session.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                null,
                new PlcsimRuntimeStoragePreference(null),
                PlcsimRuntimeProvisioningMode.UseExisting,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(30),
                "correlation"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Rejected, result.Status);
        Assert.Null(result.Runtime);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V8.Worker.RuntimeTargetAmbiguous");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V8_binding_opens_runtime_session_through_worker_host()
    {
        var worker = new StubPlcsimAdvancedV8WorkerSessionHandle("worker-session");
        var host = new StubPlcsimAdvancedV8WorkerSessionHost(
            PlcsimAdvancedV8WorkerSessionHostOpenResult.SucceededResult(
                worker,
                new[]
                {
                    new PlcsimEvidence("worker", "WorkerPipe", "test-pipe")
                }));
        var binding = new PlcsimAdvancedV8Binding(
            new StubPlcsimAdvancedV8EnvironmentProbe(ReadyProbeWithRuntimeApiDirectory("C:\\PLCSIMADV\\API\\8.0")),
            host);
        var bindingContext = CreateBindingContext();
        binding.Initialize(bindingContext);

        var result = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);

        Assert.Same(bindingContext, host.BindingContext);

        Assert.True(result.Succeeded);
        var session = Assert.IsAssignableFrom<IPlcsimRuntimeSession>(result.Session);
        Assert.Equal("worker-session", session.Runtime.SessionId);
        Assert.Equal(PlcsimRuntimeHealthState.Healthy, session.Health);
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "RuntimeApiDirectory");
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "WorkerPipe");
    }

    [Fact]
    public async Task V8_runtime_session_ownership_comes_from_prepare_result()
    {
        var worker = new StubPlcsimAdvancedV8WorkerSessionHandle("worker-session")
        {
            PreparedRuntimeIsOwned = true
        };
        var binding = CreateBindingWithWorker(worker);
        var open = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);
        var session = Assert.IsAssignableFrom<IPlcsimRuntimeSession>(open.Session);

        Assert.False(session.Runtime.IsOwned);

        var prepared = await session.PrepareRuntimeAsync(
            new PlcsimRuntimePrepareRequest(
                new PlcsimRuntimeReference(null, "CreatedByTest"),
                new PlcsimRuntimeStoragePreference("D:\\RuntimeStorage"),
                PlcsimRuntimeProvisioningMode.CreateOwned,
                PlcsimRuntimeCpuType.S71500,
                TimeSpan.FromSeconds(30),
                "ownership"),
            CancellationToken.None);

        Assert.True(prepared.Succeeded);
        Assert.True(session.Runtime.IsOwned);
    }

    [Fact]
    public async Task V8_runtime_session_invalidating_worker_event_unregisters_communication_route()
    {
        var worker = new StubPlcsimAdvancedV8WorkerSessionHandle("worker-session");
        var binding = CreateBindingWithWorker(worker);
        var open = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);
        var session = Assert.IsAssignableFrom<IPlcsimRuntimeSession>(open.Session);

        worker.RaiseRuntimeStateChanged(PlcsimRuntimeStateChangeKind.RuntimeManagerLost);

        var communication = await binding.PrepareCommunicationAsync(
            CreateCommunicationPrepareRequest(session.Runtime.SessionId),
            CancellationToken.None);

        Assert.Equal(PlcsimRuntimeHealthState.Invalidated, session.Health);
        Assert.Equal(PlcsimOperationStatus.Unavailable, communication.Status);
        Assert.Contains(
            communication.Diagnostics,
            diagnostic => diagnostic.Code == "PlcsimAdvanced.V8.RuntimeSessionNotFound");
    }

    [Fact]
    public async Task V8_runtime_session_close_failure_still_unregisters_communication_route()
    {
        var worker = new StubPlcsimAdvancedV8WorkerSessionHandle("worker-session")
        {
            CloseException = new InvalidOperationException("close failed")
        };
        var binding = CreateBindingWithWorker(worker);
        var open = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);
        var session = Assert.IsAssignableFrom<IPlcsimRuntimeSession>(open.Session);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.CloseAsync(CancellationToken.None));
        var communication = await binding.PrepareCommunicationAsync(
            CreateCommunicationPrepareRequest(session.Runtime.SessionId),
            CancellationToken.None);

        Assert.Equal(1, worker.CloseCount);
        Assert.Equal(PlcsimRuntimeHealthState.Invalidated, session.Health);
        Assert.Equal(PlcsimOperationStatus.Unavailable, communication.Status);
    }

    [Fact]
    public async Task V8_runtime_session_delegates_runtime_discovery_to_worker()
    {
        var worker = new StubPlcsimAdvancedV8WorkerSessionHandle(
            "worker-session",
            new PlcsimRuntimeDiscoveryResult(
                PlcsimOperationStatus.Succeeded,
                new[]
                {
                    new PlcsimRuntimeTargetCandidate(
                        new PlcsimRuntimeReference("runtime-id", "PlcUnderTest"),
                        "8.0",
                        "8.0",
                        SiemensRuntimeLocality.Local,
                        PlcsimRuntimeControlState.Run,
                        true,
                        false)
                },
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        var binding = new PlcsimAdvancedV8Binding(
            new StubPlcsimAdvancedV8EnvironmentProbe(ReadyProbeWithRuntimeApiDirectory("C:\\PLCSIMADV\\API\\8.0")),
            new StubPlcsimAdvancedV8WorkerSessionHost(PlcsimAdvancedV8WorkerSessionHostOpenResult.SucceededResult(
                worker,
                Array.Empty<PlcsimEvidence>())));
        binding.Initialize(CreateBindingContext());

        var open = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);
        var discovery = await open.Session!.DiscoverRuntimeTargetsAsync(
            new PlcsimRuntimeDiscoveryRequest("correlation"),
            CancellationToken.None);

        Assert.True(discovery.Succeeded);
        var candidate = Assert.Single(discovery.Candidates);
        Assert.Equal("PlcUnderTest", candidate.Reference.InstanceName);
        Assert.Equal(PlcsimRuntimeControlState.Run, candidate.State);
    }

    private static PlcsimAdvancedV8EnvironmentProbeResult ReadyProbeWithRuntimeApiDirectory(string runtimeApiDirectory)
    {
        return PlcsimAdvancedV8EnvironmentProbeResult.Ready(new[]
        {
            new PlcsimEvidence(
                "plcsim-advanced-v8-api-directory",
                "RuntimeApiDirectory",
                runtimeApiDirectory),
            new PlcsimEvidence(
                "plcsim-advanced-v8-runtime-api-dll",
                "RuntimeApiDll",
                Path.Combine(runtimeApiDirectory, "Siemens.Simatic.Simulation.Runtime.Api.x64.dll"))
        });
    }

    private static PlcsimRuntimeSessionOpenRequest CreateOpenRequest()
    {
        return new PlcsimRuntimeSessionOpenRequest(
            new PlcsimRuntimeApiExpectation("8.0", "8.0", SiemensRuntimeLocality.Local),
            new PlcsimRuntimeReference(null, "PlcUnderTest"),
            new PlcsimRuntimeStoragePreference(null),
            PlcsimRuntimeCleanupPreference.Keep,
            null);
    }

    private static PlcsimAdvancedV8Binding CreateBindingWithWorker(
        StubPlcsimAdvancedV8WorkerSessionHandle worker)
    {
        var binding = new PlcsimAdvancedV8Binding(
            new StubPlcsimAdvancedV8EnvironmentProbe(
                ReadyProbeWithRuntimeApiDirectory("C:\\PLCSIMADV\\API\\8.0")),
            new StubPlcsimAdvancedV8WorkerSessionHost(
                PlcsimAdvancedV8WorkerSessionHostOpenResult.SucceededResult(
                    worker,
                    Array.Empty<PlcsimEvidence>())));
        binding.Initialize(CreateBindingContext());
        return binding;
    }

    private static SiemensBindingContext CreateBindingContext(
        string? logDirectory = null,
        bool rawEventLoggingEnabled = false,
        LogLevel logLevel = LogLevel.Debug)
    {
        return new SiemensBindingContext(
            "siemens-test",
            new OperationalLoggingOptions(
                logDirectory ?? CreateTestLogDirectory(),
                logLevel,
                10 * 1024 * 1024,
                5,
                rawEventLoggingEnabled));
    }

    private static string CreateTestLogDirectory()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            Guid.NewGuid().ToString("N"),
            "logs");
    }

    private static string ReadWorkerLog(string logDirectory, string accessProfileId)
    {
        var logPath = Assert.Single(Directory.GetFiles(
            logDirectory,
            "access-profile-" + accessProfileId + "-plcsim-advanced-v8-worker-*.log"));
        return File.ReadAllText(logPath);
    }

    private static string ResolveBuiltWorkerPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "access-profiles",
                "siemens",
                "plcsim-advanced",
                "v8",
                "worker",
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker",
                "bin",
                "Debug",
                "net48",
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker.exe");
    }

    private static string ResolveBuiltValidationWorkerPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "access-profiles",
                "siemens",
                "plcsim-advanced",
                "v8",
                "tests",
                "worker",
                "ValidationWorker",
                "bin",
                "Debug",
                "net48",
                "PLCSIM.UnitTest.PlcsimAdvanced.V8.ValidationWorker.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "PLCSIM.UnitTest.PlcsimAdvanced.V8.ValidationWorker.exe");
    }

    private static PlcsimRuntimePrepareRequest CreateOwnedPrepareRequest(string correlationId = "compensation")
    {
        return new PlcsimRuntimePrepareRequest(
            new PlcsimRuntimeReference(null, "CompensationTestRuntime"),
            new PlcsimRuntimeStoragePreference("D:\\RuntimeStorage\\CompensationTestRuntime"),
            PlcsimRuntimeProvisioningMode.CreateOwned,
            PlcsimRuntimeCpuType.S71500,
            TimeSpan.FromSeconds(5),
            correlationId);
    }

    private static async Task<IPlcsimAdvancedV8WorkerSessionHandle> OpenValidationWorkerSessionAsync(
        string validationBehavior,
        TimeSpan? commandResponseTimeout = null,
        string? logDirectory = null,
        bool rawEventLoggingEnabled = false,
        LogLevel logLevel = LogLevel.Debug)
    {
        var workerPath = ResolveBuiltValidationWorkerPath();
        var effectiveLogDirectory = logDirectory ?? CreateTestLogDirectory();
        var host = new PlcsimAdvancedV8WorkerSessionHost(
            () => workerPath,
            commandResponseTimeout: commandResponseTimeout ?? TimeSpan.FromSeconds(5));

        var result = await host.OpenAsync(
            string.IsNullOrWhiteSpace(validationBehavior) ? "validation" : validationBehavior,
            CreateBindingContext(effectiveLogDirectory, rawEventLoggingEnabled, logLevel),
            CancellationToken.None);
        Assert.True(
            result.Succeeded,
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)
                    .Concat(result.Evidence.Select(evidence => evidence.Kind + ": " + evidence.Summary))
                    .Append("WorkerPath: " + workerPath)
                    .Append("LogDirectory: " + effectiveLogDirectory)));
        return Assert.IsAssignableFrom<IPlcsimAdvancedV8WorkerSessionHandle>(result.Session);
    }
    private static string FormatDiagnostics(
        IReadOnlyList<PlcsimDiagnostic> diagnostics,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        var diagnosticText = diagnostics.Count == 0
            ? "Diagnostics: <none>"
            : "Diagnostics: " + string.Join(
                " | ",
                diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message} ({diagnostic.Phase})"));
        var evidenceText = evidence.Count == 0
            ? "Evidence: <none>"
            : "Evidence: " + string.Join(
                " | ",
                evidence.Select(item => $"{item.Kind}: {item.Summary}"));
        return diagnosticText + Environment.NewLine + evidenceText;
    }

    private static PlcsimCommunicationPrepareRequest CreateCommunicationPrepareRequest(
        string runtimeSessionId)
    {
        return new PlcsimCommunicationPrepareRequest(
            runtimeSessionId,
            "DB_UnitTestRunControl",
            "DB_UnitTestEventBuffer",
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            CreateWorkerOptions(),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            "prepare");
    }

    private static PlcsimCommunicationWorkerOptions CreateWorkerOptions()
    {
        return new PlcsimCommunicationWorkerOptions(
            PlcsimCommunicationObservationStrategyKind.Polling,
            TimeSpan.FromMilliseconds(100));
    }


}
