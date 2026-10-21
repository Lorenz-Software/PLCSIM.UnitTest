using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V21;

public sealed class TiaOpennessV21ProjectSessionTests
{
    [Fact]
    public async Task V21_binding_returns_project_session_when_engineering_session_open_succeeds()
    {
        var projectSource = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", Guid.NewGuid().ToString("N"), "sample.ap21");
        Directory.CreateDirectory(Path.GetDirectoryName(projectSource)!);
        File.WriteAllText(projectSource, string.Empty);
        var session = new RecordingV21EngineeringSessionHandle("tia-session-1", projectSource: projectSource);
        var probe = new RecordingV21EnvironmentProbe(
            TiaOpennessV21EnvironmentProbeResult.Ready("unused-public-api", Array.Empty<TiaEvidence>()));
        var host = new RecordingV21EngineeringSessionHost(TiaOpennessV21EngineeringSessionHostOpenResult.SucceededResult(
            session,
            new[]
            {
                new TiaEvidence(
                    "tia-openness-v21-test-host",
                    "EngineeringSessionHost",
                    "Test host opened.")
            }));
        var binding = new TiaOpennessV21Binding(probe, host);
        var bindingContext = new SiemensBindingContext("siemens", TiaOpennessV21TestSupport.DefaultOperationalLogging);
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
        Assert.Contains(open.Evidence, evidence => evidence.Id == "tia-openness-v21-engineering-session");
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
    public async Task V21_binding_keeps_close_failure_on_session_owner_path()
    {
        var projectSource = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", Guid.NewGuid().ToString("N"), "sample.ap21");
        Directory.CreateDirectory(Path.GetDirectoryName(projectSource)!);
        File.WriteAllText(projectSource, string.Empty);
        var session = new RecordingV21EngineeringSessionHandle(
            "tia-session-1",
            new InvalidOperationException("Close failed."));
        var probe = new RecordingV21EnvironmentProbe(
            TiaOpennessV21EnvironmentProbeResult.Ready("unused-public-api", Array.Empty<TiaEvidence>()));
        var host = new RecordingV21EngineeringSessionHost(TiaOpennessV21EngineeringSessionHostOpenResult.SucceededResult(
            session,
            Array.Empty<TiaEvidence>()));
        var binding = new TiaOpennessV21Binding(probe, host);
        binding.Initialize(new SiemensBindingContext("siemens", TiaOpennessV21TestSupport.DefaultOperationalLogging));

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
    public async Task V21_binding_forwards_archive_open_mode_and_work_base_to_host()
    {
        var projectSource = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", Guid.NewGuid().ToString("N"), "sample.zap21");
        var archiveWorkDirectory = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "archive-work");
        Directory.CreateDirectory(Path.GetDirectoryName(projectSource)!);
        File.WriteAllText(projectSource, string.Empty);
        var session = new RecordingV21EngineeringSessionHandle("tia-session-1", projectSource: projectSource);
        var probe = new RecordingV21EnvironmentProbe(
            TiaOpennessV21EnvironmentProbeResult.Ready("unused-public-api", Array.Empty<TiaEvidence>()));
        var host = new RecordingV21EngineeringSessionHost(
            TiaOpennessV21EngineeringSessionHostOpenResult.SucceededResult(
                session,
                Array.Empty<TiaEvidence>()));
        var binding = new TiaOpennessV21Binding(probe, host);
        binding.Initialize(new SiemensBindingContext("siemens", TiaOpennessV21TestSupport.DefaultOperationalLogging));

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

    private sealed class RecordingV21EngineeringSessionHost : ITiaOpennessV21EngineeringSessionHost
    {
        private readonly TiaOpennessV21EngineeringSessionHostOpenResult result;

        public RecordingV21EngineeringSessionHost(TiaOpennessV21EngineeringSessionHostOpenResult result)
        {
            this.result = result;
        }

        public bool WasCalled { get; private set; }

        public string? PublicApiDirectory { get; private set; }

        public string? ProjectSource { get; private set; }

        public TiaProjectOpenMode? ProjectOpenMode { get; private set; }

        public string? ArchiveWorkDirectory { get; private set; }

        public SiemensBindingContext? BindingContext { get; private set; }

        public Task<TiaOpennessV21EngineeringSessionHostOpenResult> OpenAsync(
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
