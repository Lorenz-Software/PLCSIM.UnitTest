using System.Text.Json.Nodes;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.AccessProfiles;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class ObservationStrategyTests
{
    [Theory]
    [InlineData("{}", "Polling")]
    [InlineData("{\"unitTest\":{}}", "Polling")]
    [InlineData("{\"unitTest\":{\"observationStrategy\":\"polling\"}}", "Polling")]
    [InlineData("{\"unitTest\":{\"observationStrategy\":\"subscription\"}}", "Subscription")]
    public void StrategyReachesBothObserversWithUnchangedDefaults(string json, string expected)
    {
        var service = Create();
        Assert.True(service.LoadConfig(json).IsSuccessful);
        var stream = EventStreamSettings.Parse(service.Configuration, ProjectOwnershipMode.ExternallyManaged);
        var control = RunControlSettings.Parse(service.Configuration, ProjectOwnershipMode.ExternallyManaged);
        Assert.Equal(expected, stream.Strategy.ToString());
        Assert.Equal(stream.Strategy, control.Strategy);
        Assert.Equal(TimeSpan.FromMilliseconds(10), stream.MonitoringInterval);
        Assert.Equal(stream.MonitoringInterval, control.Interval);
        Assert.Equal(TimeSpan.FromMilliseconds(1), stream.DrainInterval);
        Assert.Equal(32, stream.MaxEventsPerBatch);
        Assert.Equal(json, service.SerializeConfig().Value);
    }

    [Theory]
    [InlineData("polling")]
    [InlineData("subscription")]
    public void ExplicitIntervalsAndStrategySurviveRoundTrip(string strategy)
    {
        var service = Create();
        var values = new JsonObject { ["unitTest"] = new JsonObject {
            ["observationStrategy"] = strategy, ["monitoringIntervalMs"] = int.MaxValue,
            ["drainIntervalMs"] = 3, ["maxEventsPerBatch"] = 512 }, ["extension"] = "preserved" };
        Assert.True(service.LoadConfig(values.ToJsonString()).IsSuccessful);
        var copy = Create();
        Assert.True(copy.LoadConfig(service.SerializeConfig().Value!).IsSuccessful);
        Assert.True(JsonNode.DeepEquals(values, JsonNode.Parse(copy.SerializeConfig().Value!)));
        var stream = EventStreamSettings.Parse(copy.Configuration, ProjectOwnershipMode.ExternallyManaged);
        var control = RunControlSettings.Parse(copy.Configuration, ProjectOwnershipMode.ExternallyManaged);
        Assert.Equal(TimeSpan.FromMilliseconds(int.MaxValue), stream.MonitoringInterval);
        Assert.Equal(stream.MonitoringInterval, control.Interval);
        Assert.Equal(TimeSpan.FromMilliseconds(3), stream.DrainInterval);
        Assert.Equal(512, stream.MaxEventsPerBatch);
    }

    [Theory]
    [InlineData("\"plcCycle\"")]
    [InlineData("\"unknown\"")]
    [InlineData("\"Subscription\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("1")]
    public void UnsupportedStrategyIsActionableAndDoesNotReplacePreviousConfig(string value)
    {
        var service = Create();
        Assert.True(service.LoadConfig("{\"unitTest\":{\"observationStrategy\":\"subscription\"}}").IsSuccessful);
        var before = service.SerializeConfig().Value;
        var json = "{\"unitTest\":{\"observationStrategy\":" + value + "}}";
        var result = service.LoadConfig(json);
        Assert.False(result.IsSuccessful);
        Assert.Contains("'polling' or 'subscription'", Assert.Single(result.Diagnostics).Message);
        Assert.Equal(before, service.SerializeConfig().Value);
        var invalid = service.Configuration with { Values = new(json) };
        Assert.Throws<ArgumentException>(() => EventStreamSettings.Parse(invalid, ProjectOwnershipMode.ExternallyManaged));
        Assert.Throws<ArgumentException>(() => RunControlSettings.Parse(invalid, ProjectOwnershipMode.ExternallyManaged));
    }

    [Theory]
    [InlineData("monitoringIntervalMs", "0")]
    [InlineData("monitoringIntervalMs", "2147483648")]
    [InlineData("monitoringIntervalMs", "1.5")]
    [InlineData("drainIntervalMs", "0")]
    [InlineData("maxEventsPerBatch", "0")]
    public void SubscriptionRetainsPositiveIntegerValidation(string field, string value)
    {
        var service = Create();
        Assert.False(service.LoadConfig("{\"unitTest\":{\"observationStrategy\":\"subscription\",\""
            + field + "\":" + value + "}}").IsSuccessful);
    }

    [Fact]
    public void ProjectedStrategyCanBeChangedAndRemovedWithoutPersistingDefaults()
    {
        var service = Create();
        const string id = "beckhoff.unitTest.observationStrategy";
        var field = service.GetField(id, RuntimeProvisioningMode.UseExisting).Value!;
        Assert.Equal("polling", field.DefaultValue);
        Assert.Equal(new[] { "polling", "subscription" }, field.AllowedValues.Select(value => value.Value));
        Assert.True(service.AddFieldValue(id, "subscription").IsSuccessful);
        Assert.False(service.SetFieldValue(id, "plcCycle").IsSuccessful);
        Assert.Equal("subscription", service.GetStringFieldValue(id).Value);
        Assert.True(service.SetFieldValue(id, "polling").IsSuccessful);
        Assert.True(service.RemoveFieldValue(id).IsSuccessful);
        Assert.False(service.ContainsFieldValue(id));
        Assert.False(service.ContainsFieldValue("beckhoff.unitTest.monitoringIntervalMs"));
    }

    private static BeckhoffConfigurationService Create() => new("", ProjectOwnershipMode.ExternallyManaged, TimeProvider.System);
}
