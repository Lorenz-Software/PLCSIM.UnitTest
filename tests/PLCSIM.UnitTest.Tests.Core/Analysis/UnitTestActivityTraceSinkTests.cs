using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Core.Analysis;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Analysis;

public sealed class UnitTestActivityTraceSinkTests
{
    [Fact]
    public void Record_without_active_session_is_ignored()
    {
        var (sink, trace, _) = CreateSink();

        Assert.False(sink.TryRecord(Payload()));
        Assert.Null(trace.Current);
    }

    [Fact]
    public void Session_context_supplies_shared_correlations()
    {
        var (sink, trace, context) = CreateSink();
        var sessionId = Session();
        var communicationSessionId = new CommunicationSessionId("communication");
        var lifecycleObservationId = new LifecycleObservationId("lifecycle");
        var eventStreamContextId = new EventStreamContextId("event-stream");

        Assert.True(sink.TryStartSession(sessionId));
        Assert.True(context.TrySetCommunicationSession(sessionId, communicationSessionId));
        Assert.True(context.TrySetLifecycleObservation(sessionId, lifecycleObservationId));
        Assert.True(context.TrySetEventStreamContext(sessionId, eventStreamContextId));
        Assert.True(sink.TryRecord(Payload()));

        var entry = trace.Current!.Entries[^1];
        Assert.Equal(communicationSessionId, entry.CommunicationSessionId);
        Assert.Equal(lifecycleObservationId, entry.LifecycleObservationId);
        Assert.Equal(eventStreamContextId, entry.EventStreamContextId);
        Assert.Null(entry.RunEpochId);
        Assert.Null(entry.OperationId);
    }

    [Fact]
    public void Source_correlations_override_shared_context()
    {
        var (sink, trace, context) = CreateSink();
        var sessionId = Session();
        sink.TryStartSession(sessionId);
        context.TrySetCommunicationSession(sessionId, new CommunicationSessionId("shared-communication"));
        context.TrySetLifecycleObservation(sessionId, new LifecycleObservationId("shared-lifecycle"));
        context.TrySetEventStreamContext(sessionId, new EventStreamContextId("shared-stream"));

        var correlation = new UnitTestActivityCorrelation(
            new CommunicationSessionId("source-communication"),
            new RunEpochId("epoch"),
            new LifecycleObservationId("source-lifecycle"),
            new EventStreamContextId("source-stream"),
            new OperationId("operation"));

        Assert.True(sink.TryRecord(Payload(), correlation));

        var entry = trace.Current!.Entries[^1];
        Assert.Equal("source-communication", entry.CommunicationSessionId?.Value);
        Assert.Equal("epoch", entry.RunEpochId?.Value);
        Assert.Equal("source-lifecycle", entry.LifecycleObservationId?.Value);
        Assert.Equal("source-stream", entry.EventStreamContextId?.Value);
        Assert.Equal("operation", entry.OperationId?.Value);
    }

    [Fact]
    public void Context_rejects_updates_from_stale_session()
    {
        var (_, _, context) = CreateSink();
        var activeSession = Session();
        context.StartSession(activeSession);

        Assert.False(context.TrySetCommunicationSession(Session("stale"), new CommunicationSessionId("ignored")));
        Assert.False(context.TrySetLifecycleObservation(Session("stale"), new LifecycleObservationId("ignored")));
        Assert.False(context.TrySetEventStreamContext(Session("stale"), new EventStreamContextId("ignored")));

        var current = context.Current;
        Assert.Equal(activeSession, current.ApplicationSessionId);
        Assert.Null(current.CommunicationSessionId);
        Assert.Null(current.LifecycleObservationId);
        Assert.Null(current.EventStreamContextId);
    }

    [Fact]
    public void End_session_clears_context_and_rejects_late_activity()
    {
        var (sink, trace, context) = CreateSink();
        var sessionId = Session();
        sink.TryStartSession(sessionId);
        context.TrySetLifecycleObservation(sessionId, new LifecycleObservationId("lifecycle"));

        Assert.True(sink.TryEndSession(sessionId));
        Assert.Null(context.Current.ApplicationSessionId);
        Assert.False(sink.TryRecord(Payload()));
        Assert.False(trace.Current!.IsCaptureActive);
    }

    [Fact]
    public void Invalid_input_is_best_effort_and_does_not_escape()
    {
        var (sink, trace, context) = CreateSink();

        Assert.False(sink.TryStartSession(default));
        Assert.False(sink.TryRecord(null!));
        Assert.Null(context.Current.ApplicationSessionId);
        Assert.Null(trace.Current);
    }

    private static (
        UnitTestActivityTraceSink Sink,
        UnitTestActivityTrace Trace,
        UnitTestActivityTraceContext Context) CreateSink()
    {
        var time = FakeTime.Create();
        var trace = new UnitTestActivityTrace(time);
        var context = new UnitTestActivityTraceContext();
        var sink = new UnitTestActivityTraceSink(
            TestCoreServiceContext.Create(timeProvider: time),
            trace,
            context);
        return (sink, trace, context);
    }

    private static ApplicationSessionId Session(string value = "session") => new(value);

    private static SessionActivityPayload Payload() => new(UnitTestSessionActivityKind.Started);
}
