using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;

namespace PLCSIM.UnitTest.Core.Workflow;

public static class RuntimeTargetResolver
{
    public static (RuntimeTargetRef? Target, string? Error) Resolve(
        RuntimeProvisioningMode mode, RuntimeTargetId? configuredId, RuntimeTargetDiscoveryResult? discovery)
    {
        if (!Enum.IsDefined(mode)) return (null, "Invalid runtime provisioning mode.");
        if (mode == RuntimeProvisioningMode.CreateOwned)
            return configuredId is null ? (null, null) : (null,
                "CreateOwned cannot select an existing runtime target. Use UseExisting or remove the existing-target selection; creation parameters remain profile-specific.");

        if (configuredId is null)
            return discovery?.DefaultTarget is { } defaultTarget ? (defaultTarget, null) : (null,
                "UseExisting requires an explicit runtime target or a profile-designated default.");

        var matches = discovery?.Candidates.Where(candidate => candidate.Id == configuredId).ToArray() ?? [];
        return matches.Length == 1 ? (matches[0], null) : (null,
            $"Configured runtime target '{configuredId.Value}' was not uniquely resolved. No fallback target will be used.");
    }
}
