using System.Text.Json;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensSelectedBindingSelector
{
    private const string RequiredRuntimeKind = SiemensBindingKinds.PlcSimAdvanced;
    private const RuntimeLocality RequiredRuntimeLocality = RuntimeLocality.Local;

    public bool CanServeProjectSource(
        SiemensProfileRegistry registry,
        string projectSource)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var projectSourceSelection = SelectTiaProjectSourceCandidate(registry, Path.GetExtension(projectSource));
        if (projectSourceSelection.Candidate is null || projectSourceSelection.ProjectSource is null)
        {
            return false;
        }

        return SelectPlcsimCandidate(registry, projectSourceSelection.ProjectSource.Kind).Candidate is not null;
    }

    public SiemensSelectedBindingSelectionResult Select(
        AccessProfileInfo profileInfo,
        AccessProfileContext context,
        string projectSource,
        SiemensProfileRegistry registry,
        ProjectOwnershipMode projectOwnershipMode)
    {
        ArgumentNullException.ThrowIfNull(profileInfo);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(projectSource);
        ArgumentNullException.ThrowIfNull(registry);

        var identityResult = ValidateRegistryIdentity(profileInfo, context, registry);
        if (!identityResult.IsSuccessful)
        {
            return identityResult;
        }

        if (registry.State == AccessProfileRegistrationState.Error)
        {
            return Failure(
                "SiemensProfile.Load.ProfileRegistryErrorState",
                registry.Message ?? "Siemens profile registry is in an error state. Run profile registration.");
        }

        if (registry.State != AccessProfileRegistrationState.Registered)
        {
            return Failure(
                "SiemensProfile.Load.ProfileRegistryUnavailable",
                registry.Message ?? "Siemens profile registry is not registered. Run profile registration.");
        }

        if (projectOwnershipMode == ProjectOwnershipMode.ExternallyManaged)
        {
            return SelectExternal(context, projectSource, registry);
        }

        var tiaSelection = SelectTiaProjectSourceCandidate(registry, Path.GetExtension(projectSource));
        if (tiaSelection.Candidate is null || tiaSelection.ProjectSource is null)
        {
            return Failure(
                "SiemensProfile.Load.SelectedBindingMissing",
                tiaSelection.Message ?? "No usable TIA Openness binding candidate matches the requested project source.");
        }

        var plcSimSelection = SelectPlcsimCandidate(registry, tiaSelection.ProjectSource.Kind);
        if (plcSimSelection.Candidate is null)
        {
            return Failure(
                "SiemensProfile.Load.SelectedBindingMissing",
                plcSimSelection.Message ?? "No usable PLCSIM Advanced binding candidate matches the requested project source.");
        }

        var tiaBinding = tiaSelection.Candidate;
        var plcSimBinding = plcSimSelection.Candidate;
        var pathResult = ValidateSelectedBindingPaths(context, tiaBinding, "TIA Openness");
        if (!pathResult.IsSuccessful)
        {
            return pathResult;
        }

        pathResult = ValidateSelectedBindingPaths(context, plcSimBinding, "PLCSIM Advanced");
        if (!pathResult.IsSuccessful)
        {
            return pathResult;
        }

        var sourceResult = ValidateProjectSource(Path.GetExtension(projectSource), registry, tiaBinding, tiaSelection.ProjectSource);
        if (!sourceResult.IsSuccessful)
        {
            return sourceResult;
        }

        var runtimeResult = ValidateRuntime(registry, plcSimBinding, tiaSelection.ProjectSource.Kind);
        if (!runtimeResult.IsSuccessful)
        {
            return runtimeResult;
        }

        var contractResult = ValidateUnitTestContracts(registry, plcSimBinding);
        if (!contractResult.IsSuccessful)
        {
            return contractResult;
        }

        return SiemensSelectedBindingSelectionResult.Success(
            SiemensSelectedBindingLoadPlan.Create(tiaBinding, plcSimBinding));
    }

    private static SiemensSelectedBindingSelectionResult SelectExternal(
        AccessProfileContext context,
        string projectSource,
        SiemensProfileRegistry registry)
    {
        string? projectSourceKind = null;
        var projectSourceExtension = Path.GetExtension(projectSource);
        if (!string.IsNullOrWhiteSpace(projectSourceExtension))
        {
            var tiaSelection = SelectTiaProjectSourceCandidate(registry, projectSourceExtension);
            if (tiaSelection.Candidate is null || tiaSelection.ProjectSource is null)
            {
                return Failure(
                    "SiemensProfile.Load.SelectedBindingMissing",
                    tiaSelection.Message ?? "No usable TIA Openness binding candidate matches the requested project source.");
            }

            var pathResult = ValidateSelectedBindingPaths(context, tiaSelection.Candidate, "TIA Openness");
            if (!pathResult.IsSuccessful)
            {
                return pathResult;
            }

            var sourceResult = ValidateProjectSource(
                projectSourceExtension,
                registry,
                tiaSelection.Candidate,
                tiaSelection.ProjectSource);
            if (!sourceResult.IsSuccessful)
            {
                return sourceResult;
            }

            projectSourceKind = tiaSelection.ProjectSource.Kind;
        }

        var plcSimSelection = SelectPlcsimCandidate(registry, projectSourceKind);
        if (plcSimSelection.Candidate is null)
        {
            return Failure(
                "SiemensProfile.Load.SelectedBindingMissing",
                plcSimSelection.Message ?? "No usable PLCSIM Advanced binding candidate is available.");
        }

        var plcSimBinding = plcSimSelection.Candidate;
        var plcSimPathResult = ValidateSelectedBindingPaths(context, plcSimBinding, "PLCSIM Advanced");
        if (!plcSimPathResult.IsSuccessful)
        {
            return plcSimPathResult;
        }

        var runtimeResult = ValidateRuntime(registry, plcSimBinding, projectSourceKind);
        if (!runtimeResult.IsSuccessful)
        {
            return runtimeResult;
        }

        var contractResult = ValidateUnitTestContracts(registry, plcSimBinding);
        if (!contractResult.IsSuccessful)
        {
            return contractResult;
        }

        return SiemensSelectedBindingSelectionResult.Success(
            SiemensSelectedBindingLoadPlan.CreateExternal(plcSimBinding));
    }

    private static SiemensSelectedBindingSelectionResult ValidateRegistryIdentity(
        AccessProfileInfo profileInfo,
        AccessProfileContext context,
        SiemensProfileRegistry registry)
    {
        if (!string.Equals(registry.ProfileId, profileInfo.ProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(registry.ProfileVersion, profileInfo.Version, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(registry.ProfileRootPath, context.ProfileRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                "SiemensProfile.Load.ProfileRegistryIdentityMismatch",
                "Siemens profile registry identity does not match the loaded profile.");
        }

        return SiemensSelectedBindingSelectionResult.Success(null);
    }

    private static SiemensTiaCandidateSelection SelectTiaProjectSourceCandidate(
        SiemensProfileRegistry registry,
        string projectSourceExtension)
    {
        var matchingCandidates = registry.BindingCandidates
            .Where(candidate => string.Equals(candidate.BindingKind, SiemensBindingKinds.TiaOpenness, StringComparison.OrdinalIgnoreCase))
            .SelectMany(candidate => candidate.Declaration.ProjectSources
                .Where(source => string.Equals(source.Extension, projectSourceExtension, StringComparison.OrdinalIgnoreCase))
                .Select(source => new SiemensTiaCandidateSelection(candidate, source, null)))
            .ToArray();

        return matchingCandidates.Length switch
        {
            1 => matchingCandidates[0],
            0 => new SiemensTiaCandidateSelection(
                null,
                null,
                "No usable TIA Openness binding candidate supports the requested Siemens project source."),
            _ => new SiemensTiaCandidateSelection(
                null,
                null,
                "Multiple usable TIA Openness binding candidates support the requested Siemens project source.")
        };
    }

    private static SiemensPlcsimCandidateSelection SelectPlcsimCandidate(
        SiemensProfileRegistry registry,
        string? projectSourceKind)
    {
        var matchingCandidates = registry.BindingCandidates
            .Where(candidate => string.Equals(candidate.BindingKind, SiemensBindingKinds.PlcSimAdvanced, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => string.IsNullOrWhiteSpace(projectSourceKind)
                ? CandidateSupportsRequiredRuntime(candidate)
                : CandidateSupportsProjectSourceRuntime(candidate, projectSourceKind))
            .Where(CandidateSupportsRequiredUnitTestContracts)
            .ToArray();

        return matchingCandidates.Length switch
        {
            1 => new SiemensPlcsimCandidateSelection(matchingCandidates[0], null),
            0 => new SiemensPlcsimCandidateSelection(
                null,
                "No usable PLCSIM Advanced binding candidate is compatible with the selected Siemens project-source kind."),
            _ => new SiemensPlcsimCandidateSelection(
                null,
                "Multiple usable PLCSIM Advanced binding candidates are compatible with the selected Siemens project-source kind.")
        };
    }

    private static bool CandidateSupportsProjectSourceRuntime(
        SiemensBindingCandidateEvidence candidate,
        string projectSourceKind)
    {
        return candidate.Declaration.Runtimes.Any(runtime =>
            string.Equals(runtime.Kind, RequiredRuntimeKind, StringComparison.OrdinalIgnoreCase)
            && MapLocality(runtime.Locality) == RequiredRuntimeLocality
            && runtime.CompatibleProjectSourceKinds.Contains(projectSourceKind, StringComparer.OrdinalIgnoreCase));
    }

    private static bool CandidateSupportsRequiredRuntime(SiemensBindingCandidateEvidence candidate)
    {
        return candidate.Declaration.Runtimes.Any(runtime =>
            string.Equals(runtime.Kind, RequiredRuntimeKind, StringComparison.OrdinalIgnoreCase)
            && MapLocality(runtime.Locality) == RequiredRuntimeLocality);
    }

    private static bool CandidateSupportsRequiredUnitTestContracts(
        SiemensBindingCandidateEvidence candidate)
    {
        var requiredContract = SiemensAccessProfilePlugin.DefaultUnitTestContractVersion;
        return candidate.Declaration.UnitTestCommunicationContractVersions.Contains(
                requiredContract,
                StringComparer.OrdinalIgnoreCase)
            && candidate.Declaration.UnitTestEventStreamContractVersions.Contains(
                requiredContract,
                StringComparer.OrdinalIgnoreCase);
    }

    private static SiemensSelectedBindingSelectionResult ValidateSelectedBindingPaths(
        AccessProfileContext context,
        SiemensBindingCandidateEvidence binding,
        string displayName)
    {
        try
        {
            return ValidateSelectedBindingPathsCore(context, binding, displayName);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return Failure(
                "SiemensProfile.Load.SelectedBindingInaccessible",
                $"{displayName} selected binding evidence could not be accessed.",
                ex.Message);
        }
    }

    private static SiemensSelectedBindingSelectionResult ValidateSelectedBindingPathsCore(
        AccessProfileContext context,
        SiemensBindingCandidateEvidence binding,
        string displayName)
    {
        if (binding.DescriptorSchemaVersion != SiemensBindingPackageDescriptorContract.CurrentSchemaVersion)
        {
            return Failure(
                "SiemensProfile.Load.SelectedBindingMismatch",
                $"{displayName} selected binding descriptor schema version is not supported.");
        }

        var profileRoot = Path.GetFullPath(context.ProfileRootPath);
        var packageRoot = Path.GetFullPath(binding.PackageRootPath);
        var descriptorPath = Path.GetFullPath(binding.DescriptorPath);
        if (!IsPathBelow(profileRoot, packageRoot)
            || !IsPathBelow(profileRoot, descriptorPath)
            || !IsPathBelow(packageRoot, descriptorPath))
        {
            return Failure(
                "SiemensProfile.Load.SelectedBindingInaccessible",
                $"{displayName} selected binding paths are outside the selected Siemens profile root.");
        }

        if (!Directory.Exists(packageRoot)
            || !File.Exists(descriptorPath))
        {
            return Failure(
                "SiemensProfile.Load.SelectedBindingInaccessible",
                $"{displayName} selected binding package or descriptor is not accessible.");
        }

        var entryAssemblyPath = ResolveEntryAssemblyPath(packageRoot, binding.EntryPoint.Assembly);
        if (entryAssemblyPath is null || !File.Exists(entryAssemblyPath))
        {
            return Failure(
                "SiemensProfile.Load.SelectedBindingInaccessible",
                $"{displayName} selected binding entry assembly is not accessible.");
        }

        var descriptorFingerprint = SiemensBindingPackageFingerprint.ComputeFile(descriptorPath);
        if (!string.Equals(descriptorFingerprint, binding.DescriptorFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                "SiemensProfile.Load.SelectedBindingStale",
                $"{displayName} selected binding descriptor fingerprint no longer matches registration evidence.");
        }

        var packageFingerprint = SiemensBindingPackageFingerprint.ComputePackage(packageRoot);
        if (!string.Equals(packageFingerprint, binding.PackageFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                "SiemensProfile.Load.SelectedBindingStale",
                $"{displayName} selected binding package fingerprint no longer matches registration evidence.");
        }

        SiemensBindingPackageDescriptor? descriptor;
        try
        {
            descriptor = SiemensBindingPackageDescriptorReader.Read(descriptorPath);
        }
        catch (Exception ex) when (ex is JsonException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return Failure(
                "SiemensProfile.Load.SelectedBindingMismatch",
                $"{displayName} selected binding descriptor could not be revalidated.",
                ex.Message);
        }

        if (descriptor is null || !MatchesRegisteredEvidence(descriptor, binding))
        {
            return Failure(
                "SiemensProfile.Load.SelectedBindingMismatch",
                $"{displayName} selected binding descriptor does not match registration evidence.");
        }

        return SiemensSelectedBindingSelectionResult.Success(null);
    }

    private static bool MatchesRegisteredEvidence(
        SiemensBindingPackageDescriptor descriptor,
        SiemensBindingCandidateEvidence evidence)
    {
        return descriptor.Info is not null
            && descriptor.EntryPoint is not null
            && descriptor.Declaration is not null
            && descriptor.Declaration.AccessApis is not null
            && descriptor.Declaration.ProjectSources is not null
            && descriptor.Declaration.Runtimes is not null
            && descriptor.Declaration.UnitTestCommunicationContractVersions is not null
            && descriptor.Declaration.UnitTestEventStreamContractVersions is not null
            && descriptor.SchemaVersion == evidence.DescriptorSchemaVersion
            && string.Equals(descriptor.Info.BindingKind, evidence.BindingKind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(descriptor.Info.BindingVersion, evidence.BindingVersion, StringComparison.OrdinalIgnoreCase)
            && string.Equals(descriptor.EntryPoint.Assembly, evidence.EntryPoint.Assembly, StringComparison.OrdinalIgnoreCase)
            && string.Equals(descriptor.EntryPoint.TypeName, evidence.EntryPoint.TypeName, StringComparison.Ordinal)
            && DeclarationMatches(descriptor.Declaration, evidence.Declaration);
    }

    private static bool DeclarationMatches(
        SiemensBindingDeclaration descriptor,
        SiemensBindingDeclaration evidence)
    {
        return SequenceMatches(
                descriptor.AccessApis,
                evidence.AccessApis,
                (left, right) => string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(left.ProductVersion, right.ProductVersion, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(left.AccessApiVersion, right.AccessApiVersion, StringComparison.OrdinalIgnoreCase))
            && SequenceMatches(
                descriptor.ProjectSources,
                evidence.ProjectSources,
                (left, right) => string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(left.Extension, right.Extension, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(left.Version, right.Version, StringComparison.OrdinalIgnoreCase)
                    && left.OpenMode == right.OpenMode)
            && SequenceMatches(
                descriptor.Runtimes,
                evidence.Runtimes,
                (left, right) => string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(left.Version, right.Version, StringComparison.OrdinalIgnoreCase)
                    && left.Locality == right.Locality
                    && left.CompatibleProjectSourceKinds.SequenceEqual(
                        right.CompatibleProjectSourceKinds,
                        StringComparer.OrdinalIgnoreCase))
            && descriptor.UnitTestCommunicationContractVersions.SequenceEqual(
                evidence.UnitTestCommunicationContractVersions,
                StringComparer.OrdinalIgnoreCase)
            && descriptor.UnitTestEventStreamContractVersions.SequenceEqual(
                evidence.UnitTestEventStreamContractVersions,
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool SequenceMatches<T>(
        IReadOnlyList<T> left,
        IReadOnlyList<T> right,
        Func<T, T, bool> matches)
    {
        return left.Count == right.Count
            && left.Zip(right).All(pair => matches(pair.First, pair.Second));
    }

    private static SiemensSelectedBindingSelectionResult ValidateProjectSource(
        string projectSourceExtension,
        SiemensProfileRegistry registry,
        SiemensBindingCandidateEvidence tiaBinding,
        SiemensProjectSourceDeclaration selectedProjectSource)
    {
        var hasRegistryProjectSource = registry.ProjectSources.Any(source =>
            source.Availability == CapabilityAvailability.Usable
            && string.Equals(source.Kind, selectedProjectSource.Kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.Extension, projectSourceExtension, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.Version, selectedProjectSource.Version, StringComparison.OrdinalIgnoreCase));
        var hasBindingProjectSource = tiaBinding.Declaration.ProjectSources.Any(bindingSource =>
            string.Equals(bindingSource.Kind, selectedProjectSource.Kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(bindingSource.Extension, selectedProjectSource.Extension, StringComparison.OrdinalIgnoreCase)
            && string.Equals(bindingSource.Version, selectedProjectSource.Version, StringComparison.OrdinalIgnoreCase)
            && bindingSource.OpenMode == selectedProjectSource.OpenMode);
        var hasRegisteredAccessApi = registry.AccessApis.Any(api =>
            api.Availability == CapabilityAvailability.Usable
            && string.Equals(api.Kind, SiemensBindingKinds.TiaOpenness, StringComparison.OrdinalIgnoreCase));
        var hasBindingAccessApi = tiaBinding.Declaration.AccessApis.Any(api =>
            string.Equals(api.Kind, SiemensBindingKinds.TiaOpenness, StringComparison.OrdinalIgnoreCase));

        return hasRegistryProjectSource && hasBindingProjectSource && hasRegisteredAccessApi && hasBindingAccessApi
            ? SiemensSelectedBindingSelectionResult.Success(null)
            : Failure(
                "SiemensProfile.Load.SelectedBindingMismatch",
                "Selected TIA Openness binding does not match the requested registered project source.");
    }

    private static SiemensSelectedBindingSelectionResult ValidateRuntime(
        SiemensProfileRegistry registry,
        SiemensBindingCandidateEvidence plcSimBinding,
        string? selectedProjectSourceKind)
    {
        var matchingRuntimeDeclarations = plcSimBinding.Declaration.Runtimes
            .Where(runtime => string.Equals(runtime.Kind, RequiredRuntimeKind, StringComparison.OrdinalIgnoreCase)
                && MapLocality(runtime.Locality) == RequiredRuntimeLocality
                && (string.IsNullOrWhiteSpace(selectedProjectSourceKind)
                    || runtime.CompatibleProjectSourceKinds.Contains(selectedProjectSourceKind, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
        var hasBindingRuntime = matchingRuntimeDeclarations.Length > 0;
        var hasRegistryRuntime = matchingRuntimeDeclarations.Any(declaration =>
            registry.Runtimes.Any(runtime =>
                runtime.Availability == CapabilityAvailability.Usable
                && string.Equals(runtime.Kind, RequiredRuntimeKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(runtime.Version, declaration.Version, StringComparison.OrdinalIgnoreCase)
                && runtime.Locality == RequiredRuntimeLocality));

        return hasRegistryRuntime && hasBindingRuntime
            ? SiemensSelectedBindingSelectionResult.Success(null)
            : Failure(
                "SiemensProfile.Load.SelectedBindingMismatch",
                "Selected PLCSIM Advanced binding does not match the required runtime capability.");
    }

    private static SiemensSelectedBindingSelectionResult ValidateUnitTestContracts(
        SiemensProfileRegistry registry,
        SiemensBindingCandidateEvidence plcSimBinding)
    {
        var requiredContract = SiemensAccessProfilePlugin.DefaultUnitTestContractVersion;
        var hasRegistryContract = registry.UnitTestContractVersions.Contains(
            requiredContract,
            StringComparer.OrdinalIgnoreCase);
        var hasBindingContract = CandidateSupportsRequiredUnitTestContracts(plcSimBinding);

        return hasRegistryContract && hasBindingContract
            ? SiemensSelectedBindingSelectionResult.Success(null)
            : Failure(
                "SiemensProfile.Load.SelectedBindingMismatch",
                "Selected PLCSIM Advanced binding does not support the required UnitTest contract versions.");
    }

    private static RuntimeLocality MapLocality(SiemensRuntimeLocality locality)
    {
        return locality switch
        {
            SiemensRuntimeLocality.Local => RuntimeLocality.Local,
            SiemensRuntimeLocality.Remote => RuntimeLocality.Remote,
            _ => throw new ArgumentOutOfRangeException(nameof(locality), locality, "Unsupported Siemens runtime locality.")
        };
    }

    private static string? ResolveEntryAssemblyPath(string packageRoot, string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || Path.IsPathRooted(assemblyPath))
        {
            return null;
        }

        var fullAssemblyPath = Path.GetFullPath(Path.Combine(packageRoot, assemblyPath));
        return IsPathBelow(packageRoot, fullAssemblyPath) ? fullAssemblyPath : null;
    }

    private static bool IsPathBelow(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static SiemensSelectedBindingSelectionResult Failure(
        string code,
        string message,
        string? technicalDetail = null)
    {
        return SiemensSelectedBindingSelectionResult.Failure(
            message,
            SiemensDiagnostics.ConfigurationError(code, message, technicalDetail));
    }
}

internal sealed record SiemensSelectedBindingSelectionResult(
    bool IsSuccessful,
    SiemensSelectedBindingLoadPlan? LoadPlan,
    string? Message,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public static SiemensSelectedBindingSelectionResult Success(
        SiemensSelectedBindingLoadPlan? loadPlan)
    {
        return new SiemensSelectedBindingSelectionResult(true, loadPlan, null, []);
    }

    public static SiemensSelectedBindingSelectionResult Failure(
        string message,
        Diagnostic diagnostic)
    {
        return new SiemensSelectedBindingSelectionResult(false, null, message, [diagnostic]);
    }
}

internal sealed record SiemensTiaCandidateSelection(
    SiemensBindingCandidateEvidence? Candidate,
    SiemensProjectSourceDeclaration? ProjectSource,
    string? Message);

internal sealed record SiemensPlcsimCandidateSelection(
    SiemensBindingCandidateEvidence? Candidate,
    string? Message);
