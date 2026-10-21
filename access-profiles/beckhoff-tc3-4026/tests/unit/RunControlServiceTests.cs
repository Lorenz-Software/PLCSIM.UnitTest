using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using TwinCAT.Ads;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed partial class RunControlServiceTests
{
    [Fact]
    public async Task SubscriptionRegistersBeforeInitialRead()
    {
        await using var test = new Fixture();
        var result = await test.Service.PrepareAsync(test.Context,
            new("Test.RunControl", TimeSpan.FromMilliseconds(10)) { Strategy = ObservationStrategy.Subscription },
            new("192.0.2.10.1.1", 852, () => true, () => true, test.Checks.Enqueue), default);
        Assert.True(result.IsSuccessful, result.Message);
        Assert.Equal(1, test.Access.Prepares);
        Assert.Equal(1, test.Access.Registrations);
        Assert.Equal(TimeSpan.FromMilliseconds(10), test.Access.SamplingInterval);
        Assert.True(test.Access.Reads >= 1);
        Assert.Equal(Ready, Assert.Single(test.Observations));
        Assert.Empty(test.Diagnostics);
    }

    [Theory]
    [InlineData(UnitTestRunCommand.Start, 0)]
    [InlineData(UnitTestRunCommand.Abort, 0)]
    [InlineData(UnitTestRunCommand.Reset, 0)]
    [InlineData(UnitTestRunCommand.Start, 1)]
    [InlineData(UnitTestRunCommand.Abort, 1)]
    [InlineData(UnitTestRunCommand.Reset, 1)]
    [InlineData(UnitTestRunCommand.Start, 2)]
    [InlineData(UnitTestRunCommand.Abort, 2)]
    [InlineData(UnitTestRunCommand.Reset, 2)]
    [InlineData(UnitTestRunCommand.Start, 3)]
    [InlineData(UnitTestRunCommand.Abort, 3)]
    [InlineData(UnitTestRunCommand.Reset, 3)]
    public async Task CommandCancellationPreservesOnlyConfirmedWriteAcceptance(UnitTestRunCommand command, int outcome)
    {
        await using var test = new Fixture();
        await test.Prepare();
        var observation = test.Service.CurrentObservation;
        var reads = test.Access.Reads;
        using var cancellation = new CancellationTokenSource();
        if (outcome == 0) cancellation.Cancel();
        else test.Access.OnWrite = cancellation.Cancel;
        if (outcome == 2) test.Access.WriteError = new OperationCanceledException(cancellation.Token);
        if (outcome == 3) test.Access.WriteError = new AdsAccessException("Write rejected", AdsErrorCode.DeviceInvalidState);
        if (outcome is 0 or 2)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Command(command, cancellation.Token));
        else
        {
            var result = await test.Command(command, cancellation.Token);
            Assert.Equal(outcome == 1, result.IsSuccessful);
            Assert.Equal(outcome == 1, result.Value!.Accepted);
            if (outcome == 3) Assert.Contains("Write rejected", result.Message);
        }
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(outcome == 0 ? 0 : 1, test.Access.Commands.Count);
        Assert.Equal(reads, test.Access.Reads);
        Assert.Equal(observation, test.Service.CurrentObservation);
    }

    private static readonly UnitTestRunControlObservation Ready = new(UnitTestExecutionStatus.Idle,
        UnitTestExecutionSignals.Ready, UnitTestLifecycleState.Idle, UnitTestResultState.NotEvaluated);

    [Fact]
    public async Task StartsUnknownAndPreparesOnlyConfiguredRunControl()
    {
        await using var test = new Fixture();
        Assert.Equal(UnitTestLifecycleState.Unknown, test.Service.CurrentObservation.State);
        Assert.False((await test.Service.ResetAsync(test.Context, default)).Value!.Accepted);
        await test.Prepare();
        Assert.Equal("Test.RunControl", test.Access.Root);
        Assert.Equal(Ready, test.Service.CurrentObservation);
        Assert.Single(test.Observations);
        Assert.Empty(test.Access.Commands);
        Assert.Equal(AccessHealthState.Healthy, test.Service.Health.State);
    }

    [Theory]
    [InlineData(UnitTestRunCommand.Start)]
    [InlineData(UnitTestRunCommand.Abort)]
    [InlineData(UnitTestRunCommand.Reset)]
    public async Task CommandIsOneTransportWriteWithoutReadbackOrSyntheticReaction(UnitTestRunCommand command)
    {
        await using var test = new Fixture();
        await test.Prepare();
        var reads = test.Access.Reads;
        var result = await test.Command(command);
        Assert.True(result.IsSuccessful);
        Assert.Equal(new TestRunCommandResult(command, true), result.Value);
        Assert.Equal(command, Assert.Single(test.Access.Commands));
        Assert.Equal(reads, test.Access.Reads);
        Assert.Equal(Ready, test.Service.CurrentObservation);
        Assert.Single(test.Observations);
        Assert.Equal(0, test.Access.Registrations);
        Assert.Equal(0, test.Access.Requests);
    }

    [Fact]
    public async Task WriteFailureRequestsSystemCheckAndNeverReplaysCommand()
    {
        await using var test = new Fixture();
        await test.Prepare();
        test.Access.WriteError = new AdsAccessException("Write", AdsErrorCode.TargetPortNotFound);
        var result = await test.Command(UnitTestRunCommand.Start);
        Assert.False(result.IsSuccessful);
        Assert.False(result.Value!.Accepted);
        Assert.Equal(Ready, test.Service.CurrentObservation);
        Assert.Equal(AdsErrorCode.TargetPortNotFound, Assert.Single(test.Checks));
        Assert.Equal(1, test.Access.Disposals);
        Assert.False((await test.Command(UnitTestRunCommand.Start)).IsSuccessful);
        Assert.Single(test.Access.Commands);
    }

    [Fact]
    public async Task ReadFailureRetainsRealDataDeduplicatesDiagnosticsAndRecoversWithoutCommands()
    {
        await using var test = new Fixture(TimeSpan.FromMilliseconds(5));
        await test.Prepare();
        test.Access.ReadError = new AdsAccessException("Read", AdsErrorCode.ClientSyncTimeOut);
        await Until(() => test.Service.Health.State == AccessHealthState.RevalidationRequired);
        await Until(() => test.Access.Reads >= 5);
        Assert.Equal(Ready, test.Service.CurrentObservation);
        Assert.Single(test.Diagnostics, diagnostic => diagnostic.Code == "Beckhoff.RunControl.AccessFailed");
        Assert.NotEmpty(test.Checks);
        test.Access.ReadError = null;
        test.Access.Value = Ready with { State = UnitTestLifecycleState.Running, ExecutionStatus = UnitTestExecutionStatus.Running };
        await Until(() => test.Service.CurrentObservation.State == UnitTestLifecycleState.Running);
        Assert.Equal(AccessHealthState.Healthy, test.Service.Health.State);
        Assert.Single(test.Diagnostics, diagnostic => diagnostic.Code == "Beckhoff.RunControl.Recovered");
        Assert.Empty(test.Access.Commands);
    }

    [Fact]
    public async Task UnavailablePlcDoesNotPollAdsOrExpireAndReturnToRunRevalidates()
    {
        await using var test = new Fixture(TimeSpan.FromMilliseconds(5));
        await test.Prepare();
        test.Available = false;
        await Until(() => test.Access.Disposals == 1);
        var reads = test.Access.Reads;
        await Task.Delay(50);
        Assert.Equal(reads, test.Access.Reads);
        Assert.Equal(Ready, test.Service.CurrentObservation);
        Assert.Equal(AccessHealthState.RevalidationRequired, test.Service.Health.State);
        Assert.Single(test.Diagnostics);
        test.Available = true;
        await Until(() => test.Service.Health.State == AccessHealthState.Healthy);
        Assert.True(test.Access.Prepares >= 2);
        Assert.Empty(test.Access.Commands);
    }

    [Theory]
    [InlineData(AdsErrorCode.DeviceSymbolVersionInvalid)]
    [InlineData(AdsErrorCode.DeviceSymbolNotFound)]
    public async Task ChangedSymbolsAreRevalidatedWithoutCommands(AdsErrorCode code)
    {
        await using var test = new Fixture(TimeSpan.FromMilliseconds(5));
        await test.Prepare();
        test.Access.ReadError = new AdsAccessException("Symbols changed", code);
        await Until(() => test.Access.Disposals > 0);
        test.Access.ReadError = null;
        await Until(() => test.Service.Health.State == AccessHealthState.Healthy);
        Assert.True(test.Access.Prepares > 1);
        Assert.Empty(test.Checks);
        Assert.Empty(test.Access.Commands);
    }

    [Fact]
    public async Task InvalidContractDoesNotPublishZerosOrRetryWrites()
    {
        await using var test = new Fixture(TimeSpan.FromMilliseconds(5));
        await test.Prepare();
        test.Access.ReadError = new InvalidDataException("Wrong execution layout.");
        await Until(() => test.Service.Health.State == AccessHealthState.Invalidated);
        var reads = test.Access.Reads;
        await Task.Delay(40);
        Assert.Equal(reads, test.Access.Reads);
        Assert.Equal(Ready, test.Service.CurrentObservation);
        Assert.False((await test.Command(UnitTestRunCommand.Reset)).IsSuccessful);
        Assert.Single(test.Diagnostics);
        Assert.Empty(test.Access.Commands);
    }

    [Fact]
    public async Task FailedInitialValidationRemainsUnknownAndClosesAccess()
    {
        await using var test = new Fixture();
        test.Access.PrepareError = new InvalidDataException("Unsupported contract.");
        Assert.False((await test.PrepareResult()).IsSuccessful);
        Assert.Equal(UnitTestLifecycleState.Unknown, test.Service.CurrentObservation.State);
        Assert.Empty(test.Observations);
        Assert.Equal(0, test.Access.Reads);
        Assert.Equal(1, test.Access.Disposals);
        Assert.Equal(AccessHealthProblem.ContractInvalid, test.Service.Health.Problem);
    }

    [Fact]
    public async Task ReplacedRuntimeContextCannotReceiveCommands()
    {
        await using var test = new Fixture();
        await test.Prepare();
        test.Current = false;
        Assert.False((await test.Command(UnitTestRunCommand.Start)).IsSuccessful);
        Assert.Equal(AccessHealthProblem.ContextLost, test.Service.Health.Problem);
        Assert.Empty(test.Access.Commands);
    }

    [Fact]
    public async Task DifferentCommunicationSessionCannotReceiveCommands()
    {
        await using var test = new Fixture();
        await test.Prepare();
        var stale = test.Context with { Session = new(new("different"), DateTimeOffset.UtcNow) };
        Assert.False((await test.Service.StartAsync(stale, default)).IsSuccessful);
        Assert.Empty(test.Access.Commands);
        Assert.Equal(AccessHealthState.Healthy, test.Service.Health.State);
    }

    [Fact]
    public async Task CanceledPreparationClosesAccessAndCannotPublishLateRead()
    {
        await using var test = new Fixture();
        using var cancellation = new CancellationTokenSource();
        test.Access.Read = _ => { cancellation.Cancel(); return Task.FromResult(Ready); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.PrepareResult(cancellation.Token));
        Assert.Equal(1, test.Access.Disposals);
        Assert.Empty(test.Observations);
        Assert.Equal(UnitTestLifecycleState.Unknown, test.Service.CurrentObservation.State);
    }

    [Fact]
    public async Task ReleaseCancelsPendingReadAndDiscardsLateCompletion()
    {
        await using var test = new Fixture(TimeSpan.FromMilliseconds(5));
        await test.Prepare();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Access.Read = async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            return Ready with { State = UnitTestLifecycleState.Completed };
        };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True((await test.Service.ReleaseCommunicationAsync(default)).IsSuccessful);
        Assert.Equal(1, test.Access.Disposals);
        Assert.Equal(UnitTestLifecycleState.Unknown, test.Service.CurrentObservation.State);
        Assert.Single(test.Observations);
        Assert.Equal(AccessHealthState.NotEstablished, test.Service.Health.State);
    }

    [Fact]
    public async Task NewCommunicationClosesOldAccessBeforeOpeningAndDoesNotInheritObservation()
    {
        await using var test = new Fixture();
        await test.Prepare();
        var old = test.Access;
        test.Access = new FakeAccess { PrepareError = new InvalidDataException("Changed contract.") };
        test.BeforeOpen = () => Assert.Equal(1, old.Disposals);
        Assert.False((await test.PrepareResult()).IsSuccessful);
        Assert.Equal(UnitTestLifecycleState.Unknown, test.Service.CurrentObservation.State);
        Assert.Equal(1, test.Access.Disposals);
    }

    [Fact]
    public async Task CleanupFailureIsNotReportedAsSuccess()
    {
        await using var test = new Fixture();
        await test.Prepare();
        test.Access.DisposeError = new IOException("Release failed.");
        Assert.False((await test.Service.ReleaseCommunicationAsync(default)).IsSuccessful);
        Assert.Equal(ServiceState.CleanupRequired, test.Service.State);
        Assert.False((await test.Command(UnitTestRunCommand.Start)).Value!.Accepted);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task QueuedCommandCannotOvertakeReadOrWriteAfterRelease()
    {
        await using var test = new Fixture(TimeSpan.FromMilliseconds(5));
        await test.Prepare();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Access.Read = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Ready;
        };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var command = test.Command(UnitTestRunCommand.Start);
        Assert.False(command.IsCompleted);
        Assert.Empty(test.Access.Commands);
        Assert.True((await test.Service.ReleaseCommunicationAsync(default)).IsSuccessful);
        Assert.False((await command).IsSuccessful);
        Assert.Empty(test.Access.Commands);
    }

    [Fact]
    public async Task CanceledQueuedCommandDoesNotWrite()
    {
        await using var test = new Fixture(TimeSpan.FromMilliseconds(5));
        await test.Prepare();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Access.Read = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Ready;
        };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource();
        var command = test.Service.ResetAsync(test.Context, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
        Assert.Empty(test.Access.Commands);
    }

    [Fact]
    public async Task RepeatedReadAndCleanupFailureDoNotFloodDiagnostics()
    {
        await using var test = new Fixture(TimeSpan.FromMilliseconds(5));
        await test.Prepare();
        test.Access.ReadError = new AdsAccessException("Read", AdsErrorCode.TargetMachineNotFound);
        test.Access.DisposeError = new IOException("Offline cleanup.");
        await Until(() => test.Access.Disposals >= 4);
        Assert.Single(test.Diagnostics, diagnostic => diagnostic.Code == "Beckhoff.RunControl.AccessFailed");
        Assert.Single(test.Diagnostics, diagnostic => diagnostic.Code == "Beckhoff.RunControl.CleanupFailed");
        test.Available = false;
        await Until(() => test.Service.Health.Message.Contains("currently unavailable"));
    }

    [Theory]
    [InlineData(AdsErrorCode.DeviceInvalidState)]
    [InlineData(AdsErrorCode.MailBoxError)]
    public async Task InitialTransportFailureRequestsEarlySystemCheck(AdsErrorCode code)
    {
        await using var test = new Fixture();
        test.Access.PrepareError = new AdsAccessException("Prepare", code);
        Assert.False((await test.PrepareResult()).IsSuccessful);
        Assert.Equal(code, Assert.Single(test.Checks));
        Assert.Equal(UnitTestLifecycleState.Unknown, test.Service.CurrentObservation.State);
    }

    [Fact]
    public async Task RouterRegistrationFailureIsReportedAndCanRecover()
    {
        await using var test = new Fixture();
        test.BeforeOpen = () => throw new TwinCAT.Ads.Server.LoopbackNotRegisteredException();
        Assert.False((await test.PrepareResult()).IsSuccessful);
        Assert.Equal(AccessHealthState.RevalidationRequired, test.Service.Health.State);
        Assert.Equal(UnitTestLifecycleState.Unknown, test.Service.CurrentObservation.State);
        Assert.Single(test.Diagnostics);
        test.BeforeOpen = null;
        await test.Prepare();
    }

    [Fact]
    public async Task UnexpectedReadFailureInvalidatesObservationAndRejectsCommands()
    {
        await using var test = new Fixture(TimeSpan.FromMilliseconds(5));
        await test.Prepare();
        test.Access.ReadError = new FormatException("Unexpected decoder failure.");
        await Until(() => test.Service.State == ServiceState.Invalidated);
        var reads = test.Access.Reads;
        await Task.Delay(40);
        Assert.Equal(reads, test.Access.Reads);
        Assert.Equal(Ready, test.Service.CurrentObservation);
        Assert.Equal(AccessHealthState.Invalidated, test.Service.Health.State);
        Assert.Equal("Beckhoff.RunControl.ObserverFailed", Assert.Single(test.Diagnostics).Code);
        Assert.False((await test.Command(UnitTestRunCommand.Reset)).IsSuccessful);
        Assert.Empty(test.Access.Commands);
        Assert.True((await test.Service.ReleaseCommunicationAsync(default)).IsSuccessful);
        Assert.Equal(1, test.Access.Disposals);
    }

    [Fact]
    public async Task ThrowingPreparationObserverDoesNotRetainDetachedObservation()
    {
        await using var test = new Fixture();
        EventHandler<UnitTestRunControlObservationChangedEventArgs> observer = (_, _) =>
            throw new InvalidOperationException("Observer failed.");
        test.Service.RunControlObservationChanged += observer;
        Assert.False((await test.PrepareResult()).IsSuccessful);
        Assert.Equal(UnitTestLifecycleState.Unknown, test.Service.CurrentObservation.State);
        Assert.Equal(1, test.Access.Disposals);
        Assert.False((await test.Command(UnitTestRunCommand.Reset)).IsSuccessful);
        test.Service.RunControlObservationChanged -= observer;
        await test.Prepare();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly BeckhoffUnitTestLifecycleService Service;
        internal FakeAccess Access = new();
        internal ObservationStrategy Strategy = ObservationStrategy.Polling;
        internal volatile bool Current = true, Available = true;
        internal long BindingGeneration;
        internal Action? BeforeOpen;
        internal ConcurrentQueue<Diagnostic> Diagnostics = new();
        internal ConcurrentQueue<AdsErrorCode> Checks = new();
        internal ConcurrentQueue<UnitTestRunControlObservation> Observations = new();
        internal UnitTestContext Context = new(new(new("communication"), DateTimeOffset.UtcNow),
            new(ProjectOwnershipMode.ExternallyManaged, null, null), new(new("runtime"), "Local", "Local"), new(new("192.0.2.10.1.1"), "Local"));
        private readonly TimeSpan interval;

        internal Fixture(TimeSpan? interval = null)
        {
            this.interval = interval ?? TimeSpan.FromMinutes(1);
            Service = new(new AccessProfileContext(NullLoggerFactory.Instance, TimeProvider.System, AppContext.BaseDirectory,
                Path.GetTempPath(), new OperationalLoggingOptions(Path.GetTempPath(), LogLevel.Information, 1048576, 2)), endpoint =>
                {
                    BeforeOpen?.Invoke();
                    Assert.Equal("192.0.2.10.1.1", endpoint.NetId);
                    Assert.Equal(852, endpoint.Port);
                    return Access;
                });
            Service.DiagnosticEventHandler += (_, args) => Diagnostics.Enqueue(args.Diagnostic);
            Service.RunControlObservationChanged += (_, args) => Observations.Enqueue(args.Observation);
        }

        internal Task<PLCSIM.UnitTest.Common.Operations.OperationResult> PrepareResult(CancellationToken token = default) =>
            Service.PrepareAsync(Context, new("Test.RunControl", interval) { Strategy = Strategy },
                new("192.0.2.10.1.1", 852, () => Current, () => Available, Checks.Enqueue)
                { BindingGeneration = () => Interlocked.Read(ref BindingGeneration) }, token);
        internal async Task Prepare()
        {
            var result = await PrepareResult();
            Assert.True(result.IsSuccessful, result.Message);
        }
        internal Task<PLCSIM.UnitTest.Common.Operations.OperationResult<TestRunCommandResult>> Command(UnitTestRunCommand command, CancellationToken token = default) => command switch
        {
            UnitTestRunCommand.Start => Service.StartAsync(Context, token),
            UnitTestRunCommand.Abort => Service.AbortAsync(Context, token),
            _ => Service.ResetAsync(Context, token)
        };
        public async ValueTask DisposeAsync() => await Service.CloseAsync();
    }

    private sealed class FakeAccess : IRunControlAccess, IRunControlNotificationAccess
    {
        internal RunControlObservationSignal Signal = new();
        internal int Registrations, Requests;
        internal TimeSpan SamplingInterval, LastFallback;
        public Task RegisterNotificationsAsync(TimeSpan interval, CancellationToken token)
        {
            Assert.True(Prepares > 0);
            Assert.Equal(0, Reads);
            SamplingInterval = interval;
            Registrations++;
            return Task.CompletedTask;
        }
        public Task WaitForObservationAsync(TimeSpan fallback, CancellationToken token)
        {
            LastFallback = fallback;
            return Signal.WaitAsync(fallback, TimeProvider.System, token);
        }
        public void RequestObservation() { Requests++; Signal.Request(); }
        internal string? Root;
        internal int Reads, Prepares, Disposals;
        internal Exception? PrepareError, ReadError, WriteError, DisposeError;
        internal Action? OnWrite;
        internal UnitTestRunControlObservation Value = Ready;
        internal Func<CancellationToken, Task<UnitTestRunControlObservation>>? Read;
        internal ConcurrentQueue<UnitTestRunCommand> Commands = new();
        public Task PrepareAsync(string root, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Root = root;
            Interlocked.Increment(ref Prepares);
            if (PrepareError is not null) throw PrepareError;
            return Task.CompletedTask;
        }
        public Task<UnitTestRunControlObservation> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Reads);
            if (Registrations != 0) Signal.BeginRead();
            if (ReadError is not null) throw ReadError;
            return Read?.Invoke(token) ?? Task.FromResult(Value);
        }
        public Task WriteAsync(UnitTestRunCommand command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Commands.Enqueue(command);
            OnWrite?.Invoke();
            if (WriteError is not null) throw WriteError;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            Signal.Close();
            if (DisposeError is not null) throw DisposeError;
            return ValueTask.CompletedTask;
        }
    }
}
