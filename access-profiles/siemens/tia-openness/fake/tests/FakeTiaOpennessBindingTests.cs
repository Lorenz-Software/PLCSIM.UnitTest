using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.Fake;
using Microsoft.Extensions.Time.Testing;
using System.IO;
using System.Security;
using System.Text.Json;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.Fake;

public sealed class FakeTiaOpennessBindingTests
{
    [Fact]
    public async Task Fake_session_defaults_safety_authorization_to_not_required()
    {
        var projectPath = WriteFakeProject();
        var open = await new FakeTiaOpennessBinding().OpenProjectAsync(
            new TiaProjectOpenRequest(projectPath, TiaProjectOpenMode.DirectProject, null, null),
            CancellationToken.None);
        var session = Assert.IsAssignableFrom<ITiaSafetyProgramAuthorizationSession>(open.Session);
        var discovery = await open.Session!.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("configured-plc"),
            CancellationToken.None);

        var result = await session.GetSafetyProgramAuthorizationRequirementAsync(
            discovery.SelectedTarget!,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(TiaSafetyProgramAuthorizationRequirement.NotRequired, result.Requirement);
    }

    [Fact]
    public async Task Fake_session_uses_injected_safety_authorization_results()
    {
        var requirement = new TiaSafetyProgramAuthorizationRequirementResult(
            TiaOperationStatus.Succeeded,
            TiaSafetyProgramAuthorizationRequirement.PasswordRequired,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>());
        var authorization = new TiaSafetyProgramAuthorizationResult(
            TiaOperationStatus.Failed,
            false,
            new[]
            {
                new TiaDiagnostic(
                    "FakeTia.SafetyAuthorizationRejected",
                    TiaDiagnosticSeverity.Error,
                    "Injected Safety authorization rejection.",
                    "SafetyAuthorization",
                    null)
            },
            Array.Empty<TiaEvidence>());
        var projectPath = WriteFakeProject();
        var open = await new FakeTiaOpennessBinding(Behavior(
            safetyAuthorizationRequirement: requirement,
            safetyAuthorization: authorization)).OpenProjectAsync(
            new TiaProjectOpenRequest(projectPath, TiaProjectOpenMode.DirectProject, null, null),
            CancellationToken.None);
        var session = Assert.IsAssignableFrom<ITiaSafetyProgramAuthorizationSession>(open.Session);
        var discovery = await open.Session!.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("configured-plc"),
            CancellationToken.None);
        using var password = new SecureString();
        password.AppendChar('x');

        var requirementResult = await session.GetSafetyProgramAuthorizationRequirementAsync(
            discovery.SelectedTarget!,
            CancellationToken.None);
        var authorizationResult = await session.AuthorizeSafetyProgramAsync(
            discovery.SelectedTarget!,
            password,
            CancellationToken.None);

        Assert.Same(requirement, requirementResult);
        Assert.Same(authorization, authorizationResult);
    }

    [Fact]
    public async Task Fake_binding_executes_successful_session_flow()
    {
        var binding = new FakeTiaOpennessBinding();

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                GetSiemensFakeProjectPath(),
                TiaProjectOpenMode.DirectProject,
                null,
                "test"),
            CancellationToken.None);

        Assert.True(open.Succeeded);
        Assert.NotNull(open.Session);
        Assert.Equal(SiemensBindingKinds.TiaOpenness, binding.Info.BindingKind);
        Assert.Single(
            binding.Declaration.ProjectSources,
            source => source.Kind == SiemensProjectSourceKinds.SiemensFakeProject && source.Extension == ".apf");

        var progress = new List<TiaProgressSnapshot>();
        var health = new List<TiaProjectHealthChangedEventArgs>();
        open.Session.ProgressChanged += (_, snapshot) => progress.Add(snapshot);
        open.Session.HealthChanged += (_, args) => health.Add(args);

        var discovery = await open.Session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(null),
            CancellationToken.None);

        Assert.True(discovery.Succeeded);
        var target = Assert.Single(discovery.Targets);
        Assert.Equal(target, discovery.SelectedTarget);
        Assert.Equal("siemens-fake-plc", target.Id);

        var build = await open.Session.BuildAsync(
            new TiaBuildRequest(target),
            CancellationToken.None);

        Assert.True(build.Succeeded);
        Assert.Equal(0, build.ErrorCount);

        var transfer = await open.Session.TransferAsync(
            new TiaTransferRequest(
                target,
                CreateFakeRuntimeTarget()),
            CancellationToken.None);

        Assert.True(transfer.Succeeded);

        await open.Session.CloseAsync(CancellationToken.None);

        Assert.Contains(progress, item => item.Operation == "DiscoverTargets");
        Assert.Contains(progress, item => item.Operation == "Build");
        Assert.Contains(progress, item => item.Operation == "Transfer");
        Assert.Single(health, item => item.Current == TiaProjectHealthState.Disposed);
        Assert.Equal(TiaProjectHealthState.Disposed, open.Session.Health);
    }

    [Fact]
    public async Task Fake_binding_reports_missing_target_without_selecting_fallback()
    {
        var binding = new FakeTiaOpennessBinding();
        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                GetSiemensFakeProjectPath(),
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.NotNull(open.Session);

        var discovery = await open.Session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("missing-plc"),
            CancellationToken.None);

        Assert.False(discovery.Succeeded);
        Assert.Equal(TiaOperationStatus.Failed, discovery.Status);
        Assert.Null(discovery.SelectedTarget);
        Assert.Contains(discovery.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.TargetNotFound");
    }

    [Fact]
    public async Task Fake_binding_rejects_unsupported_open_mode_without_session()
    {
        var binding = new FakeTiaOpennessBinding();

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                GetSiemensFakeProjectPath(),
                TiaProjectOpenMode.RetrievedArchive,
                null,
                null),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Null(open.Session);
        Assert.Equal(TiaOperationStatus.Failed, open.Status);
        Assert.Contains(open.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.ProjectOpenModeUnsupported");
    }

    [Fact]
    public async Task Fake_binding_retrieves_archive_into_owned_workspace_and_cleans_it_on_close()
    {
        var workspaceRoot = CreateTemporaryDirectory("fake-tia-archive-workspace");
        var binding = new FakeTiaOpennessBinding();

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                GetSiemensFakeArchivePath(),
                TiaProjectOpenMode.RetrievedArchive,
                workspaceRoot,
                null),
            CancellationToken.None);

        Assert.True(open.Succeeded);
        Assert.NotNull(open.Session);
        Assert.Equal(GetSiemensFakeArchivePath(), open.Session.Project.ProjectSource);
        Assert.Single(Directory.GetDirectories(workspaceRoot));

        await open.Session.CloseAsync(CancellationToken.None);

        Assert.Empty(Directory.GetDirectories(workspaceRoot));
    }

    [Fact]
    public async Task Fake_binding_cleans_archive_workspace_when_open_fails()
    {
        var workspaceRoot = CreateTemporaryDirectory("fake-tia-archive-open-failure");
        var binding = new FakeTiaOpennessBinding(
            Behavior(open: Scenario(TiaOperationStatus.Failed)));

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                GetSiemensFakeArchivePath(),
                TiaProjectOpenMode.RetrievedArchive,
                workspaceRoot,
                null),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Null(open.Session);
        Assert.Empty(Directory.GetDirectories(workspaceRoot));
    }

    [Fact]
    public async Task Fake_binding_cleans_archive_workspace_when_open_is_cancelled()
    {
        var workspaceRoot = CreateTemporaryDirectory("fake-tia-archive-cancel");
        var binding = new FakeTiaOpennessBinding(
            Behavior(open: Scenario(TiaOperationStatus.Succeeded, delayMs: 5000)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            binding.OpenProjectAsync(
                new TiaProjectOpenRequest(
                    GetSiemensFakeArchivePath(),
                    TiaProjectOpenMode.RetrievedArchive,
                    workspaceRoot,
                    null),
                cancellation.Token));

        Assert.Empty(Directory.GetDirectories(workspaceRoot));
    }

    [Fact]
    public async Task Fake_binding_preserves_open_failure_when_archive_cleanup_also_fails()
    {
        var workspaceRoot = CreateTemporaryDirectory("fake-tia-archive-open-cleanup-failure");
        var binding = new FakeTiaOpennessBinding(
            Behavior(
                open: Scenario(
                    TiaOperationStatus.Failed,
                    diagnosticCode: "FakeTia.OpenRejected"),
                archiveCleanupFailureMessage: "Injected archive cleanup failure."));

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                GetSiemensFakeArchivePath(),
                TiaProjectOpenMode.RetrievedArchive,
                workspaceRoot,
                null),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Null(open.Session);
        Assert.Contains(open.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.OpenRejected");
        Assert.Contains(open.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.ArchiveWorkspaceCleanupFailed");
        Assert.Contains(open.Evidence, evidence => evidence.Kind == "ArchiveWorkspaceCleanupFailure");
        Assert.Single(Directory.GetDirectories(workspaceRoot));

        Directory.Delete(workspaceRoot, recursive: true);
    }

    [Fact]
    public async Task Fake_binding_preserves_cancellation_when_archive_cleanup_also_fails()
    {
        var workspaceRoot = CreateTemporaryDirectory("fake-tia-archive-cancel-cleanup-failure");
        var binding = new FakeTiaOpennessBinding(
            Behavior(
                open: Scenario(TiaOperationStatus.Succeeded, delayMs: 5000),
                archiveCleanupFailureMessage: "Injected archive cleanup failure."));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            binding.OpenProjectAsync(
                new TiaProjectOpenRequest(
                    GetSiemensFakeArchivePath(),
                    TiaProjectOpenMode.RetrievedArchive,
                    workspaceRoot,
                    null),
                cancellation.Token));

        Assert.Equal(
            "Injected archive cleanup failure.",
            exception.Data["FakeTia.ArchiveWorkspaceCleanupFailed"]);
        Assert.Single(Directory.GetDirectories(workspaceRoot));

        Directory.Delete(workspaceRoot, recursive: true);
    }

    [Fact]
    public async Task Fake_binding_closes_session_truthfully_when_archive_cleanup_fails()
    {
        var workspaceRoot = CreateTemporaryDirectory("fake-tia-archive-close-cleanup-failure");
        var binding = new FakeTiaOpennessBinding(
            Behavior(archiveCleanupFailureMessage: "Injected archive cleanup failure."));
        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                GetSiemensFakeArchivePath(),
                TiaProjectOpenMode.RetrievedArchive,
                workspaceRoot,
                null),
            CancellationToken.None);
        Assert.NotNull(open.Session);

        await open.Session.CloseAsync(CancellationToken.None);

        Assert.Equal(TiaProjectHealthState.Disposed, open.Session.Health);
        Assert.Single(Directory.GetDirectories(workspaceRoot));

        Directory.Delete(workspaceRoot, recursive: true);
    }

    [Fact]
    public async Task Fake_binding_reports_configured_target_discovery_failure_without_selected_target()
    {
        var projectPath = WriteFakeProject();
        var binding = new FakeTiaOpennessBinding(
            Behavior(
                discoverTargets: Scenario(
                    TiaOperationStatus.TimedOut,
                    diagnosticCode: "FakeTia.TargetDiscoveryTimedOut",
                    diagnosticSeverity: TiaDiagnosticSeverity.Error,
                    diagnosticMessage: "Configured fake target discovery timeout.")));

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectPath,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.NotNull(open.Session);
        var discovery = await open.Session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("configured-plc"),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.TimedOut, discovery.Status);
        Assert.Null(discovery.SelectedTarget);
        Assert.Contains(discovery.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.TargetDiscoveryTimedOut");
    }

    [Fact]
    public async Task Fake_binding_allows_behavior_injection_without_project_behavior()
    {
        var projectPath = WriteFakeProject();
        var behavior = Behavior(
            open: Scenario(
                TiaOperationStatus.Failed,
                diagnosticCode: "FakeTia.InjectedOpenFailed",
                diagnosticSeverity: TiaDiagnosticSeverity.Error,
                diagnosticMessage: "Injected fake open failure."));
        var binding = new FakeTiaOpennessBinding(behavior);

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectPath,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Null(open.Session);
        Assert.Equal(TiaOperationStatus.Failed, open.Status);
        Assert.Contains(open.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.InjectedOpenFailed");
    }

    [Fact]
    public async Task Fake_binding_injected_behavior_overrides_project_file_behavior()
    {
        var projectPath = WriteFakeProject(
            """
            {
              "discoverTargets": {
                "status": "Succeeded"
              }
            }
            """);
        var behavior = Behavior(
            discoverTargets: Scenario(
                TiaOperationStatus.TimedOut,
                diagnosticCode: "FakeTia.InjectedDiscoveryTimedOut",
                diagnosticSeverity: TiaDiagnosticSeverity.Error,
                diagnosticMessage: "Injected fake discovery timeout."));
        var binding = new FakeTiaOpennessBinding(behavior);

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectPath,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.NotNull(open.Session);
        var discovery = await open.Session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("configured-plc"),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.TimedOut, discovery.Status);
        Assert.Null(discovery.SelectedTarget);
        Assert.Contains(discovery.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.InjectedDiscoveryTimedOut");
    }

    [Fact]
    public async Task Fake_binding_reports_ambiguous_target_match()
    {
        var projectPath = WriteFakeProject(
            "{}",
            """
            [
              {
                "id": "duplicate-plc",
                "displayName": "Configured PLC A",
                "technicalPath": "Project/ConfiguredStationA/ConfiguredCpu",
                "cpuFamily": "S7-1500",
                "cpuType": "CPU 1516F-3 PN/DP"
              },
              {
                "id": "duplicate-plc",
                "displayName": "Configured PLC B",
                "technicalPath": "Project/ConfiguredStationB/ConfiguredCpu",
                "cpuFamily": "S7-1500",
                "cpuType": "CPU 1516F-3 PN/DP"
              }
            ]
            """);
        var binding = new FakeTiaOpennessBinding();

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectPath,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.NotNull(open.Session);
        var discovery = await open.Session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("duplicate-plc"),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Failed, discovery.Status);
        Assert.Null(discovery.SelectedTarget);
        Assert.Contains(discovery.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.TargetAmbiguous");
    }

    [Fact]
    public async Task Fake_binding_uses_configured_build_and_transfer_scenarios()
    {
        var projectPath = WriteFakeProject();
        var binding = new FakeTiaOpennessBinding(
            Behavior(
                build: Scenario(
                    TiaOperationStatus.SucceededWithWarnings,
                    warningCount: 2,
                    diagnosticCode: "FakeTia.BuildWarning",
                    diagnosticSeverity: TiaDiagnosticSeverity.Warning,
                    diagnosticMessage: "Configured fake build warning."),
                transfer: Scenario(
                    TiaOperationStatus.Failed,
                    diagnosticCode: "FakeTia.TransferFailed",
                    diagnosticSeverity: TiaDiagnosticSeverity.Error,
                    diagnosticMessage: "Configured fake transfer failure.")));

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectPath,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.NotNull(open.Session);
        var discovery = await open.Session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("configured-plc"),
            CancellationToken.None);
        Assert.True(discovery.Succeeded);
        Assert.NotNull(discovery.SelectedTarget);

        var build = await open.Session.BuildAsync(
            new TiaBuildRequest(discovery.SelectedTarget),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.SucceededWithWarnings, build.Status);
        Assert.Equal(2, build.WarningCount);
        Assert.Contains(build.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.BuildWarning");

        var transfer = await open.Session.TransferAsync(
            new TiaTransferRequest(
                discovery.SelectedTarget,
                CreateFakeRuntimeTarget()),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Failed, transfer.Status);
        Assert.Contains(transfer.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.TransferFailed");
    }

    [Theory]
    [InlineData("Succeeded", true, "Information")]
    [InlineData("SucceededWithWarnings", true, "Warning")]
    [InlineData("Failed", false, "Error")]
    [InlineData("Cancelled", false, "Error")]
    [InlineData("TimedOut", false, "Error")]
    [InlineData("Invalidated", false, "Error")]
    [InlineData("Unclear", false, "Error")]
    public async Task Fake_binding_transfer_scenario_maps_configured_status_matrix(
        string status,
        bool expectedSucceeded,
        string severity)
    {
        var operationStatus = Enum.Parse<TiaOperationStatus>(status);
        var transfer = await RunConfiguredTransferAsync(
            WriteFakeProject(),
            Behavior(
                transfer: Scenario(
                    operationStatus,
                    diagnosticCode: $"FakeTia.Transfer{status}",
                    diagnosticSeverity: Enum.Parse<TiaDiagnosticSeverity>(severity),
                    diagnosticMessage: $"Configured fake transfer {status}.")));

        Assert.Equal(operationStatus, transfer.Status);
        Assert.Equal(expectedSucceeded, transfer.Succeeded);
        Assert.Contains(transfer.Diagnostics, diagnostic => diagnostic.Code == $"FakeTia.Transfer{status}");
        Assert.Contains(transfer.Evidence, evidence => evidence.Kind == "Transfer");
    }

    [Fact]
    public async Task Fake_binding_transfer_rejects_target_outside_current_session()
    {
        var binding = new FakeTiaOpennessBinding();
        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                GetSiemensFakeProjectPath(),
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.NotNull(open.Session);
        var transfer = await open.Session.TransferAsync(
            new TiaTransferRequest(
                new TiaProjectTargetRef(
                    "stale-plc",
                    "Stale PLC",
                    "Project/Stale",
                    "S7-1500",
                    "CPU 1516F-3 PN/DP"),
                CreateFakeRuntimeTarget()),
            CancellationToken.None);

        Assert.False(transfer.Succeeded);
        Assert.Equal(TiaOperationStatus.Invalidated, transfer.Status);
        Assert.Contains(transfer.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.TransferProjectTargetInvalid");
    }

    [Fact]
    public async Task Fake_binding_transfer_rejects_incompatible_runtime_destination()
    {
        var binding = new FakeTiaOpennessBinding();
        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                GetSiemensFakeProjectPath(),
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.NotNull(open.Session);
        var discovery = await open.Session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(null),
            CancellationToken.None);
        Assert.NotNull(discovery.SelectedTarget);

        var transfer = await open.Session.TransferAsync(
            new TiaTransferRequest(
                discovery.SelectedTarget,
                new TiaRuntimeTransferTarget(
                    "remote-runtime",
                    "Remote Runtime",
                    "UnsupportedRuntime",
                    "fake",
                    SiemensRuntimeLocality.Remote,
                    "Remote Runtime",
                    TiaTransferConnectionMode.PnIe,
                    TiaTransferPcInterface.PlcSim,
                    1,
                    null)),
            CancellationToken.None);

        Assert.False(transfer.Succeeded);
        Assert.Equal(TiaOperationStatus.Failed, transfer.Status);
        Assert.Contains(transfer.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.TransferRuntimeTargetInvalid");
    }

    [Fact]
    public async Task Fake_binding_invalidated_build_scenario_updates_health_and_blocks_follow_up_operations()
    {
        var projectPath = WriteFakeProject();
        var binding = new FakeTiaOpennessBinding(
            Behavior(
                build: Scenario(
                    TiaOperationStatus.Invalidated,
                    diagnosticCode: "FakeTia.BuildInvalidated",
                    diagnosticSeverity: TiaDiagnosticSeverity.Error,
                    diagnosticMessage: "Configured fake build invalidation.")));
        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectPath,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.NotNull(open.Session);
        var health = new List<TiaProjectHealthChangedEventArgs>();
        open.Session.HealthChanged += (_, args) => health.Add(args);
        var discovery = await open.Session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("configured-plc"),
            CancellationToken.None);
        Assert.NotNull(discovery.SelectedTarget);

        var build = await open.Session.BuildAsync(
            new TiaBuildRequest(discovery.SelectedTarget),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Invalidated, build.Status);
        Assert.Equal(TiaProjectHealthState.Invalidated, open.Session.Health);
        Assert.Contains(health, item => item.Current == TiaProjectHealthState.Invalidated);

        var followUpDiscovery = await open.Session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("configured-plc"),
            CancellationToken.None);
        Assert.Equal(TiaOperationStatus.Invalidated, followUpDiscovery.Status);
        Assert.Null(followUpDiscovery.SelectedTarget);
        Assert.Contains(followUpDiscovery.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.SessionInvalidated");

        await open.Session.CloseAsync(CancellationToken.None);
        Assert.Equal(TiaProjectHealthState.Disposed, open.Session.Health);
        Assert.Contains(
            health,
            item => item.Previous == TiaProjectHealthState.Invalidated
                && item.Current == TiaProjectHealthState.Disposed);
    }

    [Fact]
    public async Task Fake_binding_close_is_idempotent_and_blocks_follow_up_operations()
    {
        var binding = new FakeTiaOpennessBinding();
        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                GetSiemensFakeProjectPath(),
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.NotNull(open.Session);
        var health = new List<TiaProjectHealthChangedEventArgs>();
        open.Session.HealthChanged += (_, args) => health.Add(args);

        await open.Session.CloseAsync(CancellationToken.None);
        await open.Session.CloseAsync(CancellationToken.None);

        Assert.Equal(TiaProjectHealthState.Disposed, open.Session.Health);
        Assert.Single(health, item => item.Current == TiaProjectHealthState.Disposed);

        var build = await open.Session.BuildAsync(
            new TiaBuildRequest(
                new TiaProjectTargetRef(
                    "siemens-fake-plc",
                    "Siemens Fake PLC",
                    "Project/FakeStation/FakeCpu",
                    "S7-1500",
                    "CPU 1516F-3 PN/DP")),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Invalidated, build.Status);
        Assert.Contains(build.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.SessionDisposed");
    }

    [Theory]
    [InlineData("Succeeded", true, 0, 0, "Information")]
    [InlineData("SucceededWithWarnings", true, 3, 0, "Warning")]
    [InlineData("Failed", false, 0, 4, "Error")]
    [InlineData("Cancelled", false, 0, 0, "Error")]
    [InlineData("TimedOut", false, 0, 0, "Error")]
    [InlineData("Invalidated", false, 0, 0, "Error")]
    [InlineData("Unclear", false, 0, 0, "Error")]
    public async Task Fake_binding_build_scenario_maps_configured_status_matrix(
        string status,
        bool expectedSucceeded,
        int warningCount,
        int errorCount,
        string severity)
    {
        var operationStatus = Enum.Parse<TiaOperationStatus>(status);
        var build = await RunConfiguredBuildAsync(
            WriteFakeProject(),
            Behavior(
                build: Scenario(
                    operationStatus,
                    warningCount: warningCount,
                    errorCount: errorCount,
                    diagnosticCode: $"FakeTia.Build{status}",
                    diagnosticSeverity: Enum.Parse<TiaDiagnosticSeverity>(severity),
                    diagnosticMessage: $"Configured fake build {status}.")));

        Assert.Equal(operationStatus, build.Status);
        Assert.Equal(expectedSucceeded, build.Succeeded);
        Assert.Equal(warningCount, build.WarningCount);
        Assert.Equal(errorCount, build.ErrorCount);
        Assert.Contains(build.Diagnostics, diagnostic => diagnostic.Code == $"FakeTia.Build{status}");
        Assert.Contains(build.Evidence, evidence => evidence.Kind == "Build");
    }

    [Fact]
    public async Task Fake_binding_build_rejects_target_outside_current_session()
    {
        var binding = new FakeTiaOpennessBinding();
        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                GetSiemensFakeProjectPath(),
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.NotNull(open.Session);
        var build = await open.Session.BuildAsync(
            new TiaBuildRequest(
                new TiaProjectTargetRef(
                    "stale-plc",
                    "Stale PLC",
                    "Project/Stale",
                    "S7-1500",
                    "CPU 1516F-3 PN/DP")),
            CancellationToken.None);

        Assert.False(build.Succeeded);
        Assert.Equal(TiaOperationStatus.Invalidated, build.Status);
        Assert.Equal(1, build.ErrorCount);
        Assert.Contains(build.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.BuildTargetInvalid");
    }

    [Fact]
    public async Task Fake_binding_uses_configured_open_scenario_without_creating_session()
    {
        var projectPath = WriteFakeProject();
        var binding = new FakeTiaOpennessBinding(
            Behavior(
                open: Scenario(
                    TiaOperationStatus.Failed,
                    diagnosticCode: "FakeTia.OpenRejected",
                    diagnosticSeverity: TiaDiagnosticSeverity.Error,
                    diagnosticMessage: "Configured fake open failure.")));

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectPath,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Null(open.Session);
        Assert.Equal(TiaOperationStatus.Failed, open.Status);
        Assert.Contains(open.Diagnostics, diagnostic => diagnostic.Code == "FakeTia.OpenRejected");
    }

    [Fact]
    public async Task Fake_binding_uses_injected_time_provider_for_configured_delays()
    {
        var projectPath = WriteFakeProject();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero));
        var binding = new FakeTiaOpennessBinding(
            Behavior(open: Scenario(TiaOperationStatus.Succeeded, delayMs: 5000)),
            timeProvider);

        var openTask = binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectPath,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.False(openTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var open = await openTask;

        Assert.True(open.Succeeded);
        Assert.NotNull(open.Session);
    }

    [Fact]
    public void Siemens_fake_configuration_loads_siemens_fake_project_source()
    {
        var configPath = GetSiemensFakeConfigurationPath();
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));

        var root = document.RootElement;
        Assert.Equal("siemens", root.GetProperty("accessProfile").GetProperty("id").GetString());
        Assert.Equal("2.0.0", root.GetProperty("accessProfile").GetProperty("version").GetString());
        Assert.Equal(@"..\FakeProjects\siemens-fake-project.apf", root.GetProperty("project").GetProperty("source").GetString());

        var values = root.GetProperty("accessProfile").GetProperty("values");
        Assert.Equal("siemens-fake-plc", values.GetProperty("plcRef").GetProperty("id").GetString());
        var runtime = values.GetProperty("runtimeRef");
        Assert.False(runtime.TryGetProperty("id", out _));
        Assert.Equal("SiemensFakeRuntime", runtime.GetProperty("instanceName").GetString());
        Assert.Equal("s7-1500", runtime.GetProperty("cpuType").GetString());
        var unitTest = values.GetProperty("unitTest");
        Assert.Equal("polling", unitTest.GetProperty("observationStrategy").GetString());
        Assert.Equal(10, unitTest.GetProperty("monitoringIntervalMs").GetInt32());
        Assert.Equal(1, unitTest.GetProperty("drainIntervalMs").GetInt32());
        Assert.Equal(32, unitTest.GetProperty("maxEventsPerBatch").GetInt32());
        Assert.Equal("release", values.GetProperty("cleanup").GetProperty("runtime").GetString());
    }

    [Fact]
    public void Fake_binding_manifest_matches_binding_metadata_and_entry_point()
    {
        var binding = new FakeTiaOpennessBinding();
        using var document = JsonDocument.Parse(File.ReadAllText(GetFakeTiaBindingManifestPath()));

        var root = document.RootElement;
        Assert.Equal(SiemensBindingPackageDescriptorContract.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());

        var info = root.GetProperty("info");
        Assert.Equal(binding.Info.BindingKind, info.GetProperty("bindingKind").GetString());
        Assert.Equal(binding.Info.BindingVersion, info.GetProperty("bindingVersion").GetString());
        Assert.Equal(binding.Info.DisplayName, info.GetProperty("displayName").GetString());

        var entryPoint = root.GetProperty("entryPoint");
        Assert.Equal(Path.GetFileName(typeof(FakeTiaOpennessBinding).Assembly.Location), entryPoint.GetProperty("assembly").GetString());
        Assert.Equal(typeof(FakeTiaOpennessBinding).FullName, entryPoint.GetProperty("typeName").GetString());
        Assert.Same(
            typeof(FakeTiaOpennessBinding),
            typeof(FakeTiaOpennessBinding).Assembly.GetType(entryPoint.GetProperty("typeName").GetString()!));

        var projectSources = root
            .GetProperty("declaration")
            .GetProperty("projectSources")
            .EnumerateArray()
            .ToArray();
        Assert.Collection(
            projectSources.OrderBy(source => source.GetProperty("extension").GetString(), StringComparer.Ordinal),
            directProject =>
            {
                Assert.Equal(SiemensProjectSourceKinds.SiemensFakeProject, directProject.GetProperty("kind").GetString());
                Assert.Equal(".apf", directProject.GetProperty("extension").GetString());
                Assert.Equal("fake", directProject.GetProperty("version").GetString());
                Assert.Equal("DirectProject", directProject.GetProperty("openMode").GetString());
            },
            archiveProject =>
            {
                Assert.Equal(SiemensProjectSourceKinds.SiemensFakeProject, archiveProject.GetProperty("kind").GetString());
                Assert.Equal(".zapf", archiveProject.GetProperty("extension").GetString());
                Assert.Equal("fake", archiveProject.GetProperty("version").GetString());
                Assert.Equal("RetrievedArchive", archiveProject.GetProperty("openMode").GetString());
            });
    }

    private static string GetSiemensFakeProjectPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "access-profiles",
                "siemens",
                "tia-openness",
                "fake",
                "tests",
                "Resources",
                "FakeProjects",
                "siemens-fake-project.apf");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "Resources",
            "FakeProjects",
            "siemens-fake-project.apf");
    }

    private static string GetSiemensFakeArchivePath()
    {
        return Path.Combine(
            AppContext.BaseDirectory,
            "Resources",
            "FakeProjects",
            "siemens-fake-project.zapf");
    }

    private static string CreateTemporaryDirectory(string purpose)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            purpose,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetSiemensFakeConfigurationPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "access-profiles",
                "siemens",
                "tia-openness",
                "fake",
                "tests",
                "Resources",
                "Configuration",
                "siemens-fake-unittest.config.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "Resources",
            "Configuration",
            "siemens-fake-unittest.config.json");
    }

    private static string GetFakeTiaBindingManifestPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "access-profiles",
                "siemens",
                "tia-openness",
                "fake",
                "src",
                "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.Fake",
                SiemensBindingPackageDescriptorContract.FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            SiemensBindingPackageDescriptorContract.FileName);
    }
    private static async Task<TiaBuildResult> RunConfiguredBuildAsync(
        string projectPath,
        FakeTiaOpennessBinding.FakeTiaBehavior? behavior = null)
    {
        var binding = behavior is null
            ? new FakeTiaOpennessBinding()
            : new FakeTiaOpennessBinding(behavior);
        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectPath,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.NotNull(open.Session);
        var discovery = await open.Session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("configured-plc"),
            CancellationToken.None);
        Assert.NotNull(discovery.SelectedTarget);
        return await open.Session.BuildAsync(
            new TiaBuildRequest(discovery.SelectedTarget),
            CancellationToken.None);
    }

    private static async Task<TiaTransferResult> RunConfiguredTransferAsync(
        string projectPath,
        FakeTiaOpennessBinding.FakeTiaBehavior? behavior = null)
    {
        var binding = behavior is null
            ? new FakeTiaOpennessBinding()
            : new FakeTiaOpennessBinding(behavior);
        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectPath,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.NotNull(open.Session);
        var discovery = await open.Session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("configured-plc"),
            CancellationToken.None);
        Assert.NotNull(discovery.SelectedTarget);
        return await open.Session.TransferAsync(
            new TiaTransferRequest(discovery.SelectedTarget, CreateFakeRuntimeTarget()),
            CancellationToken.None);
    }

    private static TiaRuntimeTransferTarget CreateFakeRuntimeTarget()
    {
        return new TiaRuntimeTransferTarget(
            "fake-runtime",
            "Fake Runtime",
            SiemensBindingKinds.PlcSimAdvanced,
            "fake-8",
            SiemensRuntimeLocality.Local,
            "Fake Runtime",
            TiaTransferConnectionMode.PnIe,
            TiaTransferPcInterface.PlcSim,
            1,
            null);
    }

    private static FakeTiaOpennessBinding.FakeTiaBehavior Behavior(
        FakeTiaOpennessBinding.FakeTiaOperationScenario? open = null,
        FakeTiaOpennessBinding.FakeTiaOperationScenario? discoverTargets = null,
        FakeTiaOpennessBinding.FakeTiaOperationScenario? build = null,
        FakeTiaOpennessBinding.FakeTiaOperationScenario? transfer = null,
        FakeTiaOpennessBinding.FakeTiaOperationScenario? close = null,
        string? archiveCleanupFailureMessage = null,
        TiaSafetyProgramAuthorizationRequirementResult? safetyAuthorizationRequirement = null,
        TiaSafetyProgramAuthorizationResult? safetyAuthorization = null)
    {
        return new FakeTiaOpennessBinding.FakeTiaBehavior(
            open ?? FakeTiaOpennessBinding.FakeTiaOperationScenario.SucceededScenario,
            discoverTargets ?? FakeTiaOpennessBinding.FakeTiaOperationScenario.SucceededScenario,
            build ?? FakeTiaOpennessBinding.FakeTiaOperationScenario.SucceededScenario,
            transfer ?? FakeTiaOpennessBinding.FakeTiaOperationScenario.SucceededScenario,
            close ?? FakeTiaOpennessBinding.FakeTiaOperationScenario.SucceededScenario,
            archiveCleanupFailureMessage,
            safetyAuthorizationRequirement,
            safetyAuthorization);
    }

    private static FakeTiaOpennessBinding.FakeTiaOperationScenario Scenario(
        TiaOperationStatus status,
        int warningCount = 0,
        int errorCount = 0,
        int delayMs = 0,
        string? diagnosticCode = null,
        TiaDiagnosticSeverity diagnosticSeverity = TiaDiagnosticSeverity.Information,
        string? diagnosticMessage = null)
    {
        return new FakeTiaOpennessBinding.FakeTiaOperationScenario(
            status,
            warningCount,
            errorCount,
            delayMs,
            diagnosticCode,
            diagnosticSeverity,
            diagnosticMessage);
    }

    private static string WriteFakeProject(string behaviorJson = "{}", string? targetsJson = null)
    {
        targetsJson ??= """
            [
              {
                "id": "configured-plc",
                "displayName": "Configured PLC",
                "technicalPath": "Project/ConfiguredStation/ConfiguredCpu",
                "cpuFamily": "S7-1500",
                "cpuType": "CPU 1516F-3 PN/DP"
              }
            ]
            """;
        var directory = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "fake-tia-projects", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "configured.apf");
        File.WriteAllText(
            path,
            $$"""
            {
              "schemaVersion": 1,
              "project": {
                "displayName": "Configured Siemens Fake TIA Project",
                "productVersion": "fake-21",
                "projectVersion": "fake"
              },
              "targets": {{targetsJson}},
              "behavior": {{behaviorJson}}
            }
            """);
        return path;
    }
}
