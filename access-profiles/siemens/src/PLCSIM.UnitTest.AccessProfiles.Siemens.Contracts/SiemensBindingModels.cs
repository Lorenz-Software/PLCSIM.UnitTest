namespace PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

/// <summary>
/// Stable self-information of one Siemens-internal technology binding package.
/// </summary>
public sealed record SiemensBindingInfo(
    string BindingKind,
    string BindingVersion,
    string DisplayName);

/// <summary>
/// Siemens-internal declaration reported by a technology binding package before
/// the Siemens profile shell maps binding facts to shared capability DTOs.
/// </summary>
public sealed record SiemensBindingDeclaration(
    IReadOnlyList<SiemensAccessApiDeclaration> AccessApis,
    IReadOnlyList<SiemensProjectSourceDeclaration> ProjectSources,
    IReadOnlyList<SiemensRuntimeDeclaration> Runtimes,
    IReadOnlyList<string> UnitTestCommunicationContractVersions,
    IReadOnlyList<string> UnitTestEventStreamContractVersions)
{
    public static SiemensBindingDeclaration Empty { get; } = new(
        Array.Empty<SiemensAccessApiDeclaration>(),
        Array.Empty<SiemensProjectSourceDeclaration>(),
        Array.Empty<SiemensRuntimeDeclaration>(),
        Array.Empty<string>(),
        Array.Empty<string>());
}

/// <summary>
/// Context passed by the Siemens profile shell when it asks a concrete binding
/// package to resolve its own environment/readiness state.
/// </summary>
public sealed record SiemensBindingReadinessRequest(
    string PackageRootPath,
    string EntryAssemblyPath,
    SiemensBindingDeclaration Declaration);

/// <summary>
/// Binding-owned readiness result. The Siemens profile shell maps this neutral
/// result to shared application capability DTOs without interpreting concrete
/// vendor/API details.
/// </summary>
public sealed record SiemensBindingReadinessResult(
    SiemensBindingReadinessState State,
    string EvidenceSummary,
    IReadOnlyList<SiemensBindingReadinessDiagnostic> Diagnostics)
{
    public bool IsUsable => State == SiemensBindingReadinessState.Usable;

    public static SiemensBindingReadinessResult Usable(string evidenceSummary)
    {
        return new SiemensBindingReadinessResult(
            SiemensBindingReadinessState.Usable,
            evidenceSummary,
            Array.Empty<SiemensBindingReadinessDiagnostic>());
    }

    public static SiemensBindingReadinessResult Unavailable(string evidenceSummary)
    {
        return new SiemensBindingReadinessResult(
            SiemensBindingReadinessState.Unavailable,
            evidenceSummary,
            Array.Empty<SiemensBindingReadinessDiagnostic>());
    }

    public static SiemensBindingReadinessResult Error(
        string code,
        string message,
        string? technicalDetail = null)
    {
        return new SiemensBindingReadinessResult(
            SiemensBindingReadinessState.Error,
            message,
            new[]
            {
                new SiemensBindingReadinessDiagnostic(
                    code,
                    message,
                    technicalDetail)
            });
    }
}

public enum SiemensBindingReadinessState
{
    Usable,
    Unavailable,
    Error
}

public sealed record SiemensBindingReadinessDiagnostic(
    string Code,
    string Message,
    string? TechnicalDetail);

/// <summary>
/// Declares one Siemens access API/product combination supported by a binding.
/// Product version and access API version are deliberately separate.
/// </summary>
public sealed record SiemensAccessApiDeclaration(
    string Kind,
    string ProductVersion,
    string AccessApiVersion);

/// <summary>
/// Declares one Siemens project source form supported by a binding.
/// </summary>
public sealed record SiemensProjectSourceDeclaration(
    string Kind,
    string Extension,
    string Version,
    TiaProjectOpenMode OpenMode = TiaProjectOpenMode.DirectProject);

/// <summary>
/// Declares one Siemens runtime form supported by a binding.
/// </summary>
public sealed record SiemensRuntimeDeclaration(
    string Kind,
    string Version,
    SiemensRuntimeLocality Locality,
    IReadOnlyList<string> CompatibleProjectSourceKinds);

public enum SiemensRuntimeLocality
{
    Local,
    Remote
}

/// <summary>
/// Entry point used by the Siemens profile shell to instantiate a Siemens-internal
/// technology binding package. This is not the generic access-profile manifest.
/// </summary>
public sealed record SiemensBindingEntryPoint(
    string Assembly,
    string TypeName);

/// <summary>
/// Minimal package descriptor for Siemens-internal technology binding discovery.
/// </summary>
public sealed record SiemensBindingPackageDescriptor(
    int SchemaVersion,
    SiemensBindingInfo Info,
    SiemensBindingEntryPoint EntryPoint,
    SiemensBindingDeclaration Declaration,
    SiemensBindingSetupDeclaration? Setup = null);

/// <summary>
/// Optional setup capabilities exposed by a Siemens-internal binding package.
/// </summary>
public sealed record SiemensBindingSetupDeclaration(
    bool TiaOpennessAuthorizationProbe);
