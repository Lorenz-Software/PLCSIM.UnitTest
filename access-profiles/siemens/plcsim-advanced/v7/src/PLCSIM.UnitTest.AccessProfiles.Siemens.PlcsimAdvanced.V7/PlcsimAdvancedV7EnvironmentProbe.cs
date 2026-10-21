using Microsoft.Win32;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7;

internal interface IPlcsimAdvancedV7EnvironmentProbe
{
    Task<PlcsimAdvancedV7EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

internal interface IPlcsimAdvancedV7EnvironmentEvidenceSource
{
    PlcsimAdvancedV7RegistryEvidence ReadRegistryEvidence();

    PlcsimAdvancedV7PathKind InspectPath(string path);

    PlcsimAdvancedV7FileVersionEvidence InspectFileVersion(string path);

    PlcsimAdvancedV7ExportEvidence InspectExports(string path);
}

internal sealed class PlcsimAdvancedV7EnvironmentProbe : IPlcsimAdvancedV7EnvironmentProbe
{
    internal const string RegistryPath = @"SOFTWARE\Wow6432Node\Siemens\Shared Tools\PLCSIMADV_SimRT\";
    internal const string RegistryValueName = "Path";
    internal const string ApiVersionSubdirectory = @"API\7.0";
    internal const string RuntimeApiDllName = "Siemens.Simatic.Simulation.Runtime.Api.x64.dll";
    internal const string InitializeEntryPoint = "RuntimeApiEntry_Initialize";
    internal const string DestroyInterfaceEntryPoint = "RuntimeApiEntry_DestroyInterface";
    internal const string ExpectedInterfaceVersion = "0x00070000";
    internal const string ExpectedProductVersion = "7.0.0.0";

    private readonly IPlcsimAdvancedV7EnvironmentEvidenceSource evidenceSource;

    public PlcsimAdvancedV7EnvironmentProbe()
        : this(new PlcsimAdvancedV7EnvironmentEvidenceSource())
    {
    }

    internal PlcsimAdvancedV7EnvironmentProbe(
        IPlcsimAdvancedV7EnvironmentEvidenceSource evidenceSource)
    {
        this.evidenceSource = evidenceSource ?? throw new ArgumentNullException(nameof(evidenceSource));
    }

    public Task<PlcsimAdvancedV7EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PlcsimAdvancedV7RegistryEvidence registry;
        try
        {
            registry = evidenceSource.ReadRegistryEvidence();
        }
        catch (Exception ex) when (IsEvidenceAccessFailure(ex))
        {
            return Task.FromResult(Failed(
                "PlcsimAdvanced.V7.RegistryReadFailed",
                "PLCSIM Advanced V7 Runtime API registry evidence could not be read.",
                "plcsim-advanced-v7-registry",
                "RegistryReadFailure",
                ex,
                Array.Empty<PlcsimEvidence>()));
        }

        if (!registry.HasRegistryKey || string.IsNullOrWhiteSpace(registry.InstallPath))
        {
            return Task.FromResult(PlcsimAdvancedV7EnvironmentProbeResult.Unavailable(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V7.RegistryEvidenceMissing",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V7 Runtime API registry evidence was not found.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v7-registry"),
                Array.Empty<PlcsimEvidence>()));
        }

        string apiDirectory;
        try
        {
            apiDirectory = Path.Combine(
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(registry.InstallPath)),
                ApiVersionSubdirectory);
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            return Task.FromResult(Failed(
                "PlcsimAdvanced.V7.InstallPathInvalid",
                "PLCSIM Advanced V7 Runtime API registry evidence contains an invalid installation path.",
                "plcsim-advanced-v7-registry-path",
                "InstallPathResolutionFailure",
                ex,
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v7-registry-path",
                        "RegistryPath",
                        registry.InstallPath)
                }));
        }

        var evidence = new List<PlcsimEvidence>
        {
            new(
                "plcsim-advanced-v7-registry-path",
                "RegistryPath",
                registry.InstallPath),
            new(
                "plcsim-advanced-v7-interface-version",
                "RuntimeApiInterfaceVersion",
                ExpectedInterfaceVersion),
            new(
                "plcsim-advanced-v7-api-directory",
                "RuntimeApiDirectory",
                apiDirectory)
        };

        var apiDirectoryInspection = InspectPath(
            apiDirectory,
            "PlcsimAdvanced.V7.ApiDirectoryInspectFailed",
            "PLCSIM Advanced V7 Runtime API directory could not be inspected.",
            "plcsim-advanced-v7-api-directory",
            evidence);
        if (apiDirectoryInspection.Failure is not null)
        {
            return Task.FromResult(apiDirectoryInspection.Failure);
        }

        if (apiDirectoryInspection.Kind == PlcsimAdvancedV7PathKind.Missing)
        {
            return Task.FromResult(PlcsimAdvancedV7EnvironmentProbeResult.Unavailable(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V7.ApiDirectoryMissing",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V7 Runtime API directory was not found.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v7-api-directory"),
                evidence));
        }

        if (apiDirectoryInspection.Kind != PlcsimAdvancedV7PathKind.Directory)
        {
            return Task.FromResult(PlcsimAdvancedV7EnvironmentProbeResult.Failed(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V7.ApiDirectoryInvalid",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V7 Runtime API directory evidence does not reference a directory.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v7-api-directory"),
                evidence));
        }

        var runtimeApiDllPath = Path.Combine(apiDirectory, RuntimeApiDllName);
        var dllInspection = InspectPath(
            runtimeApiDllPath,
            "PlcsimAdvanced.V7.RuntimeApiDllInspectFailed",
            "PLCSIM Advanced V7 Runtime API DLL path could not be inspected.",
            "plcsim-advanced-v7-runtime-api-dll",
            evidence);
        if (dllInspection.Failure is not null)
        {
            return Task.FromResult(dllInspection.Failure);
        }

        if (dllInspection.Kind == PlcsimAdvancedV7PathKind.Missing)
        {
            return Task.FromResult(PlcsimAdvancedV7EnvironmentProbeResult.Unavailable(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V7.RuntimeApiDllMissing",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V7 x64 Runtime API DLL was not found.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v7-runtime-api-dll"),
                evidence));
        }

        if (dllInspection.Kind != PlcsimAdvancedV7PathKind.File)
        {
            return Task.FromResult(PlcsimAdvancedV7EnvironmentProbeResult.Failed(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V7.RuntimeApiDllInvalid",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V7 Runtime API DLL evidence does not reference a file.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v7-runtime-api-dll"),
                evidence));
        }

        try
        {
            var version = evidenceSource.InspectFileVersion(runtimeApiDllPath);
            evidence.Add(new PlcsimEvidence(
                "plcsim-advanced-v7-runtime-api-dll",
                "RuntimeApiDll",
                $"{runtimeApiDllPath}; ProductVersion={version.ProductVersion}; FileVersion={version.FileVersion}"));

            if (!ProductVersionMatches(version.ProductVersion, ExpectedProductVersion))
            {
                return Task.FromResult(PlcsimAdvancedV7EnvironmentProbeResult.Unavailable(
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V7.ProductVersionMismatch",
                        PlcsimDiagnosticSeverity.Error,
                        $"PLCSIM Advanced V7 Runtime API product version {ExpectedProductVersion} was not found.",
                        "EnvironmentProbe",
                        "plcsim-advanced-v7-runtime-api-dll"),
                    evidence));
            }
        }
        catch (Exception ex) when (IsEvidenceAccessFailure(ex) || ex is Win32Exception)
        {
            return Task.FromResult(Failed(
                "PlcsimAdvanced.V7.RuntimeApiDllInspectFailed",
                "PLCSIM Advanced V7 Runtime API DLL metadata could not be inspected.",
                "plcsim-advanced-v7-runtime-api-dll",
                "RuntimeApiDllInspectionFailure",
                ex,
                evidence));
        }

        PlcsimAdvancedV7ExportEvidence exportInspection;
        try
        {
            exportInspection = evidenceSource.InspectExports(runtimeApiDllPath);
        }
        catch (Exception ex) when (IsEvidenceAccessFailure(ex)
            || ex is BadImageFormatException
            or DllNotFoundException
            or EntryPointNotFoundException)
        {
            return Task.FromResult(Failed(
                "PlcsimAdvanced.V7.RuntimeApiExportProbeFailed",
                "PLCSIM Advanced V7 Runtime API exports could not be inspected.",
                "plcsim-advanced-v7-runtime-api-exports",
                "RuntimeApiExportProbeFailure",
                ex,
                evidence));
        }

        if (!exportInspection.Succeeded)
        {
            return Task.FromResult(PlcsimAdvancedV7EnvironmentProbeResult.Failed(
                new PlcsimDiagnostic(
                    exportInspection.Code,
                    PlcsimDiagnosticSeverity.Error,
                    exportInspection.Message,
                    "EnvironmentProbe",
                    "plcsim-advanced-v7-runtime-api-exports"),
                evidence.Append(new PlcsimEvidence(
                    "plcsim-advanced-v7-runtime-api-exports",
                    "RuntimeApiExportProbe",
                    exportInspection.TechnicalDetail ?? exportInspection.Message)).ToArray()));
        }

        evidence.Add(new PlcsimEvidence(
            "plcsim-advanced-v7-runtime-api-exports",
            "RuntimeApiExports",
            $"{InitializeEntryPoint}; {DestroyInterfaceEntryPoint}"));
        return Task.FromResult(PlcsimAdvancedV7EnvironmentProbeResult.Ready(evidence));
    }

    private PathInspectionResult InspectPath(
        string path,
        string failureCode,
        string failureMessage,
        string evidenceId,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        try
        {
            return new PathInspectionResult(evidenceSource.InspectPath(path), null);
        }
        catch (Exception ex) when (IsEvidenceAccessFailure(ex))
        {
            return new PathInspectionResult(
                PlcsimAdvancedV7PathKind.Missing,
                Failed(
                    failureCode,
                    failureMessage,
                    evidenceId,
                    "PathInspectionFailure",
                    ex,
                    evidence));
        }
    }

    private static PlcsimAdvancedV7EnvironmentProbeResult Failed(
        string code,
        string message,
        string evidenceId,
        string evidenceKind,
        Exception exception,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        return PlcsimAdvancedV7EnvironmentProbeResult.Failed(
            new PlcsimDiagnostic(
                code,
                PlcsimDiagnosticSeverity.Error,
                message,
                "EnvironmentProbe",
                evidenceId),
            evidence.Append(new PlcsimEvidence(
                evidenceId,
                evidenceKind,
                exception.Message)).ToArray());
    }

    private static bool IsEvidenceAccessFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException;
    }

    private static bool ProductVersionMatches(string? actual, string expected)
    {
        return Version.TryParse(actual, out var actualVersion)
            && Version.TryParse(expected, out var expectedVersion)
            && actualVersion == expectedVersion;
    }

    private sealed record PathInspectionResult(
        PlcsimAdvancedV7PathKind Kind,
        PlcsimAdvancedV7EnvironmentProbeResult? Failure);
}

internal sealed class PlcsimAdvancedV7EnvironmentEvidenceSource
    : IPlcsimAdvancedV7EnvironmentEvidenceSource
{
    public PlcsimAdvancedV7RegistryEvidence ReadRegistryEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new PlcsimAdvancedV7RegistryEvidence(false, null);
        }

        using var key = Registry.LocalMachine.OpenSubKey(PlcsimAdvancedV7EnvironmentProbe.RegistryPath);
        var installPath = key?.GetValue(PlcsimAdvancedV7EnvironmentProbe.RegistryValueName) as string;
        return new PlcsimAdvancedV7RegistryEvidence(
            key is not null,
            string.IsNullOrWhiteSpace(installPath) ? null : installPath);
    }

    public PlcsimAdvancedV7PathKind InspectPath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory)
                ? PlcsimAdvancedV7PathKind.Directory
                : PlcsimAdvancedV7PathKind.File;
        }
        catch (FileNotFoundException)
        {
            return PlcsimAdvancedV7PathKind.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return PlcsimAdvancedV7PathKind.Missing;
        }
    }

    public PlcsimAdvancedV7FileVersionEvidence InspectFileVersion(string path)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        return new PlcsimAdvancedV7FileVersionEvidence(
            version.ProductVersion,
            version.FileVersion);
    }

    public PlcsimAdvancedV7ExportEvidence InspectExports(string path)
    {
        if (!NativeLibrary.TryLoad(path, out var handle))
        {
            return PlcsimAdvancedV7ExportEvidence.Failed(
                "PlcsimAdvanced.V7.RuntimeApiDllLoadFailed",
                "PLCSIM Advanced V7 Runtime API DLL could not be loaded for export inspection.",
                path);
        }

        try
        {
            if (!NativeLibrary.TryGetExport(
                handle,
                PlcsimAdvancedV7EnvironmentProbe.InitializeEntryPoint,
                out _))
            {
                return PlcsimAdvancedV7ExportEvidence.Failed(
                    "PlcsimAdvanced.V7.RuntimeApiInitializeEntryMissing",
                    "PLCSIM Advanced V7 Runtime API initialize entry point was not found.",
                    PlcsimAdvancedV7EnvironmentProbe.InitializeEntryPoint);
            }

            if (!NativeLibrary.TryGetExport(
                handle,
                PlcsimAdvancedV7EnvironmentProbe.DestroyInterfaceEntryPoint,
                out _))
            {
                return PlcsimAdvancedV7ExportEvidence.Failed(
                    "PlcsimAdvanced.V7.RuntimeApiDestroyEntryMissing",
                    "PLCSIM Advanced V7 Runtime API destroy-interface entry point was not found.",
                    PlcsimAdvancedV7EnvironmentProbe.DestroyInterfaceEntryPoint);
            }

            return PlcsimAdvancedV7ExportEvidence.SucceededResult;
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }
}

internal sealed record PlcsimAdvancedV7RegistryEvidence(
    bool HasRegistryKey,
    string? InstallPath);

internal sealed record PlcsimAdvancedV7FileVersionEvidence(
    string? ProductVersion,
    string? FileVersion);

internal sealed record PlcsimAdvancedV7ExportEvidence(
    bool Succeeded,
    string Code,
    string Message,
    string? TechnicalDetail)
{
    public static PlcsimAdvancedV7ExportEvidence SucceededResult { get; } = new(
        true,
        string.Empty,
        string.Empty,
        null);

    public static PlcsimAdvancedV7ExportEvidence Failed(
        string code,
        string message,
        string? technicalDetail)
    {
        return new PlcsimAdvancedV7ExportEvidence(false, code, message, technicalDetail);
    }
}

internal enum PlcsimAdvancedV7PathKind
{
    Missing,
    Directory,
    File
}

internal sealed record PlcsimAdvancedV7EnvironmentProbeResult(
    PlcsimAdvancedV7EnvironmentProbeState State,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool IsReady => State == PlcsimAdvancedV7EnvironmentProbeState.Ready;

    public static PlcsimAdvancedV7EnvironmentProbeResult Ready(IReadOnlyList<PlcsimEvidence> evidence)
    {
        return new PlcsimAdvancedV7EnvironmentProbeResult(
            PlcsimAdvancedV7EnvironmentProbeState.Ready,
            Array.Empty<PlcsimDiagnostic>(),
            evidence);
    }

    public static PlcsimAdvancedV7EnvironmentProbeResult Unavailable(
        PlcsimDiagnostic diagnostic,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        return new PlcsimAdvancedV7EnvironmentProbeResult(
            PlcsimAdvancedV7EnvironmentProbeState.Unavailable,
            new[] { diagnostic },
            evidence);
    }

    public static PlcsimAdvancedV7EnvironmentProbeResult Failed(
        PlcsimDiagnostic diagnostic,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        return new PlcsimAdvancedV7EnvironmentProbeResult(
            PlcsimAdvancedV7EnvironmentProbeState.Failed,
            new[] { diagnostic },
            evidence);
    }
}

internal enum PlcsimAdvancedV7EnvironmentProbeState
{
    Ready,
    Unavailable,
    Failed
}
