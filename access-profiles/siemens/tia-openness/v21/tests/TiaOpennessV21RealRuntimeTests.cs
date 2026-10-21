using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21;
using System.Diagnostics;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V21;

public sealed class TiaOpennessV21RealRuntimeTests
{
    private const string RealLifetimeValidationVariable = "PLCSIM_UNITTEST_VALIDATE_TIA_OPENNESS_V21_LIFETIMES";
    private const string RealProjectSourceVariable = "PLCSIM_UNITTEST_TIA_OPENNESS_V21_PROJECT";
    private const string RealSafetyGuardValidationVariable = "PLCSIM_UNITTEST_VALIDATE_TIA_OPENNESS_V21_SAFETY_GUARD";
    private const string RealSafetyProjectSourceVariable = "PLCSIM_UNITTEST_TIA_OPENNESS_V21_SAFETY_PROJECT";

    [Fact]
    public async Task V21_binding_rejects_safety_program_but_not_standard_program_when_enabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(RealSafetyGuardValidationVariable),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var projectSource = Environment.GetEnvironmentVariable(RealSafetyProjectSourceVariable);
        Assert.False(string.IsNullOrWhiteSpace(projectSource), $"Set {RealSafetyProjectSourceVariable} to a V21 Safety test archive.");
        projectSource = Path.GetFullPath(projectSource!);
        Assert.True(File.Exists(projectSource), $"TIA Openness V21 Safety-validation project was not found: {projectSource}");

        var binding = new TiaOpennessV21Binding();
        binding.Initialize(new SiemensBindingContext("siemens-safety-guard-validation", TiaOpennessV21TestSupport.DefaultOperationalLogging));
        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectSource,
                TiaProjectOpenMode.RetrievedArchive,
                null,
                null),
            CancellationToken.None);
        Assert.True(open.Succeeded, FormatDiagnostics(open.Diagnostics, open.Evidence));
        var session = Assert.IsAssignableFrom<ITiaEngineeringSession>(open.Session);
        var safetySession = Assert.IsAssignableFrom<ITiaSafetyProgramAuthorizationSession>(open.Session);

        try
        {
            var discovery = await session.DiscoverTargetsAsync(
                new TiaProjectTargetDiscoveryRequest(null),
                CancellationToken.None);
            Assert.Equal(2, discovery.Targets.Count);
            var standardTarget = Assert.Single(discovery.Targets, target => target.DisplayName == "PLC_1");
            var safetyTarget = Assert.Single(discovery.Targets, target => target.DisplayName == "PLC_2");

            var standardRequirement = await safetySession.GetSafetyProgramAuthorizationRequirementAsync(
                standardTarget,
                CancellationToken.None);
            Assert.True(
                standardRequirement.Succeeded,
                FormatDiagnostics(standardRequirement.Diagnostics, standardRequirement.Evidence));
            Assert.Equal(TiaSafetyProgramAuthorizationRequirement.NotRequired, standardRequirement.Requirement);

            var safetyRequirement = await safetySession.GetSafetyProgramAuthorizationRequirementAsync(
                safetyTarget,
                CancellationToken.None);
            Assert.Equal(TiaOperationStatus.NotSupported, safetyRequirement.Status);
            Assert.Contains(
                safetyRequirement.Diagnostics,
                diagnostic => diagnostic.Code == "TiaOpenness.V21.Worker.SafetyProgramNotSupported");

            var safetyBuild = await session.BuildAsync(
                new TiaBuildRequest(safetyTarget),
                CancellationToken.None);
            Assert.Equal(TiaOperationStatus.NotSupported, safetyBuild.Status);
            Assert.Contains(
                safetyBuild.Diagnostics,
                diagnostic => diagnostic.Code == "TiaOpenness.V21.Worker.SafetyProgramNotSupported");

            var safetyTransfer = await session.TransferAsync(
                new TiaTransferRequest(
                    safetyTarget,
                    new TiaRuntimeTransferTarget(
                        "safety-guard-runtime",
                        "Safety guard runtime",
                        SiemensBindingKinds.PlcSimAdvanced,
                        "8.0",
                        SiemensRuntimeLocality.Local,
                        "FPlcUnderTest",
                        TiaTransferConnectionMode.PnIe,
                        TiaTransferPcInterface.PlcSim,
                        1,
                        null)),
                CancellationToken.None);
            Assert.Equal(TiaOperationStatus.NotSupported, safetyTransfer.Status);
            Assert.Contains(
                safetyTransfer.Diagnostics,
                diagnostic => diagnostic.Code == "TiaOpenness.V21.Worker.SafetyProgramNotSupported");
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task V21_binding_can_validate_real_repeated_session_lifetimes_when_enabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(RealLifetimeValidationVariable),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var projectSource = ResolveRealProjectSource();
        Assert.True(File.Exists(projectSource), $"TIA Openness V21 lifetime-validation project was not found: {projectSource}");
        var initialWorkerIds = GetV21WorkerProcessIds();
        const int sessionCycles = 3;
        const int warmupDiscoveryCycles = 2;
        const int measuredDiscoveryCycles = 24;

        for (var sessionCycle = 0; sessionCycle < sessionCycles; sessionCycle++)
        {
            var binding = new TiaOpennessV21Binding();
            binding.Initialize(new SiemensBindingContext("siemens-lifetime-validation", TiaOpennessV21TestSupport.DefaultOperationalLogging));
            var open = await binding.OpenProjectAsync(
                new TiaProjectOpenRequest(
                    projectSource,
                    TiaProjectOpenMode.DirectProject,
                    null,
                    null),
                CancellationToken.None);
            Assert.True(open.Succeeded, FormatDiagnostics(open.Diagnostics, open.Evidence));
            var session = Assert.IsAssignableFrom<ITiaEngineeringSession>(open.Session);
            var workerProcessId = await WaitForNewV21WorkerProcessAsync(initialWorkerIds);

            try
            {
                if (sessionCycle == 0)
                {
                    WorkerResourceSnapshot? baseline = null;
                    var measurements = new List<WorkerResourceSnapshot>();
                    for (var discoveryCycle = 0;
                         discoveryCycle < warmupDiscoveryCycles + measuredDiscoveryCycles;
                         discoveryCycle++)
                    {
                        var discovery = await session.DiscoverTargetsAsync(
                            new TiaProjectTargetDiscoveryRequest(null),
                            CancellationToken.None);
                        Assert.True(discovery.Succeeded, FormatDiagnostics(discovery.Diagnostics, discovery.Evidence));
                        var snapshot = CaptureWorkerResources(workerProcessId, discoveryCycle);
                        if (discoveryCycle == warmupDiscoveryCycles - 1)
                        {
                            baseline = snapshot;
                        }
                        else if (discoveryCycle >= warmupDiscoveryCycles)
                        {
                            measurements.Add(snapshot);
                        }
                    }

                    Assert.NotNull(baseline);
                    AssertWorkerResourcesRemainBounded(baseline!, measurements);
                }
                else
                {
                    var discovery = await session.DiscoverTargetsAsync(
                        new TiaProjectTargetDiscoveryRequest(null),
                        CancellationToken.None);
                    Assert.True(discovery.Succeeded, FormatDiagnostics(discovery.Diagnostics, discovery.Evidence));
                }
            }
            finally
            {
                await session.CloseAsync(CancellationToken.None);
            }

            await WaitForV21WorkerProcessesAsync(initialWorkerIds);
        }
    }

    private static string ResolveRealProjectSource()
    {
        var configured = Environment.GetEnvironmentVariable(RealProjectSourceVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                ".tmp",
                "S7Project",
                "S7_UnitTest_v21",
                "S7_UnitTest_v21.ap21");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "S7_UnitTest_v21.ap21");
    }

    private static IReadOnlySet<int> GetV21WorkerProcessIds()
    {
        return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(TiaOpennessV21WorkerProcessHost.WorkerExecutableName))
            .Select(process =>
            {
                using (process)
                {
                    return process.Id;
                }
            })
            .ToHashSet();
    }

    private static async Task<int> WaitForNewV21WorkerProcessAsync(IReadOnlySet<int> initialWorkerIds)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var processId = GetV21WorkerProcessIds().FirstOrDefault(id => !initialWorkerIds.Contains(id));
            if (processId != 0)
            {
                return processId;
            }

            await Task.Delay(50);
        }

        Assert.Fail("TIA Openness V21 worker process was not observed after opening the real engineering session.");
        return 0;
    }

    private static async Task WaitForV21WorkerProcessesAsync(IReadOnlySet<int> allowedWorkerIds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var unexpected = GetV21WorkerProcessIds()
                .Where(processId => !allowedWorkerIds.Contains(processId))
                .ToArray();
            if (unexpected.Length == 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        var remaining = GetV21WorkerProcessIds()
            .Where(processId => !allowedWorkerIds.Contains(processId))
            .ToArray();
        Assert.Fail($"TIA Openness V21 worker processes remained after cleanup: {string.Join(", ", remaining)}");
    }

    private static WorkerResourceSnapshot CaptureWorkerResources(int processId, int cycle)
    {
        using var process = Process.GetProcessById(processId);
        process.Refresh();
        return new WorkerResourceSnapshot(
            cycle,
            process.PrivateMemorySize64,
            process.HandleCount,
            process.Threads.Count);
    }

    private static void AssertWorkerResourcesRemainBounded(
        WorkerResourceSnapshot baseline,
        IReadOnlyList<WorkerResourceSnapshot> measurements)
    {
        const long maximumPrivateMemoryGrowth = 64L * 1024 * 1024;
        const int maximumHandleGrowth = 16;
        const int maximumThreadGrowth = 4;
        Assert.NotEmpty(measurements);

        var maximumMemory = measurements.Max(snapshot => snapshot.PrivateMemoryBytes);
        var maximumHandles = measurements.Max(snapshot => snapshot.HandleCount);
        var maximumThreads = measurements.Max(snapshot => snapshot.ThreadCount);
        var evidence = string.Join(
            "; ",
            new[] { baseline }.Concat(measurements).Select(snapshot =>
                $"cycle={snapshot.Cycle},private={snapshot.PrivateMemoryBytes},handles={snapshot.HandleCount},threads={snapshot.ThreadCount}"));
        Console.WriteLine($"TIA Openness V21 worker resource evidence: {evidence}");

        Assert.True(
            maximumMemory <= baseline.PrivateMemoryBytes + maximumPrivateMemoryGrowth,
            $"Worker private memory exceeded the post-warm-up guardrail. {evidence}");
        Assert.True(
            maximumHandles <= baseline.HandleCount + maximumHandleGrowth,
            $"Worker handle count exceeded the post-warm-up guardrail. {evidence}");
        Assert.True(
            maximumThreads <= baseline.ThreadCount + maximumThreadGrowth,
            $"Worker thread count exceeded the post-warm-up guardrail. {evidence}");
    }

    private static string FormatDiagnostics(
        IReadOnlyList<TiaDiagnostic> diagnostics,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return string.Join(
            Environment.NewLine,
            diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")
                .Concat(evidence.Select(item => $"{item.Kind}: {item.Summary}")));
    }

    private sealed record WorkerResourceSnapshot(
        int Cycle,
        long PrivateMemoryBytes,
        int HandleCount,
        int ThreadCount);
}
