using Microsoft.Win32;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8;

internal interface IPlcsimAdvancedV8EnvironmentProbe
{
    Task<PlcsimAdvancedV8EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

internal interface IPlcsimAdvancedV8EnvironmentEvidenceSource
{
    PlcsimAdvancedV8RegistryEvidence ReadRegistryEvidence();

    PlcsimAdvancedV8PathKind InspectPath(string path);

    PlcsimAdvancedV8FileVersionEvidence InspectFileVersion(string path);

    PlcsimAdvancedV8ExportEvidence InspectExports(string path);
}

internal sealed class PlcsimAdvancedV8EnvironmentProbe : IPlcsimAdvancedV8EnvironmentProbe
{
    internal const string RegistryPath = @"SOFTWARE\Wow6432Node\Siemens\Shared Tools\PLCSIMADV_SimRT\";
    internal const string RegistryValueName = "Path";
    internal const string ApiVersionSubdirectory = @"API\8.0";
    internal const string RuntimeApiDllName = "Siemens.Simatic.Simulation.Runtime.Api.x64.dll";
    internal const string InitializeEntryPoint = "RuntimeApiEntry_Initialize";
    internal const string DestroyInterfaceEntryPoint = "RuntimeApiEntry_DestroyInterface";
    internal const string ExpectedInterfaceVersion = "0x00080000";
    internal const string ExpectedProductVersion = "8.0.0.0";

    private readonly IPlcsimAdvancedV8EnvironmentEvidenceSource evidenceSource;

    public PlcsimAdvancedV8EnvironmentProbe()
        : this(new PlcsimAdvancedV8EnvironmentEvidenceSource())
    {
    }

    internal PlcsimAdvancedV8EnvironmentProbe(
        IPlcsimAdvancedV8EnvironmentEvidenceSource evidenceSource)
    {
        this.evidenceSource = evidenceSource ?? throw new ArgumentNullException(nameof(evidenceSource));
    }

    public Task<PlcsimAdvancedV8EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PlcsimAdvancedV8RegistryEvidence registry;
        try
        {
            registry = evidenceSource.ReadRegistryEvidence();
        }
        catch (Exception ex) when (IsEvidenceAccessFailure(ex))
        {
            return Task.FromResult(Failed(
                "PlcsimAdvanced.V8.RegistryReadFailed",
                "PLCSIM Advanced V8 Runtime API registry evidence could not be read.",
                "plcsim-advanced-v8-registry",
                "RegistryReadFailure",
                ex,
                Array.Empty<PlcsimEvidence>()));
        }

        if (!registry.HasRegistryKey || string.IsNullOrWhiteSpace(registry.InstallPath))
        {
            return Task.FromResult(PlcsimAdvancedV8EnvironmentProbeResult.Unavailable(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V8.RegistryEvidenceMissing",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V8 Runtime API registry evidence was not found.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v8-registry"),
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
                "PlcsimAdvanced.V8.InstallPathInvalid",
                "PLCSIM Advanced V8 Runtime API registry evidence contains an invalid installation path.",
                "plcsim-advanced-v8-registry-path",
                "InstallPathResolutionFailure",
                ex,
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v8-registry-path",
                        "RegistryPath",
                        registry.InstallPath)
                }));
        }

        var evidence = new List<PlcsimEvidence>
        {
            new(
                "plcsim-advanced-v8-registry-path",
                "RegistryPath",
                registry.InstallPath),
            new(
                "plcsim-advanced-v8-interface-version",
                "RuntimeApiInterfaceVersion",
                ExpectedInterfaceVersion),
            new(
                "plcsim-advanced-v8-api-directory",
                "RuntimeApiDirectory",
                apiDirectory)
        };

        var apiDirectoryInspection = InspectPath(
            apiDirectory,
            "PlcsimAdvanced.V8.ApiDirectoryInspectFailed",
            "PLCSIM Advanced V8 Runtime API directory could not be inspected.",
            "plcsim-advanced-v8-api-directory",
            evidence);
        if (apiDirectoryInspection.Failure is not null)
        {
            return Task.FromResult(apiDirectoryInspection.Failure);
        }

        if (apiDirectoryInspection.Kind == PlcsimAdvancedV8PathKind.Missing)
        {
            return Task.FromResult(PlcsimAdvancedV8EnvironmentProbeResult.Unavailable(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V8.ApiDirectoryMissing",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V8 Runtime API directory was not found.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v8-api-directory"),
                evidence));
        }

        if (apiDirectoryInspection.Kind != PlcsimAdvancedV8PathKind.Directory)
        {
            return Task.FromResult(PlcsimAdvancedV8EnvironmentProbeResult.Failed(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V8.ApiDirectoryInvalid",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V8 Runtime API directory evidence does not reference a directory.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v8-api-directory"),
                evidence));
        }

        var runtimeApiDllPath = Path.Combine(apiDirectory, RuntimeApiDllName);
        var dllInspection = InspectPath(
            runtimeApiDllPath,
            "PlcsimAdvanced.V8.RuntimeApiDllInspectFailed",
            "PLCSIM Advanced V8 Runtime API DLL path could not be inspected.",
            "plcsim-advanced-v8-runtime-api-dll",
            evidence);
        if (dllInspection.Failure is not null)
        {
            return Task.FromResult(dllInspection.Failure);
        }

        if (dllInspection.Kind == PlcsimAdvancedV8PathKind.Missing)
        {
            return Task.FromResult(PlcsimAdvancedV8EnvironmentProbeResult.Unavailable(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V8.RuntimeApiDllMissing",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V8 x64 Runtime API DLL was not found.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v8-runtime-api-dll"),
                evidence));
        }

        if (dllInspection.Kind != PlcsimAdvancedV8PathKind.File)
        {
            return Task.FromResult(PlcsimAdvancedV8EnvironmentProbeResult.Failed(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V8.RuntimeApiDllInvalid",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V8 Runtime API DLL evidence does not reference a file.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v8-runtime-api-dll"),
                evidence));
        }

        try
        {
            var version = evidenceSource.InspectFileVersion(runtimeApiDllPath);
            evidence.Add(new PlcsimEvidence(
                "plcsim-advanced-v8-runtime-api-dll",
                "RuntimeApiDll",
                $"{runtimeApiDllPath}; ProductVersion={version.ProductVersion}; FileVersion={version.FileVersion}"));

            if (!ProductVersionMatches(version.ProductVersion, ExpectedProductVersion))
            {
                return Task.FromResult(PlcsimAdvancedV8EnvironmentProbeResult.Unavailable(
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V8.ProductVersionMismatch",
                        PlcsimDiagnosticSeverity.Error,
                        $"PLCSIM Advanced V8 Runtime API product version {ExpectedProductVersion} was not found.",
                        "EnvironmentProbe",
                        "plcsim-advanced-v8-runtime-api-dll"),
                    evidence));
            }
        }
        catch (Exception ex) when (IsEvidenceAccessFailure(ex) || ex is Win32Exception)
        {
            return Task.FromResult(Failed(
                "PlcsimAdvanced.V8.RuntimeApiDllInspectFailed",
                "PLCSIM Advanced V8 Runtime API DLL metadata could not be inspected.",
                "plcsim-advanced-v8-runtime-api-dll",
                "RuntimeApiDllInspectionFailure",
                ex,
                evidence));
        }

        PlcsimAdvancedV8ExportEvidence exportInspection;
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
                "PlcsimAdvanced.V8.RuntimeApiExportProbeFailed",
                "PLCSIM Advanced V8 Runtime API exports could not be inspected.",
                "plcsim-advanced-v8-runtime-api-exports",
                "RuntimeApiExportProbeFailure",
                ex,
                evidence));
        }

        if (!exportInspection.Succeeded)
        {
            return Task.FromResult(PlcsimAdvancedV8EnvironmentProbeResult.Failed(
                new PlcsimDiagnostic(
                    exportInspection.Code,
                    PlcsimDiagnosticSeverity.Error,
                    exportInspection.Message,
                    "EnvironmentProbe",
                    "plcsim-advanced-v8-runtime-api-exports"),
                evidence.Append(new PlcsimEvidence(
                    "plcsim-advanced-v8-runtime-api-exports",
                    "RuntimeApiExportProbe",
                    exportInspection.TechnicalDetail ?? exportInspection.Message)).ToArray()));
        }

        evidence.Add(new PlcsimEvidence(
            "plcsim-advanced-v8-runtime-api-exports",
            "RuntimeApiExports",
            $"{InitializeEntryPoint}; {DestroyInterfaceEntryPoint}"));
        return Task.FromResult(PlcsimAdvancedV8EnvironmentProbeResult.Ready(evidence));
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
                PlcsimAdvancedV8PathKind.Missing,
                Failed(
                    failureCode,
                    failureMessage,
                    evidenceId,
                    "PathInspectionFailure",
                    ex,
                    evidence));
        }
    }

    private static PlcsimAdvancedV8EnvironmentProbeResult Failed(
        string code,
        string message,
        string evidenceId,
        string evidenceKind,
        Exception exception,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        return PlcsimAdvancedV8EnvironmentProbeResult.Failed(
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
        PlcsimAdvancedV8PathKind Kind,
        PlcsimAdvancedV8EnvironmentProbeResult? Failure);
}

internal sealed class PlcsimAdvancedV8EnvironmentEvidenceSource
    : IPlcsimAdvancedV8EnvironmentEvidenceSource
{
    public PlcsimAdvancedV8RegistryEvidence ReadRegistryEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new PlcsimAdvancedV8RegistryEvidence(false, null);
        }

        using var key = Registry.LocalMachine.OpenSubKey(PlcsimAdvancedV8EnvironmentProbe.RegistryPath);
        var installPath = key?.GetValue(PlcsimAdvancedV8EnvironmentProbe.RegistryValueName) as string;
        return new PlcsimAdvancedV8RegistryEvidence(
            key is not null,
            string.IsNullOrWhiteSpace(installPath) ? null : installPath);
    }

    public PlcsimAdvancedV8PathKind InspectPath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory)
                ? PlcsimAdvancedV8PathKind.Directory
                : PlcsimAdvancedV8PathKind.File;
        }
        catch (FileNotFoundException)
        {
            return PlcsimAdvancedV8PathKind.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return PlcsimAdvancedV8PathKind.Missing;
        }
    }

    public PlcsimAdvancedV8FileVersionEvidence InspectFileVersion(string path)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        return new PlcsimAdvancedV8FileVersionEvidence(
            version.ProductVersion,
            version.FileVersion);
    }

    public PlcsimAdvancedV8ExportEvidence InspectExports(string path)
    {
        if (!NativeLibrary.TryLoad(path, out var handle))
        {
            return PlcsimAdvancedV8ExportEvidence.Failed(
                "PlcsimAdvanced.V8.RuntimeApiDllLoadFailed",
                "PLCSIM Advanced V8 Runtime API DLL could not be loaded for export inspection.",
                path);
        }

        try
        {
            if (!NativeLibrary.TryGetExport(
                handle,
                PlcsimAdvancedV8EnvironmentProbe.InitializeEntryPoint,
                out _))
            {
                return PlcsimAdvancedV8ExportEvidence.Failed(
                    "PlcsimAdvanced.V8.RuntimeApiInitializeEntryMissing",
                    "PLCSIM Advanced V8 Runtime API initialize entry point was not found.",
                    PlcsimAdvancedV8EnvironmentProbe.InitializeEntryPoint);
            }

            if (!NativeLibrary.TryGetExport(
                handle,
                PlcsimAdvancedV8EnvironmentProbe.DestroyInterfaceEntryPoint,
                out _))
            {
                return PlcsimAdvancedV8ExportEvidence.Failed(
                    "PlcsimAdvanced.V8.RuntimeApiDestroyEntryMissing",
                    "PLCSIM Advanced V8 Runtime API destroy-interface entry point was not found.",
                    PlcsimAdvancedV8EnvironmentProbe.DestroyInterfaceEntryPoint);
            }

            return PlcsimAdvancedV8ExportEvidence.SucceededResult;
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }
}

internal sealed record PlcsimAdvancedV8RegistryEvidence(
    bool HasRegistryKey,
    string? InstallPath);

internal sealed record PlcsimAdvancedV8FileVersionEvidence(
    string? ProductVersion,
    string? FileVersion);

internal sealed record PlcsimAdvancedV8ExportEvidence(
    bool Succeeded,
    string Code,
    string Message,
    string? TechnicalDetail)
{
    public static PlcsimAdvancedV8ExportEvidence SucceededResult { get; } = new(
        true,
        string.Empty,
        string.Empty,
        null);

    public static PlcsimAdvancedV8ExportEvidence Failed(
        string code,
        string message,
        string? technicalDetail)
    {
        return new PlcsimAdvancedV8ExportEvidence(false, code, message, technicalDetail);
    }
}

internal enum PlcsimAdvancedV8PathKind
{
    Missing,
    Directory,
    File
}

internal sealed record PlcsimAdvancedV8EnvironmentProbeResult(
    PlcsimAdvancedV8EnvironmentProbeState State,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool IsReady => State == PlcsimAdvancedV8EnvironmentProbeState.Ready;

    public static PlcsimAdvancedV8EnvironmentProbeResult Ready(IReadOnlyList<PlcsimEvidence> evidence)
    {
        return new PlcsimAdvancedV8EnvironmentProbeResult(
            PlcsimAdvancedV8EnvironmentProbeState.Ready,
            Array.Empty<PlcsimDiagnostic>(),
            evidence);
    }

    public static PlcsimAdvancedV8EnvironmentProbeResult Unavailable(
        PlcsimDiagnostic diagnostic,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        return new PlcsimAdvancedV8EnvironmentProbeResult(
            PlcsimAdvancedV8EnvironmentProbeState.Unavailable,
            new[] { diagnostic },
            evidence);
    }

    public static PlcsimAdvancedV8EnvironmentProbeResult Failed(
        PlcsimDiagnostic diagnostic,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        return new PlcsimAdvancedV8EnvironmentProbeResult(
            PlcsimAdvancedV8EnvironmentProbeState.Failed,
            new[] { diagnostic },
            evidence);
    }
}

internal enum PlcsimAdvancedV8EnvironmentProbeState
{
    Ready,
    Unavailable,
    Failed
}
