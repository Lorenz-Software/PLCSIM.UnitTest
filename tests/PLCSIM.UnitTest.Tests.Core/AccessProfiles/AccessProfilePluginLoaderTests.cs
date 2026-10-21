using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Tests.Core.TestLogging;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.AccessProfiles;

public sealed class AccessProfilePluginLoaderTests
{
    [Fact]
    public void Discover_returns_registered_builtin_profiles()
    {
        var loader = CreateLoader([FakeAccessProfileFactory.CreatePlugin()]);

        var result = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));

        Assert.Empty(result.Diagnostics);
        Assert.Single(result.Candidates);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileId, result.Candidates[0].Info.ProfileId);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileVersion, result.Candidates[0].Info.Version);
        Assert.Equal(FakeAccessProfilePlugin.DefaultDisplayName, result.Candidates[0].Info.DisplayName);
        Assert.Equal(FakeAccessProfilePlugin.DefaultDescription, result.Candidates[0].Info.Description);
        Assert.Equal(AccessProfileSourceKind.BuiltIn, result.Candidates[0].SourceKind);
        Assert.Equal(
            $"built-in:{FakeAccessProfilePlugin.DefaultProfileId}@{FakeAccessProfilePlugin.DefaultProfileVersion}",
            result.Candidates[0].SourcePath);
        Assert.Null(result.Candidates[0].EntryPoint);
        Assert.Equal(AccessProfileCandidateState.Discovered, result.Candidates[0].State);
    }

    [Fact]
    public void Discover_returns_directory_profile_candidate_from_valid_manifest()
    {
        var loader = CreateLoader([]);
        var root = CreateTempDirectory();
        try
        {
            var packageDirectory = Path.Combine(root, "fake-profile");
            Directory.CreateDirectory(packageDirectory);
            File.WriteAllText(Path.Combine(packageDirectory, "Fake.Profile.dll"), string.Empty);
            WriteManifest(packageDirectory, """
                {
                  "schemaVersion": 1,
                  "profile": {
                    "id": "fake",
                    "version": "1.0.0",
                    "displayName": "Fake Access Profile",
                    "description": "Directory fake profile.",
                    "contractVersion": "2.0.0"
                  },
                  "entryPoint": {
                    "assembly": "Fake.Profile.dll",
                    "type": "Fake.Profile.Plugin"
                  }
                }
                """);

            var result = loader.Discover(new AccessProfileDiscoveryOptions(root, IncludeBuiltInProfiles: false));

            Assert.Empty(result.Diagnostics);
            Assert.Single(result.Candidates);
            var candidate = result.Candidates[0];
            Assert.Equal(AccessProfileSourceKind.DirectoryPackage, candidate.SourceKind);
            Assert.Equal(AccessProfileCandidateState.Discovered, candidate.State);
            Assert.Equal("fake", candidate.Info.ProfileId);
            Assert.Equal("1.0.0", candidate.Info.Version);
            Assert.Equal(Path.Combine(packageDirectory, "Fake.Profile.dll"), candidate.EntryPoint?.AssemblyPath);
            Assert.Equal("Fake.Profile.Plugin", candidate.EntryPoint?.TypeName);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Discover_reports_invalid_manifest_json_without_candidate()
    {
        var loader = CreateLoader([]);
        var root = CreateTempDirectory();
        try
        {
            var packageDirectory = Path.Combine(root, "broken-profile");
            Directory.CreateDirectory(packageDirectory);
            WriteManifest(packageDirectory, "{");

            var result = loader.Discover(new AccessProfileDiscoveryOptions(root, IncludeBuiltInProfiles: false));

            Assert.Empty(result.Candidates);
            Assert.Single(result.Diagnostics);
            Assert.Equal("Profile.Discovery.Manifest.InvalidJson", result.Diagnostics[0].Code);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Discover_reports_missing_discovery_directory_without_candidates()
    {
        var loader = CreateLoader([]);
        var missingDirectory = Path.Combine(Path.GetTempPath(), "plcsim-unittest-missing-profiles", Guid.NewGuid().ToString("N"));

        var result = loader.Discover(new AccessProfileDiscoveryOptions(missingDirectory, IncludeBuiltInProfiles: false));

        Assert.Empty(result.Candidates);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("Profile.Discovery.Directory.NotFound", diagnostic.Code);
        Assert.Contains(missingDirectory, diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("2.0.1")]
    [InlineData("9.0.0")]
    public void Discover_marks_incompatible_contract_candidate(string contractVersion)
    {
        var loader = CreateLoader([]);
        var root = CreateTempDirectory();
        try
        {
            var packageDirectory = Path.Combine(root, "future-profile");
            Directory.CreateDirectory(packageDirectory);
            File.WriteAllText(Path.Combine(packageDirectory, "Future.Profile.dll"), string.Empty);
            WriteManifest(packageDirectory, $$"""
                {
                  "schemaVersion": 1,
                  "profile": {
                    "id": "future",
                    "version": "2.0.0",
                    "displayName": "Future Access Profile",
                    "contractVersion": "{{contractVersion}}"
                  },
                  "entryPoint": {
                    "assembly": "Future.Profile.dll",
                    "type": "Future.Profile.Plugin"
                  }
                }
                """);

            var result = loader.Discover(new AccessProfileDiscoveryOptions(root, IncludeBuiltInProfiles: false));

            Assert.Single(result.Candidates);
            Assert.Single(result.Diagnostics);
            Assert.Equal(AccessProfileCandidateState.ContractIncompatible, result.Candidates[0].State);
            Assert.Equal("Profile.Discovery.Manifest.ContractIncompatible", result.Diagnostics[0].Code);
            Assert.Contains("This application requires '2.0.0'", result.Diagnostics[0].Message);
            Assert.Contains("Install a compatible profile package", result.Diagnostics[0].Message);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Discover_marks_entry_assembly_outside_package_as_metadata_invalid()
    {
        var loader = CreateLoader([]);
        var root = CreateTempDirectory();
        try
        {
            var packageDirectory = Path.Combine(root, "escaping-profile");
            Directory.CreateDirectory(packageDirectory);
            WriteManifest(
                packageDirectory,
                CreateManifestJson(
                    "escaping",
                    "1.0.0",
                    "../Outside.Profile.dll",
                    "Escaping.Profile.Plugin"));

            var result = loader.Discover(new AccessProfileDiscoveryOptions(root, IncludeBuiltInProfiles: false));

            var candidate = Assert.Single(result.Candidates);
            Assert.Equal(AccessProfileCandidateState.MetadataInvalid, candidate.State);
            Assert.Null(candidate.EntryPoint);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("Profile.Discovery.Manifest.EntryAssemblyPathInvalid", diagnostic.Code);
            Assert.Contains("outside the package directory", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Discover_marks_missing_entry_assembly_candidate_as_load_failed()
    {
        var loader = CreateLoader([]);
        var root = CreateTempDirectory();
        try
        {
            var packageDirectory = Path.Combine(root, "missing-assembly-profile");
            Directory.CreateDirectory(packageDirectory);
            WriteManifest(packageDirectory, """
                {
                  "schemaVersion": 1,
                  "profile": {
                    "id": "missingassembly",
                    "version": "1.0.0",
                    "displayName": "Missing Assembly Profile",
                    "contractVersion": "2.0.0"
                  },
                  "entryPoint": {
                    "assembly": "Missing.Profile.dll",
                    "type": "Missing.Profile.Plugin"
                  }
                }
                """);

            var result = loader.Discover(new AccessProfileDiscoveryOptions(root, IncludeBuiltInProfiles: false));

            Assert.Single(result.Candidates);
            Assert.Single(result.Diagnostics);
            Assert.Equal(AccessProfileCandidateState.LoadFailed, result.Candidates[0].State);
            Assert.Equal("Profile.Discovery.Manifest.EntryAssemblyMissing", result.Diagnostics[0].Code);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Discover_orders_candidates_by_source_priority_profile_id_version_and_source_path()
    {
        var timeProvider = FakeTime.Create();
        var root = CreateTempDirectory();
        try
        {
            var alphaPackage = Path.Combine(root, "alpha-directory-profile");
            Directory.CreateDirectory(alphaPackage);
            File.WriteAllText(Path.Combine(alphaPackage, "Alpha.Profile.dll"), string.Empty);
            WriteManifest(alphaPackage, CreateManifestJson("alpha", "1.0.0", "Alpha.Profile.dll", "Alpha.Profile.Plugin"));

            var fakeDirectoryPackage = Path.Combine(root, "fake-directory-profile");
            Directory.CreateDirectory(fakeDirectoryPackage);
            File.WriteAllText(Path.Combine(fakeDirectoryPackage, "Fake.Profile.dll"), string.Empty);
            WriteManifest(fakeDirectoryPackage, CreateManifestJson("fake", "1.1.0", "Fake.Profile.dll", "Fake.Profile.Plugin"));

            var loader = CreateLoader([FakeAccessProfileFactory.CreatePlugin(timeProvider)], timeProvider);

            var result = loader.Discover(new AccessProfileDiscoveryOptions(root, IncludeBuiltInProfiles: true));

            Assert.Empty(result.Diagnostics);
            Assert.Equal(
                [
                    "BuiltIn:fake:1.0.0",
                    "DirectoryPackage:alpha:1.0.0",
                    "DirectoryPackage:fake:1.1.0"
                ],
                result.Candidates.Select(candidate => $"{candidate.SourceKind}:{candidate.Info.ProfileId}:{candidate.Info.Version}"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Load_creates_selected_profile()
    {
        var timeProvider = FakeTime.Create();
        var loader = CreateLoader([FakeAccessProfileFactory.CreatePlugin(timeProvider)], timeProvider);
        var discovery = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));

        var result = await loader.LoadAsync(
            CreateRequest(FakeAccessProfileFactory.CreateDefaultConfig()),
            discovery,
            CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.Loaded, result.State);
        Assert.NotNull(result.Profile);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileId, result.SelectedProfile?.ProfileId);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileVersion, result.SelectedProfile?.Version);
        Assert.Equal(FakeAccessProfilePlugin.DefaultDisplayName, result.SelectedProfile?.DisplayName);
        Assert.NotNull(result.Profile.Project);
        Assert.NotNull(result.Profile.PlcControl);
        Assert.NotNull(result.Profile.UnitTestLifecycle);
        Assert.NotNull(result.Profile.UnitTestEventStream);
    }

    [Fact]
    public async Task Load_creates_directory_package_profile_from_entry_point()
    {
        var root = CreateTempDirectory();
        try
        {
            await VerifyDirectoryPackageLoadAsync(root);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Load_creates_fake_profile_from_directory_package()
    {
        var root = CreateTempDirectory();
        try
        {
            await VerifyFakeDirectoryPackageLoadAsync(root);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Load_hands_only_session_setup_to_selected_profile()
    {
        var timeProvider = FakeTime.Create();
        var plugin = new RecordingAccessProfilePlugin(timeProvider);
        var loader = CreateLoader([plugin], timeProvider);
        var discovery = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));
        var config = new AccessProfileConfig(
            "recording",
            "1.0.0",
            new AccessProfileConfigurationPayload("{\"plcRef\":{\"id\":\"PLC_1\"},\"runtimeRef\":{\"id\":\"Runtime_1\"}}"),
            @"D:\Projects\SampleProject.ap");

        var result = await loader.LoadAsync(
            CreateRequest(config),
            discovery,
            CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.Loaded, result.State);
        Assert.NotNull(result.Profile);
        Assert.Null(plugin.ValidatedConfig);
        Assert.Equal(config.ProfileId, plugin.CreatedRequest?.ProfileId);
        Assert.Equal(config.ProfileVersion, plugin.CreatedRequest?.ProfileVersion);
        Assert.Equal(config.ProjectSource, plugin.CreatedRequest?.ProjectSource);
        Assert.Equal(ProjectOwnershipMode.ApplicationManaged, plugin.CreatedRequest?.ProjectOwnershipMode);
    }

    [Fact]
    public async Task Load_reports_assembly_load_failure_for_invalid_entry_assembly()
    {
        var root = CreateTempDirectory();
        try
        {
            var packageDirectory = Path.Combine(root, "invalid-assembly-profile");
            Directory.CreateDirectory(packageDirectory);
            File.WriteAllText(Path.Combine(packageDirectory, "Invalid.Profile.dll"), string.Empty);
            WriteManifest(
                packageDirectory,
                CreateManifestJson(
                    "invalidassembly",
                    "1.0.0",
                    "Invalid.Profile.dll",
                    "Invalid.Profile.Plugin"));
            var loader = CreateLoader([]);
            var discovery = loader.Discover(new AccessProfileDiscoveryOptions(root, IncludeBuiltInProfiles: false));

            var result = await loader.LoadAsync(
                CreateRequest(CreateConfig("invalidassembly", "1.0.0")),
                discovery,
                CancellationToken.None);

            Assert.Equal(AccessProfileLoadState.Failed, result.State);
            Assert.Single(result.Diagnostics);
            Assert.Equal("Profile.Load.AssemblyLoadFailed", result.Diagnostics[0].Code);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Load_reports_missing_entry_type()
    {
        var root = CreateTempDirectory();
        try
        {
            CreatePackageWithCopiedTestAssembly(
                root,
                "missing-type-profile",
                "MissingType.Profile.dll",
                entryType: "Missing.Type.Plugin",
                profileId: "missingtype",
                profileVersion: "1.0.0");
            var loader = CreateLoader([]);
            var discovery = loader.Discover(new AccessProfileDiscoveryOptions(root, IncludeBuiltInProfiles: false));

            var result = await loader.LoadAsync(
                CreateRequest(CreateConfig("missingtype", "1.0.0")),
                discovery,
                CancellationToken.None);

            Assert.Equal(AccessProfileLoadState.Failed, result.State);
            Assert.Single(result.Diagnostics);
            Assert.Equal("Profile.Load.EntryTypeMissing", result.Diagnostics[0].Code);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Load_reports_entry_type_that_does_not_implement_profile_plugin()
    {
        var root = CreateTempDirectory();
        try
        {
            CreatePackageWithCopiedTestAssembly(
                root,
                "invalid-type-profile",
                "InvalidType.Profile.dll",
                typeof(AccessProfilePluginLoaderTests),
                "invalidtype",
                "1.0.0");
            var loader = CreateLoader([]);
            var discovery = loader.Discover(new AccessProfileDiscoveryOptions(root, IncludeBuiltInProfiles: false));

            var result = await loader.LoadAsync(
                CreateRequest(CreateConfig("invalidtype", "1.0.0")),
                discovery,
                CancellationToken.None);

            Assert.Equal(AccessProfileLoadState.Failed, result.State);
            Assert.Single(result.Diagnostics);
            Assert.Equal("Profile.Load.EntryTypeInvalid", result.Diagnostics[0].Code);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Load_reports_entry_profile_identity_mismatch()
    {
        var root = CreateTempDirectory();
        try
        {
            CreatePackageWithCopiedTestAssembly(
                root,
                "mismatched-profile",
                "Mismatch.Profile.dll",
                typeof(MismatchingDirectoryLoadTestAccessProfilePlugin),
                "directorytest",
                "1.0.0");
            var loader = CreateLoader([]);
            var discovery = loader.Discover(new AccessProfileDiscoveryOptions(root, IncludeBuiltInProfiles: false));

            var result = await loader.LoadAsync(
                CreateRequest(CreateConfig("directorytest", "1.0.0")),
                discovery,
                CancellationToken.None);

            Assert.Equal(AccessProfileLoadState.Failed, result.State);
            Assert.Single(result.Diagnostics);
            Assert.Equal("Profile.Load.EntryProfileMismatch", result.Diagnostics[0].Code);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Load_without_requested_version_selects_highest_discovered_version()
    {
        var timeProvider = FakeTime.Create();
        var loader = CreateLoader([
            CreateFakePlugin("1.0.0", timeProvider),
            CreateFakePlugin("1.2.0", timeProvider)], timeProvider);
        var discovery = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));

        var result = await loader.LoadAsync(
            CreateRequest(CreateConfig(FakeAccessProfilePlugin.DefaultProfileId)),
            discovery,
            CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.Loaded, result.State);
        Assert.Equal("1.2.0", result.SelectedProfile?.Version);
    }

    [Fact]
    public async Task Load_fails_for_ambiguous_same_id_and_version_candidates()
    {
        var timeProvider = FakeTime.Create();
        var root = CreateTempDirectory();
        try
        {
            var packageDirectory = Path.Combine(root, "fake-profile");
            Directory.CreateDirectory(packageDirectory);
            File.WriteAllText(Path.Combine(packageDirectory, "Fake.Profile.dll"), string.Empty);
            WriteManifest(packageDirectory, """
                {
                  "schemaVersion": 1,
                  "profile": {
                    "id": "fake",
                    "version": "1.0.0",
                    "displayName": "Directory Fake Access Profile",
                    "contractVersion": "2.0.0"
                  },
                  "entryPoint": {
                    "assembly": "Fake.Profile.dll",
                    "type": "Fake.Profile.Plugin"
                  }
                }
                """);
            var loader = CreateLoader([FakeAccessProfileFactory.CreatePlugin(timeProvider)], timeProvider);
            var discovery = loader.Discover(new AccessProfileDiscoveryOptions(root, IncludeBuiltInProfiles: true));

            var result = await loader.LoadAsync(
                CreateRequest(FakeAccessProfileFactory.CreateDefaultConfig()),
                discovery,
                CancellationToken.None);

            Assert.Equal(AccessProfileLoadState.Failed, result.State);
            Assert.Single(result.Diagnostics);
            Assert.Equal("Profile.Load.AmbiguousProfileVersion", result.Diagnostics[0].Code);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Load_returns_not_found_for_missing_requested_version()
    {
        var timeProvider = FakeTime.Create();
        var loader = CreateLoader([FakeAccessProfileFactory.CreatePlugin(timeProvider)], timeProvider);
        var discovery = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));

        var result = await loader.LoadAsync(
            CreateRequest(CreateConfig(FakeAccessProfilePlugin.DefaultProfileId, "9.9.9")),
            discovery,
            CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.NotFound, result.State);
        Assert.Single(result.Diagnostics);
        Assert.Equal("Profile.Load.VersionNotFound", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Load_returns_not_found_for_unknown_profile()
    {
        var loader = CreateLoader([FakeAccessProfileFactory.CreatePlugin()]);
        var discovery = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));

        var result = await loader.LoadAsync(
            CreateRequest(CreateConfig("missing")),
            discovery,
            CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.NotFound, result.State);
        Assert.Null(result.Profile);
        Assert.Single(result.Diagnostics);
        Assert.Equal("Profile.Load.NotFound", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Load_does_not_validate_operational_configuration()
    {
        var loader = CreateLoader([new RejectingAccessProfilePlugin()]);
        var discovery = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));

        var result = await loader.LoadAsync(
            CreateRequest(CreateConfig("rejecting", "1.0.0")),
            discovery,
            CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.Loaded, result.State);
        Assert.NotNull(result.Profile);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Load_does_not_call_throwing_operational_validation()
    {
        var loader = CreateLoader([new ThrowingValidationAccessProfilePlugin()]);
        var discovery = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));

        var result = await loader.LoadAsync(
            CreateRequest(CreateConfig("throwing-validation", "1.0.0")),
            discovery,
            CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.Loaded, result.State);
        Assert.NotNull(result.Profile);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Load_returns_failed_when_plugin_load_returns_failure()
    {
        var loader = CreateLoader([new FailingLoadAccessProfilePlugin()]);
        var discovery = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));

        var result = await loader.LoadAsync(
            CreateRequest(CreateConfig("failing-load", "1.0.0")),
            discovery,
            CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.Failed, result.State);
        Assert.Null(result.Profile);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "FailingLoad.Profile.LoadFailed");
    }

    [Fact]
    public async Task Load_returns_failed_when_plugin_load_throws()
    {
        var loader = CreateLoader([new ThrowingLoadAccessProfilePlugin()]);
        var discovery = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));

        var result = await loader.LoadAsync(
            CreateRequest(CreateConfig("throwing-load", "1.0.0")),
            discovery,
            CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.Failed, result.State);
        Assert.Null(result.Profile);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Profile.Load.SessionLoadFailed");
    }

    [Fact]
    public async Task Load_propagates_requested_cancellation_from_profile()
    {
        var plugin = new CancelableLoadAccessProfilePlugin();
        var loader = CreateLoader([plugin]);
        var discovery = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));
        using var cancellation = new CancellationTokenSource();

        var load = loader.LoadAsync(
            CreateRequest(CreateConfig(plugin.Info.ProfileId, plugin.Info.Version)),
            discovery,
            cancellation.Token);
        await plugin.LoadEntered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
    }

    [Fact]
    public async Task Load_returns_failed_when_plugin_reports_success_without_session()
    {
        var loader = CreateLoader([new InvalidSuccessAccessProfilePlugin()]);
        var discovery = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));

        var result = await loader.LoadAsync(
            CreateRequest(CreateConfig("invalid-success", "1.0.0")),
            discovery,
            CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.Failed, result.State);
        Assert.Null(result.Profile);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Profile.Load.InvalidSessionResult");
    }

    [Fact]
    public async Task Load_disposes_rejected_session_when_required_service_is_missing()
    {
        var plugin = new MissingServiceAccessProfilePlugin();
        var loader = CreateLoader([plugin]);
        var discovery = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));

        var result = await loader.LoadAsync(
            CreateRequest(CreateConfig("missing-service", "1.0.0")),
            discovery,
            CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.Failed, result.State);
        Assert.Null(result.Profile);
        Assert.True(plugin.Session?.IsDisposed);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Profile.Load.ServiceMissing.Configuration");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Profile.Load.ServiceMissing.Project");
    }

    [Fact]
    public async Task Load_writes_technical_logs_for_failed_profile_selection()
    {
        var loggerFactory = new ListLoggerFactory<AccessProfilePluginLoader>();
        var loader = CreateLoader([FakeAccessProfileFactory.CreatePlugin()], loggerFactory: loggerFactory);
        var discovery = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));

        var result = await loader.LoadAsync(
            CreateRequest(CreateConfig("missing")),
            discovery,
            CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.NotFound, result.State);
        Assert.Contains(loggerFactory.Logger.Entries, entry => entry.Message.Contains("Loading access profile", StringComparison.Ordinal));
        Assert.Contains(loggerFactory.Logger.Entries, entry => entry.Message.Contains("no matching profile", StringComparison.Ordinal));
        Assert.Contains(loggerFactory.Logger.Entries, entry => entry.Message.Contains("Profile.Load.NotFound", StringComparison.Ordinal));
    }

    private sealed class RejectingAccessProfilePlugin : IAccessProfilePlugin
    {
        public AccessProfileInfo Info { get; } = new("rejecting", "Rejecting", "1.0.0", "Rejecting profile.");

        public void Initialize(AccessProfileContext context)
        {
        }

        public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
        {
            return new AccessProfileValidationResult(
                false,
                [new Diagnostic(
                    "RejectingProfile.Config.Invalid",
                    DiagnosticCategory.Configuration,
                    Microsoft.Extensions.Logging.LogLevel.Error,
                    DiagnosticSource.AccessProfile,
                    DiagnosticPhase.Configuration,
                    "Rejected.",
                    new DiagnosticCorrelation(),
                    FakeTime.DefaultStart)]);
        }

        public Task<PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult> LoadAsync(
            AccessProfileSessionRequest request,
            CancellationToken cancellationToken)
        {
            IAccessProfileSession profile = new FakeAccessProfile(
                Info,
                FakeAccessProfileFactory.CreateServices(FakeTime.Create()),
                FakeTime.Create());
            return Task.FromResult(PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult.Success(profile));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAccessProfilePlugin : IAccessProfilePlugin
    {
        private readonly TimeProvider timeProvider;
        private AccessProfileContext? context;

        public RecordingAccessProfilePlugin(TimeProvider timeProvider)
        {
            this.timeProvider = timeProvider;
        }

        public AccessProfileInfo Info { get; } = new("recording", "Recording", "1.0.0", "Recording profile.");

        public AccessProfileConfig? ValidatedConfig { get; private set; }

        public AccessProfileSessionRequest? CreatedRequest { get; private set; }

        public void Initialize(AccessProfileContext context)
        {
            this.context = context;
        }

        public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
        {
            ValidatedConfig = config;
            return new AccessProfileValidationResult(true, []);
        }

        public Task<PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult> LoadAsync(
            AccessProfileSessionRequest request,
            CancellationToken cancellationToken)
        {
            CreatedRequest = request;
            var initializedContext = context ?? throw new InvalidOperationException("Plugin not initialized.");
            IAccessProfileSession profile = new FakeAccessProfile(
                Info,
                FakeAccessProfileFactory.CreateServices(timeProvider),
                initializedContext.TimeProvider);
            return Task.FromResult(PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult.Success(profile));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingValidationAccessProfilePlugin : IAccessProfilePlugin
    {
        public AccessProfileInfo Info { get; } = new("throwing-validation", "Throwing Validation", "1.0.0", "Throws during validation.");

        public void Initialize(AccessProfileContext context)
        {
        }

        public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
        {
            throw new InvalidOperationException("Validation failed unexpectedly.");
        }

        public Task<PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult> LoadAsync(
            AccessProfileSessionRequest request,
            CancellationToken cancellationToken)
        {
            IAccessProfileSession profile = new FakeAccessProfile(
                Info,
                FakeAccessProfileFactory.CreateServices(FakeTime.Create()),
                FakeTime.Create());
            return Task.FromResult(PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult.Success(profile));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingLoadAccessProfilePlugin : IAccessProfilePlugin
    {
        public AccessProfileInfo Info { get; } = new("failing-load", "Failing Load", "1.0.0", "Returns load failure.");

        public void Initialize(AccessProfileContext context)
        {
        }

        public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
        {
            return new AccessProfileValidationResult(true, []);
        }

        public Task<PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult> LoadAsync(
            AccessProfileSessionRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult.Failure(
                "Profile load failed.",
                [new Diagnostic(
                    "FailingLoad.Profile.LoadFailed",
                    DiagnosticCategory.Environment,
                    Microsoft.Extensions.Logging.LogLevel.Error,
                    DiagnosticSource.AccessProfile,
                    DiagnosticPhase.ProfileValidation,
                    "Profile load failed.",
                    new DiagnosticCorrelation(),
                    FakeTime.DefaultStart)]));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingLoadAccessProfilePlugin : IAccessProfilePlugin
    {
        public AccessProfileInfo Info { get; } = new("throwing-load", "Throwing Load", "1.0.0", "Throws during load.");

        public void Initialize(AccessProfileContext context)
        {
        }

        public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
        {
            return new AccessProfileValidationResult(true, []);
        }

        public Task<PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult> LoadAsync(
            AccessProfileSessionRequest request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Load failed unexpectedly.");
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancelableLoadAccessProfilePlugin : IAccessProfilePlugin
    {
        public AccessProfileInfo Info { get; } = new(
            "cancelable-load",
            "Cancelable Load",
            "1.0.0",
            "Waits until profile loading is cancelled.");

        public TaskCompletionSource LoadEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Initialize(AccessProfileContext context)
        {
        }

        public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
        {
            return new AccessProfileValidationResult(true, []);
        }

        public async Task<PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult> LoadAsync(
            AccessProfileSessionRequest request,
            CancellationToken cancellationToken)
        {
            LoadEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult.Failure(
                "Cancellation was not observed.");
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InvalidSuccessAccessProfilePlugin : IAccessProfilePlugin
    {
        public AccessProfileInfo Info { get; } = new("invalid-success", "Invalid Success", "1.0.0", "Returns invalid successful load result.");

        public void Initialize(AccessProfileContext context)
        {
        }

        public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
        {
            return new AccessProfileValidationResult(true, []);
        }

        public Task<PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult> LoadAsync(
            AccessProfileSessionRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult(
                true,
                null,
                "Invalid successful result.",
                Array.Empty<Diagnostic>()));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MissingServiceAccessProfilePlugin : IAccessProfilePlugin
    {
        private AccessProfileContext? context;

        public AccessProfileInfo Info { get; } = new("missing-service", "Missing Service", "1.0.0", "Returns a session without all required services.");

        public MissingServiceAccessProfileSession? Session { get; private set; }

        public void Initialize(AccessProfileContext context)
        {
            this.context = context;
        }

        public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
        {
            return new AccessProfileValidationResult(true, []);
        }

        public Task<PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult> LoadAsync(
            AccessProfileSessionRequest request,
            CancellationToken cancellationToken)
        {
            var initializedContext = context ?? throw new InvalidOperationException("Plugin not initialized.");
            Session = new MissingServiceAccessProfileSession(
                Info,
                FakeAccessProfileFactory.CreateServices(initializedContext.TimeProvider));
            return Task.FromResult(PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult.Success(Session));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MissingServiceAccessProfileSession : IAccessProfileSession
    {
        private readonly FakeAccessProfileServices services;

        public MissingServiceAccessProfileSession(
            AccessProfileInfo info,
            FakeAccessProfileServices services)
        {
            Info = info;
            this.services = services;
        }

        public AccessProfileInfo Info { get; }

        public bool IsDisposed { get; private set; }

        public event EventHandler<DiagnosticEventArgs>? DiagnosticEventHandler
        {
            add { }
            remove { }
        }

        public event EventHandler<ProfileStateChangedEventArgs>? StateChangedEventHandler
        {
            add { }
            remove { }
        }

        public event EventHandler<AccessHealthChangedEventArgs>? HealthChangedEventHandler
        {
            add { }
            remove { }
        }

        public AccessHealth Health { get; } = AccessHealth.Healthy(FakeTime.DefaultStart);

        public IAccessProfileConfigurationService Configuration => null!;

        public IPlcProjectService Project => null!;

        public IPlcControlService PlcControl => services.PlcControl;

        public IUnitTestLifecycleService UnitTestLifecycle => services.UnitTestLifecycle;

        public IUnitTestEventStreamService UnitTestEventStream => services.UnitTestEventStream;

        public Task<OperationResult<CommunicationSession>> PrepareUnitTestCommunicationAsync(
            CommunicationPrepareRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(OperationResult<CommunicationSession>.Failure("Not used."));
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static AccessProfileConfig CreateConfig(string profileId, string? profileVersion = null)
    {
        return new AccessProfileConfig(
            profileId,
            profileVersion,
            AccessProfileConfigurationPayload.Empty,
            "sample.ap");
    }

    private static FakeAccessProfilePlugin CreateFakePlugin(string version, TimeProvider timeProvider)
    {
        return new FakeAccessProfilePlugin(
            new AccessProfileInfo(
                FakeAccessProfilePlugin.DefaultProfileId,
                FakeAccessProfilePlugin.DefaultDisplayName,
                version,
                FakeAccessProfilePlugin.DefaultDescription),
            (_, _) => FakeAccessProfileFactory.CreateServices(timeProvider));
    }

    private static AccessProfilePluginLoader CreateLoader(
        IEnumerable<IAccessProfilePlugin> plugins,
        TimeProvider? timeProvider = null,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
    {
        return new AccessProfilePluginLoader(
            TestCoreServiceContext.Create(
                timeProvider: timeProvider,
                loggerFactory: loggerFactory),
            plugins);
    }

    private static AccessProfileSessionRequest CreateRequest(
        AccessProfileConfig config,
        ProjectOwnershipMode projectOwnershipMode = ProjectOwnershipMode.ApplicationManaged)
    {
        return new AccessProfileSessionRequest(
            config.ProfileId,
            config.ProfileVersion,
            config.ProjectSource,
            projectOwnershipMode);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task VerifyDirectoryPackageLoadAsync(string root)
    {
        var packageDirectory = CreatePackageWithCopiedTestAssembly(
            root,
            "directory-profile",
            "Directory.Profile.dll",
            typeof(DirectoryLoadTestAccessProfilePlugin),
            "directorytest",
            "1.0.0");
        var loader = CreateLoader([]);
        var discovery = loader.Discover(
            new AccessProfileDiscoveryOptions(root, IncludeBuiltInProfiles: false));

        var result = await loader.LoadAsync(
            CreateRequest(CreateConfig("directorytest", "1.0.0")),
            discovery,
            CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.Loaded, result.State);
        Assert.NotNull(result.Profile);
        Assert.Equal("directorytest", result.SelectedProfile?.ProfileId);
        Assert.Equal("1.0.0", result.SelectedProfile?.Version);
        Assert.True(File.Exists(Path.Combine(packageDirectory, "Directory.Profile.dll")));

        await result.Profile!.DisposeAsync();

        Assert.True(File.Exists(Path.Combine(packageDirectory, "plugin-disposed.marker")));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task VerifyFakeDirectoryPackageLoadAsync(string root)
    {
        CreatePackageWithCopiedFakeAssembly(root);
        var loader = CreateLoader([]);
        var discovery = loader.Discover(
            new AccessProfileDiscoveryOptions(root, IncludeBuiltInProfiles: false));

        var result = await loader.LoadAsync(
            CreateRequest(FakeAccessProfileFactory.CreateDefaultConfig()),
            discovery,
            CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.Loaded, result.State);
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Profile);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileId, result.SelectedProfile?.ProfileId);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileVersion, result.SelectedProfile?.Version);
        Assert.NotNull(result.Profile.Project);
        Assert.NotNull(result.Profile.PlcControl);
        Assert.NotNull(result.Profile.UnitTestLifecycle);
        Assert.NotNull(result.Profile.UnitTestEventStream);

        await result.Profile.DisposeAsync();
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "plcsim-unittest-profile-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }
    }

    private static void WriteManifest(string packageDirectory, string json)
    {
        File.WriteAllText(Path.Combine(packageDirectory, "manifest.json"), json);
    }

    private static string CreatePackageWithCopiedTestAssembly(
        string root,
        string packageName,
        string assemblyFileName,
        Type pluginType,
        string profileId,
        string profileVersion)
    {
        return CreatePackageWithCopiedTestAssembly(
            root,
            packageName,
            assemblyFileName,
            pluginType.FullName!,
            profileId,
            profileVersion);
    }

    private static string CreatePackageWithCopiedTestAssembly(
        string root,
        string packageName,
        string assemblyFileName,
        string entryType,
        string profileId,
        string profileVersion)
    {
        var packageDirectory = Path.Combine(root, packageName);
        Directory.CreateDirectory(packageDirectory);
        File.Copy(typeof(AccessProfilePluginLoaderTests).Assembly.Location, Path.Combine(packageDirectory, assemblyFileName));
        var fakeAssemblyPath = typeof(FakeAccessProfilePlugin).Assembly.Location;
        File.Copy(fakeAssemblyPath, Path.Combine(packageDirectory, Path.GetFileName(fakeAssemblyPath)));
        WriteManifest(
            packageDirectory,
            CreateManifestJson(profileId, profileVersion, assemblyFileName, entryType));
        return packageDirectory;
    }

    private static string CreatePackageWithCopiedFakeAssembly(string root)
    {
        var packageDirectory = Path.Combine(root, "fake");
        Directory.CreateDirectory(packageDirectory);
        var assemblyFileName = Path.GetFileName(typeof(FakeAccessProfilePlugin).Assembly.Location);
        File.Copy(typeof(FakeAccessProfilePlugin).Assembly.Location, Path.Combine(packageDirectory, assemblyFileName));
        WriteManifest(
            packageDirectory,
            CreateManifestJson(
                FakeAccessProfilePlugin.DefaultProfileId,
                FakeAccessProfilePlugin.DefaultProfileVersion,
                assemblyFileName,
                typeof(FakeAccessProfilePlugin).FullName!));
        return packageDirectory;
    }

    private static string CreateManifestJson(
        string profileId,
        string profileVersion,
        string assemblyFileName,
        string entryType)
    {
        return $$"""
            {
              "schemaVersion": 1,
              "profile": {
                "id": "{{profileId}}",
                "version": "{{profileVersion}}",
                "displayName": "Directory Access Profile",
                "contractVersion": "2.0.0"
              },
              "entryPoint": {
                "assembly": "{{assemblyFileName}}",
                "type": "{{entryType}}"
              }
            }
            """;
    }
}

public sealed class DirectoryLoadTestAccessProfilePlugin : IAccessProfilePlugin
{
    private AccessProfileContext? context;

    public AccessProfileInfo Info { get; } = new("directorytest", "Directory Test Profile", "1.0.0", "Test directory profile.");

    public void Initialize(AccessProfileContext context)
    {
        this.context = context;
    }

    public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
    {
        return new AccessProfileValidationResult(true, []);
    }

    public Task<PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult> LoadAsync(
        AccessProfileSessionRequest request,
        CancellationToken cancellationToken)
    {
        var initializedContext = context ?? throw new InvalidOperationException("Plugin not initialized.");
        IAccessProfileSession profile = new FakeAccessProfile(
            Info,
            FakeAccessProfileFactory.CreateServices(initializedContext.TimeProvider),
            initializedContext.TimeProvider);
        return Task.FromResult(PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult.Success(profile));
    }

    public ValueTask DisposeAsync()
    {
        if (context is not null)
        {
            File.WriteAllText(
                Path.Combine(context.ProfileRootPath, "plugin-disposed.marker"),
                "disposed");
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class MismatchingDirectoryLoadTestAccessProfilePlugin : IAccessProfilePlugin
{
    private AccessProfileContext? context;

    public AccessProfileInfo Info { get; } = new("other", "Other Directory Test Profile", "1.0.0", "Mismatched test profile.");

    public void Initialize(AccessProfileContext context)
    {
        this.context = context;
    }

    public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
    {
        return new AccessProfileValidationResult(true, []);
    }

    public Task<PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult> LoadAsync(
        AccessProfileSessionRequest request,
        CancellationToken cancellationToken)
    {
        var initializedContext = context ?? throw new InvalidOperationException("Plugin not initialized.");
        IAccessProfileSession profile = new FakeAccessProfile(
            Info,
            FakeAccessProfileFactory.CreateServices(initializedContext.TimeProvider),
            initializedContext.TimeProvider);
        return Task.FromResult(PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult.Success(profile));
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
