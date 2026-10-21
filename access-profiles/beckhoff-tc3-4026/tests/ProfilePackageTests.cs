using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Testing.Core;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff;

public sealed class ProfilePackageTests
{
    [Fact]
    public void PackageDoesNotRedistributeInstalledEngineeringInteropAssemblies()
    {
        var files = Directory.GetFiles(Path.Combine(Packages, ProfileId), "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).ToArray();
        Assert.DoesNotContain("Microsoft.VisualStudio.Interop.dll", files, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("TCatSysManagerLib.dll", files, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(files, name => name is not null &&
            (name.EndsWith(".tlb", StringComparison.OrdinalIgnoreCase)
            || name.Contains("TCatSysManager", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void PackagedProfileContainsNoBeckhoffAssemblyReferenceOrEmbeddedTypes()
    {
        using var stream = File.OpenRead(Path.Combine(Packages, ProfileId, "PLCSIM.UnitTest.AccessProfiles.Beckhoff.dll"));
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        Assert.DoesNotContain(metadata.AssemblyReferences, handle =>
            metadata.GetString(metadata.GetAssemblyReference(handle).Name) == "TCatSysManagerLib");
        Assert.DoesNotContain(metadata.TypeDefinitions, handle =>
            metadata.GetString(metadata.GetTypeDefinition(handle).Namespace) == "TCatSysManagerLib");
    }

    private const string ProfileId = "beckhoff-tc3-4026";
    [Fact]
    public async Task PackagedSubscriptionExampleAndLocalDocumentationAreUsable()
    {
        var root = Path.Combine(Packages, ProfileId, "docs");
        foreach (var relative in new[] { "configuration.md", "verification.md", "performance/README.md",
            "performance/data/strategy-summary.csv", "performance/diagrams/capacity-monitoring.svg",
            "contracts/runtime-provisioning.md" })
            Assert.True(File.Exists(Path.Combine(root, relative)), relative);
        await using var session = await LoadAsync();
        var json = await File.ReadAllTextAsync(Path.Combine(root, "examples", "subscription-values.json"));
        Assert.True(session.Configuration.LoadConfig(json).IsSuccessful);
        Assert.True(session.Configuration.ValidateConfig(RuntimeProvisioningMode.UseExisting).IsValid);
        Assert.Equal("subscription", session.Configuration.GetStringFieldValue("beckhoff.unitTest.observationStrategy").Value);
        Assert.Equal(10, session.Configuration.GetIntegerFieldValue("beckhoff.unitTest.monitoringIntervalMs").Value);
    }

    [Fact]
    public async Task SubscriptionConfigurationUsesExistingHostProjectionAndSerialization()
    {
        await using var session = await LoadAsync();
        var config = session.Configuration;
        var field = config.GetFields(RuntimeProvisioningMode.CreateOwned).Value!.Fields
            .Single(value => value.Id == "beckhoff.unitTest.observationStrategy");
        Assert.Equal(new[] { "polling", "subscription" }, field.AllowedValues.Select(value => value.Value));
        Assert.Equal("polling", field.DefaultValue);
        Assert.True(config.AddFieldValue(field.Id, "subscription").IsSuccessful);
        var serialized = config.SerializeConfig().Value!;
        await using var copy = await LoadAsync();
        Assert.True(copy.Configuration.LoadConfig(serialized).IsSuccessful);
        Assert.Equal(serialized, copy.Configuration.SerializeConfig().Value);
        Assert.True(copy.Configuration.ValidateConfig(RuntimeProvisioningMode.CreateOwned).IsValid);
    }

    internal static string Packages => Environment.GetEnvironmentVariable("PLCSIM_UNITTEST_BECKHOFF_PACKAGE_TEST_ROOT")
        is { Length: > 0 } root ? Path.GetFullPath(root) : Path.Combine(AppContext.BaseDirectory, "profiles");
    private static AccessProfileSessionRequest Request(ProjectOwnershipMode ownership = ProjectOwnershipMode.ExternallyManaged, string source = "") =>
        new(ProfileId, "1.0.0", source, ownership);

    private static async Task<IAccessProfileSession> LoadAsync(AccessProfileSessionRequest? request = null, ILoggerFactory? logger = null)
    {
        var loader = new AccessProfilePluginLoader(TestCoreServiceContext.Create(
            loggerFactory: logger, accessProfileLoggerFactory: _ => logger ?? NullLoggerFactory.Instance,
            accessProfileDataRootDirectory: Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "beckhoff-package-tests")), []);
        var discovery = loader.Discover(new(Packages, false));
        Assert.Empty(discovery.Diagnostics);
        Assert.Equal(ProfileId, Assert.Single(discovery.Candidates).Info.ProfileId);
        var result = await loader.LoadAsync(request ?? Request(), discovery, CancellationToken.None);
        Assert.True(result.State == AccessProfileLoadState.Loaded, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        var session = Assert.IsAssignableFrom<IAccessProfileSession>(result.Profile);
        Assert.Equal(Path.GetFullPath(Path.Combine(Packages, ProfileId, "PLCSIM.UnitTest.AccessProfiles.Beckhoff.dll")),
            Path.GetFullPath(session.Project.GetType().Assembly.Location), ignoreCase: true);
        return session;
    }

    [Fact]
    public async Task PackageLoadsThroughHostWithoutClaimingRuntimeAvailability()
    {
        await using var session = await LoadAsync();
        Assert.Equal(ProfileId, session.Info.ProfileId);
        Assert.Equal("1.0.0", session.Info.Version);
        Assert.Equal(AccessHealthState.Healthy, session.Health.State);
        Assert.All(new IAccessProfileService[] { session.Project, session.PlcControl, session.UnitTestLifecycle, session.UnitTestEventStream }, service =>
        {
            Assert.Equal(ServiceState.NotPrepared, service.State);
            Assert.Equal(AccessHealthState.NotEstablished, service.Health.State);
        });
        Assert.Equal(PlcOperatingState.Unknown, session.PlcControl.CurrentState.OperatingState);
        Assert.False(session.PlcControl.CurrentState.ReadyForExecution);
        Assert.False(session.UnitTestEventStream.CurrentMonitoringState.IsMonitoring);
        Assert.False(session.UnitTestEventStream.CurrentMonitoringState.BufferObservedEmpty);
        Assert.Equal(EventBufferReadState.NotObserved, session.UnitTestEventStream.CurrentMonitoringState.LastReadState);
    }

    [Fact]
    public async Task AdsV7LoadsInsideProfileContextWithUnmodifiedHostLogging()
    {
        using var logger = new TrackingLoggerFactory();
        await using var session = await LoadAsync(logger: logger);
        var hostLogging = typeof(ILoggerFactory).Assembly;
        Assert.StartsWith("8.0.0", hostLogging.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        var profileAssembly = session.Project.GetType().Assembly;
        var factoryType = profileAssembly.GetType("PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime.AdsClientFactory", true)!;
        var factory = Activator.CreateInstance(factoryType, logger)!;
        using var client = (IDisposable)factoryType.GetMethod("CreateClient")!.Invoke(factory, null)!;
        Assert.False((bool)client.GetType().GetProperty("IsConnected")!.GetValue(client)!);
        Assert.Same(AssemblyLoadContext.GetLoadContext(profileAssembly), AssemblyLoadContext.GetLoadContext(client.GetType().Assembly));
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(profileAssembly));
        Assert.Contains(logger.Categories, category => category.Contains("Ads", StringComparison.OrdinalIgnoreCase));
        client.Dispose();
        Assert.False(logger.Disposed);
    }

    [Fact]
    public async Task DefaultsAreProjectedWithoutTurningOmittedPortIntoAnOverride()
    {
        await using var session = await LoadAsync();
        var config = session.Configuration;
        var fields = config.GetFields(RuntimeProvisioningMode.UseExisting).Value!.Fields;
        Assert.Equal("851", fields.Single(field => field.Id == "beckhoff.runtimeRef.plcPort").DefaultValue);
        Assert.Equal("10", fields.Single(field => field.Id == "beckhoff.unitTest.monitoringIntervalMs").DefaultValue);
        Assert.Equal("1", fields.Single(field => field.Id == "beckhoff.unitTest.drainIntervalMs").DefaultValue);
        Assert.Equal("32", fields.Single(field => field.Id == "beckhoff.unitTest.maxEventsPerBatch").DefaultValue);
        Assert.Equal("{}", config.SerializeConfig().Value);
        Assert.False(config.ValidateConfig(RuntimeProvisioningMode.UseExisting).IsValid);
        Assert.True(config.ValidateConfig(RuntimeProvisioningMode.CreateOwned).IsValid);
        Assert.False(config.ContainsFieldValue("beckhoff.runtimeRef.plcPort"));
    }

    [Theory]
    [InlineData("project.sln", false)]
    [InlineData("project.tszip", true)]
    public async Task ManagedProjectionDerivesPortAndOnlyExposesArchiveWorkspaceForArchives(string source, bool archive)
    {
        await using var session = await LoadAsync(Request(ProjectOwnershipMode.ApplicationManaged, source));
        var fields = session.Configuration.GetFields(RuntimeProvisioningMode.CreateOwned).Value!.Fields;
        Assert.Null(fields.Single(field => field.Id == "beckhoff.runtimeRef.plcPort").DefaultValue);
        Assert.Equal(archive, fields.Any(field => field.Id == "beckhoff.twincat.archiveWorkDirectory"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("{\"unitTest\":null}")]
    [InlineData("{\"unitTest\":{\"monitoringIntervalMs\":0}}")]
    [InlineData("{\"unitTest\":{\"drainIntervalMs\":1.5}}")]
    [InlineData("{\"unitTest\":{\"maxEventsPerBatch\":2147483648}}")]
    [InlineData("{\"unitTest\":{\"observationStrategy\":\"plcCycle\"}}")]
    [InlineData("{\"unitTest\":{\"runControl\":\"\"}}")]
    [InlineData("{\"runtimeRef\":{\"id\":\"not-a-net-id\"}}")]
    [InlineData("{\"runtimeRef\":{\"plcPort\":65536}}")]
    [InlineData("{\"runtimeRef\":{\"plcPort\":0}}")]
    public async Task InvalidImportPreservesPreviousConfiguration(string json)
    {
        await using var session = await LoadAsync();
        var config = session.Configuration;
        Assert.True(config.LoadConfig("{\"runtimeRef\":{\"id\":\"192.0.2.10.1.1\",\"plcPort\":852}}").IsSuccessful);
        var before = config.SerializeConfig().Value;
        Assert.False(config.LoadConfig(json).IsSuccessful);
        Assert.Equal(before, config.SerializeConfig().Value);
    }

    [Fact]
    public async Task FieldMutationAndClearingTargetPreservePortAndUnrelatedValues()
    {
        await using var session = await LoadAsync();
        var config = session.Configuration;
        Assert.True(config.LoadConfig("{\"runtimeRef\":{\"id\":\"192.0.2.10.1.1\",\"plcPort\":852},\"extension\":{\"value\":1}}").IsSuccessful);
        Assert.True(config.SetRuntimeTarget(null).IsSuccessful);
        Assert.Null(config.RuntimeTargetId);
        Assert.Equal(852, config.GetIntegerFieldValue("beckhoff.runtimeRef.plcPort").Value);
        const string field = "beckhoff.unitTest.maxEventsPerBatch";
        Assert.False(config.SetFieldValue(field, 99).IsSuccessful);
        Assert.True(config.AddFieldValue(field, 512).IsSuccessful);
        Assert.False(config.AddFieldValue(field, 16).IsSuccessful);
        Assert.False(config.SetFieldValue(field, "16").IsSuccessful);
        Assert.True(config.SetFieldValue(field, 16).IsSuccessful);
        Assert.Equal(16, config.GetIntegerFieldValue(field).Value);
        Assert.True(config.RemoveFieldValue(field).IsSuccessful);
        Assert.False(config.ContainsFieldValue(field));
        using var json = JsonDocument.Parse(config.SerializeConfig().Value!);
        Assert.Equal(1, json.RootElement.GetProperty("extension").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task UnimplementedOperationsStayUnsupportedAndCancelledDiscoveryDoesNotRun()
    {
        await using var session = await LoadAsync();
        var diagnostics = new List<Diagnostic>();
        var profileDiagnostics = new List<Diagnostic>();
        session.DiagnosticEventHandler += (_, args) =>
        {
            profileDiagnostics.Add(args.Diagnostic);
            diagnostics.Add(args.Diagnostic);
        };
        session.Project.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        session.PlcControl.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        session.UnitTestLifecycle.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        session.UnitTestEventStream.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        var config = session.Configuration.Configuration;
        Assert.Equal(OperationResultState.NotSupported, (await session.Project.DiscoverProjectTargetsAsync(new(config), default)).State);
        var unpreparedCommand = await session.UnitTestLifecycle.ResetAsync(null!, default);
        Assert.Equal(OperationResultState.Failed, unpreparedCommand.State);
        Assert.False(unpreparedCommand.Value!.Accepted);
        Assert.Equal(OperationResultState.Failed, (await session.UnitTestEventStream.StartMonitoringAsync(null!, default)).State);
        await Assert.ThrowsAsync<ArgumentNullException>(() => session.PrepareUnitTestCommunicationAsync(null!, default));
        Assert.Single(diagnostics);
        Assert.Empty(profileDiagnostics);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.PlcControl.DiscoverRuntimeTargetsAsync(new(config), new(true)));
        Assert.Equal(OperationResultState.Succeeded, (await session.Project.CleanupAsync(default)).State);
    }

    [Fact]
    public async Task SessionsAreIsolatedAndDisposalDoesNotDisposeHostLogger()
    {
        using var logger = new TrackingLoggerFactory();
        var first = await LoadAsync(logger: logger);
        await using var second = await LoadAsync(logger: logger);
        first.Configuration.AddFieldValue("beckhoff.unitTest.maxEventsPerBatch", 16);
        Assert.False(second.Configuration.ContainsFieldValue("beckhoff.unitTest.maxEventsPerBatch"));
        var changes = 0;
        var configuration = first.Configuration;
        var lifecycle = first.UnitTestLifecycle;
        first.StateChangedEventHandler += (_, _) => changes++;
        await first.DisposeAsync();
        await first.DisposeAsync();
        Assert.Equal(1, changes);
        Assert.Equal(AccessHealthState.Disposed, lifecycle.Health.State);
        Assert.Equal(ServiceState.Disposed, lifecycle.State);
        Assert.Throws<ObjectDisposedException>(() => configuration.SerializeConfig());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lifecycle.ResetAsync(null!, default));
        Assert.False(logger.Disposed);
        Assert.Equal(AccessHealthState.Healthy, second.Health.State);
    }

    [Fact]
    public async Task DisposalObserverCanReenterWithoutRepeatingCleanup()
    {
        await using var package = await LoadAsync();
        await using var plugin = CreatePlugin(package);
        plugin.Initialize(CreateContext());
        var session = (await plugin.LoadAsync(Request(), default)).Session!;
        var notifications = 0;
        session.StateChangedEventHandler += (_, _) =>
        {
            notifications++;
            Assert.True(session.DisposeAsync().IsCompletedSuccessfully);
        };
        await session.DisposeAsync();
        Assert.Equal(1, notifications);
        Assert.Equal(AccessHealthState.Disposed, session.Health.State);
    }

    [Fact]
    public async Task PluginRequiresInitializationAndRejectsParallelSessionAndWrongIdentity()
    {
        await using var package = await LoadAsync();
        await using var plugin = CreatePlugin(package);
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadAsync(Request(), default));
        plugin.Initialize(CreateContext());
        Assert.Throws<InvalidOperationException>(() => plugin.Initialize(CreateContext()));
        Assert.False((await plugin.LoadAsync(Request() with { ProfileId = "beckhoff" }, default)).IsSuccessful);
        Assert.False((await plugin.LoadAsync(Request() with { ProfileVersion = "2.0.0" }, default)).IsSuccessful);
        var first = await plugin.LoadAsync(Request(), default);
        Assert.True(first.IsSuccessful);
        Assert.False((await plugin.LoadAsync(Request(), default)).IsSuccessful);
        await first.Session!.DisposeAsync();
        var next = await plugin.LoadAsync(Request(), default);
        Assert.True(next.IsSuccessful);
        await plugin.DisposeAsync();
        Assert.Equal(AccessHealthState.Disposed, next.Session!.Health.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => plugin.LoadAsync(Request(), default));
    }

    [Fact]
    public async Task RegistrationCapabilitiesMatchHostContractAndInstalledPrerequisites()
    {
        await using var package = await LoadAsync();
        await using var plugin = CreatePlugin(package);
        plugin.Initialize(CreateContext());
        var result = await plugin.RegisterAsync(default);
        var validation = new AccessProfileCapabilityValidator().Validate(result);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        var available = result.Capabilities.AccessApis.All(api => api.Availability == CapabilityAvailability.Usable);
        Assert.Equal(available ? AccessProfileRegistrationState.Registered : AccessProfileRegistrationState.Unavailable, result.State);
        var expected = available ? CapabilityAvailability.Usable : CapabilityAvailability.Unavailable;
        Assert.All(result.Capabilities.ProjectSources, source => Assert.Equal(expected, source.Availability));
        Assert.All(result.Capabilities.Runtimes, runtime => Assert.Equal(expected, runtime.Availability));
        if (available) Assert.Equal("1.0.0", Assert.Single(result.Capabilities.UnitTestContractVersions));
        else Assert.Empty(result.Capabilities.UnitTestContractVersions);
        Assert.True(plugin.ValidateConfig(new(ProfileId, "1.0.0", AccessProfileConfigurationPayload.Empty, "")).IsValid);
        Assert.False(plugin.ValidateConfig(new("beckhoff", null, AccessProfileConfigurationPayload.Empty, "")).IsValid);
    }

    [Fact]
    public async Task FailedCompositionDoesNotOccupyTheSessionSlotOrDisposeHostResources()
    {
        await using var package = await LoadAsync();
        await using var plugin = CreatePlugin(package);
        using var logger = new TrackingLoggerFactory { FailAtCategory = 3 };
        plugin.Initialize(CreateContext(logger));
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadAsync(Request(), default));
        var retry = await plugin.LoadAsync(Request(), default);
        Assert.True(retry.IsSuccessful);
        await retry.Session!.DisposeAsync();
        Assert.False(logger.Disposed);
    }

    [Fact]
    public async Task ThrowingDisposalObserverCannotLeaveUsableServicesOrAnOccupiedSessionSlot()
    {
        await using var package = await LoadAsync();
        await using var plugin = CreatePlugin(package);
        plugin.Initialize(CreateContext());
        var session = (await plugin.LoadAsync(Request(), default)).Session!;
        session.Project.StateChangedEventHandler += (_, _) => throw new InvalidOperationException("Observer failed.");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.DisposeAsync());
        Assert.All(new IAccessProfileService[] { session.Project, session.PlcControl, session.UnitTestLifecycle, session.UnitTestEventStream },
            service => Assert.Equal(ServiceState.Disposed, service.State));
        Assert.True((await plugin.LoadAsync(Request(), default)).IsSuccessful);
    }

    private static IAccessProfilePlugin CreatePlugin(IAccessProfileSession session) =>
        (IAccessProfilePlugin)Activator.CreateInstance(session.Project.GetType().Assembly.GetType(
            "PLCSIM.UnitTest.AccessProfiles.Beckhoff.BeckhoffAccessProfilePlugin", true)!)!;

    private static AccessProfileContext CreateContext(ILoggerFactory? logger = null) => new(
        logger ?? NullLoggerFactory.Instance, TimeProvider.System, Path.Combine(Packages, ProfileId),
        Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "beckhoff-package-tests"),
        new OperationalLoggingOptions(Path.GetTempPath(), LogLevel.Information, 1048576, 2));

    private sealed class TrackingLoggerFactory : ILoggerFactory
    {
        public List<string> Categories { get; } = [];
        public bool Disposed { get; private set; }
        public int? FailAtCategory { get; init; }
        public ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            if (Categories.Count == FailAtCategory) throw new InvalidOperationException("Logger creation failed.");
            return NullLogger.Instance;
        }
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }
}
