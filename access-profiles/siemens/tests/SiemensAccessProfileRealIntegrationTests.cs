using System.Diagnostics;
using System.Text.Json;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Testing.Core;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class SiemensAccessProfileRealIntegrationCollection
{
    public const string CollectionName = "Siemens access profile real integration";
}

[Collection(SiemensAccessProfileRealIntegrationCollection.CollectionName)]
public sealed class SiemensAccessProfileRealIntegrationTests
{
    private const string ValidationVariable = "PLCSIM_UNITTEST_VALIDATE_SIEMENS_PROFILE_LIFETIMES";
    private const string ProfileRootVariable = "PLCSIM_UNITTEST_SIEMENS_REAL_PROFILE_ROOT";
    private const string ProjectSourceVariable = "PLCSIM_UNITTEST_SIEMENS_PROJECT";
    private const string RuntimeInstanceVariable = "PLCSIM_UNITTEST_SIEMENS_RUNTIME_INSTANCE";
    private const string TiaWorkerProcessName = "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21.Worker";
    private const string PlcsimWorkerProcessName = "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker";
    private const int SessionCycles = 3;

    [Fact]
    public async Task Siemens_profile_can_validate_real_repeated_managed_and_external_session_lifetimes_when_enabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(ValidationVariable),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var repositoryRoot = ResolveRepositoryRoot();
        var profilesRoot = ResolveProfilesRoot(repositoryRoot);
        var profileRoot = Path.Combine(profilesRoot, "siemens");
        var projectSource = ResolveProjectSource(repositoryRoot);
        var runtimeInstance = Environment.GetEnvironmentVariable(RuntimeInstanceVariable) ?? "PlcUnderTest";
        Assert.True(Directory.Exists(profileRoot), $"Deployed Siemens profile package was not found: {profileRoot}");
        Assert.True(File.Exists(projectSource), $"TIA Openness project was not found: {projectSource}");

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            "siemens-profile-real-integration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        var initialWorkers = GetSiemensWorkerProcessIds();

        try
        {
            var coreContext = TestCoreServiceContext.Create();
            var discoveryOptions = new AccessProfileDiscoveryOptions(
                profilesRoot,
                IncludeBuiltInProfiles: false);
            var pluginLoader = new AccessProfilePluginLoader(coreContext, []);
            var validator = new AccessProfileCapabilityValidator();
            var registryStore = new FileApplicationProfileRegistryStore(
                coreContext,
                new ApplicationProfileRegistryOptions(
                    Path.Combine(temporaryRoot, ApplicationProfileRegistryOptions.RegistryFileName)),
                validator);
            var registrationService = new ApplicationProfileRegistrationService(
                pluginLoader,
                discoveryOptions,
                registryStore,
                validator,
                coreContext);

            var registration = await registrationService.RefreshAllProfileRegistrationsAsync(
                TimeSpan.FromMinutes(3),
                CancellationToken.None);
            var siemensRegistration = Assert.Single(
                registration.Registry.Profiles,
                profile => string.Equals(profile.ProfileId, "siemens", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(AccessProfileRegistrationState.Registered, siemensRegistration.Registration.State);
            await WaitForSiemensWorkersAsync(initialWorkers);

            await using var sessions = new AccessProfileSessionService(
                pluginLoader,
                discoveryOptions,
                coreContext);
            var plcsimMeasurements = new List<WorkerResourceSnapshot>();
            var tiaMeasurements = new List<WorkerResourceSnapshot>();

            for (var cycle = 0; cycle < SessionCycles; cycle++)
            {
                plcsimMeasurements.Add(await ExecuteExternalSessionCycleAsync(
                    sessions,
                    runtimeInstance,
                    cycle,
                    initialWorkers));
            }

            for (var cycle = 0; cycle < SessionCycles; cycle++)
            {
                var measurements = await ExecuteManagedSessionCycleAsync(
                    sessions,
                    projectSource,
                    runtimeInstance,
                    cycle,
                    initialWorkers);
                tiaMeasurements.Add(measurements.Tia);
                plcsimMeasurements.Add(measurements.Plcsim);
            }

            AssertResourceSeriesBounded(
                "TIA Openness V21",
                tiaMeasurements,
                maximumPrivateMemoryGrowth: 64L * 1024 * 1024,
                maximumHandleGrowth: 16,
                maximumThreadGrowth: 4);
            AssertResourceSeriesBounded(
                "PLCSIM Advanced V8",
                plcsimMeasurements,
                maximumPrivateMemoryGrowth: 32L * 1024 * 1024,
                maximumHandleGrowth: 8,
                maximumThreadGrowth: 2);
            AssertPackageEntryAssembliesReleased(profileRoot);
        }
        finally
        {
            await WaitForSiemensWorkersAsync(initialWorkers);
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    private static async Task<WorkerResourceSnapshot> ExecuteExternalSessionCycleAsync(
        AccessProfileSessionService sessions,
        string runtimeInstance,
        int cycle,
        IReadOnlySet<int> initialWorkers)
    {
        var load = await sessions.LoadAsync(
            new AccessProfileSessionRequest(
                "siemens",
                "2.0.0",
                string.Empty,
                ProjectOwnershipMode.ExternallyManaged),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        var session = Assert.IsType<AccessProfileSession>(load.Session);
        var configuration = CreateConfiguration(string.Empty);

        try
        {
            var runtime = await PrepareExistingRuntimeAsync(session, configuration, runtimeInstance);
            var workerId = await WaitForNewWorkerAsync(PlcsimWorkerProcessName, initialWorkers);
            Assert.Empty(GetNewWorkerProcessIds(TiaWorkerProcessName, initialWorkers));
            var snapshot = CaptureWorkerResources(PlcsimWorkerProcessName, workerId, cycle, "external");

            var release = await session.Profile.PlcControl.ReleaseRuntimeAsync(
                new PlcRuntimeReleaseRequest(
                    runtime.Runtime,
                    runtime.RuntimeTarget,
                    RuntimeResourceReleaseMode.KeepResource),
                CancellationToken.None);
            Assert.True(release.IsSuccessful, release.Message);
            _ = runtime;
            return snapshot;
        }
        finally
        {
            await sessions.UnloadAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            await WaitForSiemensWorkersAsync(initialWorkers);
        }
    }

    private static async Task<ManagedCycleMeasurements> ExecuteManagedSessionCycleAsync(
        AccessProfileSessionService sessions,
        string projectSource,
        string runtimeInstance,
        int cycle,
        IReadOnlySet<int> initialWorkers)
    {
        var load = await sessions.LoadAsync(
            new AccessProfileSessionRequest(
                "siemens",
                "2.0.0",
                projectSource,
                ProjectOwnershipMode.ApplicationManaged),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        var session = Assert.IsType<AccessProfileSession>(load.Session);
        var configuration = CreateConfiguration(projectSource);

        try
        {
            var projectDiscovery = await session.Profile.Project.DiscoverProjectTargetsAsync(
                new PlcProjectRequest(configuration),
                CancellationToken.None);
            Assert.True(projectDiscovery.IsSuccessful, projectDiscovery.Message);
            Assert.NotEmpty(projectDiscovery.Value!.Candidates);
            var tiaWorkerId = await WaitForNewWorkerAsync(TiaWorkerProcessName, initialWorkers);
            var tiaSnapshot = CaptureWorkerResources(TiaWorkerProcessName, tiaWorkerId, cycle, "managed");

            var runtime = await PrepareExistingRuntimeAsync(session, configuration, runtimeInstance);
            var plcsimWorkerId = await WaitForNewWorkerAsync(PlcsimWorkerProcessName, initialWorkers);
            var plcsimSnapshot = CaptureWorkerResources(PlcsimWorkerProcessName, plcsimWorkerId, cycle, "managed");

            var projectRelease = await session.Profile.Project.ReleaseProjectAsync(CancellationToken.None);
            Assert.True(projectRelease.IsSuccessful, projectRelease.Message);
            var runtimeRelease = await session.Profile.PlcControl.ReleaseRuntimeAsync(
                new PlcRuntimeReleaseRequest(
                    runtime.Runtime,
                    runtime.RuntimeTarget,
                    RuntimeResourceReleaseMode.KeepResource),
                CancellationToken.None);
            Assert.True(runtimeRelease.IsSuccessful, runtimeRelease.Message);
            return new ManagedCycleMeasurements(tiaSnapshot, plcsimSnapshot);
        }
        finally
        {
            await sessions.UnloadAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            await WaitForSiemensWorkersAsync(initialWorkers);
        }
    }

    private static async Task<PreparedPlcRuntime> PrepareExistingRuntimeAsync(
        AccessProfileSession session,
        AccessProfileConfig configuration,
        string runtimeInstance)
    {
        var discovery = await session.Profile.PlcControl.DiscoverRuntimeTargetsAsync(
            new PlcRuntimeDiscoveryRequest(configuration),
            CancellationToken.None);
        Assert.True(discovery.IsSuccessful, discovery.Message);
        var runtimeTarget = Assert.Single(
            discovery.Value!.Candidates,
            target => string.Equals(target.TechnicalName, runtimeInstance, StringComparison.OrdinalIgnoreCase)
                || string.Equals(target.DisplayName, runtimeInstance, StringComparison.OrdinalIgnoreCase));
        var prepare = await session.Profile.PlcControl.PrepareRuntimeAsync(
            new PlcRuntimePrepareRequest(
                configuration,
                RuntimeProvisioningMode.UseExisting,
                runtimeTarget),
            CancellationToken.None);
        Assert.True(prepare.IsSuccessful, prepare.Message);
        return Assert.IsType<PreparedPlcRuntime>(prepare.Value);
    }

    private static AccessProfileConfig CreateConfiguration(string projectSource)
    {
        return new AccessProfileConfig(
            "siemens",
            "2.0.0",
            new AccessProfileConfigurationPayload(
                """
                {
                  "unitTest": {
                    "observationStrategy": "polling",
                    "pollIntervalMs": 100
                  }
                }
                """),
            projectSource);
    }

    private static string ResolveProfilesRoot(string repositoryRoot)
    {
        var configured = Environment.GetEnvironmentVariable(ProfileRootVariable);
        return !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(
                repositoryRoot,
                "src",
                "PLCSIM.UnitTest.App.Wpf",
                "bin",
                "Debug",
                "net8.0-windows",
                "profiles");
    }

    private static string ResolveProjectSource(string repositoryRoot)
    {
        var configured = Environment.GetEnvironmentVariable(ProjectSourceVariable);
        return !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(
                repositoryRoot,
                ".tmp",
                "S7Project",
                "S7_UnitTest_v21",
                "S7_UnitTest_v21.ap21");
    }

    private static string ResolveRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root could not be resolved from the test output path.");
    }

    private static IReadOnlySet<int> GetSiemensWorkerProcessIds()
    {
        return GetWorkerProcessIds(TiaWorkerProcessName)
            .Concat(GetWorkerProcessIds(PlcsimWorkerProcessName))
            .ToHashSet();
    }

    private static IReadOnlySet<int> GetWorkerProcessIds(string processName)
    {
        return Process.GetProcessesByName(processName)
            .Select(process =>
            {
                using (process)
                {
                    return process.Id;
                }
            })
            .ToHashSet();
    }

    private static int[] GetNewWorkerProcessIds(
        string processName,
        IReadOnlySet<int> initialWorkers)
    {
        return GetWorkerProcessIds(processName)
            .Where(processId => !initialWorkers.Contains(processId))
            .ToArray();
    }

    private static async Task<int> WaitForNewWorkerAsync(
        string processName,
        IReadOnlySet<int> initialWorkers)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var processId = GetNewWorkerProcessIds(processName, initialWorkers).FirstOrDefault();
            if (processId != 0)
            {
                return processId;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Worker process '{processName}' was not observed.");
        return 0;
    }

    private static async Task WaitForSiemensWorkersAsync(IReadOnlySet<int> allowedWorkerIds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = GetSiemensWorkerProcessIds()
                .Where(processId => !allowedWorkerIds.Contains(processId))
                .ToArray();
            if (remaining.Length == 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        var unexpected = GetSiemensWorkerProcessIds()
            .Where(processId => !allowedWorkerIds.Contains(processId))
            .ToArray();
        Assert.Fail($"Siemens worker processes remained after profile cleanup: {string.Join(", ", unexpected)}");
    }

    private static WorkerResourceSnapshot CaptureWorkerResources(
        string worker,
        int processId,
        int cycle,
        string flow)
    {
        using var process = Process.GetProcessById(processId);
        process.Refresh();
        var snapshot = new WorkerResourceSnapshot(
            worker,
            flow,
            cycle,
            process.PrivateMemorySize64,
            process.HandleCount,
            process.Threads.Count);
        Console.WriteLine(
            $"Siemens profile worker evidence: worker={snapshot.Worker},flow={snapshot.Flow},cycle={snapshot.Cycle},private={snapshot.PrivateMemoryBytes},handles={snapshot.HandleCount},threads={snapshot.ThreadCount}");
        return snapshot;
    }

    private static void AssertResourceSeriesBounded(
        string component,
        IReadOnlyList<WorkerResourceSnapshot> measurements,
        long maximumPrivateMemoryGrowth,
        int maximumHandleGrowth,
        int maximumThreadGrowth)
    {
        Assert.True(measurements.Count >= 3);
        var baseline = measurements[0];
        var measured = measurements.Skip(1).ToArray();
        var evidence = string.Join(
            "; ",
            measurements.Select(snapshot =>
                $"flow={snapshot.Flow},cycle={snapshot.Cycle},private={snapshot.PrivateMemoryBytes},handles={snapshot.HandleCount},threads={snapshot.ThreadCount}"));
        Assert.True(
            measured.Max(snapshot => snapshot.PrivateMemoryBytes)
                <= baseline.PrivateMemoryBytes + maximumPrivateMemoryGrowth,
            $"{component} worker private memory exceeded the post-warm-up guardrail. {evidence}");
        Assert.True(
            measured.Max(snapshot => snapshot.HandleCount)
                <= baseline.HandleCount + maximumHandleGrowth,
            $"{component} worker handle count exceeded the post-warm-up guardrail. {evidence}");
        Assert.True(
            measured.Max(snapshot => snapshot.ThreadCount)
                <= baseline.ThreadCount + maximumThreadGrowth,
            $"{component} worker thread count exceeded the post-warm-up guardrail. {evidence}");
    }

    private static void AssertPackageEntryAssembliesReleased(string profileRoot)
    {
        foreach (var assemblyPath in GetPackageEntryAssemblyPaths(profileRoot))
        {
            Exception? lastFailure = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                ForceFullCollection();
                try
                {
                    using var stream = File.Open(
                        assemblyPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None);
                    lastFailure = null;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lastFailure = ex;
                    Thread.Sleep(25);
                }
            }

            Assert.Null(lastFailure);
        }
    }

    private static IReadOnlyList<string> GetPackageEntryAssemblyPaths(string profileRoot)
    {
        var paths = new List<string>();
        foreach (var manifestPath in Directory.EnumerateFiles(
                     profileRoot,
                     "manifest.json",
                     SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("entryPoint", out var entryPoint)
                || !entryPoint.TryGetProperty("assembly", out var assembly)
                || string.IsNullOrWhiteSpace(assembly.GetString()))
            {
                continue;
            }

            paths.Add(Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(manifestPath)!,
                assembly.GetString()!)));
        }

        Assert.NotEmpty(paths);
        return paths;
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record ManagedCycleMeasurements(
        WorkerResourceSnapshot Tia,
        WorkerResourceSnapshot Plcsim);

    private sealed record WorkerResourceSnapshot(
        string Worker,
        string Flow,
        int Cycle,
        long PrivateMemoryBytes,
        int HandleCount,
        int ThreadCount);
}
