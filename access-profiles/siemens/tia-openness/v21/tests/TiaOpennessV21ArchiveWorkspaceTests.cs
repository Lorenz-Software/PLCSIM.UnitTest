using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V21;

public sealed class TiaOpennessV21ArchiveWorkspaceTests
{
    [Fact]
    public void V21_archive_workspace_is_an_owned_child_and_cleanup_preserves_configured_base()
    {
        var baseDirectory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            "archive-workspace-tests",
            Guid.NewGuid().ToString("N"));
        var siblingDirectory = Path.Combine(baseDirectory, "keep");
        Directory.CreateDirectory(siblingDirectory);
        var ownedDirectory =
            TiaOpennessV21ArchiveWorkspace.CreateOwnedArchiveWorkDirectoryPath(
                baseDirectory,
                "worker-42");
        Directory.CreateDirectory(ownedDirectory);
        File.WriteAllText(Path.Combine(ownedDirectory, "partial-project.ap21"), string.Empty);

        Assert.Null(TiaOpennessV21ArchiveWorkspace.DeleteOwnedArchiveWorkDirectory(ownedDirectory));
        Assert.Null(TiaOpennessV21ArchiveWorkspace.DeleteOwnedArchiveWorkDirectory(ownedDirectory));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(baseDirectory, "tia-worker-42")),
            ownedDirectory);
        Assert.False(Directory.Exists(ownedDirectory));
        Assert.True(Directory.Exists(baseDirectory));
        Assert.True(Directory.Exists(siblingDirectory));

        Directory.Delete(baseDirectory, recursive: true);
    }

    [Fact]
    public void V21_archive_open_failure_preserves_primary_diagnostic_and_reports_cleanup_failure()
    {
        var primary = TiaOpennessV21EngineeringSessionHostOpenResult.Failed(
            new TiaDiagnostic(
                "TiaOpenness.V21.PrimaryOpenFailure",
                TiaDiagnosticSeverity.Error,
                "Primary open failure.",
                "OpenEngineeringSession",
                "primary-evidence"),
            Array.Empty<TiaEvidence>());

        var result = new TiaOpennessV21ArchiveWorkspace("invalid\0archive-workspace")
            .CompleteFailedOpen(primary);

        Assert.Equal("TiaOpenness.V21.PrimaryOpenFailure", result.Diagnostics[0].Code);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "TiaOpenness.V21.ArchiveWorkspaceCleanupFailed"
                && diagnostic.Severity == TiaDiagnosticSeverity.Warning);
        Assert.Contains(
            result.Evidence,
            evidence => evidence.Kind == "ArchiveWorkspaceCleanupFailure");
    }

    [Fact]
    public void V21_archive_open_cancellation_preserves_cancellation_and_attaches_cleanup_failure()
    {
        var cancellation = new OperationCanceledException("Open was cancelled.");

        new TiaOpennessV21ArchiveWorkspace("invalid\0archive-workspace")
            .RecordCancellationCleanupFailure(cancellation);

        Assert.True(cancellation.Data.Contains("TiaOpenness.V21.ArchiveWorkspaceCleanupFailed"));
        Assert.NotEmpty((string)cancellation.Data["TiaOpenness.V21.ArchiveWorkspaceCleanupFailed"]!);
    }

    [Fact]
    public async Task V21_archive_open_failure_does_not_leave_an_owned_workspace()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            "archive-open-failure-tests",
            Guid.NewGuid().ToString("N"));
        var archiveBaseDirectory = Path.Combine(testRoot, "work");
        var projectSource = Path.Combine(testRoot, "sample.zap21");
        Directory.CreateDirectory(archiveBaseDirectory);
        File.WriteAllText(projectSource, string.Empty);
        var probe = new RecordingV21EnvironmentProbe(
            TiaOpennessV21EnvironmentProbeResult.Ready(
                Path.Combine(testRoot, "missing-public-api"),
                [
                new TiaEvidence(
                    "tia-openness-v21-public-api",
                    "PublicApiDirectory",
                    Path.Combine(testRoot, "missing-public-api"))
            ]));
        var binding = new TiaOpennessV21Binding(probe);
        binding.Initialize(new SiemensBindingContext("siemens", TiaOpennessV21TestSupport.DefaultOperationalLogging));

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
