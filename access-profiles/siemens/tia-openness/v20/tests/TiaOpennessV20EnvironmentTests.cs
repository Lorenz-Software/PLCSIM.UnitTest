using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V20;

public sealed class TiaOpennessV20EnvironmentTests
{
    [Fact]
    public void Registry_source_uses_documented_v20_public_api_key()
    {
        Assert.Equal(
            @"SOFTWARE\Siemens\Automation\Openness\20.0\PublicAPI\20.0.0.0",
            TiaOpennessV20RegistryEvidenceSource.RegistryPath);
    }

    [Fact]
    public async Task Missing_registry_evidence_is_unavailable()
    {
        var probe = new TiaOpennessV20EnvironmentProbe(
            new StubEvidenceSource(() => null),
            () => "unused-worker.exe");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TiaOpennessV20EnvironmentProbeState.Unavailable, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V20.RegistryEvidenceMissing");
    }

    [Fact]
    public async Task Registry_access_failure_is_failed()
    {
        var probe = new TiaOpennessV20EnvironmentProbe(
            new StubEvidenceSource(() => throw new UnauthorizedAccessException("denied")),
            () => "unused-worker.exe");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TiaOpennessV20EnvironmentProbeState.Failed, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V20.RegistryReadFailed");
    }

    [Fact]
    public void Binding_readiness_preserves_registry_failure_detail()
    {
        var probe = new TiaOpennessV20EnvironmentProbe(
            new StubEvidenceSource(() => throw new UnauthorizedAccessException("registry access denied")),
            () => "unused-worker.exe");
        var binding = new TiaOpennessV20Binding(probe);

        var result = binding.ResolveReadiness(new SiemensBindingReadinessRequest(
            "package-root",
            "entry-assembly",
            binding.Declaration));

        Assert.Equal(SiemensBindingReadinessState.Error, result.State);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "TiaOpenness.V20.RegistryReadFailed"
                && diagnostic.TechnicalDetail == "registry access denied");
    }

    [Fact]
    public async Task Wrong_registry_version_is_failed_before_file_access()
    {
        var probe = new TiaOpennessV20EnvironmentProbe(
            new StubEvidenceSource(() => new TiaOpennessV20InstallationEvidence(
                "19.0.0.0",
                "d29ec89bac048f84",
                "missing-Siemens.Engineering.dll")),
            () => "unused-worker.exe");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TiaOpennessV20EnvironmentProbeState.Failed, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V20.RegistryVersionMismatch");
    }

    private sealed class StubEvidenceSource : ITiaOpennessV20InstallationEvidenceSource
    {
        private readonly Func<TiaOpennessV20InstallationEvidence?> capture;

        public StubEvidenceSource(Func<TiaOpennessV20InstallationEvidence?> capture)
        {
            this.capture = capture;
        }

        public TiaOpennessV20InstallationEvidence? Capture() => capture();
    }
}
