using System.Text.Json;
using PLCSIM.UnitTest.Common.AccessProfiles;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed record RunControlSettings(string Root, TimeSpan Interval)
{
    internal ObservationStrategy Strategy { get; init; } = ObservationStrategy.Polling;

    internal static RunControlSettings Parse(AccessProfileConfig configuration, ProjectOwnershipMode ownership)
    {
        _ = RuntimeAccessConfiguration.Parse(configuration, ownership);
        using var document = JsonDocument.Parse(configuration.Values.Json);
        var root = "GVL_UnitTest.RunControl";
        var milliseconds = 10;
        var strategy = ObservationStrategy.Polling;
        if (document.RootElement.TryGetProperty("unitTest", out var unitTest))
        {
            strategy = ObservationStrategyConfiguration.Parse(unitTest);
            if (unitTest.TryGetProperty("runControl", out var symbol)) root = symbol.GetString()!;
            if (unitTest.TryGetProperty("monitoringIntervalMs", out var interval)) milliseconds = interval.GetInt32();
        }
        return new(root, TimeSpan.FromMilliseconds(milliseconds)) { Strategy = strategy };
    }
}

// Bound to one prepared technical Runtime context, never to a router default or a replacement context.
internal sealed record RuntimeDataEndpoint(string NetId, int Port, Func<bool> IsCurrent,
    Func<bool> IsAvailable, Action<TwinCAT.Ads.AdsErrorCode> RequestSystemCheck)
{
    internal Func<long> BindingGeneration { get; init; } = static () => 0;
}
