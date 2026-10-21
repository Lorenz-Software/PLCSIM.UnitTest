using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.App.Wpf.ViewModels;
using PLCSIM.UnitTest.Common.AccessProfiles;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Wpf;

public sealed class ProfileConfigurationEditorViewModelTests
{
    [Fact]
    public void Missing_mandatory_configuration_service_is_rejected()
    {
        var viewModel = new ProfileConfigurationEditorViewModel();

        Assert.Throws<ArgumentNullException>(() =>
            viewModel.Load(null!, CreateConfig("{}"), RuntimeProvisioningMode.UseExisting));
    }

    [Fact]
    public void Profile_validation_gates_progression_and_successful_edit_replaces_payload()
    {
        var viewModel = new ProfileConfigurationEditorViewModel();
        var service = new FakeProfileConfigurationService(FakeAccessProfilePlugin.CreateDefaultInfo());
        viewModel.Load(
            service,
            CreateConfig("{ \"testSettings\": { \"iterations\": 0 }, \"future\": 42 }"),
            RuntimeProvisioningMode.UseExisting);

        Assert.False(viewModel.Validate(updateStatus: true));
        Assert.Contains("positive integer", viewModel.Status, StringComparison.Ordinal);

        var iterations = Assert.Single(
            viewModel.Fields,
            field => field.Id == "fake.test.iterations");
        iterations.Value = "2";

        Assert.Same(
            iterations,
            Assert.Single(viewModel.Fields, field => field.Id == "fake.test.iterations"));
        Assert.True(viewModel.Validate(updateStatus: true));
        Assert.NotNull(viewModel.Configuration);
        Assert.Contains("\"iterations\": 2", viewModel.Configuration.Values.Json, StringComparison.Ordinal);
        Assert.Contains("\"future\": 42", viewModel.Configuration.Values.Json, StringComparison.Ordinal);
    }

    private static AccessProfileConfig CreateConfig(string values)
    {
        return new AccessProfileConfig(
            FakeAccessProfilePlugin.DefaultProfileId,
            FakeAccessProfilePlugin.DefaultProfileVersion,
            new AccessProfileConfigurationPayload(values),
            "fake-project.json");
    }
}
