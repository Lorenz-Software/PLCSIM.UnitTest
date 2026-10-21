using System.Text.Json;
using System.Text.Json.Serialization;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensBindingPackageDiscovery
{
    private const string LibDirectoryName = "lib";
    private const string BindingsDirectoryName = "bindings";

    public SiemensBindingPackageDiscoveryResult Discover(string profileRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRootPath);

        var candidates = new List<SiemensBindingPackageCandidate>();
        var diagnostics = new List<Diagnostic>();
        var bindingsRoot = Path.Combine(Path.GetFullPath(profileRootPath), LibDirectoryName, BindingsDirectoryName);

        if (!Directory.Exists(bindingsRoot))
        {
            return new SiemensBindingPackageDiscoveryResult(bindingsRoot, candidates, diagnostics);
        }

        string[] packageDirectories;
        try
        {
            packageDirectories = Directory.GetDirectories(bindingsRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.BindingDiscovery.DirectoryInaccessible",
                $"Siemens binding package directory '{bindingsRoot}' could not be read.",
                ex.Message));
            return new SiemensBindingPackageDiscoveryResult(bindingsRoot, candidates, diagnostics);
        }

        foreach (var packageDirectory in packageDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var descriptorPath = Path.Combine(packageDirectory, SiemensBindingPackageDescriptorContract.FileName);
            if (!File.Exists(descriptorPath))
            {
                continue;
            }

            DiscoverPackage(packageDirectory, descriptorPath, candidates, diagnostics);
        }

        return new SiemensBindingPackageDiscoveryResult(bindingsRoot, candidates, diagnostics);
    }

    private static void DiscoverPackage(
        string packageDirectory,
        string descriptorPath,
        List<SiemensBindingPackageCandidate> candidates,
        List<Diagnostic> diagnostics)
    {
        SiemensBindingPackageDescriptor? descriptor;
        try
        {
            descriptor = SiemensBindingPackageDescriptorReader.Read(descriptorPath);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.BindingDiscovery.DescriptorInvalidJson",
                $"Siemens binding descriptor '{descriptorPath}' is not valid JSON.",
                ex.Message));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.BindingDiscovery.DescriptorUnreadable",
                $"Siemens binding descriptor '{descriptorPath}' could not be read.",
                ex.Message));
            return;
        }

        if (descriptor is null)
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.BindingDiscovery.DescriptorEmpty",
                $"Siemens binding descriptor '{descriptorPath}' is empty."));
            return;
        }

        var candidateDiagnostics = new List<Diagnostic>();
        var state = ValidateDescriptor(
            descriptor,
            packageDirectory,
            descriptorPath,
            candidateDiagnostics,
            out var entryAssemblyPath);

        string descriptorFingerprint = string.Empty;
        string packageFingerprint = string.Empty;
        if (state == SiemensBindingPackageCandidateState.Discovered)
        {
            descriptorFingerprint = SiemensBindingPackageFingerprint.ComputeFile(descriptorPath);
            packageFingerprint = SiemensBindingPackageFingerprint.ComputePackage(packageDirectory);
        }

        diagnostics.AddRange(candidateDiagnostics);
        candidates.Add(new SiemensBindingPackageCandidate(
            Path.GetFullPath(packageDirectory),
            Path.GetFullPath(descriptorPath),
            entryAssemblyPath,
            descriptorFingerprint,
            packageFingerprint,
            descriptor,
            state,
            candidateDiagnostics));
    }

    private static SiemensBindingPackageCandidateState ValidateDescriptor(
        SiemensBindingPackageDescriptor descriptor,
        string packageDirectory,
        string descriptorPath,
        List<Diagnostic> diagnostics,
        out string? entryAssemblyPath)
    {
        entryAssemblyPath = null;

        if (descriptor.SchemaVersion != SiemensBindingPackageDescriptorContract.CurrentSchemaVersion)
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.BindingDiscovery.DescriptorUnsupportedSchemaVersion",
                $"Siemens binding descriptor '{descriptorPath}' uses unsupported schema version '{descriptor.SchemaVersion}'."));
            return SiemensBindingPackageCandidateState.MetadataInvalid;
        }

        if (!ValidateInfo(descriptor.Info, descriptorPath, diagnostics))
        {
            return SiemensBindingPackageCandidateState.MetadataInvalid;
        }

        if (!ValidateEntryPoint(descriptor.EntryPoint, packageDirectory, descriptorPath, diagnostics, out entryAssemblyPath))
        {
            return SiemensBindingPackageCandidateState.MetadataInvalid;
        }

        if (!ValidateDeclaration(descriptor.Declaration, descriptorPath, diagnostics))
        {
            return SiemensBindingPackageCandidateState.MetadataInvalid;
        }

        if (!File.Exists(entryAssemblyPath))
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.BindingDiscovery.EntryAssemblyMissing",
                $"Siemens binding descriptor '{descriptorPath}' references missing entry assembly '{entryAssemblyPath}'."));
            return SiemensBindingPackageCandidateState.LoadFailed;
        }

        return SiemensBindingPackageCandidateState.Discovered;
    }

    private static bool ValidateInfo(
        SiemensBindingInfo? info,
        string descriptorPath,
        List<Diagnostic> diagnostics)
    {
        if (info is null
            || string.IsNullOrWhiteSpace(info.BindingKind)
            || string.IsNullOrWhiteSpace(info.BindingVersion)
            || string.IsNullOrWhiteSpace(info.DisplayName))
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.BindingDiscovery.InfoInvalid",
                $"Siemens binding descriptor '{descriptorPath}' misses required binding info metadata."));
            return false;
        }

        if (!string.Equals(info.BindingKind, SiemensBindingKinds.TiaOpenness, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(info.BindingKind, SiemensBindingKinds.PlcSimAdvanced, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.BindingDiscovery.BindingKindUnsupported",
                $"Siemens binding descriptor '{descriptorPath}' defines unsupported binding kind '{info.BindingKind}'."));
            return false;
        }

        if (!Version.TryParse(info.BindingVersion, out _))
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.BindingDiscovery.BindingVersionInvalid",
                $"Siemens binding descriptor '{descriptorPath}' defines invalid binding version '{info.BindingVersion}'."));
            return false;
        }

        return true;
    }

    private static bool ValidateEntryPoint(
        SiemensBindingEntryPoint? entryPoint,
        string packageDirectory,
        string descriptorPath,
        List<Diagnostic> diagnostics,
        out string? entryAssemblyPath)
    {
        entryAssemblyPath = null;
        if (entryPoint is null
            || string.IsNullOrWhiteSpace(entryPoint.Assembly)
            || string.IsNullOrWhiteSpace(entryPoint.TypeName))
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.BindingDiscovery.EntryPointInvalid",
                $"Siemens binding descriptor '{descriptorPath}' misses required entry point metadata."));
            return false;
        }

        if (Path.IsPathRooted(entryPoint.Assembly))
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.BindingDiscovery.EntryAssemblyPathInvalid",
                $"Siemens binding descriptor '{descriptorPath}' defines an entry assembly path that is not relative."));
            return false;
        }

        var fullPackageDirectory = Path.GetFullPath(packageDirectory);
        var fullAssemblyPath = Path.GetFullPath(Path.Combine(fullPackageDirectory, entryPoint.Assembly));
        var packagePrefix = fullPackageDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? fullPackageDirectory
            : fullPackageDirectory + Path.DirectorySeparatorChar;
        if (!fullAssemblyPath.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.BindingDiscovery.EntryAssemblyPathInvalid",
                $"Siemens binding descriptor '{descriptorPath}' defines an entry assembly path outside the package directory."));
            return false;
        }

        entryAssemblyPath = fullAssemblyPath;
        return true;
    }

    private static bool ValidateDeclaration(
        SiemensBindingDeclaration? declaration,
        string descriptorPath,
        List<Diagnostic> diagnostics)
    {
        if (declaration is null
            || declaration.AccessApis is null
            || declaration.ProjectSources is null
            || declaration.Runtimes is null
            || declaration.UnitTestCommunicationContractVersions is null
            || declaration.UnitTestEventStreamContractVersions is null)
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.BindingDiscovery.DeclarationInvalid",
                $"Siemens binding descriptor '{descriptorPath}' misses required declaration arrays."));
            return false;
        }

        var valid = true;
        valid &= ValidateAccessApis(declaration.AccessApis, descriptorPath, diagnostics);
        valid &= ValidateProjectSources(declaration.ProjectSources, descriptorPath, diagnostics);
        valid &= ValidateRuntimes(declaration.Runtimes, descriptorPath, diagnostics);
        valid &= ValidateVersionList(declaration.UnitTestCommunicationContractVersions, "UnitTestCommunicationContractVersions", descriptorPath, diagnostics);
        valid &= ValidateVersionList(declaration.UnitTestEventStreamContractVersions, "UnitTestEventStreamContractVersions", descriptorPath, diagnostics);
        return valid;
    }

    private static bool ValidateAccessApis(
        IEnumerable<SiemensAccessApiDeclaration> declarations,
        string descriptorPath,
        List<Diagnostic> diagnostics)
    {
        var valid = true;
        foreach (var declaration in declarations)
        {
            if (string.IsNullOrWhiteSpace(declaration.Kind)
                || string.IsNullOrWhiteSpace(declaration.ProductVersion)
                || string.IsNullOrWhiteSpace(declaration.AccessApiVersion))
            {
                diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                    "SiemensProfile.BindingDiscovery.AccessApiDeclarationInvalid",
                    $"Siemens binding descriptor '{descriptorPath}' contains an invalid access API declaration."));
                valid = false;
            }
        }

        return valid;
    }

    private static bool ValidateProjectSources(
        IEnumerable<SiemensProjectSourceDeclaration> declarations,
        string descriptorPath,
        List<Diagnostic> diagnostics)
    {
        var valid = true;
        foreach (var declaration in declarations)
        {
            if (string.IsNullOrWhiteSpace(declaration.Kind)
                || string.IsNullOrWhiteSpace(declaration.Extension)
                || string.IsNullOrWhiteSpace(declaration.Version)
                || !declaration.Extension.StartsWith(".", StringComparison.Ordinal))
            {
                diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                    "SiemensProfile.BindingDiscovery.ProjectSourceDeclarationInvalid",
                    $"Siemens binding descriptor '{descriptorPath}' contains an invalid project source declaration."));
                valid = false;
            }
        }

        return valid;
    }

    private static bool ValidateRuntimes(
        IEnumerable<SiemensRuntimeDeclaration> declarations,
        string descriptorPath,
        List<Diagnostic> diagnostics)
    {
        var valid = true;
        foreach (var declaration in declarations)
        {
            if (string.IsNullOrWhiteSpace(declaration.Kind)
                || string.IsNullOrWhiteSpace(declaration.Version)
                || !Enum.IsDefined(declaration.Locality)
                || declaration.CompatibleProjectSourceKinds is null
                || declaration.CompatibleProjectSourceKinds.Count == 0
                || declaration.CompatibleProjectSourceKinds.Any(string.IsNullOrWhiteSpace))
            {
                diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                    "SiemensProfile.BindingDiscovery.RuntimeDeclarationInvalid",
                    $"Siemens binding descriptor '{descriptorPath}' contains an invalid runtime declaration."));
                valid = false;
            }
        }

        return valid;
    }

    private static bool ValidateVersionList(
        IEnumerable<string> versions,
        string fieldName,
        string descriptorPath,
        List<Diagnostic> diagnostics)
    {
        var valid = true;
        foreach (var version in versions)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                    $"SiemensProfile.BindingDiscovery.{fieldName}.Invalid",
                    $"Siemens binding descriptor '{descriptorPath}' contains an invalid {fieldName} entry."));
                valid = false;
            }
        }

        return valid;
    }

}

internal static class SiemensBindingPackageDescriptorReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SiemensBindingPackageDescriptor? Read(string descriptorPath)
    {
        var json = File.ReadAllText(descriptorPath);
        return JsonSerializer.Deserialize<SiemensBindingPackageDescriptor>(json, SerializerOptions);
    }
}

internal sealed record SiemensBindingPackageDiscoveryResult(
    string BindingsRootPath,
    IReadOnlyList<SiemensBindingPackageCandidate> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics);

internal sealed record SiemensBindingPackageCandidate(
    string PackageRootPath,
    string DescriptorPath,
    string? EntryAssemblyPath,
    string DescriptorFingerprint,
    string PackageFingerprint,
    SiemensBindingPackageDescriptor Descriptor,
    SiemensBindingPackageCandidateState State,
    IReadOnlyList<Diagnostic> Diagnostics);

internal enum SiemensBindingPackageCandidateState
{
    Discovered,
    MetadataInvalid,
    LoadFailed
}
