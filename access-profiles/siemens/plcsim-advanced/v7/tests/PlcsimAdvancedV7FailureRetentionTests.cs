using System.Collections.Concurrent;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;
using static PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V7.PlcsimAdvancedV7RealRuntimeTests;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V7;

public sealed class PlcsimAdvancedV7FailureRetentionTests
{
    [Theory]
    [InlineData("worker-revalidation-terminal-entry")]
    [InlineData("worker-revalidation-terminal-post-status")]
    public async Task Failure_after_metadata_read_returns_real_counters_without_a_batch(string behavior)
    {
        var result = await StartValidationEventStreamMonitoringAsync(behavior);
        try
        {
            Assert.False(result.Monitoring.Succeeded);
            Assert.Equal(EventBufferReadState.SessionInvalid, result.Monitoring.State.LastReadState);
            Assert.Equal(EventStreamBatchReleaseState.Idle, result.Monitoring.State.BatchReleaseState);
            Assert.Equal(1u, result.Monitoring.State.ProducerSequenceId);
            Assert.Equal(0u, result.Monitoring.State.DeletedSequenceId);
            Assert.False(result.Monitoring.State.BufferObservedEmpty);
            Assert.NotEmpty(result.Monitoring.Diagnostics);
            Assert.Empty(result.Batches);
        }
        finally
        {
            await result.Communication.CloseAsync(CancellationToken.None);
            await result.Worker.CloseAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Async_failure_delivers_retained_metadata_before_terminal_health_and_closed_result()
    {
        var worker = await OpenValidationWorkerSessionAsync("worker-revalidation-terminal-later-entry");
        var prepare = await worker.PrepareCommunicationAsync(new PlcsimCommunicationPrepareRequest(
            worker.SessionId, "DB_UnitTestRunControl", "DB_UnitTestEventBuffer",
            UnitTestContractVersions.PlcsimAdvancedCommunication, UnitTestContractVersions.EventStreamModel,
            CreateWorkerOptions(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), "prepare"),
            CancellationToken.None);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);
        var order = new ConcurrentQueue<string>();
        var states = new ConcurrentQueue<PlcsimEventStreamMonitoringState>();
        var batches = new ConcurrentQueue<UnitTestEventBatch>();
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        communication.EventBatchReceived += (_, args) => batches.Enqueue(args.Batch);
        communication.EventStreamMonitoringStateChanged += (_, args) =>
        {
            states.Enqueue(args.State);
            if (args.State.LastReadState == EventBufferReadState.SessionInvalid)
                order.Enqueue("state");
        };
        communication.HealthChanged += (_, args) =>
        {
            if (args.Current is PlcsimCommunicationHealthState.Faulted or PlcsimCommunicationHealthState.Invalidated)
            {
                order.Enqueue("fault");
                fault.TrySetResult();
            }
        };
        try
        {
            var start = await communication.StartEventStreamMonitoringAsync(
                new PlcsimEventStreamMonitoringRequest(communication.Communication.SessionId, "start"),
                CancellationToken.None);
            Assert.True(start.Succeeded);
            await fault.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "state", "fault" }, order.ToArray());
            var terminal = Assert.Single(states.Where(state => state.LastReadState == EventBufferReadState.SessionInvalid));
            Assert.Equal(1u, terminal.ProducerSequenceId);
            Assert.False(terminal.BufferObservedEmpty);
            Assert.Equal(EventStreamBatchReleaseState.Completed, terminal.BatchReleaseState);
            Assert.Single(batches);
            var stopped = await communication.StopEventStreamMonitoringAsync(
                new PlcsimEventStreamStopRequest(communication.Communication.SessionId, "stop"),
                CancellationToken.None);
            Assert.False(stopped.Succeeded);
            Assert.Equal(terminal, stopped.State);
        }
        finally
        {
            await communication.CloseAsync(CancellationToken.None);
            await worker.CloseAsync(CancellationToken.None);
        }
    }
}
