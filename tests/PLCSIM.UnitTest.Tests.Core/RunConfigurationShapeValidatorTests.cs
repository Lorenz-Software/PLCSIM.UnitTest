using System.Text.Json;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Core.Workflow;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core;

public sealed class RunConfigurationShapeValidatorTests
{
    private static RunConfigurationFile? Parse(string json) => JsonSerializer.Deserialize<RunConfigurationFile>(json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    [Fact]
    public void Null_configuration_returns_only_empty_error()
    {
        Assert.Equal(("Empty", "Configuration file is empty or does not contain a run configuration."),
            Assert.Single(RunConfigurationShapeValidator.Validate(null)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Both_supported_versions_allow_omitted_runtime(int version)
    {
        Assert.Empty(RunConfigurationShapeValidator.Validate(Parse($$"""
            {"version":{{version}},"project":{"source":"sample.ap"},"accessProfile":{"id":"test"} }
            """)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Runtime_section_requires_version_two(int version)
    {
        var errors = RunConfigurationShapeValidator.Validate(Parse($$"""
            {"version":{{version}},"project":{"source":"sample.ap"},"accessProfile":{"id":"test"},"runtime":{} }
            """));
        if (version == 2) Assert.Empty(errors);
        else Assert.Equal(("Runtime.RequiresVersion2", "The runtime section requires configuration version 2."), Assert.Single(errors));
    }

    [Fact]
    public void Errors_preserve_order_and_exact_messages()
    {
        var errors = RunConfigurationShapeValidator.Validate(Parse("""
            {"version":99,"project":{"source":" "},"accessProfile":{"id":"","values":[]}}
            """));
        Assert.Equal(new (string, string)[]
        {
            ("Version.Unsupported", "Configuration version '99' is not supported. Expected version 1 or 2."),
            ("Project.Source.Missing", "Configuration project.source is missing."),
            ("AccessProfile.Id.Missing", "Configuration accessProfile.id is missing."),
            ("AccessProfile.Values.Invalid", "Configuration accessProfile.values must be a JSON object when present.")
        }, errors);
    }

    [Theory]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"version\":2,\"project\":null,\"accessProfile\":null}")]
    public void Missing_sections_are_reported_without_dereferencing_them(string json)
    {
        Assert.Equal(new[] { "Project.Source.Missing", "AccessProfile.Id.Missing" },
            RunConfigurationShapeValidator.Validate(Parse(json)).Select(error => error.Code));
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("{}", true)]
    [InlineData("[]", false)]
    [InlineData("true", false)]
    [InlineData("42", false)]
    [InlineData("\"text\"", false)]
    public void Profile_values_are_optional_but_must_be_an_object_when_present(string values, bool valid)
    {
        var configuration = Parse($$"""
            {"version":2,"project":{"source":"sample.ap"},"accessProfile":{"id":"test","values":{{values}} } }
            """);
        Assert.Equal(valid, RunConfigurationShapeValidator.Validate(configuration).Count == 0);
    }
}
