using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using TwinCAT.Ads;
using Xunit;
using static PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit.FakeEventStreamAccess;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed partial class EventStreamServiceTests
{
    [Fact]
    public async Task SubscriptionRejectsAccessWithoutNotificationSupport()
    {
        await using var test = new Fixture();
        test.Settings = test.Settings with { Strategy = ObservationStrategy.Subscription };
        var result = await test.PrepareResult();
        Assert.Equal(OperationResultState.Failed, result.State);
        Assert.Equal(1, test.Access.Prepares);
        Assert.Equal(1, test.Access.Disposals);
        Assert.Empty(test.Access.Calls);
        Assert.Contains("does not support subscription", result.Message);
    }

    [Fact]
    public async Task PreparationBindsConfiguredRootsButNeitherReadsPayloadNorWrites()
    {
        await using var test = new Fixture();
        Assert.Equal(EventBufferReadState.NotObserved, test.Service.CurrentMonitoringState.LastReadState);
        Assert.False((await test.Start()).IsSuccessful);
        await test.Prepare();
        Assert.Equal(test.Settings, test.Access.Settings);
        Assert.Equal(1, test.Access.Prepares);
        Assert.Equal(0, test.Access.MetadataReads);
        Assert.Equal(0, test.Access.Reads);
        Assert.Empty(test.Access.Writes);
        Assert.False(test.Service.CurrentMonitoringState.IsMonitoring);
        Assert.Equal(test.Context.Session.SessionId, test.Service.CurrentMonitoringState.SessionId);
    }

    [Fact]
    public async Task StartsOncePublishesAfterWriteWithoutWaitingForDeletionAndRetainsStateOnStop()
    {
        await using var test = new Fixture();
        await test.Prepare();
        test.Service.EventBatchReceivedEventHandler += (_, args) =>
        {
            Assert.Equal(2u, test.Access.Value.DeleteSequenceId);
            Assert.Single(test.Access.Writes);
            Assert.Equal(new uint[] { 1, 2 }, args.Batch.Events.Select(item => item.SequenceId));
        };
        Assert.True((await test.Start()).IsSuccessful);
        await Until(() => test.Batches.Count == 1 && test.Service.CurrentMonitoringState.BatchReleaseState == EventStreamBatchReleaseState.Completed);
        Assert.True((await test.Start()).IsSuccessful);
        Assert.True((await test.Service.StopMonitoringAsync(default)).IsSuccessful);
        Assert.False(test.Service.CurrentMonitoringState.IsMonitoring);
        Assert.Equal(EventBufferReadState.Valid, test.Service.CurrentMonitoringState.LastReadState);
        Assert.Equal(3u, test.Service.CurrentMonitoringState.ProducerSequenceId);
        Assert.Equal(0u, test.Service.CurrentMonitoringState.DeletedSequenceId);
        Assert.Equal(0, test.Access.Disposals);
        Assert.Single(test.Batches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrReleaseJoinsAcceptedWriteAndPublishesBeforeDisposal(bool release)
    {
        await using var test = new Fixture();
        var entered = Signal();
        var finish = Signal();
        test.Access.BeforeWrite = async (_, token) =>
        {
            Assert.False(token.CanBeCanceled);
            entered.TrySetResult();
            await finish.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };
        await test.Prepare();
        await test.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        test.Service.EventBatchReceivedEventHandler += (_, _) => Assert.Equal(0, test.Access.Disposals);
        var stop = release ? test.Service.ReleaseCommunicationAsync(default) : test.Service.StopMonitoringAsync(default);
        try
        {
            Assert.False(stop.IsCompleted);
            Assert.Empty(test.Batches);
            Assert.Equal(0, test.Access.Disposals);
        }
        finally { finish.TrySetResult(); }
        Assert.True((await stop.WaitAsync(TimeSpan.FromSeconds(3))).IsSuccessful);
        Assert.Single(test.Batches);
        Assert.Single(test.Access.Writes);
        Assert.Equal(release ? 1 : 0, test.Access.Disposals);
        Assert.False(test.Service.CurrentMonitoringState.IsMonitoring);
    }

    [Fact]
    public async Task StopBeforeCommitCancelsReadWithoutWriteOrPublication()
    {
        await using var test = new Fixture();
        var entered = Signal();
        test.Access.BeforeRead = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        await test.Prepare();
        await test.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True((await test.Service.ReleaseCommunicationAsync(default).WaitAsync(TimeSpan.FromSeconds(3))).IsSuccessful);
        Assert.Empty(test.Batches);
        Assert.Empty(test.Access.Writes);
        Assert.Equal(1, test.Access.Disposals);
        Assert.Equal(3u, test.Service.CurrentMonitoringState.ProducerSequenceId);
    }

    [Fact]
    public async Task SchedulingWaitsAfterAttemptAndUsesDrainUntilTrustedEmpty()
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        test.Settings = test.Settings with { MonitoringInterval = TimeSpan.FromMilliseconds(25), DrainInterval = TimeSpan.FromMilliseconds(2) };
        var entered = Signal();
        var finish = Signal();
        test.Access.BeforeWrite = async (_, _) => { entered.TrySetResult(); await finish.Task.WaitAsync(TimeSpan.FromSeconds(5)); };
        await test.Prepare();
        await test.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try { Assert.Empty(clock.Timers); }
        finally { finish.TrySetResult(); }
        var first = await clock.Next();
        Assert.Equal(test.Settings.DrainInterval, first.Due);
        test.Access.Value = Observation(5, 8, lost: 3, pending: 2);
        first.Fire();
        var pending = await clock.Next();
        Assert.Equal(test.Settings.DrainInterval, pending.Due);
        Assert.Equal(1, test.Access.Reads);
        Assert.Single(test.Batches);
        Assert.Equal(8u, test.Service.CurrentMonitoringState.ProducerSequenceId);
        Assert.Equal((ushort)3, test.Service.CurrentMonitoringState.LostEventCount);
        test.Access.Value = Observation(0, 8, 8, 3, indexOut: 6);
        pending.Fire();
        var empty = await clock.Next();
        Assert.Equal(test.Settings.MonitoringInterval, empty.Due);
        Assert.Equal(2, test.Batches.Count);
        Assert.Empty(test.Batches.Last().Events);
        empty.Fire();
        Assert.Equal(test.Settings.MonitoringInterval, (await clock.Next()).Due);
        Assert.Equal(2, test.Batches.Count);
    }

    [Fact]
    public async Task FailedWatermarkIsDiagnosedAndRetryReobservesRatherThanReplaying()
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        test.Access.WriteError = new AdsAccessException("Write rejected", AdsErrorCode.ClientSyncTimeOut);
        await test.Prepare();
        await test.Start();
        var retry = await clock.Next();
        Assert.Empty(test.Batches);
        Assert.Equal(EventBufferReadState.Valid, test.Service.CurrentMonitoringState.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Idle, test.Service.CurrentMonitoringState.BatchReleaseState);
        Assert.False(test.Service.CurrentMonitoringState.BufferObservedEmpty);
        Assert.Equal(3u, test.Service.CurrentMonitoringState.ProducerSequenceId);
        Assert.Single(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.WatermarkWriteFailed");
        Assert.Single(test.Checks);
        var replacement = new FakeEventStreamAccess { Value = Observation(1, 4, 3), Payload = [Entry(4)] };
        test.Access = replacement;
        retry.Fire();
        await clock.Next();
        Assert.Equal(4u, Assert.Single(Assert.Single(test.Batches).Events).SequenceId);
        Assert.Equal(4u, Assert.Single(replacement.Writes));
        Assert.Equal(AccessHealthState.Healthy, test.Service.Health.State);
    }

    [Fact]
    public async Task SnapshotFailureIsRetryableDeduplicatedAndDoesNotDisposeBinding()
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        test.Access.ReadError = new InvalidDataException("Incomplete range");
        await test.Prepare();
        await test.Start();
        (await clock.Next()).Fire();
        var retry = await clock.Next();
        Assert.Empty(test.Access.Writes);
        Assert.Empty(test.Batches);
        Assert.Equal(3u, test.Service.CurrentMonitoringState.ProducerSequenceId);
        Assert.Equal(EventBufferReadState.Inconsistent, test.Service.CurrentMonitoringState.LastReadState);
        Assert.Equal(0, test.Access.Disposals);
        Assert.Single(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.SnapshotRejected");
        test.Access.ReadError = null;
        retry.Fire();
        await clock.Next();
        Assert.Single(test.Batches);
        Assert.Equal(EventBufferReadState.Valid, test.Service.CurrentMonitoringState.LastReadState);
    }

    [Fact]
    public async Task ContractFailureDuringPreparationClosesBindingWithoutStartingMonitoring()
    {
        await using var test = new Fixture();
        test.Access.PrepareError = new InvalidDataException("Unsupported contract");
        Assert.False((await test.PrepareResult()).IsSuccessful);
        Assert.Equal(AccessHealthState.Invalidated, test.Service.Health.State);
        Assert.Equal(EventBufferReadState.NotObserved, test.Service.CurrentMonitoringState.LastReadState);
        Assert.False(test.Service.CurrentMonitoringState.BufferObservedEmpty);
        Assert.Equal(1, test.Access.Disposals);
        Assert.Equal(0, test.Access.Reads);
        Assert.False((await test.Start()).IsSuccessful);
    }

    [Fact]
    public async Task ReadFailureRetainsCountersAndPriorBatchEvidence()
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        await test.Prepare();
        await test.Start();
        var retry = await clock.Next();
        test.Access.MetadataError = new AdsAccessException("Lost access", AdsErrorCode.TargetPortNotFound);
        retry.Fire();
        await clock.Next();
        Assert.Equal(3u, test.Service.CurrentMonitoringState.ProducerSequenceId);
        Assert.False(test.Service.CurrentMonitoringState.BufferObservedEmpty);
        Assert.Equal(EventStreamBatchReleaseState.Completed, test.Service.CurrentMonitoringState.BatchReleaseState);
        Assert.Equal(EventBufferReadState.RetryRequired, test.Service.CurrentMonitoringState.LastReadState);
        Assert.Single(test.Checks);
        Assert.Single(test.Batches);
    }

    [Fact]
    public async Task StopDuringRebindingDisposesPartialBindingAndResumePreparesFreshAccess()
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        await test.Prepare();
        test.Access.MetadataError = new AdsAccessException("Read failed", AdsErrorCode.DeviceSymbolVersionInvalid);
        await test.Start();
        var retry = await clock.Next();
        var entered = Signal();
        var partial = new FakeEventStreamAccess
        {
            BeforePrepare = async token => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        };
        test.Access = partial;
        retry.Fire();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True((await test.Service.StopMonitoringAsync(default)).IsSuccessful);
        Assert.Equal(1, partial.Disposals);
        Assert.Empty(test.Batches);
        test.Access = new();
        await test.Start();
        await clock.Next();
        Assert.Equal(1, test.Access.Prepares);
        Assert.Single(test.Batches);
    }

    [Fact]
    public async Task NewContextStartsNotObservedAndWrongContextCannotStart()
    {
        await using var test = new Fixture();
        await test.Prepare();
        await test.Start();
        await Until(() => test.Service.CurrentMonitoringState.BatchReleaseState == EventStreamBatchReleaseState.Completed);
        var old = test.Context;
        test.Context = old with { Session = new(new("new-session"), DateTimeOffset.UtcNow) };
        test.Access = new();
        await test.Prepare();
        Assert.Equal(EventBufferReadState.NotObserved, test.Service.CurrentMonitoringState.LastReadState);
        Assert.Equal(0u, test.Service.CurrentMonitoringState.ProducerSequenceId);
        Assert.False(test.Service.CurrentMonitoringState.BufferObservedEmpty);
        Assert.False((await test.Service.StartMonitoringAsync(old, default)).IsSuccessful);
    }

    [Fact]
    public async Task LostTechnicalContextDoesNotUseNewTargetOrWrite()
    {
        await using var test = new Fixture();
        await test.Prepare();
        test.Access.BeforePendingCheck = () => test.Current = false;
        await test.Start();
        await Until(() => !test.Service.CurrentMonitoringState.IsMonitoring);
        Assert.Equal(AccessHealthState.Invalidated, test.Service.Health.State);
        Assert.Empty(test.Access.Writes);
        Assert.Empty(test.Batches);
        Assert.Single(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.ContextLost");
    }

    [Fact]
    public async Task BatchCallbackCanRequestAsynchronousStopWithoutHoldingAdsGate()
    {
        await using var test = new Fixture();
        var stopped = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Service.EventBatchReceivedEventHandler += async (_, _) =>
        {
            try { stopped.TrySetResult(await test.Service.StopMonitoringAsync(default)); }
            catch (Exception error) { stopped.TrySetException(error); }
        };
        await test.Prepare();
        await test.Start();
        Assert.True((await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3))).IsSuccessful);
        Assert.Single(test.Batches);
    }

    [Fact]
    public async Task ThrowingBatchSubscriberInvalidatesInsteadOfSilentlyContinuingAfterCommittedWrite()
    {
        await using var test = new Fixture();
        test.Service.EventBatchReceivedEventHandler += (_, _) => throw new InvalidOperationException("Receiver failed");
        await test.Prepare();
        await test.Start();
        await Until(() => !test.Service.CurrentMonitoringState.IsMonitoring);
        Assert.Equal(AccessHealthState.Invalidated, test.Service.Health.State);
        Assert.Single(test.Access.Writes);
        Assert.Single(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.ObserverFailed");
        Assert.False((await test.Start()).IsSuccessful);
    }

    [Fact]
    public async Task CleanupFailureIsNotReportedAsSuccessfulRelease()
    {
        await using var test = new Fixture();
        await test.Prepare();
        test.Access.DisposeError = new IOException("Cleanup failed");
        Assert.False((await test.Service.ReleaseCommunicationAsync(default)).IsSuccessful);
        Assert.Equal(ServiceState.CleanupRequired, test.Service.State);
        Assert.Single(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.CleanupFailed");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly BeckhoffUnitTestEventStreamService Service;
        internal FakeEventStreamAccess Access = new();
        internal IEventStreamAccess? AccessOverride;
        internal volatile bool Current = true, Available = true;
        internal long BindingGeneration;
        internal readonly ConcurrentQueue<Diagnostic> Diagnostics = new();
        internal readonly ConcurrentQueue<AdsErrorCode> Checks = new();
        internal readonly ConcurrentQueue<UnitTestEventBatch> Batches = new();
        internal EventStreamSettings Settings = new("Test.Events", "Test.Run", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 2);
        internal UnitTestContext Context = new(new(new("communication"), DateTimeOffset.UtcNow),
            new(ProjectOwnershipMode.ExternallyManaged, null, null), new(new("runtime"), "Local", "Local"), new(new("192.0.2.10.1.1"), "Local"));

        internal Fixture(TimeProvider? clock = null, ILoggerFactory? loggerFactory = null,
            LogLevel logLevel = LogLevel.Information, bool raw = false)
        {
            Service = new(new AccessProfileContext(loggerFactory ?? NullLoggerFactory.Instance, clock ?? TimeProvider.System, AppContext.BaseDirectory,
                Path.GetTempPath(), new OperationalLoggingOptions(Path.GetTempPath(), logLevel, 1048576, 2, raw)), endpoint =>
                {
                    Assert.Equal("192.0.2.10.1.1", endpoint.NetId);
                    Assert.Equal(852, endpoint.Port);
                    return AccessOverride ?? Access;
                });
            Service.DiagnosticEventHandler += (_, args) => Diagnostics.Enqueue(args.Diagnostic);
            Service.EventBatchReceivedEventHandler += (_, args) => Batches.Enqueue(args.Batch);
        }

        internal Task<OperationResult> PrepareResult() => Service.PrepareAsync(Context, Settings,
            new("192.0.2.10.1.1", 852, () => Current, () => Available, Checks.Enqueue)
            { BindingGeneration = () => Interlocked.Read(ref BindingGeneration) }, default);
        internal async Task Prepare()
        {
            var result = await PrepareResult();
            Assert.True(result.IsSuccessful, result.Message);
        }
        internal Task<OperationResult<EventStreamState>> Start() => Service.StartMonitoringAsync(Context, default);
        public async ValueTask DisposeAsync() => await Service.CloseAsync();
    }

    private sealed class DelayClock : TimeProvider
    {
        internal readonly ConcurrentQueue<DelayTimer> Timers = new();
        internal DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => Now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DelayTimer(callback, state, dueTime);
            Timers.Enqueue(timer);
            return timer;
        }
        internal async Task<DelayTimer> Next()
        {
            await Until(() => !Timers.IsEmpty);
            Assert.True(Timers.TryDequeue(out var timer));
            return timer!;
        }
    }

    private sealed class DelayTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        internal TimeSpan Due { get; } = due;
        private int disposed;
        internal void Fire() { if (Volatile.Read(ref disposed) == 0) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
