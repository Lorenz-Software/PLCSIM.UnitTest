using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.AccessProfiles;

public sealed class AccessProfileSessionServiceTests
{
    [Fact]
    public async Task LoadAsync_creates_current_session_from_discovered_profile()
    {
        var service = CreateService(new RecordingAccessProfilePlugin("one", "1.0.0"));

        var result = await service.LoadAsync(CreateRequest("one", "1.0.0"), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.Loaded, result.State);
        Assert.NotNull(result.Session);
        Assert.Same(result.Session, service.CurrentSession);
        Assert.Equal("one", result.Session.SelectedProfile.ProfileId);
        Assert.Equal("1.0.0", result.Session.SelectedProfile.Version);
        Assert.NotNull(result.Session.Configuration);
    }

    [Fact]
    public async Task LoadAsync_exposes_configuration_service_through_session_wrappers()
    {
        var configuration = new FakeProfileConfigurationService(
            FakeAccessProfilePlugin.CreateDefaultInfo());
        var service = CreateService(new RecordingAccessProfilePlugin("one", "1.0.0", configuration));

        var result = await service.LoadAsync(CreateRequest("one", "1.0.0"), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.Loaded, result.State);
        Assert.Same(configuration, result.Session?.Configuration);
        Assert.Same(configuration, result.Session?.Profile.Configuration);
    }

    [Fact]
    public async Task LoadAsync_without_requested_version_selects_latest_discovered_version()
    {
        var service = CreateService(
            new RecordingAccessProfilePlugin("one", "1.0.0"),
            new RecordingAccessProfilePlugin("one", "1.2.0"));

        var result = await service.LoadAsync(CreateRequest("one"), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.Loaded, result.State);
        Assert.Equal("1.2.0", result.Session?.SelectedProfile.Version);
    }

    [Fact]
    public async Task LoadAsync_propagates_and_retains_project_ownership_mode()
    {
        var plugin = new RecordingAccessProfilePlugin("one", "1.0.0");
        var service = CreateService(plugin);
        var config = CreateConfig("one", "1.0.0");
        var request = new AccessProfileSessionRequest(
            config.ProfileId,
            config.ProfileVersion,
            config.ProjectSource,
            ProjectOwnershipMode.ExternallyManaged);

        var result = await service.LoadAsync(request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Same(request, plugin.LastRequest);
        Assert.Same(request, result.Session?.Request);
        Assert.Equal(ProjectOwnershipMode.ExternallyManaged, result.Session?.ProjectOwnershipMode);
    }

    [Fact]
    public async Task LoadAsync_unloads_existing_session_before_replacing_it()
    {
        var firstPlugin = new RecordingAccessProfilePlugin("one", "1.0.0");
        var secondPlugin = new RecordingAccessProfilePlugin("two", "1.0.0");
        var service = CreateService(firstPlugin, secondPlugin);

        _ = await service.LoadAsync(CreateRequest("one", "1.0.0"), TimeSpan.FromSeconds(30), CancellationToken.None);
        var firstProfile = Assert.IsType<RecordingAccessProfile>(firstPlugin.LastSession);

        var second = await service.LoadAsync(CreateRequest("two", "1.0.0"), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(firstProfile.IsDisposed);
        Assert.Equal(AccessProfileLoadState.Loaded, second.State);
        Assert.Equal("two", service.CurrentSession?.SelectedProfile.ProfileId);
    }

    [Fact]
    public async Task LoadAsync_clears_existing_session_when_next_load_fails()
    {
        var plugin = new RecordingAccessProfilePlugin("one", "1.0.0");
        var service = CreateService(plugin);
        _ = await service.LoadAsync(CreateRequest("one", "1.0.0"), TimeSpan.FromSeconds(30), CancellationToken.None);
        var firstProfile = Assert.IsType<RecordingAccessProfile>(plugin.LastSession);

        var missing = await service.LoadAsync(CreateRequest("missing", "1.0.0"), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(firstProfile.IsDisposed);
        Assert.Equal(AccessProfileLoadState.NotFound, missing.State);
        Assert.Null(missing.Session);
        Assert.Null(service.CurrentSession);
    }

    [Fact]
    public async Task LoadAsync_returns_timed_out_when_profile_load_exceeds_deadline()
    {
        var timeProvider = FakeTime.Create();
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new RecordingAccessProfilePlugin("one", "1.0.0")
        {
            LoadAsyncHandler = async cancellationToken =>
            {
                loadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, cancellationToken);
                throw new InvalidOperationException("The held load should only complete through cancellation.");
            }
        };
        var service = CreateService(timeProvider, plugin);

        var loadTask = service.LoadAsync(
            CreateRequest("one", "1.0.0"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        await loadStarted.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var result = await loadTask;

        Assert.Equal(AccessProfileLoadState.TimedOut, result.State);
        Assert.Null(result.Session);
        Assert.Null(service.CurrentSession);
    }

    [Fact]
    public async Task UnloadAsync_reports_timeout_but_owned_disposal_continues()
    {
        var timeProvider = FakeTime.Create();
        var plugin = new RecordingAccessProfilePlugin("one", "1.0.0");
        var service = CreateService(timeProvider, plugin);
        _ = await service.LoadAsync(
            CreateRequest("one", "1.0.0"),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        var profile = Assert.IsType<RecordingAccessProfile>(plugin.LastSession);
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        profile.DisposeAsyncHandler = async () =>
        {
            disposalStarted.TrySetResult();
            await allowDisposal.Task;
        };

        var unloadTask = service.UnloadAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await disposalStarted.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<TimeoutException>(() => unloadTask);
        Assert.Null(service.CurrentSession);
        allowDisposal.TrySetResult();
        await service.DisposeAsync();
        Assert.True(profile.IsDisposed);
    }

    [Fact]
    public async Task UnloadAsync_disposes_current_session_and_clears_it()
    {
        var plugin = new RecordingAccessProfilePlugin("one", "1.0.0");
        var service = CreateService(plugin);
        _ = await service.LoadAsync(CreateRequest("one", "1.0.0"), TimeSpan.FromSeconds(30), CancellationToken.None);
        var profile = Assert.IsType<RecordingAccessProfile>(plugin.LastSession);

        await service.UnloadAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(profile.IsDisposed);
        Assert.Null(service.CurrentSession);
    }

    [Fact]
    public async Task Cancelled_unload_wait_detaches_session_and_blocks_replacement_until_disposal_finishes()
    {
        var plugin = new RecordingAccessProfilePlugin("one", "1.0.0");
        var service = CreateService(plugin);
        _ = await service.LoadAsync(CreateRequest("one", "1.0.0"), TimeSpan.FromSeconds(30), CancellationToken.None);
        var profile = Assert.IsType<RecordingAccessProfile>(plugin.LastSession);
        var disposalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDisposal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        profile.DisposeAsyncHandler = async () =>
        {
            disposalStarted.TrySetResult();
            await allowDisposal.Task;
        };
        using var cancellation = new CancellationTokenSource();

        var unloadTask = service.UnloadAsync(TimeSpan.FromSeconds(30), cancellation.Token);
        await disposalStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unloadTask);

        Assert.Null(service.CurrentSession);
        var replacementTask = service.LoadAsync(
            CreateRequest("one", "1.0.0"),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        Assert.False(replacementTask.IsCompleted);

        allowDisposal.TrySetResult();
        var replacement = await replacementTask;

        Assert.True(profile.IsDisposed);
        Assert.Equal(AccessProfileLoadState.Loaded, replacement.State);
        Assert.NotNull(service.CurrentSession);
    }

    [Fact]
    public async Task DisposeAsync_waits_for_active_unload_after_callers_wait_was_cancelled()
    {
        var plugin = new RecordingAccessProfilePlugin("one", "1.0.0");
        var service = CreateService(plugin);
        _ = await service.LoadAsync(CreateRequest("one", "1.0.0"), TimeSpan.FromSeconds(30), CancellationToken.None);
        var profile = Assert.IsType<RecordingAccessProfile>(plugin.LastSession);
        var disposalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDisposal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        profile.DisposeAsyncHandler = async () =>
        {
            disposalStarted.TrySetResult();
            await allowDisposal.Task;
        };
        using var cancellation = new CancellationTokenSource();

        var unloadTask = service.UnloadAsync(TimeSpan.FromSeconds(30), cancellation.Token);
        await disposalStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unloadTask);

        var disposeTask = service.DisposeAsync().AsTask();

        Assert.False(disposeTask.IsCompleted);
        allowDisposal.TrySetResult();
        await disposeTask;
        Assert.True(profile.IsDisposed);
    }

    [Fact]
    public async Task Session_remains_accessible_while_inner_profile_publishes_during_dispose()
    {
        var info = new AccessProfileInfo("one", "Profile one", "1.0.0", "Recording profile.");
        var profile = new RecordingAccessProfile(
            info,
            FakeAccessProfileFactory.CreateServices(FakeTime.Create()));
        var session = new AccessProfileSession(CreateRequest("one", "1.0.0"), info, profile);
        profile.Disposing = () => Assert.Same(profile, session.Profile);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(1, profile.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => _ = session.Profile);
    }

    private static AccessProfileSessionService CreateService(params IAccessProfilePlugin[] plugins)
    {
        return CreateService(FakeTime.Create(), plugins);
    }

    private static AccessProfileSessionService CreateService(
        TimeProvider timeProvider,
        params IAccessProfilePlugin[] plugins)
    {
        var coreContext = TestCoreServiceContext.Create(timeProvider: timeProvider);
        return new AccessProfileSessionService(
            new AccessProfilePluginLoader(coreContext, plugins),
            new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true),
            coreContext);
    }

    private static AccessProfileConfig CreateConfig(string profileId, string? profileVersion = null)
    {
        return new AccessProfileConfig(
            profileId,
            profileVersion,
            AccessProfileConfigurationPayload.Empty,
            "sample.ap");
    }

    private static AccessProfileSessionRequest CreateRequest(string profileId, string? profileVersion = null)
    {
        var config = CreateConfig(profileId, profileVersion);
        return new AccessProfileSessionRequest(
            config.ProfileId,
            config.ProfileVersion,
            config.ProjectSource,
            ProjectOwnershipMode.ApplicationManaged);
    }

    private sealed class RecordingAccessProfilePlugin : IAccessProfilePlugin
    {
        private readonly string profileId;
        private readonly string version;
        private readonly IAccessProfileConfigurationService? configuration;
        private AccessProfileContext? context;

        public RecordingAccessProfilePlugin(
            string profileId,
            string version,
            IAccessProfileConfigurationService? configuration = null)
        {
            this.profileId = profileId;
            this.version = version;
            this.configuration = configuration;
            Info = new AccessProfileInfo(profileId, $"Profile {profileId}", version, "Recording profile.");
        }

        public AccessProfileInfo Info { get; }

        public IAccessProfileSession? LastSession { get; private set; }

        public AccessProfileSessionRequest? LastRequest { get; private set; }

        public Func<CancellationToken, Task<PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult>>? LoadAsyncHandler { get; init; }

        public void Initialize(AccessProfileContext context)
        {
            this.context = context;
        }

        public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
        {
            return string.Equals(config.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)
                ? new AccessProfileValidationResult(true, [])
                : new AccessProfileValidationResult(
                    false,
                    [new Diagnostic(
                        "RecordingProfile.Config.Invalid",
                        DiagnosticCategory.Configuration,
                        Microsoft.Extensions.Logging.LogLevel.Error,
                        DiagnosticSource.AccessProfile,
                        DiagnosticPhase.Configuration,
                        "Invalid profile.",
                        new DiagnosticCorrelation(),
                        FakeTime.DefaultStart)]);
        }

        public Task<PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult> LoadAsync(
            AccessProfileSessionRequest request,
            CancellationToken cancellationToken)
        {
            var initializedContext = context ?? throw new InvalidOperationException("Plugin not initialized.");
            LastRequest = request;
            if (LoadAsyncHandler is not null)
            {
                return LoadAsyncHandler(cancellationToken);
            }
            LastSession = new RecordingAccessProfile(
                Info,
                FakeAccessProfileFactory.CreateServices(initializedContext.TimeProvider),
                configuration);
            return Task.FromResult(
                PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult.Success(LastSession));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAccessProfile : IAccessProfileSession
    {
        public RecordingAccessProfile(
            AccessProfileInfo info,
            FakeAccessProfileServices services,
            IAccessProfileConfigurationService? configuration = null)
        {
            Info = info;
            Configuration = configuration ?? new FakeProfileConfigurationService(info);
            Project = services.Project;
            PlcControl = services.PlcControl;
            UnitTestLifecycle = services.UnitTestLifecycle;
            UnitTestEventStream = services.UnitTestEventStream;
        }

        public AccessProfileInfo Info { get; }

        public bool IsDisposed { get; private set; }

        public int DisposeCount { get; private set; }

        public Action? Disposing { get; set; }

        public Func<ValueTask>? DisposeAsyncHandler { get; set; }

        public IAccessProfileConfigurationService Configuration { get; }

        public IPlcProjectService Project { get; }

        public IPlcControlService PlcControl { get; }

        public IUnitTestLifecycleService UnitTestLifecycle { get; }

        public IUnitTestEventStreamService UnitTestEventStream { get; }

        public AccessHealth Health { get; } = AccessHealth.Healthy(FakeTime.DefaultStart);

        public event EventHandler<DiagnosticEventArgs>? DiagnosticEventHandler;

        public event EventHandler<ProfileStateChangedEventArgs>? StateChangedEventHandler;

        public event EventHandler<AccessHealthChangedEventArgs>? HealthChangedEventHandler
        {
            add { }
            remove { }
        }

        public async Task<OperationResult<CommunicationSession>> PrepareUnitTestCommunicationAsync(
            CommunicationPrepareRequest request,
            CancellationToken cancellationToken)
        {
            return await Task.FromResult(OperationResult<CommunicationSession>.Failure("Not used."));
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            Disposing?.Invoke();
            if (DisposeAsyncHandler is not null)
            {
                await DisposeAsyncHandler();
            }

            IsDisposed = true;
        }

        public void PublishDiagnostic(Diagnostic diagnostic)
        {
            DiagnosticEventHandler?.Invoke(this, new DiagnosticEventArgs(diagnostic));
        }

        public void PublishState(ProfileStateChangedEventArgs args)
        {
            StateChangedEventHandler?.Invoke(this, args);
        }
    }

}
