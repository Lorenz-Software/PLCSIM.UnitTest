using PLCSIM.UnitTest.Common.Artifacts;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Exporters.JUnit;
using System.Xml.Linq;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Exporters.JUnit;

public sealed class JUnitArtifactExporterTests
{
    [Fact]
    public async Task Passed_testcase_is_written_without_result_child()
    {
        using var output = new TemporaryArtifactFile();
        var exporter = new JUnitArtifactExporter();
        var snapshot = CreateSnapshot([
            CreateSuite("MotorControl", [
                CreateTestCase("StartMotor", UnitTestResultState.Passed, EvidenceState.Complete, runtime: 25)
            ])
        ]);

        var result = await exporter.ExportAsync(CreateRequest(output.Path), snapshot, CancellationToken.None);

        var document = XDocument.Load(output.Path);
        var testCase = Assert.Single(document.Descendants("testcase"));
        var testSuite = Assert.Single(document.Descendants("testsuite"));
        Assert.Equal(ArtifactState.Written, result.State);
        Assert.Equal("MotorControl", (string?)testCase.Attribute("classname"));
        Assert.Equal("StartMotor", (string?)testCase.Attribute("name"));
        Assert.Equal("0.025", (string?)testCase.Attribute("time"));
        Assert.Equal("0.025", (string?)testSuite.Attribute("time"));
        Assert.Empty(testCase.Elements());
        Assert.Equal("1", (string?)document.Root?.Attribute("tests"));
        Assert.Equal("0", (string?)document.Root?.Attribute("failures"));
        Assert.Equal("0", (string?)document.Root?.Attribute("errors"));
        Assert.Null(document.Root?.Attribute("runEpochId"));
    }

    [Fact]
    public async Task Suite_time_uses_suite_aggregation_when_testcase_runtime_is_missing()
    {
        using var output = new TemporaryArtifactFile();
        var exporter = new JUnitArtifactExporter();
        var snapshot = CreateSnapshot([
            CreateSuite("MotorControl", [
                CreateTestCase("StartMotor", UnitTestResultState.Passed, EvidenceState.Complete)
            ], runtime: 25)
        ]);

        await exporter.ExportAsync(CreateRequest(output.Path), snapshot, CancellationToken.None);

        var document = XDocument.Load(output.Path);
        var testSuite = Assert.Single(document.Descendants("testsuite"));
        var testCase = Assert.Single(document.Descendants("testcase"));
        Assert.Equal("0.025", (string?)testSuite.Attribute("time"));
        Assert.Equal("0", (string?)testCase.Attribute("time"));
    }

    [Fact]
    public async Task Run_epoch_wall_clock_duration_is_not_used_for_junit_time()
    {
        using var output = new TemporaryArtifactFile();
        var exporter = new JUnitArtifactExporter();
        var startedAt = DateTimeOffset.Parse("2026-06-23T10:00:00Z");
        var completedAt = startedAt.AddMinutes(5);
        var snapshot = CreateSnapshot(
            CreateRunControl([
                CreateSuite("MotorControl", [
                    CreateTestCase("StartMotor", UnitTestResultState.Passed, EvidenceState.Complete, runtime: 25)
                ])
            ], UnitTestResultState.Passed, EvidenceState.Complete),
            startedAt,
            completedAt);

        await exporter.ExportAsync(CreateRequest(output.Path), snapshot, CancellationToken.None);

        var document = XDocument.Load(output.Path);
        Assert.Equal("0.025", (string?)document.Root?.Attribute("time"));
        Assert.DoesNotContain("300", document.ToString(SaveOptions.DisableFormatting), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Supported_junit_xml_shape_uses_only_expected_elements_and_attributes()
    {
        using var output = new TemporaryArtifactFile();
        var exporter = new JUnitArtifactExporter();
        var snapshot = CreateSnapshot([
            CreateSuite("Safety", [
                CreateTestCase("PassedCase", UnitTestResultState.Passed, EvidenceState.Complete, runtime: 10),
                CreateTestCase("FailedCase", UnitTestResultState.Failed, EvidenceState.Complete, runtime: 20),
                CreateTestCase("ErrorCase", UnitTestResultState.Error, EvidenceState.Complete, runtime: 30),
                CreateTestCase("IncompleteCase", UnitTestResultState.Passed, EvidenceState.Incomplete, runtime: 40)
            ], runtime: 100)
        ]);

        await exporter.ExportAsync(CreateRequest(output.Path), snapshot, CancellationToken.None);

        var document = XDocument.Load(output.Path);
        Assert.Equal("testsuites", document.Root?.Name.LocalName);
        AssertOnlyAttributes(document.Root!, "name", "tests", "failures", "errors", "skipped", "time");
        Assert.Equal("4", (string?)document.Root?.Attribute("tests"));
        Assert.Equal("1", (string?)document.Root?.Attribute("failures"));
        Assert.Equal("2", (string?)document.Root?.Attribute("errors"));
        Assert.Equal("0.1", (string?)document.Root?.Attribute("time"));

        var testSuite = Assert.Single(document.Descendants("testsuite"));
        AssertOnlyAttributes(testSuite, "name", "tests", "failures", "errors", "skipped", "time");

        foreach (var testCase in document.Descendants("testcase"))
        {
            AssertOnlyAttributes(testCase, "classname", "name", "time");
        }

        foreach (var outcome in document.Descendants("failure").Concat(document.Descendants("error")))
        {
            AssertOnlyAttributes(outcome, "message");
        }

        string[] supportedElements = ["testsuites", "testsuite", "testcase", "failure", "error"];
        Assert.All(
            document.Descendants(),
            element => Assert.Contains(
                element.Name.LocalName,
                supportedElements));
    }

    [Fact]
    public async Task Failed_and_error_testcases_are_mapped_to_junit_children()
    {
        using var output = new TemporaryArtifactFile();
        var exporter = new JUnitArtifactExporter();
        var snapshot = CreateSnapshot([
            CreateSuite("Safety", [
                CreateTestCase("EmergencyStop", UnitTestResultState.Failed, EvidenceState.Complete),
                CreateTestCase("GuardDoor", UnitTestResultState.Error, EvidenceState.Complete)
            ])
        ]);

        await exporter.ExportAsync(CreateRequest(output.Path), snapshot, CancellationToken.None);

        var document = XDocument.Load(output.Path);
        var testCases = document.Descendants("testcase").ToArray();
        Assert.NotNull(testCases[0].Element("failure"));
        Assert.NotNull(testCases[1].Element("error"));
        Assert.Equal("1", (string?)document.Root?.Attribute("failures"));
        Assert.Equal("1", (string?)document.Root?.Attribute("errors"));
    }

    [Theory]
    [InlineData(UnitTestResultState.NotEvaluated, EvidenceState.Complete)]
    [InlineData(UnitTestResultState.Unknown, EvidenceState.Complete)]
    [InlineData(UnitTestResultState.Passed, EvidenceState.Incomplete)]
    [InlineData(UnitTestResultState.Passed, EvidenceState.Ambiguous)]
    [InlineData(UnitTestResultState.Passed, EvidenceState.Conflicting)]
    public async Task Not_evaluated_or_incomplete_testcase_is_mapped_to_error(
        UnitTestResultState resultState,
        EvidenceState evidenceState)
    {
        using var output = new TemporaryArtifactFile();
        var exporter = new JUnitArtifactExporter();
        var snapshot = CreateSnapshot([
            CreateSuite("Diagnostics", [
                CreateTestCase("SignalMapping", resultState, evidenceState)
            ])
        ]);

        await exporter.ExportAsync(CreateRequest(output.Path), snapshot, CancellationToken.None);

        var testCase = Assert.Single(XDocument.Load(output.Path).Descendants("testcase"));
        Assert.NotNull(testCase.Element("error"));
    }

    [Fact]
    public async Task Aborted_testcase_is_mapped_to_error()
    {
        using var output = new TemporaryArtifactFile();
        var exporter = new JUnitArtifactExporter();
        var snapshot = CreateSnapshot([
            CreateSuite("Recovery", [
                CreateTestCase("AbortCase", UnitTestResultState.Abort, EvidenceState.Complete)
            ])
        ]);

        await exporter.ExportAsync(CreateRequest(output.Path), snapshot, CancellationToken.None);

        var testCase = Assert.Single(XDocument.Load(output.Path).Descendants("testcase"));
        Assert.NotNull(testCase.Element("error"));
    }

    [Fact]
    public async Task Framework_finding_on_concrete_testcase_is_mapped_to_error()
    {
        using var output = new TemporaryArtifactFile();
        var exporter = new JUnitArtifactExporter();
        var snapshot = CreateSnapshot([
            CreateSuite("Framework", [
                CreateTestCase(
                    "FrameworkCase",
                    UnitTestResultState.Passed,
                    EvidenceState.Complete,
                    findings: [
                        new UnitTestFinding(
                            UnitTestFindingKind.Framework,
                            UnitTestResultState.Error,
                            "Framework error",
                            "PLC-side framework reported an error.",
                            null)
                    ])
            ])
        ]);

        await exporter.ExportAsync(CreateRequest(output.Path), snapshot, CancellationToken.None);

        var testCase = Assert.Single(XDocument.Load(output.Path).Descendants("testcase"));
        var error = Assert.Single(testCase.Elements("error"));
        Assert.Equal("PLC-side framework reported an error.", (string?)error.Attribute("message"));
    }

    [Fact]
    public async Task Run_and_suite_level_errors_do_not_create_synthetic_testcases()
    {
        using var output = new TemporaryArtifactFile();
        var exporter = new JUnitArtifactExporter();
        var root = CreateRunControl([
            CreateSuite("SuiteWithNoCases", [])
        ], UnitTestResultState.Error, EvidenceState.Conflicting);
        var snapshot = CreateSnapshot(root);

        await exporter.ExportAsync(CreateRequest(output.Path), snapshot, CancellationToken.None);

        var document = XDocument.Load(output.Path);
        Assert.Empty(document.Descendants("testcase"));
        Assert.Equal("0", (string?)document.Root?.Attribute("tests"));
        Assert.Equal("0", (string?)document.Root?.Attribute("failures"));
        Assert.Equal("0", (string?)document.Root?.Attribute("errors"));
    }

    [Fact]
    public async Task Missing_output_path_returns_failed_result_and_diagnostic()
    {
        var exporter = new JUnitArtifactExporter();
        var diagnostics = new List<Diagnostic>();
        exporter.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);

        var result = await exporter.ExportAsync(
            new ExportRequest(ArtifactFormat.JUnitXml, string.Empty, Required: true),
            CreateSnapshot([]),
            CancellationToken.None);

        Assert.Equal(ArtifactState.Failed, result.State);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "JUnitExporter.OutputPathMissing");
    }

    [Fact]
    public async Task Existing_artifact_file_is_overwritten_without_suffix_file()
    {
        using var output = new TemporaryArtifactFile();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output.Path)!);
        await File.WriteAllTextAsync(output.Path, "old artifact");
        var exporter = new JUnitArtifactExporter();
        var snapshot = CreateSnapshot([
            CreateSuite("MotorControl", [
                CreateTestCase("StartMotor", UnitTestResultState.Passed, EvidenceState.Complete)
            ])
        ]);

        var result = await exporter.ExportAsync(CreateRequest(output.Path), snapshot, CancellationToken.None);

        Assert.Equal(ArtifactState.Written, result.State);
        Assert.DoesNotContain("old artifact", await File.ReadAllTextAsync(output.Path));
        Assert.True(File.Exists(output.Path));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(output.Path)!, "junit*.xml"),
            path => path != output.Path);
    }

    [Fact]
    public async Task Failed_atomic_replace_removes_temporary_file()
    {
        using var output = new TemporaryArtifactFile();
        Directory.CreateDirectory(output.Path);
        var exporter = new JUnitArtifactExporter();

        var result = await exporter.ExportAsync(
            CreateRequest(output.Path),
            CreateSnapshot([]),
            CancellationToken.None);

        Assert.Equal(ArtifactState.Failed, result.State);
        Assert.Empty(Directory.EnumerateFiles(
            System.IO.Path.GetDirectoryName(output.Path)!,
            $".{System.IO.Path.GetFileName(output.Path)}.*.tmp"));
    }

    [Fact]
    public async Task Missing_output_directory_is_created()
    {
        using var output = new TemporaryArtifactFile();
        var exporter = new JUnitArtifactExporter();

        var result = await exporter.ExportAsync(CreateRequest(output.Path), CreateSnapshot([]), CancellationToken.None);

        Assert.Equal(ArtifactState.Written, result.State);
        Assert.True(File.Exists(output.Path));
    }

    [Fact]
    public async Task Aborted_run_retains_failed_cases_and_assertion_evidence_in_junit()
    {
        using var output = new TemporaryArtifactFile();
        var finding = new UnitTestFinding(UnitTestFindingKind.Assertion, UnitTestResultState.Failed,
            "ExpectedValue", "Expected TRUE, observed FALSE", new UnitTestEventRef(4));
        var suite = CreateSuite("Suite", [
            CreateTestCase("FinishedFailure", UnitTestResultState.Failed, EvidenceState.Complete),
            CreateTestCase("AbortedAfterFailure", UnitTestResultState.Abort, EvidenceState.Complete, findings: [finding]),
            CreateTestCase("Aborted", UnitTestResultState.Abort, EvidenceState.Complete)
        ]) with { Result = UnitTestResultState.Abort };
        var snapshot = CreateSnapshot(CreateRunControl([suite], UnitTestResultState.Abort, EvidenceState.Complete),
            state: UnitTestRunState.Aborted);
        var result = await new JUnitArtifactExporter().ExportAsync(CreateRequest(output.Path), snapshot, CancellationToken.None);
        Assert.Equal(ArtifactState.Written, result.State);
        var document = XDocument.Load(output.Path);
        Assert.Equal("2", (string?)document.Root?.Attribute("failures"));
        Assert.Equal("1", (string?)document.Root?.Attribute("errors"));
        Assert.Contains("Expected TRUE, observed FALSE", document.ToString());
        Assert.Equal(UnitTestResultState.Abort, snapshot.Root.Result);
    }

    private static ExportRequest CreateRequest(string outputPath)
    {
        return new ExportRequest(ArtifactFormat.JUnitXml, outputPath, Required: true);
    }

    private static void AssertOnlyAttributes(XElement element, params string[] expected)
    {
        Assert.Equal(
            expected.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            element.Attributes()
                .Select(static attribute => attribute.Name.LocalName)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray());
    }

    private static UnitTestRunEpochSnapshot CreateSnapshot(IReadOnlyList<UnitTestRunNode> suites)
    {
        return CreateSnapshot(CreateRunControl(suites, UnitTestResultState.Passed, EvidenceState.Complete));
    }

    private static UnitTestRunEpochSnapshot CreateSnapshot(
        UnitTestRunNode root,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        UnitTestRunState state = UnitTestRunState.Completed)
    {
        return new UnitTestRunEpochSnapshot(
            new RunEpochId("epoch-1"),
            state,
            root.Result,
            root.EvidenceState,
            startedAt ?? DateTimeOffset.UtcNow,
            completedAt ?? DateTimeOffset.UtcNow,
            [],
            [],
            root);
    }

    private static UnitTestRunNode CreateRunControl(
        IReadOnlyList<UnitTestRunNode> children,
        UnitTestResultState result,
        EvidenceState evidence)
    {
        return CreateNode("RunControl", UnitTestNodeKind.RunControl, result, evidence, children);
    }

    private static UnitTestRunNode CreateSuite(
        string name,
        IReadOnlyList<UnitTestRunNode> children,
        uint? runtime = null)
    {
        return CreateNode(
            name,
            UnitTestNodeKind.TestSuite,
            UnitTestResultState.Passed,
            EvidenceState.Complete,
            children,
            runtime is null
                ? null
                : new UnitTestAggregation(1, 0, 0, 0, 1, 1, 0, 0, runtime, 0));
    }

    private static UnitTestRunNode CreateTestCase(
        string name,
        UnitTestResultState result,
        EvidenceState evidence,
        uint? runtime = null,
        IReadOnlyList<UnitTestFinding>? findings = null)
    {
        return CreateNode(
            name,
            UnitTestNodeKind.TestCase,
            result,
            evidence,
            [],
            runtime is null
                ? null
                : new UnitTestAggregation(1, 0, 0, 0, 1, result == UnitTestResultState.Passed ? 1u : 0u, result == UnitTestResultState.Failed ? 1u : 0u, result == UnitTestResultState.Error ? 1u : 0u, runtime, 0),
            findings);
    }

    private static UnitTestRunNode CreateNode(
        string name,
        UnitTestNodeKind kind,
        UnitTestResultState result,
        EvidenceState evidence,
        IReadOnlyList<UnitTestRunNode> children,
        UnitTestAggregation? aggregation = null,
        IReadOnlyList<UnitTestFinding>? findings = null)
    {
        return new UnitTestRunNode(
            new UnitTestNodeId(name),
            name,
            name,
            kind,
            UnitTestLifecycleState.Completed,
            result,
            evidence,
            aggregation,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            findings ?? [],
            children);
    }

    private sealed class TemporaryArtifactFile : IDisposable
    {
        private readonly string directory;

        public TemporaryArtifactFile()
        {
            directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Path = System.IO.Path.Combine(directory, "junit.xml");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
