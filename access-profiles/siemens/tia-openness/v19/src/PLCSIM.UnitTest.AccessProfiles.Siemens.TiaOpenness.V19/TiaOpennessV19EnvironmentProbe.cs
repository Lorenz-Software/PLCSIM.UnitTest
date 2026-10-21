using Microsoft.Win32;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using System.Reflection;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19;

internal interface ITiaOpennessV19EnvironmentProbe
{
    Task<TiaOpennessV19EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

internal interface ITiaOpennessV19InstallationEvidenceSource
{
    TiaOpennessV19InstallationEvidence? Capture();
}

internal sealed class TiaOpennessV19EnvironmentProbe : ITiaOpennessV19EnvironmentProbe
{
    private const string ExpectedAssemblyVersion = "19.0.0.0";
    private const string EngineeringAssemblyName = "Siemens.Engineering";

    private readonly ITiaOpennessV19InstallationEvidenceSource installationEvidenceSource;
    private readonly Func<string> workerPathResolver;

    public TiaOpennessV19EnvironmentProbe()
        : this(
            new TiaOpennessV19RegistryEvidenceSource(),
            TiaOpennessV19WorkerProcessHost.ResolveDeployedWorkerPath)
    {
    }

    internal TiaOpennessV19EnvironmentProbe(
        ITiaOpennessV19InstallationEvidenceSource installationEvidenceSource,
        Func<string>? workerPathResolver = null)
    {
        this.installationEvidenceSource = installationEvidenceSource
            ?? throw new ArgumentNullException(nameof(installationEvidenceSource));
        this.workerPathResolver = workerPathResolver
            ?? TiaOpennessV19WorkerProcessHost.ResolveDeployedWorkerPath;
    }

    public Task<TiaOpennessV19EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TiaOpennessV19InstallationEvidence? installation;
        try
        {
            installation = installationEvidenceSource.Capture();
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException)
        {
            return Task.FromResult(TiaOpennessV19EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V19.RegistryReadFailed",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V19 registry evidence could not be read.",
                    "EnvironmentProbe",
                    "tia-openness-v19-registry"),
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v19-registry",
                        "RegistryReadFailure",
                        ex.Message)
                }));
        }

        if (installation is null)
        {
            return Task.FromResult(TiaOpennessV19EnvironmentProbeResult.Unavailable(
                new TiaDiagnostic(
                    "TiaOpenness.V19.RegistryEvidenceMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V19 registry evidence was not found at the documented PublicAPI key.",
                    "EnvironmentProbe",
                    "tia-openness-v19-registry"),
                Array.Empty<TiaEvidence>()));
        }

        var evidence = new List<TiaEvidence>
        {
            new(
                "tia-openness-v19-registry",
                "RegistryKey",
                TiaOpennessV19RegistryEvidenceSource.RegistryPath)
        };

        if (string.IsNullOrWhiteSpace(installation.AssemblyVersion)
            || string.IsNullOrWhiteSpace(installation.PublicKeyToken)
            || string.IsNullOrWhiteSpace(installation.EngineeringAssemblyPath))
        {
            return Task.FromResult(TiaOpennessV19EnvironmentProbeResult.Unavailable(
                new TiaDiagnostic(
                    "TiaOpenness.V19.RegistryEvidenceIncomplete",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V19 registry evidence does not contain the required PublicAPI values.",
                    "EnvironmentProbe",
                    "tia-openness-v19-registry"),
                evidence));
        }

        if (!string.Equals(
                NormalizeVersion(installation.AssemblyVersion),
                ExpectedAssemblyVersion,
                StringComparison.Ordinal))
        {
            return Task.FromResult(TiaOpennessV19EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V19.RegistryVersionMismatch",
                    TiaDiagnosticSeverity.Error,
                    $"TIA Openness V19 registry evidence reports unsupported assembly version '{installation.AssemblyVersion}'.",
                    "EnvironmentProbe",
                    "tia-openness-v19-registry"),
                evidence));
        }

        string engineeringAssemblyPath;
        try
        {
            engineeringAssemblyPath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(installation.EngineeringAssemblyPath));
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            return Task.FromResult(TiaOpennessV19EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V19.PublicApiPathInvalid",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V19 registry evidence contains an invalid PublicAPI assembly path.",
                    "EnvironmentProbe",
                    "tia-openness-v19-registry"),
                evidence.Concat(new[]
                {
                    new TiaEvidence(
                        "tia-openness-v19-public-api-path-error",
                        "PublicApiPathError",
                        ex.Message)
                }).ToArray()));
        }

        var publicApiDirectory = Path.GetDirectoryName(engineeringAssemblyPath);
        if (string.IsNullOrWhiteSpace(publicApiDirectory))
        {
            return Task.FromResult(TiaOpennessV19EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V19.PublicApiPathInvalid",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V19 registry evidence does not resolve to a PublicAPI directory.",
                    "EnvironmentProbe",
                    "tia-openness-v19-registry"),
                evidence));
        }

        evidence.Add(new TiaEvidence(
            "tia-openness-v19-public-api",
            "PublicApiDirectory",
            publicApiDirectory));

        var expectedPublicKeyToken = NormalizePublicKeyToken(installation.PublicKeyToken);
        var requiredAssemblies = new[]
        {
            new RequiredAssembly(EngineeringAssemblyName, engineeringAssemblyPath)
        };

        foreach (var requiredAssembly in requiredAssemblies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var validation = ValidateAssembly(requiredAssembly, expectedPublicKeyToken, evidence);
            if (validation is not null)
            {
                return Task.FromResult(validation);
            }
        }

        string workerPath;
        try
        {
            workerPath = workerPathResolver();
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return Task.FromResult(TiaOpennessV19EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V19.WorkerPathInvalid",
                    TiaDiagnosticSeverity.Error,
                    "The deployed TIA Openness V19 worker path could not be resolved.",
                    "EnvironmentProbe",
                    "tia-openness-v19-worker"),
                evidence.Concat(new[]
                {
                    new TiaEvidence(
                        "tia-openness-v19-worker-path-error",
                        "WorkerPathError",
                        ex.Message)
                }).ToArray()));
        }

        var workerValidation = ValidateAssembly(
            new RequiredAssembly(
                "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19.Worker",
                workerPath),
            expectedPublicKeyToken: null,
            evidence,
            missingCode: "TiaOpenness.V19.WorkerMissing",
            invalidCode: "TiaOpenness.V19.WorkerInvalid",
            evidenceId: "tia-openness-v19-worker",
            expectedAssemblyVersion: null,
            missingIsFailure: true);
        if (workerValidation is not null)
        {
            return Task.FromResult(workerValidation);
        }

        return Task.FromResult(TiaOpennessV19EnvironmentProbeResult.Ready(
            publicApiDirectory,
            evidence));
    }

    private static TiaOpennessV19EnvironmentProbeResult? ValidateAssembly(
        RequiredAssembly requiredAssembly,
        string? expectedPublicKeyToken,
        List<TiaEvidence> evidence,
        string missingCode = "TiaOpenness.V19.PublicApiAssemblyMissing",
        string invalidCode = "TiaOpenness.V19.PublicApiAssemblyInvalid",
        string evidenceId = "tia-openness-v19-public-api",
        string? expectedAssemblyVersion = ExpectedAssemblyVersion,
        bool missingIsFailure = false)
    {
        try
        {
            _ = File.GetAttributes(requiredAssembly.Path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            var diagnostic = new TiaDiagnostic(
                missingCode,
                TiaDiagnosticSeverity.Error,
                $"Required TIA Openness V19 component '{Path.GetFileName(requiredAssembly.Path)}' was not found.",
                "EnvironmentProbe",
                evidenceId);
            return missingIsFailure
                ? TiaOpennessV19EnvironmentProbeResult.Failed(diagnostic, evidence)
                : TiaOpennessV19EnvironmentProbeResult.Unavailable(diagnostic, evidence);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException)
        {
            return TiaOpennessV19EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    invalidCode,
                    TiaDiagnosticSeverity.Error,
                    $"Required TIA Openness V19 component '{Path.GetFileName(requiredAssembly.Path)}' could not be accessed.",
                    "EnvironmentProbe",
                    evidenceId),
                evidence.Concat(new[]
                {
                    new TiaEvidence(
                        $"{evidenceId}-assembly-access-error",
                        "AssemblyAccessFailure",
                        ex.Message)
                }).ToArray());
        }

        try
        {
            var assemblyName = AssemblyName.GetAssemblyName(requiredAssembly.Path);
            var actualPublicKeyToken = NormalizePublicKeyToken(assemblyName.GetPublicKeyToken());
            if (!string.Equals(assemblyName.Name, requiredAssembly.Name, StringComparison.Ordinal)
                || (expectedAssemblyVersion is not null
                    && !string.Equals(assemblyName.Version?.ToString(), expectedAssemblyVersion, StringComparison.Ordinal))
                || (expectedPublicKeyToken is not null
                    && !string.Equals(actualPublicKeyToken, expectedPublicKeyToken, StringComparison.OrdinalIgnoreCase)))
            {
                return TiaOpennessV19EnvironmentProbeResult.Failed(
                    new TiaDiagnostic(
                        invalidCode,
                        TiaDiagnosticSeverity.Error,
                        $"Required TIA Openness V19 component '{Path.GetFileName(requiredAssembly.Path)}' has incompatible assembly identity.",
                        "EnvironmentProbe",
                        evidenceId),
                    evidence);
            }

            evidence.Add(new TiaEvidence(
                $"{evidenceId}-{requiredAssembly.Name.ToLowerInvariant().Replace('.', '-')}",
                "Assembly",
                $"{assemblyName.Name}, Version={assemblyName.Version}, PublicKeyToken={actualPublicKeyToken}"));
            return null;
        }
        catch (Exception ex) when (ex is BadImageFormatException
            or FileLoadException
            or FileNotFoundException
            or IOException
            or UnauthorizedAccessException)
        {
            return TiaOpennessV19EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    invalidCode,
                    TiaDiagnosticSeverity.Error,
                    $"Required TIA Openness V19 component '{Path.GetFileName(requiredAssembly.Path)}' could not be inspected.",
                    "EnvironmentProbe",
                    evidenceId),
                evidence.Concat(new[]
                {
                    new TiaEvidence(
                        $"{evidenceId}-assembly-error",
                        "AssemblyInspectionFailure",
                        ex.Message)
                }).ToArray());
        }
    }

    private static string NormalizeVersion(string value)
    {
        var normalized = value.Trim().TrimStart('V', 'v');
        return Version.TryParse(normalized, out var version)
            && version.Build >= 0
            && version.Revision >= 0
                ? version.ToString(4)
                : normalized;
    }

    private static string NormalizePublicKeyToken(string value)
    {
        return value.Trim().Replace(" ", string.Empty).ToLowerInvariant();
    }

    private static string NormalizePublicKeyToken(byte[]? value)
    {
        return value is null ? string.Empty : Convert.ToHexString(value).ToLowerInvariant();
    }

    private sealed record RequiredAssembly(string Name, string Path);
}

internal sealed class TiaOpennessV19RegistryEvidenceSource : ITiaOpennessV19InstallationEvidenceSource
{
    internal const string RegistryPath =
        @"SOFTWARE\Siemens\Automation\Openness\19.0\PublicAPI\19.0.0.0";

    public TiaOpennessV19InstallationEvidence? Capture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = localMachine.OpenSubKey(RegistryPath);
        if (key is null)
        {
            return null;
        }

        return new TiaOpennessV19InstallationEvidence(
            key.GetValue("AssemblyVersion") as string,
            key.GetValue("PublicKeyToken") as string,
            key.GetValue("Siemens.Engineering") as string);
    }
}

internal sealed record TiaOpennessV19InstallationEvidence(
    string? AssemblyVersion,
    string? PublicKeyToken,
    string? EngineeringAssemblyPath);

internal sealed record TiaOpennessV19EnvironmentProbeResult(
    TiaOpennessV19EnvironmentProbeState State,
    string? PublicApiDirectory,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool IsReady => State == TiaOpennessV19EnvironmentProbeState.Ready;

    public static TiaOpennessV19EnvironmentProbeResult Ready(
        string publicApiDirectory,
        IReadOnlyList<TiaEvidence> evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicApiDirectory);

        return new TiaOpennessV19EnvironmentProbeResult(
            TiaOpennessV19EnvironmentProbeState.Ready,
            publicApiDirectory,
            Array.Empty<TiaDiagnostic>(),
            evidence);
    }

    public static TiaOpennessV19EnvironmentProbeResult Unavailable(
        TiaDiagnostic diagnostic,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV19EnvironmentProbeResult(
            TiaOpennessV19EnvironmentProbeState.Unavailable,
            null,
            new[] { diagnostic },
            evidence);
    }

    public static TiaOpennessV19EnvironmentProbeResult Failed(
        TiaDiagnostic diagnostic,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV19EnvironmentProbeResult(
            TiaOpennessV19EnvironmentProbeState.Failed,
            null,
            new[] { diagnostic },
            evidence);
    }
}

internal enum TiaOpennessV19EnvironmentProbeState
{
    Ready,
    Unavailable,
    Failed
}
