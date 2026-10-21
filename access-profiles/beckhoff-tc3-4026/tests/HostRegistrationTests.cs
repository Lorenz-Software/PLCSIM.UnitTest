using System.Security.Cryptography;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Testing.Core;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff;

public sealed class HostRegistrationTests : IDisposable
{
    private const string ProfileId = "beckhoff-tc3-4026";
    private readonly string root = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "beckhoff-host-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task HostPersistsRegistrationAndSelectsSourcesAccordingToInstalledPrerequisites()
    {
        var package = Path.Combine(ProfilePackageTests.Packages, ProfileId);
        var before = Fingerprint(package);
        var context = TestCoreServiceContext.Create(accessProfileDataRootDirectory: Path.Combine(root, "data"));
        var loader = new AccessProfilePluginLoader(context, []);
        var discovery = new AccessProfileDiscoveryOptions(Path.GetDirectoryName(package)!, false);
        var validator = new AccessProfileCapabilityValidator();
        var store = new FileApplicationProfileRegistryStore(context, new(Path.Combine(root, "registry.json")), validator);
        var service = new ApplicationProfileRegistrationService(loader, discovery, store, validator, context);

        var refresh = await service.RefreshAllProfileRegistrationsAsync(TimeSpan.FromSeconds(30), default);
        Assert.Equal(ApplicationProfileRegistrationRefreshStatus.Completed, refresh.Status);
        var entry = Assert.Single((await store.LoadAsync(default)).Profiles);
        Assert.Equal(ProfileId, entry.ProfileId);
        var available = entry.Capabilities.AccessApis.All(api => api.Availability == CapabilityAvailability.Usable);
        Assert.Equal(available ? AccessProfileRegistrationState.Registered : AccessProfileRegistrationState.Unavailable,
            entry.Registration.State);
        Assert.True(validator.ValidateRegistry(await store.LoadAsync(default)).IsValid);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "data"), "profile-registry.json", SearchOption.AllDirectories));
        var check = await service.CheckProfileRegistrationsAsync(default);
        Assert.Equal(available ? ApplicationProfileRegistrationCheckStatus.Current
            : ApplicationProfileRegistrationCheckStatus.RefreshRequired, check.Status);

        var selection = new ApplicationProfileSelectionService(loader, discovery, store, context);
        var source = Path.Combine(root, "fixture.sln");
        File.WriteAllText(Path.Combine(root, "fixture.tsproj"), "<TcSmProject TcVersion=\"3.1.4026.26\"><Project><Plc /></Project></TcSmProject>");
        File.WriteAllText(source, "Microsoft Visual Studio Solution File, Format Version 12.00\nProject(\"{B1E792BE-AA5F-4E3C-8C82-674BF9C0715B}\") = \"System\", \"fixture.tsproj\", \"{0684F38A-C466-4A6C-8EBF-1AEFEE1862A7}\"\nEndProject\n");
        var selected = await selection.EvaluateProjectSourceAsync(source, default);
        if (available) Assert.Single(selected.Profiles);
        else Assert.Empty(selected.Profiles);
        Assert.Empty(selected.Diagnostics);
        Assert.Equal(before, Fingerprint(package));
    }

    [Fact]
    public async Task MissingEntryAssemblyFailsRegistrationInsteadOfCreatingUsableCapabilities()
    {
        var package = Path.Combine(root, "profiles", ProfileId);
        Directory.CreateDirectory(package);
        File.Copy(Path.Combine(ProfilePackageTests.Packages, ProfileId, "manifest.json"), Path.Combine(package, "manifest.json"));
        var context = TestCoreServiceContext.Create(accessProfileDataRootDirectory: Path.Combine(root, "data"));
        var validator = new AccessProfileCapabilityValidator();
        var store = new FileApplicationProfileRegistryStore(context, new(Path.Combine(root, "registry.json")), validator);
        var service = new ApplicationProfileRegistrationService(new(context, []), new(Path.GetDirectoryName(package)!, false), store, validator, context);

        var refresh = await service.RefreshAllProfileRegistrationsAsync(TimeSpan.FromSeconds(30), default);
        Assert.Equal(ApplicationProfileRegistrationRefreshStatus.CompletedWithErrors, refresh.Status);
        var entry = Assert.Single((await store.LoadAsync(default)).Profiles);
        Assert.Equal(AccessProfileRegistrationState.Error, entry.Registration.State);
        Assert.Empty(entry.Capabilities.AccessApis);
        Assert.False(Directory.Exists(Path.Combine(root, "data")));
    }

    private static string[] Fingerprint(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.OrdinalIgnoreCase)
        .Select(path => Path.GetRelativePath(directory, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        .ToArray();

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
