using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

public sealed class RunResultNodeViewModel : ObservableObject
{
    private bool isExpanded = true;

    public RunResultNodeViewModel(
        UnitTestRunNode node,
        bool isTerminalRun,
        int level = 0,
        bool forcePresentationError = false)
    {
        Id = node.Id.Value;
        Level = level;
        Indent = level * 16;
        IndentMargin = new Thickness(Indent, 0, 0, 0);
        Name = node.Name;
        DisplayName = string.IsNullOrWhiteSpace(node.DisplayName)
            ? node.Name
            : node.DisplayName;
        Kind = node.Kind.ToString();
        LifecycleState = node.LifecycleState.ToString();
        Result = node.Result.ToString();
        EvidenceState = node.EvidenceState.ToString();
        CreatedAt = FormatTimestamp(node.CreatedAt);
        LastUpdatedAt = FormatTimestamp(node.LastUpdatedAt);
        Runtime = FormatRuntime(node.Aggregation?.Runtime);
        Summary = FormatSummary(node);
        Findings = new ObservableCollection<RunResultFindingViewModel>(
            node.Findings.Select(finding => new RunResultFindingViewModel(finding)));
        Children = new ObservableCollection<RunResultNodeViewModel>(
            node.Children.Select(child => new RunResultNodeViewModel(child, isTerminalRun, level + 1)));
        HasMissingLocalEvidence = HasIncompleteRequiredEvidence(node, isTerminalRun);
        var presentationResult = DeterminePresentationResult(
            node.Result,
            isTerminalRun,
            HasMissingLocalEvidence,
            forcePresentationError,
            Children);
        PresentationResult = presentationResult.ToString();
        StatusBrush = RunResultStatusBrushes.ForResult(presentationResult);
        EvidenceWarningVisibility = HasMissingLocalEvidence
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatusToolTip = CreateStatusToolTip(
            node.Result,
            presentationResult,
            HasMissingLocalEvidence,
            forcePresentationError);
    }

    public string Id { get; }

    public int Level { get; }

    public int Indent { get; }

    public Thickness IndentMargin { get; }

    public string Name { get; }

    public string DisplayName { get; }

    public string Kind { get; }

    public string LifecycleState { get; }

    public string Result { get; }

    public string PresentationResult { get; }

    public string EvidenceState { get; }

    public string CreatedAt { get; }

    public string LastUpdatedAt { get; }

    public string Runtime { get; }

    public string Summary { get; }

    public Brush StatusBrush { get; }

    public bool HasMissingLocalEvidence { get; }

    public Visibility EvidenceWarningVisibility { get; }

    public string StatusToolTip { get; }

    public ObservableCollection<RunResultFindingViewModel> Findings { get; }

    public string FindingCountText => Findings.Count == 0
        ? "No findings"
        : $"{Findings.Count} finding(s)";

    public ObservableCollection<RunResultNodeViewModel> Children { get; }

    public bool IsExpandable => Kind == UnitTestNodeKind.TestSuite.ToString() && Children.Count > 0;

    public Visibility ExpanderVisibility => IsExpandable ? Visibility.Visible : Visibility.Hidden;

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (SetProperty(ref isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpanderIconData));
            }
        }
    }

    public string ExpanderIconData => IsExpanded ? "M 4,6 L 8,10 L 12,6 Z" : "M 6,4 L 10,8 L 6,12 Z";

    public static RunResultNodeViewModel FromNode(
        UnitTestRunNode node,
        bool isTerminalRun = false,
        bool forcePresentationError = false)
    {
        return new RunResultNodeViewModel(node, isTerminalRun, forcePresentationError: forcePresentationError);
    }

    private static UnitTestResultState DeterminePresentationResult(
        UnitTestResultState reportedResult,
        bool isTerminalRun,
        bool hasMissingLocalEvidence,
        bool forcePresentationError,
        IEnumerable<RunResultNodeViewModel> children)
    {
        var childResults = children.Select(child => child.PresentationResult).ToArray();
        if (reportedResult == UnitTestResultState.Error
            || hasMissingLocalEvidence
            || forcePresentationError
            || childResults.Contains(UnitTestResultState.Error.ToString(), StringComparer.Ordinal))
        {
            return UnitTestResultState.Error;
        }

        if (reportedResult == UnitTestResultState.Abort)
        {
            return UnitTestResultState.Abort;
        }

        if (reportedResult == UnitTestResultState.Failed
            || childResults.Contains(UnitTestResultState.Failed.ToString(), StringComparer.Ordinal))
        {
            return UnitTestResultState.Failed;
        }

        if (isTerminalRun && reportedResult == UnitTestResultState.NotEvaluated)
        {
            return UnitTestResultState.Error;
        }

        return reportedResult;
    }

    private static bool HasIncompleteRequiredEvidence(
        UnitTestRunNode node,
        bool isTerminalRun)
    {
        if (!isTerminalRun
            || node.EvidenceState == PLCSIM.UnitTest.Common.UnitTests.EvidenceState.Complete
            || node.CreationEvent is null)
        {
            return false;
        }

        return node.Kind == UnitTestNodeKind.RunControl
            ? node.ResultEvent is null || node.CompletionEvent is null
            : node.Result == UnitTestResultState.NotEvaluated
                && (node.ResultEvent is null || node.CompletionEvent is null);
    }

    private static string CreateStatusToolTip(
        UnitTestResultState reportedResult,
        UnitTestResultState presentationResult,
        bool hasMissingLocalEvidence,
        bool forcePresentationError)
    {
        var evidence = hasMissingLocalEvidence
            ? " Required result evidence is incomplete."
            : forcePresentationError
                ? " Run evidence is incomplete."
            : string.Empty;
        return $"Reported result: {reportedResult}. Presentation state: {presentationResult}.{evidence}";
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp)
    {
        return timestamp?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "-";
    }

    private static string FormatRuntime(uint? runtime)
    {
        return runtime is null
            ? "-"
            : $"{runtime} ms";
    }

    private static string FormatSummary(UnitTestRunNode node)
    {
        return node.Aggregation is null
            ? FormatFallbackSummary(node)
            : FormatAggregationSummary(node.Kind, node.Aggregation, node.EvidenceState);
    }

    private static string FormatFallbackSummary(UnitTestRunNode node)
    {
        var childLabel = node.Kind switch
        {
            UnitTestNodeKind.RunControl => "suite",
            UnitTestNodeKind.TestSuite => "test case",
            UnitTestNodeKind.TestCase => "child node",
            _ => "child node"
        };
        var childText = node.Children.Count == 1
            ? $"1 observed {childLabel}"
            : $"{node.Children.Count} observed {childLabel}s";
        var findingText = node.Findings.Count == 1
            ? "1 finding"
            : $"{node.Findings.Count} findings";

        return node.Kind == UnitTestNodeKind.TestCase
            ? $"{findingText}; aggregation evidence not available."
            : $"{childText}; {findingText}; aggregation evidence not available.";
    }

    private static string FormatAggregationSummary(
        UnitTestNodeKind kind,
        UnitTestAggregation aggregation,
        EvidenceState evidenceState)
    {
        var label = kind switch
        {
            UnitTestNodeKind.RunControl => "Suites",
            UnitTestNodeKind.TestSuite => "Test cases",
            UnitTestNodeKind.TestCase => "Assertions",
            _ => "Items"
        };
        var parts = new List<string>
        {
            $"{label}: total {aggregation.Total}",
            $"passed {aggregation.Passed}",
            $"failed {aggregation.Failed}",
            $"errors {aggregation.Error}"
        };

        AddOptionalCount(parts, "inactive", aggregation.Inactive);
        AddOptionalCount(parts, "idle", aggregation.Idle);
        AddOptionalCount(parts, "running", aggregation.Running);
        AddOptionalCount(parts, "completed", aggregation.Completed);
        AddOptionalCount(parts, "lost events", aggregation.LostEvents);
        if (aggregation.Runtime is not null)
        {
            parts.Add($"runtime {aggregation.Runtime} ms");
        }

        parts.Add($"evidence {evidenceState}");
        return string.Join(", ", parts) + ".";
    }

    private static void AddOptionalCount(ICollection<string> parts, string label, uint? value)
    {
        if (value is not null)
        {
            parts.Add($"{label} {value}");
        }
    }
}
