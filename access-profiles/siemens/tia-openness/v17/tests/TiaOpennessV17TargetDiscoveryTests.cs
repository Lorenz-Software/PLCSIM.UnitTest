using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V17;

public sealed class TiaOpennessV17TargetDiscoveryTests
{
    [Fact]
    public async Task V17_engineering_session_selects_single_supported_target_without_plc_ref()
    {
        var session = new TiaOpennessV17EngineeringSession(
            new RecordingV17EngineeringSessionHandle(
                "tia-session-1",
                targets: new[] { TiaOpennessV17TestSupport.CreateTarget("plc-1") }));

        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(null),
            CancellationToken.None);

        Assert.True(discovery.Succeeded);
        var target = Assert.Single(discovery.Targets);
        Assert.Equal(target, discovery.SelectedTarget);
        Assert.Equal("plc-1", target.Id);
    }

    [Fact]
    public async Task V17_engineering_session_resolves_configured_plc_ref_exactly()
    {
        var session = new TiaOpennessV17EngineeringSession(
            new RecordingV17EngineeringSessionHandle(
                "tia-session-1",
                targets: new[]
                {
                    TiaOpennessV17TestSupport.CreateTarget("PLC-1"),
                    TiaOpennessV17TestSupport.CreateTarget("plc-1")
                }));

        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("plc-1"),
            CancellationToken.None);

        Assert.True(discovery.Succeeded);
        Assert.Equal("plc-1", discovery.SelectedTarget?.Id);
    }

    [Fact]
    public async Task V17_engineering_session_filters_non_plc_targets_before_selection()
    {
        var plcTarget = TiaOpennessV17TestSupport.CreateTarget("plc-1");
        var nonPlcTarget = new TiaOpennessV17TargetProbe(
            "hmi-1",
            "HMI 1",
            "Project/Line/HMI_1",
            "S7-1500",
            "HMI",
            "HMI",
            "HMI",
            false);
        var session = new TiaOpennessV17EngineeringSession(
            new RecordingV17EngineeringSessionHandle(
                "tia-session-1",
                targets: new[] { nonPlcTarget, plcTarget }));

        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(null),
            CancellationToken.None);

        var selected = Assert.Single(discovery.Targets);
        Assert.Equal(plcTarget.Id, selected.Id);
        Assert.Equal(plcTarget.TechnicalPath, selected.TechnicalPath);
        Assert.Same(selected, discovery.SelectedTarget);
        Assert.Contains(discovery.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V17.TargetUnsupported");
    }

    [Fact]
    public async Task V17_engineering_session_reports_missing_configured_plc_ref()
    {
        var session = new TiaOpennessV17EngineeringSession(
            new RecordingV17EngineeringSessionHandle(
                "tia-session-1",
                targets: new[] { TiaOpennessV17TestSupport.CreateTarget("plc-1") }));

        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest("missing-plc"),
            CancellationToken.None);

        Assert.False(discovery.Succeeded);
        Assert.Null(discovery.SelectedTarget);
        Assert.Contains(discovery.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V17.TargetNotFound");
    }

    [Fact]
    public async Task V17_engineering_session_reports_ambiguous_target_when_no_plc_ref_is_configured()
    {
        var session = new TiaOpennessV17EngineeringSession(
            new RecordingV17EngineeringSessionHandle(
                "tia-session-1",
                targets: new[]
                {
                    TiaOpennessV17TestSupport.CreateTarget("plc-1"),
                    TiaOpennessV17TestSupport.CreateTarget("plc-2")
                }));

        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(null),
            CancellationToken.None);

        Assert.False(discovery.Succeeded);
        Assert.Null(discovery.SelectedTarget);
        Assert.Contains(discovery.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V17.TargetAmbiguous");
    }

}
