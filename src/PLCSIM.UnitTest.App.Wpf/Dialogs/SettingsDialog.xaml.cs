using System.Windows;
using PLCSIM.UnitTest.App.Wpf.Services;
using PLCSIM.UnitTest.App.Wpf.ViewModels;

namespace PLCSIM.UnitTest.App.Wpf.Dialogs;

public partial class SettingsDialog : Window
{
    private readonly ISettingsPathDialogService pathDialogs;

    public SettingsDialog(WpfApplicationSettings settings, ISettingsPathDialogService pathDialogs)
    {
        InitializeComponent();
        this.pathDialogs = pathDialogs;
        DataContext = new SettingsDialogViewModel(settings);
    }

    public WpfApplicationSettings? Settings { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsDialogViewModel viewModel)
        {
            DialogResult = false;
            return;
        }

        if (!viewModel.TryCreateSettings(out var settings, out var error))
        {
            MessageBox.Show(this, error, "Invalid Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Settings = settings;
        DialogResult = true;
    }

    private void BrowseOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel viewModel
            && pathDialogs.SelectDirectory("Select output directory", viewModel.ResolvedOutputDirectory) is { } directory)
        {
            viewModel.OutputDirectory = directory;
        }
    }

    private void BrowseLogDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel viewModel
            && pathDialogs.SelectDirectory("Select log directory", viewModel.ResolvedLogDirectory) is { } directory)
        {
            viewModel.LogDirectory = directory;
        }
    }
}
