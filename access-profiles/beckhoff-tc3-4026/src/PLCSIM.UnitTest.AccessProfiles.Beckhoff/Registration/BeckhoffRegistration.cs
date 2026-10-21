using PLCSIM.UnitTest.Common.AccessProfiles;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Registration;

internal static class BeckhoffRegistration
{
    internal const string SolutionKind = "twincat.solution";
    internal const string ArchiveKind = "twincat.project-archive";

    internal static AccessProfileRegistrationResult CreateResult(BeckhoffEnvironment environment)
    {
        var apis = new List<AccessApiCapability>();
        foreach (var version in environment.Engineering.Select(item => item.ProductVersion).Distinct())
            apis.Add(new("twincat.xae", version, "4026", environment.XaeShellPath is null
                ? CapabilityAvailability.Unavailable : CapabilityAvailability.Usable));
        if (environment.Engineering.Count == 0)
            apis.Add(new("twincat.xae", null, "4026", CapabilityAvailability.Unavailable));

        foreach (var version in environment.UsermodeRuntime.Select(item => item.ProductVersion).Distinct())
            apis.Add(new("twincat.usermode", version, "4026", CapabilityAvailability.Usable));
        if (environment.UsermodeRuntime.Count == 0)
            apis.Add(new("twincat.usermode", null, "4026", CapabilityAvailability.Unavailable));

        var prerequisitesPresent = environment.XaeShellPath is not null
            && environment.Engineering.Count != 0 && environment.UsermodeRuntime.Count != 0;
        var availability = prerequisitesPresent ? CapabilityAvailability.Usable : CapabilityAvailability.Unavailable;
        var capabilities = new AccessProfileCapabilities(apis,
            [new(SolutionKind, ".sln", "4026", availability),
             new(ArchiveKind, ".tszip", "4026", availability)],
            [new("twincat.usermode", "4026", RuntimeLocality.Local, availability)], prerequisitesPresent ? ["1.0.0"] : []);
        return new(prerequisitesPresent ? AccessProfileRegistrationState.Registered : AccessProfileRegistrationState.Unavailable,
            capabilities, prerequisitesPresent
            ? "TwinCAT 4026 access profile registration completed. Runtime availability and licensing are checked during access."
            : "Required TwinCAT 4026 XAE shell, Visual Studio interop, engineering or Usermode components are missing.");
    }
}
