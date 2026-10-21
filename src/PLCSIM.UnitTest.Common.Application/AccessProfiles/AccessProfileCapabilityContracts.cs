namespace PLCSIM.UnitTest.Common.AccessProfiles;

/// <summary>
/// Resolved capabilities reported by an access profile during explicit registration.
/// UnitTest contract versions describe the PLC-side UnitTest event-stream/result
/// contract supported by the profile. Profile-specific transport or direct-access
/// profile versions remain profile-internal.
/// </summary>
public sealed record AccessProfileCapabilities(
    IReadOnlyList<AccessApiCapability> AccessApis,
    IReadOnlyList<ProjectSourceCapability> ProjectSources,
    IReadOnlyList<RuntimeCapability> Runtimes,
    IReadOnlyList<string> UnitTestContractVersions)
{
    public static AccessProfileCapabilities Empty { get; } = new(
        Array.Empty<AccessApiCapability>(),
        Array.Empty<ProjectSourceCapability>(),
        Array.Empty<RuntimeCapability>(),
        Array.Empty<string>());
}

/// <summary>
/// Profile-owned registration state after registration.
/// </summary>
public enum AccessProfileRegistrationState
{
    Registered,
    Unavailable,
    Error
}

/// <summary>
/// Availability of one resolved capability entry.
/// </summary>
public enum CapabilityAvailability
{
    Usable,
    Unavailable,
    Error
}

/// <summary>
/// Resolved access API capability, for example a TIA Openness or PLCSIM Advanced API variant.
/// </summary>
public sealed record AccessApiCapability(
    string Kind,
    string? ProductVersion,
    string AccessApiVersion,
    CapabilityAvailability Availability);

/// <summary>
/// Resolved PLC project source capability.
/// </summary>
public sealed record ProjectSourceCapability(
    string Kind,
    string Extension,
    string Version,
    CapabilityAvailability Availability);

/// <summary>
/// Resolved PLC runtime capability.
/// </summary>
public sealed record RuntimeCapability(
    string Kind,
    string Version,
    RuntimeLocality Locality,
    CapabilityAvailability Availability);

/// <summary>
/// Generic runtime locality exposed to the application.
/// </summary>
public enum RuntimeLocality
{
    Local,
    Remote
}
