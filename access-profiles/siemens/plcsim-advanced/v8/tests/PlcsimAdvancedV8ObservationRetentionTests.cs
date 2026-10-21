using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8;

public sealed class PlcsimAdvancedV8ObservationRetentionTests
{
    [Fact]
    public void Missing_worker_response_retains_received_metadata_only_for_the_same_session()
    {
        var mapper = new PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.PlcsimAdvancedV8WorkerMessageMapper("runtime");
        mapper.CreateEventStreamMonitoringState(new Dictionary<string, string>
        {
            ["communicationSessionId"] = "session",
            ["isMonitoring"] = "true",
            ["lastReadState"] = "Valid",
            ["batchReleaseState"] = "Completed",
            ["producerSequenceId"] = "412",
            ["deletedSequenceId"] = "256",
            ["lostEventCount"] = "3",
            ["bufferFull"] = "true",
            ["bufferObservedEmpty"] = "false"
        }, "session");
        var failure = mapper.CreateUnavailableEventStreamMonitoringState("session");
        Assert.Equal(EventBufferReadState.SessionInvalid, failure.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Completed, failure.BatchReleaseState);
        Assert.Equal(412u, failure.ProducerSequenceId);
        Assert.Equal(256u, failure.DeletedSequenceId);
        Assert.True(failure.BufferFull);
        Assert.Equal((ushort)3, failure.LostEventCount);
        var other = mapper.CreateUnavailableEventStreamMonitoringState("other");
        Assert.Equal(0u, other.ProducerSequenceId);
        Assert.False(other.BufferObservedEmpty);
        Assert.Equal(EventStreamBatchReleaseState.Idle, other.BatchReleaseState);
    }

    [Fact]
    public void Pending_attempt_retains_metadata_and_completed_batch_state_without_payload()
    {
        var fixture = new Fixture();
        var completed = fixture.Capture();
        Assert.NotNull(completed.CapturedBatch);
        var pending = fixture.Retained();
        Assert.Equal("Valid", pending.LastReadState);
        Assert.Equal("Completed", pending.BatchReleaseState);
        Assert.Equal(412u, pending.ProducerSequenceId);
        Assert.Equal(256u, pending.DeletedSequenceId);
        Assert.False(pending.BufferObservedEmpty);
        Assert.Null(pending.CapturedBatch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_attempt_preserves_latest_metadata_without_retracting_previous_handover(bool postRead)
    {
        var fixture = new Fixture();
        fixture.Capture();
        fixture.Metadata(postRead ? 450u : 430u, 288, 4, false);
        var failure = fixture.Failure("ReadFailed");
        Assert.False(failure.Succeeded);
        Assert.Equal("SessionInvalid", failure.LastReadState);
        Assert.Equal("Completed", failure.BatchReleaseState);
        Assert.Equal(postRead ? 450u : 430u, failure.ProducerSequenceId);
        Assert.Equal(288u, failure.DeletedSequenceId);
        Assert.Equal((ushort)4, failure.LostEventCount);
        Assert.Null(failure.CapturedBatch);
    }

    [Fact]
    public void Captured_but_withheld_batch_is_idle_and_uses_post_read_metadata()
    {
        var fixture = new Fixture();
        var result = fixture.Capture(publish: false);
        Assert.Equal("Valid", result.LastReadState);
        Assert.Equal("Idle", result.BatchReleaseState);
        Assert.Equal(412u, result.ProducerSequenceId);
        Assert.Null(result.CapturedBatch);
    }

    [Fact]
    public void Batch_metadata_matches_monitoring_metadata_not_pre_read_counters()
    {
        var result = new Fixture().Capture();
        Assert.Equal(result.ProducerSequenceId, result.CapturedBatch!.ProducerSequenceId);
        Assert.Equal(result.DeletedSequenceId, result.CapturedBatch.DeletedSequenceId);
        Assert.Equal(result.LostEventCount, result.CapturedBatch.LostEventCount);
    }

    [Fact]
    public void Stop_and_same_session_reconnect_retain_but_other_session_and_teardown_initialize()
    {
        var fixture = new Fixture();
        fixture.Capture();
        Assert.True((bool)fixture.Invoke("TryResetCommunicationState", true));
        Assert.Equal(412u, fixture.Retained().ProducerSequenceId);
        Assert.False(fixture.Retained().IsMonitoring);
        var other = fixture.Retained("other");
        Assert.Equal("NotObserved", other.LastReadState);
        Assert.Equal(0u, other.ProducerSequenceId);
        Assert.False(other.BufferObservedEmpty);
        Assert.True((bool)fixture.Invoke("TryResetCommunicationState", false));
        Assert.Equal("NotObserved", fixture.Retained().LastReadState);
    }

    [Fact]
    public void Actual_zero_metadata_replaces_previous_values_without_assuming_a_read_classification()
    {
        var fixture = new Fixture();
        fixture.Capture();
        fixture.Metadata(0, 0, 0, true);
        var result = fixture.Retained();
        Assert.True(result.BufferObservedEmpty);
        Assert.Equal(0u, result.ProducerSequenceId);
        Assert.Equal(0u, result.DeletedSequenceId);
        Assert.Equal("Valid", result.LastReadState);
    }

    [Fact]
    public void Failure_for_another_session_does_not_borrow_metadata()
    {
        var previous = new PlcsimEventStreamMonitoringState("old", true,
            EventBufferReadState.Valid, EventStreamBatchReleaseState.Completed, false, 7, true, 412, 256);
        var failure = EventStreamMonitoringResult.CreateFailureResult("Rejected", "new", "Mismatch", "Mismatch", previous);
        Assert.Equal(0u, failure.ProducerSequenceId);
        Assert.Equal(0u, failure.DeletedSequenceId);
        Assert.Equal("Idle", failure.BatchReleaseState);
        Assert.False(failure.BufferObservedEmpty);
    }

    // Invoke production observation helpers without loading a Siemens API or starting a PLC.
    private sealed class Fixture
    {
        private readonly object context;
        private readonly Type contextType;
        private readonly Type metadataType;

        public Fixture()
        {
            var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker.exe"));
            var program = assembly.GetType("PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker.Program", true)!;
            contextType = program.GetNestedType("RuntimeApiContext", BindingFlags.NonPublic)!;
            metadataType = program.GetNestedType("EventBufferMetadataReadResult", BindingFlags.NonPublic)!;
            var eventType = program.GetNestedType("RuntimeStateEvent", BindingFlags.NonPublic)!;
            var publisher = Expression.Lambda(typeof(Action<>).MakeGenericType(eventType),
                Expression.Empty(), Expression.Parameter(eventType)).Compile();
            context = Activator.CreateInstance(contextType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { typeof(FakeManager), publisher, NullLogger.Instance }, null)!;
            Set("preparedCommunicationSessionId", "session");
            Set("eventStreamMonitoringActive", true);
        }

        private void Set(string name, object value) =>
            contextType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, value);

        public object Invoke(string method, params object[] arguments) =>
            contextType.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context, arguments)!;

        public void Metadata(uint producer, uint deleted, ushort lost, bool empty)
        {
            var metadata = metadataType.GetMethod("Success")!.Invoke(null,
                new object[] { Fifo(empty), new PlcsimEventBufferStatusSnapshot(producer, deleted, lost) })!;
            Invoke("RecordEventBufferMetadata", metadata);
        }

        public EventStreamMonitoringResult Retained(string session = "session") =>
            (EventStreamMonitoringResult)Invoke("CreateRetainedEventStreamResult", session, false);

        public EventStreamMonitoringResult Failure(string code)
        {
            var gate = contextType.GetField("eventStreamObservationLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
            lock (gate)
                return (EventStreamMonitoringResult)Invoke("CreateFailedEventStreamResult", "Failed", "session", code, "Injected");
        }

        public EventStreamMonitoringResult Capture(bool publish = true)
        {
            var fifo = Fifo(false);
            var status = new PlcsimEventBufferStatusSnapshot(300, 256, 0);
            var classification = new EventBufferPollingSnapshotClassifier("1.0.0", "test").Classify(
                fifo, status, new Dictionary<ushort, uint> { [1] = 257 }, fifo, status, DateTimeOffset.UtcNow);
            Metadata(412, 256, 3, false);
            var observation = (PlcsimEventStreamMonitoringState)Invoke("GetEventStreamObservation", "session");
            var entries = new[] { new EventBufferCapturedEntry(1, 257, 0, 10, 10, 2, "Run", "", 0, 0, "") };
            var batch = publish ? EventStreamCapturedBatch.FromClassification("session", classification, entries, observation) : null;
            return (EventStreamMonitoringResult)Invoke("CreateObservedEventStreamResult",
                "session", true, classification, batch!, "", "");
        }

        private static PlcsimEventBufferFifoControlSnapshot Fifo(bool empty) =>
            new(1, empty ? (ushort)1 : (ushort)2, 255, empty, false, false, false);
    }

    public static class FakeManager { }
}
