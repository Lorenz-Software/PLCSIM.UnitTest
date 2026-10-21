namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

internal sealed class TerminalOutputPresenter
{
    private const int MaxTerminalLines = 500;

    private readonly List<TerminalOutputEntry> terminalLines = [];
    private string? workflowTerminalText;
    private string? artifactTerminalText;

    public string Text { get; private set; } = string.Empty;

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public void AppendLine(string message)
    {
        AppendEntries(TerminalOutputSource.Local, [message]);
    }

    public void ApplyWorkflowOutput(string? value)
    {
        ApplySourceOutput(TerminalOutputSource.Workflow, value, ref workflowTerminalText);
    }

    public void ApplyArtifactOutput(string? value)
    {
        ApplySourceOutput(TerminalOutputSource.Artifact, value, ref artifactTerminalText);
    }

    public void Clear()
    {
        terminalLines.Clear();
        workflowTerminalText = null;
        artifactTerminalText = null;
        RefreshText();
    }

    public void ClearArtifactOutput()
    {
        artifactTerminalText = null;
        terminalLines.RemoveAll(static entry => entry.Source == TerminalOutputSource.Artifact);
        RefreshText();
    }

    private void ApplySourceOutput(
        TerminalOutputSource source,
        string? value,
        ref string? currentValue)
    {
        var normalizedValue = string.IsNullOrWhiteSpace(value) ? null : value;
        if (string.Equals(currentValue, normalizedValue, StringComparison.Ordinal))
        {
            return;
        }

        currentValue = normalizedValue;
        terminalLines.RemoveAll(entry => entry.Source == source);
        if (normalizedValue is not null)
        {
            AppendEntries(
                source,
                normalizedValue
                    .Split([Environment.NewLine], StringSplitOptions.None)
                    .Where(static line => !string.IsNullOrWhiteSpace(line)));
            return;
        }

        RefreshText();
    }

    private void AppendEntries(TerminalOutputSource source, IEnumerable<string> messages)
    {
        var timestamp = DateTimeOffset.Now.ToString("HH:mm:ss");
        terminalLines.AddRange(messages.Select(message => new TerminalOutputEntry(
            source,
            $"{timestamp}  {message}")));
        if (terminalLines.Count > MaxTerminalLines)
        {
            terminalLines.RemoveRange(0, terminalLines.Count - MaxTerminalLines);
        }

        RefreshText();
    }

    private void RefreshText()
    {
        Text = string.Join(Environment.NewLine, terminalLines.Select(static entry => entry.Text));
    }

    private sealed record TerminalOutputEntry(TerminalOutputSource Source, string Text);

    private enum TerminalOutputSource
    {
        Local,
        Workflow,
        Artifact
    }
}
