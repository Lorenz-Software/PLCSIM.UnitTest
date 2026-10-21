using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed partial class EventStreamServiceTests
{
    [Theory]
    [InlineData(LogLevel.Information, true, 0)]
    [InlineData(LogLevel.Debug, false, 0)]
    [InlineData(LogLevel.Debug, true, 2)]
    public async Task ServiceHonorsRawOptionAndSeverity(LogLevel level, bool raw, int expected)
    {
        using var logger = new ProfileTestLoggerFactory(level);
        await using var test = new Fixture(loggerFactory: logger, logLevel: level, raw: raw);
        test.Access.Payload[0] = test.Access.Payload[0] with
        {
            OwnerScope = UnitTestOwnerScope.Framework, EventType = UnitTestEventType.Trigger,
            Name = "Startup.Reset"
        };
        await test.Prepare();
        await test.Start();
        await Until(() => test.Batches.Count == 1);
        await test.Service.StopMonitoringAsync(default);
        Assert.Equal(expected, logger.Entries.Count(entry => entry.Message.StartsWith("RawPlcEvent")));
        if (expected > 0) Assert.Contains(logger.Entries, entry => entry.Message.StartsWith("RawPlcEvent") && entry.Message.Contains("Startup.Reset"));
        Assert.Equal(new[] { "metadata", "payload", "metadata", "pending", "write" }, test.Access.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedEmptyPollsAndRepeatedFailuresDoNotSpam(bool failure)
    {
        using var logger = new ProfileTestLoggerFactory(LogLevel.Debug);
        var clock = new DelayClock();
        await using var test = new Fixture(clock, logger, LogLevel.Debug, raw: true);
        test.Access.Value = FakeEventStreamAccess.Observation(count: 0, producer: 0);
        if (failure) test.Access.MetadataError = new IOException("Controlled read failure");
        await test.Prepare();
        await test.Start();
        var timer = await clock.Next();
        var count = logger.Entries.Count;
        for (var n = 0; n < 10; n++)
        {
            timer.Fire();
            timer = await clock.Next();
        }
        Assert.Equal(count, logger.Entries.Count);
        Assert.Equal(failure ? 1 : 0, test.Diagnostics.Count);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.StartsWith("RawPlcEvent") || entry.Message.StartsWith("EventBatch accepted"));
        if (failure)
            Assert.Contains(logger.Entries, entry => entry.Message.Contains("EventStream diagnostic context") && entry.Message.Contains("192.0.2.10.1.1:852"));
        await test.Service.StopMonitoringAsync(default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RawLoggingPreservesWriteBeforeHandover(bool brokenLogger)
    {
        using var logger = new ProfileTestLoggerFactory(LogLevel.Debug) { ThrowOnLog = brokenLogger };
        await using var test = new Fixture(loggerFactory: logger, logLevel: LogLevel.Debug, raw: true);
        logger.OnLog = message => { if (message.StartsWith("RawPlcEvent")) test.Access.Calls.Enqueue("raw-log"); };
        test.Service.EventBatchReceivedEventHandler += (_, _) => test.Access.Calls.Enqueue("handover");
        await test.Prepare();
        await test.Start();
        await Until(() => test.Access.Calls.Contains("handover"));
        await test.Service.StopMonitoringAsync(default);
        Assert.Single(test.Batches);
        Assert.Equal(2, test.Batches.Single().Events.Count);
        var calls = test.Access.Calls.ToArray();
        Assert.True(Array.IndexOf(calls, "write") < Array.IndexOf(calls, "handover"));
        Assert.Equal(brokenLogger ? 0 : 2, calls.Count(call => call == "raw-log"));
        if (!brokenLogger)
        {
            Assert.True(Array.IndexOf(calls, "write") < Array.IndexOf(calls, "raw-log"));
            Assert.True(Array.LastIndexOf(calls, "raw-log") < Array.IndexOf(calls, "handover"));
        }
        Assert.Empty(test.Diagnostics);
    }

    [Theory]
    [InlineData("write-failed")]
    [InlineData("withheld")]
    [InlineData("inconsistent")]
    public async Task UnacceptedCapturesAreNotLoggedAsRawEvents(string failure)
    {
        using var logger = new ProfileTestLoggerFactory(LogLevel.Debug);
        var clock = new DelayClock();
        await using var test = new Fixture(clock, logger, LogLevel.Debug, raw: true);
        if (failure == "write-failed") test.Access.WriteError = new IOException("Rejected watermark");
        if (failure == "withheld") test.Access.PendingBeforeWrite = 9;
        if (failure == "inconsistent") test.Access.Payload = [];
        await test.Prepare();
        await test.Start();
        _ = await clock.Next();
        await test.Service.StopMonitoringAsync(default);
        Assert.Empty(test.Batches);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.StartsWith("RawPlcEvent"));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.StartsWith("EventBatch accepted"));
    }
}
