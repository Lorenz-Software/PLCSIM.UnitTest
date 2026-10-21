using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Core.Diagnostics;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

internal sealed class WpfApplicationDiagnosticReporter
{
    private readonly IDiagnosticWriter diagnosticWriter;
    private readonly TimeProvider timeProvider;

    public WpfApplicationDiagnosticReporter(
        IDiagnosticWriter diagnosticWriter,
        TimeProvider timeProvider)
    {
        this.diagnosticWriter = diagnosticWriter ?? throw new ArgumentNullException(nameof(diagnosticWriter));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public void Add(Diagnostic diagnostic)
    {
        diagnosticWriter.Add(diagnostic);
    }

    public void AddRange(IReadOnlyCollection<Diagnostic> diagnostics)
    {
        diagnosticWriter.AddRange(diagnostics);
    }

    public Diagnostic CreateConfiguration(
        string code,
        string message,
        LogLevel level,
        string? evidence = null)
    {
        return new Diagnostic(
            code,
            DiagnosticCategory.Configuration,
            level,
            DiagnosticSource.WpfApplication,
            DiagnosticPhase.Configuration,
            message,
            new DiagnosticCorrelation(),
            timeProvider.GetUtcNow(),
            EvidenceReference: string.IsNullOrWhiteSpace(evidence)
                ? null
                : new DiagnosticEvidenceReference("configuration", evidence));
    }

    public void ReportApplicationLogUnavailable(
        string message,
        string? path,
        string? technicalDetail = null)
    {
        Add(new Diagnostic(
            "Wpf.ApplicationLog.Unavailable",
            DiagnosticCategory.Output,
            LogLevel.Warning,
            DiagnosticSource.WpfApplication,
            DiagnosticPhase.OutputCoordination,
            message,
            new DiagnosticCorrelation(),
            timeProvider.GetUtcNow(),
            Id: $"wpf-log-{Guid.NewGuid():N}",
            TechnicalDetail: technicalDetail,
            EvidenceReference: string.IsNullOrWhiteSpace(path)
                ? null
                : new DiagnosticEvidenceReference("application-log", path)));
    }

    public void ReportProfileRegistryUnavailable(string message, string? technicalDetail = null)
    {
        Add(new Diagnostic(
            "Wpf.ProfileRegistry.Unavailable",
            DiagnosticCategory.Configuration,
            LogLevel.Error,
            DiagnosticSource.WpfApplication,
            DiagnosticPhase.Configuration,
            message,
            new DiagnosticCorrelation(),
            timeProvider.GetUtcNow(),
            Id: $"wpf-profile-registry-{Guid.NewGuid():N}",
            TechnicalDetail: technicalDetail));
    }

    public void ReportSettingsSaveFailure(string technicalDetail)
    {
        Add(new Diagnostic(
            "Wpf.Settings.SaveFailed",
            DiagnosticCategory.Configuration,
            LogLevel.Warning,
            DiagnosticSource.WpfApplication,
            DiagnosticPhase.Configuration,
            "Application settings could not be saved. Check file access and available disk space, then try again. The previous settings remain active.",
            new DiagnosticCorrelation(),
            timeProvider.GetUtcNow(),
            Id: $"wpf-settings-{Guid.NewGuid():N}",
            TechnicalDetail: technicalDetail));
    }

    public void ReportArtifactUnavailable(
        string message,
        string? path,
        string? technicalDetail = null)
    {
        Add(new Diagnostic(
            "Wpf.Artifact.Unavailable",
            DiagnosticCategory.Output,
            LogLevel.Warning,
            DiagnosticSource.WpfApplication,
            DiagnosticPhase.OutputCoordination,
            string.IsNullOrWhiteSpace(path) ? message : $"{message} Path: {path}",
            new DiagnosticCorrelation(),
            timeProvider.GetUtcNow(),
            Id: $"wpf-artifact-{Guid.NewGuid():N}",
            TechnicalDetail: technicalDetail,
            EvidenceReference: string.IsNullOrWhiteSpace(path)
                ? null
                : new DiagnosticEvidenceReference("artifact", path)));
    }

    public void ReportAnalysisExportFailure(
        string message,
        string? path,
        string? technicalDetail = null)
    {
        Add(new Diagnostic(
            "Wpf.Analysis.ExportFailed",
            DiagnosticCategory.Output,
            LogLevel.Warning,
            DiagnosticSource.WpfApplication,
            DiagnosticPhase.OutputCoordination,
            string.IsNullOrWhiteSpace(path) ? message : $"{message} Path: {path}",
            new DiagnosticCorrelation(),
            timeProvider.GetUtcNow(),
            Id: $"wpf-analysis-export-{Guid.NewGuid():N}",
            TechnicalDetail: technicalDetail,
            EvidenceReference: string.IsNullOrWhiteSpace(path)
                ? null
                : new DiagnosticEvidenceReference("analysis-report", path)));
    }

    public void ReportCleanupFailure(
        string code,
        string message,
        string? technicalDetail = null)
    {
        Add(new Diagnostic(
            code,
            DiagnosticCategory.Cleanup,
            LogLevel.Error,
            DiagnosticSource.WpfApplication,
            DiagnosticPhase.Cleanup,
            message,
            new DiagnosticCorrelation(),
            timeProvider.GetUtcNow(),
            Id: $"wpf-cleanup-{Guid.NewGuid():N}",
            TechnicalDetail: technicalDetail));
    }
}
