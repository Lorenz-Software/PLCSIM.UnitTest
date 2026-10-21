using Microsoft.CSharp.RuntimeBinder;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class XaeDynamicBindingTests
{
    private const string NetId = "192.0.2.10.1.1";

    [Fact]
    public void ActivationAndDownloadUseDynamicAdaptersWithoutVendorMetadata()
    {
        var manager = new Manager();
        var target = EngineeringPlcTarget.FromXml(@"C:\source", @"C:\source\system.tsproj", "system", EngineeringFixture.PlcXml());
        XaeProjectAccess.ExecuteActivation(manager, NetId, default);
        XaeProjectAccess.ExecuteDownload(manager, target, NetId, default);
        Assert.Equal(["set-target", "read-target", "activate", "read-target", "restart-system",
            "read-target", "lookup:TIPC^Tests"], manager.Calls);
        Assert.Equal(260, manager.Root.NestedProject.Flags);
        Assert.Equal(["login", "is-logged-in", "logoff"], manager.Root.NestedProject.Calls);
    }

    [Fact]
    public void DynamicLoginFailureRetainsVendorErrorAndAttemptsLogoff()
    {
        var error = new System.Runtime.InteropServices.COMException("download rejected", unchecked((int)0x80004005));
        var online = new Online { LoginError = error };
        Assert.Same(error, Assert.Throws<System.Runtime.InteropServices.COMException>(() =>
            XaeProjectAccess.ExecuteLogin(XaeVendorAdapters.Online(online), default)));
        Assert.Equal(["login", "logoff"], online.Calls);
    }

    [Fact]
    public void DynamicNonthrowingLoginStillRequiresOnlineEvidence()
    {
        var online = new Online { LoggedIn = false };
        Assert.Throws<IOException>(() => XaeProjectAccess.ExecuteLogin(XaeVendorAdapters.Online(online), default));
        Assert.Equal(["login", "is-logged-in", "logoff"], online.Calls);
    }

    [Fact]
    public void MissingDispatchMemberIsNotTreatedAsSuccess()
    {
        Assert.Throws<RuntimeBinderException>(() => XaeVendorAdapters.System(new object()).ActivateConfiguration());
        Assert.Throws<RuntimeBinderException>(() => XaeVendorAdapters.ProjectRoot(new object()).NestedProject);
        var error = Assert.Throws<AggregateException>(() => XaeProjectAccess.ExecuteLogin(XaeVendorAdapters.Online(new object()), default));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.All(error.InnerExceptions, failure => Assert.IsType<RuntimeBinderException>(failure));
    }

    [Theory]
    [InlineData("3.1.4026.26")]
    [InlineData("3.1.4026.27")]
    [InlineData("3.1.4026.30")]
    public void EffectiveVersionAcceptsCompatiblePatchesWithoutAssigningAVersion(string version)
    {
        var host = new Host(new RemoteManager(version));
        Assert.Equal(version, XaeProjectAccess.ValidateLoadedEngineeringVersion(host));
        Assert.Equal("TcRemoteManager", host.RequestedObject);
        Assert.True(host.Manager.Disposed);
        Assert.Equal(0, host.Manager.VersionAssignments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("3.1.4024.78")]
    [InlineData("3.1.4028.0")]
    public void UnsupportedOrUnknownEffectiveVersionFailsAndReleasesReference(string? version)
    {
        var host = new Host(new RemoteManager(version));
        Assert.Throws<InvalidDataException>(() => XaeProjectAccess.ValidateLoadedEngineeringVersion(host));
        Assert.True(host.Manager.Disposed);
    }

    [Fact]
    public void NormallyReturningVersionSetterDoesNotEstablishExactSelection()
    {
        var host = new Host(new RemoteManager("3.1.4026.27"));
        host.Manager.Version = "3.1.4026.9999";
        Assert.Throws<InvalidDataException>(() => XaeProjectAccess.ValidateLoadedEngineeringVersion(host, "3.1.4026.9999"));
        Assert.True(host.Manager.Disposed);
        Assert.Equal("3.1.4026.27", XaeProjectAccess.ValidateLoadedEngineeringVersion(new Host(new RemoteManager("3.1.4026.27")), "3.1.4026.27"));
    }

    [Fact]
    public void MissingRemoteManagerMemberFailsAndReleasesReference()
    {
        var manager = new MissingVersion();
        Assert.Throws<RuntimeBinderException>(() => XaeProjectAccess.ValidateLoadedEngineeringVersion(new MissingVersionHost(manager)));
        Assert.True(manager.Disposed);
    }

    [Fact]
    public void VendorVersionReadFailureIsRetainedAndReleasesReference()
    {
        var failure = new System.Runtime.InteropServices.COMException("version query failed");
        var host = new Host(new RemoteManager("3.1.4026.27") { ReadError = failure });
        Assert.Same(failure, Assert.Throws<System.Runtime.InteropServices.COMException>(() =>
            XaeProjectAccess.ValidateLoadedEngineeringVersion(host)));
        Assert.True(host.Manager.Disposed);
    }

    [Fact]
    public void DynamicCancellationAfterLoginStillReleasesEngineeringLogin()
    {
        using var cancellation = new CancellationTokenSource();
        var online = new Online { OnLogin = cancellation.Cancel };
        Assert.Throws<OperationCanceledException>(() =>
            XaeProjectAccess.ExecuteLogin(XaeVendorAdapters.Online(online), cancellation.Token));
        Assert.Equal(["login", "logoff"], online.Calls);
    }

    [Fact]
    public void ProfileContainsNeitherBeckhoffAssemblyReferenceNorEmbeddedVendorTypes()
    {
        using var stream = File.OpenRead(typeof(XaeProjectAccess).Assembly.Location);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        Assert.DoesNotContain(metadata.AssemblyReferences, handle =>
            metadata.GetString(metadata.GetAssemblyReference(handle).Name) == "TCatSysManagerLib");
        Assert.DoesNotContain(metadata.TypeDefinitions, handle =>
            metadata.GetString(metadata.GetTypeDefinition(handle).Namespace) == "TCatSysManagerLib");
    }

    public sealed class Manager
    {
        public string NetId { get; private set; } = "developer-target";
        public List<string> Calls { get; } = [];
        public Root Root { get; } = new();
        public void SetTargetNetId(string value) { Calls.Add("set-target"); NetId = value; }
        public string GetTargetNetId() { Calls.Add("read-target"); return NetId; }
        public void ActivateConfiguration() => Calls.Add("activate");
        public void StartRestartTwinCAT() => Calls.Add("restart-system");
        public object LookupTreeItem(string path) { Calls.Add("lookup:" + path); return Root; }
    }

    public sealed class Root { public Online NestedProject { get; } = new(); }
    public sealed class Online
    {
        public List<string> Calls { get; } = [];
        public int Flags { get; private set; }
        public bool LoggedIn { get; set; } = true;
        public Exception? LoginError { get; set; }
        public Action? OnLogin { get; set; }
        public bool IsLoggedIn { get { Calls.Add("is-logged-in"); return LoggedIn; } }
        public void Login(int flags) { Calls.Add("login"); Flags = flags; OnLogin?.Invoke(); if (LoginError is not null) throw LoginError; }
        public void Logoff() => Calls.Add("logoff");
    }

    public sealed class Host(RemoteManager manager)
    {
        public RemoteManager Manager { get; } = manager;
        public string? RequestedObject { get; private set; }
        public object GetObject(string name) { RequestedObject = name; return Manager; }
    }

    public sealed class RemoteManager(string? version) : IDisposable
    {
        // Models the observed vendor setter accepting a request without selecting it.
        public string? Version { get { if (ReadError is not null) throw ReadError; return version; } set { VersionAssignments++; } }
        public Exception? ReadError { get; set; }
        public int VersionAssignments { get; private set; }
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
    public sealed class MissingVersion : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
    public sealed class MissingVersionHost(MissingVersion manager)
    {
        public object GetObject(string name) => manager;
    }
}
