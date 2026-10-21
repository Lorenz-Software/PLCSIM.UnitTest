using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Core.Diagnostics;
using PLCSIM.UnitTest.Core.Workflow;

namespace PLCSIM.UnitTest.App.Wpf.Services;

public interface IConfigurationFileLoadService
{
    Task<ConfigurationFileLoadResult> LoadAsync(string path, CancellationToken cancellationToken);
}

public sealed record ConfigurationFileLoadResult(
    string Path,
    bool IsValid,
    RunConfigurationFile? Configuration,
    RunSetup? Setup,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed class ConfigurationFileLoadService : IConfigurationFileLoadService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly RunSetupValidator setupValidator;
    private readonly TimeProvider timeProvider;
    private readonly IDiagnosticWriter diagnosticWriter;
    private readonly ILogger<ConfigurationFileLoadService> logger;

    public ConfigurationFileLoadService(
        RunSetupValidator setupValidator,
        TimeProvider timeProvider,
        IDiagnosticWriter diagnosticWriter,
        ILogger<ConfigurationFileLoadService> logger)
    {
        this.setupValidator = setupValidator;
        this.timeProvider = timeProvider;
        this.diagnosticWriter = diagnosticWriter;
        this.logger = logger;
    }

    public async Task<ConfigurationFileLoadResult> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Failure(
                path,
                CreateDiagnostic(
                    "Wpf.Configuration.FilePath.Missing",
                    "Configuration file path is missing."));
        }

        if (!File.Exists(path))
        {
            return Failure(
                path,
                CreateDiagnostic(
                    "Wpf.Configuration.File.NotFound",
                    $"Configuration file '{path}' does not exist.",
                    evidenceValue: path));
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(
                path,
                CreateDiagnostic(
                    "Wpf.Configuration.File.ReadFailed",
                    $"Configuration file '{path}' could not be read.",
                    ex.ToString(),
                    path));
        }

        RunConfigurationFile? configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<RunConfigurationFile>(json, JsonOptions);
            configuration = RemoveProjectAuthorization(configuration);
        }
        catch (JsonException ex)
        {
            return Failure(
                path,
                CreateDiagnostic(
                    "Wpf.Configuration.Json.Invalid",
                    $"Configuration file '{path}' is not valid JSON for the run configuration contract.",
                    ex.ToString(),
                    path));
        }

        var shapeDiagnostics = ValidateShape(path, configuration);
        if (shapeDiagnostics.Count > 0)
        {
            diagnosticWriter.AddRange(shapeDiagnostics);
            LogConfigurationFailure(path, shapeDiagnostics);
            return new ConfigurationFileLoadResult(path, false, configuration, null, shapeDiagnostics);
        }

        var setup = setupValidator.CreateFromConfiguration(configuration!);
        var setupValidation = setupValidator.ValidateDraft(setup);
        var diagnostics = setupValidation.Diagnostics.ToList();
        diagnosticWriter.AddRange(diagnostics);
        if (!setupValidation.IsValid)
        {
            LogConfigurationFailure(path, diagnostics);
        }

        return new ConfigurationFileLoadResult(
            path,
            setupValidation.IsValid,
            configuration,
            setupValidation.Setup,
            diagnostics);
    }

    private List<Diagnostic> ValidateShape(string path, RunConfigurationFile? configuration)
    {
        return RunConfigurationShapeValidator.Validate(configuration)
            .Select(error => CreateDiagnostic("Wpf.Configuration." + error.Code, error.Message, evidenceValue: path))
            .ToList();
    }

    private static RunConfigurationFile? RemoveProjectAuthorization(RunConfigurationFile? configuration)
    {
        if (configuration?.Project?.Authorization is null)
        {
            return configuration;
        }

        return configuration with
        {
            Project = configuration.Project with { Authorization = null }
        };
    }

    private ConfigurationFileLoadResult Failure(string path, Diagnostic diagnostic)
    {
        diagnosticWriter.Add(diagnostic);
        LogConfigurationFailure(path, new[] { diagnostic });
        return new ConfigurationFileLoadResult(path, false, null, null, new[] { diagnostic });
    }

    private void LogConfigurationFailure(string path, IReadOnlyCollection<Diagnostic> diagnostics)
    {
        logger.LogWarning(
            "Run configuration load failed. Path={ConfigurationPath}, DiagnosticCount={DiagnosticCount}, DiagnosticDetails={DiagnosticDetails}",
            path,
            diagnostics.Count,
            FormatDiagnosticDetails(diagnostics));
    }

    private static string FormatDiagnosticDetails(IReadOnlyCollection<Diagnostic> diagnostics)
    {
        return diagnostics.Count == 0
            ? "-"
            : string.Join("; ", diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
    }

    private Diagnostic CreateDiagnostic(
        string code,
        string message,
        string? technicalDetail = null,
        string? evidenceValue = null)
    {
        return new Diagnostic(
            code,
            DiagnosticCategory.Configuration,
            LogLevel.Error,
            DiagnosticSource.WpfApplication,
            DiagnosticPhase.Configuration,
            message,
            new DiagnosticCorrelation(),
            timeProvider.GetUtcNow(),
            TechnicalDetail: technicalDetail,
            EvidenceReference: evidenceValue is null
                ? null
                : new DiagnosticEvidenceReference("configuration-file", evidenceValue));
    }
}
