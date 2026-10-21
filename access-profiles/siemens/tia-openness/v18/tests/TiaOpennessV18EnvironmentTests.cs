using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V18;

public sealed class TiaOpennessV18EnvironmentTests
{
    [Fact]
    public void Registry_source_uses_documented_v18_public_api_key()
    {
        Assert.Equal(
            @"SOFTWARE\Siemens\Automation\Openness\18.0\PublicAPI\18.0.0.0",
            TiaOpennessV18RegistryEvidenceSource.RegistryPath);
    }

    [Fact]
    public async Task Missing_registry_evidence_is_unavailable()
    {
        var probe = new TiaOpennessV18EnvironmentProbe(
            new StubEvidenceSource(() => null),
            () => "unused-worker.exe");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TiaOpennessV18EnvironmentProbeState.Unavailable, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V18.RegistryEvidenceMissing");
    }

    [Fact]
    public async Task Registry_access_failure_is_failed()
    {
        var probe = new TiaOpennessV18EnvironmentProbe(
            new StubEvidenceSource(() => throw new UnauthorizedAccessException("denied")),
            () => "unused-worker.exe");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TiaOpennessV18EnvironmentProbeState.Failed, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V18.RegistryReadFailed");
    }

    [Fact]
    public void Binding_readiness_preserves_registry_failure_detail()
    {
        var probe = new TiaOpennessV18EnvironmentProbe(
            new StubEvidenceSource(() => throw new UnauthorizedAccessException("registry access denied")),
            () => "unused-worker.exe");
        var binding = new TiaOpennessV18Binding(probe);

        var result = binding.ResolveReadiness(new SiemensBindingReadinessRequest(
            "package-root",
            "entry-assembly",
            binding.Declaration));

        Assert.Equal(SiemensBindingReadinessState.Error, result.State);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "TiaOpenness.V18.RegistryReadFailed"
                && diagnostic.TechnicalDetail == "registry access denied");
    }

    [Fact]
    public async Task Wrong_registry_version_is_failed_before_file_access()
    {
        var probe = new TiaOpennessV18EnvironmentProbe(
            new StubEvidenceSource(() => new TiaOpennessV18InstallationEvidence(
                "20.0.0.0",
                "d29ec89bac048f84",
                "missing-Siemens.Engineering.dll")),
            () => "unused-worker.exe");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TiaOpennessV18EnvironmentProbeState.Failed, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V18.RegistryVersionMismatch");
    }

    private sealed class StubEvidenceSource : ITiaOpennessV18InstallationEvidenceSource
    {
        private readonly Func<TiaOpennessV18InstallationEvidence?> capture;

        public StubEvidenceSource(Func<TiaOpennessV18InstallationEvidence?> capture)
        {
            this.capture = capture;
        }

        public TiaOpennessV18InstallationEvidence? Capture() => capture();
    }
}
