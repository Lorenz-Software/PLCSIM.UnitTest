using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

internal sealed record RunResultTreePresentationState(
    IReadOnlyList<RunResultNodeViewModel> Tree,
    IReadOnlyList<RunResultNodeViewModel> Rows,
    RunResultNodeViewModel? SelectedNode,
    string Status,
    string? DisplayedEpochId,
    UnitTestRunState? DisplayedState,
    ushort LostEventCount);

internal sealed class RunResultTreePresenter
{
    private readonly HashSet<string> expandedNodeIds = [];
    private string? displayedEpochId;
    private UnitTestRunState? displayedState;
    private ushort displayedLostEventCount;
    private string? suppressedEpochId;

    public string? DisplayedEpochId => displayedEpochId;

    public UnitTestRunState? DisplayedState => displayedState;

    public RunResultTreePresentationState Refresh(
        UnitTestRunEpochSnapshot? currentRun,
        string? previousSelectedNodeId)
    {
        if (currentRun is null)
        {
            return Clear(suppressCurrentEpoch: false, currentWorkflowEpochId: null);
        }

        var epochId = currentRun.EpochId.Value;
        if (string.Equals(suppressedEpochId, epochId, StringComparison.Ordinal))
        {
            return Clear(suppressCurrentEpoch: false, currentWorkflowEpochId: null);
        }

        if (!string.Equals(displayedEpochId, epochId, StringComparison.Ordinal))
        {
            expandedNodeIds.Clear();
        }

        var isTerminalRun = currentRun.State is UnitTestRunState.Completed
            or UnitTestRunState.Aborted
            or UnitTestRunState.TimedOut
            or UnitTestRunState.Invalidated;
        var hasEpochEvidenceError = currentRun.LostEventCount > 0
            || currentRun.EvidenceState is EvidenceState.Incomplete
                or EvidenceState.Ambiguous
                or EvidenceState.Conflicting;
        var root = RunResultNodeViewModel.FromNode(
            currentRun.Root,
            isTerminalRun,
            forcePresentationError: hasEpochEvidenceError);
        ApplyExpansionState(root);
        var rows = Flatten(root).ToList();
        var selectedNode = !string.IsNullOrWhiteSpace(previousSelectedNodeId)
            ? rows.FirstOrDefault(node => string.Equals(node.Id, previousSelectedNodeId, StringComparison.Ordinal)) ?? root
            : root;

        displayedEpochId = epochId;
        displayedState = currentRun.State;
        displayedLostEventCount = currentRun.LostEventCount;

        return new RunResultTreePresentationState(
            [root],
            rows,
            selectedNode,
            $"{currentRun.State} / {root.PresentationResult} / {currentRun.EvidenceState}",
            displayedEpochId,
            displayedState,
            displayedLostEventCount);
    }

    public RunResultTreePresentationState Clear(
        bool suppressCurrentEpoch,
        string? currentWorkflowEpochId)
    {
        var epochIdToSuppress = displayedEpochId;
        if (string.IsNullOrWhiteSpace(epochIdToSuppress))
        {
            epochIdToSuppress = currentWorkflowEpochId;
        }

        if (suppressCurrentEpoch && !string.IsNullOrWhiteSpace(epochIdToSuppress))
        {
            suppressedEpochId = epochIdToSuppress;
        }

        displayedEpochId = null;
        displayedState = null;
        displayedLostEventCount = 0;
        expandedNodeIds.Clear();

        return new RunResultTreePresentationState(
            [],
            [],
            null,
            "No run captured yet",
            displayedEpochId,
            displayedState,
            displayedLostEventCount);
    }

    public RunResultTreePresentationState Suppress(
        string? epochId,
        IReadOnlyList<RunResultNodeViewModel> currentTree,
        string? selectedNodeId)
    {
        if (string.IsNullOrWhiteSpace(epochId))
        {
            return CreateCurrentState(selectedNodeId, currentTree);
        }

        suppressedEpochId = epochId;
        return string.Equals(displayedEpochId, epochId, StringComparison.Ordinal)
            ? Clear(suppressCurrentEpoch: false, currentWorkflowEpochId: null)
            : CreateCurrentState(selectedNodeId, currentTree);
    }

    public RunResultTreePresentationState ToggleExpansion(
        RunResultNodeViewModel? node,
        IReadOnlyList<RunResultNodeViewModel> currentTree,
        string? previousSelectedNodeId)
    {
        if (node?.IsExpandable != true)
        {
            return CreateCurrentState(previousSelectedNodeId, currentTree);
        }

        node.IsExpanded = !node.IsExpanded;
        if (node.IsExpanded)
        {
            expandedNodeIds.Add(node.Id);
        }
        else
        {
            expandedNodeIds.Remove(node.Id);
        }

        var root = currentTree.FirstOrDefault();
        var rows = root is null ? [] : Flatten(root).ToList();
        var selectedNode = previousSelectedNodeId is not null
            ? rows.FirstOrDefault(row => row.Id == previousSelectedNodeId) ?? node
            : node;

        return new RunResultTreePresentationState(
            currentTree,
            rows,
            selectedNode,
            "No run captured yet",
            displayedEpochId,
            displayedState,
            displayedLostEventCount);
    }

    private RunResultTreePresentationState CreateCurrentState(
        string? selectedNodeId,
        IReadOnlyList<RunResultNodeViewModel>? currentTree = null)
    {
        var tree = currentTree ?? [];
        var root = tree.FirstOrDefault();
        var rows = root is null ? [] : Flatten(root).ToList();
        var selectedNode = !string.IsNullOrWhiteSpace(selectedNodeId)
            ? rows.FirstOrDefault(node => string.Equals(node.Id, selectedNodeId, StringComparison.Ordinal))
            : null;

        return new RunResultTreePresentationState(
            tree,
            rows,
            selectedNode,
            "No run captured yet",
            displayedEpochId,
            displayedState,
            displayedLostEventCount);
    }

    private void ApplyExpansionState(RunResultNodeViewModel node)
    {
        if (node.IsExpandable)
        {
            node.IsExpanded = expandedNodeIds.Contains(node.Id);
        }

        foreach (var child in node.Children)
        {
            ApplyExpansionState(child);
        }
    }

    private static IEnumerable<RunResultNodeViewModel> Flatten(RunResultNodeViewModel root)
    {
        yield return root;
        if (root.Kind != UnitTestNodeKind.RunControl.ToString() && !root.IsExpanded)
        {
            yield break;
        }

        foreach (var child in root.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }
}
