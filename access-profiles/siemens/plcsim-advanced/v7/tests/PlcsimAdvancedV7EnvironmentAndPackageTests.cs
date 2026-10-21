using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7;
using PLCSIM.UnitTest.Common.Logging;
using System.Text.Json;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V7;

public sealed class PlcsimAdvancedV7EnvironmentAndPackageTests
{
    [Fact]
    public void V7_manifest_matches_binding_metadata_and_entry_point()
    {
        var binding = new PlcsimAdvancedV7Binding();
        using var document = JsonDocument.Parse(File.ReadAllText(GetV7BindingManifestPath()));

        var root = document.RootElement;
        Assert.Equal(SiemensBindingPackageDescriptorContract.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());

        var info = root.GetProperty("info");
        Assert.Equal(binding.Info.BindingKind, info.GetProperty("bindingKind").GetString());
        Assert.Equal(binding.Info.BindingVersion, info.GetProperty("bindingVersion").GetString());
        Assert.Equal(binding.Info.DisplayName, info.GetProperty("displayName").GetString());

        var entryPoint = root.GetProperty("entryPoint");
        Assert.Equal(Path.GetFileName(typeof(PlcsimAdvancedV7Binding).Assembly.Location), entryPoint.GetProperty("assembly").GetString());
        Assert.Equal(typeof(PlcsimAdvancedV7Binding).FullName, entryPoint.GetProperty("typeName").GetString());
        Assert.Same(
            typeof(PlcsimAdvancedV7Binding),
            typeof(PlcsimAdvancedV7Binding).Assembly.GetType(entryPoint.GetProperty("typeName").GetString()!));

        var runtime = Assert.Single(root
            .GetProperty("declaration")
            .GetProperty("runtimes")
            .EnumerateArray());
        Assert.Equal(SiemensBindingKinds.PlcSimAdvanced, runtime.GetProperty("kind").GetString());
        Assert.Equal("7.0", runtime.GetProperty("version").GetString());
        Assert.Equal("Local", runtime.GetProperty("locality").GetString());
        Assert.Equal(
            new[] { SiemensProjectSourceKinds.TiaProject },
            runtime
                .GetProperty("compatibleProjectSourceKinds")
                .EnumerateArray()
                .Select(kind => kind.GetString())
                .ToArray());

        Assert.Empty(root
            .GetProperty("declaration")
            .GetProperty("projectSources")
            .EnumerateArray());
    }

    [Fact]
    public void V7_binding_maps_ready_probe_to_usable_readiness()
    {
        var binding = new PlcsimAdvancedV7Binding(new StubEnvironmentProbe(
            PlcsimAdvancedV7EnvironmentProbeResult.Ready(new[]
            {
                new PlcsimEvidence(
                    "plcsim-advanced-v7-runtime-api-dll",
                    "RuntimeApiDll",
                    "C:\\PLCSIMADV\\API\\7.0\\Siemens.Simatic.Simulation.Runtime.Api.x64.dll")
            })));

        var readiness = binding.ResolveReadiness(CreateReadinessRequest(binding));

        Assert.Equal(SiemensBindingReadinessState.Usable, readiness.State);
        Assert.Empty(readiness.Diagnostics);
        Assert.Contains("Runtime API evidence is usable", readiness.EvidenceSummary);
    }

    [Fact]
    public void V7_binding_maps_unavailable_probe_to_unavailable_readiness()
    {
        var binding = new PlcsimAdvancedV7Binding(new StubEnvironmentProbe(
            PlcsimAdvancedV7EnvironmentProbeResult.Unavailable(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V7.RegistryEvidenceMissing",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V7 Runtime API registry evidence was not found.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v7-registry"),
                Array.Empty<PlcsimEvidence>())));

        var readiness = binding.ResolveReadiness(CreateReadinessRequest(binding));

        Assert.Equal(SiemensBindingReadinessState.Unavailable, readiness.State);
        Assert.Empty(readiness.Diagnostics);
        Assert.Equal("PLCSIM Advanced V7 Runtime API registry evidence was not found.", readiness.EvidenceSummary);
    }

    [Fact]
    public void V7_binding_maps_failed_probe_to_error_readiness()
    {
        var binding = new PlcsimAdvancedV7Binding(new StubEnvironmentProbe(
            PlcsimAdvancedV7EnvironmentProbeResult.Failed(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V7.RuntimeApiDllLoadFailed",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V7 Runtime API DLL could not be loaded for export inspection.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v7-runtime-api-exports"),
                Array.Empty<PlcsimEvidence>())));

        var readiness = binding.ResolveReadiness(CreateReadinessRequest(binding));

        Assert.Equal(SiemensBindingReadinessState.Error, readiness.State);
        var diagnostic = Assert.Single(readiness.Diagnostics);
        Assert.Equal("PlcsimAdvanced.V7.RuntimeApiDllLoadFailed", diagnostic.Code);
    }

    [Fact]
    public async Task V7_environment_probe_classifies_complete_evidence_as_ready()
    {
        var source = new StubEnvironmentEvidenceSource();
        var probe = new PlcsimAdvancedV7EnvironmentProbe(source);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(PlcsimAdvancedV7EnvironmentProbeState.Ready, result.State);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "RuntimeApiDirectory");
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "RuntimeApiDll");
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "RuntimeApiExports");
    }

    [Fact]
    public async Task V7_environment_probe_rejects_v8_api7_compatibility_dll_as_unavailable()
    {
        var source = new StubEnvironmentEvidenceSource
        {
            FileVersionEvidence = new("7.0.0.1", "70.0.327.0")
        };
        var probe = new PlcsimAdvancedV7EnvironmentProbe(source);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(PlcsimAdvancedV7EnvironmentProbeState.Unavailable, result.State);
        Assert.Equal("PlcsimAdvanced.V7.ProductVersionMismatch", Assert.Single(result.Diagnostics).Code);
        Assert.Contains(result.Evidence, evidence =>
            evidence.Kind == "RuntimeApiDll"
            && evidence.Summary.Contains("ProductVersion=7.0.0.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task V7_environment_probe_classifies_missing_registry_as_unavailable()
    {
        var source = new StubEnvironmentEvidenceSource
        {
            RegistryEvidence = new PlcsimAdvancedV7RegistryEvidence(false, null)
        };
        var probe = new PlcsimAdvancedV7EnvironmentProbe(source);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(PlcsimAdvancedV7EnvironmentProbeState.Unavailable, result.State);
        Assert.Equal("PlcsimAdvanced.V7.RegistryEvidenceMissing", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task V7_environment_probe_classifies_registry_access_failure_as_failed()
    {
        var source = new StubEnvironmentEvidenceSource
        {
            RegistryException = new UnauthorizedAccessException("registry denied")
        };
        var probe = new PlcsimAdvancedV7EnvironmentProbe(source);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(PlcsimAdvancedV7EnvironmentProbeState.Failed, result.State);
        Assert.Equal("PlcsimAdvanced.V7.RegistryReadFailed", Assert.Single(result.Diagnostics).Code);
        Assert.Contains(result.Evidence, evidence => evidence.Summary == "registry denied");
    }

    [Fact]
    public async Task V7_environment_probe_classifies_missing_api_directory_as_unavailable()
    {
        var source = new StubEnvironmentEvidenceSource
        {
            PathInspection = _ => PlcsimAdvancedV7PathKind.Missing
        };
        var probe = new PlcsimAdvancedV7EnvironmentProbe(source);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(PlcsimAdvancedV7EnvironmentProbeState.Unavailable, result.State);
        Assert.Equal("PlcsimAdvanced.V7.ApiDirectoryMissing", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task V7_environment_probe_classifies_dll_path_access_failure_as_failed()
    {
        var source = new StubEnvironmentEvidenceSource
        {
            PathInspection = path => path.EndsWith(
                PlcsimAdvancedV7EnvironmentProbe.RuntimeApiDllName,
                StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("dll denied")
                : PlcsimAdvancedV7PathKind.Directory
        };
        var probe = new PlcsimAdvancedV7EnvironmentProbe(source);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(PlcsimAdvancedV7EnvironmentProbeState.Failed, result.State);
        Assert.Equal("PlcsimAdvanced.V7.RuntimeApiDllInspectFailed", Assert.Single(result.Diagnostics).Code);
        Assert.Contains(result.Evidence, evidence => evidence.Summary == "dll denied");
    }

    [Fact]
    public async Task V7_environment_probe_classifies_export_load_failure_as_failed()
    {
        var source = new StubEnvironmentEvidenceSource
        {
            ExportEvidence = PlcsimAdvancedV7ExportEvidence.Failed(
                "PlcsimAdvanced.V7.RuntimeApiDllLoadFailed",
                "Runtime API could not be loaded.",
                "bad image")
        };
        var probe = new PlcsimAdvancedV7EnvironmentProbe(source);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(PlcsimAdvancedV7EnvironmentProbeState.Failed, result.State);
        Assert.Equal("PlcsimAdvanced.V7.RuntimeApiDllLoadFailed", Assert.Single(result.Diagnostics).Code);
        Assert.Contains(result.Evidence, evidence => evidence.Summary == "bad image");
    }

    [Fact]
    public async Task V7_binding_revalidates_environment_before_opening_worker_session()
    {
        var ready = ReadyProbeWithRuntimeApiDirectory("C:\\PLCSIMADV\\API\\7.0");
        var unavailable = PlcsimAdvancedV7EnvironmentProbeResult.Unavailable(
            new PlcsimDiagnostic(
                "PlcsimAdvanced.V7.RuntimeApiDllMissing",
                PlcsimDiagnosticSeverity.Error,
                "PLCSIM Advanced V7 x64 Runtime API DLL was not found.",
                "EnvironmentProbe",
                "plcsim-advanced-v7-runtime-api-dll"),
            Array.Empty<PlcsimEvidence>());
        var probe = new SequenceEnvironmentProbe(ready, unavailable);
        var host = new CountingWorkerSessionHost();
        var binding = new PlcsimAdvancedV7Binding(probe, host);
        binding.Initialize(CreateBindingContext());

        var readiness = binding.ResolveReadiness(CreateReadinessRequest(binding));
        var open = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);

        Assert.Equal(SiemensBindingReadinessState.Usable, readiness.State);
        Assert.Equal(PlcsimOperationStatus.Unavailable, open.Status);
        Assert.Null(open.Session);
        Assert.Equal(2, probe.ProbeCount);
        Assert.Equal(0, host.OpenCount);
    }

    [Fact]
    public async Task V7_binding_fails_runtime_session_when_probe_is_unavailable()
    {
        var binding = new PlcsimAdvancedV7Binding(new StubEnvironmentProbe(
            PlcsimAdvancedV7EnvironmentProbeResult.Unavailable(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V7.RegistryEvidenceMissing",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V7 Runtime API registry evidence was not found.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v7-registry"),
                Array.Empty<PlcsimEvidence>())));
        binding.Initialize(CreateBindingContext());

        var runtime = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);

        Assert.False(runtime.Succeeded);
        Assert.Null(runtime.Session);
        Assert.Equal(PlcsimOperationStatus.Unavailable, runtime.Status);
        Assert.Contains(runtime.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V7.RegistryEvidenceMissing");
    }

    private static PlcsimAdvancedV7EnvironmentProbeResult ReadyProbeWithRuntimeApiDirectory(
        string runtimeApiDirectory)
    {
        return PlcsimAdvancedV7EnvironmentProbeResult.Ready(new[]
        {
            new PlcsimEvidence(
                "plcsim-advanced-v7-api-directory",
                "RuntimeApiDirectory",
                runtimeApiDirectory),
            new PlcsimEvidence(
                "plcsim-advanced-v7-runtime-api-dll",
                "RuntimeApiDll",
                Path.Combine(runtimeApiDirectory, "Siemens.Simatic.Simulation.Runtime.Api.x64.dll"))
        });
    }

    private static PlcsimRuntimeSessionOpenRequest CreateOpenRequest()
    {
        return new PlcsimRuntimeSessionOpenRequest(
            new PlcsimRuntimeApiExpectation("7.0", "7.0", SiemensRuntimeLocality.Local),
            new PlcsimRuntimeReference(null, "PlcUnderTest"),
            new PlcsimRuntimeStoragePreference(null),
            PlcsimRuntimeCleanupPreference.Keep,
            null);
    }

    private static string GetV7BindingManifestPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "access-profiles",
                "siemens",
                "plcsim-advanced",
                "v7",
                "src",
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7",
                SiemensBindingPackageDescriptorContract.FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            SiemensBindingPackageDescriptorContract.FileName);
    }

    private static SiemensBindingContext CreateBindingContext()
    {
        return new SiemensBindingContext(
            "siemens-test",
            new OperationalLoggingOptions(
                Path.Combine(
                    Path.GetTempPath(),
                    "PLCSIM.UnitTest",
                    Guid.NewGuid().ToString("N"),
                    "logs"),
                LogLevel.Debug,
                10 * 1024 * 1024,
                5,
                false));
    }

    private static SiemensBindingReadinessRequest CreateReadinessRequest(
        PlcsimAdvancedV7Binding binding)
    {
        return new SiemensBindingReadinessRequest(
            AppContext.BaseDirectory,
            typeof(PlcsimAdvancedV7Binding).Assembly.Location,
            binding.Declaration);
    }

    private sealed class StubEnvironmentProbe : IPlcsimAdvancedV7EnvironmentProbe
    {
        private readonly PlcsimAdvancedV7EnvironmentProbeResult result;

        public StubEnvironmentProbe(PlcsimAdvancedV7EnvironmentProbeResult result)
        {
            this.result = result;
        }

        public Task<PlcsimAdvancedV7EnvironmentProbeResult> ProbeAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class SequenceEnvironmentProbe : IPlcsimAdvancedV7EnvironmentProbe
    {
        private readonly Queue<PlcsimAdvancedV7EnvironmentProbeResult> results;

        public SequenceEnvironmentProbe(params PlcsimAdvancedV7EnvironmentProbeResult[] results)
        {
            this.results = new Queue<PlcsimAdvancedV7EnvironmentProbeResult>(results);
        }

        public int ProbeCount { get; private set; }

        public Task<PlcsimAdvancedV7EnvironmentProbeResult> ProbeAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCount++;
            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed class StubEnvironmentEvidenceSource
        : IPlcsimAdvancedV7EnvironmentEvidenceSource
    {
        public PlcsimAdvancedV7RegistryEvidence RegistryEvidence { get; init; } = new(
            true,
            "C:\\Program Files\\Siemens\\Automation\\PLCSIMADV");

        public Exception? RegistryException { get; init; }

        public Func<string, PlcsimAdvancedV7PathKind> PathInspection { get; init; } =
            path => path.EndsWith(
                PlcsimAdvancedV7EnvironmentProbe.RuntimeApiDllName,
                StringComparison.OrdinalIgnoreCase)
                ? PlcsimAdvancedV7PathKind.File
                : PlcsimAdvancedV7PathKind.Directory;

        public PlcsimAdvancedV7FileVersionEvidence FileVersionEvidence { get; init; } =
            new("7.0.0.0", "70.0.323.0");

        public PlcsimAdvancedV7ExportEvidence ExportEvidence { get; init; } =
            PlcsimAdvancedV7ExportEvidence.SucceededResult;

        public PlcsimAdvancedV7RegistryEvidence ReadRegistryEvidence()
        {
            if (RegistryException is not null)
            {
                throw RegistryException;
            }

            return RegistryEvidence;
        }

        public PlcsimAdvancedV7PathKind InspectPath(string path)
        {
            return PathInspection(path);
        }

        public PlcsimAdvancedV7FileVersionEvidence InspectFileVersion(string path)
        {
            return FileVersionEvidence;
        }

        public PlcsimAdvancedV7ExportEvidence InspectExports(string path)
        {
            return ExportEvidence;
        }
    }

    private sealed class CountingWorkerSessionHost : IPlcsimAdvancedV7WorkerSessionHost
    {
        public int OpenCount { get; private set; }

        public Task<PlcsimAdvancedV7WorkerSessionHostOpenResult> OpenAsync(
            string runtimeApiDirectory,
            SiemensBindingContext bindingContext,
            CancellationToken cancellationToken)
        {
            OpenCount++;
            throw new InvalidOperationException("The worker host must not be opened.");
        }
    }
}
