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

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class CommunicationPreparationTests
{
    [Fact]
    public async Task PublicPreparationBindsBothServicesWithoutDrainingAndUsesReturnedSession()
    {
        await using var test = new Fixture();
        var request = await test.PrepareRuntime();
        var communication = await test.Prepare(request);
        Assert.Equal(1, test.Stream.Prepares);
        Assert.Equal(1, test.Lifecycle.Prepares);
        Assert.Equal(1, test.Lifecycle.Reads);
        Assert.Equal(0, test.Stream.MetadataReads);
        Assert.Empty(test.Stream.Writes);
        Assert.False(test.Session.UnitTestEventStream.CurrentMonitoringState.IsMonitoring);
        var context = Context(request, communication);
        Assert.True((await test.Session.UnitTestEventStream.StartMonitoringAsync(context, default)).IsSuccessful);
        await Until(() => !test.Batches.IsEmpty);
        Assert.True((await test.Session.UnitTestEventStream.StopMonitoringAsync(default)).IsSuccessful);
        Assert.Equal(communication.SessionId, Assert.Single(test.Batches).SessionId);
        Assert.True((await test.Session.UnitTestEventStream.ResetEventBufferAsync(context, default)).Value!.Accepted);
        Assert.Equal(1, test.Stream.Resets);
        Assert.Equal(0, test.Lifecycle.Writes);
        Assert.Equal(0, test.Runtime.Writes);
    }

    [Fact]
    public async Task UnpreparedRuntimeCannotOpenDependentAccess()
    {
        await using var test = new Fixture();
        var request = new CommunicationPrepareRequest(test.Session.Configuration.Configuration,
            new(ProjectOwnershipMode.ExternallyManaged, null, null), new(new("missing"), "Local", "Local"), test.Catalog.Local.ToReference());
        var result = await test.Session.PrepareUnitTestCommunicationAsync(request, default);
        Assert.False(result.IsSuccessful);
        Assert.Null(result.Value);
        Assert.Equal(0, test.Stream.Prepares);
        Assert.Equal(0, test.Lifecycle.Prepares);
        Assert.Equal("Beckhoff.Communication.PrepareFailed", Assert.Single(test.Diagnostics).Code);
        Assert.Single(test.SessionDiagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationFailureDisposesPartialCommunicationWithoutReleasingRuntime(bool lifecycleFailure)
    {
        await using var test = new Fixture();
        var request = await test.PrepareRuntime();
        if (lifecycleFailure) test.Lifecycle.PrepareError = new InvalidDataException("RunControl incompatible");
        else test.Stream.PrepareError = new InvalidDataException("EventStream incompatible");
        var result = await test.Session.PrepareUnitTestCommunicationAsync(request, default);
        Assert.False(result.IsSuccessful);
        Assert.Null(result.Value);
        Assert.Equal(1, test.Stream.Disposals);
        Assert.Equal(lifecycleFailure ? 1 : 0, test.Lifecycle.Disposals);
        Assert.Equal(0, test.Runtime.Disposals);
        Assert.False(test.Session.UnitTestEventStream.CurrentMonitoringState.IsMonitoring);
        Assert.Empty(test.Stream.Writes);
        Assert.Equal(0, test.Lifecycle.Writes);
        Assert.Equal(0, test.Runtime.Writes);
        test.Stream = new();
        test.Lifecycle = new();
        await test.Prepare(request);
    }

    [Fact]
    public async Task CancellationDuringSecondBindingCleansBothAndDoesNotReturnSession()
    {
        await using var test = new Fixture();
        var request = await test.PrepareRuntime();
        using var cancellation = new CancellationTokenSource();
        test.Lifecycle.OnPrepare = () => { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            test.Session.PrepareUnitTestCommunicationAsync(request, cancellation.Token));
        Assert.Equal(1, test.Stream.Disposals);
        Assert.Equal(1, test.Lifecycle.Disposals);
        Assert.Equal(0, test.Runtime.Disposals);
        Assert.Empty(test.Batches);
        Assert.False(test.Session.UnitTestEventStream.CurrentMonitoringState.IsMonitoring);
    }

    [Fact]
    public async Task ReconnectReleasesBothServicesBeforeReplacingRuntimeAndRequiresFreshPreparation()
    {
        await using var test = new Fixture();
        var request = await test.PrepareRuntime();
        var communication = await test.Prepare(request);
        var oldContext = Context(request, communication);
        await test.Session.UnitTestEventStream.StartMonitoringAsync(oldContext, default);
        await Until(() => !test.Batches.IsEmpty);
        var oldStream = test.Stream;
        var oldLifecycle = test.Lifecycle;
        var oldRuntime = test.Runtime;
        test.Runtime = new();
        test.BeforeRuntimeOpen = () =>
        {
            Assert.Equal(1, oldStream.Disposals);
            Assert.Equal(1, oldLifecycle.Disposals);
            Assert.Equal(1, oldRuntime.Disposals);
        };
        var result = await test.Session.ReconnectRuntimeAsync(new(request.Configuration, RuntimeProvisioningMode.UseExisting,
            request.Runtime, request.RuntimeTarget), default);
        Assert.True(result.IsSuccessful, result.Message);
        Assert.Equal(request.Runtime.Id, result.Value!.Runtime.Id);
        Assert.False(test.Session.UnitTestEventStream.CurrentMonitoringState.IsMonitoring);
        Assert.False((await test.Session.UnitTestEventStream.StartMonitoringAsync(oldContext, default)).IsSuccessful);
        Assert.False((await test.Session.UnitTestEventStream.ResetEventBufferAsync(oldContext, default)).IsSuccessful);
        Assert.False((await test.Session.UnitTestLifecycle.StartAsync(oldContext, default)).IsSuccessful);
        Assert.Equal(0, oldRuntime.Writes + test.Runtime.Writes + oldLifecycle.Writes + oldStream.Resets);
        Assert.Single(test.Batches);
        test.Stream = new();
        test.Lifecycle = new();
        var replacement = await test.Prepare(request);
        Assert.NotEqual(communication.SessionId, replacement.SessionId);
        Assert.Equal(EventBufferReadState.NotObserved, test.Session.UnitTestEventStream.CurrentMonitoringState.LastReadState);
        Assert.False(test.Session.UnitTestEventStream.CurrentMonitoringState.BufferObservedEmpty);
        Assert.True((await test.Session.UnitTestEventStream.StartMonitoringAsync(Context(request, replacement), default)).IsSuccessful);
        await Until(() => test.Batches.Count == 2);
        await test.Session.UnitTestEventStream.StopMonitoringAsync(default);
        Assert.Equal(new[] { communication.SessionId, replacement.SessionId }, test.Batches.Select(batch => batch.SessionId));
    }

    [Fact]
    public async Task FailedDependentCleanupStillReleasesOtherServiceAndDoesNotOpenReplacementRuntime()
    {
        await using var test = new Fixture();
        var request = await test.PrepareRuntime();
        await test.Prepare(request);
        test.Stream.DisposeError = new IOException("Dispose failed");
        test.BeforeRuntimeOpen = () => Assert.Fail("Replacement must not open after failed dependent cleanup.");
        var result = await test.Session.ReconnectRuntimeAsync(new(request.Configuration, RuntimeProvisioningMode.UseExisting,
            request.Runtime, request.RuntimeTarget), default);
        Assert.False(result.IsSuccessful);
        Assert.Equal(1, test.Stream.Disposals);
        Assert.Equal(1, test.Lifecycle.Disposals);
        Assert.Equal(0, test.Runtime.Disposals);
        Assert.Single(test.Diagnostics.Where(item => item.Code == "Beckhoff.EventStream.CleanupFailed"));
        Assert.DoesNotContain(test.SessionDiagnostics, item => item.Code == "Beckhoff.EventStream.CleanupFailed");
    }

    [Fact]
    public async Task DisposalDuringPreparationJoinsSetupAndDoesNotReturnUsableCommunication()
    {
        await using var test = new Fixture();
        var request = await test.PrepareRuntime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Stream.BeforePrepare = async _ =>
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(TimeSpan.FromSeconds(3));
        };
        var preparation = test.Session.PrepareUnitTestCommunicationAsync(request, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var disposal = test.Session.DisposeAsync().AsTask();
        try { Assert.False(disposal.IsCompleted); }
        finally { finish.TrySetResult(); }
        var result = await preparation.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(result.IsSuccessful);
        Assert.Null(result.Value);
        await disposal.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, test.Stream.Disposals);
        Assert.Equal(1, test.Lifecycle.Disposals);
        Assert.Equal(1, test.Runtime.Disposals);
        Assert.Equal(AccessHealthState.Disposed, test.Session.Health.State);
        Assert.Empty(test.Stream.Writes);
        Assert.Empty(test.Batches);
    }

    [Fact]
    public async Task RepreparationJoinsPreviousDrainAndDiscardsItsMetadataLatch()
    {
        await using var test = new Fixture();
        var request = await test.PrepareRuntime();
        var first = await test.Prepare(request);
        await test.Session.UnitTestEventStream.StartMonitoringAsync(Context(request, first), default);
        await Until(() => !test.Batches.IsEmpty);
        var oldStream = test.Stream;
        var oldLifecycle = test.Lifecycle;
        test.Stream = new();
        test.Lifecycle = new();
        var second = await test.Prepare(request);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(1, oldStream.Disposals);
        Assert.Equal(1, oldLifecycle.Disposals);
        Assert.Equal(0, test.Runtime.Disposals);
        Assert.Equal(0u, test.Session.UnitTestEventStream.CurrentMonitoringState.ProducerSequenceId);
        Assert.Equal(EventBufferReadState.NotObserved, test.Session.UnitTestEventStream.CurrentMonitoringState.LastReadState);
        Assert.False(test.Session.UnitTestEventStream.CurrentMonitoringState.BufferObservedEmpty);
        Assert.False(test.Session.UnitTestEventStream.CurrentMonitoringState.IsMonitoring);
        Assert.Single(test.Batches);
    }

    private static UnitTestContext Context(CommunicationPrepareRequest request, CommunicationSession session) =>
        new(session, request.ProjectContext, request.Runtime, request.RuntimeTarget);

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly FakeCatalog Catalog = new();
        internal FakeRuntimeAccess Runtime = new();
        internal FakeLifecycleAccess Lifecycle = new();
        internal FakeEventStreamAccess Stream = new();
        internal readonly BeckhoffAccessProfileSession Session;
        internal readonly ConcurrentQueue<Diagnostic> Diagnostics = new();
        internal readonly ConcurrentQueue<Diagnostic> SessionDiagnostics = new();
        internal readonly ConcurrentQueue<UnitTestEventBatch> Batches = new();
        internal Action? BeforeRuntimeOpen;

        internal Fixture()
        {
            var context = new AccessProfileContext(NullLoggerFactory.Instance, TimeProvider.System, AppContext.BaseDirectory,
                Path.GetTempPath(), new OperationalLoggingOptions(Path.GetTempPath(), LogLevel.Information, 1048576, 2));
            var runtime = new BeckhoffPlcControlService(context, ProjectOwnershipMode.ExternallyManaged, Catalog,
                (_, _) => { BeforeRuntimeOpen?.Invoke(); return Runtime; }, TimeSpan.FromMinutes(1));
            var lifecycle = new BeckhoffUnitTestLifecycleService(context, _ => Lifecycle);
            var stream = new BeckhoffUnitTestEventStreamService(context, _ => Stream);
            Session = new(context, new("beckhoff-tc3-4026", "1.0.0", "", ProjectOwnershipMode.ExternallyManaged),
                () => { }, runtime, lifecycle, stream);
            Session.DiagnosticEventHandler += (_, args) =>
            {
                SessionDiagnostics.Enqueue(args.Diagnostic);
                Diagnostics.Enqueue(args.Diagnostic);
            };
            Session.Project.DiagnosticEventHandler += (_, args) => Diagnostics.Enqueue(args.Diagnostic);
            Session.PlcControl.DiagnosticEventHandler += (_, args) => Diagnostics.Enqueue(args.Diagnostic);
            Session.UnitTestLifecycle.DiagnosticEventHandler += (_, args) => Diagnostics.Enqueue(args.Diagnostic);
            Session.UnitTestEventStream.DiagnosticEventHandler += (_, args) => Diagnostics.Enqueue(args.Diagnostic);
            stream.EventBatchReceivedEventHandler += (_, args) => Batches.Enqueue(args.Batch);
            Assert.True(Session.Configuration.LoadConfig("""{"unitTest":{"monitoringIntervalMs":60000,"drainIntervalMs":60000}}""").IsSuccessful);
        }

        internal async Task<CommunicationPrepareRequest> PrepareRuntime()
        {
            var result = await Session.PlcControl.PrepareRuntimeAsync(new(Session.Configuration.Configuration,
                RuntimeProvisioningMode.UseExisting, Catalog.Local.ToReference()), default);
            Assert.True(result.IsSuccessful, result.Message);
            return new(Session.Configuration.Configuration, new(ProjectOwnershipMode.ExternallyManaged, null, null),
                result.Value!.Runtime, result.Value.RuntimeTarget);
        }

        internal async Task<CommunicationSession> Prepare(CommunicationPrepareRequest request)
        {
            var result = await Session.PrepareUnitTestCommunicationAsync(request, default);
            Assert.True(result.IsSuccessful, result.Message);
            return result.Value!;
        }

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    private sealed class FakeCatalog : ILocalUsermodeRuntimeCatalog
    {
        internal LocalUsermodeRuntime Local { get; } = new("UmRT_Default", "192.0.2.10.1.1", @"C:\test-runtime\3.1");
        public IReadOnlyList<LocalUsermodeRuntime> Discover(CancellationToken token) => [Local];
        public LocalUsermodeRuntime ResolveDefault(CancellationToken token) => Local;
        public LocalUsermodeRuntime Verify(OnlineRuntimeIdentity identity, CancellationToken token) => Local;
    }

    private sealed class FakeRuntimeAccess : IAdsRuntimeAccess
    {
        public event EventHandler? SystemStateInvalidated { add { } remove { } }
        public event EventHandler? PlcStateInvalidated { add { } remove { } }
        public Task<SystemStateRead> ReadSystemObservationAsync(CancellationToken token) => Task.FromResult(new SystemStateRead(AdsState.Run, 1));
        internal int Disposals, Writes;
        public event EventHandler? PlcStateChanged { add { } remove { } }
        public Task<OnlineRuntimeIdentity> ReadIdentityAsync(CancellationToken token) =>
            Task.FromResult(new OnlineRuntimeIdentity("192.0.2.10.1.1", @"C:\test-runtime\3.1", "PC-WIN-U"));
        public Task<AdsState> ReadSystemStateAsync(CancellationToken token) => Task.FromResult(AdsState.Run);
        public Task<AdsState> ReadPlcStateAsync(CancellationToken token) => Task.FromResult(AdsState.Run);
        public Task SubscribePlcStateAsync(CancellationToken token) => Task.CompletedTask;
        public Task ReleasePlcAccessAsync() => Task.CompletedTask;
        public Task BindPlcPortAsync(int port) => Task.CompletedTask;
        public Task WritePlcStateAsync(AdsState state, CancellationToken token) { Writes++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class FakeLifecycleAccess : IRunControlAccess
    {
        internal int Prepares, Reads, Writes, Disposals;
        internal Exception? PrepareError;
        internal Action? OnPrepare;
        public Task PrepareAsync(string root, CancellationToken token)
        {
            Prepares++;
            OnPrepare?.Invoke();
            if (PrepareError is not null) throw PrepareError;
            return Task.CompletedTask;
        }
        public Task<UnitTestRunControlObservation> ReadAsync(CancellationToken token)
        {
            Reads++;
            return Task.FromResult(new UnitTestRunControlObservation(UnitTestExecutionStatus.Idle, UnitTestExecutionSignals.Ready,
                UnitTestLifecycleState.Idle, UnitTestResultState.NotEvaluated));
        }
        public Task WriteAsync(UnitTestRunCommand command, CancellationToken token) { Writes++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
