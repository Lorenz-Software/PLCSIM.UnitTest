using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V15_1;

public sealed class TiaOpennessV15_1ProjectSessionTests
{
    [Theory]
    [InlineData("sample.ap15_1", TiaProjectOpenMode.RetrievedArchive)]
    [InlineData("sample.zap15_1", TiaProjectOpenMode.DirectProject)]
    public async Task V15_1_binding_rejects_project_source_open_mode_mismatch_before_host_start(
        string fileName,
        TiaProjectOpenMode openMode)
    {
        var projectSource = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            Guid.NewGuid().ToString("N"),
            fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(projectSource)!);
        File.WriteAllText(projectSource, string.Empty);
        var probe = new RecordingV15_1EnvironmentProbe(
            TiaOpennessV15_1EnvironmentProbeResult.Ready("unused-public-api", Array.Empty<TiaEvidence>()));
        var host = new RecordingV15_1EngineeringSessionHost(
            TiaOpennessV15_1EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "unexpected-host-call",
                    TiaDiagnosticSeverity.Error,
                    "The host must not be called.",
                    "OpenProject",
                    null),
                Array.Empty<TiaEvidence>()));
        var binding = new TiaOpennessV15_1Binding(probe, host);
        binding.Initialize(new SiemensBindingContext("siemens", TiaOpennessV15_1TestSupport.DefaultOperationalLogging));

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(projectSource, openMode, null, null),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.False(probe.WasCalled);
        Assert.False(host.WasCalled);
        Assert.Contains(
            open.Diagnostics,
            diagnostic => diagnostic.Code == "TiaOpenness.V15_1.ProjectSourceUnsupported");
    }

    [Fact]
    public async Task V15_1_binding_returns_project_session_when_engineering_session_open_succeeds()
    {
        var projectSource = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", Guid.NewGuid().ToString("N"), "sample.ap15_1");
        Directory.CreateDirectory(Path.GetDirectoryName(projectSource)!);
        File.WriteAllText(projectSource, string.Empty);
        var session = new RecordingV15_1EngineeringSessionHandle("tia-session-1", projectSource: projectSource);
        var probe = new RecordingV15_1EnvironmentProbe(
            TiaOpennessV15_1EnvironmentProbeResult.Ready("unused-public-api", Array.Empty<TiaEvidence>()));
        var host = new RecordingV15_1EngineeringSessionHost(TiaOpennessV15_1EngineeringSessionHostOpenResult.SucceededResult(
            session,
            new[]
            {
                new TiaEvidence(
                    "tia-openness-v15_1-test-host",
                    "EngineeringSessionHost",
                    "Test host opened.")
            }));
        var binding = new TiaOpennessV15_1Binding(probe, host);
        var bindingContext = new SiemensBindingContext("siemens", TiaOpennessV15_1TestSupport.DefaultOperationalLogging);
        binding.Initialize(bindingContext);

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectSource,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.True(open.Succeeded);
        Assert.NotNull(open.Session);
        Assert.Equal(TiaOperationStatus.Succeeded, open.Status);
        Assert.Empty(open.Diagnostics);
        Assert.Equal("tia-session-1", open.Session.Project.SessionId);
        Assert.Equal(projectSource, open.Session.Project.ProjectSource);
        Assert.Equal(TiaProjectHealthState.Healthy, open.Session.Health);
        Assert.Contains(open.Evidence, evidence => evidence.Id == "tia-openness-v15_1-engineering-session");
        Assert.True(probe.WasCalled);
        Assert.True(host.WasCalled);
        Assert.Equal(projectSource, host.ProjectSource);
        Assert.Equal(TiaProjectOpenMode.DirectProject, host.ProjectOpenMode);
        Assert.Null(host.ArchiveWorkDirectory);
        Assert.Same(bindingContext, host.BindingContext);
        Assert.Equal(0, session.CloseCount);

        await open.Session.CloseAsync(CancellationToken.None);

        Assert.Equal(1, session.CloseCount);
        Assert.Equal(TiaProjectHealthState.Disposed, open.Session.Health);
    }

    [Fact]
    public async Task V15_1_binding_keeps_close_failure_on_session_owner_path()
    {
        var projectSource = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", Guid.NewGuid().ToString("N"), "sample.ap15_1");
        Directory.CreateDirectory(Path.GetDirectoryName(projectSource)!);
        File.WriteAllText(projectSource, string.Empty);
        var session = new RecordingV15_1EngineeringSessionHandle(
            "tia-session-1",
            new InvalidOperationException("Close failed."));
        var probe = new RecordingV15_1EnvironmentProbe(
            TiaOpennessV15_1EnvironmentProbeResult.Ready("unused-public-api", Array.Empty<TiaEvidence>()));
        var host = new RecordingV15_1EngineeringSessionHost(TiaOpennessV15_1EngineeringSessionHostOpenResult.SucceededResult(
            session,
            Array.Empty<TiaEvidence>()));
        var binding = new TiaOpennessV15_1Binding(probe, host);
        binding.Initialize(new SiemensBindingContext("siemens", TiaOpennessV15_1TestSupport.DefaultOperationalLogging));

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectSource,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.True(open.Succeeded);
        Assert.NotNull(open.Session);

        await Assert.ThrowsAsync<InvalidOperationException>(() => open.Session.CloseAsync(CancellationToken.None));
        Assert.Equal(1, session.CloseCount);
    }

    [Fact]
    public async Task V15_1_binding_forwards_archive_open_mode_and_work_base_to_host()
    {
        var projectSource = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", Guid.NewGuid().ToString("N"), "sample.zap15_1");
        var archiveWorkDirectory = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "archive-work");
        Directory.CreateDirectory(Path.GetDirectoryName(projectSource)!);
        File.WriteAllText(projectSource, string.Empty);
        var session = new RecordingV15_1EngineeringSessionHandle("tia-session-1", projectSource: projectSource);
        var probe = new RecordingV15_1EnvironmentProbe(
            TiaOpennessV15_1EnvironmentProbeResult.Ready("unused-public-api", Array.Empty<TiaEvidence>()));
        var host = new RecordingV15_1EngineeringSessionHost(
            TiaOpennessV15_1EngineeringSessionHostOpenResult.SucceededResult(
                session,
                Array.Empty<TiaEvidence>()));
        var binding = new TiaOpennessV15_1Binding(probe, host);
        binding.Initialize(new SiemensBindingContext("siemens", TiaOpennessV15_1TestSupport.DefaultOperationalLogging));

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectSource,
                TiaProjectOpenMode.RetrievedArchive,
                archiveWorkDirectory,
                null),
            CancellationToken.None);

        Assert.True(open.Succeeded);
        Assert.Equal(TiaProjectOpenMode.RetrievedArchive, host.ProjectOpenMode);
        Assert.Equal(archiveWorkDirectory, host.ArchiveWorkDirectory);

        await open.Session!.CloseAsync(CancellationToken.None);
    }

    private sealed class RecordingV15_1EngineeringSessionHost : ITiaOpennessV15_1EngineeringSessionHost
    {
        private readonly TiaOpennessV15_1EngineeringSessionHostOpenResult result;

        public RecordingV15_1EngineeringSessionHost(TiaOpennessV15_1EngineeringSessionHostOpenResult result)
        {
            this.result = result;
        }

        public bool WasCalled { get; private set; }

        public string? PublicApiDirectory { get; private set; }

        public string? ProjectSource { get; private set; }

        public TiaProjectOpenMode? ProjectOpenMode { get; private set; }

        public string? ArchiveWorkDirectory { get; private set; }

        public SiemensBindingContext? BindingContext { get; private set; }

        public Task<TiaOpennessV15_1EngineeringSessionHostOpenResult> OpenAsync(
            string publicApiDirectory,
            TiaProjectOpenRequest request,
            SiemensBindingContext bindingContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;
            PublicApiDirectory = publicApiDirectory;
            ProjectSource = request.ProjectSource;
            ProjectOpenMode = request.OpenMode;
            ArchiveWorkDirectory = request.ArchiveWorkDirectory;
            BindingContext = bindingContext;
            return Task.FromResult(result);
        }
    }


}
