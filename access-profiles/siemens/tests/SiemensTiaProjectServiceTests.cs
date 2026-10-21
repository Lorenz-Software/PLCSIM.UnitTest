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
    public async Task Siemens_tia_project_service_maps_project_operations_to_tia_session()
    {
        var target = new TiaProjectTargetRef(
            "plc-1",
            "PLC 1",
            "Devices/PLC_1",
            "S7-1500",
            "CPU 1516");
        var tiaSession = new RecordingTiaSession(target);
        var binding = new RecordingTiaBinding(tiaSession);
        var service = new SiemensTiaPlcProjectService(binding, TimeProvider.System);
        var projectEvents = new List<PlcProjectContext>();
        service.ProjectStateChangedEventHandler += (_, args) => projectEvents.Add(args.Project);

        var prepare = await service.PrepareProjectAsync(
            CreateProjectRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty),
            CancellationToken.None);
        var discovery = await service.DiscoverProjectTargetsAsync(
            CreateProjectRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty),
            CancellationToken.None);
        var resolution = await service.ResolveProjectTargetAsync(prepare.Value!, CancellationToken.None);
        var build = await service.BuildAsync(prepare.Value!, resolution.Value!.SelectedTarget!, CancellationToken.None);
        var transfer = await service.TransferToAsync(
            prepare.Value!,
            resolution.Value.SelectedTarget!,
            new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1", "PlcUnderTest"),
            CancellationToken.None);

        Assert.True(prepare.IsSuccessful);
        Assert.Equal(@"D:\Projects\sample.apf", binding.LastOpenRequest?.ProjectSource);
        Assert.Equal(TiaProjectOpenMode.DirectProject, binding.LastOpenRequest?.OpenMode);
        Assert.Single(projectEvents);
        Assert.True(discovery.IsSuccessful);
        Assert.Single(discovery.Value!.Candidates);
        Assert.Equal(TargetDiscoveryState.CandidatesAvailable, discovery.Value.State);
        Assert.True(resolution.IsSuccessful);
        Assert.Equal(TargetResolutionState.Resolved, resolution.Value.State);
        Assert.True(build.IsSuccessful);
        Assert.True(build.Value!.Succeeded);
        Assert.True(transfer.IsSuccessful);
        Assert.Equal("runtime-1", transfer.Value!.RuntimeTarget.Id.Value);
        Assert.Equal("plc-1", tiaSession.LastTransferRequest?.ProjectTarget.Id);
        Assert.Equal("runtime-1", tiaSession.LastTransferRequest?.RuntimeTarget.Id);
        Assert.Equal("Runtime 1", tiaSession.LastTransferRequest?.RuntimeTarget.DisplayName);
        Assert.Equal(SiemensBindingKinds.PlcSimAdvanced, tiaSession.LastTransferRequest?.RuntimeTarget.RuntimeKind);
        Assert.Equal(string.Empty, tiaSession.LastTransferRequest?.RuntimeTarget.RuntimeVersion);
        Assert.Equal(SiemensRuntimeLocality.Local, tiaSession.LastTransferRequest?.RuntimeTarget.Locality);
        Assert.Equal("PlcUnderTest", tiaSession.LastTransferRequest?.RuntimeTarget.RuntimeInstanceName);
        Assert.Equal(TiaTransferConnectionMode.PnIe, tiaSession.LastTransferRequest?.RuntimeTarget.ConnectionMode);
        Assert.Equal(TiaTransferPcInterface.PlcSim, tiaSession.LastTransferRequest?.RuntimeTarget.PcInterface);
        Assert.Equal(1, tiaSession.LastTransferRequest?.RuntimeTarget.PcInterfaceNumber);
        Assert.Null(tiaSession.LastTransferRequest?.RuntimeTarget.TargetInterfaceName);
    }

    [Fact]
    public async Task Siemens_tia_project_service_maps_supported_safety_authorization_without_vendor_leakage()
    {
        var target = new TiaProjectTargetRef(
            "plc-1",
            "PLC 1",
            "Devices/PLC_1",
            "S7-1500",
            "CPU 1516F");
        var tiaSession = new RecordingTiaSession(target)
        {
            SafetyRequirementResult = new TiaSafetyProgramAuthorizationRequirementResult(
                TiaOperationStatus.Succeeded,
                TiaSafetyProgramAuthorizationRequirement.PasswordRequired,
                Array.Empty<TiaDiagnostic>(),
                Array.Empty<TiaEvidence>())
        };
        var service = new SiemensTiaPlcProjectService(
            new RecordingTiaBinding(tiaSession),
            TimeProvider.System);
        var prepare = await service.PrepareProjectAsync(
            CreateProjectRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty),
            CancellationToken.None);
        var resolution = await service.ResolveProjectTargetAsync(prepare.Value!, CancellationToken.None);
        using var password = new System.Security.SecureString();
        password.AppendChar('x');
        password.MakeReadOnly();

        var requirement = await service.GetSafetyProgramAuthorizationRequirementAsync(
            prepare.Value!,
            resolution.Value!.SelectedTarget!,
            CancellationToken.None);
        var authorization = await service.AuthorizeSafetyProgramAsync(
            prepare.Value!,
            resolution.Value.SelectedTarget!,
            password,
            CancellationToken.None);

        Assert.True(requirement.IsSuccessful);
        Assert.Equal(
            SafetyProgramAuthorizationRequirement.PasswordRequired,
            requirement.Value?.Requirement);
        Assert.True(authorization.IsSuccessful);
        Assert.True(authorization.Value?.Authorized);
        Assert.Equal(1, tiaSession.SafetyRequirementCount);
        Assert.Equal(1, tiaSession.SafetyAuthorizationCount);
    }

    [Fact]
    public async Task Siemens_tia_project_service_preserves_binding_owned_safety_not_supported_result()
    {
        var target = new TiaProjectTargetRef(
            "plc-1",
            "PLC 1",
            "Devices/PLC_1",
            "S7-1500",
            "CPU 1516F");
        var bindingDiagnostic = new TiaDiagnostic(
            "TiaOpenness.Test.SafetyProgramNotSupported",
            TiaDiagnosticSeverity.Error,
            "The selected binding cannot transfer Safety programs.",
            "SafetyAuthorizationRequirement",
            null);
        var tiaSession = new RecordingTiaSession(target)
        {
            SafetyRequirementResult = new TiaSafetyProgramAuthorizationRequirementResult(
                TiaOperationStatus.NotSupported,
                TiaSafetyProgramAuthorizationRequirement.NotRequired,
                new[] { bindingDiagnostic },
                Array.Empty<TiaEvidence>())
        };
        var service = new SiemensTiaPlcProjectService(
            new RecordingTiaBinding(tiaSession),
            TimeProvider.System);
        var diagnostics = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        var prepare = await service.PrepareProjectAsync(
            CreateProjectRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty),
            CancellationToken.None);
        var resolution = await service.ResolveProjectTargetAsync(prepare.Value!, CancellationToken.None);

        var requirement = await service.GetSafetyProgramAuthorizationRequirementAsync(
            prepare.Value!,
            resolution.Value!.SelectedTarget!,
            CancellationToken.None);

        Assert.Equal(OperationResultState.NotSupported, requirement.State);
        Assert.Contains("selected TIA Openness binding", requirement.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == bindingDiagnostic.Code);
        Assert.Equal(1, tiaSession.SafetyRequirementCount);
        Assert.Equal(0, tiaSession.SafetyAuthorizationCount);
    }

    [Fact]
    public async Task Siemens_tia_project_service_uses_fake_binding_safety_authorization_boundary()
    {
        var projectPath = Path.Combine(
            FindRepositoryRoot(),
            "access-profiles",
            "siemens",
            "tia-openness",
            "fake",
            "tests",
            "Resources",
            "FakeProjects",
            "siemens-fake-project.apf");
        var service = new SiemensTiaPlcProjectService(
            new FakeTiaOpennessBinding(),
            TimeProvider.System);
        var prepare = await service.PrepareProjectAsync(
            CreateProjectRequest(projectPath, AccessProfileConfigurationPayload.Empty),
            CancellationToken.None);
        var resolution = await service.ResolveProjectTargetAsync(
            prepare.Value!,
            CancellationToken.None);

        var requirement = await service.GetSafetyProgramAuthorizationRequirementAsync(
            prepare.Value!,
            resolution.Value!.SelectedTarget!,
            CancellationToken.None);

        Assert.True(requirement.IsSuccessful);
        Assert.Equal(
            SafetyProgramAuthorizationRequirement.NotRequired,
            requirement.Value?.Requirement);
    }

    [Fact]
    public async Task Siemens_tia_project_service_uses_binding_declared_archive_open_mode()
    {
        var target = new TiaProjectTargetRef(
            "plc-1",
            "PLC 1",
            "Project/PLC_1",
            "S7-1500",
            "CPU 1516F-3 PN/DP");
        var binding = new RecordingTiaBinding(new RecordingTiaSession(target));
        var service = new SiemensTiaPlcProjectService(binding, TimeProvider.System);
        var archiveWorkDirectory = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "archive-work");
        var configuration = new AccessProfileConfigurationPayload(
            $$"""
            {
              "tia": {
                "archiveWorkDirectory": "{{archiveWorkDirectory.Replace("\\", "\\\\")}}"
              }
            }
            """);

        var result = await service.PrepareProjectAsync(
            CreateProjectRequest(@"D:\Projects\sample.zap21", configuration),
            CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal(TiaProjectOpenMode.RetrievedArchive, binding.LastOpenRequest?.OpenMode);
        Assert.Equal(Path.GetFullPath(archiveWorkDirectory), binding.LastOpenRequest?.ArchiveWorkDirectory);
    }

    [Fact]
    public async Task Siemens_tia_project_service_ignores_archive_workspace_for_direct_project()
    {
        var target = new TiaProjectTargetRef(
            "plc-1",
            "PLC 1",
            "Project/PLC_1",
            "S7-1500",
            "CPU 1516F-3 PN/DP");
        var binding = new RecordingTiaBinding(new RecordingTiaSession(target));
        var service = new SiemensTiaPlcProjectService(binding, TimeProvider.System);
        var configuration = new AccessProfileConfigurationPayload(
            """
            {
              "tia": {
                "archiveWorkDirectory": "%TEMP%\\PLCSIM.UnitTest\\unused-archive-work"
              }
            }
            """);

        var result = await service.PrepareProjectAsync(
            CreateProjectRequest(@"D:\Projects\sample.ap21", configuration),
            CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal(TiaProjectOpenMode.DirectProject, binding.LastOpenRequest?.OpenMode);
        Assert.Null(binding.LastOpenRequest?.ArchiveWorkDirectory);
    }

    [Fact]
    public async Task Siemens_tia_project_service_applies_current_plc_reference_to_retained_session()
    {
        var target = new TiaProjectTargetRef(
            "plc-1",
            "PLC 1",
            "Project/PLC_1",
            "S7-1500",
            "CPU 1516F-3 PN/DP");
        var tiaSession = new RecordingTiaSession(target);
        var binding = new RecordingTiaBinding(tiaSession);
        var service = new SiemensTiaPlcProjectService(binding, TimeProvider.System);

        var discovery = await service.DiscoverProjectTargetsAsync(
            CreateProjectRequest(
                @"D:\Projects\sample.apf",
                new AccessProfileConfigurationPayload("""{ "plcRef": { "id": "discovery-plc" } }""")),
            CancellationToken.None);
        Assert.Null(tiaSession.LastDiscoveryRequest!.PlcRefId);
        var prepare = await service.PrepareProjectAsync(
            CreateProjectRequest(
                @"D:\Projects\sample.apf",
                new AccessProfileConfigurationPayload("""{ "plcRef": { "id": "prepare-plc" } }""")),
            CancellationToken.None);

        Assert.True(discovery.IsSuccessful);
        Assert.True(prepare.IsSuccessful);
        Assert.Equal(1, binding.OpenCallCount);
        Assert.Equal("prepare-plc", tiaSession.LastDiscoveryRequest?.PlcRefId);
    }

    [Fact]
    public async Task Siemens_tia_project_service_reopens_project_after_release()
    {
        var target = new TiaProjectTargetRef(
            "plc-1",
            "PLC 1",
            "Project/PLC_1",
            "S7-1500",
            "CPU 1516F-3 PN/DP");
        var tiaSession = new RecordingTiaSession(target);
        var binding = new RecordingTiaBinding(tiaSession);
        var service = new SiemensTiaPlcProjectService(binding, TimeProvider.System);
        var request = CreateProjectRequest(
            @"D:\Projects\sample.apf",
            AccessProfileConfigurationPayload.Empty);

        var firstPrepare = await service.PrepareProjectAsync(request, CancellationToken.None);
        var release = await service.ReleaseProjectAsync(CancellationToken.None);
        var secondPrepare = await service.PrepareProjectAsync(request, CancellationToken.None);

        Assert.True(firstPrepare.IsSuccessful);
        Assert.True(release.IsSuccessful);
        Assert.True(secondPrepare.IsSuccessful);
        Assert.Equal(2, binding.OpenCallCount);
        Assert.Equal(1, tiaSession.CloseCount);
        Assert.NotNull(service.CurrentProject);
        Assert.Equal(AccessHealthState.Healthy, service.Health.State);
    }

    [Fact]
    public async Task Siemens_tia_project_service_ignores_stale_callbacks_after_release()
    {
        var target = new TiaProjectTargetRef(
            "plc-1",
            "PLC 1",
            "PLC_1",
            "S7-1500",
            "CPU 1516");
        var tiaSession = new RecordingTiaSession(target);
        var service = new SiemensTiaPlcProjectService(
            new RecordingTiaBinding(tiaSession),
            TimeProvider.System);
        var progress = new List<OperationProgress>();
        service.ProgressChangedEventHandler += (_, args) => progress.Add(args.Progress);
        var request = CreateProjectRequest(
            @"D:\Projects\sample.apf",
            AccessProfileConfigurationPayload.Empty);

        var prepare = await service.PrepareProjectAsync(request, CancellationToken.None);
        var release = await service.ReleaseProjectAsync(CancellationToken.None);
        progress.Clear();
        tiaSession.RaiseHealth(TiaProjectHealthState.Invalidated, "Late project invalidation.");
        tiaSession.RaiseProgress("Late", "Callback");

        Assert.True(prepare.IsSuccessful);
        Assert.True(release.IsSuccessful);
        Assert.Equal(AccessHealthState.NotEstablished, service.Health.State);
        Assert.Empty(progress);
        Assert.Null(service.CurrentProject);
        Assert.Equal(1, tiaSession.CloseCount);
    }

    [Fact]
    public async Task Siemens_tia_project_service_publishes_tia_diagnostics()
    {
        var diagnostic = new TiaDiagnostic(
            "FakeTia.OpenFailed",
            TiaDiagnosticSeverity.Error,
            "Open failed.",
            "OpenProject",
            "evidence-1");
        var binding = new RecordingTiaBinding(null, new TiaEngineeringSessionOpenResult(
            TiaOperationStatus.Failed,
            null,
            new[] { diagnostic },
            Array.Empty<TiaEvidence>()));
        var service = new SiemensTiaPlcProjectService(binding, TimeProvider.System);
        var diagnostics = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);

        var result = await service.PrepareProjectAsync(
            CreateProjectRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty),
            CancellationToken.None);

        Assert.False(result.IsSuccessful);
        var published = Assert.Single(diagnostics);
        Assert.Equal("FakeTia.OpenFailed", published.Code);
        Assert.Equal(DiagnosticSource.AccessProfile, published.Source);
        Assert.Equal(DiagnosticPhase.ProjectPreparation, published.Phase);
        Assert.Equal("Siemens.Tia", published.Correlation.ServiceId);
    }

    [Fact]
    public async Task Siemens_tia_project_discovery_returns_diagnostics_without_event_republishing()
    {
        var target = new TiaProjectTargetRef(
            "plc-1",
            "PLC 1",
            "Project/PLC_1",
            "S7-1500",
            "CPU 1516F-3 PN/DP");
        var tiaDiagnostic = new TiaDiagnostic(
            "TiaOpenness.V21.TargetNotice",
            TiaDiagnosticSeverity.Warning,
            "Target discovery notice.",
            "DiscoverTargets",
            "target-evidence");
        var service = new SiemensTiaPlcProjectService(
            new RecordingTiaBinding(
                new RecordingTiaSession(
                    target,
                    discoveryDiagnostics: new[] { tiaDiagnostic })),
            TimeProvider.System);
        var published = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => published.Add(args.Diagnostic);
        var request = CreateProjectRequest(
            @"D:\Projects\sample.apf",
            AccessProfileConfigurationPayload.Empty);

        var result = await service.DiscoverProjectTargetsAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccessful);
        var diagnostic = Assert.Single(result.Value!.Diagnostics);
        Assert.Equal(tiaDiagnostic.Code, diagnostic.Code);
        Assert.Equal("Siemens.Tia", diagnostic.Correlation.ServiceId);
        Assert.Equal(tiaDiagnostic.EvidenceId, diagnostic.TechnicalDetail);
        Assert.Empty(published);
    }

    [Fact]
    public async Task Siemens_tia_project_discovery_publishes_failed_diagnostics_once()
    {
        var tiaDiagnostic = new TiaDiagnostic(
            "TiaOpenness.V21.TargetFailed",
            TiaDiagnosticSeverity.Error,
            "Target discovery failed.",
            "DiscoverTargets",
            "target-failure-evidence");
        var service = new SiemensTiaPlcProjectService(
            new RecordingTiaBinding(
                new RecordingTiaSession(
                    new TiaProjectTargetRef(
                        "plc-1",
                        "PLC 1",
                        "Project/PLC_1",
                        "S7-1500",
                        "CPU 1516F-3 PN/DP"),
                    discoveryDiagnostics: new[] { tiaDiagnostic },
                    discoveryStatus: TiaOperationStatus.Failed)),
            TimeProvider.System);
        var published = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => published.Add(args.Diagnostic);

        var result = await service.DiscoverProjectTargetsAsync(
            CreateProjectRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty),
            CancellationToken.None);

        Assert.False(result.IsSuccessful);
        var diagnostic = Assert.Single(published);
        Assert.Equal(tiaDiagnostic.Code, diagnostic.Code);
        Assert.Equal("Siemens.Tia", diagnostic.Correlation.ServiceId);
        Assert.Equal(tiaDiagnostic.EvidenceId, diagnostic.TechnicalDetail);
    }

    [Theory]
    [InlineData("V15_1")]
    [InlineData("V17")]
    [InlineData("V18")]
    [InlineData("V19")]
    [InlineData("V20")]
    [InlineData("V21")]
    public async Task Siemens_tia_project_discovery_returns_multiple_candidates_for_interactive_selection(string version)
    {
        var targets = new[]
        {
            new TiaProjectTargetRef("plc-1", "PLC 1", "Project/PLC_1", "S7-1500", "CPU 1516"),
            new TiaProjectTargetRef("plc-2", "PLC 2", "Project/PLC_2", "S7-1500", "CPU 1516F")
        };
        var ambiguity = new TiaDiagnostic(
            $"TiaOpenness.{version}.TargetAmbiguous",
            TiaDiagnosticSeverity.Error,
            "Multiple supported PLC targets require application selection.",
            "DiscoverTargets",
            "target-discovery");
        var service = new SiemensTiaPlcProjectService(
            new RecordingTiaBinding(new RecordingTiaSession(
                targets,
                discoveryDiagnostics: new[] { ambiguity },
                discoveryStatus: TiaOperationStatus.Failed)),
            TimeProvider.System);
        var published = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => published.Add(args.Diagnostic);
        var request = CreateProjectRequest(
            @"D:\Projects\sample.ap21",
            AccessProfileConfigurationPayload.Empty);

        var result = await service.DiscoverProjectTargetsAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal(TargetDiscoveryState.CandidatesAvailable, result.Value!.State);
        Assert.Collection(
            result.Value.Candidates,
            target => Assert.Equal("plc-1", target.Id.Value),
            target => Assert.Equal("plc-2", target.Id.Value));
        Assert.Empty(result.Value.Diagnostics);
        Assert.Empty(published);

        var prepareWithoutSelection = await service.PrepareProjectAsync(request, CancellationToken.None);

        Assert.False(prepareWithoutSelection.IsSuccessful);
        Assert.Contains(published, diagnostic => diagnostic.Code == ambiguity.Code);
    }

    [Fact]
    public async Task Siemens_tia_project_discovery_does_not_apply_configured_selection()
    {
        var targets = new[]
        {
            new TiaProjectTargetRef("plc-1", "PLC 1", "Project/PLC_1", "S7-1500", "CPU 1516"),
            new TiaProjectTargetRef("plc-2", "PLC 2", "Project/PLC_2", "S7-1500", "CPU 1516F")
        };
        var ambiguity = new TiaDiagnostic(
            "TiaOpenness.V21.TargetAmbiguous",
            TiaDiagnosticSeverity.Error,
            "The configured PLC target is ambiguous.",
            "DiscoverTargets",
            "target-discovery");
        var session = new RecordingTiaSession(
            targets,
            discoveryDiagnostics: new[] { ambiguity },
            discoveryStatus: TiaOperationStatus.Failed);
        var service = new SiemensTiaPlcProjectService(
            new RecordingTiaBinding(session),
            TimeProvider.System);

        var result = await service.DiscoverProjectTargetsAsync(
            CreateProjectRequest(
                @"D:\Projects\sample.ap21",
                new AccessProfileConfigurationPayload("""{ "plcRef": { "id": "plc-1" } }""")),
            CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal(2, result.Value!.Candidates.Count);
        Assert.Null(session.LastDiscoveryRequest!.PlcRefId);
    }

    [Theory]
    [InlineData("plc-1")]
    [InlineData("missing-plc")]
    public async Task Siemens_tia_discovery_defers_configured_id_validation_to_application(string configuredId)
    {
        var target = new TiaProjectTargetRef("plc-1", "PLC 1", "Project/PLC_1", "S7-1500", "CPU 1516");
        var session = new RecordingTiaSession(target);
        var service = new SiemensTiaPlcProjectService(new RecordingTiaBinding(session), TimeProvider.System);

        var result = await service.DiscoverProjectTargetsAsync(
            CreateProjectRequest(@"D:\Projects\sample.ap21",
                new AccessProfileConfigurationPayload(JsonSerializer.Serialize(new { plcRef = new { id = configuredId } }))),
            CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal("plc-1", Assert.Single(result.Value!.Candidates).Id.Value);
        Assert.NotNull(session.LastDiscoveryRequest);
        Assert.Null(session.LastDiscoveryRequest.PlcRefId);
    }

    [Theory]
    [InlineData("V15_1")]
    [InlineData("V17")]
    [InlineData("V18")]
    [InlineData("V19")]
    [InlineData("V20")]
    [InlineData("V21")]
    public async Task Siemens_tia_discovery_with_no_supported_targets_is_successful_empty_discovery(string version)
    {
        var diagnostic = new TiaDiagnostic($"TiaOpenness.{version}.TargetNotFound",
            TiaDiagnosticSeverity.Error, "No supported PLC targets.", "DiscoverTargets", "target-discovery");
        var session = new RecordingTiaSession(Array.Empty<TiaProjectTargetRef>(),
            discoveryDiagnostics: new[] { diagnostic }, discoveryStatus: TiaOperationStatus.Failed);
        var service = new SiemensTiaPlcProjectService(new RecordingTiaBinding(session), TimeProvider.System);
        var published = new List<Diagnostic>();
        service.DiagnosticEventHandler += (_, args) => published.Add(args.Diagnostic);
        var request = CreateProjectRequest(@"D:\Projects\sample.ap21",
            new AccessProfileConfigurationPayload("""{ "plcRef": { "id": "missing-plc" } }"""));

        var result = await service.DiscoverProjectTargetsAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal(TargetDiscoveryState.NoCandidates, result.Value!.State);
        Assert.Empty(result.Value.Candidates);
        Assert.Empty(result.Value.Diagnostics);
        Assert.Empty(published);
        Assert.Null(session.LastDiscoveryRequest!.PlcRefId);

        var prepare = await service.PrepareProjectAsync(request, CancellationToken.None);
        Assert.False(prepare.IsSuccessful);
        Assert.Equal("missing-plc", session.LastDiscoveryRequest!.PlcRefId);
        Assert.Single(published);
        var resolution = await service.ResolveProjectTargetAsync(service.CurrentProject!, CancellationToken.None);
        Assert.False(resolution.IsSuccessful);
    }

    [Theory]
    [InlineData(TiaOperationStatus.Invalidated)]
    [InlineData(TiaOperationStatus.Cancelled)]
    [InlineData(TiaOperationStatus.TimedOut)]
    [InlineData(TiaOperationStatus.NotSupported)]
    [InlineData(TiaOperationStatus.Unclear)]
    public async Task Siemens_tia_discovery_does_not_hide_non_failure_statuses(TiaOperationStatus status)
    {
        var session = new RecordingTiaSession(Array.Empty<TiaProjectTargetRef>(),
            discoveryDiagnostics: new[] {
                new TiaDiagnostic("TiaOpenness.V21.TargetNotFound", TiaDiagnosticSeverity.Error,
                    "No target.", "DiscoverTargets", "target-discovery") },
            discoveryStatus: status);
        var service = new SiemensTiaPlcProjectService(new RecordingTiaBinding(session), TimeProvider.System);

        var result = await service.DiscoverProjectTargetsAsync(
            CreateProjectRequest(@"D:\Projects\sample.ap21", AccessProfileConfigurationPayload.Empty),
            CancellationToken.None);

        Assert.False(result.IsSuccessful);
    }

    [Theory]
    [InlineData(0, "DiscoverTargets", true)]
    [InlineData(0, "Open", false)]
    [InlineData(1, "DiscoverTargets", false)]
    [InlineData(2, "DiscoverTargets", true)]
    public async Task Siemens_tia_discovery_does_not_hide_real_failures_or_inconsistent_selection_errors(
        int targetCount, string phase, bool includeWorkerFailure)
    {
        var targets = Enumerable.Range(0, targetCount)
            .Select(index => new TiaProjectTargetRef($"plc-{index}", "PLC", "Project/PLC", "S7-1500", "CPU 1516"))
            .ToArray();
        var diagnostics = new List<TiaDiagnostic> {
            new("TiaOpenness.V21." + (targetCount == 0 ? "TargetNotFound" : "TargetAmbiguous"),
                TiaDiagnosticSeverity.Error, "Target selection failed.", phase, "target-discovery") };
        if (includeWorkerFailure)
        {
            diagnostics.Add(new TiaDiagnostic("TiaOpenness.V21.WorkerFailed", TiaDiagnosticSeverity.Error,
                "Worker failed.", "DiscoverTargets", "worker-failure"));
        }
        var service = new SiemensTiaPlcProjectService(new RecordingTiaBinding(new RecordingTiaSession(
            targets, discoveryDiagnostics: diagnostics, discoveryStatus: TiaOperationStatus.Failed)), TimeProvider.System);

        var result = await service.DiscoverProjectTargetsAsync(
            CreateProjectRequest(@"D:\Projects\sample.ap21", AccessProfileConfigurationPayload.Empty),
            CancellationToken.None);

        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task Siemens_tia_project_service_maps_close_failure_to_operation_result_and_health()
    {
        var target = new TiaProjectTargetRef(
            "plc-1",
            "PLC 1",
            "Project/PLC_1",
            "S7-1500",
            "CPU 1516F-3 PN/DP");
        var service = new SiemensTiaPlcProjectService(
            new RecordingTiaBinding(new RecordingTiaSession(target, new IOException("close failed"))),
            TimeProvider.System);

        var prepare = await service.PrepareProjectAsync(
            CreateProjectRequest(@"D:\Projects\sample.apf", AccessProfileConfigurationPayload.Empty),
            CancellationToken.None);
        var release = await service.ReleaseProjectAsync(CancellationToken.None);

        Assert.True(prepare.IsSuccessful);
        Assert.False(release.IsSuccessful);
        Assert.Contains("close failed", release.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AccessHealthState.Invalidated, service.Health.State);
    }

}
