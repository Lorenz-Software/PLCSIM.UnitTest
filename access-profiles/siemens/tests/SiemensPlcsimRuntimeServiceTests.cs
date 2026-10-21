using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.Fake;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.Fake;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens;

public sealed partial class SiemensAccessProfilePluginTests
{
    [Fact]
    public async Task Siemens_plcsim_runtime_discovery_returns_diagnostics_without_event_republishing()
    {
        var plcsimDiagnostic = new PlcsimDiagnostic(
            "PlcsimAdvanced.V8.RuntimeNotice",
            PlcsimDiagnosticSeverity.Warning,
            "Runtime discovery notice.",
            "DiscoverRuntimeTargets",
            "runtime-evidence");
        var service = new SiemensPlcsimPlcControlService(
            new RecordingPlcsimBinding(
                new RecordingPlcsimRuntimeSession(
                    CreateRuntimeCandidate(),
                    discoveryDiagnostics: new[] { plcsimDiagnostic })),
            TimeProvider.System);
        var published = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => published.Add(args.Diagnostic);

        var config = CreateRuntimePrepareRequest(
            @"D:\Projects\sample.apf",
            AccessProfileConfigurationPayload.Empty).Configuration;
        var result = await service.DiscoverRuntimeTargetsAsync(
            new PlcRuntimeDiscoveryRequest(config),
            CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Single(result.Value!.Candidates);
        Assert.Null(result.Value.DefaultTarget);
        var diagnostic = Assert.Single(result.Value!.Diagnostics);
        Assert.Equal(plcsimDiagnostic.Code, diagnostic.Code);
        Assert.Equal("Siemens.PlcsimAdvanced", diagnostic.Correlation.ServiceId);
        Assert.Equal(plcsimDiagnostic.EvidenceId, diagnostic.TechnicalDetail);
        Assert.Empty(published);
    }

    [Fact]
    public async Task Siemens_plcsim_runtime_discovery_publishes_failed_diagnostics_once()
    {
        var plcsimDiagnostic = new PlcsimDiagnostic(
            "PlcsimAdvanced.V8.RuntimeFailed",
            PlcsimDiagnosticSeverity.Error,
            "Runtime discovery failed.",
            "DiscoverRuntimeTargets",
            "runtime-failure-evidence");
        var service = new SiemensPlcsimPlcControlService(
            new RecordingPlcsimBinding(
                new RecordingPlcsimRuntimeSession(
                    CreateRuntimeCandidate(),
                    discoveryDiagnostics: new[] { plcsimDiagnostic },
                    discoveryStatus: PlcsimOperationStatus.Failed)),
            TimeProvider.System);
        var published = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => published.Add(args.Diagnostic);

        var config = CreateRuntimePrepareRequest(
            @"D:\Projects\sample.apf",
            AccessProfileConfigurationPayload.Empty).Configuration;
        var result = await service.DiscoverRuntimeTargetsAsync(
            new PlcRuntimeDiscoveryRequest(config),
            CancellationToken.None);

        Assert.False(result.IsSuccessful);
        var diagnostic = Assert.Single(published);
        Assert.Equal(plcsimDiagnostic.Code, diagnostic.Code);
        Assert.Equal("Siemens.PlcsimAdvanced", diagnostic.Correlation.ServiceId);
        Assert.Equal(plcsimDiagnostic.EvidenceId, diagnostic.TechnicalDetail);
    }

    [Fact]
    public async Task Siemens_plcsim_control_service_maps_runtime_operations_to_plcsim_session()
    {
        var runtime = new PlcsimRuntimeTargetCandidate(
            new PlcsimRuntimeReference("runtime-1", "Runtime 1"),
            "fake-8",
            "fake-8",
            SiemensRuntimeLocality.Local,
            PlcsimRuntimeControlState.Stop,
            IsUsable: true,
            IsOwned: true);
        var runtimeSession = new RecordingPlcsimRuntimeSession(runtime);
        var binding = new RecordingPlcsimBinding(runtimeSession);
        var service = new SiemensPlcsimPlcControlService(binding, TimeProvider.System);
        var stateEvents = new List<PlcState>();
        service.PlcStateChangedEventHandler += (_, args) => stateEvents.Add(args.State);

        var request = CreateRuntimePrepareRequest(
            @"D:\Projects\sample.apf",
            new AccessProfileConfigurationPayload(
                """
                {
                  "runtimeRef": {
                    "id": "runtime-1",
                    "instanceName": "Runtime 1",
                    "storagePath": "%TEMP%\\PLCSIM.UnitTest\\runtime-state",
                    "provisioningMode": "createOwned",
                    "cpuType": "s7-1500",
                    "timeouts": {
                      "prepareMs": 1100,
                      "startMs": 1200,
                      "stopMs": 1300,
                      "releaseMs": 1400
                    }
                  },
                  "cleanup": { "runtime": "release" }
                }
                """),
            runtimeTarget: null,
            provisioningMode: RuntimeProvisioningMode.CreateOwned);

        var discovery = await service.DiscoverRuntimeTargetsAsync(
            new PlcRuntimeDiscoveryRequest(request.Configuration),
            CancellationToken.None);
        var prepare = await service.PrepareRuntimeAsync(request, CancellationToken.None);
        var resolution = await service.ResolveRuntimeTargetAsync(prepare.Value!.Runtime, CancellationToken.None);
        var start = await service.StartAsync(prepare.Value!.Runtime, resolution.Value!.SelectedTarget!, CancellationToken.None);
        Assert.DoesNotContain(stateEvents, state => state.OperatingState == PlcOperatingState.Running);
        Assert.Equal(PlcOperatingState.Stopped, service.CurrentState.OperatingState);

        runtimeSession.RaiseRuntimeState(PlcsimRuntimeStateChangeKind.OperatingStateChanged, PlcsimRuntimeControlState.Run);

        var stop = await service.StopAsync(prepare.Value!.Runtime, resolution.Value.SelectedTarget!, CancellationToken.None);
        Assert.Equal(PlcOperatingState.Running, service.CurrentState.OperatingState);

        runtimeSession.RaiseRuntimeState(PlcsimRuntimeStateChangeKind.OperatingStateChanged, PlcsimRuntimeControlState.Stop);
        Assert.Equal(PlcOperatingState.Stopped, service.CurrentState.OperatingState);

        var release = await service.ReleaseRuntimeAsync(
            CreateRuntimeReleaseRequest(prepare.Value!, RuntimeResourceReleaseMode.DestroyResource),
            CancellationToken.None);

        Assert.True(discovery.IsSuccessful);
        Assert.Single(discovery.Value!.Candidates);
        Assert.Equal(TargetDiscoveryState.CandidatesAvailable, discovery.Value.State);
        Assert.True(prepare.IsSuccessful);
        Assert.Equal("runtime-session-1", prepare.Value!.Runtime.Id.Value);
        Assert.Equal("Runtime 1", prepare.Value.Runtime.DisplayName);
        Assert.Null(binding.LastOpenRequest?.RuntimeReference);
        Assert.Null(binding.LastOpenRequest?.StoragePreference.StoragePath);
        Assert.Equal(PlcsimRuntimeCleanupPreference.Keep, binding.LastOpenRequest?.CleanupPreference);
        Assert.Null(runtimeSession.LastPrepareRequest?.RuntimeReference?.Id);
        Assert.Equal("Runtime 1", runtimeSession.LastPrepareRequest?.RuntimeReference?.InstanceName);
        Assert.EndsWith(Path.Combine("PLCSIM.UnitTest", "runtime-state"), runtimeSession.LastPrepareRequest?.StoragePreference.StoragePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PlcsimRuntimeProvisioningMode.CreateOwned, runtimeSession.LastPrepareRequest?.ProvisioningMode);
        Assert.Equal(PlcsimRuntimeCpuType.S71500, runtimeSession.LastPrepareRequest?.CpuType);
        Assert.Equal(TimeSpan.FromMinutes(1), runtimeSession.LastPrepareRequest?.Timeout);
        Assert.True(resolution.IsSuccessful);
        Assert.Equal(TargetResolutionState.Resolved, resolution.Value.State);
        Assert.True(start.IsSuccessful);
        Assert.Equal(TimeSpan.FromSeconds(10), runtimeSession.LastStartRequest?.Timeout);
        Assert.Equal(PlcOperatingState.Running, start.Value!.OperatingState);
        Assert.True(start.Value.ReadyForExecution);
        Assert.True(stop.IsSuccessful);
        Assert.Equal(TimeSpan.FromSeconds(10), runtimeSession.LastStopRequest?.Timeout);
        Assert.Equal(PlcOperatingState.Stopped, stop.Value!.OperatingState);
        Assert.False(stop.Value.ReadyForExecution);
        Assert.True(release.IsSuccessful);
        Assert.Equal(TimeSpan.FromSeconds(30), runtimeSession.LastReleaseRequest?.Timeout);
        Assert.Equal(1, runtimeSession.ReleaseCount);
        Assert.Equal(1, runtimeSession.CloseCount);
        Assert.Contains(stateEvents, state => state.OperatingState == PlcOperatingState.Running);
    }

    [Fact]
    public async Task Siemens_plcsim_control_service_does_not_reuse_discovery_configuration_for_prepare()
    {
        var runtime = new PlcsimRuntimeTargetCandidate(
            new PlcsimRuntimeReference("runtime-1", "Runtime 1"),
            "fake-8",
            "fake-8",
            SiemensRuntimeLocality.Local,
            PlcsimRuntimeControlState.Stop,
            IsUsable: true,
            IsOwned: true);
        var runtimeSession = new RecordingPlcsimRuntimeSession(runtime);
        var binding = new RecordingPlcsimBinding(runtimeSession);
        var service = new SiemensPlcsimPlcControlService(binding, TimeProvider.System);
        var discoveryConfig = CreateConfig(
            SiemensAccessProfilePlugin.DefaultProfileId,
            SiemensAccessProfilePlugin.DefaultProfileVersion,
            """
            {
              "runtimeRef": {
                "id": "stale-runtime",
                "instanceName": "Stale Runtime",
                "storagePath": "stale-storage",
                "timeouts": { "prepareMs": 900 }
              },
              "cleanup": { "runtime": "keep" }
            }
            """,
            @"D:\Projects\sample.apf");
        var prepareRequest = CreateRuntimePrepareRequest(
            @"D:\Projects\sample.apf",
            new AccessProfileConfigurationPayload(
                """
                {
                  "runtimeRef": {
                    "id": "current-runtime",
                    "instanceName": "Current Runtime",
                    "storagePath": "%TEMP%\\PLCSIM.UnitTest\\current-runtime",
                    "timeouts": { "prepareMs": 1700 }
                  },
                  "cleanup": { "runtime": "release" }
                }
                """),
            runtimeTarget: null,
            provisioningMode: RuntimeProvisioningMode.CreateOwned);

        var discovery = await service.DiscoverRuntimeTargetsAsync(
            new PlcRuntimeDiscoveryRequest(discoveryConfig),
            CancellationToken.None);
        var prepare = await service.PrepareRuntimeAsync(prepareRequest, CancellationToken.None);

        Assert.True(discovery.IsSuccessful);
        Assert.True(prepare.IsSuccessful);
        Assert.Null(binding.LastOpenRequest?.RuntimeReference);
        Assert.Null(binding.LastOpenRequest?.StoragePreference.StoragePath);
        Assert.Equal(PlcsimRuntimeCleanupPreference.Keep, binding.LastOpenRequest?.CleanupPreference);
        Assert.Null(runtimeSession.LastPrepareRequest?.RuntimeReference?.Id);
        Assert.Equal("Current Runtime", runtimeSession.LastPrepareRequest?.RuntimeReference?.InstanceName);
        Assert.EndsWith(
            Path.Combine("PLCSIM.UnitTest", "current-runtime"),
            runtimeSession.LastPrepareRequest?.StoragePreference.StoragePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TimeSpan.FromMinutes(1), runtimeSession.LastPrepareRequest?.Timeout);
    }

    [Fact]
    public async Task Siemens_plcsim_control_service_create_owned_uses_default_storage_and_conservative_fallback_cleanup()
    {
        var runtime = new PlcsimRuntimeTargetCandidate(
            new PlcsimRuntimeReference("runtime-1", "Runtime 1"),
            "fake-8",
            "fake-8",
            SiemensRuntimeLocality.Local,
            PlcsimRuntimeControlState.Stop,
            IsUsable: true,
            IsOwned: true);
        var runtimeSession = new RecordingPlcsimRuntimeSession(runtime);
        var service = new SiemensPlcsimPlcControlService(
            new RecordingPlcsimBinding(runtimeSession),
            TimeProvider.System);
        var request = CreateRuntimePrepareRequest(
            @"D:\Projects\sample.apf",
            new AccessProfileConfigurationPayload(
                """
                {
                  "runtimeRef": {
                    "id": "stale-runtime-id",
                    "instanceName": "Runtime 1"
                  }
                }
                """),
            runtimeTarget: null,
            provisioningMode: RuntimeProvisioningMode.CreateOwned);

        var discovery = await service.DiscoverRuntimeTargetsAsync(
            new PlcRuntimeDiscoveryRequest(request.Configuration),
            CancellationToken.None);
        var prepare = await service.PrepareRuntimeAsync(request, CancellationToken.None);
        var cleanup = await service.CleanupAsync(CancellationToken.None);

        Assert.True(discovery.IsSuccessful);
        Assert.True(prepare.IsSuccessful);
        Assert.True(cleanup.IsSuccessful);
        Assert.Null(runtimeSession.LastPrepareRequest?.RuntimeReference?.Id);
        Assert.Equal("Runtime 1", runtimeSession.LastPrepareRequest?.RuntimeReference?.InstanceName);
        Assert.Contains(
            Path.Combine("PLCSIM.UnitTest", "runtimes", "Runtime 1-"),
            runtimeSession.LastPrepareRequest?.StoragePreference.StoragePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(runtimeSession.LastReleaseRequest);
        Assert.Equal(0, runtimeSession.ReleaseCount);
        Assert.Equal(1, runtimeSession.CloseCount);
    }

    [Fact]
    public async Task Siemens_plcsim_control_service_reconnects_owned_runtime_without_reprovisioning_and_preserves_cleanup()
    {
        var runtime = CreateRuntimeCandidate() with { IsOwned = true };
        var originalSession = new RecordingPlcsimRuntimeSession(runtime);
        var binding = new RecordingPlcsimBinding(originalSession);
        var profileSession = new SiemensAccessProfileSession(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensSelectedBindings(
                new TestTiaBinding(),
                binding,
                new RecordingPlcsimCommunicationBinding(new RecordingPlcsimCommunicationSession())),
            ProjectOwnershipMode.ApplicationManaged,
            CreateResourceScope(),
            TimeProvider.System,
            new SiemensTestLoggerFactory().CreateLogger("test"));
        var prepareRequest = CreateRuntimePrepareRequest(
            @"D:\Projects\sample.apf",
            new AccessProfileConfigurationPayload(
                """
                {
                  "runtimeRef": {
                    "instanceName": "Runtime 1"
                  },
                  "cleanup": { "runtime": "release" }
                }
                """),
            runtimeTarget: null,
            provisioningMode: RuntimeProvisioningMode.CreateOwned);
        var prepared = await profileSession.PlcControl.PrepareRuntimeAsync(prepareRequest, CancellationToken.None);

        var reconnect = await profileSession.ReconnectRuntimeAsync(
            new PlcRuntimeReconnectRequest(
                prepareRequest.Configuration,
                RuntimeProvisioningMode.CreateOwned,
                prepared.Value!.Runtime,
                prepared.Value.RuntimeTarget),
            CancellationToken.None);
        var cleanup = await profileSession.PlcControl.ReleaseRuntimeAsync(
            CreateRuntimeReleaseRequest(reconnect.Value!, RuntimeResourceReleaseMode.DestroyResource),
            CancellationToken.None);

        Assert.True(reconnect.IsSuccessful);
        Assert.Equal(prepared.Value.Runtime.Id, reconnect.Value!.Runtime.Id);
        Assert.Equal(1, binding.OpenCount);
        Assert.Equal(1, originalSession.RevalidateCount);
        Assert.Equal(1, originalSession.CloseCount);
        Assert.Equal(
            originalSession.LastPrepareRequest?.RuntimeReference,
            originalSession.LastRevalidationRequest?.ExpectedRuntime);
        Assert.True(cleanup.IsSuccessful);
        Assert.Equal(PlcsimRuntimeCleanupPreference.Release, originalSession.LastReleaseRequest?.CleanupPreference);
        Assert.Equal(1, originalSession.ReleaseCount);
    }

    [Fact]
    public async Task Siemens_plcsim_reconnect_can_retry_after_runtime_revalidation_failure()
    {
        var runtime = CreateRuntimeCandidate();
        var originalSession = new RecordingPlcsimRuntimeSession(runtime);
        var binding = new RecordingPlcsimBinding(originalSession);
        var profileSession = new SiemensAccessProfileSession(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensSelectedBindings(
                new TestTiaBinding(),
                binding,
                new RecordingPlcsimCommunicationBinding(new RecordingPlcsimCommunicationSession())),
            ProjectOwnershipMode.ApplicationManaged,
            CreateResourceScope(),
            TimeProvider.System,
            new SiemensTestLoggerFactory().CreateLogger("test"));
        var prepareRequest = CreateRuntimePrepareRequest(
            @"D:\Projects\sample.apf",
            new AccessProfileConfigurationPayload(
                """
                {
                  "runtimeRef": { "id": "runtime-1", "instanceName": "Runtime 1" },
                  "cleanup": { "runtime": "keep" }
                }
                """),
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1", "Runtime 1"));
        var prepared = await profileSession.PlcControl.PrepareRuntimeAsync(prepareRequest, CancellationToken.None);
        originalSession.EnqueueRevalidationResult(new PlcsimRuntimeObservationResult(
            PlcsimOperationStatus.Failed,
            null,
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>()));
        var reconnectRequest = new PlcRuntimeReconnectRequest(
            prepareRequest.Configuration,
            RuntimeProvisioningMode.UseExisting,
            prepared.Value!.Runtime,
            prepared.Value.RuntimeTarget);

        var first = await profileSession.ReconnectRuntimeAsync(reconnectRequest, CancellationToken.None);
        var retry = await profileSession.ReconnectRuntimeAsync(reconnectRequest, CancellationToken.None);

        Assert.False(first.IsSuccessful);
        Assert.True(retry.IsSuccessful);
        Assert.Equal(prepared.Value.Runtime.Id, retry.Value!.Runtime.Id);
        Assert.Equal(1, binding.OpenCount);
        Assert.Equal(2, originalSession.RevalidateCount);
        Assert.Equal(0, originalSession.CloseCount);
    }

    [Fact]
    public async Task Siemens_plcsim_reconnect_failure_preserves_owned_runtime_cleanup_responsibility()
    {
        var runtime = CreateRuntimeCandidate() with { IsOwned = true };
        var originalSession = new RecordingPlcsimRuntimeSession(runtime);
        originalSession.EnqueueRevalidationResult(new PlcsimRuntimeObservationResult(
            PlcsimOperationStatus.Failed,
            null,
            [
                new PlcsimDiagnostic(
                    "FakePlcsim.RevalidationFailed",
                    PlcsimDiagnosticSeverity.Error,
                    "Runtime revalidation failed.",
                    "RevalidateRuntime",
                    null)
            ],
            Array.Empty<PlcsimEvidence>()));
        var binding = new RecordingPlcsimBinding(originalSession);
        var profileSession = new SiemensAccessProfileSession(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensSelectedBindings(
                new TestTiaBinding(),
                binding,
                new RecordingPlcsimCommunicationBinding(new RecordingPlcsimCommunicationSession())),
            ProjectOwnershipMode.ApplicationManaged,
            CreateResourceScope(),
            TimeProvider.System,
            new SiemensTestLoggerFactory().CreateLogger("test"));
        var prepareRequest = CreateRuntimePrepareRequest(
            @"D:\Projects\sample.apf",
            new AccessProfileConfigurationPayload(
                """
                {
                  "runtimeRef": { "instanceName": "Runtime 1" },
                  "cleanup": { "runtime": "release" }
                }
                """),
            runtimeTarget: null,
            provisioningMode: RuntimeProvisioningMode.CreateOwned);
        var prepared = await profileSession.PlcControl.PrepareRuntimeAsync(prepareRequest, CancellationToken.None);

        var reconnect = await profileSession.ReconnectRuntimeAsync(
            new PlcRuntimeReconnectRequest(
                prepareRequest.Configuration,
                RuntimeProvisioningMode.CreateOwned,
                prepared.Value!.Runtime,
                prepared.Value.RuntimeTarget),
            CancellationToken.None);
        var cleanup = await profileSession.PlcControl.ReleaseRuntimeAsync(
            CreateRuntimeReleaseRequest(prepared.Value!, RuntimeResourceReleaseMode.DestroyResource),
            CancellationToken.None);

        Assert.False(reconnect.IsSuccessful);
        Assert.True(cleanup.IsSuccessful);
        Assert.Equal(1, binding.OpenCount);
        Assert.Equal(1, originalSession.RevalidateCount);
        Assert.Equal(1, originalSession.ReleaseCount);
        Assert.Equal(PlcsimRuntimeCleanupPreference.Release, originalSession.LastReleaseRequest?.CleanupPreference);
    }

    [Fact]
    public async Task Siemens_plcsim_control_service_uses_selected_runtime_target_for_session_open_and_prepare()
    {
        var runtime = new PlcsimRuntimeTargetCandidate(
            new PlcsimRuntimeReference("selected-runtime", "Selected Runtime"),
            "fake-8",
            "fake-8",
            SiemensRuntimeLocality.Local,
            PlcsimRuntimeControlState.Stop,
            IsUsable: true,
            IsOwned: false);
        var runtimeSession = new RecordingPlcsimRuntimeSession(runtime);
        var binding = new RecordingPlcsimBinding(runtimeSession);
        var service = new SiemensPlcsimPlcControlService(binding, TimeProvider.System);
        var selectedRuntime = new RuntimeTargetRef(
            new RuntimeTargetId("selected-runtime"),
            "Selected Runtime",
            "Selected Runtime");

        var request = CreateRuntimePrepareRequest(
            @"D:\Projects\sample.ap21",
            new AccessProfileConfigurationPayload(
                """
                {
                  "runtimeRef": {
                    "id": "configured-runtime",
                    "instanceName": 17,
                    "storagePath": false,
                    "cpuType": "unsupported"
                  },
                  "cleanup": { "runtime": "release" }
                }
                """),
            selectedRuntime);

        var prepare = await service.PrepareRuntimeAsync(request, CancellationToken.None);
        var release = await service.ReleaseRuntimeAsync(
            CreateRuntimeReleaseRequest(prepare.Value!, RuntimeResourceReleaseMode.KeepResource),
            CancellationToken.None);

        Assert.True(prepare.IsSuccessful);
        Assert.True(release.IsSuccessful);
        Assert.Equal("selected-runtime", binding.LastOpenRequest?.RuntimeReference?.Id);
        Assert.Equal("Selected Runtime", binding.LastOpenRequest?.RuntimeReference?.InstanceName);
        Assert.Null(binding.LastOpenRequest?.StoragePreference.StoragePath);
        Assert.Equal(PlcsimRuntimeCleanupPreference.Keep, binding.LastOpenRequest?.CleanupPreference);
        Assert.Equal("selected-runtime", runtimeSession.LastPrepareRequest?.RuntimeReference?.Id);
        Assert.Equal("Selected Runtime", runtimeSession.LastPrepareRequest?.RuntimeReference?.InstanceName);
        Assert.Null(runtimeSession.LastPrepareRequest?.StoragePreference.StoragePath);
        Assert.Equal(PlcsimRuntimeCleanupPreference.Keep, runtimeSession.LastReleaseRequest?.CleanupPreference);
    }

    [Fact]
    public async Task Siemens_plcsim_control_service_publishes_plcsim_diagnostics()
    {
        var diagnostic = new PlcsimDiagnostic(
            "FakePlcsim.OpenFailed",
            PlcsimDiagnosticSeverity.Error,
            "Open failed.",
            "OpenRuntimeSession",
            "evidence-1");
        var binding = new RecordingPlcsimBinding(null, new PlcsimRuntimeSessionOpenResult(
            PlcsimOperationStatus.Failed,
            null,
            new[] { diagnostic },
            Array.Empty<PlcsimEvidence>()));
        var service = new SiemensPlcsimPlcControlService(binding, TimeProvider.System);
        var diagnostics = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);

        var result = await service.PrepareRuntimeAsync(
            CreateRuntimePrepareRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty),
            CancellationToken.None);

        Assert.False(result.IsSuccessful);
        var published = Assert.Single(diagnostics);
        Assert.Equal("FakePlcsim.OpenFailed", published.Code);
        Assert.Equal(DiagnosticSource.AccessProfile, published.Source);
        Assert.Equal(DiagnosticPhase.RuntimeProvisioning, published.Phase);
        Assert.Equal("Siemens.PlcsimAdvanced", published.Correlation.ServiceId);
    }

    [Fact]
    public async Task Siemens_plcsim_control_service_reports_close_failure_after_runtime_release()
    {
        var runtime = CreateRuntimeCandidate();
        var runtimeSession = new RecordingPlcsimRuntimeSession(
            runtime,
            closeException: new IOException("runtime close failed"));
        var binding = new RecordingPlcsimBinding(runtimeSession);
        var service = new SiemensPlcsimPlcControlService(binding, TimeProvider.System);
        var diagnostics = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        var request = CreateRuntimePrepareRequest(
            @"D:\Projects\sample.apf",
            new AccessProfileConfigurationPayload(
                """
                {
                  "runtimeRef": {
                    "id": "runtime-1",
                    "instanceName": "Runtime 1",
                    "storagePath": "%TEMP%\\PLCSIM.UnitTest\\runtime-state"
                  },
                  "cleanup": { "runtime": "release" }
                }
                """));

        var prepare = await service.PrepareRuntimeAsync(request, CancellationToken.None);
        var release = await service.ReleaseRuntimeAsync(
            CreateRuntimeReleaseRequest(prepare.Value!, RuntimeResourceReleaseMode.DestroyResource),
            CancellationToken.None);

        Assert.True(prepare.IsSuccessful);
        Assert.False(release.IsSuccessful);
        Assert.Equal(PlcRuntimeReleaseOutcome.ResourceDestroyed, release.Value);
        Assert.Contains("runtime close failed", release.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, runtimeSession.ReleaseCount);
        Assert.Equal(1, runtimeSession.CloseCount);
        Assert.Equal(AccessHealthState.Invalidated, service.Health.State);
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == "Siemens.PlcsimAdvanced.RuntimeCleanupFailed"
            && diagnostic.Category == DiagnosticCategory.Cleanup
            && diagnostic.Phase == DiagnosticPhase.Cleanup);
    }

    [Fact]
    public async Task Siemens_plcsim_control_service_ignores_runtime_state_events_during_intentional_release_and_close()
    {
        var runtimeSession = new RecordingPlcsimRuntimeSession(
            CreateRuntimeCandidate(),
            raiseInvalidRuntimeStateOnRelease: true,
            raiseInvalidRuntimeStateOnClose: true);
        var service = new SiemensPlcsimPlcControlService(
            new RecordingPlcsimBinding(runtimeSession),
            TimeProvider.System);
        var states = new List<PlcState>();
        service.PlcStateChangedEventHandler += (_, args) => states.Add(args.State);
        var request = CreateRuntimePrepareRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty);

        var prepare = await service.PrepareRuntimeAsync(request, CancellationToken.None);
        states.Clear();
        var release = await service.ReleaseRuntimeAsync(
            CreateRuntimeReleaseRequest(prepare.Value!, RuntimeResourceReleaseMode.KeepResource),
            CancellationToken.None);

        Assert.True(prepare.IsSuccessful);
        Assert.True(release.IsSuccessful);
        Assert.DoesNotContain(states, state => state.InstanceState == RuntimeInstanceState.Faulted);
        Assert.Equal(RuntimeInstanceState.Unavailable, service.CurrentState.InstanceState);
        Assert.Equal(PlcOperatingState.Unavailable, service.CurrentState.OperatingState);
        Assert.Equal(1, runtimeSession.CloseCount);
    }

    [Fact]
    public async Task Siemens_plcsim_control_service_ignores_stale_callbacks_after_release()
    {
        var runtimeSession = new RecordingPlcsimRuntimeSession(CreateRuntimeCandidate());
        var service = new SiemensPlcsimPlcControlService(
            new RecordingPlcsimBinding(runtimeSession),
            TimeProvider.System);
        var states = new List<PlcState>();
        var progress = new List<OperationProgress>();
        service.PlcStateChangedEventHandler += (_, args) => states.Add(args.State);
        service.ProgressChangedEventHandler += (_, args) => progress.Add(args.Progress);
        var request = CreateRuntimePrepareRequest(
            @"D:\Projects\sample.apf",
            AccessProfileConfigurationPayload.Empty);

        var prepare = await service.PrepareRuntimeAsync(request, CancellationToken.None);
        var release = await service.ReleaseRuntimeAsync(
            CreateRuntimeReleaseRequest(prepare.Value!, RuntimeResourceReleaseMode.KeepResource),
            CancellationToken.None);
        states.Clear();
        progress.Clear();
        runtimeSession.RaiseHealth(PlcsimRuntimeHealthState.Invalidated, "Late runtime invalidation.");
        runtimeSession.RaiseRuntimeState(
            PlcsimRuntimeStateChangeKind.ConnectionLost,
            PlcsimRuntimeControlState.Invalid,
            "Late runtime state.");
        runtimeSession.RaiseProgress("Late", "Callback");

        Assert.True(prepare.IsSuccessful);
        Assert.True(release.IsSuccessful);
        Assert.Equal(AccessHealthState.NotEstablished, service.Health.State);
        Assert.Empty(states);
        Assert.Empty(progress);
        Assert.Equal(RuntimeInstanceState.Unavailable, service.CurrentState.InstanceState);
        Assert.Equal(1, runtimeSession.CloseCount);
    }

    [Fact]
    public async Task Siemens_plcsim_control_service_publishes_start_failure_diagnostics()
    {
        var runtime = CreateRuntimeCandidate();
        var diagnostic = new PlcsimDiagnostic(
            "FakePlcsim.StartFailed",
            PlcsimDiagnosticSeverity.Error,
            "Start failed.",
            "StartRuntime",
            "evidence-start");
        var runtimeSession = new RecordingPlcsimRuntimeSession(
            runtime,
            startResult: new PlcsimRuntimeControlResult(
                PlcsimOperationStatus.Failed,
                PlcsimRuntimeControlState.Stop,
                new[] { diagnostic },
                Array.Empty<PlcsimEvidence>()));
        var service = new SiemensPlcsimPlcControlService(
            new RecordingPlcsimBinding(runtimeSession),
            TimeProvider.System);
        var diagnostics = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        var request = CreateRuntimePrepareRequest(
            @"D:\Projects\sample.apf",
            new AccessProfileConfigurationPayload(
                """
                {
                  "runtimeRef": {
                    "id": "runtime-1",
                    "instanceName": "Runtime 1"
                  }
                }
                """));

        var prepare = await service.PrepareRuntimeAsync(request, CancellationToken.None);
        var resolution = await service.ResolveRuntimeTargetAsync(prepare.Value!.Runtime, CancellationToken.None);
        var start = await service.StartAsync(prepare.Value!.Runtime, resolution.Value!.SelectedTarget!, CancellationToken.None);

        Assert.True(prepare.IsSuccessful);
        Assert.False(start.IsSuccessful);
        Assert.Equal(PlcOperatingState.Stopped, service.CurrentState.OperatingState);
        Assert.Contains(diagnostics, published =>
            published.Code == "FakePlcsim.StartFailed"
            && published.Phase == DiagnosticPhase.RuntimeProvisioning
            && published.Correlation.ServiceId == "Siemens.PlcsimAdvanced"
            && published.TechnicalDetail == "evidence-start");
    }

    [Fact]
    public async Task Siemens_plcsim_control_service_forwards_runtime_health_invalidation()
    {
        var runtimeSession = new RecordingPlcsimRuntimeSession(CreateRuntimeCandidate());
        var service = new SiemensPlcsimPlcControlService(
            new RecordingPlcsimBinding(runtimeSession),
            TimeProvider.System);
        var request = CreateRuntimePrepareRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty);

        var prepare = await service.PrepareRuntimeAsync(request, CancellationToken.None);
        runtimeSession.RaiseHealth(PlcsimRuntimeHealthState.Invalidated, "Runtime invalidated.");

        Assert.True(prepare.IsSuccessful);
        Assert.Equal(AccessHealthState.Invalidated, service.Health.State);
        Assert.Equal(AccessHealthProblem.ContextLost, service.Health.Problem);
        Assert.Equal("Runtime invalidated.", service.Health.Message);
    }

    [Fact]
    public async Task Siemens_plcsim_control_service_forwards_runtime_state_events()
    {
        var runtimeSession = new RecordingPlcsimRuntimeSession(CreateRuntimeCandidate());
        var service = new SiemensPlcsimPlcControlService(
            new RecordingPlcsimBinding(runtimeSession),
            TimeProvider.System);
        var request = CreateRuntimePrepareRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty);
        var states = new List<PlcState>();
        service.PlcStateChangedEventHandler += (_, args) => states.Add(args.State);

        var prepare = await service.PrepareRuntimeAsync(request, CancellationToken.None);
        states.Clear();
        runtimeSession.RaiseRuntimeState(PlcsimRuntimeStateChangeKind.OperatingStateChanged, PlcsimRuntimeControlState.Run);

        Assert.True(prepare.IsSuccessful);
        var state = Assert.Single(states);
        Assert.Equal(PlcOperatingState.Running, state.OperatingState);
        Assert.True(state.ReadyForExecution);
    }

    [Theory]
    [InlineData(PlcsimRuntimeControlState.Booting, RuntimeInstanceState.Starting, PlcOperatingState.Unavailable)]
    [InlineData(PlcsimRuntimeControlState.Startup, RuntimeInstanceState.Available, PlcOperatingState.Starting)]
    [InlineData(PlcsimRuntimeControlState.ShuttingDown, RuntimeInstanceState.Stopping, PlcOperatingState.Unavailable)]
    [InlineData(PlcsimRuntimeControlState.Freeze, RuntimeInstanceState.Available, PlcOperatingState.Frozen)]
    [InlineData(PlcsimRuntimeControlState.Hold, RuntimeInstanceState.Available, PlcOperatingState.Held)]
    public async Task Siemens_plcsim_control_service_maps_runtime_and_plc_state_events(
        PlcsimRuntimeControlState sourceState,
        RuntimeInstanceState expectedInstanceState,
        PlcOperatingState expectedOperatingState)
    {
        var runtimeSession = new RecordingPlcsimRuntimeSession(CreateRuntimeCandidate());
        var service = new SiemensPlcsimPlcControlService(
            new RecordingPlcsimBinding(runtimeSession),
            TimeProvider.System);
        var request = CreateRuntimePrepareRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty);
        var states = new List<PlcState>();
        service.PlcStateChangedEventHandler += (_, args) => states.Add(args.State);

        var prepare = await service.PrepareRuntimeAsync(request, CancellationToken.None);
        states.Clear();
        runtimeSession.RaiseRuntimeState(PlcsimRuntimeStateChangeKind.OperatingStateChanged, sourceState);

        Assert.True(prepare.IsSuccessful);
        var state = Assert.Single(states);
        Assert.Equal(expectedInstanceState, state.InstanceState);
        Assert.Equal(expectedOperatingState, state.OperatingState);
        Assert.False(state.ReadyForExecution);
    }

    [Fact]
    public async Task Siemens_plcsim_control_service_forwards_runtime_loss_as_faulted_instance_state()
    {
        var runtimeSession = new RecordingPlcsimRuntimeSession(CreateRuntimeCandidate());
        var service = new SiemensPlcsimPlcControlService(
            new RecordingPlcsimBinding(runtimeSession),
            TimeProvider.System);
        var request = CreateRuntimePrepareRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty);
        var states = new List<PlcState>();
        var diagnostics = new List<Diagnostic>();
        service.PlcStateChangedEventHandler += (_, args) => states.Add(args.State);
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);

        var prepare = await service.PrepareRuntimeAsync(request, CancellationToken.None);
        states.Clear();
        runtimeSession.RaiseRuntimeState(
            PlcsimRuntimeStateChangeKind.RuntimeManagerLost,
            PlcsimRuntimeControlState.Invalid,
            "Runtime manager was lost.");

        Assert.True(prepare.IsSuccessful);
        var state = Assert.Single(states);
        Assert.Equal(RuntimeInstanceState.Faulted, state.InstanceState);
        Assert.Equal(PlcOperatingState.Unknown, state.OperatingState);
        Assert.False(state.ReadyForExecution);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "FakePlcsim.RuntimeStateChanged");
    }

}
