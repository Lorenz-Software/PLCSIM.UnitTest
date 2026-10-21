using Microsoft.Win32;

namespace PLCSIM.UnitTest.App.Wpf.Services;

public interface IAnalysisReportFileDialogService
{
    string? SelectPackageExportPath(string suggestedFileName);
}

public sealed class AnalysisReportFileDialogService : IAnalysisReportFileDialogService
{
    public string? SelectPackageExportPath(string suggestedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        var dialog = new SaveFileDialog
        {
            Title = "Export Evidence Analysis Package",
            FileName = suggestedFileName,
            DefaultExt = ".zip",
            Filter = "ZIP packages (*.zip)|*.zip|All files (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true
        };

        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }
}
