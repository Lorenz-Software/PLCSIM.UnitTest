using System.Text.Json;
using PLCSIM.UnitTest.Common.AccessProfiles;
using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed record RuntimeAccessConfiguration(string? NetId, int? PlcPort)
{
    public static RuntimeAccessConfiguration Parse(AccessProfileConfig configuration, ProjectOwnershipMode ownership)
    {
        if (configuration.ProfileId != BeckhoffAccessProfilePlugin.ProfileInfo.ProfileId
            || configuration.ProfileVersion is not null && configuration.ProfileVersion != BeckhoffAccessProfilePlugin.ProfileInfo.Version)
            throw new ArgumentException("Configuration belongs to another profile or version.");
        var validator = new BeckhoffConfigurationService(configuration.ProjectSource, ownership, TimeProvider.System);
        var result = validator.LoadConfig(configuration.Values.Json);
        if (!result.IsSuccessful)
            throw new ArgumentException(string.Join(" ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        using var document = JsonDocument.Parse(configuration.Values.Json);
        string? netId = null;
        int? port = ownership == ProjectOwnershipMode.ExternallyManaged ? 851 : null;
        if (document.RootElement.TryGetProperty("runtimeRef", out var runtime))
        {
            if (runtime.TryGetProperty("id", out var id)) netId = NormalizeNetId(id.GetString()!);
            if (runtime.TryGetProperty("plcPort", out var plcPort)) port = plcPort.GetInt32();
        }
        return new(netId, port);
    }

    public static string NormalizeNetId(string value)
    {
        if (!AmsNetId.TryParse(value, out var id) || id.IsEmpty || id.IsBroadcast || id.IsLoopback)
            throw new ArgumentException("Select a concrete Runtime AMS Net ID, not a router alias.");
        return id.ToString();
    }
}
