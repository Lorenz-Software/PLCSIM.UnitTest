using PLCSIM.UnitTest.App.Wpf.Services;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Core.AccessProfiles;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Wpf;

public sealed class ProjectSourceFileDialogFilterBuilderTests
{
    [Fact]
    public void Build_includes_all_usable_registered_project_source_capabilities()
    {
        var profile = new AccessProfileRegistryEntry(
            "siemens",
            "1.0.0",
            "Siemens",
            new AccessProfileRegistrySource("package", @"D:\Profiles\siemens", "fingerprint"),
            new AccessProfileRegistryRegistration(
                AccessProfileRegistrationState.Registered,
                DateTimeOffset.UtcNow,
                null),
            new AccessProfileCapabilities(
                Array.Empty<AccessApiCapability>(),
                [
                    new ProjectSourceCapability(
                        "tia-project-v21",
                        ".ap21",
                        "21",
                        CapabilityAvailability.Usable),
                    new ProjectSourceCapability(
                        "tia-project-v21",
                        ".zap21",
                        "21",
                        CapabilityAvailability.Usable),
                    new ProjectSourceCapability(
                        "unavailable-project",
                        ".unavailable",
                        "1",
                        CapabilityAvailability.Unavailable)
                ],
                Array.Empty<RuntimeCapability>(),
                Array.Empty<string>()));

        var filter = ProjectSourceFileDialogFilterBuilder.Build([profile]);

        Assert.Equal(
            "Supported project files (*.ap21;*.zap21)|*.ap21;*.zap21|All files (*.*)|*.*",
            filter);
    }
}
