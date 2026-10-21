using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V15_1;

public sealed class TiaOpennessV15_1ArchiveWorkspaceTests
{
    [Fact]
    public void V15_1_archive_workspace_uses_isolated_default_temp_child()
    {
        var ownedDirectory =
            TiaOpennessV15_1ArchiveWorkspace.CreateOwnedArchiveWorkDirectoryPath(
                null,
                "worker-42");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "PLCSIM.UnitTest",
                "projects",
                "tia-worker-42")),
            ownedDirectory);
    }

    [Fact]
    public void V15_1_archive_workspace_is_an_owned_child_and_cleanup_preserves_configured_base()
    {
        var baseDirectory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            "archive-workspace-tests",
            Guid.NewGuid().ToString("N"));
        var siblingDirectory = Path.Combine(baseDirectory, "keep");
        Directory.CreateDirectory(siblingDirectory);
        var ownedDirectory =
            TiaOpennessV15_1ArchiveWorkspace.CreateOwnedArchiveWorkDirectoryPath(
                baseDirectory,
                "worker-42");
        Directory.CreateDirectory(ownedDirectory);
        File.WriteAllText(Path.Combine(ownedDirectory, "partial-project.ap15_1"), string.Empty);

        Assert.Null(TiaOpennessV15_1ArchiveWorkspace.DeleteOwnedArchiveWorkDirectory(ownedDirectory));
        Assert.Null(TiaOpennessV15_1ArchiveWorkspace.DeleteOwnedArchiveWorkDirectory(ownedDirectory));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(baseDirectory, "tia-worker-42")),
            ownedDirectory);
        Assert.False(Directory.Exists(ownedDirectory));
        Assert.True(Directory.Exists(baseDirectory));
        Assert.True(Directory.Exists(siblingDirectory));

        Directory.Delete(baseDirectory, recursive: true);
    }

    [Fact]
    public void V15_1_archive_open_failure_preserves_primary_diagnostic_and_reports_cleanup_failure()
    {
        var primary = TiaOpennessV15_1EngineeringSessionHostOpenResult.Failed(
            new TiaDiagnostic(
                "TiaOpenness.V15_1.PrimaryOpenFailure",
                TiaDiagnosticSeverity.Error,
                "Primary open failure.",
                "OpenEngineeringSession",
                "primary-evidence"),
            Array.Empty<TiaEvidence>());

        var result = new TiaOpennessV15_1ArchiveWorkspace("invalid\0archive-workspace")
            .CompleteFailedOpen(primary);

        Assert.Equal("TiaOpenness.V15_1.PrimaryOpenFailure", result.Diagnostics[0].Code);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "TiaOpenness.V15_1.ArchiveWorkspaceCleanupFailed"
                && diagnostic.Severity == TiaDiagnosticSeverity.Warning);
        Assert.Contains(
            result.Evidence,
            evidence => evidence.Kind == "ArchiveWorkspaceCleanupFailure");
    }

    [Fact]
    public void V15_1_archive_open_cancellation_preserves_cancellation_and_attaches_cleanup_failure()
    {
        var cancellation = new OperationCanceledException("Open was cancelled.");

        new TiaOpennessV15_1ArchiveWorkspace("invalid\0archive-workspace")
            .RecordCancellationCleanupFailure(cancellation);

        Assert.True(cancellation.Data.Contains("TiaOpenness.V15_1.ArchiveWorkspaceCleanupFailed"));
        Assert.NotEmpty((string)cancellation.Data["TiaOpenness.V15_1.ArchiveWorkspaceCleanupFailed"]!);
    }

    [Fact]
    public async Task V15_1_archive_open_failure_does_not_leave_an_owned_workspace()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            "archive-open-failure-tests",
            Guid.NewGuid().ToString("N"));
        var archiveBaseDirectory = Path.Combine(testRoot, "work");
        var projectSource = Path.Combine(testRoot, "sample.zap15_1");
        Directory.CreateDirectory(archiveBaseDirectory);
        File.WriteAllText(projectSource, string.Empty);
        var probe = new RecordingV15_1EnvironmentProbe(
            TiaOpennessV15_1EnvironmentProbeResult.Ready(
                Path.Combine(testRoot, "missing-public-api"),
                [
                new TiaEvidence(
                    "tia-openness-v15_1-public-api",
                    "PublicApiDirectory",
                    Path.Combine(testRoot, "missing-public-api"))
            ]));
        var binding = new TiaOpennessV15_1Binding(probe);
        binding.Initialize(new SiemensBindingContext("siemens", TiaOpennessV15_1TestSupport.DefaultOperationalLogging));

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectSource,
                TiaProjectOpenMode.RetrievedArchive,
                archiveBaseDirectory,
                null),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Null(open.Session);
        Assert.Empty(Directory.GetDirectories(archiveBaseDirectory));

        Directory.Delete(testRoot, recursive: true);
    }

}
