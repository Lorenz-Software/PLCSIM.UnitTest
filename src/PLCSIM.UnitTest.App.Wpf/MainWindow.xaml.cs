using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.App.Wpf.Dialogs;
using PLCSIM.UnitTest.App.Wpf.Services;
using PLCSIM.UnitTest.App.Wpf.ViewModels;

namespace PLCSIM.UnitTest.App.Wpf;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly ILogger<MainWindow> logger;
    private readonly IHelpContentOpenService helpContentOpenService;
    private readonly IClipboardCopyService clipboardCopyService;
    private readonly Action<Window> showClipboardFailure;
    private EvidenceAnalysisWindow? evidenceAnalysisWindow;
    private bool applicationExitApproved;
    private bool applicationExitInProgress;

    public MainWindow(
        MainWindowViewModel viewModel,
        ILogger<MainWindow> logger,
        IHelpContentOpenService helpContentOpenService,
        IClipboardCopyService clipboardCopyService)
        : this(viewModel, logger, helpContentOpenService, clipboardCopyService, ShowClipboardFailure)
    {
    }

    internal MainWindow(
        MainWindowViewModel viewModel,
        ILogger<MainWindow> logger,
        IHelpContentOpenService helpContentOpenService,
        IClipboardCopyService clipboardCopyService,
        Action<Window> showClipboardFailure)
    {
        this.viewModel = viewModel;
        this.logger = logger;
        this.helpContentOpenService = helpContentOpenService;
        this.clipboardCopyService = clipboardCopyService;
        this.showClipboardFailure = showClipboardFailure;

        InitializeComponent();
        DataContext = viewModel;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        if (viewModel is not null)
        {
            viewModel.AnalysisPresented += ViewModel_AnalysisPresented;
        }

        this.logger.LogInformation("Main window initialized.");
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        try
        {
            await viewModel.InitializeAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WPF startup initialization failed.");
        }
    }

    private void ViewModel_AnalysisPresented(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowEvidenceAnalysisWindow);
            return;
        }

        ShowEvidenceAnalysisWindow();
    }

    private void ShowEvidenceAnalysisWindow()
    {
        if (evidenceAnalysisWindow is null)
        {
            evidenceAnalysisWindow = new EvidenceAnalysisWindow(viewModel, clipboardCopyService)
            {
                Owner = this
            };
            evidenceAnalysisWindow.Closed += (_, _) => evidenceAnalysisWindow = null;
            evidenceAnalysisWindow.Show();
        }

        if (evidenceAnalysisWindow.WindowState == WindowState.Minimized)
        {
            evidenceAnalysisWindow.WindowState = WindowState.Normal;
        }

        evidenceAnalysisWindow.Activate();
        evidenceAnalysisWindow.Focus();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (viewModel is not null)
        {
            viewModel.AnalysisPresented -= ViewModel_AnalysisPresented;
        }
        evidenceAnalysisWindow?.Close();
        evidenceAnalysisWindow = null;
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        logger.LogInformation("Exit menu item selected.");
        Close();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (applicationExitApproved || !viewModel.RequiresApplicationExitConfirmation)
        {
            return;
        }

        e.Cancel = true;
        if (applicationExitInProgress || !viewModel.ConfirmApplicationExit())
        {
            return;
        }

        applicationExitInProgress = true;
        IsEnabled = false;
        logger.LogInformation("Confirmed application exit is releasing the active session.");

        try
        {
            await viewModel.CleanupForApplicationExitAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected application exit cleanup failure. Shutdown will continue.");
        }

        applicationExitApproved = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void HelpContentsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            helpContentOpenService.Open();
            logger.LogInformation(
                "Offline help opened: {HelpEntryPagePath}",
                helpContentOpenService.EntryPagePath);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Offline help could not be opened from {HelpEntryPagePath}.",
                helpContentOpenService.EntryPagePath);

            MessageBox.Show(
                this,
                "The packaged offline help could not be opened. See the application log for details.",
                "Help Unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        logger.LogDebug("About dialog opened.");
        var dialog = new AboutDialog
        {
            Owner = this
        };

        dialog.ShowDialog();
    }

    private void OutputTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        OutputTextBox.ScrollToEnd();
    }

    private void CopySelectedDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedDiagnostic is not null)
        {
            CopyToClipboard(DiagnosticClipboardFormatter.Format(viewModel.SelectedDiagnostic));
        }
    }

    private void CopyAllDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.Diagnostics.Count > 0)
        {
            CopyToClipboard(DiagnosticClipboardFormatter.FormatAll(viewModel.Diagnostics));
        }
    }

    private void CopyToClipboard(string text)
    {
        if (!clipboardCopyService.TryCopyText(text))
        {
            showClipboardFailure(this);
        }
    }

    private static void ShowClipboardFailure(Window owner)
    {
        MessageBox.Show(owner,
            "Could not copy to the clipboard. Please try again.",
            "Copy Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void RunResultExpander_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel
            && sender is System.Windows.Controls.Button button
            && button.Tag is RunResultNodeViewModel node)
        {
            viewModel.ToggleRunResultNodeExpansion(node);
            e.Handled = true;
        }
    }

    private void RunResultRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
        {
            return;
        }

        if (HasAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (DataContext is MainWindowViewModel viewModel
            && sender is FrameworkElement element
            && element.DataContext is RunResultNodeViewModel node)
        {
            viewModel.ToggleRunResultNodeExpansion(node);
            e.Handled = true;
        }
    }

    private static bool HasAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T)
            {
                return true;
            }

            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }

        return false;
    }
}
