using System.IO;
using PLCSIM.UnitTest.App.Wpf.Services;
using PLCSIM.UnitTest.Core.Diagnostics;
using PLCSIM.UnitTest.Core.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Wpf;

public sealed class ConfigurationFileLoadServiceTests : IDisposable
{
    private readonly string temporaryDirectory;

    public ConfigurationFileLoadServiceTests()
    {
        temporaryDirectory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "PLCSIM.UnitTest.ConfigurationFileLoadServiceTests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(temporaryDirectory);
    }

    [Fact]
    public async Task Load_async_accepts_valid_fake_configuration_and_preserves_profile_payload()
    {
        var path = WriteConfiguration(
            """
            {
              "version": 1,
              "project": {
                "source": "fake-project.ap"
              },
              "accessProfile": {
                "id": "fake",
                "version": "1.0.0",
                "values": {
                  "plcRef": {
                    "id": "fake-project-target"
                  },
                  "runtimeRef": {
                    "id": "fake-runtime-target"
                  }
                }
              },
              "execution": {
                "timeouts": {
                  "projectPrepare": "00:01:00",
                  "buildTransfer": "00:10:00",
                  "plcCommand": "00:00:10",
                  "plcStartReaction": "00:00:30",
                  "plcStopReaction": "00:00:30",
                  "unitTestCommand": "00:00:03",
                  "unitTestCommandReaction": "00:00:10",
                  "testRun": "00:02:00",
                  "finalEvent": "00:00:10"
                }
              }
            }
            """);

        var result = await CreateService().LoadAsync(path, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Configuration);
        Assert.NotNull(result.Setup);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("fake-project.ap", result.Configuration.Project.Source);
        Assert.Equal("fake", result.Setup.AccessProfile.ProfileId);
        Assert.Equal("1.0.0", result.Setup.AccessProfile.ProfileVersion);
        Assert.Equal("fake-project.ap", result.Setup.AccessProfile.ProjectSource);
        Assert.Contains("\"plcRef\"", result.Setup.AccessProfile.Values.Json, StringComparison.Ordinal);
        Assert.Contains("\"runtimeRef\"", result.Setup.AccessProfile.Values.Json, StringComparison.Ordinal);
        Assert.Equal("artifacts", result.Setup.OutputPolicy.Directory);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Setup.Execution.Timeouts.PlcCommand);
        Assert.Equal(TimeSpan.FromSeconds(3), result.Setup.Execution.Timeouts.UnitTestCommand);
        Assert.Equal(TimeSpan.FromMinutes(2), result.Setup.Execution.Timeouts.TestRun);
    }

    [Fact]
    public async Task Load_async_accepts_repository_fake_configuration_sample()
    {
        var path = GetRepositoryPath(
            "access-profiles",
            "fake",
            "tests",
            "Resources",
            "Configuration",
            "fake-unittest.config.json");

        var result = await CreateService().LoadAsync(path, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Setup);
        using var values = System.Text.Json.JsonDocument.Parse(result.Setup!.AccessProfile.Values.Json);
        var settings = values.RootElement.GetProperty("testSettings");
        Assert.Equal("Configuration sample run", settings.GetProperty("name").GetString());
        Assert.Equal(2, settings.GetProperty("iterations").GetInt32());
        Assert.Equal(1.25f, settings.GetProperty("factor").GetSingle());
        Assert.True(settings.GetProperty("enabled").GetBoolean());
        Assert.Equal("diagnostic", settings.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Load_async_preserves_archive_source_and_opaque_profile_workspace()
    {
        var path = WriteConfiguration(
            """
            {
              "version": 1,
              "project": {
                "source": "D:\\Projects\\Sample.zap21"
              },
              "accessProfile": {
                "id": "siemens",
                "version": "1.0.0",
                "values": {
                  "tia": {
                    "archiveWorkDirectory": "D:\\TIA\\ArchiveWork"
                  }
                }
              }
            }
            """);

        var result = await CreateService().LoadAsync(path, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Setup);
        Assert.Equal(@"D:\Projects\Sample.zap21", result.Setup.AccessProfile.ProjectSource);
        using var values = System.Text.Json.JsonDocument.Parse(result.Setup.AccessProfile.Values.Json);
        Assert.Equal(
            @"D:\TIA\ArchiveWork",
            values.RootElement.GetProperty("tia").GetProperty("archiveWorkDirectory").GetString());
    }

    [Fact]
    public async Task Load_async_discards_persisted_safety_password_for_wpf_use()
    {
        var path = WriteConfiguration(
            """
            {
              "version": 1,
              "project": {
                "source": "sample.ap21",
                "authorization": {
                  "safetyPassword": "must-not-enter-wpf-state"
                }
              },
              "accessProfile": {
                "id": "siemens"
              }
            }
            """);

        var result = await CreateService().LoadAsync(path, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Configuration);
        Assert.Null(result.Configuration.Project.Authorization);
        Assert.DoesNotContain(
            "must-not-enter-wpf-state",
            result.Configuration.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_async_returns_diagnostic_for_missing_file()
    {
        var path = System.IO.Path.Combine(temporaryDirectory, "missing.config.json");
        var sink = new DiagnosticSink();

        var result = await CreateService(sink).LoadAsync(path, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Null(result.Configuration);
        Assert.Null(result.Setup);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("Wpf.Configuration.File.NotFound", diagnostic.Code);
        Assert.Equal(path, diagnostic.EvidenceReference?.Reference);
        var entry = Assert.Single(sink.Current.Entries);
        Assert.Equal("Wpf.Configuration.File.NotFound", entry.Current.Code);
        Assert.Equal(1, entry.OccurrenceCount);
    }

    [Fact]
    public async Task Load_async_returns_diagnostic_for_invalid_json()
    {
        var path = WriteConfiguration("{");

        var result = await CreateService().LoadAsync(path, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Null(result.Configuration);
        Assert.Null(result.Setup);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Wpf.Configuration.Json.Invalid");
    }

    [Fact]
    public async Task Load_async_rejects_non_object_profile_values()
    {
        var path = WriteConfiguration(
            """
            {
              "version": 1,
              "project": {
                "source": "fake-project.ap"
              },
              "accessProfile": {
                "id": "fake",
                "values": "invalid"
              }
            }
            """);

        var result = await CreateService().LoadAsync(path, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Configuration);
        Assert.Null(result.Setup);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Wpf.Configuration.AccessProfile.Values.Invalid");
    }

    [Fact]
    public async Task Load_async_rejects_unsupported_contract_version()
    {
        var path = WriteConfiguration(
            """
            {
              "version": 3,
              "project": {
                "source": "fake-project.ap"
              },
              "accessProfile": {
                "id": "fake"
              }
            }
            """);

        var result = await CreateService().LoadAsync(path, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Configuration);
        Assert.Null(result.Setup);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "Wpf.Configuration.Version.Unsupported");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Load_accepts_runtime_intent_only_in_version_two(int version)
    {
        var path = WriteConfiguration($$"""
            {"version":{{version}},"project":{"source":"fake.ap"},"accessProfile":{"id":"fake"},
             "runtime":{"provisioningMode":"UseExisting"} }
            """);
        var result = await CreateService().LoadAsync(path, CancellationToken.None);
        Assert.Equal(version == 2, result.IsValid);
        if (version == 2)
        {
            Assert.Equal(PLCSIM.UnitTest.Common.AccessProfiles.RuntimeProvisioningMode.UseExisting, result.Setup!.RuntimeProvisioningMode);
            Assert.Null(result.Setup.RuntimeTarget);
        }
        else
        {
            Assert.Null(result.Setup);
            Assert.Contains(result.Diagnostics, item => item.Code == "Wpf.Configuration.Runtime.RequiresVersion2");
        }
    }

    [Theory]
    [InlineData("null", "Empty")]
    [InlineData("{\"version\":2,\"project\":null,\"accessProfile\":{\"id\":\"fake\"}}", "Project.Source.Missing")]
    public async Task Shared_shape_errors_keep_wpf_diagnostic_context(string json, string code)
    {
        var path = WriteConfiguration(json);
        var sink = new DiagnosticSink();
        var result = await CreateService(sink).LoadAsync(path, CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Null(result.Setup);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("Wpf.Configuration." + code, diagnostic.Code);
        Assert.Equal(PLCSIM.UnitTest.Common.Diagnostics.DiagnosticSource.WpfApplication, diagnostic.Source);
        Assert.Equal(path, diagnostic.EvidenceReference?.Reference);
        var entry = Assert.Single(sink.Current.Entries);
        Assert.Equal(diagnostic.Code, entry.Current.Code);
        Assert.Equal(1, entry.OccurrenceCount);
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(temporaryDirectory))
        {
            System.IO.Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private string WriteConfiguration(string content)
    {
        var path = System.IO.Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}.config.json");
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private static ConfigurationFileLoadService CreateService(DiagnosticSink? diagnosticSink = null)
    {
        var diagnostics = new DiagnosticServices(diagnosticSink ?? new DiagnosticSink());
        var timeProvider = TimeProvider.System;
        return new ConfigurationFileLoadService(
            new RunSetupValidator(timeProvider),
            timeProvider,
            diagnostics.Writer,
            NullLogger<ConfigurationFileLoadService>.Instance);
    }

    private static string GetRepositoryPath(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "access-profiles"))
                && Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return Path.Combine(directory.FullName, Path.Combine(pathParts));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root could not be located.");
    }
}
