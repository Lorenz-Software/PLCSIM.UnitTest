using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using PLCSIM.UnitTest.App.Wpf.Services;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Wpf;

public sealed partial class HelpContentTests
{
    [Fact]
    public void Packaged_help_contains_entry_page_and_resolvable_local_references()
    {
        var helpDirectory = Path.Combine(AppContext.BaseDirectory, "Help");
        var entryPage = Path.Combine(helpDirectory, "index.html");

        Assert.True(File.Exists(entryPage), $"Packaged help entry page was not found: {entryPage}");

        var pages = Directory.GetFiles(helpDirectory, "*.html", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(pages);
        Assert.Contains(
            "docs/configuration.md",
            File.ReadAllText(Path.Combine(helpDirectory, "configuration.html")),
            StringComparison.Ordinal);

        foreach (var page in pages)
        {
            var content = File.ReadAllText(page);
            Assert.Contains("analysis.html", content, StringComparison.Ordinal);
            var navigationStart = content.IndexOf("<nav", StringComparison.Ordinal);
            var navigationEnd = content.IndexOf("</nav>", navigationStart, StringComparison.Ordinal);
            var navigation = content[navigationStart..navigationEnd];
            var navigationLinks = LocalReferencePattern().Matches(navigation);
            Assert.Equal(
                new[] { "index.html", "workflow.html", "operation.html", "analysis.html", "diagnostics.html", "settings.html", "configuration.html", "ci-integration.html" },
                navigationLinks.Select(link => link.Groups["path"].Value));
            foreach (Match match in LocalReferencePattern().Matches(content))
            {
                var reference = WebUtility.HtmlDecode(match.Groups["path"].Value);
                if (ShouldIgnore(reference))
                {
                    continue;
                }

                var relativePath = reference
                    .Split(['#', '?'], 2, StringSplitOptions.None)[0]
                    .Replace('/', Path.DirectorySeparatorChar);
                var targetPath = Path.GetFullPath(
                    Path.Combine(Path.GetDirectoryName(page)!, relativePath));

                Assert.StartsWith(
                    Path.GetFullPath(helpDirectory) + Path.DirectorySeparatorChar,
                    targetPath,
                    StringComparison.OrdinalIgnoreCase);
                Assert.True(
                    File.Exists(targetPath),
                    $"Help reference '{reference}' in '{Path.GetFileName(page)}' does not exist.");
            }
        }
    }

    [Fact]
    public void Packaged_help_covers_release_operator_features()
    {
        var helpDirectory = Path.Combine(AppContext.BaseDirectory, "Help");
        var operation = File.ReadAllText(Path.Combine(helpDirectory, "operation.html"));
        var analysis = File.ReadAllText(Path.Combine(helpDirectory, "analysis.html"));
        var diagnostics = File.ReadAllText(Path.Combine(helpDirectory, "diagnostics.html"));
        var ciIntegration = File.ReadAllText(Path.Combine(helpDirectory, "ci-integration.html"));
        var workflow = File.ReadAllText(Path.Combine(helpDirectory, "workflow.html"));
        var configuration = File.ReadAllText(Path.Combine(helpDirectory, "configuration.html"));
        var license = File.ReadAllText(Path.Combine(helpDirectory, "license.html"));

        Assert.Contains("Reset Event Buffer", operation, StringComparison.Ordinal);
        Assert.Contains("lost events", operation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Run Analysis", analysis, StringComparison.Ordinal);
        Assert.Contains("PLC Lifecycle Analysis", analysis, StringComparison.Ordinal);
        Assert.Contains("Communication Health Analysis", analysis, StringComparison.Ordinal);
        Assert.Contains("analysis.md", analysis, StringComparison.Ordinal);
        Assert.Contains("assets/screenshots/evidence-analysis.png", analysis, StringComparison.Ordinal);
        Assert.Contains("Help &gt; Logs", diagnostics, StringComparison.Ordinal);
        Assert.Contains("Examples\\CI", ciIntegration, StringComparison.Ordinal);
        Assert.Contains("Provision the agent once", ciIntegration, StringComparison.Ordinal);
        Assert.Contains("register-profiles", ciIntegration, StringComparison.Ordinal);
        Assert.Contains("teamcity.kts", ciIntegration, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"../Examples/", ciIntegration, StringComparison.Ordinal);
        Assert.Contains("self-managed Windows agent", ciIntegration, StringComparison.Ordinal);
        Assert.Contains("authoritative job result", ciIntegration, StringComparison.Ordinal);
        Assert.Contains("Refresh Profile Registrations", workflow, StringComparison.Ordinal);
        Assert.Contains("profiles\\&lt;profile package&gt;\\docs\\configuration.md", configuration, StringComparison.Ordinal);
        Assert.Contains("Third-party notices", license, StringComparison.Ordinal);
        Assert.Contains("THIRD-PARTY-NOTICES.txt", license, StringComparison.Ordinal);
        Assert.Contains("assets/screenshots/about-dialog.png", license, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_service_resolves_entry_page_below_application_directory()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var service = new ShellHelpContentOpenService(applicationDirectory);

        Assert.Equal(
            Path.Combine(Path.GetFullPath(applicationDirectory), "Help", "index.html"),
            service.EntryPagePath);
    }

    [Fact]
    public void Help_service_reports_missing_packaged_entry_page()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var service = new ShellHelpContentOpenService(applicationDirectory);

        var exception = Assert.Throws<FileNotFoundException>(service.Open);

        Assert.Equal(service.EntryPagePath, exception.FileName);
        Assert.Contains("packaged offline help", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldIgnore(string reference)
    {
        return string.IsNullOrWhiteSpace(reference)
            || reference.StartsWith('#')
            || Uri.TryCreate(reference, UriKind.Absolute, out _);
    }

    [GeneratedRegex("(?:href|src)=\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LocalReferencePattern();
}
