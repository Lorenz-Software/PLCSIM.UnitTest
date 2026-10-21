using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8;
public sealed class PlcsimAdvancedV8PlcCycleObservationTests
{
    private const string WorkerAssemblyFileName =
        "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker.exe";
    [Fact]
    public void Production_worker_accepts_the_managed_v8_plc_cycle_surface()
    {
        var runtime = new FakePlcCycleRuntime();
        var adapter = WorkerReflection.CreateAdapter(runtime);
        Assert.NotNull(adapter);
    }
    [Theory]
    [MemberData(nameof(UnavailableManagedSurfaces))]
    public void Production_worker_rejects_incomplete_managed_plc_cycle_surfaces(
        object runtime,
        string expectedFailure)
    {
        var result = WorkerReflection.TryCreateAdapter(runtime);
        Assert.False(result.Succeeded);
        Assert.Null(result.Adapter);
        Assert.Contains(expectedFailure, result.Failure, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData(FakeRuntimeErrorCode.OK, true, false, false)]
    [InlineData(FakeRuntimeErrorCode.Timeout, false, true, false)]
    [InlineData(FakeRuntimeErrorCode.InvalidState, false, false, true)]
    public void Production_adapter_maps_managed_wait_results(
        FakeRuntimeErrorCode errorCode,
        bool expectedSignaled,
        bool expectedTimedOut,
        bool expectedFailed)
    {
        var runtime = new FakePlcCycleRuntime();
        runtime.EnqueueWaitResult(errorCode);
        var adapter = WorkerReflection.CreateAdapter(runtime);
        var wait = WorkerReflection.Invoke(adapter, "Wait", 37u);
        Assert.Equal(expectedSignaled, WorkerReflection.Read<bool>(wait, "EventSignaled"));
        Assert.Equal(expectedTimedOut, WorkerReflection.Read<bool>(wait, "TimedOut"));
        Assert.Equal(expectedFailed, WorkerReflection.Read<bool>(wait, "Failed"));
        Assert.Equal(37u, runtime.LastWaitTimeoutMs);
    }
    [Fact]
    public void Production_context_compensates_a_registration_failure()
    {
        var runtime = new FakePlcCycleRuntime
        {
            RegisterException = new FakeRuntimeException("registration failed", "RegistrationDenied")
        };
        var context = WorkerReflection.CreateRuntimeContext(runtime);
        var enable = WorkerReflection.Invoke(context, "EnablePlcCycleObservation");
        Assert.False(WorkerReflection.Read<bool>(enable, "Succeeded"));
        Assert.Equal("RegistrationDenied", WorkerReflection.Read<string>(enable, "ErrorCode"));
        Assert.False(runtime.IsSendSyncEventInDefaultModeEnabled);
        Assert.Equal(1, runtime.RegisterCount);
        Assert.Equal(0, runtime.UnregisterCount);
    }
    [Fact]
    public void Production_context_distinguishes_signal_timeout_and_wait_failure()
    {
        var runtime = new FakePlcCycleRuntime();
        runtime.EnqueueWaitResult(FakeRuntimeErrorCode.OK);
        runtime.EnqueueWaitResult(FakeRuntimeErrorCode.Timeout);
        runtime.EnqueueWaitResult(FakeRuntimeErrorCode.InvalidState);
        var context = WorkerReflection.CreateEnabledRuntimeContext(runtime);
        var signaled = WorkerReflection.Invoke(
            context,
            "WaitForPlcCycleObservationWakeup",
            CancellationToken.None);
        var timedOut = WorkerReflection.Invoke(
            context,
            "WaitForPlcCycleObservationWakeup",
            CancellationToken.None);
        var failed = WorkerReflection.Invoke(
            context,
            "WaitForPlcCycleObservationWakeup",
            CancellationToken.None);
        Assert.True(WorkerReflection.Read<bool>(signaled, "Observe"));
        Assert.False(WorkerReflection.Read<bool>(signaled, "Faulted"));
        Assert.False(WorkerReflection.Read<bool>(timedOut, "Observe"));
        Assert.False(WorkerReflection.Read<bool>(timedOut, "Faulted"));
        Assert.True(WorkerReflection.Read<bool>(failed, "Faulted"));
        Assert.Equal(
            "PlcsimAdvanced.V8.Worker.PlcCycleObservationWaitFailed",
            WorkerReflection.Read<string>(failed, "Code"));
    }
    [Fact]
    public async Task Production_context_observes_cancellation_after_a_managed_wait_returns()
    {
        var runtime = new FakePlcCycleRuntime
        {
            BlockWait = true
        };
        var context = WorkerReflection.CreateEnabledRuntimeContext(runtime);
        using var cancellation = new CancellationTokenSource();
        var waitTask = Task.Run(
            () => WorkerReflection.Invoke(
                context,
                "WaitForPlcCycleObservationWakeup",
                cancellation.Token));
        Assert.True(runtime.WaitEntered.Wait(TimeSpan.FromSeconds(2)));
        cancellation.Cancel();
        runtime.ReleaseWait.Set();
        var result = await waitTask;
        Assert.True(WorkerReflection.Read<bool>(result, "StopRequested"));
        Assert.False(WorkerReflection.Read<bool>(result, "Observe"));
        Assert.False(WorkerReflection.Read<bool>(result, "Faulted"));
    }
    [Fact]
    public void Production_context_can_enable_and_reset_plc_cycle_observation_repeatedly()
    {
        var runtime = new FakePlcCycleRuntime();
        var context = WorkerReflection.CreateRuntimeContext(runtime);
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var enable = WorkerReflection.Invoke(context, "EnablePlcCycleObservation");
            Assert.True(WorkerReflection.Read<bool>(enable, "Succeeded"));
            Assert.True(runtime.IsSendSyncEventInDefaultModeEnabled);
            var reset = WorkerReflection.Invoke(context, "TryResetCommunicationState", false);
            Assert.True(Assert.IsType<bool>(reset));
            Assert.False(runtime.IsSendSyncEventInDefaultModeEnabled);
        }
        Assert.Equal(2, runtime.RegisterCount);
        Assert.Equal(2, runtime.UnregisterCount);
    }
    [Fact]
    public void Production_context_keeps_the_same_plc_cycle_registration_across_stop_and_start()
    {
        var runtime = new FakePlcCycleRuntime { OperatingState = "Stop" };
        runtime.EnqueueWaitResult(FakeRuntimeErrorCode.Timeout);
        runtime.EnqueueWaitResult(FakeRuntimeErrorCode.OK);
        var context = WorkerReflection.CreateEnabledRuntimeContext(runtime);
        var stopped = WorkerReflection.Invoke(
            context,
            "WaitForPlcCycleObservationWakeup",
            CancellationToken.None);
        Assert.False(WorkerReflection.Read<bool>(stopped, "Observe"));
        Assert.True(runtime.IsSendSyncEventInDefaultModeEnabled);
        Assert.Equal(1, runtime.RegisterCount);
        Assert.Equal(0, runtime.UnregisterCount);
        runtime.OperatingState = "Run";
        var running = WorkerReflection.Invoke(
            context,
            "WaitForPlcCycleObservationWakeup",
            CancellationToken.None);
        Assert.True(WorkerReflection.Read<bool>(running, "Observe"));
        Assert.True(runtime.IsSendSyncEventInDefaultModeEnabled);
        Assert.Equal(1, runtime.RegisterCount);
        Assert.Equal(0, runtime.UnregisterCount);
    }
    [Fact]
    public void Production_context_does_not_schedule_plc_cycle_observation_for_backlog_or_release_state()
    {
        var runtime = new FakePlcCycleRuntime();
        runtime.EnqueueWaitResult(FakeRuntimeErrorCode.Timeout);
        var context = WorkerReflection.CreateEnabledRuntimeContext(runtime);
        WorkerReflection.Set(context, "communicationObservationStrategy", "PlcCycle");
        WorkerReflection.Set(context, "eventStreamBacklogKnown", true);
        WorkerReflection.Set(context, "eventStreamReleasePending", true);
        var wakeup = WorkerReflection.Invoke(
            context,
            "WaitForObservationWakeup",
            CancellationToken.None);
        Assert.False(WorkerReflection.Read<bool>(wakeup, "Observe"));
        Assert.False(WorkerReflection.Read<bool>(wakeup, "Faulted"));
        Assert.Equal(100u, runtime.LastWaitTimeoutMs);
        Assert.Equal(1, runtime.WaitCount);
    }
    [Fact]
    public void Production_context_observes_every_signaled_cycle_when_monitoring_interval_is_omitted()
    {
        var runtime = new FakePlcCycleRuntime();
        runtime.EnqueueWaitResult(FakeRuntimeErrorCode.OK);
        var context = WorkerReflection.CreateEnabledRuntimeContext(runtime);
        WorkerReflection.Set(context, "communicationObservationStrategy", "PlcCycle");
        WorkerReflection.Set(context, "communicationMonitoringInterval", null);
        var wakeup = WorkerReflection.Invoke(
            context,
            "WaitForObservationWakeup",
            CancellationToken.None);
        Assert.True(WorkerReflection.Read<bool>(wakeup, "Observe"));
        Assert.False(WorkerReflection.Read<bool>(wakeup, "Faulted"));
    }
    [Fact]
    public void Production_context_throttles_signaled_cycles_until_explicit_monitoring_interval_elapsed()
    {
        var runtime = new FakePlcCycleRuntime();
        runtime.EnqueueWaitResult(FakeRuntimeErrorCode.OK);
        runtime.EnqueueWaitResult(FakeRuntimeErrorCode.OK);
        var context = WorkerReflection.CreateEnabledRuntimeContext(runtime);
        WorkerReflection.Set(context, "communicationObservationStrategy", "PlcCycle");
        WorkerReflection.Set(context, "communicationMonitoringInterval", TimeSpan.FromMilliseconds(20));
        WorkerReflection.InvokeVoid(context, "ResetPlcCycleMonitoringInterval");
        var throttled = WorkerReflection.Invoke(
            context,
            "WaitForObservationWakeup",
            CancellationToken.None);
        Thread.Sleep(30);
        var elapsed = WorkerReflection.Invoke(
            context,
            "WaitForObservationWakeup",
            CancellationToken.None);
        Assert.False(WorkerReflection.Read<bool>(throttled, "Observe"));
        Assert.True(WorkerReflection.Read<bool>(elapsed, "Observe"));
    }
    [Theory]
    [InlineData("eventStreamBacklogKnown")]
    [InlineData("eventStreamReleasePending")]
    public void Production_context_bypasses_explicit_plc_cycle_throttle_while_draining(string activeState)
    {
        var runtime = new FakePlcCycleRuntime();
        runtime.EnqueueWaitResult(FakeRuntimeErrorCode.OK);
        var context = WorkerReflection.CreateEnabledRuntimeContext(runtime);
        WorkerReflection.Set(context, "communicationObservationStrategy", "PlcCycle");
        WorkerReflection.Set(context, "communicationMonitoringInterval", TimeSpan.FromSeconds(1));
        WorkerReflection.Set(context, activeState, true);
        WorkerReflection.InvokeVoid(context, "ResetPlcCycleMonitoringInterval");
        var wakeup = WorkerReflection.Invoke(
            context,
            "WaitForObservationWakeup",
            CancellationToken.None);
        Assert.True(WorkerReflection.Read<bool>(wakeup, "Observe"));
        Assert.False(WorkerReflection.Read<bool>(wakeup, "Faulted"));
    }
    public static TheoryData<object, string> UnavailableManagedSurfaces => new()
    {
        { new object(), "IsSendSyncEventInDefaultModeEnabled" },
        { new ReadOnlyDefaultModeRuntime(), "readable and writable Boolean" },
        { new WaitResultWithoutErrorCodeRuntime(), "does not expose an ErrorCode" }
    };
    public enum FakeRuntimeErrorCode
    {
        OK = 0,
        Timeout = -11,
        InvalidState = -12
    }
    private sealed class FakeSyncPointResult
    {
        public FakeSyncPointResult(FakeRuntimeErrorCode errorCode)
        {
            ErrorCode = errorCode;
        }
        public FakeRuntimeErrorCode ErrorCode { get; }
    }
    private sealed class FakePlcCycleRuntime
    {
        private readonly ConcurrentQueue<FakeRuntimeErrorCode> waitResults = new();
        public string ID => "0";
        public string Name => "PlcUnderTest";
        public string OperatingState { get; set; } = "Run";
        public event FakeOperatingStateChangedHandler? OnOperatingStateChanged
        {
            add { }
            remove { }
        }
        public bool IsSendSyncEventInDefaultModeEnabled { get; set; }
        public Exception? RegisterException { get; set; }
        public bool BlockWait { get; set; }
        public ManualResetEventSlim WaitEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseWait { get; } = new(false);
        public int RegisterCount { get; private set; }
        public int UnregisterCount { get; private set; }
        public int WaitCount { get; private set; }
        public uint LastWaitTimeoutMs { get; private set; }
        public void EnqueueWaitResult(FakeRuntimeErrorCode errorCode)
        {
            waitResults.Enqueue(errorCode);
        }
        public void RegisterOnSyncPointReachedEvent()
        {
            RegisterCount++;
            if (RegisterException is not null)
            {
                throw RegisterException;
            }
        }
        public FakeSyncPointResult WaitForOnSyncPointReachedEvent(uint timeoutMs)
        {
            WaitCount++;
            LastWaitTimeoutMs = timeoutMs;
            WaitEntered.Set();
            if (BlockWait)
            {
                ReleaseWait.Wait(TimeSpan.FromSeconds(2));
            }
            return new FakeSyncPointResult(
                waitResults.TryDequeue(out var result)
                    ? result
                    : FakeRuntimeErrorCode.Timeout);
        }
        public void UnregisterOnSyncPointReachedEvent()
        {
            UnregisterCount++;
        }
    }
    private delegate void FakeOperatingStateChangedHandler(
        object sender,
        object errorCode,
        object observedAt,
        object previousState,
        object currentState);
    private sealed class ReadOnlyDefaultModeRuntime
    {
        public bool IsSendSyncEventInDefaultModeEnabled => false;
        public void RegisterOnSyncPointReachedEvent()
        {
        }
        public FakeSyncPointResult WaitForOnSyncPointReachedEvent(uint timeoutMs)
        {
            return new FakeSyncPointResult(FakeRuntimeErrorCode.Timeout);
        }
        public void UnregisterOnSyncPointReachedEvent()
        {
        }
    }
    private sealed class WaitResultWithoutErrorCodeRuntime
    {
        public bool IsSendSyncEventInDefaultModeEnabled { get; set; }
        public void RegisterOnSyncPointReachedEvent()
        {
        }
        public object WaitForOnSyncPointReachedEvent(uint timeoutMs)
        {
            return new object();
        }
        public void UnregisterOnSyncPointReachedEvent()
        {
        }
    }
    private sealed class FakeRuntimeException : Exception
    {
        public FakeRuntimeException(string message, string runtimeErrorCode)
            : base(message)
        {
            RuntimeErrorCode = runtimeErrorCode;
        }
        public string RuntimeErrorCode { get; }
    }
    private static class WorkerReflection
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Lazy<Assembly> WorkerAssembly = new(LoadWorkerAssembly);
        private static readonly Lazy<Type> ProgramType = new(
            () => WorkerAssembly.Value.GetType(
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker.Program",
                throwOnError: true)!);
        public static AdapterCreationResult TryCreateAdapter(object runtime)
        {
            var adapterType = GetNestedType("PlcCycleObservationAdapter");
            var tryCreate = adapterType.GetMethod(
                "TryCreate",
                BindingFlags.Static | BindingFlags.Public)
                ?? throw new MissingMethodException(adapterType.FullName, "TryCreate");
            var arguments = new object?[] { runtime, null, null };
            var succeeded = Assert.IsType<bool>(tryCreate.Invoke(null, arguments));
            return new AdapterCreationResult(
                succeeded,
                arguments[1],
                Assert.IsType<string>(arguments[2]));
        }
        public static object CreateAdapter(object runtime)
        {
            var result = TryCreateAdapter(runtime);
            Assert.True(result.Succeeded, result.Failure);
            Assert.NotNull(result.Adapter);
            return result.Adapter;
        }
        public static object CreateRuntimeContext(object runtime)
        {
            var contextType = GetNestedType("RuntimeApiContext");
            var context = RuntimeHelpers.GetUninitializedObject(contextType);
            SetField(context, "preparedInstance", runtime);
            SetField(context, "logger", NullLogger.Instance);
            return context;
        }
        public static object CreateEnabledRuntimeContext(object runtime)
        {
            var context = CreateRuntimeContext(runtime);
            var enable = Invoke(context, "EnablePlcCycleObservation");
            Assert.True(Read<bool>(enable, "Succeeded"), Read<string>(enable, "Message"));
            return context;
        }
        public static void Set(object target, string fieldName, object? value)
        {
            SetField(target, fieldName, value);
        }
        public static object Invoke(object instance, string methodName, params object[] arguments)
        {
            var method = instance.GetType().GetMethod(methodName, InstanceFlags)
                ?? throw new MissingMethodException(instance.GetType().FullName, methodName);
            return method.Invoke(instance, arguments)
                ?? throw new InvalidOperationException($"{methodName} returned no result.");
        }
        public static void InvokeVoid(object instance, string methodName, params object[] arguments)
        {
            var method = instance.GetType().GetMethod(methodName, InstanceFlags)
                ?? throw new MissingMethodException(instance.GetType().FullName, methodName);
            method.Invoke(instance, arguments);
        }
        public static T Read<T>(object instance, string propertyName)
        {
            var property = instance.GetType().GetProperty(propertyName, InstanceFlags)
                ?? throw new MissingMemberException(instance.GetType().FullName, propertyName);
            return Assert.IsType<T>(property.GetValue(instance));
        }
        private static void SetField(object instance, string fieldName, object? value)
        {
            var field = instance.GetType().GetField(fieldName, InstanceFlags)
                ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);
            field.SetValue(instance, value);
        }
        private static Type GetNestedType(string name)
        {
            return ProgramType.Value.GetNestedType(name, BindingFlags.NonPublic)
                ?? throw new TypeLoadException($"Worker nested type '{name}' was not found.");
        }
        private static Assembly LoadWorkerAssembly()
        {
            var path = Path.Combine(AppContext.BaseDirectory, WorkerAssemblyFileName);
            Assert.True(File.Exists(path), $"Production V8 worker was not copied to '{path}'.");
            return Assembly.LoadFrom(path);
        }
    }
    private sealed record AdapterCreationResult(
        bool Succeeded,
        object? Adapter,
        string Failure);
}
