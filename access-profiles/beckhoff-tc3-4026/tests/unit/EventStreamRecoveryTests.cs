using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
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
    public async Task BatchCallbackCanRequestResetAndRecoveryDiagnosticsFollowItsFailure()
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        var resetDone = new TaskCompletionSource<OperationResult<EventBufferResetCommandResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Access.ResetError = new AdsAccessException("Reset rejected", AdsErrorCode.TargetPortNotFound);
        test.Service.EventBatchReceivedEventHandler += async (_, _) =>
        {
            try { resetDone.TrySetResult(await test.Service.ResetEventBufferAsync(test.Context, default)); }
            catch (Exception error) { resetDone.TrySetException(error); }
        };
        await test.Prepare();
        await test.Start();
        Assert.False((await resetDone.Task.WaitAsync(TimeSpan.FromSeconds(3))).Value!.Accepted);
        var next = await clock.Next();
        test.Access = new() { Value = Observation(pending: 2) };
        next.Fire();
        await clock.Next();
        Assert.Equal(AccessHealthState.Healthy, test.Service.Health.State);
        Assert.Equal(ServiceState.Ready, test.Service.State);
        var codes = test.Diagnostics.Select(item => item.Code).ToList();
        Assert.True(codes.IndexOf("Beckhoff.EventStream.ResetWriteFailed") < codes.IndexOf("Beckhoff.EventStream.AccessRecovered"));
        Assert.Single(test.Batches);
        Assert.Equal(0, test.Access.Resets);
    }

    [Fact]
    public async Task ResetWritesOnceWithoutReadsClearingMetadataOrWaitingForPlcExecution()
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        test.Access.Value = Observation(3, 22, lost: 19);
        await test.Prepare();
        await test.Start();
        await clock.Next();
        var state = test.Service.CurrentMonitoringState;
        var reads = test.Access.MetadataReads;
        var batches = test.Batches.Count;
        var result = await test.Service.ResetEventBufferAsync(test.Context, default);
        Assert.True(result.IsSuccessful);
        Assert.True(result.Value!.Accepted);
        Assert.Equal(1, test.Access.Resets);
        Assert.Equal(reads, test.Access.MetadataReads);
        Assert.Equal(state, test.Service.CurrentMonitoringState);
        Assert.Equal(batches, test.Batches.Count);
        Assert.Equal(2u, test.Access.Value.DeleteSequenceId);
        Assert.DoesNotContain(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.Recovered");
    }

    [Fact]
    public async Task ResetCanBeSentWithoutStartingMonitorAndAcceptedWriteSurvivesCallerCancellation()
    {
        await using var test = new Fixture();
        using var cancelled = new CancellationTokenSource();
        test.Access.BeforeReset = token => { Assert.Equal(cancelled.Token, token); cancelled.Cancel(); return Task.CompletedTask; };
        await test.Prepare();
        var result = await test.Service.ResetEventBufferAsync(test.Context, cancelled.Token);
        Assert.True(result.Value!.Accepted);
        Assert.Equal(1, test.Access.Resets);
        Assert.Equal(0, test.Access.MetadataReads);
        Assert.Equal(EventBufferReadState.NotObserved, test.Service.CurrentMonitoringState.LastReadState);
        Assert.False(test.Service.CurrentMonitoringState.BufferObservedEmpty);
    }

    [Fact]
    public async Task ResetBeforeDispatchCanBeCancelledAndWrongContextIsRejected()
    {
        await using var test = new Fixture();
        Assert.False((await test.Service.ResetEventBufferAsync(test.Context, default)).Value!.Accepted);
        await test.Prepare();
        var other = test.Context with { Session = new(new("other"), DateTimeOffset.UtcNow) };
        Assert.False((await test.Service.ResetEventBufferAsync(other, default)).Value!.Accepted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Service.ResetEventBufferAsync(test.Context, new(true)));
        Assert.Equal(0, test.Access.Resets);
    }

    [Fact]
    public async Task ResetWaitsForCaptureCommitAndDoesNotOverwritePendingWatermark()
    {
        await using var test = new Fixture();
        var writing = Signal();
        var finish = Signal();
        test.Access.BeforeWrite = async (_, _) => { writing.TrySetResult(); await finish.Task.WaitAsync(TimeSpan.FromSeconds(3)); };
        await test.Prepare();
        await test.Start();
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var reset = test.Service.ResetEventBufferAsync(test.Context, default);
        try { Assert.False(reset.IsCompleted); Assert.Equal(0, test.Access.Resets); }
        finally { finish.TrySetResult(); }
        Assert.True((await reset.WaitAsync(TimeSpan.FromSeconds(3))).Value!.Accepted);
        await test.Service.StopMonitoringAsync(default);
        Assert.Single(test.Batches);
        Assert.Equal(new[] { "write", "reset" }, test.Access.Calls.Where(call => call is "write" or "reset"));
        Assert.Equal(2u, test.Access.Value.DeleteSequenceId);
    }

    [Fact]
    public async Task FailedResetIsDiagnosedWithoutReplayOrSyntheticCleanState()
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        test.Access.Value = Observation(3, 22, lost: 19);
        await test.Prepare();
        await test.Start();
        var retry = await clock.Next();
        var original = test.Access;
        original.ResetError = new AdsAccessException("Reset rejected", AdsErrorCode.TargetPortNotFound);
        var result = await test.Service.ResetEventBufferAsync(test.Context, default);
        Assert.False(result.IsSuccessful);
        Assert.False(result.Value!.Accepted);
        Assert.Equal(1, original.Resets);
        Assert.Equal((ushort)19, test.Service.CurrentMonitoringState.LostEventCount);
        Assert.Single(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.ResetWriteFailed");
        Assert.Single(test.Checks);
        test.Access = new() { Value = original.Value };
        retry.Fire();
        await clock.Next();
        Assert.Equal(0, test.Access.Resets);
        Assert.Equal(0, test.Access.Reads);
        Assert.Empty(test.Access.Writes);
        Assert.Single(test.Batches);
    }

    [Fact]
    public async Task FullAndLossAreTransitionsNotAccessFailuresAndConsumptionContinues()
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        test.Access.Value = Observation(3, 3, length: 4);
        await test.Prepare();
        await test.Start();
        var next = await clock.Next();
        Assert.Single(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.BufferFull");
        Assert.DoesNotContain(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.EventsLost");
        Assert.Equal(AccessHealthState.Healthy, test.Service.Health.State);
        test.Access.Value = Observation(3, 22, lost: 19, pending: 2, length: 4);
        next.Fire();
        next = await clock.Next();
        next.Fire();
        next = await clock.Next();
        Assert.Single(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.EventsLost");
        Assert.Single(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.BufferFull");
        Assert.Single(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.RecoveryStarted");
        Assert.Equal(1, test.Access.Reads);
        test.Access.Value = Observation(1, 22, 2, 19, indexOut: 3, length: 4);
        test.Access.Payload = [Entry(3)];
        next.Fire();
        next = await clock.Next();
        Assert.Equal(2, test.Batches.Count);
        Assert.Equal(new uint[] { 2, 3 }, test.Access.Writes);
        Assert.Equal(AccessHealthState.Healthy, test.Service.Health.State);
        test.Access.Value = Observation(0, 22, 3, 19, indexOut: 4, length: 4);
        next.Fire();
        next = await clock.Next();
        Assert.True(test.Service.CurrentMonitoringState.BufferObservedEmpty);
        Assert.Equal((ushort)19, test.Service.CurrentMonitoringState.LostEventCount);
        Assert.DoesNotContain(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.Recovered");
        test.Access.Value = Observation(0, 0, length: 4);
        next.Fire();
        await clock.Next();
        Assert.Single(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.Recovered");
        Assert.True(test.Service.CurrentMonitoringState.IsMonitoring);
        Assert.Equal(AccessHealthState.Healthy, test.Service.Health.State);
    }

    [Fact]
    public async Task ExplicitResetDoesNotPublishZeroUntilActuallyObservedAndNewSequencesAreNotSuppressed()
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        await test.Prepare();
        await test.Start();
        var next = await clock.Next();
        await test.Service.ResetEventBufferAsync(test.Context, default);
        Assert.Equal(3u, test.Service.CurrentMonitoringState.ProducerSequenceId);
        test.Access.Value = Observation(0, 0);
        next.Fire();
        next = await clock.Next();
        var empty = test.Batches.Last();
        Assert.Empty(empty.Events);
        Assert.Equal(EventBufferReadState.Empty, empty.ReadState);
        Assert.Equal(0u, empty.ProducerSequenceId);
        Assert.Equal(0u, empty.DeletedSequenceId);
        Assert.True(empty.BufferObservedEmpty);
        test.Access.Value = Observation();
        next.Fire();
        await clock.Next();
        Assert.Equal(new uint[] { 1, 2 }, test.Batches.Last().Events.Select(item => item.SequenceId));
        Assert.All(test.Batches, batch => Assert.Equal(test.Context.Session.SessionId, batch.SessionId));
        Assert.Equal(new uint[] { 2, 2 }, test.Access.Writes);
    }

    [Fact]
    public async Task ResetWithoutZeroWindowDoesNotInventEpochBoundaryOrDropCapturedEvents()
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        test.Access.Value = Observation(2, 100, 98);
        test.Access.Payload = [Entry(99), Entry(100)];
        await test.Prepare();
        await test.Start();
        var next = await clock.Next();
        test.Access.Value = Observation();
        test.Access.Payload = [Entry(1), Entry(2), Entry(3)];
        next.Fire();
        await clock.Next();
        Assert.Equal(2, test.Batches.Count);
        Assert.Equal(new uint[] { 1, 2 }, test.Batches.Last().Events.Select(item => item.SequenceId));
        Assert.DoesNotContain(test.Batches, batch => batch.BufferObservedEmpty || batch.ReadState == EventBufferReadState.Empty);
        Assert.All(test.Batches, batch => Assert.Equal(test.Context.Session.SessionId, batch.SessionId));
        Assert.Equal(new uint[] { 100, 2 }, test.Access.Writes);
        Assert.Equal(0, test.Access.Resets);
    }

    [Theory]
    [InlineData(AdsErrorCode.DeviceSymbolVersionInvalid)]
    [InlineData(AdsErrorCode.DeviceSymbolNotFound)]
    [InlineData(AdsErrorCode.TargetPortNotFound)]
    public async Task ChangedAccessRevalidatesAndPendingDeletionDoesNotCauseDuplicatePayload(AdsErrorCode code)
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        await test.Prepare();
        await test.Start();
        var next = await clock.Next();
        var original = test.Access;
        original.MetadataError = new AdsAccessException("Access changed", code);
        next.Fire();
        next = await clock.Next();
        Assert.Equal(1, original.Disposals);
        Assert.Equal(AccessHealthState.RevalidationRequired, test.Service.Health.State);
        Assert.Equal(EventStreamBatchReleaseState.Completed, test.Service.CurrentMonitoringState.BatchReleaseState);
        test.Access = new() { Value = original.Value };
        next.Fire();
        await clock.Next();
        Assert.Equal(1, test.Access.Prepares);
        Assert.Equal(0, test.Access.Reads);
        Assert.Empty(test.Access.Writes);
        Assert.Equal(0, test.Access.Resets);
        Assert.Single(test.Batches);
        Assert.Single(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.AccessRecovered");
    }

    [Fact]
    public async Task ProlongedUnavailablePlcRetainsDataWithoutAdsPollingDeadlineOrSpam()
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        await test.Prepare();
        await test.Start();
        var next = await clock.Next();
        var reads = test.Access.MetadataReads;
        test.Available = false;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            clock.Now = clock.Now.AddMinutes(1);
            next.Fire();
            next = await clock.Next();
        }
        Assert.Equal(reads, test.Access.MetadataReads);
        Assert.Equal(3u, test.Service.CurrentMonitoringState.ProducerSequenceId);
        Assert.False(test.Service.CurrentMonitoringState.BufferObservedEmpty);
        Assert.Equal(AccessHealthState.RevalidationRequired, test.Service.Health.State);
        Assert.True(test.Service.CurrentMonitoringState.IsMonitoring);
        Assert.Single(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.AccessFailed");
        Assert.False((await test.Service.ResetEventBufferAsync(test.Context, default)).Value!.Accepted);
        test.Access = new() { Value = Observation(0, 0) };
        test.Available = true;
        next.Fire();
        await clock.Next();
        Assert.Equal(AccessHealthState.Healthy, test.Service.Health.State);
        Assert.True(test.Batches.Last().BufferObservedEmpty);
        Assert.Equal(0, test.Access.Resets);
    }

    [Fact]
    public async Task RevalidationRejectsIncompatibleContractAndStopsWithoutSyntheticZeros()
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        await test.Prepare();
        await test.Start();
        var next = await clock.Next();
        test.Access.MetadataError = new AdsAccessException("Changed symbols", AdsErrorCode.DeviceSymbolVersionInvalid);
        next.Fire();
        next = await clock.Next();
        test.Access = new() { PrepareError = new InvalidDataException("Unsupported contract") };
        next.Fire();
        await Until(() => !test.Service.CurrentMonitoringState.IsMonitoring);
        Assert.Equal(AccessHealthState.Invalidated, test.Service.Health.State);
        Assert.Equal(EventBufferReadState.ContractInvalid, test.Service.CurrentMonitoringState.LastReadState);
        Assert.Equal(3u, test.Service.CurrentMonitoringState.ProducerSequenceId);
        Assert.False(test.Service.CurrentMonitoringState.BufferObservedEmpty);
        Assert.Equal(1, test.Access.Disposals);
        Assert.Empty(test.Access.Writes);
        Assert.Equal(0, test.Access.Resets);
        Assert.False((await test.Start()).IsSuccessful);
    }

    [Fact]
    public async Task CleanupFailureDoesNotHideReadFailureOrStopRevalidation()
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        await test.Prepare();
        test.Access.MetadataError = new AdsAccessException("Read failed", AdsErrorCode.TargetPortNotFound);
        test.Access.DisposeError = new IOException("Unsubscribe failed");
        await test.Start();
        var next = await clock.Next();
        Assert.Single(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.AccessFailed");
        Assert.Single(test.Diagnostics, item => item.Code == "Beckhoff.EventStream.CleanupFailed");
        Assert.True(test.Service.CurrentMonitoringState.IsMonitoring);
        test.Access = new();
        next.Fire();
        await clock.Next();
        Assert.Single(test.Batches);
        Assert.Equal(AccessHealthState.Healthy, test.Service.Health.State);
    }
}
