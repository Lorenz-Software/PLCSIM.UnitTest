using PLCSIM.UnitTest.Common.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

/// <summary>
/// Base contract implemented by Siemens-internal technology binding packages.
/// </summary>
public interface ISiemensTechnologyBinding
{
    void Initialize(SiemensBindingContext context);

    SiemensBindingInfo Info { get; }

    SiemensBindingDeclaration Declaration { get; }

    SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request);
}

/// <summary>
/// Profile-owned host context supplied once to a loaded Siemens technology binding.
/// </summary>
public sealed record SiemensBindingContext(
    string AccessProfileId,
    OperationalLoggingOptions Logging);

/// <summary>
/// Boundary for TIA Openness engineering bindings.
/// </summary>
public interface ITiaOpennessEngineeringBinding : ISiemensTechnologyBinding
{
    Task<TiaEngineeringSessionOpenResult> OpenProjectAsync(
        TiaProjectOpenRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional setup boundary implemented by real TIA Openness bindings that can
/// trigger and verify their own interactive Siemens authorization flow.
/// </summary>
public interface ITiaOpennessAuthorizationProbeBinding
{
    Task<TiaAuthorizationProbeResult> ProbeAuthorizationAsync(
        TiaAuthorizationProbeRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Boundary for PLCSIM Advanced runtime bindings.
/// </summary>
public interface IPlcsimAdvancedRuntimeBinding : ISiemensTechnologyBinding
{
    Task<PlcsimRuntimeSessionOpenResult> OpenRuntimeSessionAsync(
        PlcsimRuntimeSessionOpenRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Boundary for PLCSIM Advanced UnitTest communication bindings.
/// </summary>
public interface IPlcsimUnitTestCommunicationBinding : ISiemensTechnologyBinding
{
    Task<PlcsimCommunicationPrepareResult> PrepareCommunicationAsync(
        PlcsimCommunicationPrepareRequest request,
        CancellationToken cancellationToken);
}
