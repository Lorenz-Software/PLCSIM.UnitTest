using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V15_1;

public sealed class TiaOpennessV15_1EnvironmentTests
{
    [Fact]
    public void Registry_source_uses_documented_v15_1_public_api_key()
    {
        Assert.Equal(
            @"SOFTWARE\Siemens\Automation\Openness\15.1\PublicAPI\15.1.0.0",
            TiaOpennessV15_1RegistryEvidenceSource.RegistryPath);
    }

    [Fact]
    public async Task Missing_registry_evidence_is_unavailable()
    {
        var probe = new TiaOpennessV15_1EnvironmentProbe(
            new StubEvidenceSource(() => null),
            () => "unused-worker.exe");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TiaOpennessV15_1EnvironmentProbeState.Unavailable, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V15_1.RegistryEvidenceMissing");
    }

    [Fact]
    public async Task Registry_access_failure_is_failed()
    {
        var probe = new TiaOpennessV15_1EnvironmentProbe(
            new StubEvidenceSource(() => throw new UnauthorizedAccessException("denied")),
            () => "unused-worker.exe");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TiaOpennessV15_1EnvironmentProbeState.Failed, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V15_1.RegistryReadFailed");
    }

    [Fact]
    public void Binding_readiness_preserves_registry_failure_detail()
    {
        var probe = new TiaOpennessV15_1EnvironmentProbe(
            new StubEvidenceSource(() => throw new UnauthorizedAccessException("registry access denied")),
            () => "unused-worker.exe");
        var binding = new TiaOpennessV15_1Binding(probe);

        var result = binding.ResolveReadiness(new SiemensBindingReadinessRequest(
            "package-root",
            "entry-assembly",
            binding.Declaration));

        Assert.Equal(SiemensBindingReadinessState.Error, result.State);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "TiaOpenness.V15_1.RegistryReadFailed"
                && diagnostic.TechnicalDetail == "registry access denied");
    }

    [Fact]
    public async Task Wrong_registry_version_is_failed_before_file_access()
    {
        var probe = new TiaOpennessV15_1EnvironmentProbe(
            new StubEvidenceSource(() => new TiaOpennessV15_1InstallationEvidence(
                "20.0.0.0",
                "d29ec89bac048f84",
                "missing-Siemens.Engineering.dll")),
            () => "unused-worker.exe");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TiaOpennessV15_1EnvironmentProbeState.Failed, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V15_1.RegistryVersionMismatch");
    }

    [Fact]
    public async Task Binding_uses_same_environment_probe_for_registration_and_open_revalidation()
    {
        var projectSource = CreateProjectSource(".ap15_1");
        var probe = new RecordingV15_1EnvironmentProbe(
            TiaOpennessV15_1EnvironmentProbeResult.Ready(
                "recording-public-api",
                Array.Empty<TiaEvidence>()));
        var host = new RecordingEngineeringSessionHost(
            TiaOpennessV15_1EngineeringSessionHostOpenResult.SucceededResult(
                new RecordingV15_1EngineeringSessionHandle(
                    "tia-session-1",
                    projectSource: projectSource),
                Array.Empty<TiaEvidence>()));
        var binding = new TiaOpennessV15_1Binding(probe, host);
        binding.Initialize(new SiemensBindingContext(
            "siemens",
            TiaOpennessV15_1TestSupport.DefaultOperationalLogging));

        var readiness = binding.ResolveReadiness(new SiemensBindingReadinessRequest(
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
    public async Task Binding_authorization_probe_combines_environment_and_host_evidence()
    {
        var environmentEvidence = new TiaEvidence(
            "tia-openness-v15_1-public-api",
            "PublicApiDirectory",
            "recording-public-api");
        var hostEvidence = new TiaEvidence(
            "tia-openness-v15_1-authorization-worker",
            "AuthorizationProbeWorker",
            "worker.exe");
        var probe = new RecordingV15_1EnvironmentProbe(
            TiaOpennessV15_1EnvironmentProbeResult.Ready(
                "recording-public-api",
                new[] { environmentEvidence }));
        var authorizationHost = new RecordingAuthorizationProbeHost(
            TiaOpennessV15_1AuthorizationProbeHostResult.Authorized(
                new[] { hostEvidence }));
        var binding = new TiaOpennessV15_1Binding(
            probe,
            authorizationProbeHost: authorizationHost);
        binding.Initialize(new SiemensBindingContext(
            "siemens",
            TiaOpennessV15_1TestSupport.DefaultOperationalLogging));

        var result = await binding.ProbeAuthorizationAsync(
            new TiaAuthorizationProbeRequest("probe-1"),
            CancellationToken.None);

        Assert.Equal(TiaAuthorizationProbeStatus.Authorized, result.Status);
        Assert.Equal("recording-public-api", authorizationHost.PublicApiDirectory);
        Assert.Equal(new[] { environmentEvidence, hostEvidence }, result.Evidence);
    }

    [Fact]
    public async Task Binding_rejects_missing_project_source_before_environment_probe()
    {
        var probe = new RecordingV15_1EnvironmentProbe(
            TiaOpennessV15_1EnvironmentProbeResult.Ready(
                "unused-public-api",
                Array.Empty<TiaEvidence>()));
        var binding = new TiaOpennessV15_1Binding(probe);
        var missingProject = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            Guid.NewGuid().ToString("N"),
            "missing.ap15_1");

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                missingProject,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Null(open.Session);
        Assert.Equal(TiaOperationStatus.Failed, open.Status);
        Assert.Contains(
            open.Diagnostics,
            diagnostic => diagnostic.Code == "TiaOpenness.V15_1.ProjectSourceNotFound");
        Assert.False(probe.WasCalled);
    }

    [Fact]
    public async Task Binding_rejects_unsupported_project_source_extension_before_environment_probe()
    {
        var probe = new RecordingV15_1EnvironmentProbe(
            TiaOpennessV15_1EnvironmentProbeResult.Ready(
                "unused-public-api",
                Array.Empty<TiaEvidence>()));
        var binding = new TiaOpennessV15_1Binding(probe);
        var unsupportedProject = CreateProjectSource(".txt");

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                unsupportedProject,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Null(open.Session);
        Assert.Equal(TiaOperationStatus.Failed, open.Status);
        Assert.Contains(
            open.Diagnostics,
            diagnostic => diagnostic.Code == "TiaOpenness.V15_1.ProjectSourceUnsupported");
        Assert.False(probe.WasCalled);
    }

    [Fact]
    public async Task Binding_reports_environment_probe_failure_for_existing_project_source()
    {
        var projectSource = CreateProjectSource(".ap15_1");
        var probe = new RecordingV15_1EnvironmentProbe(
            TiaOpennessV15_1EnvironmentProbeResult.Unavailable(
                new TiaDiagnostic(
                    "TiaOpenness.V15_1.PublicApiNotFound",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V15.1 PublicAPI was not found.",
                    "EnvironmentProbe",
                    "tia-openness-v15_1-public-api"),
                Array.Empty<TiaEvidence>()));
        var binding = new TiaOpennessV15_1Binding(probe);

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
        Assert.Contains(
            open.Diagnostics,
            diagnostic => diagnostic.Code == "TiaOpenness.V15_1.PublicApiNotFound");
        Assert.True(probe.WasCalled);
    }

    private static string CreateProjectSource(string extension)
    {
        var projectSource = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            Guid.NewGuid().ToString("N"),
            "sample" + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(projectSource)!);
        File.WriteAllText(projectSource, string.Empty);
        return projectSource;
    }

    private sealed class RecordingEngineeringSessionHost : ITiaOpennessV15_1EngineeringSessionHost
    {
        private readonly TiaOpennessV15_1EngineeringSessionHostOpenResult result;

        public RecordingEngineeringSessionHost(
            TiaOpennessV15_1EngineeringSessionHostOpenResult result)
        {
            this.result = result;
        }

        public string? PublicApiDirectory { get; private set; }

        public Task<TiaOpennessV15_1EngineeringSessionHostOpenResult> OpenAsync(
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

    private sealed class RecordingAuthorizationProbeHost : ITiaOpennessV15_1AuthorizationProbeHost
    {
        private readonly TiaOpennessV15_1AuthorizationProbeHostResult result;

        public RecordingAuthorizationProbeHost(
            TiaOpennessV15_1AuthorizationProbeHostResult result)
        {
            this.result = result;
        }

        public string? PublicApiDirectory { get; private set; }

        public Task<TiaOpennessV15_1AuthorizationProbeHostResult> ProbeAsync(
            string publicApiDirectory,
            SiemensBindingContext bindingContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublicApiDirectory = publicApiDirectory;
            return Task.FromResult(result);
        }
    }

    private sealed class StubEvidenceSource : ITiaOpennessV15_1InstallationEvidenceSource
    {
        private readonly Func<TiaOpennessV15_1InstallationEvidence?> capture;

        public StubEvidenceSource(Func<TiaOpennessV15_1InstallationEvidence?> capture)
        {
            this.capture = capture;
        }

        public TiaOpennessV15_1InstallationEvidence? Capture() => capture();
    }
}
