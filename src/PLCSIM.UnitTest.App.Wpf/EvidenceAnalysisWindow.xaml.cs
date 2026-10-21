using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.App.Wpf.Services;
using PLCSIM.UnitTest.App.Wpf.ViewModels;

namespace PLCSIM.UnitTest.App.Wpf;

public partial class EvidenceAnalysisWindow : Window
{
    private MainWindowViewModel? viewModel;
    private readonly IClipboardCopyService clipboardCopyService;
    private readonly Action<Window> showClipboardFailure;

    internal EvidenceAnalysisWindow(IClipboardCopyService? clipboardCopyService = null)
    {
        this.clipboardCopyService = clipboardCopyService
            ?? new ClipboardCopyService(NullLogger<ClipboardCopyService>.Instance);
        showClipboardFailure = ShowClipboardFailure;
        InitializeComponent();
    }

    public EvidenceAnalysisWindow(MainWindowViewModel viewModel, IClipboardCopyService clipboardCopyService)
        : this(viewModel, clipboardCopyService, ShowClipboardFailure)
    {
    }

    internal EvidenceAnalysisWindow(MainWindowViewModel viewModel, IClipboardCopyService clipboardCopyService,
        Action<Window> showClipboardFailure)
        : this(clipboardCopyService)
    {
        this.showClipboardFailure = showClipboardFailure;
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        Activated += EvidenceAnalysisWindow_Activated;
    }

    private void CopyAnalysisReport_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel?.CanCopyAnalysisReport == true)
        {
            if (!clipboardCopyService.TryCopyText(viewModel.AnalysisReport))
            {
                showClipboardFailure(this);
            }
        }
    }

    private static void ShowClipboardFailure(Window owner)
    {
        MessageBox.Show(owner,
            "Could not copy to the clipboard. Please try again.",
            "Copy Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void AnalysisReportTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        AnalysisReportTextBox.ScrollToHome();
    }

    private void EvidenceAnalysisWindow_Activated(object? sender, EventArgs e)
    {
        viewModel?.RefreshAnalysisFreshnessStatus();
    }
}
