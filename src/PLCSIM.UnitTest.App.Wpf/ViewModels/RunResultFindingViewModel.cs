using System.Windows.Media;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

public sealed class RunResultFindingViewModel
{
    public RunResultFindingViewModel(UnitTestFinding finding)
    {
        Kind = finding.Kind.ToString();
        Result = finding.Result.ToString();
        Name = string.IsNullOrWhiteSpace(finding.Name)
            ? "(unnamed finding)"
            : finding.Name;
        Message = finding.Message;
        EvidenceReference = finding.SourceEvent is null
            ? "-"
            : $"event #{finding.SourceEvent.SequenceId}";
        StatusBrush = RunResultStatusBrushes.ForResult(finding.Result);
    }

    public string Kind { get; }

    public string Result { get; }

    public string Name { get; }

    public string Message { get; }

    public string EvidenceReference { get; }

    public Brush StatusBrush { get; }
}
