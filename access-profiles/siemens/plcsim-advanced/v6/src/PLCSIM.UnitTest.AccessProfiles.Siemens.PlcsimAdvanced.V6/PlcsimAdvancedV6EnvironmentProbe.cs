using Microsoft.Win32;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6;

internal interface IPlcsimAdvancedV6EnvironmentProbe
{
    Task<PlcsimAdvancedV6EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

internal interface IPlcsimAdvancedV6EnvironmentEvidenceSource
{
    PlcsimAdvancedV6RegistryEvidence ReadRegistryEvidence();

    PlcsimAdvancedV6PathKind InspectPath(string path);

    PlcsimAdvancedV6FileVersionEvidence InspectFileVersion(string path);

    PlcsimAdvancedV6ExportEvidence InspectExports(string path);
}

internal sealed class PlcsimAdvancedV6EnvironmentProbe : IPlcsimAdvancedV6EnvironmentProbe
{
    internal const string RegistryPath = @"SOFTWARE\Wow6432Node\Siemens\Shared Tools\PLCSIMADV_SimRT\";
    internal const string RegistryValueName = "Path";
    internal const string ApiVersionSubdirectory = @"API\6.0";
    internal const string RuntimeApiDllName = "Siemens.Simatic.Simulation.Runtime.Api.x64.dll";
    internal const string InitializeEntryPoint = "RuntimeApiEntry_Initialize";
    internal const string DestroyInterfaceEntryPoint = "RuntimeApiEntry_DestroyInterface";
    internal const string ExpectedInterfaceVersion = "0x00060000";
    internal const string ExpectedProductVersion = "6.0.0.0";

    private readonly IPlcsimAdvancedV6EnvironmentEvidenceSource evidenceSource;

    public PlcsimAdvancedV6EnvironmentProbe()
        : this(new PlcsimAdvancedV6EnvironmentEvidenceSource())
    {
    }

    internal PlcsimAdvancedV6EnvironmentProbe(
        IPlcsimAdvancedV6EnvironmentEvidenceSource evidenceSource)
    {
        this.evidenceSource = evidenceSource ?? throw new ArgumentNullException(nameof(evidenceSource));
    }

    public Task<PlcsimAdvancedV6EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PlcsimAdvancedV6RegistryEvidence registry;
        try
        {
            registry = evidenceSource.ReadRegistryEvidence();
        }
        catch (Exception ex) when (IsEvidenceAccessFailure(ex))
        {
            return Task.FromResult(Failed(
                "PlcsimAdvanced.V6.RegistryReadFailed",
                "PLCSIM Advanced V6 Runtime API registry evidence could not be read.",
                "plcsim-advanced-v6-registry",
                "RegistryReadFailure",
                ex,
                Array.Empty<PlcsimEvidence>()));
        }

        if (!registry.HasRegistryKey || string.IsNullOrWhiteSpace(registry.InstallPath))
        {
            return Task.FromResult(PlcsimAdvancedV6EnvironmentProbeResult.Unavailable(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V6.RegistryEvidenceMissing",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V6 Runtime API registry evidence was not found.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v6-registry"),
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
                "PlcsimAdvanced.V6.InstallPathInvalid",
                "PLCSIM Advanced V6 Runtime API registry evidence contains an invalid installation path.",
                "plcsim-advanced-v6-registry-path",
                "InstallPathResolutionFailure",
                ex,
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-registry-path",
                        "RegistryPath",
                        registry.InstallPath)
                }));
        }

        var evidence = new List<PlcsimEvidence>
        {
            new(
                "plcsim-advanced-v6-registry-path",
                "RegistryPath",
                registry.InstallPath),
            new(
                "plcsim-advanced-v6-interface-version",
                "RuntimeApiInterfaceVersion",
                ExpectedInterfaceVersion),
            new(
                "plcsim-advanced-v6-api-directory",
                "RuntimeApiDirectory",
                apiDirectory)
        };

        var apiDirectoryInspection = InspectPath(
            apiDirectory,
            "PlcsimAdvanced.V6.ApiDirectoryInspectFailed",
            "PLCSIM Advanced V6 Runtime API directory could not be inspected.",
            "plcsim-advanced-v6-api-directory",
            evidence);
        if (apiDirectoryInspection.Failure is not null)
        {
            return Task.FromResult(apiDirectoryInspection.Failure);
        }

        if (apiDirectoryInspection.Kind == PlcsimAdvancedV6PathKind.Missing)
        {
            return Task.FromResult(PlcsimAdvancedV6EnvironmentProbeResult.Unavailable(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V6.ApiDirectoryMissing",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V6 Runtime API directory was not found.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v6-api-directory"),
                evidence));
        }

        if (apiDirectoryInspection.Kind != PlcsimAdvancedV6PathKind.Directory)
        {
            return Task.FromResult(PlcsimAdvancedV6EnvironmentProbeResult.Failed(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V6.ApiDirectoryInvalid",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V6 Runtime API directory evidence does not reference a directory.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v6-api-directory"),
                evidence));
        }

        var runtimeApiDllPath = Path.Combine(apiDirectory, RuntimeApiDllName);
        var dllInspection = InspectPath(
            runtimeApiDllPath,
            "PlcsimAdvanced.V6.RuntimeApiDllInspectFailed",
            "PLCSIM Advanced V6 Runtime API DLL path could not be inspected.",
            "plcsim-advanced-v6-runtime-api-dll",
            evidence);
        if (dllInspection.Failure is not null)
        {
            return Task.FromResult(dllInspection.Failure);
        }

        if (dllInspection.Kind == PlcsimAdvancedV6PathKind.Missing)
        {
            return Task.FromResult(PlcsimAdvancedV6EnvironmentProbeResult.Unavailable(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V6.RuntimeApiDllMissing",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V6 x64 Runtime API DLL was not found.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v6-runtime-api-dll"),
                evidence));
        }

        if (dllInspection.Kind != PlcsimAdvancedV6PathKind.File)
        {
            return Task.FromResult(PlcsimAdvancedV6EnvironmentProbeResult.Failed(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V6.RuntimeApiDllInvalid",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V6 Runtime API DLL evidence does not reference a file.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v6-runtime-api-dll"),
                evidence));
        }

        try
        {
            var version = evidenceSource.InspectFileVersion(runtimeApiDllPath);
            evidence.Add(new PlcsimEvidence(
                "plcsim-advanced-v6-runtime-api-dll",
                "RuntimeApiDll",
                $"{runtimeApiDllPath}; ProductVersion={version.ProductVersion}; FileVersion={version.FileVersion}"));

            if (!ProductVersionMatches(version.ProductVersion, ExpectedProductVersion))
            {
                return Task.FromResult(PlcsimAdvancedV6EnvironmentProbeResult.Unavailable(
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.ProductVersionMismatch",
                        PlcsimDiagnosticSeverity.Error,
                        $"PLCSIM Advanced V6 Runtime API product version {ExpectedProductVersion} was not found.",
                        "EnvironmentProbe",
                        "plcsim-advanced-v6-runtime-api-dll"),
                    evidence));
            }
        }
        catch (Exception ex) when (IsEvidenceAccessFailure(ex) || ex is Win32Exception)
        {
            return Task.FromResult(Failed(
                "PlcsimAdvanced.V6.RuntimeApiDllInspectFailed",
                "PLCSIM Advanced V6 Runtime API DLL metadata could not be inspected.",
                "plcsim-advanced-v6-runtime-api-dll",
                "RuntimeApiDllInspectionFailure",
                ex,
                evidence));
        }

        PlcsimAdvancedV6ExportEvidence exportInspection;
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
                "PlcsimAdvanced.V6.RuntimeApiExportProbeFailed",
                "PLCSIM Advanced V6 Runtime API exports could not be inspected.",
                "plcsim-advanced-v6-runtime-api-exports",
                "RuntimeApiExportProbeFailure",
                ex,
                evidence));
        }

        if (!exportInspection.Succeeded)
        {
            return Task.FromResult(PlcsimAdvancedV6EnvironmentProbeResult.Failed(
                new PlcsimDiagnostic(
                    exportInspection.Code,
                    PlcsimDiagnosticSeverity.Error,
                    exportInspection.Message,
                    "EnvironmentProbe",
                    "plcsim-advanced-v6-runtime-api-exports"),
                evidence.Append(new PlcsimEvidence(
                    "plcsim-advanced-v6-runtime-api-exports",
                    "RuntimeApiExportProbe",
                    exportInspection.TechnicalDetail ?? exportInspection.Message)).ToArray()));
        }

        evidence.Add(new PlcsimEvidence(
            "plcsim-advanced-v6-runtime-api-exports",
            "RuntimeApiExports",
            $"{InitializeEntryPoint}; {DestroyInterfaceEntryPoint}"));
        return Task.FromResult(PlcsimAdvancedV6EnvironmentProbeResult.Ready(evidence));
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
                PlcsimAdvancedV6PathKind.Missing,
                Failed(
                    failureCode,
                    failureMessage,
                    evidenceId,
                    "PathInspectionFailure",
                    ex,
                    evidence));
        }
    }

    private static PlcsimAdvancedV6EnvironmentProbeResult Failed(
        string code,
        string message,
        string evidenceId,
        string evidenceKind,
        Exception exception,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        return PlcsimAdvancedV6EnvironmentProbeResult.Failed(
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
        PlcsimAdvancedV6PathKind Kind,
        PlcsimAdvancedV6EnvironmentProbeResult? Failure);
}

internal sealed class PlcsimAdvancedV6EnvironmentEvidenceSource
    : IPlcsimAdvancedV6EnvironmentEvidenceSource
{
    public PlcsimAdvancedV6RegistryEvidence ReadRegistryEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new PlcsimAdvancedV6RegistryEvidence(false, null);
        }

        using var key = Registry.LocalMachine.OpenSubKey(PlcsimAdvancedV6EnvironmentProbe.RegistryPath);
        var installPath = key?.GetValue(PlcsimAdvancedV6EnvironmentProbe.RegistryValueName) as string;
        return new PlcsimAdvancedV6RegistryEvidence(
            key is not null,
            string.IsNullOrWhiteSpace(installPath) ? null : installPath);
    }

    public PlcsimAdvancedV6PathKind InspectPath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory)
                ? PlcsimAdvancedV6PathKind.Directory
                : PlcsimAdvancedV6PathKind.File;
        }
        catch (FileNotFoundException)
        {
            return PlcsimAdvancedV6PathKind.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return PlcsimAdvancedV6PathKind.Missing;
        }
    }

    public PlcsimAdvancedV6FileVersionEvidence InspectFileVersion(string path)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        return new PlcsimAdvancedV6FileVersionEvidence(
            version.ProductVersion,
            version.FileVersion);
    }

    public PlcsimAdvancedV6ExportEvidence InspectExports(string path)
    {
        if (!NativeLibrary.TryLoad(path, out var handle))
        {
            return PlcsimAdvancedV6ExportEvidence.Failed(
                "PlcsimAdvanced.V6.RuntimeApiDllLoadFailed",
                "PLCSIM Advanced V6 Runtime API DLL could not be loaded for export inspection.",
                path);
        }

        try
        {
            if (!NativeLibrary.TryGetExport(
                handle,
                PlcsimAdvancedV6EnvironmentProbe.InitializeEntryPoint,
                out _))
            {
                return PlcsimAdvancedV6ExportEvidence.Failed(
                    "PlcsimAdvanced.V6.RuntimeApiInitializeEntryMissing",
                    "PLCSIM Advanced V6 Runtime API initialize entry point was not found.",
                    PlcsimAdvancedV6EnvironmentProbe.InitializeEntryPoint);
            }

            if (!NativeLibrary.TryGetExport(
                handle,
                PlcsimAdvancedV6EnvironmentProbe.DestroyInterfaceEntryPoint,
                out _))
            {
                return PlcsimAdvancedV6ExportEvidence.Failed(
                    "PlcsimAdvanced.V6.RuntimeApiDestroyEntryMissing",
                    "PLCSIM Advanced V6 Runtime API destroy-interface entry point was not found.",
                    PlcsimAdvancedV6EnvironmentProbe.DestroyInterfaceEntryPoint);
            }

            return PlcsimAdvancedV6ExportEvidence.SucceededResult;
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }
}

internal sealed record PlcsimAdvancedV6RegistryEvidence(
    bool HasRegistryKey,
    string? InstallPath);

internal sealed record PlcsimAdvancedV6FileVersionEvidence(
    string? ProductVersion,
    string? FileVersion);

internal sealed record PlcsimAdvancedV6ExportEvidence(
    bool Succeeded,
    string Code,
    string Message,
    string? TechnicalDetail)
{
    public static PlcsimAdvancedV6ExportEvidence SucceededResult { get; } = new(
        true,
        string.Empty,
        string.Empty,
        null);

    public static PlcsimAdvancedV6ExportEvidence Failed(
        string code,
        string message,
        string? technicalDetail)
    {
        return new PlcsimAdvancedV6ExportEvidence(false, code, message, technicalDetail);
    }
}

internal enum PlcsimAdvancedV6PathKind
{
    Missing,
    Directory,
    File
}

internal sealed record PlcsimAdvancedV6EnvironmentProbeResult(
    PlcsimAdvancedV6EnvironmentProbeState State,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool IsReady => State == PlcsimAdvancedV6EnvironmentProbeState.Ready;

    public static PlcsimAdvancedV6EnvironmentProbeResult Ready(IReadOnlyList<PlcsimEvidence> evidence)
    {
        return new PlcsimAdvancedV6EnvironmentProbeResult(
            PlcsimAdvancedV6EnvironmentProbeState.Ready,
            Array.Empty<PlcsimDiagnostic>(),
            evidence);
    }

    public static PlcsimAdvancedV6EnvironmentProbeResult Unavailable(
        PlcsimDiagnostic diagnostic,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        return new PlcsimAdvancedV6EnvironmentProbeResult(
            PlcsimAdvancedV6EnvironmentProbeState.Unavailable,
            new[] { diagnostic },
            evidence);
    }

    public static PlcsimAdvancedV6EnvironmentProbeResult Failed(
        PlcsimDiagnostic diagnostic,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        return new PlcsimAdvancedV6EnvironmentProbeResult(
            PlcsimAdvancedV6EnvironmentProbeState.Failed,
            new[] { diagnostic },
            evidence);
    }
}

internal enum PlcsimAdvancedV6EnvironmentProbeState
{
    Ready,
    Unavailable,
    Failed
}
