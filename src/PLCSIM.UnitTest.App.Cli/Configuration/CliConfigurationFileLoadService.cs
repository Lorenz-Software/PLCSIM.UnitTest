using System.Text.Json;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Core.Workflow;

namespace PLCSIM.UnitTest.App.Cli.Configuration;

public interface ICliConfigurationFileLoadService
{
    Task<CliConfigurationFileLoadResult> LoadAsync(string path, CancellationToken cancellationToken);
}

public sealed record CliConfigurationFileLoadResult(
    string Path,
    bool IsValid,
    RunConfigurationFile? Configuration,
    RunSetup? Setup,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed class CliConfigurationFileLoadService : ICliConfigurationFileLoadService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly RunSetupValidator setupValidator;
    private readonly TimeProvider timeProvider;

    public CliConfigurationFileLoadService(
        RunSetupValidator setupValidator,
        TimeProvider timeProvider)
    {
        this.setupValidator = setupValidator ?? throw new ArgumentNullException(nameof(setupValidator));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<CliConfigurationFileLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(path))
        {
            return Failure(path, CreateDiagnostic(
                "Cli.Configuration.FilePath.Missing",
                "Configuration file path is missing."));
        }

        if (!File.Exists(path))
        {
            return Failure(path, CreateDiagnostic(
                "Cli.Configuration.File.NotFound",
                $"Configuration file '{path}' does not exist.",
                path));
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
            return Failure(path, CreateDiagnostic(
                "Cli.Configuration.File.ReadFailed",
                $"Configuration file '{path}' could not be read.",
                path,
                ex.ToString()));
        }

        RunConfigurationFile? configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<RunConfigurationFile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Failure(path, CreateDiagnostic(
                "Cli.Configuration.Json.Invalid",
                $"Configuration file '{path}' is not valid JSON for the run configuration contract.",
                path,
                ex.ToString()));
        }

        var shapeDiagnostics = ValidateShape(path, configuration);
        if (shapeDiagnostics.Count > 0)
        {
            return new CliConfigurationFileLoadResult(
                path,
                false,
                configuration,
                null,
                shapeDiagnostics);
        }

        var setup = setupValidator.CreateFromConfiguration(configuration!);
        var setupValidation = setupValidator.ValidateDraft(setup);
        return new CliConfigurationFileLoadResult(
            path,
            setupValidation.IsValid,
            configuration,
            setupValidation.Setup,
            setupValidation.Diagnostics);
    }

    private List<Diagnostic> ValidateShape(string path, RunConfigurationFile? configuration)
    {
        return RunConfigurationShapeValidator.Validate(configuration)
            .Select(error => CreateDiagnostic("Cli.Configuration." + error.Code, error.Message, evidenceValue: path))
            .ToList();
    }

    private static CliConfigurationFileLoadResult Failure(string path, Diagnostic diagnostic)
    {
        return new CliConfigurationFileLoadResult(path, false, null, null, [diagnostic]);
    }

    private Diagnostic CreateDiagnostic(
        string code,
        string message,
        string? evidenceValue = null,
        string? technicalDetail = null)
    {
        return new Diagnostic(
            code,
            DiagnosticCategory.Configuration,
            Microsoft.Extensions.Logging.LogLevel.Error,
            DiagnosticSource.CliApplication,
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
