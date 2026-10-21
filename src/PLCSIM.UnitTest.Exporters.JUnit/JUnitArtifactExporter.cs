using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Artifacts;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.UnitTests;
using System.Globalization;
using System.Xml.Linq;

namespace PLCSIM.UnitTest.Exporters.JUnit;

/// <summary>
/// Exports a stable UnitTest run epoch snapshot as JUnit XML.
/// </summary>
public sealed class JUnitArtifactExporter : IArtifactExporter
{
    private readonly TimeProvider timeProvider;

    public JUnitArtifactExporter(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ArtifactFormat Format => ArtifactFormat.JUnitXml;

    public event EventHandler<DiagnosticEventArgs>? DiagnosticEventHandler;

    public async Task<ExportResult> ExportAsync(
        ExportRequest request,
        UnitTestRunEpochSnapshot runEpoch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runEpoch);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Format != ArtifactFormat.JUnitXml)
        {
            ReportDiagnostic(
                "JUnitExporter.FormatMismatch",
                LogLevel.Error,
                $"JUnit exporter cannot write artifact format '{request.Format}'.",
                request,
                runEpoch);
            return new ExportResult(request.Format, ArtifactState.Failed, request.OutputPath);
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            ReportDiagnostic(
                "JUnitExporter.OutputPathMissing",
                LogLevel.Error,
                "JUnit XML output path is missing.",
                request,
                runEpoch);
            return new ExportResult(request.Format, ArtifactState.Failed, request.OutputPath);
        }

        string? temporaryPath = null;
        try
        {
            var document = CreateDocument(runEpoch);
            var targetPath = Path.GetFullPath(request.OutputPath);
            var directory = Path.GetDirectoryName(targetPath)
                ?? throw new InvalidOperationException("JUnit XML output path has no parent directory.");
            Directory.CreateDirectory(directory);

            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await document.SaveAsync(stream, SaveOptions.None, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, overwrite: true);
            temporaryPath = null;
            return new ExportResult(request.Format, ArtifactState.Written, request.OutputPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportDiagnostic(
                "JUnitExporter.WriteFailed",
                LogLevel.Error,
                "JUnit XML artifact could not be written.",
                request,
                runEpoch,
                ex.Message);
            return new ExportResult(request.Format, ArtifactState.Failed, request.OutputPath);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static XDocument CreateDocument(UnitTestRunEpochSnapshot runEpoch)
    {
        var suites = GetSuiteGroups(runEpoch.Root).ToArray();
        var totals = suites.Aggregate(
            new JUnitCounts(),
            static (sum, suite) => sum + suite.Counts);

        var root = new XElement(
            "testsuites",
            new XAttribute("name", runEpoch.Root.DisplayName),
            new XAttribute("tests", totals.Tests),
            new XAttribute("failures", totals.Failures),
            new XAttribute("errors", totals.Errors),
            new XAttribute("skipped", 0),
            new XAttribute("time", FormatSeconds(suites.Sum(static suite => suite.Seconds))));

        foreach (var suite in suites)
        {
            root.Add(CreateSuiteElement(suite));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static XElement CreateSuiteElement(JUnitSuite suite)
    {
        var element = new XElement(
            "testsuite",
            new XAttribute("name", suite.Name),
            new XAttribute("tests", suite.Counts.Tests),
            new XAttribute("failures", suite.Counts.Failures),
            new XAttribute("errors", suite.Counts.Errors),
            new XAttribute("skipped", 0),
            new XAttribute("time", FormatSeconds(suite.Seconds)));

        foreach (var testCase in suite.TestCases)
        {
            element.Add(CreateTestCaseElement(testCase, suite.Name));
        }

        return element;
    }

    private static XElement CreateTestCaseElement(UnitTestRunNode testCase, string suiteName)
    {
        var outcome = Classify(testCase);
        var element = new XElement(
            "testcase",
            new XAttribute("classname", suiteName),
            new XAttribute("name", testCase.DisplayName),
            new XAttribute("time", FormatSeconds(GetRuntimeSeconds(testCase))));

        if (outcome.Kind == JUnitOutcomeKind.Failure)
        {
            element.Add(new XElement(
                "failure",
                new XAttribute("message", outcome.Message),
                outcome.Detail));
        }
        else if (outcome.Kind == JUnitOutcomeKind.Error)
        {
            element.Add(new XElement(
                "error",
                new XAttribute("message", outcome.Message),
                outcome.Detail));
        }

        return element;
    }

    private static IEnumerable<JUnitSuite> GetSuiteGroups(UnitTestRunNode root)
    {
        var suiteNodes = Flatten(root)
            .Where(static node => node.Kind == UnitTestNodeKind.TestSuite)
            .ToArray();

        foreach (var suite in suiteNodes)
        {
            var testCases = Flatten(suite)
                .Where(static node => node.Kind == UnitTestNodeKind.TestCase)
                .ToArray();
            yield return new JUnitSuite(
                suite.DisplayName,
                testCases,
                Count(testCases),
                GetSuiteRuntimeSeconds(suite, testCases));
        }

        var suiteOwnedTestCases = suiteNodes
            .SelectMany(Flatten)
            .Where(static node => node.Kind == UnitTestNodeKind.TestCase)
            .ToHashSet();
        var rootTestCases = Flatten(root)
            .Where(node => node.Kind == UnitTestNodeKind.TestCase && !suiteOwnedTestCases.Contains(node))
            .ToArray();
        if (rootTestCases.Length > 0 || suiteNodes.Length == 0)
        {
            yield return new JUnitSuite(
                root.DisplayName,
                rootTestCases,
                Count(rootTestCases),
                rootTestCases.Sum(GetRuntimeSeconds));
        }
    }

    private static IEnumerable<UnitTestRunNode> Flatten(UnitTestRunNode node)
    {
        yield return node;
        foreach (var child in node.Children.SelectMany(Flatten))
        {
            yield return child;
        }
    }

    private static JUnitCounts Count(IReadOnlyList<UnitTestRunNode> testCases)
    {
        var failures = 0;
        var errors = 0;

        foreach (var testCase in testCases)
        {
            switch (Classify(testCase).Kind)
            {
                case JUnitOutcomeKind.Failure:
                    failures++;
                    break;
                case JUnitOutcomeKind.Error:
                    errors++;
                    break;
            }
        }

        return new JUnitCounts(testCases.Count, failures, errors);
    }

    private static JUnitOutcome Classify(UnitTestRunNode testCase)
    {
        var strongestFinding = testCase.Findings
            .Where(static finding => finding.Result is UnitTestResultState.Failed or UnitTestResultState.Error)
            .OrderBy(static finding => finding.Result == UnitTestResultState.Error ? 0 : 1)
            .FirstOrDefault();
        if (strongestFinding is not null)
        {
            return strongestFinding.Result == UnitTestResultState.Failed
                ? JUnitOutcome.Failure(strongestFinding.Message)
                : JUnitOutcome.Error(strongestFinding.Message);
        }

        if (testCase.EvidenceState is EvidenceState.Incomplete
            or EvidenceState.Ambiguous
            or EvidenceState.Conflicting)
        {
            return JUnitOutcome.Error($"Testcase evidence is {testCase.EvidenceState}.");
        }

        return testCase.Result switch
        {
            UnitTestResultState.Passed => JUnitOutcome.Passed,
            UnitTestResultState.Failed => JUnitOutcome.Failure("Testcase failed."),
            UnitTestResultState.Error => JUnitOutcome.Error("Testcase reported an error."),
            UnitTestResultState.NotEvaluated => JUnitOutcome.Error("Testcase was not evaluated."),
            UnitTestResultState.Abort => JUnitOutcome.Error("Testcase was aborted."),
            UnitTestResultState.Unknown => JUnitOutcome.Error("Testcase result is unknown."),
            _ => JUnitOutcome.Error($"Testcase result is {testCase.Result}.")
        };
    }

    private static double GetRuntimeSeconds(UnitTestRunNode node)
    {
        return (node.Aggregation?.Runtime ?? 0) / 1000d;
    }

    private static double GetSuiteRuntimeSeconds(
        UnitTestRunNode suite,
        IReadOnlyCollection<UnitTestRunNode> testCases)
    {
        return suite.Aggregation?.Runtime is { } runtime
            ? runtime / 1000d
            : testCases.Sum(GetRuntimeSeconds);
    }

    private static string FormatSeconds(double seconds)
    {
        return seconds.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void ReportDiagnostic(
        string code,
        LogLevel level,
        string message,
        ExportRequest request,
        UnitTestRunEpochSnapshot runEpoch,
        string? technicalDetail = null)
    {
        var diagnostic = new Diagnostic(
            code,
            DiagnosticCategory.Output,
            level,
            DiagnosticSource.OutputCoordinator,
            DiagnosticPhase.OutputCoordination,
            message,
            new DiagnosticCorrelation(
                UnitTestRunEpochId: runEpoch.EpochId,
                OutputRequestId: new OutputRequestId($"{request.Format}:{request.OutputPath}")),
            timeProvider.GetUtcNow(),
            TechnicalDetail: technicalDetail);
        DiagnosticEventHandler?.Invoke(this, new DiagnosticEventArgs(diagnostic));
    }

    private sealed record JUnitSuite(
        string Name,
        IReadOnlyList<UnitTestRunNode> TestCases,
        JUnitCounts Counts,
        double Seconds);

    private sealed record JUnitCounts(int Tests = 0, int Failures = 0, int Errors = 0)
    {
        public static JUnitCounts operator +(JUnitCounts left, JUnitCounts right)
        {
            return new JUnitCounts(
                left.Tests + right.Tests,
                left.Failures + right.Failures,
                left.Errors + right.Errors);
        }
    }

    private sealed record JUnitOutcome(JUnitOutcomeKind Kind, string Message, string Detail)
    {
        public static JUnitOutcome Passed { get; } = new(JUnitOutcomeKind.Passed, string.Empty, string.Empty);

        public static JUnitOutcome Failure(string message)
        {
            return new JUnitOutcome(JUnitOutcomeKind.Failure, message, message);
        }

        public static JUnitOutcome Error(string message)
        {
            return new JUnitOutcome(JUnitOutcomeKind.Error, message, message);
        }
    }

    private enum JUnitOutcomeKind
    {
        Passed,
        Failure,
        Error
    }
}
