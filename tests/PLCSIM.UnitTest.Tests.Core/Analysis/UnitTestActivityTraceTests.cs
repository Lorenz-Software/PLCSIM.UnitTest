using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Core.Analysis;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Analysis;

public sealed class UnitTestActivityTraceTests
{
    [Fact]
    public void Constructor_rejects_non_positive_capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnitTestActivityTrace(FakeTime.Create(), 0));
    }

    [Fact]
    public void Start_session_replaces_completed_trace_and_records_boundary()
    {
        var time = FakeTime.Create();
        var trace = new UnitTestActivityTrace(time, 4);

        var first = trace.StartSession(Session("first"));
        Assert.True(trace.TryEndSession(Session("first")));
        time.Advance(TimeSpan.FromSeconds(1));
        var second = trace.StartSession(Session("second"));

        Assert.True(first.IsCaptureActive);
        Assert.Equal("second", second.SessionId.Value);
        Assert.Equal(1, second.OldestSequence);
        Assert.Equal(1, second.NewestSequence);
        Assert.Equal(UnitTestSessionActivityKind.Started, SessionPayload(second.Entries[0]).Activity);
        Assert.Equal(0, second.EvictedEntryCount);
    }

    [Fact]
    public void Repeated_start_is_idempotent_but_new_session_replaces_active_trace()
    {
        var trace = new UnitTestActivityTrace(FakeTime.Create());
        trace.StartSession(Session());
        Assert.True(trace.TryAppend(Session(), Payload()));

        var repeated = trace.StartSession(Session());
        Assert.Equal(2, repeated.Entries.Count);
        Assert.False(trace.TryClear());

        var replacement = trace.StartSession(Session("other"));
        Assert.Equal("other", replacement.SessionId.Value);
        Assert.Single(replacement.Entries);
        Assert.Equal(1, replacement.NewestSequence);
    }

    [Fact]
    public void Append_assigns_monotonic_sequence_and_preserves_correlations()
    {
        var time = FakeTime.Create();
        var trace = new UnitTestActivityTrace(time);
        var sessionId = Session();
        trace.StartSession(sessionId);
        time.Advance(TimeSpan.FromMilliseconds(25));

        var appended = trace.TryAppend(
            sessionId,
            new SessionActivityPayload(UnitTestSessionActivityKind.Started),
            new CommunicationSessionId("communication"),
            new RunEpochId("epoch"),
            new LifecycleObservationId("lifecycle"),
            new EventStreamContextId("event-stream"),
            new OperationId("operation"));

        Assert.True(appended);
        var entry = trace.Current!.Entries[1];
        Assert.Equal(2, entry.Sequence);
        Assert.Equal(time.GetUtcNow(), entry.ObservedAt);
        Assert.Equal("communication", entry.CommunicationSessionId?.Value);
        Assert.Equal("epoch", entry.RunEpochId?.Value);
        Assert.Equal("lifecycle", entry.LifecycleObservationId?.Value);
        Assert.Equal("event-stream", entry.EventStreamContextId?.Value);
        Assert.Equal("operation", entry.OperationId?.Value);
    }

    [Fact]
    public void Bounded_trace_retains_newest_entries_and_reports_eviction()
    {
        var trace = new UnitTestActivityTrace(FakeTime.Create(), 3);
        var sessionId = Session();
        trace.StartSession(sessionId);

        Assert.True(trace.TryAppend(sessionId, Payload()));
        Assert.True(trace.TryAppend(sessionId, Payload()));
        Assert.True(trace.TryAppend(sessionId, Payload()));

        var snapshot = trace.Current!;
        Assert.Equal([2L, 3L, 4L], snapshot.Entries.Select(static entry => entry.Sequence));
        Assert.Equal(2, snapshot.OldestSequence);
        Assert.Equal(4, snapshot.NewestSequence);
        Assert.Equal(1, snapshot.EvictedEntryCount);
    }

    [Fact]
    public void Stale_or_inactive_session_activity_is_rejected()
    {
        var trace = new UnitTestActivityTrace(FakeTime.Create());
        var sessionId = Session();

        Assert.False(trace.TryAppend(sessionId, Payload()));
        trace.StartSession(sessionId);
        Assert.False(trace.TryAppend(Session("stale"), Payload()));
        Assert.False(trace.TryEndSession(Session("stale")));
        Assert.True(trace.TryEndSession(sessionId));
        Assert.False(trace.TryAppend(sessionId, Payload()));
    }

    [Fact]
    public void End_session_records_boundary_and_retains_completed_snapshot()
    {
        var time = FakeTime.Create();
        var trace = new UnitTestActivityTrace(time);
        var sessionId = Session();
        trace.StartSession(sessionId);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.True(trace.TryEndSession(sessionId));

        var snapshot = trace.Current!;
        Assert.False(snapshot.IsCaptureActive);
        Assert.Equal(time.GetUtcNow(), snapshot.EndedAt);
        Assert.Equal(UnitTestSessionActivityKind.Ended, SessionPayload(snapshot.Entries[^1]).Activity);
        Assert.Equal(2, snapshot.NewestSequence);
    }

    [Fact]
    public void Clear_removes_completed_trace()
    {
        var trace = new UnitTestActivityTrace(FakeTime.Create());
        trace.StartSession(Session());
        trace.TryEndSession(Session());

        Assert.True(trace.TryClear());
        Assert.Null(trace.Current);
    }

    [Fact]
    public void Concurrent_appends_are_serialized_without_sequence_gaps()
    {
        const int appendCount = 500;
        var trace = new UnitTestActivityTrace(FakeTime.Create(), appendCount + 1);
        var sessionId = Session();
        trace.StartSession(sessionId);

        Parallel.For(0, appendCount, _ => Assert.True(trace.TryAppend(sessionId, Payload())));

        var snapshot = trace.Current!;
        Assert.Equal(appendCount + 1, snapshot.Entries.Count);
        Assert.Equal(
            Enumerable.Range(1, appendCount + 1).Select(static value => (long)value),
            snapshot.Entries.Select(static entry => entry.Sequence));
        Assert.Equal(0, snapshot.EvictedEntryCount);
    }

    private static ApplicationSessionId Session(string value = "session") => new(value);

    private static SessionActivityPayload Payload() => new(UnitTestSessionActivityKind.Started);

    private static SessionActivityPayload SessionPayload(UnitTestActivityEntry entry) =>
        Assert.IsType<SessionActivityPayload>(entry.Payload);
}
