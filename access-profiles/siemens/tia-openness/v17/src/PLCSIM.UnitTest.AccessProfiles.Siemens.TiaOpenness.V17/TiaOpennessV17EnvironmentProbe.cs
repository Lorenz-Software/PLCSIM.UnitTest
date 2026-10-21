using Microsoft.Win32;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using System.Reflection;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17;

internal interface ITiaOpennessV17EnvironmentProbe
{
    Task<TiaOpennessV17EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

internal interface ITiaOpennessV17InstallationEvidenceSource
{
    TiaOpennessV17InstallationEvidence? Capture();
}

internal sealed class TiaOpennessV17EnvironmentProbe : ITiaOpennessV17EnvironmentProbe
{
    private const string ExpectedAssemblyVersion = "17.0.0.0";
    private const string EngineeringAssemblyName = "Siemens.Engineering";

    private readonly ITiaOpennessV17InstallationEvidenceSource installationEvidenceSource;
    private readonly Func<string> workerPathResolver;

    public TiaOpennessV17EnvironmentProbe()
        : this(
            new TiaOpennessV17RegistryEvidenceSource(),
            TiaOpennessV17WorkerProcessHost.ResolveDeployedWorkerPath)
    {
    }

    internal TiaOpennessV17EnvironmentProbe(
        ITiaOpennessV17InstallationEvidenceSource installationEvidenceSource,
        Func<string>? workerPathResolver = null)
    {
        this.installationEvidenceSource = installationEvidenceSource
            ?? throw new ArgumentNullException(nameof(installationEvidenceSource));
        this.workerPathResolver = workerPathResolver
            ?? TiaOpennessV17WorkerProcessHost.ResolveDeployedWorkerPath;
    }

    public Task<TiaOpennessV17EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TiaOpennessV17InstallationEvidence? installation;
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
            return Task.FromResult(TiaOpennessV17EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V17.RegistryReadFailed",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V17 registry evidence could not be read.",
                    "EnvironmentProbe",
                    "tia-openness-v17-registry"),
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v17-registry",
                        "RegistryReadFailure",
                        ex.Message)
                }));
        }

        if (installation is null)
        {
            return Task.FromResult(TiaOpennessV17EnvironmentProbeResult.Unavailable(
                new TiaDiagnostic(
                    "TiaOpenness.V17.RegistryEvidenceMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V17 registry evidence was not found at the documented PublicAPI key.",
                    "EnvironmentProbe",
                    "tia-openness-v17-registry"),
                Array.Empty<TiaEvidence>()));
        }

        var evidence = new List<TiaEvidence>
        {
            new(
                "tia-openness-v17-registry",
                "RegistryKey",
                TiaOpennessV17RegistryEvidenceSource.RegistryPath)
        };

        if (string.IsNullOrWhiteSpace(installation.AssemblyVersion)
            || string.IsNullOrWhiteSpace(installation.PublicKeyToken)
            || string.IsNullOrWhiteSpace(installation.EngineeringAssemblyPath))
        {
            return Task.FromResult(TiaOpennessV17EnvironmentProbeResult.Unavailable(
                new TiaDiagnostic(
                    "TiaOpenness.V17.RegistryEvidenceIncomplete",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V17 registry evidence does not contain the required PublicAPI values.",
                    "EnvironmentProbe",
                    "tia-openness-v17-registry"),
                evidence));
        }

        if (!string.Equals(
                NormalizeVersion(installation.AssemblyVersion),
                ExpectedAssemblyVersion,
                StringComparison.Ordinal))
        {
            return Task.FromResult(TiaOpennessV17EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V17.RegistryVersionMismatch",
                    TiaDiagnosticSeverity.Error,
                    $"TIA Openness V17 registry evidence reports unsupported assembly version '{installation.AssemblyVersion}'.",
                    "EnvironmentProbe",
                    "tia-openness-v17-registry"),
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
            return Task.FromResult(TiaOpennessV17EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V17.PublicApiPathInvalid",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V17 registry evidence contains an invalid PublicAPI assembly path.",
                    "EnvironmentProbe",
                    "tia-openness-v17-registry"),
                evidence.Concat(new[]
                {
                    new TiaEvidence(
                        "tia-openness-v17-public-api-path-error",
                        "PublicApiPathError",
                        ex.Message)
                }).ToArray()));
        }

        var publicApiDirectory = Path.GetDirectoryName(engineeringAssemblyPath);
        if (string.IsNullOrWhiteSpace(publicApiDirectory))
        {
            return Task.FromResult(TiaOpennessV17EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V17.PublicApiPathInvalid",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V17 registry evidence does not resolve to a PublicAPI directory.",
                    "EnvironmentProbe",
                    "tia-openness-v17-registry"),
                evidence));
        }

        evidence.Add(new TiaEvidence(
            "tia-openness-v17-public-api",
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
            return Task.FromResult(TiaOpennessV17EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V17.WorkerPathInvalid",
                    TiaDiagnosticSeverity.Error,
                    "The deployed TIA Openness V17 worker path could not be resolved.",
                    "EnvironmentProbe",
                    "tia-openness-v17-worker"),
                evidence.Concat(new[]
                {
                    new TiaEvidence(
                        "tia-openness-v17-worker-path-error",
                        "WorkerPathError",
                        ex.Message)
                }).ToArray()));
        }

        var workerValidation = ValidateAssembly(
            new RequiredAssembly(
                "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17.Worker",
                workerPath),
            expectedPublicKeyToken: null,
            evidence,
            missingCode: "TiaOpenness.V17.WorkerMissing",
            invalidCode: "TiaOpenness.V17.WorkerInvalid",
            evidenceId: "tia-openness-v17-worker",
            expectedAssemblyVersion: null,
            missingIsFailure: true);
        if (workerValidation is not null)
        {
            return Task.FromResult(workerValidation);
        }

        return Task.FromResult(TiaOpennessV17EnvironmentProbeResult.Ready(
            publicApiDirectory,
            evidence));
    }

    private static TiaOpennessV17EnvironmentProbeResult? ValidateAssembly(
        RequiredAssembly requiredAssembly,
        string? expectedPublicKeyToken,
        List<TiaEvidence> evidence,
        string missingCode = "TiaOpenness.V17.PublicApiAssemblyMissing",
        string invalidCode = "TiaOpenness.V17.PublicApiAssemblyInvalid",
        string evidenceId = "tia-openness-v17-public-api",
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
                $"Required TIA Openness V17 component '{Path.GetFileName(requiredAssembly.Path)}' was not found.",
                "EnvironmentProbe",
                evidenceId);
            return missingIsFailure
                ? TiaOpennessV17EnvironmentProbeResult.Failed(diagnostic, evidence)
                : TiaOpennessV17EnvironmentProbeResult.Unavailable(diagnostic, evidence);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException)
        {
            return TiaOpennessV17EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    invalidCode,
                    TiaDiagnosticSeverity.Error,
                    $"Required TIA Openness V17 component '{Path.GetFileName(requiredAssembly.Path)}' could not be accessed.",
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
                return TiaOpennessV17EnvironmentProbeResult.Failed(
                    new TiaDiagnostic(
                        invalidCode,
                        TiaDiagnosticSeverity.Error,
                        $"Required TIA Openness V17 component '{Path.GetFileName(requiredAssembly.Path)}' has incompatible assembly identity.",
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
            return TiaOpennessV17EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    invalidCode,
                    TiaDiagnosticSeverity.Error,
                    $"Required TIA Openness V17 component '{Path.GetFileName(requiredAssembly.Path)}' could not be inspected.",
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

internal sealed class TiaOpennessV17RegistryEvidenceSource : ITiaOpennessV17InstallationEvidenceSource
{
    internal const string RegistryPath =
        @"SOFTWARE\Siemens\Automation\Openness\17.0\PublicAPI\17.0.0.0";

    public TiaOpennessV17InstallationEvidence? Capture()
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

        return new TiaOpennessV17InstallationEvidence(
            key.GetValue("AssemblyVersion") as string,
            key.GetValue("PublicKeyToken") as string,
            key.GetValue("Siemens.Engineering") as string);
    }
}

internal sealed record TiaOpennessV17InstallationEvidence(
    string? AssemblyVersion,
    string? PublicKeyToken,
    string? EngineeringAssemblyPath);

internal sealed record TiaOpennessV17EnvironmentProbeResult(
    TiaOpennessV17EnvironmentProbeState State,
    string? PublicApiDirectory,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool IsReady => State == TiaOpennessV17EnvironmentProbeState.Ready;

    public static TiaOpennessV17EnvironmentProbeResult Ready(
        string publicApiDirectory,
        IReadOnlyList<TiaEvidence> evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicApiDirectory);

        return new TiaOpennessV17EnvironmentProbeResult(
            TiaOpennessV17EnvironmentProbeState.Ready,
            publicApiDirectory,
            Array.Empty<TiaDiagnostic>(),
            evidence);
    }

    public static TiaOpennessV17EnvironmentProbeResult Unavailable(
        TiaDiagnostic diagnostic,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV17EnvironmentProbeResult(
            TiaOpennessV17EnvironmentProbeState.Unavailable,
            null,
            new[] { diagnostic },
            evidence);
    }

    public static TiaOpennessV17EnvironmentProbeResult Failed(
        TiaDiagnostic diagnostic,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV17EnvironmentProbeResult(
            TiaOpennessV17EnvironmentProbeState.Failed,
            null,
            new[] { diagnostic },
            evidence);
    }
}

internal enum TiaOpennessV17EnvironmentProbeState
{
    Ready,
    Unavailable,
    Failed
}
