using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Logging;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;
using static PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit.FakeEventStreamAccess;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class ProfileLoggingTests
{
    [Theory]
    [InlineData(LogLevel.Trace, LogLevel.Information)]
    [InlineData(LogLevel.Debug, LogLevel.Information)]
    [InlineData(LogLevel.Information, LogLevel.Warning)]
    [InlineData(LogLevel.Warning, LogLevel.Warning)]
    [InlineData(LogLevel.Error, LogLevel.Error)]
    [InlineData(LogLevel.Critical, LogLevel.Critical)]
    [InlineData(LogLevel.None, LogLevel.None)]
    public void VendorLevelsFollowProfileVerbosityWithoutTransportDumps(LogLevel profileLevel, LogLevel vendorLevel)
    {
        using var host = new ProfileTestLoggerFactory(profileLevel);
        var factory = new AdsLoggerFactory(host);
        var logger = factory.CreateLogger("TwinCAT.Test");
        var formatted = 0;
        foreach (var level in Enum.GetValues<LogLevel>())
        {
            var expected = level != LogLevel.None && vendorLevel != LogLevel.None && level >= vendorLevel;
            Assert.Equal(expected, logger.IsEnabled(level));
            var before = formatted;
            logger.Log(level, new EventId(42), "payload", null, (state, _) => { formatted++; return state; });
            Assert.Equal(expected ? 1 : 0, formatted - before);
        }
        Assert.All(host.Entries, item =>
        {
            Assert.Equal("TwinCAT.Test", item.Category);
            Assert.Equal(42, item.Id.Id);
            Assert.StartsWith("ADS: ", item.Message);
        });
        factory.Dispose();
        Assert.False(host.Disposed);
        Assert.Throws<NotSupportedException>(() => factory.AddProvider(null!));
    }

    [Theory]
    [InlineData(LogLevel.Information, true, 0)]
    [InlineData(LogLevel.Debug, false, 1)]
    [InlineData(LogLevel.Debug, true, 2)]
    public void RawEventsAreSeparatelyGatedAndPreservePayload(LogLevel level, bool raw, int count)
    {
        using var host = new ProfileTestLoggerFactory(level);
        var pc = DateTimeOffset.Parse("2026-10-08T08:00:00Z");
        var entry = Entry(17) with
        {
            OwnerScope = UnitTestOwnerScope.Framework, EventType = UnitTestEventType.Trigger,
            Name = "UnitTest.Reset", ResourceName = "resource", Message = "line1\nline2\"\\end",
            State = UnitTestLifecycleState.Idle, Result = UnitTestResultState.NotEvaluated
        };
        new EventBatchLog(new ProfileLogger(host.CreateLogger("Profile")), new FixedClock(pc), raw)
            .Record(Batch(entry), "192.0.2.10.1.1", 852);
        Assert.Equal(count, host.Entries.Count);
        if (count != 2) return;
        var message = host.Entries.Last().Message;
        Assert.DoesNotContain("\n", message);
        using var json = JsonDocument.Parse(message["RawPlcEvent ".Length..]);
        var data = json.RootElement;
        Assert.Equal(pc, data.GetProperty("pcObservedAt").GetDateTimeOffset());
        Assert.Equal(entry.Timestamp, data.GetProperty("plcEventTimestamp").GetDateTimeOffset());
        Assert.Equal("communication", data.GetProperty("communicationSessionId").GetString());
        Assert.Equal("192.0.2.10.1.1", data.GetProperty("netId").GetString());
        Assert.Equal(852, data.GetProperty("plcPort").GetInt32());
        Assert.Equal(17u, data.GetProperty("sequenceId").GetUInt32());
        Assert.Equal("Framework", data.GetProperty("ownerScope").GetString());
        Assert.Equal("Trigger", data.GetProperty("eventType").GetString());
        Assert.Equal("Information", data.GetProperty("severity").GetString());
        Assert.Equal(entry.Name, data.GetProperty("name").GetString());
        Assert.Equal(entry.ResourceName, data.GetProperty("resourceName").GetString());
        Assert.Equal("Idle", data.GetProperty("state").GetString());
        Assert.Equal("NotEvaluated", data.GetProperty("result").GetString());
        Assert.Equal(entry.Message, data.GetProperty("message").GetString());
        Assert.False(data.TryGetProperty("runEpochId", out _));
    }

    [Fact]
    public void EmptyObservationsProduceNoBatchLog()
    {
        using var host = new ProfileTestLoggerFactory(LogLevel.Debug);
        new EventBatchLog(host.CreateLogger("Profile"), TimeProvider.System, true)
            .Record(new(new("communication"), [], 0, 0, 0, true, EventBufferReadState.Empty), "local", 851);
        Assert.Empty(host.Entries);
    }

    [Theory]
    [InlineData(LogLevel.Information, true)]
    [InlineData(LogLevel.Debug, false)]
    public void DisabledRawLoggingDoesNotReadClock(LogLevel level, bool raw)
    {
        using var host = new ProfileTestLoggerFactory(level);
        var clock = new CountingClock();
        new EventBatchLog(host.CreateLogger("Profile"), clock, raw).Record(Batch(Entry(1)), "local", 851);
        Assert.Equal(0, clock.Reads);
    }

    [Fact]
    public void LogFailuresAreContained()
    {
        using var host = new ProfileTestLoggerFactory(LogLevel.Debug) { ThrowOnLog = true };
        var logger = new ProfileLogger(host.CreateLogger("Profile"));
        logger.LogError("error");
        new EventBatchLog(logger, TimeProvider.System, true).Record(Batch(Entry(1)), "local", 851);
        host.ThrowOnEnabled = true;
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        logger.LogDebug("disabled");
    }

    private static UnitTestEventBatch Batch(UnitTestEventEntry entry) =>
        new(new("communication"), [entry], entry.SequenceId, 0, 0, false, EventBufferReadState.Valid);
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    private sealed class CountingClock : TimeProvider
    {
        internal int Reads;
        public override DateTimeOffset GetUtcNow() { Reads++; return DateTimeOffset.UnixEpoch; }
    }
}

internal sealed class ProfileTestLoggerFactory(LogLevel minimum) : ILoggerFactory
{
    internal readonly ConcurrentQueue<(string Category, LogLevel Level, EventId Id, string Message)> Entries = new();
    internal bool Disposed, ThrowOnLog, ThrowOnEnabled;
    internal Action<string>? OnLog;
    public ILogger CreateLogger(string categoryName) => new TestLogger(this, categoryName, minimum);
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
    public void Dispose() => Disposed = true;

    private sealed class TestLogger(ProfileTestLoggerFactory owner, string category, LogLevel minimum) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => owner.ThrowOnEnabled ? throw new IOException("Broken log provider")
            : level >= minimum && minimum != LogLevel.None && level != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (owner.ThrowOnLog) throw new IOException("Broken log file");
            var message = formatter(state, exception);
            owner.OnLog?.Invoke(message);
            owner.Entries.Enqueue((category, level, id, message));
        }
    }
}
