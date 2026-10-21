using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Testing.Core;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.AccessProfiles;

public sealed class AccessProfileDataDirectoryResolverTests
{
    [Fact]
    public void Resolve_is_stable_for_the_same_profile_source()
    {
        var root = CreateTemporaryDirectory();
        var package = CreateTemporaryDirectory();
        var resolver = new AccessProfileDataDirectoryResolver(new AccessProfileDataOptions(root));
        var candidate = CreateCandidate("siemens", "1.0.0", package);

        var first = resolver.Resolve(candidate);
        File.WriteAllText(Path.Combine(package, "changed-after-resolution.txt"), "changed");
        var second = resolver.Resolve(candidate);

        Assert.Equal(first, second);
        Assert.StartsWith(Path.GetFullPath(root), first, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, Path.GetFileName(first).Length);
    }

    [Fact]
    public void Resolve_isolates_profile_version_and_package_source()
    {
        var resolver = new AccessProfileDataDirectoryResolver(
            new AccessProfileDataOptions(CreateTemporaryDirectory()));
        var firstSource = CreateTemporaryDirectory();
        var secondSource = CreateTemporaryDirectory();

        var first = resolver.Resolve(CreateCandidate("siemens", "1.0.0", firstSource));
        var otherVersion = resolver.Resolve(CreateCandidate("siemens", "2.0.0", firstSource));
        var otherSource = resolver.Resolve(CreateCandidate("siemens", "1.0.0", secondSource));

        Assert.NotEqual(first, otherVersion);
        Assert.NotEqual(first, otherSource);
        Assert.Contains($"{Path.DirectorySeparatorChar}profile-siemens{Path.DirectorySeparatorChar}", first);
        Assert.Contains($"{Path.DirectorySeparatorChar}version-1.0.0{Path.DirectorySeparatorChar}", first);
    }

    [Fact]
    public void Resolve_uses_safe_readable_profile_segments()
    {
        var resolver = new AccessProfileDataDirectoryResolver(
            new AccessProfileDataOptions(CreateTemporaryDirectory()));
        var candidate = CreateCandidate("vendor/profile", "1:0", CreateTemporaryDirectory());

        var path = resolver.Resolve(candidate);
        var relativeSegments = Path.GetRelativePath(
                Path.GetFullPath(Path.Combine(path, "..", "..", "..")),
                path)
            .Split(Path.DirectorySeparatorChar);

        Assert.StartsWith("profile-", relativeSegments[0]);
        Assert.DoesNotContain('/', relativeSegments[0]);
        Assert.StartsWith("version-", relativeSegments[1]);
        Assert.DoesNotContain(':', relativeSegments[1]);
    }

    [Fact]
    public async Task Loader_creates_and_supplies_profile_data_directory_before_initialization()
    {
        var dataRoot = CreateTemporaryDirectory();
        var plugin = new CapturingPlugin();
        var loader = new AccessProfilePluginLoader(
            TestCoreServiceContext.Create(accessProfileDataRootDirectory: dataRoot),
            [plugin]);
        var discovery = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));
        var candidate = Assert.Single(discovery.Candidates);
        var expected = new AccessProfileDataDirectoryResolver(new AccessProfileDataOptions(dataRoot))
            .Resolve(candidate);

        await loader.LoadAsync(
            new AccessProfileSessionRequest(
                plugin.Info.ProfileId,
                plugin.Info.Version,
                "sample.fake",
                ProjectOwnershipMode.ApplicationManaged),
            discovery,
            CancellationToken.None);

        Assert.NotNull(plugin.Context);
        Assert.Equal(expected, plugin.Context.ProfileDataDirectory);
        Assert.Equal(candidate.SourcePath, plugin.Context.ProfileRootPath);
        Assert.True(Directory.Exists(expected));
    }

    [Fact]
    public async Task Loader_reports_initialization_failure_when_profile_data_root_is_not_a_directory()
    {
        var blockingFile = Path.Combine(CreateTemporaryDirectory(), "profile-data");
        File.WriteAllText(blockingFile, "not a directory");
        var plugin = new CapturingPlugin();
        var loader = new AccessProfilePluginLoader(
            TestCoreServiceContext.Create(accessProfileDataRootDirectory: blockingFile),
            [plugin]);
        var discovery = loader.Discover(new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true));

        var result = await loader.LoadAsync(
            new AccessProfileSessionRequest(
                plugin.Info.ProfileId,
                plugin.Info.Version,
                "sample.fake",
                ProjectOwnershipMode.ApplicationManaged),
            discovery,
            CancellationToken.None);

        Assert.Null(plugin.Context);
        Assert.Equal(AccessProfileLoadState.Failed, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Profile.Load.InitializeFailed");
    }

    private static AccessProfileCandidate CreateCandidate(
        string profileId,
        string profileVersion,
        string sourcePath)
    {
        return new AccessProfileCandidate(
            new AccessProfileInfo(profileId, profileId, profileVersion, "test"),
            AccessProfileSourceKind.DirectoryPackage,
            sourcePath,
            null,
            AccessProfileCandidateState.Discovered,
            Array.Empty<Diagnostic>());
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            "tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CapturingPlugin : IAccessProfilePlugin
    {
        public AccessProfileInfo Info { get; } = new(
            "capture",
            "Capture",
            "1.0.0",
            "Captures initialization context.");

        public AccessProfileContext? Context { get; private set; }

        public void Initialize(AccessProfileContext context)
        {
            Context = context;
        }

        public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
        {
            return new AccessProfileValidationResult(true, Array.Empty<Diagnostic>());
        }

        public Task<PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult> LoadAsync(
            AccessProfileSessionRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult.Failure(
                    "Expected test failure."));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
