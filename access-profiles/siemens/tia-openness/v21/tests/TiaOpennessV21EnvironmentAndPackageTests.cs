using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21;
using System.Text.Json;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V21;

public sealed class TiaOpennessV21EnvironmentAndPackageTests
{
    [Fact]
    public void V21_binding_manifest_matches_binding_metadata_and_entry_point()
    {
        var binding = new TiaOpennessV21Binding();
        using var document = JsonDocument.Parse(File.ReadAllText(GetV21TiaBindingManifestPath()));

        var root = document.RootElement;
        Assert.Equal(SiemensBindingPackageDescriptorContract.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());

        var info = root.GetProperty("info");
        Assert.Equal(binding.Info.BindingKind, info.GetProperty("bindingKind").GetString());
        Assert.Equal(binding.Info.BindingVersion, info.GetProperty("bindingVersion").GetString());
        Assert.Equal(binding.Info.DisplayName, info.GetProperty("displayName").GetString());

        var entryPoint = root.GetProperty("entryPoint");
        Assert.Equal(Path.GetFileName(typeof(TiaOpennessV21Binding).Assembly.Location), entryPoint.GetProperty("assembly").GetString());
        Assert.Equal(typeof(TiaOpennessV21Binding).FullName, entryPoint.GetProperty("typeName").GetString());
        Assert.Same(
            typeof(TiaOpennessV21Binding),
            typeof(TiaOpennessV21Binding).Assembly.GetType(entryPoint.GetProperty("typeName").GetString()!));
        Assert.IsAssignableFrom<ITiaOpennessAuthorizationProbeBinding>(binding);
        Assert.True(
            root.GetProperty("setup").GetProperty("tiaOpennessAuthorizationProbe").GetBoolean());

        var projectSources = root
            .GetProperty("declaration")
            .GetProperty("projectSources")
            .EnumerateArray()
            .ToArray();
        Assert.Collection(
            projectSources.OrderBy(source => source.GetProperty("extension").GetString(), StringComparer.Ordinal),
            projectSource =>
            {
                Assert.Equal(".ap21", projectSource.GetProperty("extension").GetString());
                Assert.Equal("21", projectSource.GetProperty("version").GetString());
            },
            archiveSource =>
            {
                Assert.Equal(".zap21", archiveSource.GetProperty("extension").GetString());
                Assert.Equal("21", archiveSource.GetProperty("version").GetString());
                Assert.Equal("RetrievedArchive", archiveSource.GetProperty("openMode").GetString());
            });
        Assert.Equal(
            binding.Declaration.ProjectSources
                .OrderBy(source => source.Extension, StringComparer.Ordinal)
                .Select(source => (source.Kind, source.Extension, source.Version, source.OpenMode)),
            projectSources
                .OrderBy(source => source.GetProperty("extension").GetString(), StringComparer.Ordinal)
                .Select(source => (
                    source.GetProperty("kind").GetString()!,
                    source.GetProperty("extension").GetString()!,
                    source.GetProperty("version").GetString()!,
                    Enum.Parse<TiaProjectOpenMode>(source.GetProperty("openMode").GetString()!))));
    }

    [Fact]
    public void V21_registry_evidence_source_uses_documented_net48_key()
    {
        Assert.Equal(
            @"SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48",
            TiaOpennessV21RegistryEvidenceSource.RegistryPath);
    }

    [Fact]
    public async Task V21_environment_probe_maps_missing_documented_registry_evidence_to_unavailable()
    {
        var probe = new TiaOpennessV21EnvironmentProbe(
            new StubV21InstallationEvidenceSource(() => null),
            () => "unused-worker.exe");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TiaOpennessV21EnvironmentProbeState.Unavailable, result.State);
        Assert.Null(result.PublicApiDirectory);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V21.RegistryEvidenceMissing");
    }

    [Fact]
    public async Task V21_environment_probe_maps_registry_access_failure_to_error()
    {
        var probe = new TiaOpennessV21EnvironmentProbe(
            new StubV21InstallationEvidenceSource(
                () => throw new UnauthorizedAccessException("registry access denied")),
            () => "unused-worker.exe");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TiaOpennessV21EnvironmentProbeState.Failed, result.State);
        Assert.Null(result.PublicApiDirectory);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V21.RegistryReadFailed");
    }

    [Fact]
    public async Task V21_binding_uses_same_environment_probe_for_registration_and_open_revalidation()
    {
        var projectSource = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            Guid.NewGuid().ToString("N"),
            "sample.ap21");
        Directory.CreateDirectory(Path.GetDirectoryName(projectSource)!);
        File.WriteAllText(projectSource, string.Empty);
        var probe = new RecordingV21EnvironmentProbe(
            TiaOpennessV21EnvironmentProbeResult.Ready("recording-public-api", Array.Empty<TiaEvidence>()));
        var host = new RecordingV21EngineeringSessionHost(
            TiaOpennessV21EngineeringSessionHostOpenResult.SucceededResult(
                new StubV21EngineeringSessionHandle("tia-session-1", projectSource),
                Array.Empty<TiaEvidence>()));
        var binding = new TiaOpennessV21Binding(probe, host);
        binding.Initialize(new SiemensBindingContext("siemens", TiaOpennessV21TestSupport.DefaultOperationalLogging));

        var readiness = binding.ResolveReadiness(
            new SiemensBindingReadinessRequest(
                "package-root",
                "binding.dll",
                binding.Declaration));
        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectSource,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.True(readiness.IsUsable);
        Assert.True(open.Succeeded);
        Assert.Equal(2, probe.CallCount);
        Assert.Equal("recording-public-api", host.PublicApiDirectory);

        await open.Session!.CloseAsync(CancellationToken.None);
    }


    [Fact]
    public async Task V21_binding_authorization_probe_combines_environment_and_host_evidence()
    {
        var environmentEvidence = new TiaEvidence(
            "tia-openness-v21-public-api",
            "PublicApiDirectory",
            "recording-public-api");
        var hostEvidence = new TiaEvidence(
            "tia-openness-v21-authorization-worker",
            "AuthorizationProbeWorker",
            "worker.exe");
        var probe = new RecordingV21EnvironmentProbe(
            TiaOpennessV21EnvironmentProbeResult.Ready(
                "recording-public-api",
                new[] { environmentEvidence }));
        var authorizationHost = new RecordingV21AuthorizationProbeHost(
            TiaOpennessV21AuthorizationProbeHostResult.Authorized(new[] { hostEvidence }));
        var binding = new TiaOpennessV21Binding(
            probe,
            authorizationProbeHost: authorizationHost);
        binding.Initialize(new SiemensBindingContext(
            "siemens",
            TiaOpennessV21TestSupport.DefaultOperationalLogging));

        var result = await binding.ProbeAuthorizationAsync(
            new TiaAuthorizationProbeRequest("probe-1"),
            CancellationToken.None);

        Assert.Equal(TiaAuthorizationProbeStatus.Authorized, result.Status);
        Assert.Equal("recording-public-api", authorizationHost.PublicApiDirectory);
        Assert.Equal(new[] { environmentEvidence, hostEvidence }, result.Evidence);
    }

    [Fact]
    public async Task V21_binding_rejects_missing_project_source_before_environment_probe()
    {
        var probe = new RecordingV21EnvironmentProbe(
            TiaOpennessV21EnvironmentProbeResult.Ready("unused-public-api", Array.Empty<TiaEvidence>()));
        var binding = new TiaOpennessV21Binding(probe);

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                "sample.ap21",
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Null(open.Session);
        Assert.Equal(TiaOperationStatus.Failed, open.Status);
        Assert.Contains(open.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V21.ProjectSourceNotFound");
        Assert.False(probe.WasCalled);
    }

    [Fact]
    public async Task V21_binding_rejects_unsupported_project_source_extension()
    {
        var probe = new RecordingV21EnvironmentProbe(
            TiaOpennessV21EnvironmentProbeResult.Ready("unused-public-api", Array.Empty<TiaEvidence>()));
        var binding = new TiaOpennessV21Binding(probe);

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                GetV21TiaBindingManifestPath(),
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Null(open.Session);
        Assert.Equal(TiaOperationStatus.Failed, open.Status);
        Assert.Contains(open.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V21.ProjectSourceUnsupported");
        Assert.False(probe.WasCalled);
    }

    [Fact]
    public async Task V21_binding_reports_environment_probe_failure_for_existing_project_source()
    {
        var projectSource = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", Guid.NewGuid().ToString("N"), "sample.ap21");
        Directory.CreateDirectory(Path.GetDirectoryName(projectSource)!);
        File.WriteAllText(projectSource, string.Empty);
        var probe = new RecordingV21EnvironmentProbe(TiaOpennessV21EnvironmentProbeResult.Unavailable(
            new TiaDiagnostic(
                "TiaOpenness.V21.PublicApiNotFound",
                TiaDiagnosticSeverity.Error,
                "TIA Openness V21 PublicAPI net48 directory was not found.",
                "EnvironmentProbe",
                "tia-openness-v21-public-api"),
            Array.Empty<TiaEvidence>()));
        var binding = new TiaOpennessV21Binding(probe);

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectSource,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Null(open.Session);
        Assert.Equal(TiaOperationStatus.Failed, open.Status);
        Assert.Contains(open.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V21.PublicApiNotFound");
        Assert.True(probe.WasCalled);
    }

    private static string GetV21TiaBindingManifestPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "access-profiles",
                "siemens",
                "tia-openness",
                "v21",
                "src",
                "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21",
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

    private sealed class StubV21InstallationEvidenceSource : ITiaOpennessV21InstallationEvidenceSource
    {
        private readonly Func<TiaOpennessV21InstallationEvidence?> capture;

        public StubV21InstallationEvidenceSource(Func<TiaOpennessV21InstallationEvidence?> capture)
        {
            this.capture = capture;
        }

        public TiaOpennessV21InstallationEvidence? Capture()
        {
            return capture();
        }
    }

    private sealed class RecordingV21EngineeringSessionHost : ITiaOpennessV21EngineeringSessionHost
    {
        private readonly TiaOpennessV21EngineeringSessionHostOpenResult result;

        public RecordingV21EngineeringSessionHost(TiaOpennessV21EngineeringSessionHostOpenResult result)
        {
            this.result = result;
        }

        public string? PublicApiDirectory { get; private set; }

        public Task<TiaOpennessV21EngineeringSessionHostOpenResult> OpenAsync(
            string publicApiDirectory,
            TiaProjectOpenRequest request,
            SiemensBindingContext bindingContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublicApiDirectory = publicApiDirectory;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingV21AuthorizationProbeHost : ITiaOpennessV21AuthorizationProbeHost
    {
        private readonly TiaOpennessV21AuthorizationProbeHostResult result;

        public RecordingV21AuthorizationProbeHost(
            TiaOpennessV21AuthorizationProbeHostResult result)
        {
            this.result = result;
        }

        public string? PublicApiDirectory { get; private set; }

        public Task<TiaOpennessV21AuthorizationProbeHostResult> ProbeAsync(
            string publicApiDirectory,
            SiemensBindingContext bindingContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublicApiDirectory = publicApiDirectory;
            return Task.FromResult(result);
        }
    }

    private sealed class StubV21EngineeringSessionHandle : ITiaOpennessV21EngineeringSessionHandle
    {
        public StubV21EngineeringSessionHandle(string sessionId, string projectSource)
        {
            SessionId = sessionId;
            ProjectSource = projectSource;
        }

        public string SessionId { get; }

        public string ProjectSource { get; }

        public string ProductVersion => "21";

        public string ProjectVersion => "21";

        public Task<TiaOpennessV21TargetDiscoveryHostResult> DiscoverTargetsAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<TiaOpennessV21BuildHostResult> BuildAsync(
            TiaProjectTargetRef target,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<TiaOpennessV21SafetyAuthorizationRequirementHostResult> GetSafetyProgramAuthorizationRequirementAsync(
            TiaProjectTargetRef target,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<TiaOpennessV21SafetyAuthorizationHostResult> AuthorizeSafetyProgramAsync(
            TiaProjectTargetRef target,
            System.Security.SecureString password,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<TiaOpennessV21TransferHostResult> TransferAsync(
            TiaProjectTargetRef projectTarget,
            TiaRuntimeTransferTarget runtimeTarget,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
