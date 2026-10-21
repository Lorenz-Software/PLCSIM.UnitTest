using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Common.Artifacts;

/// <summary>
/// Exports a stable UnitTest run epoch snapshot to one artifact format.
/// </summary>
public interface IArtifactExporter
{
    /// <summary>
    /// Gets the artifact format implemented by this exporter.
    /// </summary>
    ArtifactFormat Format { get; }

    /// <summary>
    /// Publishes exporter-owned diagnostics.
    /// </summary>
    event EventHandler<DiagnosticEventArgs> DiagnosticEventHandler;

    /// <summary>
    /// Writes the requested artifact from a stable run epoch snapshot.
    /// </summary>
    Task<ExportResult> ExportAsync(
        ExportRequest request,
        UnitTestRunEpochSnapshot runEpoch,
        CancellationToken cancellationToken);
}

/// <summary>
/// Request to generate a single output artifact.
/// </summary>
public sealed record ExportRequest(
    ArtifactFormat Format,
    string OutputPath,
    bool Required);

/// <summary>
/// Result of a single artifact export request.
/// </summary>
public sealed record ExportResult(
    ArtifactFormat Format,
    ArtifactState State,
    string? OutputPath);

/// <summary>
/// Supported artifact formats.
/// </summary>
public enum ArtifactFormat
{
    JUnitXml
}

/// <summary>
/// State of a requested artifact.
/// </summary>
public enum ArtifactState
{
    NotRequested,
    Pending,
    Written,
    Skipped,
    Failed
}
