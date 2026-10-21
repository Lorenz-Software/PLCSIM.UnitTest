using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using System.Diagnostics;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20;

internal sealed class TiaOpennessV20ArchiveWorkspace
{
    private const string CleanupFailureDataKey = "TiaOpenness.V20.ArchiveWorkspaceCleanupFailed";

    internal TiaOpennessV20ArchiveWorkspace(string ownedDirectory)
    {
        OwnedDirectory = ownedDirectory;
    }

    public string OwnedDirectory { get; }

    public static TiaOpennessV20ArchiveWorkspace? Create(
        TiaProjectOpenMode openMode,
        string? configuredBaseDirectory,
        string workerInstanceId)
    {
        return openMode == TiaProjectOpenMode.RetrievedArchive
            ? new TiaOpennessV20ArchiveWorkspace(
                CreateOwnedArchiveWorkDirectoryPath(configuredBaseDirectory, workerInstanceId))
            : null;
    }

    public TiaOpennessV20EngineeringSessionHostOpenResult CompleteFailedOpen(
        TiaOpennessV20EngineeringSessionHostOpenResult primaryResult)
    {
        var cleanupFailure = DeleteOwnedArchiveWorkDirectory(OwnedDirectory);
        if (cleanupFailure is null)
        {
            return primaryResult;
        }

        var message = BoundCleanupMessage(cleanupFailure.Message);
        return primaryResult with
        {
            Diagnostics = primaryResult.Diagnostics.Concat(new[]
            {
                new TiaDiagnostic(
                    "TiaOpenness.V20.ArchiveWorkspaceCleanupFailed",
                    TiaDiagnosticSeverity.Warning,
                    "TIA Openness V20 could not remove its owned archive workspace after project open failed: " + message,
                    "OpenEngineeringSession",
                    "tia-openness-v20-archive-workspace-cleanup")
            }).ToArray(),
            Evidence = primaryResult.Evidence.Concat(new[]
            {
                new TiaEvidence(
                    "tia-openness-v20-archive-workspace-cleanup",
                    "ArchiveWorkspaceCleanupFailure",
                    message)
            }).ToArray()
        };
    }

    public void RecordCancellationCleanupFailure(OperationCanceledException cancellation)
    {
        var cleanupFailure = DeleteOwnedArchiveWorkDirectory(OwnedDirectory);
        if (cleanupFailure is null)
        {
            return;
        }

        var message = BoundCleanupMessage(cleanupFailure.Message);
        cancellation.Data[CleanupFailureDataKey] = message;
        Trace.TraceWarning(
            "TiaOpenness.V20.ArchiveWorkspaceCleanupFailed: TIA Openness V20 could not remove its owned archive workspace after cancellation: {0}",
            message);
    }

    public void TraceCleanupFailure()
    {
        var cleanupFailure = DeleteOwnedArchiveWorkDirectory(OwnedDirectory);
        if (cleanupFailure is null)
        {
            return;
        }

        Trace.TraceWarning(
            "TiaOpenness.V20.ArchiveWorkspaceCleanupFailed: TIA Openness V20 could not remove its owned archive workspace during terminal session cleanup: {0}",
            BoundCleanupMessage(cleanupFailure.Message));
    }

    internal static string CreateOwnedArchiveWorkDirectoryPath(
        string? configuredBaseDirectory,
        string workerInstanceId)
    {
        var baseDirectory = string.IsNullOrWhiteSpace(configuredBaseDirectory)
            ? Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "projects")
            : configuredBaseDirectory;
        var normalizedBaseDirectory = Path.GetFullPath(baseDirectory);
        var ownedDirectory = Path.GetFullPath(
            Path.Combine(normalizedBaseDirectory, "tia-" + workerInstanceId));
        EnsureOwnedChildPath(normalizedBaseDirectory, ownedDirectory);
        return ownedDirectory;
    }

    internal static Exception? DeleteOwnedArchiveWorkDirectory(string? ownedDirectory)
    {
        if (string.IsNullOrWhiteSpace(ownedDirectory))
        {
            return null;
        }

        try
        {
            var normalizedOwnedDirectory = Path.GetFullPath(ownedDirectory);
            var parentDirectory = Directory.GetParent(normalizedOwnedDirectory)?.FullName;
            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                return null;
            }

            EnsureOwnedChildPath(parentDirectory, normalizedOwnedDirectory);
            if (Directory.Exists(normalizedOwnedDirectory))
            {
                Directory.Delete(normalizedOwnedDirectory, recursive: true);
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.GetBaseException();
        }
    }

    private static string BoundCleanupMessage(string? message)
    {
        const int maximumLength = 2048;
        var normalized = string.IsNullOrWhiteSpace(message)
            ? "The cleanup operation failed without an error message."
            : message.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : normalized.Substring(0, maximumLength);
    }

    private static void EnsureOwnedChildPath(string baseDirectory, string childDirectory)
    {
        var normalizedBaseDirectory = Path.GetFullPath(baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedChildDirectory = Path.GetFullPath(childDirectory);
        if (!normalizedChildDirectory.StartsWith(
                normalizedBaseDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "TIA archive workspace must be a child of its configured base directory.");
        }
    }
}
