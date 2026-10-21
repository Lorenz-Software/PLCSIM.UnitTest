using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class XaeTransferCallTests
{
    private const string Target = "192.0.2.10.1.1";

    [Fact]
    public void ActivationOverridesDeveloperNetIdAndDoesNotSaveOrStartPlc()
    {
        var manager = new Manager();
        XaeProjectAccess.ExecuteActivation(manager, Target, default);
        Assert.Equal(Target, manager.NetId);
        Assert.Equal(["set-target", "read-target", "activate", "read-target", "restart-system"], manager.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TargetReadbackIsCheckedBeforeEachSystemMutation(bool beforeActivation)
    {
        var manager = new Manager { RejectTarget = beforeActivation, ChangeOnActivate = !beforeActivation };
        Assert.Throws<InvalidOperationException>(() => XaeProjectAccess.ExecuteActivation(manager, Target, default));
        Assert.DoesNotContain("restart-system", manager.Calls);
        Assert.Equal(!beforeActivation, manager.Calls.Contains("activate"));
    }

    [Fact]
    public void CancellationAfterActivationDoesNotRestartSystem()
    {
        using var cancellation = new CancellationTokenSource();
        var manager = new Manager { OnActivate = cancellation.Cancel };
        Assert.Throws<OperationCanceledException>(() => XaeProjectAccess.ExecuteActivation(manager, Target, cancellation.Token));
        Assert.DoesNotContain("restart-system", manager.Calls);
    }

    [Fact]
    public void LoginUsesExactFlagsChecksLoggedInAndLogsOffWithoutOtherPlcOperations()
    {
        var online = new Online();
        XaeProjectAccess.ExecuteLogin(online, default);
        Assert.Equal(260, online.Flags);
        Assert.Equal(["login", "is-logged-in", "logoff"], online.Calls);
    }

    [Fact]
    public void NonthrowingLoginIsNotSuccessWithoutOnlineEvidence()
    {
        var online = new Online { LoggedIn = false };
        Assert.Throws<IOException>(() => XaeProjectAccess.ExecuteLogin(online, default));
        Assert.Equal(["login", "is-logged-in", "logoff"], online.Calls);
    }

    [Fact]
    public void FailedLoginStillAttemptsOwnedLogoff()
    {
        var online = new Online { LoginError = new IOException("download rejected") };
        Assert.Throws<IOException>(() => XaeProjectAccess.ExecuteLogin(online, default));
        Assert.Equal(["login", "logoff"], online.Calls);
    }

    [Fact]
    public void FailedLoginAndFailedCleanupPreserveBothErrors()
    {
        var online = new Online { LoginError = new IOException("download rejected"), LogoffError = new IOException("logoff failed") };
        var error = Assert.Throws<AggregateException>(() => XaeProjectAccess.ExecuteLogin(online, default));
        Assert.Equal(2, error.InnerExceptions.Count);
    }

    [Fact]
    public void SuccessfulLoginCannotHideFailedLogoff()
    {
        var online = new Online { LogoffError = new IOException("logoff failed") };
        Assert.Throws<IOException>(() => XaeProjectAccess.ExecuteLogin(online, default));
    }

    [Fact]
    public void CancellationAfterLoginStillLogsOff()
    {
        using var cancellation = new CancellationTokenSource();
        var online = new Online { OnLogin = cancellation.Cancel };
        Assert.Throws<OperationCanceledException>(() => XaeProjectAccess.ExecuteLogin(online, cancellation.Token));
        Assert.Equal(["login", "logoff"], online.Calls);
    }

    [Fact]
    public void PrecancelledLoginDoesNotTouchVendor()
    {
        var online = new Online();
        Assert.Throws<OperationCanceledException>(() => XaeProjectAccess.ExecuteLogin(online, new CancellationToken(true)));
        Assert.Empty(online.Calls);
    }

    [Fact]
    public void OnlySelectedSystemManagerIsUsedIncludingInsideSolutionFolder()
    {
        var selected = new Project { FullName = @"C:\source\selected.tsproj" };
        var other = new Project { FullName = @"C:\source\other.tsproj" };
        var host = new Host { Solution = new Solution { Projects = new Projects { Values = [other,
            new Project { Kind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}",
                ProjectItems = new Items { Values = [new Item { SubProject = selected }] } }] } } };
        var target = EngineeringPlcTarget.FromXml(@"C:\source", selected.FullName, "selected", EngineeringFixture.PlcXml());
        XaeProjectAccess.WithSelectedManager(host, target, manager => Assert.Same(selected.Object, manager));
        Assert.Empty(other.Object.Calls);
        Assert.Throws<InvalidOperationException>(() => XaeProjectAccess.WithSelectedManager(host,
            target with { SystemProjectPath = @"C:\source\missing.tsproj" }, _ => Assert.Fail("Unexpected manager")));
    }

    [Fact]
    public void DownloadReacquiresNestedOnlineInterfaceWithoutChangingTarget()
    {
        var first = new Online();
        var second = new Online();
        var manager = new Manager { NetId = Target, Root = new Root(first) };
        var target = EngineeringPlcTarget.FromXml(@"C:\source", @"C:\source\system.tsproj", "system", EngineeringFixture.PlcXml());
        XaeProjectAccess.ExecuteDownload(manager, target, Target, default);
        manager.Root = new Root(second);
        XaeProjectAccess.ExecuteDownload(manager, target, Target, default);
        Assert.Equal(["login", "is-logged-in", "logoff"], first.Calls);
        Assert.Equal(first.Calls, second.Calls);
        Assert.Equal(["read-target", "lookup:TIPC^Tests", "read-target", "lookup:TIPC^Tests"], manager.Calls);
    }

    [Fact]
    public void DownloadRefusesChangedDestinationBeforeAcquiringOnlineInterface()
    {
        var manager = new Manager();
        var target = EngineeringPlcTarget.FromXml(@"C:\source", @"C:\source\system.tsproj", "system", EngineeringFixture.PlcXml());
        Assert.Throws<InvalidOperationException>(() => XaeProjectAccess.ExecuteDownload(manager, target, Target, default));
        Assert.Equal(["read-target"], manager.Calls);
    }

    private sealed class Root(object nested) : IXaeProjectRoot { public object NestedProject => nested; }

    public sealed class Host { public Solution Solution { get; set; } = new(); }
    public sealed class Solution { public Projects Projects { get; set; } = new(); }
    public sealed class Projects { public Project[] Values = []; public int Count => Values.Length; public Project Item(int i) => Values[i - 1]; }
    public sealed class Project
    {
        public string Kind { get; set; } = "system";
        public string FullName { get; set; } = "";
        public Manager Object { get; } = new();
        public Items ProjectItems { get; set; } = new();
    }
    public sealed class Items { public Item[] Values = []; public int Count => Values.Length; public Item Item(int i) => Values[i - 1]; }
    public sealed class Item { public Project? SubProject { get; set; } }
    public sealed class Manager : IXaeSystemTransfer
    {
        public string NetId = "192.0.2.99.1.1";
        public List<string> Calls = [];
        public bool RejectTarget, ChangeOnActivate;
        public Action? OnActivate;
        public void SetTargetNetId(string value) { Calls.Add("set-target"); if (!RejectTarget) NetId = value; }
        public string GetTargetNetId() { Calls.Add("read-target"); return NetId; }
        public void ActivateConfiguration() { Calls.Add("activate"); if (ChangeOnActivate) NetId = "changed"; OnActivate?.Invoke(); }
        public void StartRestartTwinCAT() => Calls.Add("restart-system");
        public object LookupTreeItem(string path) { Calls.Add("lookup:" + path); return Root!; }
        public object? Root;
    }
    private sealed class Online : IXaePlcOnline
    {
        internal List<string> Calls = [];
        internal int Flags;
        internal bool LoggedIn = true;
        internal Exception? LoginError, LogoffError;
        internal Action? OnLogin;
        public bool IsLoggedIn { get { Calls.Add("is-logged-in"); return LoggedIn; } }
        public int OnlineApplicationState => throw new NotSupportedException();
        public int OnlineOperationState => throw new NotSupportedException();
        public void Login(int flags) { Calls.Add("login"); Flags = flags; OnLogin?.Invoke(); if (LoginError is not null) throw LoginError; }
        public void Logoff() { Calls.Add("logoff"); if (LogoffError is not null) throw LogoffError; }
        public void Start() => throw new NotSupportedException();
        public void Stop() => throw new NotSupportedException();
        public void Reset(int type) => throw new NotSupportedException();
    }
}
