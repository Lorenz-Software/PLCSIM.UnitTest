using Microsoft.Win32;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using System.Reflection;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21;

internal interface ITiaOpennessV21EnvironmentProbe
{
    Task<TiaOpennessV21EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

internal interface ITiaOpennessV21InstallationEvidenceSource
{
    TiaOpennessV21InstallationEvidence? Capture();
}

internal sealed class TiaOpennessV21EnvironmentProbe : ITiaOpennessV21EnvironmentProbe
{
    private const string ExpectedAssemblyVersion = "21.0.0.0";
    private const string BaseAssemblyName = "Siemens.Engineering.Base";
    private const string Step7AssemblyName = "Siemens.Engineering.Step7";

    private readonly ITiaOpennessV21InstallationEvidenceSource installationEvidenceSource;
    private readonly Func<string> workerPathResolver;

    public TiaOpennessV21EnvironmentProbe()
        : this(
            new TiaOpennessV21RegistryEvidenceSource(),
            TiaOpennessV21WorkerProcessHost.ResolveDeployedWorkerPath)
    {
    }

    internal TiaOpennessV21EnvironmentProbe(
        ITiaOpennessV21InstallationEvidenceSource installationEvidenceSource,
        Func<string>? workerPathResolver = null)
    {
        this.installationEvidenceSource = installationEvidenceSource
            ?? throw new ArgumentNullException(nameof(installationEvidenceSource));
        this.workerPathResolver = workerPathResolver
            ?? TiaOpennessV21WorkerProcessHost.ResolveDeployedWorkerPath;
    }

    public Task<TiaOpennessV21EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TiaOpennessV21InstallationEvidence? installation;
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
            return Task.FromResult(TiaOpennessV21EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V21.RegistryReadFailed",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V21 registry evidence could not be read.",
                    "EnvironmentProbe",
                    "tia-openness-v21-registry"),
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v21-registry",
                        "RegistryReadFailure",
                        ex.Message)
                }));
        }

        if (installation is null)
        {
            return Task.FromResult(TiaOpennessV21EnvironmentProbeResult.Unavailable(
                new TiaDiagnostic(
                    "TiaOpenness.V21.RegistryEvidenceMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V21 registry evidence was not found at the documented net48 PublicAPI key.",
                    "EnvironmentProbe",
                    "tia-openness-v21-registry"),
                Array.Empty<TiaEvidence>()));
        }

        var evidence = new List<TiaEvidence>
        {
            new(
                "tia-openness-v21-registry",
                "RegistryKey",
                TiaOpennessV21RegistryEvidenceSource.RegistryPath)
        };

        if (string.IsNullOrWhiteSpace(installation.AssemblyVersion)
            || string.IsNullOrWhiteSpace(installation.PublicKeyToken)
            || string.IsNullOrWhiteSpace(installation.BaseAssemblyPath))
        {
            return Task.FromResult(TiaOpennessV21EnvironmentProbeResult.Unavailable(
                new TiaDiagnostic(
                    "TiaOpenness.V21.RegistryEvidenceIncomplete",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V21 registry evidence does not contain the required net48 PublicAPI values.",
                    "EnvironmentProbe",
                    "tia-openness-v21-registry"),
                evidence));
        }

        if (!string.Equals(
                NormalizeVersion(installation.AssemblyVersion),
                ExpectedAssemblyVersion,
                StringComparison.Ordinal))
        {
            return Task.FromResult(TiaOpennessV21EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V21.RegistryVersionMismatch",
                    TiaDiagnosticSeverity.Error,
                    $"TIA Openness V21 registry evidence reports unsupported assembly version '{installation.AssemblyVersion}'.",
                    "EnvironmentProbe",
                    "tia-openness-v21-registry"),
                evidence));
        }

        string baseAssemblyPath;
        try
        {
            baseAssemblyPath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(installation.BaseAssemblyPath));
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            return Task.FromResult(TiaOpennessV21EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V21.PublicApiPathInvalid",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V21 registry evidence contains an invalid PublicAPI assembly path.",
                    "EnvironmentProbe",
                    "tia-openness-v21-registry"),
                evidence.Concat(new[]
                {
                    new TiaEvidence(
                        "tia-openness-v21-public-api-path-error",
                        "PublicApiPathError",
                        ex.Message)
                }).ToArray()));
        }

        var publicApiDirectory = Path.GetDirectoryName(baseAssemblyPath);
        if (string.IsNullOrWhiteSpace(publicApiDirectory))
        {
            return Task.FromResult(TiaOpennessV21EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V21.PublicApiPathInvalid",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V21 registry evidence does not resolve to a PublicAPI directory.",
                    "EnvironmentProbe",
                    "tia-openness-v21-registry"),
                evidence));
        }

        evidence.Add(new TiaEvidence(
            "tia-openness-v21-public-api",
            "PublicApiDirectory",
            publicApiDirectory));

        var expectedPublicKeyToken = NormalizePublicKeyToken(installation.PublicKeyToken);
        var requiredAssemblies = new[]
        {
            new RequiredAssembly(BaseAssemblyName, baseAssemblyPath),
            new RequiredAssembly(Step7AssemblyName, Path.Combine(publicApiDirectory, Step7AssemblyName + ".dll"))
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
            return Task.FromResult(TiaOpennessV21EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V21.WorkerPathInvalid",
                    TiaDiagnosticSeverity.Error,
                    "The deployed TIA Openness V21 worker path could not be resolved.",
                    "EnvironmentProbe",
                    "tia-openness-v21-worker"),
                evidence.Concat(new[]
                {
                    new TiaEvidence(
                        "tia-openness-v21-worker-path-error",
                        "WorkerPathError",
                        ex.Message)
                }).ToArray()));
        }

        var workerValidation = ValidateAssembly(
            new RequiredAssembly(
                "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21.Worker",
                workerPath),
            expectedPublicKeyToken: null,
            evidence,
            missingCode: "TiaOpenness.V21.WorkerMissing",
            invalidCode: "TiaOpenness.V21.WorkerInvalid",
            evidenceId: "tia-openness-v21-worker",
            expectedAssemblyVersion: null,
            missingIsFailure: true);
        if (workerValidation is not null)
        {
            return Task.FromResult(workerValidation);
        }

        return Task.FromResult(TiaOpennessV21EnvironmentProbeResult.Ready(
            publicApiDirectory,
            evidence));
    }

    private static TiaOpennessV21EnvironmentProbeResult? ValidateAssembly(
        RequiredAssembly requiredAssembly,
        string? expectedPublicKeyToken,
        List<TiaEvidence> evidence,
        string missingCode = "TiaOpenness.V21.PublicApiAssemblyMissing",
        string invalidCode = "TiaOpenness.V21.PublicApiAssemblyInvalid",
        string evidenceId = "tia-openness-v21-public-api",
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
                $"Required TIA Openness V21 component '{Path.GetFileName(requiredAssembly.Path)}' was not found.",
                "EnvironmentProbe",
                evidenceId);
            return missingIsFailure
                ? TiaOpennessV21EnvironmentProbeResult.Failed(diagnostic, evidence)
                : TiaOpennessV21EnvironmentProbeResult.Unavailable(diagnostic, evidence);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException)
        {
            return TiaOpennessV21EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    invalidCode,
                    TiaDiagnosticSeverity.Error,
                    $"Required TIA Openness V21 component '{Path.GetFileName(requiredAssembly.Path)}' could not be accessed.",
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
                return TiaOpennessV21EnvironmentProbeResult.Failed(
                    new TiaDiagnostic(
                        invalidCode,
                        TiaDiagnosticSeverity.Error,
                        $"Required TIA Openness V21 component '{Path.GetFileName(requiredAssembly.Path)}' has incompatible assembly identity.",
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
            return TiaOpennessV21EnvironmentProbeResult.Failed(
                new TiaDiagnostic(
                    invalidCode,
                    TiaDiagnosticSeverity.Error,
                    $"Required TIA Openness V21 component '{Path.GetFileName(requiredAssembly.Path)}' could not be inspected.",
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

internal sealed class TiaOpennessV21RegistryEvidenceSource : ITiaOpennessV21InstallationEvidenceSource
{
    internal const string RegistryPath =
        @"SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48";

    public TiaOpennessV21InstallationEvidence? Capture()
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

        return new TiaOpennessV21InstallationEvidence(
            key.GetValue("AssemblyVersion") as string,
            key.GetValue("PublicKeyToken") as string,
            key.GetValue("Siemens.Engineering.Base") as string);
    }
}

internal sealed record TiaOpennessV21InstallationEvidence(
    string? AssemblyVersion,
    string? PublicKeyToken,
    string? BaseAssemblyPath);

internal sealed record TiaOpennessV21EnvironmentProbeResult(
    TiaOpennessV21EnvironmentProbeState State,
    string? PublicApiDirectory,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool IsReady => State == TiaOpennessV21EnvironmentProbeState.Ready;

    public static TiaOpennessV21EnvironmentProbeResult Ready(
        string publicApiDirectory,
        IReadOnlyList<TiaEvidence> evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicApiDirectory);

        return new TiaOpennessV21EnvironmentProbeResult(
            TiaOpennessV21EnvironmentProbeState.Ready,
            publicApiDirectory,
            Array.Empty<TiaDiagnostic>(),
            evidence);
    }

    public static TiaOpennessV21EnvironmentProbeResult Unavailable(
        TiaDiagnostic diagnostic,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV21EnvironmentProbeResult(
            TiaOpennessV21EnvironmentProbeState.Unavailable,
            null,
            new[] { diagnostic },
            evidence);
    }

    public static TiaOpennessV21EnvironmentProbeResult Failed(
        TiaDiagnostic diagnostic,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV21EnvironmentProbeResult(
            TiaOpennessV21EnvironmentProbeState.Failed,
            null,
            new[] { diagnostic },
            evidence);
    }
}

internal enum TiaOpennessV21EnvironmentProbeState
{
    Ready,
    Unavailable,
    Failed
}
