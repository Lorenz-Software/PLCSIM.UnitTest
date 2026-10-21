using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Registration;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Logging;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class RegistrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "beckhoff-registration-tests", Guid.NewGuid().ToString("N"));
    private AccessProfileContext Context => new(NullLoggerFactory.Instance, TimeProvider.System,
        Path.Combine(root, "package"), Path.Combine(root, "data"),
        new OperationalLoggingOptions(Path.Combine(root, "logs"), LogLevel.Debug, 1048576, 2));
    private AccessProfileSessionRequest Request => new("beckhoff-tc3-4026", "1.0.0", "", ProjectOwnershipMode.ExternallyManaged);

    [Fact]
    public async Task MissingEnvironmentIsUnavailableAndNotAnError()
    {
        var probe = new Probe(_ => BeckhoffEnvironment.Empty);
        await using var plugin = new BeckhoffAccessProfilePlugin(probe);
        plugin.Initialize(Context);
        var result = await plugin.RegisterAsync(default);
        Assert.Equal(AccessProfileRegistrationState.Unavailable, result.State);
        Assert.All(result.Capabilities.AccessApis, api => Assert.Equal(CapabilityAvailability.Unavailable, api.Availability));
        var saved = new BeckhoffProfileRegistryStore().Load(Context);
        Assert.Empty(saved.Environment.Engineering);
        Assert.Empty(saved.Environment.UsermodeRuntime);
        Assert.False(Directory.Exists(Context.ProfileRootPath));
        Assert.Equal(1, probe.Calls);
        Assert.Equal(AccessProfileProjectSourceEvaluationState.Unsupported, plugin.EvaluateProjectSource("missing.sln").State);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task CompleteEnvironmentAdvertisesImplementedExecution()
    {
        var environment = CompleteEnvironment();
        await using var plugin = new BeckhoffAccessProfilePlugin(new Probe(_ => environment));
        plugin.Initialize(Context);
        var result = await plugin.RegisterAsync(default);
        Assert.Equal(AccessProfileRegistrationState.Registered, result.State);
        Assert.All(result.Capabilities.AccessApis, api => Assert.Equal(CapabilityAvailability.Usable, api.Availability));
        Assert.All(result.Capabilities.ProjectSources, item => Assert.Equal(CapabilityAvailability.Usable, item.Availability));
        Assert.All(result.Capabilities.Runtimes, item => Assert.Equal(CapabilityAvailability.Usable, item.Availability));
        Assert.Equal("1.0.0", Assert.Single(result.Capabilities.UnitTestContractVersions));
        var saved = new BeckhoffProfileRegistryStore().Load(Context);
        Assert.Equal(environment.XaeShellPath, saved.Environment.XaeShellPath);
        Assert.Equal("3.1.4026.26", Assert.Single(saved.Environment.Engineering).ProductVersion);
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("engineering")]
    [InlineData("runtime")]
    public void IncompleteEnvironmentCannotAdvertiseExecution(string missing)
    {
        var environment = CompleteEnvironment();
        environment = missing switch
        {
            "shell" => environment with { XaeShellPath = null },
            "engineering" => environment with { Engineering = [] },
            _ => environment with { UsermodeRuntime = [] }
        };
        var result = BeckhoffRegistration.CreateResult(environment);
        Assert.Equal(AccessProfileRegistrationState.Unavailable, result.State);
        Assert.All(result.Capabilities.ProjectSources, source => Assert.Equal(CapabilityAvailability.Unavailable, source.Availability));
        Assert.All(result.Capabilities.Runtimes, runtime => Assert.Equal(CapabilityAvailability.Unavailable, runtime.Availability));
        Assert.Empty(result.Capabilities.UnitTestContractVersions);
        Assert.Contains("missing", result.Message);
    }

    [Fact]
    public async Task ProbeFailureReplacesPreviousEvidenceWithExplicitError()
    {
        var store = new BeckhoffProfileRegistryStore();
        await store.SaveAsync(Context, BeckhoffProfileRegistry.Create(Context,
            BeckhoffRegistration.CreateResult(CompleteEnvironment()), CompleteEnvironment()), default);
        await using var plugin = new BeckhoffAccessProfilePlugin(new Probe(_ => throw new UnauthorizedAccessException("Registry access denied.")));
        plugin.Initialize(Context);
        var result = await plugin.RegisterAsync(default);
        Assert.Equal(AccessProfileRegistrationState.Error, result.State);
        Assert.Contains("denied", result.Message);
        var saved = store.Load(Context);
        Assert.Equal(AccessProfileRegistrationState.Error, saved.Result.State);
        Assert.Empty(saved.Environment.Engineering);
        Assert.Empty(saved.Result.Capabilities.AccessApis);
        Assert.Equal(AccessProfileProjectSourceEvaluationState.Error, plugin.EvaluateProjectSource("project.sln").State);
    }

    [Fact]
    public async Task RegistrationRefusesToWriteInsideThePackage()
    {
        await using var plugin = new BeckhoffAccessProfilePlugin(new Probe(_ => BeckhoffEnvironment.Empty));
        plugin.Initialize(Context with { ProfileDataDirectory = Path.Combine(Context.ProfileRootPath, "data") });
        Assert.Equal(AccessProfileRegistrationState.Error, (await plugin.RegisterAsync(default)).State);
        Assert.False(Directory.Exists(Context.ProfileRootPath));
    }

    [Fact]
    public async Task UnwritableDataDestinationProducesRegistrationError()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Context.ProfileDataDirectory, "A file is not a directory.");
        await using var plugin = new BeckhoffAccessProfilePlugin(new Probe(_ => BeckhoffEnvironment.Empty));
        plugin.Initialize(Context);
        var result = await plugin.RegisterAsync(default);
        Assert.Equal(AccessProfileRegistrationState.Error, result.State);
        Assert.Contains("could not be written", result.Message);
    }

    [Fact]
    public async Task CancellationPreservesTheLastCompleteRegistry()
    {
        var store = new BeckhoffProfileRegistryStore();
        var registry = BeckhoffProfileRegistry.Create(Context, BeckhoffRegistration.CreateResult(BeckhoffEnvironment.Empty), BeckhoffEnvironment.Empty);
        await store.SaveAsync(Context, registry, default);
        var path = BeckhoffProfileRegistryStore.GetPath(Context);
        var before = File.ReadAllBytes(path);
        using var cancellation = new CancellationTokenSource();
        await using var plugin = new BeckhoffAccessProfilePlugin(new Probe(token =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return BeckhoffEnvironment.Empty;
        }));
        plugin.Initialize(Context);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.RegisterAsync(cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(Context.ProfileDataDirectory));
    }

    [Fact]
    public async Task RegistrationDoesNotCaptureTheCallersSynchronizationContext()
    {
        await Task.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new RejectingSynchronizationContext());
            try
            {
                var store = new BeckhoffProfileRegistryStore();
                store.SaveAsync(Context, BeckhoffProfileRegistry.Create(Context,
                    BeckhoffRegistration.CreateResult(BeckhoffEnvironment.Empty), BeckhoffEnvironment.Empty), default).GetAwaiter().GetResult();
            }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AccessProfileRegistrationState.Unavailable, new BeckhoffProfileRegistryStore().Load(Context).Result.State);
    }

    [Fact]
    public async Task RegistrationExcludesOtherRegistrationAndLoadAndDisposeWaitsForCompletion()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var plugin = new BeckhoffAccessProfilePlugin(new Probe(token =>
        {
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10), token)) throw new TimeoutException("Test did not release the probe.");
            return BeckhoffEnvironment.Empty;
        }));
        plugin.Initialize(Context);
        var registration = plugin.RegisterAsync(default);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(AccessProfileRegistrationState.Error, (await plugin.RegisterAsync(default)).State);
            Assert.False((await plugin.LoadAsync(Request, default)).IsSuccessful);
            Assert.Equal(AccessProfileProjectSourceEvaluationState.Error, plugin.EvaluateProjectSource("project.sln").State);
            var disposal = plugin.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            release.Set();
            Assert.Equal(AccessProfileRegistrationState.Unavailable, (await registration).State);
            await disposal;
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task ActiveSessionRejectsRegistrationWithoutRunningTheProbe()
    {
        var probe = new Probe(_ => BeckhoffEnvironment.Empty);
        await using var plugin = new BeckhoffAccessProfilePlugin(probe);
        plugin.Initialize(Context);
        await using var session = (await plugin.LoadAsync(Request, default)).Session!;
        Assert.Equal(AccessProfileRegistrationState.Error, (await plugin.RegisterAsync(default)).State);
        Assert.Equal(0, probe.Calls);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("profile")]
    [InlineData("version")]
    [InlineData("path")]
    [InlineData("nullCapabilities")]
    [InlineData("nullEnvironment")]
    [InlineData("badJson")]
    public async Task CorruptOrStaleRegistryFailsSourceEvaluationWithoutProbing(string corruption)
    {
        var store = new BeckhoffProfileRegistryStore();
        await store.SaveAsync(Context, BeckhoffProfileRegistry.Create(Context,
            BeckhoffRegistration.CreateResult(BeckhoffEnvironment.Empty), BeckhoffEnvironment.Empty), default);
        var path = BeckhoffProfileRegistryStore.GetPath(Context);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        switch (corruption)
        {
            case "schema": json["schemaVersion"] = 2; break;
            case "profile": json["profileId"] = "beckhoff-tc3-4024"; break;
            case "version": json["profileVersion"] = "2.0.0"; break;
            case "path": json["profileRootPath"] = Path.Combine(root, "another-package"); break;
            case "nullCapabilities": json["result"]!["capabilities"] = null; break;
            case "nullEnvironment": json["environment"] = null; break;
        }
        File.WriteAllText(path, corruption == "badJson" ? "{" : json.ToJsonString());
        var probe = new Probe(_ => throw new InvalidOperationException("Source evaluation must not probe the environment."));
        await using var plugin = new BeckhoffAccessProfilePlugin(probe);
        plugin.Initialize(Context);
        Assert.Equal(AccessProfileProjectSourceEvaluationState.Error, plugin.EvaluateProjectSource("project.sln").State);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task MissingRegistryIsAnErrorAndIsNotCreatedBySourceEvaluation()
    {
        await using var plugin = new BeckhoffAccessProfilePlugin(new Probe(_ => throw new InvalidOperationException()));
        plugin.Initialize(Context);
        Assert.Equal(AccessProfileProjectSourceEvaluationState.Error, plugin.EvaluateProjectSource("project.sln").State);
        Assert.False(Directory.Exists(Context.ProfileDataDirectory));
    }

    private BeckhoffEnvironment CompleteEnvironment() => new(Path.Combine(root, "TcXaeShell.exe"),
        [new(Path.Combine(root, "TwinCAT System Manager.x64.dll"), "3.1.4026.26")],
        [new(Path.Combine(root, "TcSystemServiceUm.exe"), "3.1.4026.30")]);

    internal sealed class Probe(Func<CancellationToken, BeckhoffEnvironment> inspect) : IBeckhoffEnvironmentProbe
    {
        internal int Calls { get; private set; }
        public BeckhoffEnvironment Inspect(CancellationToken cancellationToken) { Calls++; return inspect(cancellationToken); }
    }

    private sealed class RejectingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => throw new InvalidOperationException("Unexpected context capture.");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
