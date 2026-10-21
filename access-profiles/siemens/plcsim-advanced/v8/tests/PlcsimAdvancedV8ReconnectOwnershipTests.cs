using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8;

public sealed class PlcsimAdvancedV8ReconnectOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reconnect_releases_old_interface_before_creating_and_adopting_replacement(bool owned)
    {
        using var fixture = new Fixture(owned);
        var replacement = new FakeInterface("new");
        FakeManager.Next = replacement;

        var result = fixture.Reconnect();

        Assert.True(result.Succeeded);
        Assert.Equal(owned, result.Runtime!.IsOwned);
        Assert.Same(replacement, fixture.Prepared);
        Assert.Equal(1, fixture.Old.DisposeCount);
        Assert.Equal(0, fixture.Old.SubscriberCount);
        Assert.Equal(0, replacement.DisposeCount);
        Assert.Equal(2, replacement.SubscriberCount);
        Assert.Equal(new[]
        {
            "old:remove-operating", "old:remove-software", "old:dispose",
            "create", "new:add-operating", "new:add-software"
        }, FakeManager.Calls);
        Assert.Equal(0, FakeManager.UnregisterCount);
    }

    [Theory]
    [InlineData("ID")]
    [InlineData("Name")]
    [InlineData("OperatingState")]
    [InlineData("identity")]
    [InlineData("registration")]
    [InlineData("unavailable")]
    public void Failed_replacement_is_released_and_next_reconnect_can_use_retained_identity(string failure)
    {
        using var fixture = new Fixture();
        var replacement = new FakeInterface("failed") { Failure = failure };
        FakeManager.Next = failure == "unavailable" ? null : replacement;

        if (failure is "ID" or "Name" or "OperatingState")
        {
            var exception = Assert.Throws<TargetInvocationException>(() => fixture.Reconnect());
            Assert.Equal("Injected " + failure + " failure.", exception.GetBaseException().Message);
        }
        else
        {
            var result = fixture.Reconnect();
            Assert.False(result.Succeeded);
            var expectedCode = failure switch
            {
                "identity" => "RuntimeIdentityChanged",
                "registration" => "RuntimeEventRegistrationFailed",
                _ => "RuntimeInterfaceUnavailable"
            };
            Assert.EndsWith(expectedCode, result.Code, StringComparison.Ordinal);
        }

        Assert.Null(fixture.Prepared);
        Assert.Equal(1, fixture.Old.DisposeCount);
        Assert.Equal(0, fixture.Old.SubscriberCount);
        Assert.Equal(failure == "unavailable" ? 0 : 1, replacement.DisposeCount);
        Assert.Equal(0, replacement.SubscriberCount);
        if (failure == "registration")
        {
            Assert.True(FakeManager.Calls.IndexOf("failed:remove-operating")
                < FakeManager.Calls.IndexOf("failed:dispose"));
            Assert.True(FakeManager.Calls.IndexOf("failed:remove-software")
                < FakeManager.Calls.IndexOf("failed:dispose"));
        }

        var retry = new FakeInterface("retry");
        FakeManager.Next = retry;
        var recovered = fixture.Reconnect();

        Assert.True(recovered.Succeeded);
        Assert.Equal("0", recovered.Runtime!.Id);
        Assert.Equal("PlcUnderTest", recovered.Runtime.InstanceName);
        Assert.Same(retry, fixture.Prepared);
        Assert.Equal(0, retry.DisposeCount);
        Assert.Equal(2, retry.SubscriberCount);
        Assert.Equal(1, fixture.Old.DisposeCount);
        Assert.Equal(failure == "unavailable" ? 0 : 1, replacement.DisposeCount);
        Assert.Equal(0, FakeManager.UnregisterCount);
    }

    // Exercise the production worker without loading a Siemens assembly or starting a PLC.
    private sealed class Fixture : IDisposable
    {
        private readonly Type contextType;
        private readonly object context;
        public FakeInterface Old { get; } = new("old");

        public Fixture(bool owned = false)
        {
            FakeManager.Calls.Clear();
            FakeManager.UnregisterCount = 0;
            FakeManager.Next = null;
            var assembly = Assembly.LoadFrom(Path.Combine(
                AppContext.BaseDirectory,
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker.exe"));
            var program = assembly.GetType(
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker.Program", true)!;
            contextType = program.GetNestedType("RuntimeApiContext", BindingFlags.NonPublic)!;
            var eventType = program.GetNestedType("RuntimeStateEvent", BindingFlags.NonPublic)!;
            var publisher = Expression.Lambda(
                typeof(Action<>).MakeGenericType(eventType),
                Expression.Empty(),
                Expression.Parameter(eventType)).Compile();
            context = Activator.CreateInstance(contextType,
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { typeof(FakeManager), publisher, NullLogger.Instance }, null)!;
            Set("preparedInstance", Old);
            Set("preparedInstanceIsOwned", owned);
            var snapshot = new RuntimeTarget("0", "PlcUnderTest", "Run", owned);
            Set("preparedRuntimeSnapshot", snapshot);
            contextType.GetMethod("RegisterPreparedInstanceEvents",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context, new object[] { snapshot });
            FakeManager.Calls.Clear();
        }

        public object? Prepared => contextType.GetField("preparedInstance",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context);

        public RuntimeObservationResult Reconnect() =>
            (RuntimeObservationResult)contextType.GetMethod("RevalidateRuntime")!
                .Invoke(context, new object[] { "0", "PlcUnderTest" })!;

        private void Set(string name, object? value) =>
            contextType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, value);

        public void Dispose()
        {
            contextType.GetMethod("UnregisterPreparedInstanceEvents",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context, null);
            (Prepared as IDisposable)?.Dispose();
            Set("preparedInstance", null);
        }
    }

    public static class FakeManager
    {
        public static List<string> Calls { get; } = new();
        public static FakeInterface? Next { get; set; }
        public static int UnregisterCount { get; set; }
        public static bool IsRuntimeManagerAvailable => true;
        public static bool IsInitialized => true;

        public static object? CreateInterface(int id)
        {
            Assert.Equal(0, id);
            Calls.Add("create");
            return Next;
        }
    }

    public sealed class FakeInterface(string label) : IDisposable
    {
        private Action<object, object, object, object, object>? operating;
        private Action<object, object>? software;
        public string? Failure { get; init; }
        public int DisposeCount { get; private set; }
        public int SubscriberCount => (operating?.GetInvocationList().Length ?? 0)
            + (software?.GetInvocationList().Length ?? 0);
        public string ID => Read("ID", Failure == "identity" ? "99" : "0");
        public string Name => Read("Name", "PlcUnderTest");
        public string OperatingState => Read("OperatingState", "Run");

        private string Read(string property, string value) => Failure == property
            ? throw new InvalidOperationException("Injected " + property + " failure.")
            : value;

        public event Action<object, object, object, object, object> OnOperatingStateChanged
        {
            add { FakeManager.Calls.Add(label + ":add-operating"); operating += value; }
            remove { FakeManager.Calls.Add(label + ":remove-operating"); operating -= value; }
        }

        public event Action<object, object> OnSoftwareConfigurationChanged
        {
            add
            {
                FakeManager.Calls.Add(label + ":add-software");
                if (Failure == "registration")
                    throw new InvalidOperationException("Injected registration failure.");
                software += value;
            }
            remove { FakeManager.Calls.Add(label + ":remove-software"); software -= value; }
        }

        public void Dispose()
        {
            FakeManager.Calls.Add(label + ":dispose");
            DisposeCount++;
        }

        public void UnregisterInstance()
        {
            FakeManager.UnregisterCount++;
            throw new InvalidOperationException("Reconnect must never unregister the PLC instance.");
        }
    }
}
