using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.PluginLoading;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensProfileRegistrationOrchestrator
{
    private readonly SiemensBindingPackageDiscovery bindingPackageDiscovery;

    public SiemensProfileRegistrationOrchestrator(
        SiemensBindingPackageDiscovery? bindingPackageDiscovery = null)
    {
        this.bindingPackageDiscovery = bindingPackageDiscovery ?? new SiemensBindingPackageDiscovery();
    }

    public async Task<SiemensProfileRegistration> RegisterAsync(
        AccessProfileContext context,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(bindingContext);
        cancellationToken.ThrowIfCancellationRequested();

        var discovery = bindingPackageDiscovery.Discover(context.ProfileRootPath);
        cancellationToken.ThrowIfCancellationRequested();
        var discoveredCandidates = discovery.Candidates
            .Where(candidate => candidate.State == SiemensBindingPackageCandidateState.Discovered)
            .ToArray();
        var readiness = await ResolveBindingReadinessAsync(
            discoveredCandidates,
            bindingContext,
            cancellationToken).ConfigureAwait(false);
        var readinessDiagnostics = readiness
            .SelectMany(pair => pair.Value.Diagnostics)
            .Select(diagnostic => SiemensDiagnostics.ConfigurationError(
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.TechnicalDetail))
            .ToArray();
        var capabilities = CreateCapabilities(discoveredCandidates, readiness);

        if (HasRegistrationError(discovery))
        {
            return CreateRegistration(
                AccessProfileRegistrationState.Error,
                capabilities,
                "Siemens binding package discovery found registration errors.",
                discovery,
                [],
                readinessDiagnostics);
        }

        if (readiness.Values.Any(result => result.State == SiemensBindingReadinessState.Error))
        {
            return CreateRegistration(
                AccessProfileRegistrationState.Error,
                capabilities,
                "Siemens binding readiness resolution found registration errors.",
                discovery,
                [],
                readinessDiagnostics);
        }

        var tiaBindings = GetCandidates(discoveredCandidates, SiemensBindingKinds.TiaOpenness)
            .Where(candidate => IsBindingReady(candidate, readiness))
            .ToArray();
        var plcSimBindings = GetCandidates(discoveredCandidates, SiemensBindingKinds.PlcSimAdvanced)
            .Where(candidate => IsBindingReady(candidate, readiness))
            .ToArray();

        if (plcSimBindings.Length == 0)
        {
            return CreateRegistration(
                AccessProfileRegistrationState.Unavailable,
                capabilities,
                "Siemens registration requires one usable PLCSIM Advanced binding.",
                discovery,
                CreateBindingCandidateEvidence(
                    tiaBindings.Concat(plcSimBindings)
                        .OrderBy(candidate => candidate.Descriptor.Info.BindingKind, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(candidate => candidate.Descriptor.Info.BindingVersion, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(candidate => candidate.PackageRootPath, StringComparer.OrdinalIgnoreCase),
                    readiness),
                readinessDiagnostics);
        }

        var message = tiaBindings.Length == 0
            ? "Siemens access profile registration completed for externally managed project operation; no usable TIA Openness binding is available."
            : "Siemens access profile registration completed.";
        return CreateRegistration(
            AccessProfileRegistrationState.Registered,
            capabilities,
            message,
            discovery,
            CreateBindingCandidateEvidence(
                tiaBindings.Concat(plcSimBindings)
                    .OrderBy(candidate => candidate.Descriptor.Info.BindingKind, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(candidate => candidate.Descriptor.Info.BindingVersion, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(candidate => candidate.PackageRootPath, StringComparer.OrdinalIgnoreCase),
                readiness),
            readinessDiagnostics);
    }

    private static SiemensProfileRegistration CreateRegistration(
        AccessProfileRegistrationState state,
        AccessProfileCapabilities capabilities,
        string message,
        SiemensBindingPackageDiscoveryResult discovery,
        IReadOnlyList<SiemensBindingCandidateEvidence> bindingCandidates,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        return new SiemensProfileRegistration(
            new AccessProfileRegistrationResult(state, capabilities, message),
            bindingCandidates,
            discovery,
            diagnostics);
    }

    private static bool HasRegistrationError(SiemensBindingPackageDiscoveryResult discovery)
    {
        return discovery.Diagnostics.Any(diagnostic => diagnostic.Level >= LogLevel.Error);
    }

    private static SiemensBindingPackageCandidate[] GetCandidates(
        IEnumerable<SiemensBindingPackageCandidate> candidates,
        string bindingKind)
    {
        return candidates
            .Where(candidate => string.Equals(candidate.Descriptor.Info.BindingKind, bindingKind, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.Descriptor.Info.BindingVersion, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.PackageRootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<Dictionary<SiemensBindingPackageCandidate, SiemensBindingReadinessResult>> ResolveBindingReadinessAsync(
        IReadOnlyList<SiemensBindingPackageCandidate> candidates,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<SiemensBindingPackageCandidate, SiemensBindingReadinessResult>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[candidate] = await ResolveCandidateReadinessAsync(
                candidate,
                bindingContext,
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static async Task<SiemensBindingReadinessResult> ResolveCandidateReadinessAsync(
        SiemensBindingPackageCandidate candidate,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(candidate.EntryAssemblyPath))
        {
            return SiemensBindingReadinessResult.Error(
                "SiemensProfile.BindingReadiness.EntryAssemblyMissing",
                $"The Siemens binding package '{candidate.PackageRootPath}' does not declare a usable entry assembly path.",
                candidate.DescriptorPath);
        }

        SiemensBindingPackageLease<ISiemensTechnologyBinding> lease;
        try
        {
            lease = await SiemensBindingPackageLease<ISiemensTechnologyBinding>.LoadAsync(
                candidate.PackageRootPath,
                candidate.EntryAssemblyPath,
                candidate.Descriptor.EntryPoint.TypeName,
                bindingContext,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PluginPackageLoadException ex)
        {
            return CreateReadinessPackageLoadError(candidate, ex);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or BadImageFormatException
            or ArgumentException
            or NotSupportedException)
        {
            return SiemensBindingReadinessResult.Error(
                "SiemensProfile.BindingReadiness.EntryCreateFailed",
                $"The Siemens binding entry point '{candidate.Descriptor.EntryPoint.TypeName}' could not be created.",
                ex.Message);
        }
        catch (Exception ex)
        {
            return SiemensBindingReadinessResult.Error(
                "SiemensProfile.BindingReadiness.EntryCreateFailed",
                $"The Siemens binding entry point '{candidate.Descriptor.EntryPoint.TypeName}' failed during initialization.",
                ex.Message);
        }

        SiemensBindingReadinessResult result;
        try
        {
            var binding = lease.Binding;
            if (!string.Equals(binding.Info.BindingKind, candidate.Descriptor.Info.BindingKind, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(binding.Info.BindingVersion, candidate.Descriptor.Info.BindingVersion, StringComparison.OrdinalIgnoreCase))
            {
                result = SiemensBindingReadinessResult.Error(
                    "SiemensProfile.BindingReadiness.EntryBindingMismatch",
                    $"The Siemens binding entry point '{candidate.Descriptor.EntryPoint.TypeName}' reports kind/version '{binding.Info.BindingKind}/{binding.Info.BindingVersion}' but the descriptor declares '{candidate.Descriptor.Info.BindingKind}/{candidate.Descriptor.Info.BindingVersion}'.",
                    candidate.DescriptorPath);
            }
            else
            {
                result = binding.ResolveReadiness(new SiemensBindingReadinessRequest(
                    candidate.PackageRootPath,
                    candidate.EntryAssemblyPath,
                    candidate.Descriptor.Declaration));
            }
        }
        catch (Exception ex)
        {
            result = SiemensBindingReadinessResult.Error(
                "SiemensProfile.BindingReadiness.ResolveFailed",
                $"The Siemens binding '{candidate.Descriptor.Info.BindingKind}/{candidate.Descriptor.Info.BindingVersion}' failed while resolving readiness.",
                ex.InnerException?.Message ?? ex.Message);
        }

        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return SiemensBindingReadinessResult.Error(
                "SiemensProfile.BindingReadiness.EntryDisposeFailed",
                $"The Siemens binding '{candidate.Descriptor.Info.BindingKind}/{candidate.Descriptor.Info.BindingVersion}' could not be disposed after readiness resolution.",
                ex.Message);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static SiemensBindingReadinessResult CreateReadinessPackageLoadError(
        SiemensBindingPackageCandidate candidate,
        PluginPackageLoadException exception)
    {
        var code = exception.FailureKind switch
        {
            PluginPackageLoadFailureKind.EntryPointTypeNotFound => "SiemensProfile.BindingReadiness.EntryTypeMissing",
            PluginPackageLoadFailureKind.EntryPointTypeInvalid => "SiemensProfile.BindingReadiness.EntryTypeInvalid",
            _ => "SiemensProfile.BindingReadiness.EntryCreateFailed"
        };
        return SiemensBindingReadinessResult.Error(
            code,
            $"The Siemens binding entry point '{candidate.Descriptor.EntryPoint.TypeName}' could not be loaded from package '{candidate.PackageRootPath}'.",
            exception.InnerException?.Message ?? exception.Message);
    }

    private static bool IsBindingReady(
        SiemensBindingPackageCandidate candidate,
        IReadOnlyDictionary<SiemensBindingPackageCandidate, SiemensBindingReadinessResult> readiness)
    {
        return readiness.TryGetValue(candidate, out var result) && result.IsUsable;
    }

    private static string? GetReadinessEvidenceSummary(
        SiemensBindingPackageCandidate candidate,
        IReadOnlyDictionary<SiemensBindingPackageCandidate, SiemensBindingReadinessResult> readiness)
    {
        return readiness.TryGetValue(candidate, out var result)
            ? result.EvidenceSummary
            : "Selected from binding descriptor discovery.";
    }

    private static AccessProfileCapabilities CreateCapabilities(
        IReadOnlyList<SiemensBindingPackageCandidate> candidates,
        IReadOnlyDictionary<SiemensBindingPackageCandidate, SiemensBindingReadinessResult> readiness)
    {
        return new AccessProfileCapabilities(
            candidates
                .SelectMany(candidate => candidate.Descriptor.Declaration.AccessApis.Select(declaration => new { candidate, declaration }))
                .Select(item => new AccessApiCapability(
                    item.declaration.Kind,
                    item.declaration.ProductVersion,
                    item.declaration.AccessApiVersion,
                    GetBindingAvailability(item.candidate, readiness)))
                .Distinct()
                .OrderBy(capability => capability.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(capability => capability.ProductVersion, StringComparer.OrdinalIgnoreCase)
                .ThenBy(capability => capability.AccessApiVersion, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            candidates
                .SelectMany(candidate => candidate.Descriptor.Declaration.ProjectSources.Select(declaration => new { candidate, declaration }))
                .Select(declaration => new ProjectSourceCapability(
                    declaration.declaration.Kind,
                    declaration.declaration.Extension,
                    declaration.declaration.Version,
                    GetBindingAvailability(declaration.candidate, readiness)))
                .Distinct()
                .OrderBy(capability => capability.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(capability => capability.Extension, StringComparer.OrdinalIgnoreCase)
                .ThenBy(capability => capability.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            candidates
                .SelectMany(candidate => candidate.Descriptor.Declaration.Runtimes.Select(declaration => new { candidate, declaration }))
                .Select(item => new RuntimeCapability(
                    item.declaration.Kind,
                    item.declaration.Version,
                    MapLocality(item.declaration.Locality),
                    GetBindingAvailability(item.candidate, readiness)))
                .Distinct()
                .OrderBy(capability => capability.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(capability => capability.Version, StringComparer.OrdinalIgnoreCase)
                .ThenBy(capability => capability.Locality)
                .ToArray(),
            candidates
                .SelectMany(GetCompleteUnitTestContractVersions)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(version => version, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static CapabilityAvailability GetBindingAvailability(
        SiemensBindingPackageCandidate candidate,
        IReadOnlyDictionary<SiemensBindingPackageCandidate, SiemensBindingReadinessResult> readiness)
    {
        if (!readiness.TryGetValue(candidate, out var result))
        {
            return CapabilityAvailability.Unavailable;
        }

        return result.State switch
        {
            SiemensBindingReadinessState.Usable => CapabilityAvailability.Usable,
            SiemensBindingReadinessState.Unavailable => CapabilityAvailability.Unavailable,
            SiemensBindingReadinessState.Error => CapabilityAvailability.Error,
            _ => CapabilityAvailability.Unavailable
        };
    }

    private static IEnumerable<string> GetCompleteUnitTestContractVersions(SiemensBindingPackageCandidate candidate)
    {
        return candidate.Descriptor.Declaration.UnitTestCommunicationContractVersions
            .Intersect(
                candidate.Descriptor.Declaration.UnitTestEventStreamContractVersions,
                StringComparer.OrdinalIgnoreCase);
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

    private static IReadOnlyList<SiemensBindingCandidateEvidence> CreateBindingCandidateEvidence(
        IEnumerable<SiemensBindingPackageCandidate> candidates,
        IReadOnlyDictionary<SiemensBindingPackageCandidate, SiemensBindingReadinessResult> readiness)
    {
        return candidates
            .Select(candidate => CreateBindingCandidateEvidence(candidate, GetReadinessEvidenceSummary(candidate, readiness)))
            .ToArray();
    }

    private static SiemensBindingCandidateEvidence CreateBindingCandidateEvidence(
        SiemensBindingPackageCandidate candidate,
        string? productEvidenceSummary)
    {
        return new SiemensBindingCandidateEvidence(
            candidate.Descriptor.Info.BindingKind,
            candidate.Descriptor.Info.BindingVersion,
            candidate.PackageRootPath,
            candidate.DescriptorPath,
            candidate.Descriptor.SchemaVersion,
            candidate.DescriptorFingerprint,
            candidate.Descriptor.EntryPoint,
            candidate.PackageFingerprint,
            candidate.Descriptor.Declaration,
            productEvidenceSummary);
    }
}

internal sealed record SiemensProfileRegistration(
    AccessProfileRegistrationResult Result,
    IReadOnlyList<SiemensBindingCandidateEvidence> BindingCandidates,
    SiemensBindingPackageDiscoveryResult Discovery,
    IReadOnlyList<Diagnostic> Diagnostics);
