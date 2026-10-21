using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.Artifacts;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Output;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core;
using PLCSIM.UnitTest.Core.Diagnostics;
using PLCSIM.UnitTest.Core.Workflow;

namespace PLCSIM.UnitTest.Core.Output;

/// <summary>
/// Coordinates artifact export requests against registered artifact exporters.
/// </summary>
public sealed class ArtifactExportService
{
    private readonly IReadOnlyDictionary<ArtifactFormat, IArtifactExporter> exporters;
    private readonly ILogger<ArtifactExportService> logger;
    private ArtifactExportOptions options;
    private readonly TimeProvider timeProvider;
    private readonly IDiagnosticWriter diagnosticWriter;
    private bool previousWorkflowIsExportable;
    private RunEpochId? lastAutomaticallyExportedRunEpochId;

    public ArtifactExportService(
        CoreServiceContext coreContext,
        IEnumerable<IArtifactExporter>? exporters = null,
        ArtifactExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(coreContext);

        logger = coreContext.LoggerFactory.CreateLogger<ArtifactExportService>() ?? NullLogger<ArtifactExportService>.Instance;
        this.options = options ?? ArtifactExportOptions.Default;
        timeProvider = coreContext.TimeProvider;
        diagnosticWriter = coreContext.Diagnostics.Writer;
        this.exporters = CreateExporterMap(exporters ?? []);

        foreach (var exporter in this.exporters.Values)
        {
            exporter.DiagnosticEventHandler += (_, args) => AddDiagnostic(args.Diagnostic);
        }
    }

    public OutputState Current { get; private set; } = new([], null, null);

    public event EventHandler<OutputStateChangedEventArgs>? OutputStateChangedEventHandler;

    public void UpdateOptions(ArtifactExportOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public void OnWorkflowPublication(object? sender, RunWorkflowPublicationEventArgs args)
    {
        _ = ExportOnWorkflowPublicationEventAsync(args.Publication);
    }

    private async Task ExportOnWorkflowPublicationEventAsync(RunWorkflowPublication publication)
    {
        try
        {
            await ExportIfPublicationBecameExportableAsync(publication, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Automatic artifact export failed.");
            AddDiagnostic(new Diagnostic(
                "ArtifactExport.AutomaticExportFailed",
                DiagnosticCategory.Output,
                LogLevel.Error,
                DiagnosticSource.OutputCoordinator,
                DiagnosticPhase.OutputCoordination,
                "Automatic test result artifact export failed.",
                new DiagnosticCorrelation(UnitTestRunEpochId: publication.CurrentRun?.EpochId),
                timeProvider.GetUtcNow(),
                TechnicalDetail: ex.Message));
        }
    }

    public Task ObserveWorkflowPublicationAsync(
        RunWorkflowPublication publication,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        cancellationToken.ThrowIfCancellationRequested();

        return ExportIfPublicationBecameExportableAsync(publication, cancellationToken);
    }

    private Task ExportIfPublicationBecameExportableAsync(
        RunWorkflowPublication publication,
        CancellationToken cancellationToken)
    {
        var isExportable = publication.Status.IsExportable;
        var runEpoch = publication.CurrentRun;
        var shouldExport = !previousWorkflowIsExportable
            && isExportable
            && runEpoch is not null
            && runEpoch.EpochId != lastAutomaticallyExportedRunEpochId;

        previousWorkflowIsExportable = isExportable;
        if (!shouldExport)
        {
            return Task.CompletedTask;
        }

        lastAutomaticallyExportedRunEpochId = runEpoch!.EpochId;
        return ExportAsync(runEpoch, cancellationToken);
    }

    public async Task<OutputState> ExportAsync(
        UnitTestRunEpochSnapshot runEpoch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runEpoch);
        cancellationToken.ThrowIfCancellationRequested();

        var requests = CreateRequests();
        logger.LogDebug(
            "Artifact export started. RequestCount={RequestCount}, RunEpochId={RunEpochId}",
            requests.Count,
            runEpoch.EpochId.Value);

        var results = requests
            .Select(request => new ExportResult(request.Format, ArtifactState.Pending, request.OutputPath))
            .ToArray();
        Publish(results, runEpoch.EpochId);

        for (var index = 0; index < requests.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = requests[index];
            logger.LogInformation(
                "Artifact export requested. Format={Format}, Required={Required}, OutputPath={OutputPath}, RunEpochId={RunEpochId}",
                request.Format,
                request.Required,
                request.OutputPath,
                runEpoch.EpochId.Value);

            if (!exporters.TryGetValue(request.Format, out var exporter))
            {
                results[index] = new ExportResult(
                    request.Format,
                    request.Required ? ArtifactState.Failed : ArtifactState.Skipped,
                    request.OutputPath);
                ReportArtifactDiagnostic(
                    "ArtifactExport.ExporterMissing",
                    request.Required ? LogLevel.Error : LogLevel.Warning,
                    $"No artifact exporter is registered for format '{request.Format}'.",
                    request,
                    runEpoch);
                LogArtifactResult(results[index], runEpoch);
                Publish(results, runEpoch.EpochId);
                continue;
            }

            ExportResult exportResult;
            var artifactDiagnosticReported = false;
            try
            {
                exportResult = await exporter.ExportAsync(request, runEpoch, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Artifact export failed with exception. Format={Format}, Required={Required}, OutputPath={OutputPath}, RunEpochId={RunEpochId}",
                    request.Format,
                    request.Required,
                    request.OutputPath,
                    runEpoch.EpochId.Value);
                exportResult = new ExportResult(request.Format, ArtifactState.Failed, request.OutputPath);
                ReportArtifactDiagnostic(
                    "ArtifactExport.ExporterException",
                    request.Required ? LogLevel.Error : LogLevel.Warning,
                    $"Artifact exporter failed for format '{request.Format}'.",
                    request,
                    runEpoch,
                    ex.Message);
                artifactDiagnosticReported = true;
            }

            results[index] = exportResult;
            if (!artifactDiagnosticReported
                && (exportResult.State is ArtifactState.Failed
                    || (request.Required && exportResult.State is ArtifactState.Skipped)))
            {
                ReportArtifactDiagnostic(
                    $"ArtifactExport.{exportResult.State}",
                    request.Required ? LogLevel.Error : LogLevel.Warning,
                    $"Artifact export finished with state '{exportResult.State}' for format '{request.Format}'.",
                    request,
                    runEpoch);
            }

            LogArtifactResult(exportResult, runEpoch);
            Publish(results, runEpoch.EpochId);
        }

        logger.LogDebug(
            "Artifact export finished. RequestCount={RequestCount}, RunEpochId={RunEpochId}",
            requests.Count,
            runEpoch.EpochId.Value);
        return Current;
    }

    private IReadOnlyList<ExportRequest> CreateRequests()
    {
        return options.TestResult
            ? [new ExportRequest(
                ArtifactFormat.JUnitXml,
                Path.Combine(options.OutputDirectory, options.TestResultFileName),
                Required: true)]
            : [];
    }

    private void Publish(
        IReadOnlyList<ExportResult> results,
        RunEpochId runEpochId)
    {
        Publish(new OutputState(
            results.ToArray(),
            null,
            CreateTerminalText(results)),
            runEpochId);
    }

    private static string? CreateTerminalText(IReadOnlyList<ExportResult> results)
    {
        if (results.Count == 0)
        {
            return null;
        }

        return string.Join(
            Environment.NewLine,
            results.Select(static result =>
            {
                var path = string.IsNullOrWhiteSpace(result.OutputPath)
                    ? "<no output path>"
                    : result.OutputPath;
                return $"Artifact {result.Format} {result.State.ToString().ToLowerInvariant()}: {path}";
            }));
    }

    private void Publish(
        OutputState outputState,
        RunEpochId? runEpochId)
    {
        Current = outputState;
        OutputStateChangedEventHandler?.Invoke(this, new OutputStateChangedEventArgs(outputState, runEpochId));
    }

    private void ReportArtifactDiagnostic(
        string code,
        LogLevel level,
        string message,
        ExportRequest request,
        UnitTestRunEpochSnapshot runEpoch,
        string? technicalDetail = null)
    {
        var diagnostic = new Diagnostic(
            code,
            DiagnosticCategory.Output,
            level,
            DiagnosticSource.OutputCoordinator,
            DiagnosticPhase.OutputCoordination,
            message,
            new DiagnosticCorrelation(
                UnitTestRunEpochId: runEpoch.EpochId,
                OutputRequestId: new OutputRequestId($"{request.Format}:{request.OutputPath}")),
            timeProvider.GetUtcNow(),
            TechnicalDetail: technicalDetail);
        AddDiagnostic(diagnostic);
    }

    private void AddDiagnostic(Diagnostic diagnostic)
    {
        logger.Log(
            diagnostic.Level,
            "Artifact export diagnostic. Code={Code}, Category={Category}, Source={Source}, Phase={Phase}, Message={Message}",
            diagnostic.Code,
            diagnostic.Category,
            diagnostic.Source,
            diagnostic.Phase,
            diagnostic.Message);
        diagnosticWriter.Add(diagnostic);
    }

    private void LogArtifactResult(ExportResult result, UnitTestRunEpochSnapshot runEpoch)
    {
        var logLevel = result.State == ArtifactState.Failed ? LogLevel.Warning : LogLevel.Information;
        logger.Log(
            logLevel,
            "Artifact export result. Format={Format}, State={State}, OutputPath={OutputPath}, RunEpochId={RunEpochId}",
            result.Format,
            result.State,
            result.OutputPath,
            runEpoch.EpochId.Value);
    }

    private static IReadOnlyDictionary<ArtifactFormat, IArtifactExporter> CreateExporterMap(
        IEnumerable<IArtifactExporter> exporters)
    {
        var groups = exporters
            .GroupBy(exporter => exporter.Format)
            .ToArray();
        var duplicate = groups.FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Multiple artifact exporters are registered for format '{duplicate.Key}'.");
        }

        return groups.ToDictionary(group => group.Key, group => group.Single());
    }
}

public sealed record ArtifactExportOptions(
    string OutputDirectory,
    bool TestResult,
    string TestResultFileName = "junit.xml")
{
    public static ArtifactExportOptions Default { get; } = new("artifacts", true, "junit.xml");
}
