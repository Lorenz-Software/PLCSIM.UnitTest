using System.Text.Json;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal enum ObservationStrategy
{
    Polling,
    Subscription
}

internal static class ObservationStrategyConfiguration
{
    internal static bool TryParse(string? value, out ObservationStrategy strategy)
    {
        strategy = ObservationStrategy.Polling;
        if (value == "polling") return true;
        if (value != "subscription") return false;
        strategy = ObservationStrategy.Subscription;
        return true;
    }

    internal static ObservationStrategy Parse(JsonElement unitTest)
    {
        if (!unitTest.TryGetProperty("observationStrategy", out var value)) return ObservationStrategy.Polling;
        if (value.ValueKind == JsonValueKind.String && TryParse(value.GetString(), out var strategy)) return strategy;
        throw new ArgumentException("Beckhoff unitTest.observationStrategy must be 'polling' or 'subscription'.");
    }
}
