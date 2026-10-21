using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V19;

public sealed class TiaOpennessV19EnvironmentTests
{
    [Fact]
    public void Registry_source_uses_documented_v19_public_api_key()
    {
        Assert.Equal(
            @"SOFTWARE\Siemens\Automation\Openness\19.0\PublicAPI\19.0.0.0",
            TiaOpennessV19RegistryEvidenceSource.RegistryPath);
    }

    [Fact]
    public async Task Missing_registry_evidence_is_unavailable()
    {
        var probe = new TiaOpennessV19EnvironmentProbe(
            new StubEvidenceSource(() => null),
            () => "unused-worker.exe");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TiaOpennessV19EnvironmentProbeState.Unavailable, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V19.RegistryEvidenceMissing");
    }

    [Fact]
    public async Task Registry_access_failure_is_failed()
    {
        var probe = new TiaOpennessV19EnvironmentProbe(
            new StubEvidenceSource(() => throw new UnauthorizedAccessException("denied")),
            () => "unused-worker.exe");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TiaOpennessV19EnvironmentProbeState.Failed, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V19.RegistryReadFailed");
    }

    [Fact]
    public void Binding_readiness_preserves_registry_failure_detail()
    {
        var probe = new TiaOpennessV19EnvironmentProbe(
            new StubEvidenceSource(() => throw new UnauthorizedAccessException("registry access denied")),
            () => "unused-worker.exe");
        var binding = new TiaOpennessV19Binding(probe);

        var result = binding.ResolveReadiness(new SiemensBindingReadinessRequest(
            "package-root",
            "entry-assembly",
            binding.Declaration));

        Assert.Equal(SiemensBindingReadinessState.Error, result.State);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "TiaOpenness.V19.RegistryReadFailed"
                && diagnostic.TechnicalDetail == "registry access denied");
    }

    [Fact]
    public async Task Wrong_registry_version_is_failed_before_file_access()
    {
        var probe = new TiaOpennessV19EnvironmentProbe(
            new StubEvidenceSource(() => new TiaOpennessV19InstallationEvidence(
                "20.0.0.0",
                "d29ec89bac048f84",
                "missing-Siemens.Engineering.dll")),
            () => "unused-worker.exe");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TiaOpennessV19EnvironmentProbeState.Failed, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V19.RegistryVersionMismatch");
    }

    private sealed class StubEvidenceSource : ITiaOpennessV19InstallationEvidenceSource
    {
        private readonly Func<TiaOpennessV19InstallationEvidence?> capture;

        public StubEvidenceSource(Func<TiaOpennessV19InstallationEvidence?> capture)
        {
            this.capture = capture;
        }

        public TiaOpennessV19InstallationEvidence? Capture() => capture();
    }
}
