using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Core.Diagnostics;

namespace PLCSIM.UnitTest.Core.AccessProfiles;

internal sealed class AccessProfileLoaderDiagnosticReporter
{
    private readonly ILogger logger;
    private readonly TimeProvider timeProvider;
    private readonly IDiagnosticWriter diagnosticWriter;

    public AccessProfileLoaderDiagnosticReporter(
        ILogger logger,
        TimeProvider timeProvider,
        IDiagnosticWriter diagnosticWriter)
    {
        this.logger = logger;
        this.timeProvider = timeProvider;
        this.diagnosticWriter = diagnosticWriter;
    }

    public Diagnostic Create(
        string code,
        DiagnosticCategory category,
        LogLevel level,
        string message,
        string? technicalDetail = null)
    {
        return AccessProfileLoaderDiagnostics.Create(
            code,
            category,
            level,
            message,
            timeProvider.GetUtcNow(),
            technicalDetail);
    }

    public void PublishRange(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            Publish(diagnostic);
        }
    }

    public void Publish(Diagnostic diagnostic)
    {
        logger.Log(
            diagnostic.Level,
            "Access profile loader diagnostic. Code={Code}, Category={Category}, Message={Message}",
            diagnostic.Code,
            diagnostic.Category,
            diagnostic.Message);
        diagnosticWriter.Add(diagnostic);
    }
}

internal static class AccessProfileLoaderDiagnostics
{
    public static Diagnostic Create(
        string code,
        DiagnosticCategory category,
        LogLevel level,
        string message,
        DateTimeOffset timestamp,
        string? technicalDetail = null)
    {
        return new Diagnostic(
            code,
            category,
            level,
            DiagnosticSource.AccessProfileLoader,
            DiagnosticPhase.ProfileValidation,
            message,
            new DiagnosticCorrelation(),
            timestamp,
            TechnicalDetail: technicalDetail);
    }
}
