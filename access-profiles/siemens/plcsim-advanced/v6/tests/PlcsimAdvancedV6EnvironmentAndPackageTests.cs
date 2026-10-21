using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6;
using PLCSIM.UnitTest.Common.Logging;
using System.Text.Json;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V6;

public sealed class PlcsimAdvancedV6EnvironmentAndPackageTests
{
    [Fact]
    public void V6_manifest_matches_binding_metadata_and_entry_point()
    {
        var binding = new PlcsimAdvancedV6Binding();
        using var document = JsonDocument.Parse(File.ReadAllText(GetV6BindingManifestPath()));

        var root = document.RootElement;
        Assert.Equal(SiemensBindingPackageDescriptorContract.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());

        var info = root.GetProperty("info");
        Assert.Equal(binding.Info.BindingKind, info.GetProperty("bindingKind").GetString());
        Assert.Equal(binding.Info.BindingVersion, info.GetProperty("bindingVersion").GetString());
        Assert.Equal(binding.Info.DisplayName, info.GetProperty("displayName").GetString());

        var entryPoint = root.GetProperty("entryPoint");
        Assert.Equal(Path.GetFileName(typeof(PlcsimAdvancedV6Binding).Assembly.Location), entryPoint.GetProperty("assembly").GetString());
        Assert.Equal(typeof(PlcsimAdvancedV6Binding).FullName, entryPoint.GetProperty("typeName").GetString());
        Assert.Same(
            typeof(PlcsimAdvancedV6Binding),
            typeof(PlcsimAdvancedV6Binding).Assembly.GetType(entryPoint.GetProperty("typeName").GetString()!));

        var runtime = Assert.Single(root
            .GetProperty("declaration")
            .GetProperty("runtimes")
            .EnumerateArray());
        Assert.Equal(SiemensBindingKinds.PlcSimAdvanced, runtime.GetProperty("kind").GetString());
        Assert.Equal("6.0", runtime.GetProperty("version").GetString());
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
    public void V6_binding_maps_ready_probe_to_usable_readiness()
    {
        var binding = new PlcsimAdvancedV6Binding(new StubEnvironmentProbe(
            PlcsimAdvancedV6EnvironmentProbeResult.Ready(new[]
            {
                new PlcsimEvidence(
                    "plcsim-advanced-v6-runtime-api-dll",
                    "RuntimeApiDll",
                    "C:\\PLCSIMADV\\API\\6.0\\Siemens.Simatic.Simulation.Runtime.Api.x64.dll")
            })));

        var readiness = binding.ResolveReadiness(CreateReadinessRequest(binding));

        Assert.Equal(SiemensBindingReadinessState.Usable, readiness.State);
        Assert.Empty(readiness.Diagnostics);
        Assert.Contains("Runtime API evidence is usable", readiness.EvidenceSummary);
    }

    [Fact]
    public void V6_binding_maps_unavailable_probe_to_unavailable_readiness()
    {
        var binding = new PlcsimAdvancedV6Binding(new StubEnvironmentProbe(
            PlcsimAdvancedV6EnvironmentProbeResult.Unavailable(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V6.RegistryEvidenceMissing",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V6 Runtime API registry evidence was not found.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v6-registry"),
                Array.Empty<PlcsimEvidence>())));

        var readiness = binding.ResolveReadiness(CreateReadinessRequest(binding));

        Assert.Equal(SiemensBindingReadinessState.Unavailable, readiness.State);
        Assert.Empty(readiness.Diagnostics);
        Assert.Equal("PLCSIM Advanced V6 Runtime API registry evidence was not found.", readiness.EvidenceSummary);
    }

    [Fact]
    public void V6_binding_maps_failed_probe_to_error_readiness()
    {
        var binding = new PlcsimAdvancedV6Binding(new StubEnvironmentProbe(
            PlcsimAdvancedV6EnvironmentProbeResult.Failed(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V6.RuntimeApiDllLoadFailed",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V6 Runtime API DLL could not be loaded for export inspection.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v6-runtime-api-exports"),
                Array.Empty<PlcsimEvidence>())));

        var readiness = binding.ResolveReadiness(CreateReadinessRequest(binding));

        Assert.Equal(SiemensBindingReadinessState.Error, readiness.State);
        var diagnostic = Assert.Single(readiness.Diagnostics);
        Assert.Equal("PlcsimAdvanced.V6.RuntimeApiDllLoadFailed", diagnostic.Code);
    }

    [Fact]
    public async Task V6_environment_probe_classifies_complete_evidence_as_ready()
    {
        var source = new StubEnvironmentEvidenceSource();
        var probe = new PlcsimAdvancedV6EnvironmentProbe(source);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(PlcsimAdvancedV6EnvironmentProbeState.Ready, result.State);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "RuntimeApiDirectory");
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "RuntimeApiDll");
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "RuntimeApiExports");
    }

    [Fact]
    public async Task V6_environment_probe_rejects_compatibility_product_version_as_unavailable()
    {
        var source = new StubEnvironmentEvidenceSource
        {
            FileVersionEvidence = new("6.0.0.1", "60.0.319.0")
        };
        var probe = new PlcsimAdvancedV6EnvironmentProbe(source);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(PlcsimAdvancedV6EnvironmentProbeState.Unavailable, result.State);
        Assert.Equal("PlcsimAdvanced.V6.ProductVersionMismatch", Assert.Single(result.Diagnostics).Code);
        Assert.Contains(result.Evidence, evidence =>
            evidence.Kind == "RuntimeApiDll"
            && evidence.Summary.Contains("ProductVersion=6.0.0.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task V6_environment_probe_classifies_missing_registry_as_unavailable()
    {
        var source = new StubEnvironmentEvidenceSource
        {
            RegistryEvidence = new PlcsimAdvancedV6RegistryEvidence(false, null)
        };
        var probe = new PlcsimAdvancedV6EnvironmentProbe(source);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(PlcsimAdvancedV6EnvironmentProbeState.Unavailable, result.State);
        Assert.Equal("PlcsimAdvanced.V6.RegistryEvidenceMissing", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task V6_environment_probe_classifies_registry_access_failure_as_failed()
    {
        var source = new StubEnvironmentEvidenceSource
        {
            RegistryException = new UnauthorizedAccessException("registry denied")
        };
        var probe = new PlcsimAdvancedV6EnvironmentProbe(source);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(PlcsimAdvancedV6EnvironmentProbeState.Failed, result.State);
        Assert.Equal("PlcsimAdvanced.V6.RegistryReadFailed", Assert.Single(result.Diagnostics).Code);
        Assert.Contains(result.Evidence, evidence => evidence.Summary == "registry denied");
    }

    [Fact]
    public async Task V6_environment_probe_classifies_missing_api_directory_as_unavailable()
    {
        var source = new StubEnvironmentEvidenceSource
        {
            PathInspection = _ => PlcsimAdvancedV6PathKind.Missing
        };
        var probe = new PlcsimAdvancedV6EnvironmentProbe(source);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(PlcsimAdvancedV6EnvironmentProbeState.Unavailable, result.State);
        Assert.Equal("PlcsimAdvanced.V6.ApiDirectoryMissing", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task V6_environment_probe_classifies_dll_path_access_failure_as_failed()
    {
        var source = new StubEnvironmentEvidenceSource
        {
            PathInspection = path => path.EndsWith(
                PlcsimAdvancedV6EnvironmentProbe.RuntimeApiDllName,
                StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("dll denied")
                : PlcsimAdvancedV6PathKind.Directory
        };
        var probe = new PlcsimAdvancedV6EnvironmentProbe(source);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(PlcsimAdvancedV6EnvironmentProbeState.Failed, result.State);
        Assert.Equal("PlcsimAdvanced.V6.RuntimeApiDllInspectFailed", Assert.Single(result.Diagnostics).Code);
        Assert.Contains(result.Evidence, evidence => evidence.Summary == "dll denied");
    }

    [Fact]
    public async Task V6_environment_probe_classifies_export_load_failure_as_failed()
    {
        var source = new StubEnvironmentEvidenceSource
        {
            ExportEvidence = PlcsimAdvancedV6ExportEvidence.Failed(
                "PlcsimAdvanced.V6.RuntimeApiDllLoadFailed",
                "Runtime API could not be loaded.",
                "bad image")
        };
        var probe = new PlcsimAdvancedV6EnvironmentProbe(source);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(PlcsimAdvancedV6EnvironmentProbeState.Failed, result.State);
        Assert.Equal("PlcsimAdvanced.V6.RuntimeApiDllLoadFailed", Assert.Single(result.Diagnostics).Code);
        Assert.Contains(result.Evidence, evidence => evidence.Summary == "bad image");
    }

    [Fact]
    public async Task V6_binding_revalidates_environment_before_opening_worker_session()
    {
        var ready = ReadyProbeWithRuntimeApiDirectory("C:\\PLCSIMADV\\API\\6.0");
        var unavailable = PlcsimAdvancedV6EnvironmentProbeResult.Unavailable(
            new PlcsimDiagnostic(
                "PlcsimAdvanced.V6.RuntimeApiDllMissing",
                PlcsimDiagnosticSeverity.Error,
                "PLCSIM Advanced V6 x64 Runtime API DLL was not found.",
                "EnvironmentProbe",
                "plcsim-advanced-v6-runtime-api-dll"),
            Array.Empty<PlcsimEvidence>());
        var probe = new SequenceEnvironmentProbe(ready, unavailable);
        var host = new CountingWorkerSessionHost();
        var binding = new PlcsimAdvancedV6Binding(probe, host);
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
    public async Task V6_binding_fails_runtime_session_when_probe_is_unavailable()
    {
        var binding = new PlcsimAdvancedV6Binding(new StubEnvironmentProbe(
            PlcsimAdvancedV6EnvironmentProbeResult.Unavailable(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V6.RegistryEvidenceMissing",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V6 Runtime API registry evidence was not found.",
                    "EnvironmentProbe",
                    "plcsim-advanced-v6-registry"),
                Array.Empty<PlcsimEvidence>())));
        binding.Initialize(CreateBindingContext());

        var runtime = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);

        Assert.False(runtime.Succeeded);
        Assert.Null(runtime.Session);
        Assert.Equal(PlcsimOperationStatus.Unavailable, runtime.Status);
        Assert.Contains(runtime.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.RegistryEvidenceMissing");
    }

    private static PlcsimAdvancedV6EnvironmentProbeResult ReadyProbeWithRuntimeApiDirectory(
        string runtimeApiDirectory)
    {
        return PlcsimAdvancedV6EnvironmentProbeResult.Ready(new[]
        {
            new PlcsimEvidence(
                "plcsim-advanced-v6-api-directory",
                "RuntimeApiDirectory",
                runtimeApiDirectory),
            new PlcsimEvidence(
                "plcsim-advanced-v6-runtime-api-dll",
                "RuntimeApiDll",
                Path.Combine(runtimeApiDirectory, "Siemens.Simatic.Simulation.Runtime.Api.x64.dll"))
        });
    }

    private static PlcsimRuntimeSessionOpenRequest CreateOpenRequest()
    {
        return new PlcsimRuntimeSessionOpenRequest(
            new PlcsimRuntimeApiExpectation("6.0", "6.0", SiemensRuntimeLocality.Local),
            new PlcsimRuntimeReference(null, "PlcUnderTest"),
            new PlcsimRuntimeStoragePreference(null),
            PlcsimRuntimeCleanupPreference.Keep,
            null);
    }

    private static string GetV6BindingManifestPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "access-profiles",
                "siemens",
                "plcsim-advanced",
                "v6",
                "src",
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6",
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
        PlcsimAdvancedV6Binding binding)
    {
        return new SiemensBindingReadinessRequest(
            AppContext.BaseDirectory,
            typeof(PlcsimAdvancedV6Binding).Assembly.Location,
            binding.Declaration);
    }

    private sealed class StubEnvironmentProbe : IPlcsimAdvancedV6EnvironmentProbe
    {
        private readonly PlcsimAdvancedV6EnvironmentProbeResult result;

        public StubEnvironmentProbe(PlcsimAdvancedV6EnvironmentProbeResult result)
        {
            this.result = result;
        }

        public Task<PlcsimAdvancedV6EnvironmentProbeResult> ProbeAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class SequenceEnvironmentProbe : IPlcsimAdvancedV6EnvironmentProbe
    {
        private readonly Queue<PlcsimAdvancedV6EnvironmentProbeResult> results;

        public SequenceEnvironmentProbe(params PlcsimAdvancedV6EnvironmentProbeResult[] results)
        {
            this.results = new Queue<PlcsimAdvancedV6EnvironmentProbeResult>(results);
        }

        public int ProbeCount { get; private set; }

        public Task<PlcsimAdvancedV6EnvironmentProbeResult> ProbeAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCount++;
            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed class StubEnvironmentEvidenceSource
        : IPlcsimAdvancedV6EnvironmentEvidenceSource
    {
        public PlcsimAdvancedV6RegistryEvidence RegistryEvidence { get; init; } = new(
            true,
            "C:\\Program Files\\Siemens\\Automation\\PLCSIMADV");

        public Exception? RegistryException { get; init; }

        public Func<string, PlcsimAdvancedV6PathKind> PathInspection { get; init; } =
            path => path.EndsWith(
                PlcsimAdvancedV6EnvironmentProbe.RuntimeApiDllName,
                StringComparison.OrdinalIgnoreCase)
                ? PlcsimAdvancedV6PathKind.File
                : PlcsimAdvancedV6PathKind.Directory;

        public PlcsimAdvancedV6FileVersionEvidence FileVersionEvidence { get; init; } =
            new("6.0.0.0", "60.0.294.0");

        public PlcsimAdvancedV6ExportEvidence ExportEvidence { get; init; } =
            PlcsimAdvancedV6ExportEvidence.SucceededResult;

        public PlcsimAdvancedV6RegistryEvidence ReadRegistryEvidence()
        {
            if (RegistryException is not null)
            {
                throw RegistryException;
            }

            return RegistryEvidence;
        }

        public PlcsimAdvancedV6PathKind InspectPath(string path)
        {
            return PathInspection(path);
        }

        public PlcsimAdvancedV6FileVersionEvidence InspectFileVersion(string path)
        {
            return FileVersionEvidence;
        }

        public PlcsimAdvancedV6ExportEvidence InspectExports(string path)
        {
            return ExportEvidence;
        }
    }

    private sealed class CountingWorkerSessionHost : IPlcsimAdvancedV6WorkerSessionHost
    {
        public int OpenCount { get; private set; }

        public Task<PlcsimAdvancedV6WorkerSessionHostOpenResult> OpenAsync(
            string runtimeApiDirectory,
            SiemensBindingContext bindingContext,
            CancellationToken cancellationToken)
        {
            OpenCount++;
            throw new InvalidOperationException("The worker host must not be opened.");
        }
    }
}
