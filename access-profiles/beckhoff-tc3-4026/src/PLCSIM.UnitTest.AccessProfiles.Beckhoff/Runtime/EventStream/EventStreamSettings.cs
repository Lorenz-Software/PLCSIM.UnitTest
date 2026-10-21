using System.Text.Json;
using PLCSIM.UnitTest.Common.AccessProfiles;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed record EventStreamSettings(string EventRoot, string RunControlRoot,
    TimeSpan MonitoringInterval, TimeSpan DrainInterval, int MaxEventsPerBatch)
{
    internal ObservationStrategy Strategy { get; init; } = ObservationStrategy.Polling;

    internal static EventStreamSettings Parse(AccessProfileConfig configuration, ProjectOwnershipMode ownership)
    {
        _ = RuntimeAccessConfiguration.Parse(configuration, ownership);
        using var document = JsonDocument.Parse(configuration.Values.Json);
        var settings = new EventStreamSettings("GVL_UnitTest.EventStream", "GVL_UnitTest.RunControl",
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(1), 32);
        if (!document.RootElement.TryGetProperty("unitTest", out var unitTest)) return settings;
        return settings with
        {
            Strategy = ObservationStrategyConfiguration.Parse(unitTest),
            EventRoot = unitTest.TryGetProperty("eventBuffer", out var buffer) ? buffer.GetString()! : settings.EventRoot,
            RunControlRoot = unitTest.TryGetProperty("runControl", out var control) ? control.GetString()! : settings.RunControlRoot,
            MonitoringInterval = unitTest.TryGetProperty("monitoringIntervalMs", out var monitoring)
                ? TimeSpan.FromMilliseconds(monitoring.GetInt32()) : settings.MonitoringInterval,
            DrainInterval = unitTest.TryGetProperty("drainIntervalMs", out var drain)
                ? TimeSpan.FromMilliseconds(drain.GetInt32()) : settings.DrainInterval,
            MaxEventsPerBatch = unitTest.TryGetProperty("maxEventsPerBatch", out var count) ? count.GetInt32() : settings.MaxEventsPerBatch
        };
    }

    internal void Validate()
    {
        if (!Enum.IsDefined(Strategy) || string.IsNullOrWhiteSpace(EventRoot) || string.IsNullOrWhiteSpace(RunControlRoot)
            || MonitoringInterval <= TimeSpan.Zero || MonitoringInterval.TotalMilliseconds > int.MaxValue
            || DrainInterval <= TimeSpan.Zero || DrainInterval.TotalMilliseconds > int.MaxValue || MaxEventsPerBatch <= 0)
            throw new ArgumentException("EventStream roots, positive intervals and a positive batch limit are required.");
    }
}
